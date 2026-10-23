using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>common/api/minigame.ts Team.</summary>
    public enum Team
    {
        Red,
        Blue,
        Prey,
        Hunter
    }

    /// <summary>game/minigames/minigame.ts.</summary>
    public class Minigame
    {
        // The name for the minigame (used for scoreboard, entering, etc.)
        public string name = "";

        // State of the minigame.
        protected bool started;

        // Countdown for the lobby/in-game used for certain minigames.
        protected double countdown = 300_000; // 4 minutes.

        // The lobby area for the minigame.
        protected Area lobby = new(0, 0, 0, 0, 0);

        // Used to keep track of players in the minigame.
        protected List<Player> playersInLobby = new();
        protected List<Player> playersInGame = new();

        // Tick interval for the minigame.
        readonly double tickInterval = 1000; // Every 1 second.

        protected World world;
        readonly Opcodes.Minigame type;

        public Minigame(World world, Opcodes.Minigame type)
        {
            this.world = world;
            this.type = type;

            // Begin the tick interval for the minigame.
            Node.SetInterval(Tick, tickInterval);
        }

        public virtual void LoadArea(Area area)
        {
            // Specifically handle the lobby area.
            if (area.mObjectType != "lobby") return;

            lobby = area;

            // Handle the entry and exit of players into the lobby.
            area.OnEnter(AddPlayer);
            area.OnExit(RemovePlayer);
        }

        protected virtual void Tick() { }

        public void Disconnect(Player player)
        {
            var lobbyPosition = GetLobbyPosition();

            // Push the player outside the game area.
            player.SetPosition(lobbyPosition.x, lobbyPosition.y, false, true);

            // Remove the player from the game they're in.
            Splice(playersInGame, playersInGame.IndexOf(player));

            // Stop when there's only one player left.
            if (playersInGame.Count < 2) Stop();
        }

        /// <summary>array.splice(index, 1): index -1 removes the last element.</summary>
        protected static void Splice(List<Player> list, int index)
        {
            if (index < 0) index += list.Count;
            if (index >= 0 && index < list.Count) list.RemoveAt(index);
        }

        public virtual void Kill(Player player) { }

        public virtual void Stop()
        {
            // Signal to the players in game that the minigame has ended.
            SendPacket(playersInGame, new JsObject("action", (double)Opcodes.MinigameActions.End));

            started = false;

            foreach (var player in playersInGame)
            {
                player.ClearMinigame();

                var position = GetLobbyPosition();

                player.Teleport(position.x, position.y, false, true);
            }

            // Clear the players from the game array.
            playersInGame = new List<Player>();
        }

        void AddPlayer(Player player)
        {
            playersInLobby.Add(player);

            SendPacket(new List<Player> { player }, new JsObject("action", (double)Opcodes.MinigameState.Lobby));

            // Notify the player of the minigame.
            player.Notify($"misc:ENTERED_LOBBY;name={name}");
        }

        void RemovePlayer(Player player)
        {
            Splice(playersInLobby, playersInLobby.IndexOf(player));

            SendPacket(new List<Player> { player }, new JsObject("action", (double)Opcodes.MinigameActions.Exit));

            // Notify the player of the minigame.
            player.Notify($"misc:EXITED_LOBBY;name={name}");
        }

        protected void SendPacket(List<Player> players, JsObject info) =>
            world.Push(Modules.PacketType.Players, new PacketData
            {
                players = players,
                packet = new MinigamePacket(type, info)
            });

        protected List<Player> ShuffleLobby()
        {
            var lobby = playersInLobby;

            for (var x = lobby.Count - 1; x > 0; x--)
            {
                var y = (int)Math.Floor(Node.Random() * x);
                (lobby[x], lobby[y]) = (lobby[y], lobby[x]);
            }

            return lobby;
        }

        public Position GetLobbyPosition() => new(
            Utils.RandomInt(lobby.x + 2, lobby.x + lobby.width - 3),
            Utils.RandomInt(lobby.y + 2, lobby.y + lobby.height - 3));

        public virtual Position GetSpawnPoint(Team? team) => GetLobbyPosition();

        protected void ForEachPlayerInGame(Action<Player> callback)
        {
            // No players in game or the game hasn't started, used to prevent unnecessary overhead.
            if (!started || playersInGame.Count == 0) return;

            foreach (var player in playersInGame) callback(player);
        }
    }

    /// <summary>game/minigames/impl/teamwar.ts.</summary>
    public sealed class TeamWar : Minigame
    {
        // Areas for the minigame.
        Area redSpawn = new(0, 0, 0, 0, 0); // Spawn area for red team.
        Area blueSpawn = new(0, 0, 0, 0, 0); // Spawn area for blue team.

        double redTeamKills;
        double blueTeamKills;

        public TeamWar(World world) : base(world, Opcodes.Minigame.TeamWar)
        {
            name = "TeamWar";
            countdown = (double)Modules.MinigameConstants.TEAM_WAR_COUNTDOWN;
        }

        public override void LoadArea(Area area)
        {
            base.LoadArea(area);

            switch (area.mObjectType)
            {
                case "redteamspawn":
                    redSpawn = area;
                    return;

                case "blueteamspawn":
                    blueSpawn = area;
                    return;
            }
        }

        public override void Kill(Player player)
        {
            if (player.team == Team.Blue) blueTeamKills++;
            else if (player.team == Team.Red) redTeamKills++;
        }

        public override Position GetSpawnPoint(Team? team)
        {
            if (!started) return base.GetSpawnPoint(null);

            var area = team == Team.Red ? redSpawn : blueSpawn;

            return new Position(
                Utils.RandomInt(area.x + 1, area.x + area.width - 1),
                Utils.RandomInt(area.y + 1, area.y + area.height - 1));
        }

        protected override void Tick()
        {
            if (countdown <= 0)
            {
                countdown = (double)Modules.MinigameConstants.TEAM_WAR_COUNTDOWN;

                // Attempt to start if not started, otherwise end the game.
                if (started) Stop();
                else Start();

                return;
            }

            countdown--;

            // Send the score packet to the players in-game.
            if (playersInGame.Count > 0)
                SendPacket(playersInGame, new JsObject(
                    "action", (double)Opcodes.MinigameActions.Score,
                    "countdown", countdown,
                    "redTeamKills", redTeamKills,
                    "blueTeamKills", blueTeamKills));

            // Send the countdown packet to the players in the lobby.
            if (playersInLobby.Count > 0)
                SendPacket(playersInLobby, new JsObject(
                    "action", (double)Opcodes.MinigameActions.Lobby,
                    "countdown", countdown,
                    "started", started));
        }

        void Start()
        {
            var redTeam = ShuffleLobby();

            // Not enough players, we're not starting the game.
            if (redTeam.Count < (int)Modules.MinigameConstants.TEAM_WAR_MIN_PLAYERS)
            {
                // Notify all players there aren't enough players in the lobby.
                foreach (var player in redTeam)
                    player.Notify($"misc:MINIMUM_PLAYERS_MINIGAME;minimum={(int)Modules.MinigameConstants.TEAM_WAR_MIN_PLAYERS}");

                return;
            }

            started = true;

            // redTeam.splice(0, redTeam.length / 2): a fractional count is truncated.
            var blueCount = (int)Math.Truncate(redTeam.Count / 2.0);
            var blueTeam = redTeam.GetRange(0, blueCount);
            redTeam.RemoveRange(0, blueCount);

            // Assign each player in each team their respective team.
            foreach (var player in redTeam) player.team = Team.Red;
            foreach (var player in blueTeam) player.team = Team.Blue;

            // Concatenate all the players into one array for later.
            playersInGame = redTeam.Concat(blueTeam).ToList();

            // Teleport every player to the lobby.
            foreach (var player in playersInGame)
            {
                player.minigame = Opcodes.Minigame.TeamWar;

                // Grab the spawn point for the player's team.
                var position = GetSpawnPoint(player.team);

                player.Teleport(position.x, position.y, false, true);
            }
        }

        public override void Stop()
        {
            base.Stop();

            redTeamKills = 0;
            blueTeamKills = 0;
        }
    }

    /// <summary>game/minigames/impl/coursing.ts.</summary>
    public sealed class Coursing : Minigame
    {
        Area hunterSpawn = new(0, 0, 0, 0, 0);
        readonly List<Area> preySpawns = new();

        // Used to calculate distance of the prey from the centre.
        readonly Position centre = new(0, 0);

        public Coursing(World world) : base(world, Opcodes.Minigame.Coursing)
        {
            name = "Coursing";
            countdown = (double)Modules.MinigameConstants.COURSING_COUNTDOWN;
        }

        public override void LoadArea(Area area)
        {
            base.LoadArea(area);

            switch (area.mObjectType)
            {
                case "hunterspawn":
                    hunterSpawn = area;
                    return;

                case "preyspawn":
                    // Add the area to the list of prey spawns.
                    preySpawns.Add(area);
                    return;

                case "centre":
                    centre.x = area.x;
                    centre.y = area.y;
                    return;
            }
        }

        protected override void Tick()
        {
            if (countdown <= 0)
            {
                countdown = (double)Modules.MinigameConstants.COURSING_COUNTDOWN;

                // Attempt to start if not started, otherwise end the game.
                if (started) Stop();
                else Start();

                return;
            }

            countdown--;

            // Send the countdown packet to the players in the lobby (if any).
            if (playersInLobby.Count > 0)
                SendPacket(playersInLobby, new JsObject(
                    "action", (double)Opcodes.MinigameActions.Lobby,
                    "countdown", countdown,
                    "started", started));

            // We only update the score and send the packet every 4 ticks.
            if (countdown % 4 != 0) return;

            ForEachPlayerInGame(player =>
            {
                if (player.team == Team.Prey)
                {
                    // Dead players get a decrement in score for every tick.
                    if (player.dead)
                    {
                        player.IncrementCoursingScore(-10);
                        return;
                    }

                    // Grab the distance from the centre of the map to the player.
                    var distance = Utils.GetDistance(player.x, player.y, centre.x, centre.y);
                    var score = Math.Floor(distance / (double)Modules.MinigameConstants.COURSING_SCORE_DIVISOR);

                    // Update the player's score.
                    player.IncrementCoursingScore(score);

                    // Send a packet updating the player's score.
                    SendPacket(new List<Player> { player }, new JsObject(
                        "action", (double)Opcodes.MinigameActions.Score,
                        "score", player.coursingScore));
                }
            });
        }

        /// <summary>getSpawnPoint(isPrey = false): the argument is a team number or a boolean, used for truthiness.</summary>
        public override Position GetSpawnPoint(Team? team) => GetSpawnPoint(team is { } t && t != Team.Red);

        Position GetSpawnPoint(bool isPrey)
        {
            if (!started) return base.GetSpawnPoint(null);

            var area = isPrey ? preySpawns.ElementAtOrDefault((int)Utils.RandomInt(0, preySpawns.Count - 1)) : hunterSpawn;

            if (area == null) throw new JsTypeError("Cannot read properties of undefined (reading 'x')");

            return new Position(
                Utils.RandomInt(area.x + 1, area.x + area.width - 1),
                Utils.RandomInt(area.y + 1, area.y + area.height - 1));
        }

        void Start()
        {
            playersInLobby = ShuffleLobby();

            // Not enough players to start.
            if (playersInLobby.Count < (int)Modules.MinigameConstants.COURSING_MIN_PLAYERS)
            {
                // Notify the players that we need more players.
                foreach (var player in playersInLobby)
                    player.Notify($"misc:MINIMUM_PLAYERS_MINIGAME;minimum={(int)Modules.MinigameConstants.COURSING_MIN_PLAYERS}");

                return;
            }

            // Mark the game as started.
            started = true;

            // The length of players is always even, so we check that here.
            var count = playersInLobby.Count % 2 == 0 ? playersInLobby.Count : playersInLobby.Count - 1;
            var hunters = playersInLobby.GetRange(0, count / 2);
            playersInLobby.RemoveRange(0, count / 2);
            var prey = playersInLobby.GetRange(0, count / 2);
            playersInLobby.RemoveRange(0, count / 2);

            for (var i = 0; i < hunters.Count; i++)
            {
                var hunter = hunters[i];

                hunter.team = Team.Hunter;
                prey[i].team = Team.Prey;

                hunter.coursingTarget = prey[i].instance;
                prey[i].coursingTarget = hunter.instance;
            }

            // Concatenate the two arrays into the playersInGame array.
            playersInGame = hunters.Concat(prey).ToList();

            // Teleport all the players to their spawn points.
            foreach (var player in playersInGame)
            {
                player.minigame = Opcodes.Minigame.Coursing;
                player.coursingScore = 0;

                // Grab the spawn point for the player based on their team.
                var position = GetSpawnPoint(player.team == Team.Prey);

                player.Teleport(position.x, position.y, false, true);
            }

            Node.SetTimeout(SendPointers, 1500);
        }

        void SendPointers()
        {
            foreach (var player in playersInGame)
                player.Pointer(new JsObject("type", (double)Opcodes.Pointer.Entity, "instance", player.coursingTarget));
        }
    }

    /// <summary>game/minigames/minigames.ts.</summary>
    public sealed class Minigames
    {
        readonly JsObject<Minigame> minigames = new();
        readonly Areas areas;

        public Minigames(World world)
        {
            areas = world.map.GetMinigameAreas();

            // impl/index.ts: teamwar, coursing.
            minigames.Set("teamwar", new TeamWar(world));
            minigames.Set("coursing", new Coursing(world));

            Log.Info($"Finished loading {minigames.Count} minigame{(minigames.Count > 1 ? "s" : "")}.");

            LinkAreas();
        }

        void LinkAreas()
        {
            if (areas == null) return;

            areas.ForEachArea(area =>
            {
                if (area.minigame == null || !minigames.Has(area.minigame)) return;

                minigames[area.minigame].LoadArea(area);
            });
        }

        public Minigame Get(Opcodes.Minigame? opcode) => opcode == Opcodes.Minigame.TeamWar ? minigames["teamwar"] : null;
    }
}
