using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Game;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Controllers
{
    /// <summary>
    /// controllers/guilds.ts. The async methods run synchronously up to their first await (loadGuild); the rest
    /// continues in the callback. An exception after an await is an unhandled rejection in the original (the process
    /// dies), so continuations throw through the event loop's uncaught handler.
    /// </summary>
    public sealed class Guilds
    {
        readonly IGameDatabase database;
        readonly World world;

        public Guilds(World world)
        {
            this.world = world;
            database = world.database;
        }

        static List<object> Members(JsObject guild) => guild.Arr("members");

        public void Create(Player player, string name, object banner, object outline, object outlineColour, object crest)
        {
            // Ensure the player isn't already in a guild.
            if (JsData.Truthy(player.guild))
            {
                player.Notify("guilds:ALREADY_IN_GUILD");
                return;
            }

            // Ensure that guests cannot create guilds.
            if (player.isGuest)
            {
                player.Notify("guilds:NOT_ALLOWED_GUESTS_CREATE");
                return;
            }

            // Prevent players from creating a guild if they haven't finished the tutorial.
            if (!player.quests.IsTutorialFinished())
            {
                player.GuildNotify("guilds:MUST_FINISH_TUTORIAL_CREATE");
                return;
            }

            // Ensure the player has enough gold to create a guild.
            if (!player.inventory.HasItem("gold", 30_000))
            {
                player.GuildNotify("guilds:NOT_ENOUGH_GOLD");
                return;
            }

            var identifier = JsString.ToLowerCase(name ?? throw new JsTypeError("Cannot read properties of undefined (reading 'toLowerCase')"));

            database.LoadGuild(identifier, guild =>
            {
                // Ensure a guild doesn't already exist with that name.
                if (guild != null)
                {
                    player.GuildNotify("A guild with that name already exists.");
                    return;
                }

                // Remove the gold from the player's inventory.
                player.inventory.RemoveItem("gold", 30_000);

                var data = new JsObject(
                    "identifier", identifier,
                    "name", name,
                    "creationDate", Node.DateNow(),
                    "inviteOnly", false,
                    "experience", 0.0,
                    "owner", player.username,
                    "members", new List<object>
                    {
                        new JsObject(
                            "username", player.username,
                            "rank", (double)Modules.GuildRank.Landlord,
                            "joinDate", Node.DateNow())
                    },
                    "decoration", new JsObject(
                        "banner", banner,
                        "outline", outline,
                        "outlineColour", outlineColour,
                        "crest", crest));

                // Port fix (ANALYSIS.md §9 #10; the harness patches the original the same way): wait until the guild is
                // stored (await new Promise(resolve => saveGuild(data, resolve))), since every guild chat reads it back
                // from the database. The continuation runs one promise job after the save's callback.
                database.SaveGuild(data, () => Node.QueueMicrotask(() =>
                {
                    // Send the join packet to the player.
                    player.Send(new GuildPacket(Opcodes.Guild.Login, new JsObject(
                        "name", name,
                        "owner", player.username,
                        "members", data["members"],
                        "decoration", data["decoration"])));

                    // Assign the guild identifier to the player.
                    player.guild = identifier;

                    // Save the player
                    player.Save();
                }));
            });
        }

        public void Connect(Player player, string identifier) =>
            // Attempt to grab the guild from the database.
            database.LoadGuild(identifier, guild =>
            {
                if (guild == null)
                {
                    // Erase the guild identifier from the player.
                    player.guild = "";
                    return;
                }

                // Remove the player if they are not in the guild.
                if (!Members(guild).Any(member => ((JsObject)member).Str("username") == player.username))
                {
                    player.guild = "";
                    return;
                }

                // Send the join packet to the player.
                player.Send(new GuildPacket(Opcodes.Guild.Login, new JsObject(
                    "name", guild["name"],
                    "owner", guild["owner"],
                    "members", guild["members"],
                    "decoration", guild["decoration"])));

                // Synchronize the connection with all the members in the guild.
                UpdateStatus(player, Members(guild));
            });

        public void Join(Player player, string identifier)
        {
            if (player.isGuest)
            {
                player.Notify("guilds:NOT_ALLOWED_GUESTS_JOIN");
                return;
            }

            if (JsData.Truthy(player.guild))
            {
                player.Notify("guilds:ALREADY_IN_GUILD");
                return;
            }

            // Attempt to grab the guild from the database.
            database.LoadGuild(identifier, guild =>
            {
                if (guild == null)
                {
                    // Attempt to send the player the updated guild list.
                    Get(player, 0, 10);
                    Log.General($"Player {player.username} tried to join a guild that doesn't exist.");
                    return;
                }

                // Ensure the guild isn't full.
                if (Members(guild).Count >= Constants.MAX_GUILD_MEMBERS)
                {
                    player.Notify("guilds:GUILD_FULL");
                    return;
                }

                // Append the player to the guild's member list.
                Members(guild).Add(new JsObject(
                    "username", player.username,
                    "rank", (double)Modules.GuildRank.Fledgling,
                    "joinDate", Node.DateNow()));

                // Save the guild to the database.
                database.SaveGuild(guild, () =>
                {
                    // Set the guild identifier on the player.
                    player.guild = identifier;

                    // Save the player.
                    player.Save();

                    // Connect the player to the guild.
                    Connect(player, identifier);

                    // Send the join packet to the player.
                    player.Send(new GuildPacket(Opcodes.Guild.Login, new JsObject(
                        "name", guild["name"],
                        "owner", guild["owner"],
                        "members", guild["members"],
                        "decoration", guild["decoration"])));

                    // Sync to all the members in the guild.
                    Synchronize(Members(guild), Opcodes.Guild.Join, new JsObject(
                        "username", player.username,
                        "serverId", Config.Current.serverId));
                });
            });
        }

        public void Leave(Player player)
        {
            // No need to do anything if the player doesn't have a guild.
            if (!JsData.Truthy(player.guild))
            {
                Log.Warning($"{player.username} tried to leave a guild that they're not in.");
                return;
            }

            // Grab the guild from the database.
            database.LoadGuild(player.guild, guild =>
            {
                if (guild == null)
                {
                    Log.General($"Player {player.username} tried to leave a guild that doesn't exist.");
                    return;
                }

                // Disband the guild if the player is the owner.
                if (player.username == guild.Str("owner"))
                {
                    var members = Members(guild).Cast<JsObject>().ToList();
                    var index = 0;

                    // Send the leave packet to all the members in the guild (each kick is awaited).
                    void Next()
                    {
                        while (index < members.Count)
                        {
                            var member = members[index++];

                            if (member.Str("username") != player.username)
                            {
                                Kick(player, member.Str("username"), Next);
                                return;
                            }
                        }

                        Synchronize(Members(guild), Opcodes.Guild.Leave);

                        // Disband the guild
                        database.DeleteGuild(player.guild);

                        // Remove the guild identifier from the player.
                        player.guild = "";
                    }

                    Next();
                    return;
                }

                // Remove the player from the guild's member list.
                guild.Set("members", Members(guild).Where(member => ((JsObject)member).Str("username") != player.username).ToList());

                // Save the guild to the database.
                database.SaveGuild(guild);

                // Remove the guild object from the player.
                player.guild = "";

                // Send the leave packet to the player.
                player.Send(new GuildPacket(Opcodes.Guild.Leave));

                // Sync to all the members in the guild.
                Synchronize(Members(guild), Opcodes.Guild.Leave, new JsObject("username", player.username));
            });
        }

        /// <summary>kick(player, username); <paramref name="done"/> is the awaiting caller's continuation.</summary>
        public void Kick(Player player, string username, Action done = null)
        {
            if (!JsData.Truthy(player.guild))
            {
                Log.Warning($"{player.username} tried to kick someone from a guild that they're not in.");
                if (done != null) Node.QueueMicrotask(done);
                return;
            }

            // Grab the guild from the database.
            database.LoadGuild(player.guild, guild =>
            {
                if (guild == null)
                    Log.General($"Player {player.username} tried to kick someone from a guild that doesn't exist.");
                // Ensure the player is the owner of the guild.
                else if (player.username != guild.Str("owner")) player.Notify("guilds:NO_PERMISSION");
                // Ensure the player is not kicking themselves.
                else if (player.username == username) player.Notify("guilds:CANNOT_KICK_YOURSELF");
                else
                {
                    var otherPlayer = world.GetPlayerByName(username);

                    // Remove the guild from the player being kicked if they're online.
                    if (otherPlayer != null) otherPlayer.guild = "";

                    // Sync to all the members in the guild.
                    Synchronize(Members(guild), Opcodes.Guild.Leave, new JsObject("username", username));

                    // Remove the player from the guild's member list.
                    guild.Set("members", Members(guild).Where(member => ((JsObject)member).Str("username") != username).ToList());

                    // Save the guild to the database.
                    database.SaveGuild(guild);
                }

                if (done != null) Node.QueueMicrotask(done);
            });
        }

        public void Chat(Player player, string message)
        {
            if (!JsData.Truthy(player.guild))
            {
                Log.Warning($"{player.username} tried to chat in a guild that they're not in.");
                return;
            }

            database.LoadGuild(player.guild, guild =>
            {
                if (guild == null)
                {
                    Log.General($"Player {player.username} tried to chat in a guild that doesn't exist.");
                    return;
                }

                Synchronize(Members(guild), Opcodes.Guild.Chat, new JsObject(
                    "username", player.username,
                    "serverId", Config.Current.serverId,
                    "message", message));
            });
        }

        public void Promote(Player player, string username) => ChangeRank(player, username, 1);

        public void Demote(Player player, string username) => ChangeRank(player, username, -1);

        void ChangeRank(Player player, string username, double change) =>
            GetGuild(player.guild, guild =>
            {
                if (guild == null)
                {
                    Log.Warning($"{player.username} tried to promote someone in a guild that they're not in.");
                    return;
                }

                // getMember is async: its result arrives one job later.
                var member = GetMember(guild, username);

                Node.QueueMicrotask(() =>
                {
                    if (member?["rank"] == null)
                    {
                        Log.Warning($"{player.username} tried to promote someone in a guild that they're not in.");
                        return;
                    }

                    SetRank(guild, player, member, JsAny.ToNumber(member["rank"]) + change);
                });
            });

        public void AddExperience(Player player, double experience)
        {
            if (!JsData.Truthy(player.guild))
            {
                Log.Warning($"{player.username} tried to add experience to a guild that they're not in.");
                return;
            }

            database.LoadGuild(player.guild, guild =>
            {
                if (guild == null)
                {
                    Log.General($"Player {player.username} tried to add experience to a guild that doesn't exist.");
                    return;
                }

                guild.Set("experience", JsAny.ToNumber(guild["experience"]) + experience);

                database.SaveGuild(guild);

                Synchronize(Members(guild), Opcodes.Guild.Experience, new JsObject("experience", guild["experience"]));
            });
        }

        public void SetRank(JsObject guild, Player player, JsObject member, double rank)
        {
            if (!JsData.Truthy(player.guild))
            {
                Log.Warning($"{player.username} tried to set a rank in a guild that they're not in.");
                return;
            }

            if (guild == null)
            {
                Log.General($"Player {player.username} tried to set a rank in a guild that doesn't exist.");
                return;
            }

            var username = member.Str("username");

            if (player.username == username)
            {
                Log.Warning($"{player.username} tried to set their own rank.");
                return;
            }

            if (member["rank"] == null)
            {
                Log.Warning($"Player {username} is not in the guild.");
                return;
            }

            if (rank < (double)Modules.GuildRank.Fledgling || rank > (double)Modules.GuildRank.Landlord)
            {
                Log.Warning($"Player {username} tried to set an invalid rank.");
                return;
            }

            var playerMember = GetMember(guild, player.username);

            Node.QueueMicrotask(() =>
            {
                if (playerMember?["rank"] == null)
                {
                    Log.Warning($"{player.username} tried to set a rank in a guild that they're not in.");
                    return;
                }

                // Ensure the player has the correct permissions to update the rank.
                if (JsAny.ToNumber(playerMember["rank"]) - rank < 1)
                {
                    player.Notify("guilds:NO_PERMISSION_RANK");
                    return;
                }

                member.Set("rank", rank);

                database.SaveGuild(guild);

                Synchronize(Members(guild), Opcodes.Guild.Rank, new JsObject("username", username, "rank", rank));
            });
        }

        internal void GetGuild(string identifier, Action<JsObject> callback) =>
            database.LoadGuild(identifier, guild =>
            {
                if (guild == null) Log.Warning($"Guild {identifier} does not exist.");

                // The caller awaits getGuild (an async function): one more job.
                Node.QueueMicrotask(() => callback(guild));
            });

        internal static JsObject GetMember(JsObject guild, string username)
        {
            var member = Members(guild).Cast<JsObject>().FirstOrDefault(m => m.Str("username") == username);

            if (member == null)
            {
                Log.Warning($"Player {username} is not in the guild.");
                return null;
            }

            return member;
        }

        void Synchronize(List<object> members, Opcodes.Guild opcode, JsObject data = null)
        {
            foreach (JsObject member in members)
            {
                var player = world.GetPlayerByName(member.Str("username"));
                var packet = new GuildPacket(opcode, data);

                // Attempt to relay the packet across servers.
                if (player == null)
                {
                    world.client.Relay(member.Str("username"), packet);
                    continue;
                }

                player.Send(packet);
            }
        }

        void UpdateStatus(Player player, List<object> members)
        {
            // Update the online players in the world and then request hub to check for online players.
            var offlineMembers = new List<object>();
            var onlineMembers = new List<object>();

            // Iterate through the guild's members and check if they are online.
            foreach (JsObject member in members)
                if (world.IsOnline(member.Str("username")))
                    onlineMembers.Add(new JsObject("username", member.Str("username"), "serverId", Config.Current.serverId));
                else offlineMembers.Add(member.Str("username"));

            // Send the online members to the player's client.
            player.Send(new GuildPacket(Opcodes.Guild.Update, new JsObject("members", onlineMembers)));

            // Send the offline members to the hub to check against the online players.
            world.client.Send(new GuildPacket(Opcodes.Guild.Update, new JsObject(
                "username", player.username,
                "usernames", offlineMembers)));
        }

        public void Get(Player player, double from = 0, double to = 50) =>
            database.LoadGuilds(from, to, (info, total) =>
            {
                // Filter guild data that are full and/or invite only.
                info = info.Where(guild => Members(guild).Count < Constants.MAX_GUILD_MEMBERS && !JsData.Truthy(guild["inviteOnly"]))
                    .ToList();

                // Extract the cruical information and store it in a ListInfo array.
                var guilds = info.ConvertAll(guild => (object)new JsObject(
                    "name", guild["name"],
                    "members", (double)Members(guild).Count,
                    "decoration", guild["decoration"],
                    "inviteOnly", guild["inviteOnly"]));

                player.Send(new GuildPacket(Opcodes.Guild.List, new JsObject("guilds", guilds, "total", total)));
            });
    }
}
