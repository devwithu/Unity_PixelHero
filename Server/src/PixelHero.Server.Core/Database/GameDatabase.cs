using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;

namespace PixelHero.Server.Database
{
    /// <summary>
    /// What the game asks of the database: common/database/mongodb/mongodb.ts with loader.ts and creator.ts. Game code
    /// only sees this interface and game data (players, JSON documents), never a driver type.
    /// </summary>
    public interface IGameDatabase
    {
        void OnReady(Action callback);
        void OnFail(Action<Exception> callback);
        void CreateConnection();

        void Login(Player player);
        void Register(Player player);
        void Exists(string username, Action<bool> callback);
        void IsIpBanned(string ip, Action<bool> callback);
        void DeleteGuild(string identifier);
        void SetIpBan(string ip, bool banned);
        void SetRank(string username, double rankId);
        void RegisteredCount(Action<double> callback);

        // Password reset (the hub's API).
        void CreateResetToken(string email, Action<ObjectIdValue, string> callback);
        void ResetPassword(object id, object token, string password, Action<bool> callback);

        // Leaderboards (the hub's cache). Entries are the aggregation output: {_id: username, experience|kills, cheater}.
        void GetTotalExperienceAggregate(Action<List<JsObject>> callback);
        void GetSkillAggregate(double skill, Action<List<JsObject>> callback);
        void GetMobAggregate(string key, Action<List<JsObject>> callback);
        void GetPvpAggregate(Action<List<JsObject>> callback);

        // loader.ts
        void LoadEquipment(Player player, Action<List<object>> callback);
        void LoadInventory(Player player, Action<List<object>> callback);
        void LoadBank(Player player, Action<List<object>> callback);
        void LoadQuests(Player player, Action<List<object>> callback);
        void LoadAchievements(Player player, Action<List<object>> callback);
        void LoadSkills(Player player, Action<List<object>> callback);
        void LoadStatistics(Player player, Action<JsObject> callback);
        void LoadAbilities(Player player, Action<JsObject> callback);
        void LoadGuild(string identifier, Action<JsObject> callback);
        void LoadGuilds(double from, double to, Action<List<JsObject>, double> callback);
        void FindGuildByOwner(string owner, Action<JsObject> callback);

        // creator.ts
        void Save(Player player);
        void SaveGuild(JsObject guild, Action callback = null);

        /// <summary>Whether a connection exists (mongodb.ts hasDatabase, without the log line).</summary>
        bool Connected { get; }
    }

    /// <summary>
    /// The original's database logic over an <see cref="IDocumentStore"/>. With no store (SKIP_DATABASE and no
    /// MongoDB) it behaves like the original when the connection failed: every query is skipped.
    /// </summary>
    public sealed class GameDatabase : IGameDatabase
    {
        readonly IDocumentStore _store;

        /// <summary>Set once connect() succeeds (mongodb.ts `this.database`).</summary>
        bool _connected;

        /// <summary>Whether the loader exists (it is created when the connection attempt settles).</summary>
        bool _loaderReady;

        Action _readyCallback;
        Action<Exception> _failCallback;

        public GameDatabase(IDocumentStore store)
        {
            _store = store;

            // Attempt to connect to MongoDB.
            CreateConnection();
        }

        public bool Connected => _connected;

        public void OnReady(Action callback) => _readyCallback = callback;

        public void OnFail(Action<Exception> callback) => _failCallback = callback;

        /// <summary>client.connect().then(ready).catch(fail).</summary>
        public void CreateConnection()
        {
            void Settled(Exception error)
            {
                if (error == null)
                {
                    _connected = true;
                    _loaderReady = true;

                    _readyCallback?.Invoke();

                    Log.Notice("Successfully connected to the MongoDB server.");
                    return;
                }

                // .catch runs one promise job after the rejected .then.
                Node.QueueMicrotask(() =>
                {
                    // Initializes an empty loader controller.
                    _loaderReady = true;

                    _failCallback?.Invoke(error);
                });
            }

            if (_store == null)
                Node.SetImmediate(() => Node.QueueMicrotask(() => Settled(new Exception("No MongoDB server."))));
            else _store.Connect(Settled);
        }

