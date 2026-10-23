using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// The password reset (mongodb.ts createResetToken/resetPassword, used by the hub's API) on the memory store: the
    /// token is 32 random bytes in hex, stored as a bcrypt hash with a one-hour expiration, and a matching reset sets
    /// the new password's hash and removes the token. The store operations themselves are in the store contract.
    /// </summary>
    [NonParallelizable]
    public class ResetTokenTests
    {
        const double Epoch = 1_700_000_000_000;

        EventLoop _loop;
        JsObject _collections;
        GameDatabase _database;

        [SetUp]
        public void Connect()
        {
            _loop = new EventLoop(Epoch);
            Node.Install(_loop, new ParkMiller(1));
            Log.Sink = (_, _) => { };

            _collections = new JsObject("player_info", new List<object>
            {
                new JsObject("_id", new ObjectIdValue("65a000000000000000000001"), "username", "first", "email", "first@example.com", "password", BCrypt.Net.BCrypt.HashPassword("old", 4)),
                new JsObject("_id", new ObjectIdValue("65a000000000000000000002"), "username", "second", "email", "second@example.com", "password", "x")
            });

            _loop.Run(() => _database = new GameDatabase(new MemoryDocumentStore(_collections)));
        }

        JsObject Account(string username) =>
            ((List<object>)_collections["player_info"]).Cast<JsObject>().First(account => account.Str("username") == username);

        (ObjectIdValue id, string token) CreateToken(string email)
        {
            (ObjectIdValue, string)? result = null;
            _loop.Run(() => _database.CreateResetToken(email, (id, token) => result = (id, token)));
            Assert.That(result, Is.Not.Null, "createResetToken did not call back");
            return result.Value;
        }

        bool Reset(object id, object token, string password)
        {
            bool? result = null;
            _loop.Run(() => _database.ResetPassword(id, token, password, status => result = status));
            Assert.That(result, Is.Not.Null, "resetPassword did not call back");
            return result.Value;
        }

        [Test]
        public void UnknownEmailGivesNothing()
        {
            var (id, token) = CreateToken("nobody@example.com");

            Assert.That(id, Is.Null);
            Assert.That(token, Is.Null);
            Assert.That(Account("first").Has("resetToken"), Is.False);
        }

        [Test]
        public void TokenIsHashedWithAnHourToLive()
        {
            var (id, token) = CreateToken("first@example.com");

            Assert.That(id, Is.Not.Null.And.EqualTo(Account("first")["_id"]));
            Assert.That(token, Does.Match("^[0-9a-f]{64}$"));

            var stored = Account("first").Obj("resetToken");
            Assert.That(BCrypt.Net.BCrypt.Verify(token, stored.Str("token")), Is.True);
            Assert.That(stored.Num("expiration"), Is.EqualTo(Epoch + 60 * 60 * 1000));
            Assert.That(Account("second").Has("resetToken"), Is.False);
        }

        [Test]
        public void MatchingTokenSetsThePasswordOnce()
        {
            var (id, token) = CreateToken("first@example.com");

            Assert.That(Reset(id.Id, "0" + token.Substring(1) == token ? "1" + token.Substring(1) : "0" + token.Substring(1), "newpass"),
                Is.False, "a wrong token");
            Assert.That(BCrypt.Net.BCrypt.Verify("old", Account("first").Str("password")), Is.True);

            Assert.That(Reset(id.Id, token, "newpass"), Is.True);
            Assert.That(BCrypt.Net.BCrypt.Verify("newpass", Account("first").Str("password")), Is.True);
            Assert.That(Account("first").Has("resetToken"), Is.False);

            Assert.That(Reset(id.Id, token, "again"), Is.False, "the token is gone");
            Assert.That(BCrypt.Net.BCrypt.Verify("newpass", Account("first").Str("password")), Is.True);
        }

        [Test]
        public void ExpiredTokenIsRefused()
        {
            var (id, token) = CreateToken("first@example.com");

            _loop.RunUntil(60 * 60 * 1000);
            Assert.That(Reset(id.Id, token, "newpass"), Is.True, "still valid at exactly one hour");

            (id, token) = CreateToken("first@example.com");
            _loop.RunUntil(2 * 60 * 60 * 1000 + 1);
            Assert.That(Reset(id.Id, token, "later"), Is.False);
        }

        [Test]
        public void UnknownOrInvalidIds()
        {
            var (_, token) = CreateToken("first@example.com");

            Assert.That(Reset("0123456789abcdef01234567", token, "newpass"), Is.False, "no such account");
            Assert.That(Reset(1700000000.0, token, "newpass"), Is.False, "a number makes an id that matches nothing");
            Assert.That(() => _database.ResetPassword("not-an-id", token, "newpass", _ => { }), Throws.Exception
                .With.Message.EqualTo("input must be a 24 character hex string, 12 byte Uint8Array, or an integer"));
        }

        [Test]
        public void WithoutADatabaseNothingIsFound()
        {
            GameDatabase database = null;
            _loop.Run(() => database = new GameDatabase(null));

            (ObjectIdValue, string)? created = null;
            bool? reset = null;
            database.CreateResetToken("first@example.com", (id, token) => created = (id, token));
            database.ResetPassword("0123456789abcdef01234567", "t", "newpass", status => reset = status);

            Assert.That(created, Is.EqualTo(((ObjectIdValue)null, (string)null)));
            Assert.That(reset, Is.False);
        }
    }
}
