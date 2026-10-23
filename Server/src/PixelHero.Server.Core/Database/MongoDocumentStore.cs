using PixelHero.Js;
using PixelHero.Server.Js;
using MongoDB.Bson;
using MongoDB.Driver;

namespace PixelHero.Server.Database
{
    /// <summary>
    /// <see cref="IDocumentStore"/> on a MongoDB server (common/database/mongodb/mongodb.ts and the driver calls of
    /// loader.ts and creator.ts). Driver work runs on the thread pool; every callback is handed to
    /// <c>post</c>, which runs it on the game thread. A failed operation is rethrown on the game thread, where the
    /// original would have an unhandled rejection.
    ///
    /// Values are stored as the Node driver stores JS values: an integer number within int32 becomes an Int32, any
    /// other number a Double, and every number reads back as a double. Fields holding undefined (null here) are
    /// stored as null, as the driver does without ignoreUndefined (player_info's resetToken). Top-level field order
    /// after an update follows the server (MongoDB 5+ adds new <c>$set</c> fields in lexicographic order); game code
    /// reads documents by field name.
    /// </summary>
    public sealed class MongoDocumentStore : IDocumentStore
    {
        readonly string _url;
        readonly string _databaseName;
        readonly bool _tls;
        readonly Action<Action> _post;

        /// <summary>The connected database (after <see cref="Connect"/> succeeds); tests use it to clean up.</summary>
        public IMongoDatabase Database { get; private set; }

        /// <param name="post">Runs a callback on the game thread (WorldRunner.Post).</param>
        public MongoDocumentStore(string host, double port, string username, string password, string databaseName,
            bool tls, bool srv, string authSource, Action<Action> post)
        {
            _url = ConnectionUrl(host, port, username, password, databaseName, srv, authSource);
            _databaseName = databaseName;
            _tls = tls;
            _post = post;
        }

        /// <summary>
        /// mongodb.ts's connection URL. The credentials are percent-encoded here (the original inserts them as they
        /// are, which breaks on characters such as '@' or ':').
        /// </summary>
        public static string ConnectionUrl(string host, double port, string username, string password, string databaseName,
            bool srv, string authSource)
        {
            var scheme = srv ? "mongodb+srv" : "mongodb";
            var auth = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
                ? $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@"
                : "";
            var portText = port > 0 ? $":{JsString.Of(port)}" : "";
            var authSourceText = !string.IsNullOrEmpty(authSource) ? $"?authSource={authSource}" : "";

            return $"{scheme}://{auth}{host}{portText}/{databaseName}{authSourceText}";
        }

        public void Connect(Action<Exception> callback) =>
            Task.Run(async () =>
            {
                try
                {
                    var settings = MongoClientSettings.FromUrl(new MongoUrl(_url));
                    settings.ConnectTimeout = TimeSpan.FromMilliseconds(5000);
                    settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(5000);
                    settings.UseTls = _tls;

                    var database = new MongoClient(settings).GetDatabase(_databaseName);

                    // client.connect() in Node selects a server before it resolves; a ping does the same here.
                    await database.RunCommandAsync((Command<BsonDocument>)"{ ping: 1 }");

                    Database = database;
                    _post(() => callback(null));
                }
                catch (Exception error)
                {
                    _post(() => callback(error));
                }
            });

        IMongoCollection<BsonDocument> Collection(string name) => Database.GetCollection<BsonDocument>(name);

        /// <summary>Runs driver work and posts its result (or its failure) to the game thread.</summary>
        void Run<T>(Func<Task<T>> work, Action<T> callback) =>
            Task.Run(async () =>
            {
                try
                {
                    var result = await work();
                    _post(() => callback?.Invoke(result));
                }
                catch (Exception error)
                {
                    _post(() => throw new InvalidOperationException("MongoDB operation failed", error));
                }
            });

        public void Find(string collection, JsObject filter, Action<List<JsObject>> callback) =>
            Run(async () =>
            {
                var documents = await Collection(collection).Find(ToBson(filter)).ToListAsync();
                return documents.ConvertAll(document => FromBson(document));
            }, callback);

        public void FindOne(string collection, JsObject filter, Action<JsObject> callback) =>
            Run(async () =>
            {
                var document = await Collection(collection).Find(ToBson(filter)).FirstOrDefaultAsync();
                return document == null ? null : FromBson(document);
            }, callback);