        bool HasDatabase()
        {
            if (!_connected) Log.Error("No connection established for the database.");

            return _connected;
        }

        /// <summary>The loader's `!this.database || config.skipDatabase` test.</summary>
        bool LoaderSkips => !_connected || Config.Current.skipDatabase;

        public void Login(Player player)
        {
            if (!HasDatabase()) return;

            _store.Find("player_info", new JsObject("username", player.username), playerInfo =>
            {
                // Reject if we cannot find any data about the player.
                if (playerInfo.Count == 0) player.connection.Reject("invalidlogin");
                else
                {
                    var info = playerInfo[0];

                    Utils.Compare(player.password, info["password"], result =>
                    {
                        // Reject if the password is incorrect.
                        if (!result)
                        {
                            player.connection.Reject("invalidlogin");
                            return;
                        }

                        // Successfully passed login checks, we can send packets now.
                        player.authenticated = true;

                        // Login successful, load player data.
                        player.Load(info);
                    });
                }
            });
        }

        public void Register(Player player)
        {
            if (!HasDatabase()) return;

            // Verify account credentials for input validity and ensure username isn't profane.
            if (!Creator.VerifyPlayer(player) || Filter.IsProfane(player.username))
            {
                player.connection.Reject("invalidinput");
                return;
            }

            // Check if email exists.
            _store.Find("player_info", new JsObject("email", player.email), emailData =>
            {
                // If email exists and is specified by player, we check database for duplicates and reject if that's the case.
                if (emailData.Count > 0 && player.email != "")
                {
                    player.connection.Reject("emailexists");
                    return;
                }

                _store.Find("player_info", new JsObject("username", player.username), playerInfo =>
                {
                    // User exists and so we reject instead of double registering.
                    if (playerInfo.Count > 0)
                    {
                        player.connection.Reject("userexists");
                        return;
                    }

                    // Successfully managed to create a new user.
                    player.authenticated = true;

                    Log.Debug($"No player data found for {player.username}, creating user.");

                    player.statistics.creationTime = Node.DateNow();

                    player.Load(Creator.Serialize(player));
                });
            });
        }

        public void Exists(string username, Action<bool> callback)
        {
            if (!HasDatabase()) return;

            _store.Find("player_info", new JsObject("username", username), info => callback(info.Count > 0));
        }

        public void IsIpBanned(string ip, Action<bool> callback)
        {
            if (!HasDatabase())
            {
                callback(false);
                return;
            }

            _store.Find("ipbans", new JsObject("ip", ip), bans => callback(bans.Count > 0));
        }

        public void DeleteGuild(string identifier)
        {
            if (!HasDatabase()) return;

            _store.DeleteOne("guilds", new JsObject("identifier", identifier));
        }

        public void SetIpBan(string ip, bool banned)
        {
            if (!HasDatabase()) return;

            if (banned) _store.InsertOne("ipbans", new JsObject("ip", ip));

            if (!banned) _store.DeleteOne("ipbans", new JsObject("ip", ip));
        }

        public void SetRank(string username, double rankId)
        {
            if (!HasDatabase()) return;

            _store.Find("player_info", new JsObject("username", username), info =>
            {
                if (info.Count == 0)
                {
                    Log.Warning($"No player found with the username {username}.");
                    return;
                }

                _store.UpdateOne("player_info", new JsObject("username", username), new JsObject("rank", rankId), true);
            });
        }

        public void RegisteredCount(Action<double> callback)
        {
            if (!HasDatabase()) return;

            _store.CountDocuments("player_info", callback);
        }

