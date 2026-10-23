using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>player/friends.ts. The list is {username: {online, serverId}}.</summary>
    public sealed class Friends
    {
        // A friend object has a key of the username and a value of the online status
        readonly JsObject list = new();

        Action loadCallback;
        Action<string, bool, double> addCallback;
        Action<string> removeCallback;
        Action<string, bool, double> statusCallback;

        readonly Player player;

        public Friends(Player player) => this.player = player;

        public void Load(List<object> friends = null)
        {
            friends ??= new List<object>();

            // Nothing to load.
            if (friends.Count == 0) return;

            foreach (var entry in friends)
            {
                var username = JsString.Of(entry);
                var online = player.world.IsOnline(username);

                list.Set(username, new JsObject("online", online, "serverId", online ? Config.Current.serverId : -1.0));
            }

            loadCallback?.Invoke();
        }

        public void Add(string username)
        {
            username = JsString.ToLowerCase(username ?? throw new JsTypeError("Cannot read properties of undefined (reading 'toLowerCase')"));

            // Check that someone isn't messing with the client input :)
            if (username.Length > 32)
            {
                player.Notify("misc:FRIENDS_USERNAME_TOO_LONG");
                return;
            }

            if (username == player.username)
            {
                player.Notify("misc:FRIENDS_ADD_SELF");
                return;
            }

            // Ensure the player is not already on the list.
            if (HasFriend(username))
            {
                player.Notify("misc:FRIENDS_ALREADY_ADDED");
                return;
            }

            // Ensure the player exists.
            player.database.Exists(username, exists =>
            {
                if (!exists)
                {
                    player.Notify("misc:FRIENDS_USER_DOES_NOT_EXIST");
                    return;
                }

                // Add the friend and check if they are online.
                var online = player.world.IsOnline(username);

                list.Set(username, new JsObject("online", online, "serverId", online ? Config.Current.serverId : -1.0));

                // Add the friend to the list and pass on the online status to the client.
                var friend = list.Obj(username);
                addCallback?.Invoke(username, friend.Bool("online") ?? false, friend.Num("serverId") ?? double.NaN);

                // If the friend we just added is not online we ask to sync across all servers.
                if (!online) Sync(new List<object> { username });
            });
        }

        public void Remove(string username)
        {
            username = JsString.ToLowerCase(username ?? throw new JsTypeError("Cannot read properties of undefined (reading 'toLowerCase')"));

            // No username was found in the list.
            if (!HasFriend(username))
            {
                player.Notify("misc:FRIENDS_NOT_IN_LIST");
                return;
            }

            list.Delete(username);

            removeCallback?.Invoke(username);
        }

        public void Sync(List<object> inactiveFriends = null)
        {
            inactiveFriends ??= GetInactiveFriends();

            // Skip if there are no inactive friends.
            if (inactiveFriends.Count == 0) return;

            // Relay the message to the hub.
            player.world.client.Send(new FriendsPacket(Opcodes.Friends.Sync, new JsObject(
                "username", player.username,
                "inactiveFriends", inactiveFriends)));
        }

        public JsObject GetFriendsList() => list;

        public List<object> GetInactiveFriends() =>
            list.Keys.Where(username => !(list.Obj(username).Bool("online") ?? false)).Cast<object>().ToList();

        public bool HasFriend(string username) => list.Has(username);

        public void SetStatus(string username, bool status, double serverId)
        {
            var friend = list.Obj(username);

            if (friend == null) return;

            friend.Set("online", status);
            friend.Set("serverId", status ? serverId : -1);

            statusCallback?.Invoke(username, status, friend.Num("serverId") ?? double.NaN);
        }

        public void SetActiveFriends(JsObject activeList)
        {
            foreach (var pair in activeList)
            {
                var friend = (JsObject)pair.Value;

                SetStatus(pair.Key, friend.Bool("online") ?? false, friend.Num("serverId") ?? double.NaN);
            }
        }

        public void OnLoad(Action callback) => loadCallback = callback;

        public void OnAdd(Action<string, bool, double> callback) => addCallback = callback;

        public void OnRemove(Action<string> callback) => removeCallback = callback;

        public void OnStatus(Action<string, bool, double> callback) => statusCallback = callback;

        public List<object> Serialize() => list.Keys.Cast<object>().ToList();
    }

    /// <summary>player/statistics.ts.</summary>
    public sealed class Statistics
    {
        readonly double[] milestones = { 10, 50, 100, 500, 1000, 5000, 10_000 };

        public double pvpKills;
        public double pvpDeaths;
        public JsObject mobKills = new();
        public List<object> mobExamines = new();
        public JsObject resources = new();
        public JsObject drops = new();
        public double creationTime; // Time of game's creation.
        public double totalTimePlayed; // Total time played in milliseconds.
        public double averageTimePlayed;
        public double lastLogin;
        public double loginCount = 1;

        // Class variables for calculating login time, etc.
        public double loginTime; // Time when player logged in.

        readonly Player player;

        public Statistics(Player player)
        {
            // Field initializers: creationTime, lastLogin and loginTime are getTime() (seconds).
            creationTime = GetTime();
            lastLogin = GetTime();
            loginTime = GetTime();

            this.player = player;
        }

        static object Or(object a, object b) => JsData.Truthy(a) ? a : b;

        public void Load(JsObject data)
        {
            pvpKills = (double)Or(data["pvpKills"], pvpKills);
            pvpDeaths = (double)Or(data["pvpDeaths"], pvpDeaths);
            mobKills = (JsObject)Or(data["mobKills"], mobKills);
            mobExamines = (List<object>)Or(data["mobExamines"], mobExamines);
            resources = (JsObject)Or(data["resources"], resources);
            drops = (JsObject)Or(data["drops"], drops);
            creationTime = (double)Or(data["creationTime"], creationTime);
            totalTimePlayed = (double)Or(data["totalTimePlayed"], totalTimePlayed);
            averageTimePlayed = (double)Or(data["averageTimePlayed"], averageTimePlayed);
            lastLogin = (double)Or(data["lastLogin"], lastLogin);

            // data.loginCount + 1 || this.loginCount
            var count = JsAny.ToNumber(data["loginCount"]) + 1;
            loginCount = count != 0 && !double.IsNaN(count) ? count : loginCount;
        }

        public void HandleSkill(Modules.Skills skill)
        {
            // Skip foraging since we don't have any achievements for it.
            if (skill == Modules.Skills.Foraging) return;

            // Get the skill name and the intervals for the milestones.
            var skillName = JsString.ToLowerCase(skill.ToString());

            if (!resources.Has(skillName)) resources.Set(skillName, 0.0);

            // Increment the skill's resource count.
            var count = JsAny.ToNumber(resources[skillName]) + 1;
            resources.Set(skillName, count);

            // Check if we have reached a milestone and award an achievement if we have.
            if (milestones.Contains(count)) player.achievements.Get($"{skillName}{JsString.Of(count)}")?.Finish();
        }

        public void AddMobKill(string key)
        {
            if (!mobKills.Has(key)) mobKills.Set(key, 0.0);

            mobKills.Set(key, JsAny.ToNumber(mobKills[key]) + 1);
        }

        public void AddMobExamine(string key)
        {
            if (mobExamines.Any(item => item is string s && s == key)) return;

            mobExamines.Add(key);

            // Handle achievements for each milestone
            switch (mobExamines.Count)
            {
                case 10:
                    player.achievements.Get("examiner10").Finish();
                    return;
                case 25:
                    player.achievements.Get("examiner25").Finish();
                    return;
                case 50:
                    player.achievements.Get("examiner50").Finish();
                    return;
            }
        }

        public void AddDrop(string key, double count = 1)
        {
            if (!drops.Has(key)) drops.Set(key, count);

            drops.Set(key, JsAny.ToNumber(drops[key]) + count);
        }

        public void CalculateAverageTimePlayed()
        {
            var timePlayed = Node.DateNow() - loginTime;

            averageTimePlayed = averageTimePlayed == 0
                ? timePlayed
                : Math.Floor((averageTimePlayed * (loginCount - 1) + timePlayed) / loginCount);
        }

        public JsObject Serialize()
        {
            // Serializing also gets treated as a logging out event, so we calculate stuff here.
            lastLogin = GetTime();
            totalTimePlayed += Node.DateNow() - loginTime; // add time played to total time played.

            CalculateAverageTimePlayed();

            return new JsObject(
                "pvpKills", pvpKills,
                "pvpDeaths", pvpDeaths,
                "mobKills", mobKills,
                "mobExamines", mobExamines,
                "resources", resources,
                "drops", drops,
                "creationTime", creationTime,
                "totalTimePlayed", totalTimePlayed,
                "averageTimePlayed", averageTimePlayed,
                "lastLogin", lastLogin,
                "loginCount", loginCount,
                "cheater", player.IsCheater());
        }

        static double GetTime() => Math.Floor(Node.DateNow() / 1000);
    }
}