        public void UpdateOne(string collection, JsObject filter, JsObject set, bool upsert, Action<bool> callback = null) =>
            UpdateOne(collection, filter, set, null, upsert, callback);

        public void UpdateOne(string collection, JsObject filter, JsObject set, List<string> unset, bool upsert,
            Action<bool> callback = null) =>
            Run(async () =>
            {
                var update = new BsonDocument("$set", ToBson(set));
                if (unset is { Count: > 0 })
                    update.Add("$unset", new BsonDocument(unset.Select(key => new BsonElement(key, ""))));

                var result = await Collection(collection).UpdateOneAsync(ToBson(filter), update,
                    new UpdateOptions { IsUpsert = upsert });
                return result.IsAcknowledged;
            }, callback);

        public void InsertOne(string collection, JsObject document, Action<bool> callback = null) =>
            Run(async () =>
            {
                await Collection(collection).InsertOneAsync(ToBson(document));
                return true;
            }, callback);

        public void DeleteOne(string collection, JsObject filter, Action<bool> callback = null) =>
            Run(async () =>
            {
                var result = await Collection(collection).DeleteOneAsync(ToBson(filter));
                return result.IsAcknowledged;
            }, callback);

        public void CountDocuments(string collection, Action<double> callback) =>
            Run(async () => (double)await Collection(collection).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty),
                callback);

        public void Aggregate(string collection, List<object> pipeline, Action<List<JsObject>> callback) =>
            Run(async () =>
            {
                var stages = pipeline.Cast<JsObject>().Select(ToBson).ToArray();
                var documents = await Collection(collection)
                    .Aggregate(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages)).ToListAsync();
                return documents.ConvertAll<JsObject>(document => FromBson(document, true));
            }, callback);

        // ---- JS values <-> BSON ----

        /// <summary>A document as the Node driver serializes the JS object (undefined becomes null).</summary>
        public static BsonDocument ToBson(JsObject document)
        {
            var bson = new BsonDocument();
            foreach (var pair in document) bson.Add(pair.Key, ToBsonValue(pair.Value));
            return bson;
        }

        static BsonValue ToBsonValue(object value) => value switch
        {
            null => BsonNull.Value,
            double number => ToBsonNumber(number),
            string text => new BsonString(text),
            bool flag => BsonBoolean.Create(flag),
            ObjectIdValue id => ObjectId.TryParse(id.Id, out var objectId) ? objectId : new BsonString(id.Id),
            JsObject document => ToBson(document),
            List<object> list => new BsonArray(list.Select(ToBsonValue)),
            _ => throw new ArgumentException($"Cannot store a {value.GetType().Name} in MongoDB.")
        };

        /// <summary>js-bson's serializeNumber: integers within int32 are Int32, everything else a Double.</summary>
        static BsonValue ToBsonNumber(double number) =>
            Math.Floor(number) == number && number >= int.MinValue && number <= int.MaxValue
                ? new BsonInt32((int)number)
                : new BsonDouble(number);

        /// <summary>A stored document as the Node driver returns it (numbers promoted to JS numbers).</summary>
        /// <param name="keepNull">Null as <see cref="JsonNull"/> (aggregation results) instead of undefined.</param>
        public static JsObject FromBson(BsonDocument document, bool keepNull = false)
        {
            var result = new JsObject();
            foreach (var element in document) result.Set(element.Name, FromBsonValue(element.Value, keepNull));
            return result;
        }

        static object FromBsonValue(BsonValue value, bool keepNull = false) => value.BsonType switch
        {
            BsonType.Null => keepNull ? JsonNull.Value : null,
            BsonType.Undefined => null,
            BsonType.Int32 => (double)value.AsInt32,
            BsonType.Int64 => (double)value.AsInt64,
            BsonType.Double => value.AsDouble,
            BsonType.Decimal128 => (double)value.AsDecimal128,
            BsonType.String => value.AsString,
            BsonType.Boolean => value.AsBoolean,
            BsonType.ObjectId => new ObjectIdValue(value.AsObjectId.ToString()),
            BsonType.DateTime => (double)value.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalMilliseconds,
            BsonType.Document => FromBson(value.AsBsonDocument, keepNull),
            BsonType.Array => value.AsBsonArray.Select(item => FromBsonValue(item, keepNull)).ToList(),
            _ => throw new NotSupportedException($"Unsupported BSON type {value.BsonType}.")
        };
    }
}
