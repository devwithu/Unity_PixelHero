using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Database
{
    /// <summary>
    /// The storage under <see cref="GameDatabase"/>: collections of JSON documents (game data, never driver types) with
    /// the operations the original's MongoDB code uses. Filters are equality on top-level fields. Every operation
    /// completes through <paramref>callback</paramref> on the world's event loop.
    /// Implementations: <see cref="MemoryDocumentStore"/> (tests; harness timing), MongoDB (server), later PostgreSQL.
    /// </summary>
    public interface IDocumentStore
    {
        /// <summary>connect(): calls back with null on success or the error.</summary>
        void Connect(Action<Exception> callback);

        /// <summary>collection.find(filter).toArray().</summary>
        void Find(string collection, JsObject filter, Action<List<JsObject>> callback);

        /// <summary>collection.findOne(filter): the document or null.</summary>
        void FindOne(string collection, JsObject filter, Action<JsObject> callback);

        /// <summary>collection.updateOne(filter, {$set: set}, {upsert}).</summary>
        void UpdateOne(string collection, JsObject filter, JsObject set, bool upsert, Action<bool> callback = null);

        /// <summary>collection.updateOne(filter, {$set, $unset}).</summary>
        void UpdateOne(string collection, JsObject filter, JsObject set, List<string> unset, bool upsert, Action<bool> callback = null);

        /// <summary>collection.insertOne(document).</summary>
        void InsertOne(string collection, JsObject document, Action<bool> callback = null);

        /// <summary>collection.deleteOne(filter).</summary>
        void DeleteOne(string collection, JsObject filter, Action<bool> callback = null);

        /// <summary>collection.countDocuments().</summary>
        void CountDocuments(string collection, Action<double> callback);

        /// <summary>
        /// collection.aggregate(pipeline).toArray(). The pipeline is JSON data in MongoDB's syntax; implementations
        /// support the stages the leaderboards use ($unwind, $match, $group with $sum and $first, $sort, $limit).
        /// A null in the results (such as $first of a missing field) is <see cref="JsonNull"/>, as JSON keeps it.
        /// </summary>
        void Aggregate(string collection, List<object> pipeline, Action<List<JsObject>> callback);
    }

    /// <summary>
    /// In-memory documents with the harness's timing (Tools/fixtures/server/stubs/mongodb.ts): each operation settles
    /// as one setImmediate, in the order issued, and its callback runs as the promise reaction (a microtask after it).
    /// Documents are deep-copied in and out. Inserted documents get sequential ObjectId-like ids.
    /// </summary>
    public sealed class MemoryDocumentStore : IDocumentStore
    {
        /// <summary>The documents by collection (the harness's `database`), shared with whoever created the store.</summary>
        public readonly JsObject Collections;

        /// <summary>When false, connect() fails (a scenario without a database).</summary>
        readonly bool _available;

        int _nextObjectId = 1;

        public MemoryDocumentStore(JsObject collections, bool available = true)
        {
            Collections = collections ?? new JsObject();
            _available = available;
        }

        List<object> Collection(string name)
        {
            if (Collections[name] is not List<object> documents) Collections.Set(name, documents = new List<object>());
            return documents;
        }

        static bool Matches(JsObject document, JsObject filter)
        {
            foreach (var pair in filter)
                if (!Same(document[pair.Key], pair.Value))
                    return false;
            return true;
        }

        static bool Same(object a, object b) => a switch
        {
            null => b == null,
            double d => b is double e && d == e,
            string s => b is string t && s == t,
            bool x => b is bool y && x == y,
            ObjectIdValue id => b is ObjectIdValue other && id.Id == other.Id,
            _ => ReferenceEquals(a, b)
        };

        /// <summary>Runs <paramref name="work"/> as one immediate; the callback follows as a microtask.</summary>
        static void Settle<T>(Func<T> work, Action<T> callback) =>
            Node.SetImmediate(() =>
            {
                var result = work();
                if (callback != null) Node.QueueMicrotask(() => callback(result));
            });

        public void Connect(Action<Exception> callback) =>
            Settle<Exception>(() => _available ? null : new Exception("[harness mongodb] no database in this scenario"), callback);

        public void Find(string collection, JsObject filter, Action<List<JsObject>> callback)
        {
            Touch(collection);
            Settle(() => Collection(collection).Cast<JsObject>().Where(document => Matches(document, filter))
                .Select(document => (JsObject)JsData.Clone(document)).ToList(), callback);
        }

        public void FindOne(string collection, JsObject filter, Action<JsObject> callback)
        {
            Touch(collection);
            Settle(() =>
            {
                var document = Collection(collection).Cast<JsObject>().FirstOrDefault(d => Matches(d, filter));
                return document == null ? null : (JsObject)JsData.Clone(document);
            }, callback);
        }

        public void UpdateOne(string collection, JsObject filter, JsObject set, bool upsert, Action<bool> callback = null) =>
            UpdateOne(collection, filter, set, null, upsert, callback);

        public void UpdateOne(string collection, JsObject filter, JsObject set, List<string> unset, bool upsert,
            Action<bool> callback = null)
        {
            Touch(collection);
            Settle(() =>
            {
                var documents = Collection(collection);
                var document = documents.Cast<JsObject>().FirstOrDefault(d => Matches(d, filter));
                if (document != null)
                {
                    foreach (var pair in (JsObject)JsData.Clone(set)) document.Set(pair.Key, pair.Value);
                    if (unset != null) foreach (var key in unset) document.Delete(key);
                    return true;
                }
                if (!upsert) return true;
                var stored = new JsObject("_id", NextId());
                foreach (var pair in (JsObject)JsData.Clone(filter)) stored.Set(pair.Key, pair.Value);
                foreach (var pair in (JsObject)JsData.Clone(set)) stored.Set(pair.Key, pair.Value);
                documents.Add(stored);
                return true;
            }, callback);
        }

        public void InsertOne(string collection, JsObject document, Action<bool> callback = null)
        {
            Touch(collection);
            Settle(() =>
            {
                var stored = new JsObject("_id", NextId());
                foreach (var pair in (JsObject)JsData.Clone(document)) stored.Set(pair.Key, pair.Value);
                Collection(collection).Add(stored);
                return true;
            }, callback);
        }

        public void DeleteOne(string collection, JsObject filter, Action<bool> callback = null)
        {
            Touch(collection);
            Settle(() =>
            {
                var documents = Collection(collection);
                var index = documents.FindIndex(d => Matches((JsObject)d, filter));
                if (index != -1) documents.RemoveAt(index);
                return true;
            }, callback);
        }

        public void CountDocuments(string collection, Action<double> callback)
        {
            Touch(collection);
            Settle(() => (double)Collection(collection).Count, callback);
        }

        public void Aggregate(string collection, List<object> pipeline, Action<List<JsObject>> callback)
        {
            Touch(collection);
            Settle(() =>
            {
                var documents = Collection(collection).Cast<JsObject>().Select(d => (JsObject)JsData.Clone(d)).ToList();
                foreach (JsObject stage in pipeline) documents = RunStage(documents, stage);
                return documents;
            }, callback);
        }

        // ---- The aggregation stages the leaderboards use, with MongoDB's semantics ----

        static List<JsObject> RunStage(List<JsObject> documents, JsObject stage)
        {
            var name = stage.Keys.Single();
            var spec = stage[name];

            switch (name)
            {
                case "$unwind":
                {
                    // Documents whose field is missing, null or an empty array are dropped; a non-array is kept as is.
                    var path = ((string)spec).Substring(1);
                    var result = new List<JsObject>();
                    foreach (var document in documents)
                        switch (Resolve(document, path))
                        {
                            case List<object> list:
                                foreach (var element in list)
                                {
                                    var copy = (JsObject)JsData.Clone(document);
                                    SetPath(copy, path, JsData.Clone(element));
                                    result.Add(copy);
                                }
                                break;
                            case null:
                                break;
                            default:
                                result.Add(document);
                                break;
                        }
                    return result;
                }

                case "$match":
                    return documents.Where(document => ((JsObject)spec).All(pair =>
                    {
                        var value = Resolve(document, pair.Key);
                        // An array field matches when any element equals the value.
                        return value is List<object> list ? list.Any(e => Same(e, pair.Value)) : Same(value, pair.Value);
                    })).ToList();

                case "$group":
                {
                    var groups = new List<(object id, JsObject output, Dictionary<string, bool> seen)>();
                    var fields = (JsObject)spec;

                    foreach (var document in documents)
                    {
                        var id = Evaluate(document, fields["_id"]);
                        var index = groups.FindIndex(g => Same(g.id, id));
                        if (index == -1)
                        {
                            var output = new JsObject("_id", id);
                            foreach (var field in fields.Keys.Where(k => k != "_id"))
                            {
                                var accumulator = ((JsObject)fields[field]).Keys.Single();
                                output.Set(field, accumulator == "$sum" ? 0.0 : null);
                            }
                            groups.Add((id, output, new Dictionary<string, bool>()));
                            index = groups.Count - 1;
                        }

                        var group = groups[index];
                        foreach (var field in fields.Keys.Where(k => k != "_id"))
                        {
                            var accumulator = (JsObject)fields[field];
                            var op = accumulator.Keys.Single();
                            var value = Evaluate(document, accumulator[op]);

                            if (op == "$sum")
                            {
                                // Non-numeric values (missing fields included) are ignored.
                                if (value is double number) group.output.Set(field, (double)group.output[field] + number);
                            }
                            else if (op == "$first")
                            {
                                if (group.seen.ContainsKey(field)) continue;
                                group.seen[field] = true;
                                // A missing (or null) field gives null, which stays in the output.
                                group.output.Set(field, value ?? JsonNull.Value);
                            }
                            else throw new NotSupportedException($"Accumulator {op} is not supported.");
                        }
                    }

                    return groups.ConvertAll(g => g.output);
                }

                case "$sort":
                {
                    var keys = ((JsObject)spec).ToList();
                    // A stable sort; MongoDB leaves the order of ties unspecified.
                    return documents.OrderBy(d => d, Comparer<JsObject>.Create((a, b) =>
                    {
                        foreach (var key in keys)
                        {
                            var order = CompareValues(Resolve(a, key.Key), Resolve(b, key.Key));
                            if (order != 0) return (double)key.Value < 0 ? -order : order;
                        }
                        return 0;
                    })).ToList();
                }

                case "$limit":
                    return documents.Take((int)(double)spec).ToList();

                default:
                    throw new NotSupportedException($"Aggregation stage {name} is not supported.");
            }
        }

        /// <summary>An expression: "$a.b" reads a field path, anything else is a constant.</summary>
        static object Evaluate(JsObject document, object expression) =>
            expression is string path && path.StartsWith("$") ? Resolve(document, path.Substring(1)) : expression;

        static object Resolve(JsObject document, string path)
        {
            object value = document;
            foreach (var part in path.Split('.'))
            {
                if (value is not JsObject obj) return null;
                value = obj[part];
            }
            return value;
        }

        static void SetPath(JsObject document, string path, object value)
        {
            var parts = path.Split('.');
            var target = document;
            for (var i = 0; i < parts.Length - 1; i++) target = (JsObject)target[parts[i]];
            target.Set(parts[^1], value);
        }

        /// <summary>MongoDB's sort order for the types the leaderboards meet: null, numbers, strings, then the rest.</summary>
        static int CompareValues(object a, object b)
        {
            static int Rank(object value) => value switch
            {
                null => 0,
                double => 1,
                string => 2,
                _ => 3
            };

            var rank = Rank(a).CompareTo(Rank(b));
            if (rank != 0) return rank;

            return a switch
            {
                double x => x.CompareTo((double)b),
                string s => string.CompareOrdinal(s, (string)b),
                _ => 0
            };
        }

        /// <summary>db.collection(name): creates the collection, which fixes its place in the database.</summary>
        public void Touch(string name) => Collection(name);

        ObjectIdValue NextId() => new((_nextObjectId++).ToString("x").PadLeft(24, '0'));
    }

    /// <summary>An ObjectId as game data (its hex string). Serializes like the harness stub: {"id": hex}.</summary>
    public sealed class ObjectIdValue : JsObject
    {
        public readonly string Id;

        public ObjectIdValue(string id) : base("id", id) => Id = id;

        public override string ToString() => Id;
    }
}