        /// <summary>
        /// Attempts to creata a reset token, hashes it, and applies it to an account. Calls back with the account's id
        /// and the unhashed token (nothing when the email is unknown or there is no database).
        /// </summary>
        public void CreateResetToken(string email, Action<ObjectIdValue, string> callback)
        {
            if (!HasDatabase())
            {
                callback(null, null);
                return;
            }

            // Attempt to grab the email provided from the database.
            _store.FindOne("player_info", new JsObject("email", email), playerInfo =>
            {
                // If we don't find any account matching the email, just stop here.
                if (playerInfo == null)
                {
                    callback(null, null);
                    return;
                }

                // crypto.randomBytes(32).toString('hex').
                var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

                // Create a reset token and hash it.
                Utils.Hash(token, hash =>
                {
                    // Create a reset token and apply a one hour expiration.
                    var resetToken = new JsObject("token", hash, "expiration", Node.DateNow() + 60 * 60 * 1000);

                    // Update the account with the new reset token.
                    _store.UpdateOne("player_info", new JsObject("email", email), new JsObject("resetToken", resetToken), true);
                });

                // Create a callback with the player's ID and the unhashed token.
                callback(playerInfo["_id"] as ObjectIdValue, token);
            });
        }

        /// <summary>Validates a reset token and sets the new password (removing the token).</summary>
        public void ResetPassword(object id, object token, string password, Action<bool> callback)
        {
            if (!HasDatabase())
            {
                callback(false);
                return;
            }

            var objectId = NewObjectId(id);

            // Attempt to grab the account from the database.
            _store.FindOne("player_info", new JsObject("_id", objectId), playerInfo =>
            {
                // If we don't find any account matching the ID, just stop here.
                if (playerInfo == null)
                {
                    callback(false);
                    return;
                }

                var resetToken = playerInfo["resetToken"];

                // If no reset token exists, just stop here.
                if (!JsData.Truthy(resetToken))
                {
                    callback(false);
                    return;
                }

                // If the reset token is expired, just stop here.
                if (JsAny.ToNumber(JsAny.Get(resetToken, "expiration")) < Node.DateNow())
                {
                    callback(false);
                    return;
                }

                // If the reset token doesn't match, just stop here.
                Utils.Compare(token as string, JsAny.Get(resetToken, "token"), result =>
                {
                    if (!result)
                    {
                        Log.Warning($"Invalid reset token for {JsString.Of(playerInfo["username"])}.");
                        callback(false);
                        return;
                    }

                    // Create a hash of the new password and store it in the database. We also remove the reset token
                    // so it can't be used again.
                    Utils.Hash(password, hash =>
                        _store.UpdateOne("player_info", new JsObject("_id", objectId), new JsObject("password", hash),
                            new List<string> { "resetToken" }, true));

                    callback(true);
                });
            });
        }

        /// <summary>
        /// new ObjectId(id): a 24 character hex string, or a number (an id made from that time, which matches no
        /// account here). Anything else throws, as bson does.
        /// </summary>
        static ObjectIdValue NewObjectId(object id)
        {
            if (id is string text && text.Length == 24 && text.All(Uri.IsHexDigit)) return new ObjectIdValue(text.ToLowerInvariant());

            if (id is double time)
                return new ObjectIdValue(((uint)(long)time).ToString("x8") +
                    Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());

            throw new Exception("input must be a 24 character hex string, 12 byte Uint8Array, or an integer");
        }

        /// <summary>The total experience of each player (descending, at most 150).</summary>
        public void GetTotalExperienceAggregate(Action<List<JsObject>> callback)
        {
            if (NoDatabase(callback)) return;

            _store.Aggregate("player_skills", TotalExperiencePipeline(), callback);
        }

        /// <summary>The experience of each player in one skill (descending, at most 150).</summary>
        public void GetSkillAggregate(double skill, Action<List<JsObject>> callback)
        {
            if (NoDatabase(callback)) return;

            _store.Aggregate("player_skills", SkillPipeline(skill), callback);
        }

        /// <summary>The kills of one mob by each player (descending, at most 150).</summary>
        public void GetMobAggregate(string key, Action<List<JsObject>> callback)
        {
            if (NoDatabase(callback)) return;

            _store.Aggregate("player_statistics", MobPipeline(key), callback);
        }

