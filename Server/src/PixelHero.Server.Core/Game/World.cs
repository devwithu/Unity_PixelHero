using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Controllers;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    // Discord.Net's root namespace is also called Discord.
    using Discord = PixelHero.Server.Network.Discord;

    /// <summary>world.ts PacketData.</summary>
    public sealed class PacketData
    {
        public Packet packet;
        public Player player;
        public List<Player> players;
        public string ignore;
        public int region;
        public List<int> list;
    }

    /// <summary>game/world.ts.</summary>
    public sealed class World
    {
        public Map map;
        public Api api;
        public Stores stores;
        public Warps warps;
        public Globals globals;
        public Entities entities;
        public GameNetwork network;
        public Minigames minigames;
        public Enchanter enchanter;
        public Crafting crafting;
        public Guilds guilds;
        public HubClient client;
        public Events events;

        public Discord discord;

        readonly double maxPlayers;

        public bool allowConnections = true;

        public Action<Connection> connectionCallback;

        public SocketHandler socketHandler;
        public IGameDatabase database;

        public World(SocketHandler socketHandler, IGameDatabase database)
        {
            // Parameter properties come after the field initializers (esbuild), which construct these first.
            enchanter = new Enchanter();
            crafting = new Crafting();
            discord = new Discord(Config.Current.hubEnabled);
            maxPlayers = Config.Current.maxPlayers;

            this.socketHandler = socketHandler;
            this.database = database;

            map = new Map(this);
            api = new Api(this);
            stores = new Stores(this);
            warps = new Warps(this);
            globals = new Globals(this);
            entities = new Entities(this);
            network = new GameNetwork(this);
            minigames = new Minigames(this);
            guilds = new Guilds(this);
            client = new HubClient(this);
            events = new Events(this);

            discord.OnMessage((source, message, colour) => GlobalMessage(source, message, colour));

            OnConnection(network.HandleConnection);

            Log.Info("******************************************");

            Tick();
        }

        void Tick()
        {
            Node.SetInterval(() =>
            {
                network.Parse();
                map.regions.Parse();
            }, Config.Current.updateTime);

            Node.SetInterval(Save, Config.Current.saveInterval);
        }

        public void Push(Modules.PacketType packetType, PacketData data)
        {
            switch (packetType)
            {
                case Modules.PacketType.Broadcast:
                    network.Broadcast(data.packet);
                    return;

                case Modules.PacketType.Player:
                    network.Send(data.player, data.packet);
                    return;

                case Modules.PacketType.Players:
                    network.SendToPlayers(data.players, data.packet);
                    return;

                case Modules.PacketType.Region:
                    network.SendToRegion(data.region, data.packet, data.ignore);
                    return;

                case Modules.PacketType.Regions:
                    network.SendToSurroundingRegions(data.region, data.packet, data.ignore);
                    return;

                case Modules.PacketType.RegionList:
                    network.SendToRegionList(data.list, data.packet, data.ignore);
                    return;
            }
        }

        public void GlobalMessage(string source, string message, string colour = "", bool noPrefix = false) =>
            Push(Modules.PacketType.Broadcast, new PacketData
            {
                packet = new ChatPacket(new JsObject(
                    "source", noPrefix ? source : $"[Global]: {source}",
                    "message", message,
                    "colour", colour))
            });

        public void CleanCombat(Character cleanCharacter) =>
            entities.ForEachCharacter(character =>
            {
                if (character.HasAttacker(cleanCharacter)) character.RemoveAttacker(cleanCharacter);

                if (!character.HasTarget()) return;

                if (character.target?.instance != cleanCharacter.instance) return;

                character.ClearTarget();
            });

        public void SyncFriendsList(string username, bool logout = false, double? serverId = null)
        {
            var id = serverId ?? Config.Current.serverId;

            entities.ForEachPlayer(player =>
            {
                if (player.friends.HasFriend(username)) player.friends.SetStatus(username, !logout, id);
            });
        }

        /// <summary>async syncGuildMembers: returns at once without a guild, otherwise continues after loadGuild.</summary>
        public void SyncGuildMembers(string identifier, string username, bool logout = false, double? serverId = null)
        {
            var id = serverId ?? Config.Current.serverId;

            if (!JsData.Truthy(identifier)) return;

            database.LoadGuild(identifier, guild =>
            {
                if (guild == null) return;

                // Iterate through the members in the guild.
                foreach (JsObject member in guild.Arr("members"))
                {
                    // Skip if the member is the player we are updating.
                    if (member.Str("username") == username) continue;

                    var player = GetPlayerByName(member.Str("username"));

                    // Skip if player doesn't exist.
                    if (player == null) continue;

                    // If the player is online, send a packet with a new status.
                    player.Send(new GuildPacket(Opcodes.Guild.Update, new JsObject(
                        "members", new List<object> { new JsObject("username", username, "serverId", logout ? -1.0 : id) })));
                }
            });
        }

        public void Save()
        {
            entities.ForEachPlayer(player => player.Save());

            Log.Debug($"{Config.Current.name} {JsString.Of(Config.Current.serverId)} has successfully saved.");
        }

        public bool IsOnline(string username) => GetPlayerByName(username) != null;

        public bool IsFull() => GetPopulation() >= maxPlayers;

        public Player GetPlayerByName(string username) => entities.GetPlayer(username);

        public Grids GetGrids() => map.grids;

        public double GetPopulation() => entities.players.Count;

        public double GetDropProbability()
        {
            if (!events.IsDoubleDrop()) return Constants.DROP_PROBABILITY;

            return events.doubleDropProbability;
        }

        public double GetExperiencePerHit()
        {
            if (!events.IsIncreasedExperience()) return Constants.EXPERIENCE_PER_HIT;

            return events.experiencePerHit;
        }

        public void OnConnection(Action<Connection> callback) => connectionCallback = callback;
    }
}
