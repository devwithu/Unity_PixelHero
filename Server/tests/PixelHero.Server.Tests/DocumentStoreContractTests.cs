using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using MongoDB.Driver;
using NUnit.Framework;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// What game code relies on from an <see cref="IDocumentStore"/>. Every implementation (memory, MongoDB, later
    /// PostgreSQL) runs the same cases. Documents are compared as JSON without their _id, and top-level fields are
    /// compared by name: their order after an update is the server's business (MongoDB 5+ adds new $set fields in
    /// lexicographic order), while nested values keep their order.
    /// </summary>
    public abstract class DocumentStoreContract
    {
        protected const string Docs = "ct_docs";

        protected IDocumentStore Store;

        /// <summary>A connected store whose <see cref="Docs"/> collection is empty.</summary>
        protected abstract IDocumentStore CreateStore();

        /// <summary>Runs the store's pending work (the memory store settles on the event loop).</summary>
        protected virtual void Pump() { }

        [SetUp]
        public void CreateEmptyStore() => Store = CreateStore();

        protected T Wait<T>(Action<Action<T>> start)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            start(result => done.TrySetResult(result));
            Pump();
            Assert.That(done.Task.Wait(TimeSpan.FromSeconds(20)), Is.True, "the store did not call back");
            return done.Task.Result;
        }

        List<JsObject> Find(JsObject filter) => Wait<List<JsObject>>(callback => Store.Find(Docs, filter, callback));

        JsObject FindOne(JsObject filter) => Wait<JsObject>(callback => Store.FindOne(Docs, filter, callback));

        void Insert(JsObject document) => Wait<bool>(callback => Store.InsertOne(Docs, document, callback));

        void Update(JsObject filter, JsObject set, bool upsert, List<string> unset = null) =>
            Wait<bool>(callback => Store.UpdateOne(Docs, filter, set, unset, upsert, callback));

        void Delete(JsObject filter) => Wait<bool>(callback => Store.DeleteOne(Docs, filter, callback));

        double Count() => Wait<double>(callback => Store.CountDocuments(Docs, callback));

        /// <summary>The document as JSON with its top-level fields sorted by name and without _id.</summary>
        static string Text(JsObject document)
        {
            var sorted = new JsObject();
            foreach (var key in document.Keys.Where(key => key != "_id").OrderBy(key => key, StringComparer.Ordinal))
                sorted.Set(key, document[key]);
            return JsJson.Stringify(sorted);
        }

        static JsObject Parse(string json) => (JsObject)JsData.Parse(json);

        [Test]
        public void InsertedDocumentsComeBackWithAnId()
        {
            var document = Parse("{\"username\":\"ct_a\",\"x\":1,\"y\":2.5,\"name\":\"\uD55C\uAE00 \\u2028 \\\"q\\\"\",\"flags\":[true,false]," +
                "\"nested\":{\"b\":[1,{\"c\":\"d\"}],\"a\":{},\"2\":3,\"1\":[]}}");
            Insert(document);

            var found = Find(new JsObject("username", "ct_a"));

            Assert.That(found, Has.Count.EqualTo(1));
            Assert.That(found[0]["_id"], Is.InstanceOf<ObjectIdValue>());
            Assert.That(((ObjectIdValue)found[0]["_id"]).Id, Does.Match("^[0-9a-f]{24}$"));
            Assert.That(Text(found[0]), Is.EqualTo(Text(document)));
        }

        [Test]
        public void NumbersComeBackAsTheSameJsNumbers()
        {
            double[] numbers =
            {
                0, -1, 7, 2147483647, -2147483648, 2147483648, -2147483649, 9007199254740993, 1e21, 0.1, 1.0 / 3,
                5e-324, 1.7976931348623157e308, -0.0, 1700000000000
            };
            var document = new JsObject("username", "ct_numbers");
            for (var i = 0; i < numbers.Length; i++) document.Set("n" + i, numbers[i]);
            document.Set("list", numbers.Select(n => (object)n).ToList());
            Insert(document);

            var found = FindOne(new JsObject("username", "ct_numbers"));

            Assert.That(Text(found), Is.EqualTo(Text(document)));
        }

        [Test]
        public void FiltersMatchByValueAndType()
        {
            Insert(Parse("{\"key\":\"ct_1\",\"x\":1}"));
            Insert(Parse("{\"key\":\"ct_2\",\"x\":\"1\"}"));
            Insert(Parse("{\"key\":\"ct_3\",\"x\":1.5}"));
            Insert(Parse("{\"key\":\"ct_4\",\"x\":true}"));

            Assert.That(Find(new JsObject("x", 1.0)).Select(d => d["key"]), Is.EqualTo(new[] { "ct_1" }));
            Assert.That(Find(new JsObject("x", "1")).Select(d => d["key"]), Is.EqualTo(new[] { "ct_2" }));
            Assert.That(Find(new JsObject("x", 1.5)).Select(d => d["key"]), Is.EqualTo(new[] { "ct_3" }));
            Assert.That(Find(new JsObject("x", true)).Select(d => d["key"]), Is.EqualTo(new[] { "ct_4" }));
            Assert.That(Find(new JsObject("x", 1.0, "key", "ct_2")), Is.Empty);
            Assert.That(Find(new JsObject()).Select(d => d["key"]), Is.EqualTo(new[] { "ct_1", "ct_2", "ct_3", "ct_4" }));
            Assert.That(FindOne(new JsObject("key", "ct_none")), Is.Null);
            Assert.That(Count(), Is.EqualTo(4));
        }

        [Test]
        public void UpdatesSetAndUnsetFields()
        {
            Insert(Parse("{\"username\":\"ct_u\",\"b\":1,\"list\":[1,2],\"gone\":\"x\"}"));

            Update(new JsObject("username", "ct_u"), Parse("{\"zeta\":1,\"alpha\":{\"y\":2,\"x\":1},\"b\":5,\"list\":[3]}"), false,
                new List<string> { "gone", "missing" });

            Assert.That(Text(FindOne(new JsObject("username", "ct_u"))),
                Is.EqualTo(Text(Parse("{\"username\":\"ct_u\",\"b\":5,\"list\":[3],\"zeta\":1,\"alpha\":{\"y\":2,\"x\":1}}"))));
        }

        [Test]
        public void UpsertsCreateFromTheFilterAndTheSetFields()
        {
            Update(new JsObject("username", "ct_new"), Parse("{\"rank\":2,\"friends\":[]}"), true);
            Update(new JsObject("username", "ct_new"), Parse("{\"rank\":3}"), true);
            Update(new JsObject("username", "ct_absent"), Parse("{\"rank\":1}"), false);

            var found = Find(new JsObject());

            Assert.That(found, Has.Count.EqualTo(1));
            Assert.That(found[0]["_id"], Is.InstanceOf<ObjectIdValue>());
            Assert.That(Text(found[0]), Is.EqualTo(Text(Parse("{\"username\":\"ct_new\",\"rank\":3,\"friends\":[]}"))));
        }

        [Test]
        public void DeleteRemovesOneMatchingDocument()
        {
            Insert(Parse("{\"ip\":\"ct_ip\",\"n\":1}"));
            Insert(Parse("{\"ip\":\"ct_ip\",\"n\":2}"));
            Insert(Parse("{\"ip\":\"ct_other\",\"n\":3}"));

            Delete(new JsObject("ip", "ct_ip"));
            Delete(new JsObject("ip", "ct_none"));

            Assert.That(Find(new JsObject()).Select(d => d["n"]), Is.EqualTo(new object[] { 2.0, 3.0 }));
            Assert.That(Count(), Is.EqualTo(2));
        }

        [Test]
        public void UndefinedFieldsReadBackAsUndefined()
        {
            var document = new JsObject("username", "ct_undefined", "guild", null, "list", new List<object> { 1.0, null });
            Insert(document);

            var found = FindOne(new JsObject("username", "ct_undefined"));

            // Stored as null (MongoDB, like the Node driver) or kept as undefined (memory): either way the key is there
            // and reads back as undefined.
            Assert.That(found.Keys, Does.Contain("guild"));
            Assert.That(found["guild"], Is.Null);
            Assert.That(JsJson.Stringify(found["list"]), Is.EqualTo("[1,null]"));
        }

        [Test]
        public void FindsByObjectId()
        {
            Insert(Parse("{\"username\":\"ct_id1\"}"));
            Insert(Parse("{\"username\":\"ct_id2\"}"));

            var id = (ObjectIdValue)FindOne(new JsObject("username", "ct_id2"))["_id"];

            Assert.That(FindOne(new JsObject("_id", new ObjectIdValue(id.Id)))["username"], Is.EqualTo("ct_id2"));
            Assert.That(FindOne(new JsObject("_id", new ObjectIdValue("0123456789abcdef01234567"))), Is.Null);

            // A reset token is set as a nested object, then removed together with a password change (mongodb.ts).
            Update(new JsObject("_id", id), Parse("{\"resetToken\":{\"token\":\"h\",\"expiration\":1700003600000}}"), true);
            Assert.That(JsJson.Stringify(FindOne(new JsObject("_id", id))["resetToken"]),
                Is.EqualTo("{\"token\":\"h\",\"expiration\":1700003600000}"));

            Update(new JsObject("_id", id), Parse("{\"password\":\"p\"}"), true, new List<string> { "resetToken" });
            Assert.That(Text(FindOne(new JsObject("_id", id))), Is.EqualTo(Text(Parse("{\"username\":\"ct_id2\",\"password\":\"p\"}"))));
        }

        // ---- Aggregation (the leaderboards' pipelines from GameDatabase) ----

        List<JsObject> Aggregate(List<object> pipeline) => Wait<List<JsObject>>(callback => Store.Aggregate(Docs, pipeline, callback));

        /// <summary>The results in order, fields in order (the $group output order: _id, then the accumulators).</summary>
        static string Json(List<JsObject> documents) => JsJson.Stringify(documents.ConvertAll(document => (object)document));

        void InsertSkills()
        {
            Insert(Parse("{\"username\":\"ct_a\",\"cheater\":true,\"skills\":[{\"type\":0,\"experience\":100},{\"type\":1,\"experience\":50}]}"));
            Insert(Parse("{\"username\":\"ct_b\",\"skills\":[{\"type\":0,\"experience\":300}]}"));
            // Dropped by $unwind: an empty array and a missing field.
            Insert(Parse("{\"username\":\"ct_c\",\"cheater\":false,\"skills\":[]}"));
            Insert(Parse("{\"username\":\"ct_d\"}"));
            // $sum ignores a string; a double stays a double.
            Insert(Parse("{\"username\":\"ct_e\",\"skills\":[{\"type\":1,\"experience\":20.5},{\"type\":0,\"experience\":\"x\"}]}"));
            // An int32 sum that overflows becomes a 64-bit integer, read back as the exact number.
            Insert(Parse("{\"username\":\"ct_f\",\"cheater\":false,\"skills\":[{\"type\":0,\"experience\":2147483647},{\"type\":1,\"experience\":1}]}"));
        }

        [Test]
        public void TotalExperienceAggregate()
        {
            InsertSkills();

            Assert.That(Json(Aggregate(GameDatabase.TotalExperiencePipeline())), Is.EqualTo(
                "[{\"_id\":\"ct_f\",\"experience\":2147483648,\"cheater\":false}," +
                "{\"_id\":\"ct_b\",\"experience\":300,\"cheater\":null}," +
                "{\"_id\":\"ct_a\",\"experience\":150,\"cheater\":true}," +
                "{\"_id\":\"ct_e\",\"experience\":20.5,\"cheater\":null}]"));
        }

        [Test]
        public void SkillAggregate()
        {
            InsertSkills();

            Assert.That(Json(Aggregate(GameDatabase.SkillPipeline(1))), Is.EqualTo(
                "[{\"_id\":\"ct_a\",\"experience\":50,\"cheater\":true}," +
                "{\"_id\":\"ct_e\",\"experience\":20.5,\"cheater\":null}," +
                "{\"_id\":\"ct_f\",\"experience\":1,\"cheater\":false}]"));
            Assert.That(Json(Aggregate(GameDatabase.SkillPipeline(7))), Is.EqualTo("[]"));
        }

        [Test]
        public void MobAndPvpAggregates()
        {
            Insert(Parse("{\"username\":\"ct_a\",\"cheater\":false,\"mobKills\":{\"rat\":3,\"bat\":9},\"pvpKills\":2}"));
            Insert(Parse("{\"username\":\"ct_b\",\"mobKills\":{\"rat\":1.5},\"pvpKills\":7}"));
            // No mobKills at all (counts 0), and a negative count.
            Insert(Parse("{\"username\":\"ct_c\",\"pvpKills\":-1}"));
            Insert(Parse("{\"username\":\"ct_d\",\"mobKills\":{\"rat\":-1},\"pvpKills\":\"x\"}"));
            // A second document for one player: the counts add up, and $first keeps the first document's cheater.
            Insert(Parse("{\"username\":\"ct_b\",\"cheater\":true,\"mobKills\":{\"rat\":2},\"pvpKills\":1}"));

            Assert.That(Json(Aggregate(GameDatabase.MobPipeline("rat"))), Is.EqualTo(
                "[{\"_id\":\"ct_b\",\"kills\":3.5,\"cheater\":null}," +
                "{\"_id\":\"ct_a\",\"kills\":3,\"cheater\":false}," +
                "{\"_id\":\"ct_c\",\"kills\":0,\"cheater\":null}," +
                "{\"_id\":\"ct_d\",\"kills\":-1,\"cheater\":null}]"));
            Assert.That(Json(Aggregate(GameDatabase.PvpPipeline())), Is.EqualTo(
                "[{\"_id\":\"ct_b\",\"kills\":8,\"cheater\":null}," +
                "{\"_id\":\"ct_a\",\"kills\":2,\"cheater\":false}," +
                "{\"_id\":\"ct_d\",\"kills\":0,\"cheater\":null}," +
                "{\"_id\":\"ct_c\",\"kills\":-1,\"cheater\":null}]"));
        }

        [Test]
        public void AggregationKeepsAtMost150()
        {
            for (var i = 0; i < 160; i++)
                Insert(new JsObject("username", $"ct_{i:000}", "pvpKills", (double)i));

            var results = Aggregate(GameDatabase.PvpPipeline());

            Assert.That(results, Has.Count.EqualTo(150));
            Assert.That(results[0]["_id"], Is.EqualTo("ct_159"));
            Assert.That(results[149]["_id"], Is.EqualTo("ct_010"));
        }
    }

    [TestFixture]
    public sealed class MemoryDocumentStoreContract : DocumentStoreContract
    {
        protected override IDocumentStore CreateStore()
        {
            Node.Install(new EventLoop(), new ParkMiller(1));
            var store = new MemoryDocumentStore(new JsObject());
            Wait<Exception>(store.Connect);
            return store;
        }

        protected override void Pump() => Node.Loop.Run(() => { });
    }

    /// <summary>
    /// The contract on the MongoDB server configured in PixelHero-Data/.env (the user's test database). Only the
    /// <c>ct_docs</c> collection is touched; it is dropped before every case and at the end. Ignored when the server
    /// cannot be reached. Connection details are never printed.
    /// </summary>
    [TestFixture]
    public sealed class MongoDocumentStoreContract : DocumentStoreContract
    {
        MongoDocumentStore _store;

        [OneTimeSetUp]
        public void Connect()
        {
            var env = Path.Combine(TestPaths.Data, ".env");
            if (!File.Exists(env)) Assert.Ignore("PixelHero-Data/.env is missing.");

            var config = Config.Load(Path.Combine(TestPaths.Data, ".env.defaults"), env);
            if (string.IsNullOrEmpty(config.mongodbHost)) Assert.Ignore("No MongoDB host is configured.");

            // Callbacks run on the driver's thread: the cases only complete a task.
            _store = new MongoDocumentStore(config.mongodbHost, config.mongodbPort, config.mongodbUser, config.mongodbPassword,
                config.mongodbDatabase, config.mongodbTls, config.mongodbSrv, config.mongodbAuthSource, action => action());

            Exception error = null;
            var done = new ManualResetEventSlim();
            _store.Connect(e =>
            {
                error = e;
                done.Set();
            });
            if (!done.Wait(TimeSpan.FromSeconds(20))) Assert.Ignore("MongoDB did not answer.");
            if (error != null) Assert.Ignore($"MongoDB is unavailable ({error.GetType().Name}).");
        }

        protected override IDocumentStore CreateStore()
        {
            _store.Database.DropCollection(Docs);
            return _store;
        }

        [OneTimeTearDown]
        public void Drop() => _store?.Database?.DropCollection(Docs);
    }
}