        /// <summary>The PvP kills of each player (descending, at most 150).</summary>
        public void GetPvpAggregate(Action<List<JsObject>> callback)
        {
            if (NoDatabase(callback)) return;

            _store.Aggregate("player_statistics", PvpPipeline(), callback);
        }

        /// <summary>
        /// Port fix (ANALYSIS.md §9 #15): without a database the original's aggregations never called back, so the
        /// hub's /leaderboards request never ended. They answer an empty list instead.
        /// </summary>
        bool NoDatabase(Action<List<JsObject>> callback)
        {
            if (HasDatabase()) return false;

            callback(new List<JsObject>());
            return true;
        }

        // The leaderboards' aggregation pipelines (mongodb.ts), also run by the store contract tests.

        /// <summary>Unwinds array, groups by total experience, sorts in descending order.</summary>
        public static List<object> TotalExperiencePipeline() => new()
        {
            new JsObject("$unwind", "$skills"), // Unwinds (transforms into multiple objects for each skill).
            new JsObject("$group", new JsObject(
                "_id", "$username",
                "experience", new JsObject("$sum", "$skills.experience"),
                "cheater", new JsObject("$first", "$cheater"))),
            new JsObject("$sort", new JsObject("experience", -1.0)),
            new JsObject("$limit", 150.0)
        };

        /// <summary>Unwind the array, match the skill, sort in descending order, limit to 250.</summary>
        public static List<object> SkillPipeline(double skill) => new()
        {
            new JsObject("$unwind", "$skills"),
            new JsObject("$match", new JsObject("skills.type", skill)),
            new JsObject("$group", new JsObject(
                "_id", "$username",
                "experience", new JsObject("$first", "$skills.experience"),
                "cheater", new JsObject("$first", "$cheater"))),
            new JsObject("$sort", new JsObject("experience", -1.0)),
            new JsObject("$limit", 150.0)
        };

        public static List<object> MobPipeline(string key) => new()
        {
            new JsObject("$group", new JsObject(
                "_id", "$username",
                "kills", new JsObject("$sum", $"$mobKills.{key}"),
                "cheater", new JsObject("$first", "$cheater"))),
            new JsObject("$sort", new JsObject("kills", -1.0)),
            new JsObject("$limit", 150.0)
        };

        public static List<object> PvpPipeline() => new()
        {
            new JsObject("$group", new JsObject(
                "_id", "$username",
                "kills", new JsObject("$sum", "$pvpKills"),
                "cheater", new JsObject("$first", "$cheater"))),
            new JsObject("$sort", new JsObject("kills", -1.0)),
            new JsObject("$limit", 150.0)
        };

        // ---- loader.ts ----

        /// <summary>loader.load: calls back at once with nothing when there is no database.</summary>
        void Load(string username, string collection, Action<List<JsObject>> callback)
        {
            // Used for when we're working without a database to return empty data.
            if (LoaderSkips)
            {
                callback(null);
                return;
            }

            _store.Find(collection, new JsObject("username", username), info =>
            {
                if (info.Count > 1) Log.Debug($"[{collection}] Duplicate entry for {username}.");

                // Return empty array if we can't find any data.
                if (info.Count == 0)
                {
                    callback(null);
                    return;
                }

                // Return the raw data from the database.
                callback(info);
            });
        }

        /// <summary>`database.loader?.x`: nothing happens until the loader exists.</summary>
        bool HasLoader => _loaderReady;

        static List<object> Field(List<JsObject> info, string key) =>
            (info[0] ?? throw new JsTypeError($"Cannot destructure property '{key}'"))[key] as List<object>;

        public void LoadEquipment(Player player, Action<List<object>> callback)
        {
            if (!HasLoader) return;

            Load(player.username, "player_equipment", info =>
            {
                if (info == null)
                {
                    Log.Debug($"[player_equipment] No equipment found for {player.username}.");
                    callback(new List<object>());
                    return;
                }

                callback(Field(info, "equipments"));
            });
        }

        public void LoadInventory(Player player, Action<List<object>> callback)
        {
            if (!HasLoader) return;

            Load(player.username, "player_inventory", info =>
            {
                if (info == null)
                {
                    Log.Debug($"[player_inventory] No inventory found for {player.username}.");
                    callback(new List<object>());
                    return;
                }

                callback(Field(info, "slots"));
            });
        }

        public void LoadBank(Player player, Action<List<object>> callback)
        {
            if (!HasLoader) return;

            Load(player.username, "player_bank", info =>
            {
                if (info == null)
                {
                    Log.Debug($"[player_bank] No bank found for {player.username}.");
                    callback(new List<object>());
                    return;
                }

                callback(Field(info, "slots"));
            });
        }

        /// <summary>
        /// `await loader?.loadQuests(this)`: the loader's promise resolves when the data arrives, then the await
        /// resumes one promise job later. Without a loader the await resumes on undefined.
        /// </summary>
        public void LoadQuests(Player player, Action<List<object>> callback)
        {
            if (!HasLoader)
            {
                Node.QueueMicrotask(() => callback(null));
                return;
            }

            Load(player.username, "player_quests", info =>
            {
                var quests = info == null ? new List<object>() : Field(info, "quests");
                Node.QueueMicrotask(() => callback(quests));
            });
        }

        public void LoadAchievements(Player player, Action<List<object>> callback)
        {
            if (!HasLoader)
            {
                Node.QueueMicrotask(() => callback(null));
                return;
            }

            Load(player.username, "player_achievements", info =>
            {
                var achievements = info == null ? new List<object>() : Field(info, "achievements");
                Node.QueueMicrotask(() => callback(achievements));
            });
        }

        public void LoadSkills(Player player, Action<List<object>> callback)
        {
            if (!HasLoader) return;

            Load(player.username, "player_skills", info =>
            {
                if (info == null)
                {
                    Log.Debug($"[player_skills] No skills found for {player.username}.");
                    callback(new List<object>());
                    return;
                }

                callback(Field(info, "skills"));
            });
        }

        public void LoadStatistics(Player player, Action<JsObject> callback)
        {
            if (!HasLoader) return;

            Load(player.username, "player_statistics", info =>
            {
                if (info == null)
                {
                    Log.Debug($"[player_statistics] No statistics found for {player.username}.");
                    return;
                }

                callback(info[0]);
            });
        }

        public void LoadAbilities(Player player, Action<JsObject> callback)
        {
            if (!HasLoader) return;

            Load(player.username, "player_abilities", info =>
            {
                if (info == null)
                {
                    Log.Debug($"[player_abilities] No abilities found for {player.username}.");
                    return;
                }

                callback(info[0]);
            });
        }

        /// <summary>async loadGuild(identifier): resolves with undefined when there is no database.</summary>
        public void LoadGuild(string identifier, Action<JsObject> callback)
        {
            if (LoaderSkips)
            {
                Node.QueueMicrotask(() => callback(null));
                return;
            }

            _store.Find("guilds", new JsObject("identifier", identifier), info =>
            {
                if (info.Count > 1) Log.Warning($"[Guilds] Duplicate entry for {identifier}.");

                // Return if we can't find any data.
                var guild = info.Count == 0 ? null : info[0];

                // `await cursor.toArray()` resumes one job after the result, then the async function resolves.
                Node.QueueMicrotask(() => callback(guild));
            });
        }

        public void LoadGuilds(double from, double to, Action<List<JsObject>, double> callback)
        {
            if (LoaderSkips)
            {
                callback(new List<JsObject>(), 0);
                return;
            }

            _store.Find("guilds", new JsObject(), info => callback(info, info.Count));
        }

        public void FindGuildByOwner(string owner, Action<JsObject> callback)
        {
            if (LoaderSkips)
            {
                callback(null);
                return;
            }

            _store.Find("guilds", new JsObject("owner", owner), info =>
            {
                // If we find more than one guild with the same owner, log a warning.
                if (info.Count > 1) Log.Warning($"[Guilds] Duplicate entry for {owner}.");

                // Return empty array if we can't find any data.
                if (info.Count == 0)
                {
                    callback(null);
                    return;
                }

                // Return the raw data from the database.
                callback(info[0]);
            });
        }

        // ---- creator.ts ----

        /// <summary>`database.creator?.save(player)`: nothing without a connection.</summary>
        public void Save(Player player)
        {
            if (!_connected) return;

            try
            {
                Log.Debug($"Saving player information for {player.username}.");

                SaveInfo(player);
                UpdateCollection("player_equipment", player.username, player.equipment.Serialize());
                UpdateCollection("player_inventory", player.username, player.inventory.Serialize());
                UpdateCollection("player_bank", player.username, player.bank.Serialize());
                UpdateCollection("player_quests", player.username, player.quests.Serialize());
                UpdateCollection("player_achievements", player.username, player.achievements.Serialize());
                UpdateCollection("player_skills", player.username, player.skills.Serialize());
                UpdateCollection("player_statistics", player.username, player.statistics.Serialize());
                UpdateCollection("player_abilities", player.username, player.abilities.Serialize());
            }
            catch (Exception error)
            {
                Log.Error($"Could not save data for {player.username}.");
                Log.Error(error);
            }
        }

        void SaveInfo(Player player)
        {
            if (_store is MemoryDocumentStore memory) memory.Touch("player_info");

            GetPlayerWithHash(player, playerInfo => UpdateCollection("player_info", player.username, playerInfo));
        }

        /// <summary>database.creator.saveGuild(...) has no optional chaining: without a connection there is no creator.</summary>
        public void SaveGuild(JsObject guild, Action callback = null)
        {
            if (!_connected) throw new JsTypeError("Cannot read properties of undefined (reading 'saveGuild')");

            _store.UpdateOne("guilds", new JsObject("identifier", guild["identifier"]), guild, true, _ => callback?.Invoke());
        }

        void UpdateCollection(string collection, string username, JsObject data) =>
            _store.UpdateOne(collection, new JsObject("username", username), data, true);

        void GetPlayerWithHash(Player player, Action<JsObject> callback)
        {
            var info = Creator.Serialize(player);

            Utils.Hash(player.password, hash =>
            {
                info.Set("password", hash);

                callback(info);
            });
        }
    }

    /// <summary>creator.ts' static helpers.</summary>
    public static class Creator
    {
        public static bool VerifyPlayer(Player player)
        {
            if (!Utils.IsValidUsername(player.username)) return false;
            if (string.IsNullOrEmpty(player.username)) return false;
            if (string.IsNullOrEmpty(player.password) || player.password.Length < 3 || player.password.Length > 64)
                return false;
            if (!Utils.IsEmail(player.email)) return false;

            return true;
        }

        /// <summary>PlayerInfo. resetToken is undefined ("to prevent it from being saved"); MongoDB stores it as null.</summary>
        public static JsObject Serialize(Player player) => new(
            "username", player.username,
            "password", player.password,
            "email", player.email,
            "x", player.x,
            "y", player.y,
            "userAgent", player.userAgent,
            "rank", (double)player.rank,
            "poison", new JsObject(
                "type", player.poison != null ? (double)player.poison.type : -1.0,
                "remaining", player.poison != null ? player.poison.GetRemainingTime() : -1.0),
            "effects", player.status.Serialize(),
            "hitPoints", player.hitPoints.GetHitPoints(),
            "mana", player.mana.GetMana(),
            "orientation", player.orientation,
            "ban", player.ban,
            "mute", player.mute,
            "jail", player.jail,
            "lastWarp", player.lastWarp,
            "mapVersion", player.mapVersion,
            "regionsLoaded", player.regionsLoaded.ConvertAll(region => (object)region),
            "friends", player.friends.Serialize(),
            "lastServerId", Config.Current.serverId,
            "lastAddress", player.connection.address,
            "lastGlobalChat", player.lastGlobalChat,
            "guild", player.guild,
            "pet", player.pet != null ? player.pet.key : "",
            "resetToken", null);
    }
}
