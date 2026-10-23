using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;

namespace PixelHero.Entities
{
    /// <summary>
    /// What the client's <c>game</c> object provides to entities (client/src/game.ts): clocks, timers, map, sprites,
    /// the entity table, the main player, path finding, teleport and the outgoing socket. Hooks stand in for the
    /// subsystems of later stages (camera, info splats, sounds, bubbles).
    /// </summary>
    public sealed class World
    {
        public readonly IGameClock Clock;
        public readonly JsTimers Timers;
        public readonly ClientMap Map;
        public readonly SpritesController Sprites;
        public readonly EntitiesController Entities;
        public readonly PathFinder PathFinder = new PathFinder();
        public Player Player;

        /// <summary>game.time: Date.now() taken at the start of the frame.</summary>
        public double Time;
        public bool Pvp;

        public int TileSize => Map.Info.TileSize;

        /// <summary>Outgoing packets (socket.send).</summary>
        public Action<Packets, object> Send = (_, __) => { };

        /// <summary>The renderer's camera (null until the view sets one).</summary>
        public Camera Camera;

        public readonly InputController Input;

        public readonly InfoController Info;

        /// <summary>overlays.ts and the lights of renderer.ts.</summary>
        public readonly Lights Lights;

        /// <summary>controllers/pointer.ts.</summary>
        public readonly Pointers Pointers;

        /// <summary>game.minigame.</summary>
        public readonly Minigame Minigame = new Minigame();

        /// <summary>renderer.debugging (the Command packet's 'debug'); the debug drawing itself is not ported.</summary>
        public bool Debugging;

        /// <summary>Math.random() (tests replace it with the harness's seeded generator).</summary>
        public Func<double> Random = () => UnityEngine.Random.value;

        /// <summary>client utils/util.ts randomInt: <c>min + ~~(Math.random() * (max - min + 1))</c>.</summary>
        public int RandomInt(int min, int max) => min + JsValue.ToInt32(Random() * (max - min + 1));

        /// <summary>game.timeOffset: <c>~~performance.now() - serverTime</c> from the Handshake.</summary>
        public double TimeOffset;

        /// <summary>The client's performance.now() (milliseconds since the page started).</summary>
        public Func<double> PerformanceNow;

        /// <summary>Sent in Ready (game.ts: navigator.userAgent, lowercased).</summary>
        public string UserAgent = "replay";

        /// <summary>map.regionsLoaded, sent in Ready.</summary>
        public int RegionsLoaded;

        /// <summary>camera.centreOn(player).</summary>
        public Action<Player> CentreCamera;

        /// <summary>The main player's death animation has ended (connection.ts handleDeath: body.classList.add('death')).</summary>
        public Action DeathShown;

        /// <summary>controllers/bubble.ts clear (one entity's bubble) and clean (all bubbles).</summary>
        public Action<string> ClearBubble = _ => { };
        public Action CleanBubbles = () => { };

        /// <summary>controllers/audio.ts playSound (target null = not spatial), playMusic and stopMusic.</summary>
        public Action<string, Entity> PlaySound = (_, __) => { };
        public Action<string> PlayMusic = _ => { };
        public Action StopMusic = () => { };

        /// <summary>audio.ts playHitSound, playKillSound, playMiningSound, playChoppingSound: a random variant each
        /// (wood5 is never picked: randomInt(1, 4)).</summary>
        public void PlayHitSound(Entity target) => PlaySound($"hit{RandomInt(1, 2)}", target);
        public void PlayKillSound(Entity target) => PlaySound($"kill{RandomInt(1, 2)}", target);
        public void PlayMiningSound(Entity target) => PlaySound($"mining{RandomInt(1, 3)}", target);
        public void PlayChoppingSound(Entity target) => PlaySound($"wood{RandomInt(1, 4)}", target);


        public World(IGameClock clock, ClientMap map, IEnumerable<SpriteInfo> sprites, Func<string, bool> imageExists)
        {
            Clock = clock;
            Timers = new JsTimers(clock);
            Map = map;
            Time = clock.Now;
            Sprites = new SpritesController(sprites, Timers, imageExists);
            Entities = new EntitiesController(this);
            Input = new InputController(this);
            Info = new InfoController(this);
            Lights = new Lights(this);
            Pointers = new Pointers(this);
            Player = new Player(this, "");
            CentreCamera = player => Camera?.CentreOn(player);
            var start = clock.Now;
            PerformanceNow = () => Clock.Now - start;
        }

        /// <summary>game.ts getEntityAt: the first entity drawn on the tile that is not the main player.</summary>
        public Entity GetEntityAt(int x, int y)
        {
            foreach (var entity in Entities.Grid.At(x, y))
                if (entity.Instance != Player.Instance) return entity;
            return null;
        }

        /// <summary>
        /// game.ts searchForEntityAt: the entity whose bounding-box centre is closest to the pixel position, within
        /// half its smaller sprite side, among grids.getEntitiesAround (rows and columns from -radius to radius - 1).
        /// </summary>
        public Entity SearchForEntityAt(WorldPosition position, int radius = 3)
        {
            var entities = new List<Entity>();
            for (var i = position.GridY - radius; i < position.GridY + radius; i++)
            for (var j = position.GridX - radius; j < position.GridX + radius; j++)
                if (!Map.IsOutOfBounds(j, i)) entities.AddRange(Entities.Grid.At(j, i));

            Entity closest = null;
            foreach (var entity in entities)
            {
                if (entity.IsProjectile || entity.IsPet || IsMainPlayer(entity.Instance)) continue;
                if (entity is Resource resource && resource.Exhausted) continue;

                var (bx, by, bw, bh) = entity.GetBoundingBox();
                var centreX = bx + bw / 2;
                var centreY = by + bh / 2;
                var dx = position.X - centreX;
                var dy = position.Y - centreY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var threshold = (entity.Sprite.Width < entity.Sprite.Height ? entity.Sprite.Width : entity.Sprite.Height) / 2.0;

                if (distance > threshold) continue;

                if (closest == null || distance < closest.Distance)
                {
                    closest = entity;
                    closest.Distance = distance;
                }
            }

            return closest;
        }

        /// <summary>client utils/util.ts getRegion.</summary>
        public int GetRegion(int x, int y) =>
            (int)Math.Floor(x / (double)Constants.MAP_DIVISION_SIZE) +
            (int)Math.Floor(y / (double)Constants.MAP_DIVISION_SIZE) * (Map.Width / Constants.MAP_DIVISION_SIZE);

        public bool IsMainPlayer(string instance) => Player.Instance == instance;

        /// <summary>game.ts findPath.</summary>
        public List<int[]> FindPath(Character character, int x, int y, List<PathFinder.TileIgnore> ignores = null, string cursor = "")
        {
            var path = PathFinder.Find(Map, character.GridX, character.GridY, x, y, ignores);
            if (path.Count == 0) return path;

            if (cursor == "fishing")
            {
                var last = path[path.Count - 2];
                if (Map.IsColliding(last[0], last[1])) path.RemoveAt(path.Count - 1);
            }

            return path;
        }

        /// <summary>game.ts findAdjacentTile: step a character off its target's tile (right, left, down, up).</summary>
        public void FindAdjacentTile(Character character)
        {
            if (!Map.IsColliding(character.GridX + 1, character.GridY)) character.Go(character.GridX + 1, character.GridY);
            else if (!Map.IsColliding(character.GridX - 1, character.GridY)) character.Go(character.GridX - 1, character.GridY);
            else if (!Map.IsColliding(character.GridX, character.GridY + 1)) character.Go(character.GridX, character.GridY + 1);
            else if (!Map.IsColliding(character.GridX, character.GridY - 1)) character.Go(character.GridX, character.GridY - 1);
        }

        /// <summary>game.ts teleport.</summary>
        public void Teleport(Character character, int gridX, int gridY)
        {
            Entities.UnregisterPosition(character);
            character.SetGridPosition(gridX, gridY);
            Entities.RegisterPosition(character);

            character.Frozen = false;
            character.Teleporting = false;

            if (character.Instance == Player.Instance)
            {
                character.ClearHealthBar();
                Player.Moving = false;
                Player.DisableAction = false;
                CentreCamera(Player);
            }
        }
    }

    /// <summary>
    /// The entity part of client/src/network/connection.ts (Handshake instance, Welcome + game.postLoad's entity
    /// steps, Spawn, List, Movement, Teleport, Despawn, Animation, Points, Blink, Update, Sync, Equipment) and of
    /// renderer/updater.ts (updateEntities). Handlers keep the original's quirks: List.Positions stops at the first
    /// missing or moving entity; Points ignores zero values.
    /// </summary>
    public sealed class EntityPackets
    {
        readonly World _world;
        double _lastEntityListRequest;

        public EntityPackets(World world)
        {
            _world = world;
            _lastEntityListRequest = world.Clock.Now;
            world.Player.SyncCallback = () => world.Player.SetSprite(world.Sprites.Get(world.Player.GetSpriteName()));
        }

        EntitiesController Entities => _world.Entities;

        /// <summary>The packets <see cref="Handle"/> understands.</summary>
        public static readonly Packets[] Handled =
        {
            Packets.Handshake, Packets.Welcome, Packets.Spawn, Packets.List, Packets.Equipment, Packets.Sync, Packets.Movement,
            Packets.Teleport, Packets.Despawn, Packets.Animation, Packets.Points, Packets.Blink, Packets.Update,
            Packets.Combat, Packets.Death, Packets.Respawn, Packets.Heal, Packets.Poison, Packets.Effect, Packets.Experience, Packets.Skill, Packets.Quest, Packets.Achievement, Packets.Friends, Packets.Guild,
            Packets.Rank, Packets.Countdown, Packets.Overlay, Packets.Pointer, Packets.PVP, Packets.Command, Packets.Camera,
            Packets.Resource, Packets.Minigame, Packets.Ability
        };

        public bool Handle(Packets id, IReadOnlyList<object> args)
        {
            object Arg(int i) => i < args.Count ? args[i] : null;
            Dictionary<string, object> Data(int i) => Arg(i) as Dictionary<string, object>;
            int Opcode() => (int)(double)Arg(0);

            switch (id)
            {
                case Packets.Handshake:
                    _world.Player.Instance = (string)Data(0)["instance"];
                    _world.Player.ServerId = (int)(double)Data(0)["serverId"];
                    _world.TimeOffset = JsValue.ToInt32(_world.PerformanceNow()) - (double)Data(0)["serverTime"];
                    return true;
                case Packets.Welcome: HandleWelcome(Data(0)); return true;
                case Packets.Spawn: Entities.Create(Data(0)); return true;
                case Packets.List: HandleEntityList(Opcode(), Data(1)); return true;
                case Packets.Equipment: HandleEquipment(Opcode(), Data(1)); return true;
                case Packets.Sync: HandleSync(Data(0)); return true;
                case Packets.Movement: HandleMovement(Opcode(), Data(1)); return true;
                case Packets.Teleport: HandleTeleport(Data(0)); return true;
                case Packets.Despawn: HandleDespawn(Data(0)); return true;
                case Packets.Animation: HandleAnimation(Data(0)); return true;
                case Packets.Points: HandlePoints(Data(0)); return true;
                case Packets.Blink: (Entities.Get(Arg(0) as string) as Item)?.Blink(); return true;
                case Packets.Update: HandleUpdate((List<object>)Arg(0)); return true;
                case Packets.Combat: HandleCombat(Opcode(), Data(1)); return true;
                case Packets.Death: HandleDeath(); return true;
                case Packets.Respawn: HandleRespawn(Data(0)); return true;
                case Packets.Heal: HandleHeal(Data(0)); return true;
                case Packets.Poison: _world.Player.Poison = !(Arg(0) is double poisonType && poisonType == -1); return true;
                case Packets.Effect: HandleEffect(Opcode(), Data(1)); return true;
                case Packets.Overlay: _world.Lights.HandleOverlay((Opcodes.Overlay)Opcode(), Data(1)); return true;
                case Packets.Pointer: _world.Pointers.Handle((Opcodes.Pointer)Opcode(), Data(1)); return true;
                case Packets.PVP:
                    // connection.ts handlePVP: the flag, and the skins (not drawn in PVP).
                    _world.Pvp = JsTruthy(Data(0)?.TryGetValue("state", out var pvpState) == true ? pvpState : null);
                    _world.Player.UpdateEquipmentAppearance();
                    return true;
                case Packets.Command: HandleCommand(Data(0)); return true;
                // connection.ts handleCamera does nothing.
                case Packets.Camera: return true;
                case Packets.Resource: HandleResource(Data(0)); return true;
                case Packets.Minigame: HandleMinigame(Opcode(), Data(1)); return true;
                case Packets.Ability: HandleAbility((Opcodes.Ability)Opcode(), Data(1)); return true;
                case Packets.Experience: HandleExperience(Opcode(), Data(1)); return true;
                case Packets.Quest:
                    // connection.ts handleQuest (the Start dialog is UI).
                    if (Opcode() == (int)Opcodes.Quest.Batch) _world.Player.LoadQuests((List<object>)Data(1)["quests"]);
                    else if (Opcode() == (int)Opcodes.Quest.Progress)
                        _world.Player.SetQuest(Str(Data(1), "key"), Num(Data(1), "stage"), Num(Data(1), "subStage"));
                    return true;
                case Packets.Achievement:
                    // connection.ts handleAchievement
                    if (Opcode() == (int)Opcodes.Achievement.Batch) _world.Player.LoadAchievements((List<object>)Data(1)["achievements"]);
                    else if (Opcode() == (int)Opcodes.Achievement.Progress)
                        _world.Player.SetAchievement(Str(Data(1), "key"), Num(Data(1), "stage"), Str(Data(1), "name"), Str(Data(1), "description"));
                    return true;
                case Packets.Guild:
                    // connection.ts handleGuild (the menu handles Leave).
                    switch ((Opcodes.Guild)Opcode())
                    {
                        case Opcodes.Guild.Login:
                            _world.Player.SetGuild(Data(1));
                            break;
                        case Opcodes.Guild.Join:
                            _world.Player.AddGuildMember(Str(Data(1), "username"), Num(Data(1), "serverId"));
                            break;
                        case Opcodes.Guild.Rank:
                            _world.Player.SetGuildMember(Str(Data(1), "username"), Num(Data(1), "rank"));
                            break;
                    }
                    return true;
                case Packets.Friends:
                    // connection.ts handleFriends (the menu removes friends itself).
                    switch ((Opcodes.Friends)Opcode())
                    {
                        case Opcodes.Friends.List:
                            _world.Player.LoadFriends((Data(1) != null && Data(1).TryGetValue("list", out var list) ? list as Dictionary<string, object> : null) ?? new Dictionary<string, object>());
                            break;
                        case Opcodes.Friends.Add:
                            _world.Player.AddFriend(Str(Data(1), "username"), Data(1) != null && Data(1).TryGetValue("status", out var addedStatus) && addedStatus is bool added && added, Num(Data(1), "serverId"));
                            break;
                        case Opcodes.Friends.Status:
                            _world.Player.SetFriendStatus(Str(Data(1), "username"), Data(1) != null && Data(1).TryGetValue("status", out var onlineStatus) && onlineStatus is bool online && online, Num(Data(1), "serverId"));
                            break;
                    }
                    return true;
                case Packets.Skill:
                    // connection.ts handleSkill
                    if (Opcode() == (int)Opcodes.Skill.Batch)
                        foreach (Dictionary<string, object> skill in (List<object>)Data(1)["skills"]) _world.Player.SetSkill(skill);
                    else _world.Player.SetSkill(Data(1));
                    return true;
                case Packets.Rank: _world.Player.SetRank(Arg(0) as double?); return true;
                case Packets.Countdown:
                    if (Entities.Get(Str(Data(0), "instance")) is Entity counted) counted.SetCountdown(Num(Data(0), "time") ?? double.NaN);
                    return true;
                default: return false;
            }
        }

        static double? Num(Dictionary<string, object> data, string key) =>
            data != null && data.TryGetValue(key, out var value) && value is double number ? number : (double?)null;

        static string Str(Dictionary<string, object> data, string key) =>
            data != null && data.TryGetValue(key, out var value) ? value as string : null;

        static bool Truthy(double? value) => value.HasValue && value.Value != 0 && !double.IsNaN(value.Value);

        static bool JsTruthy(object value) =>
            value != null && !(value is bool b && !b) && !(value is double d && (d == 0 || double.IsNaN(d))) && !(value is string s && s == "");

        // connection.ts handleMinigame
        void HandleMinigame(int opcode, Dictionary<string, object> info)
        {
            var minigame = _world.Minigame;
            minigame.Type = opcode;
            minigame.Started = JsTruthy(info.TryGetValue("started", out var started) ? started : null);
            if (Truthy(Num(info, "countdown"))) minigame.Countdown = Num(info, "countdown").Value;

            switch ((Opcodes.MinigameActions?)(int?)Num(info, "action"))
            {
                case Opcodes.MinigameActions.Score:
                    minigame.SetScore(info);
                    minigame.SetStatus("ingame");
                    return;
                case Opcodes.MinigameActions.End:
                case Opcodes.MinigameActions.Lobby:
                    _world.Player.NameColour = "";
                    _world.Pointers.Clean();
                    minigame.SetStatus("lobby");
                    return;
                case Opcodes.MinigameActions.Exit:
                    minigame.Reset();
                    return;
            }
        }

        // connection.ts handleAbility (menu.synchronize('profile'): the profile has no abilities page)
        void HandleAbility(Opcodes.Ability opcode, Dictionary<string, object> info)
        {
            var player = _world.Player;
            switch (opcode)
            {
                case Opcodes.Ability.Batch:
                    foreach (Dictionary<string, object> ability in (List<object>)info["abilities"])
                        player.SetAbility(Str(ability, "key"), Num(ability, "level"), Num(ability, "type"), Num(ability, "quickSlot"));
                    break;
                case Opcodes.Ability.Add:
                case Opcodes.Ability.Update:
                    player.SetAbility(Str(info, "key"), Num(info, "level"), Num(info, "type"), Num(info, "quickSlot"));
                    break;
                case Opcodes.Ability.Toggle:
                    player.ToggleAbility(Str(info, "key"));
                    break;
            }
        }

        // connection.ts handleCommand
        void HandleCommand(Dictionary<string, object> info)
        {
            var command = Str(info, "command");
            if (command == null) throw new InvalidOperationException("TypeError: Cannot read properties of undefined (reading 'includes')");

            var player = _world.Player;
            if (command.Contains("toggle") && player.HasActiveEffect())
            {
                player.RemoveAllEffects();
                return;
            }

            switch (command)
            {
                case "debug":
                    _world.Debugging = !_world.Debugging;
                    return;
                case "hide":
                    player.SetVisible(!player.IsVisible());
                    return;
            }
        }

        // connection.ts handleResource (input.moveCursor is not ported: the port has no custom cursors)
        void HandleResource(Dictionary<string, object> info)
        {
            if (!(Entities.Get(Str(info, "instance")) is Resource resource)) return;

            var depleted = Num(info, "state") == (int)Modules.ResourceState.Depleted;
            resource.SetExhausted(depleted);
            if (depleted) resource.UpdateSilhouette(false);
        }

        /// <summary>Welcome: player.load, then game.postLoad's entity steps (orientation from storage: Down).</summary>
        void HandleWelcome(Dictionary<string, object> data)
        {
            var player = _world.Player;
            player.Load(data);
            Entities.AddEntity(player);
            player.SetSprite(_world.Sprites.Get(player.GetSpriteName()));
            player.Idle();
            player.SetOrientation((int)Modules.Orientation.Down);
            _world.CentreCamera(player);
            // postLoad: pointer.resize after camera.setZoom.
            if (_world.Camera != null) _world.Pointers.Resize();
            player.Handler = new PlayerHandler(player, _world);
            _world.Send(Packets.Ready, new Dictionary<string, object> { ["regionsLoaded"] = _world.RegionsLoaded, ["userAgent"] = _world.UserAgent });
        }

        void HandleEntityList(int opcode, Dictionary<string, object> info)
        {
            switch ((Opcodes.List)opcode)
            {
                case Opcodes.List.Spawns:
                {
                    var listed = ((List<object>)info["entities"]).Cast<string>().ToList();
                    var ids = new HashSet<string>(Entities.Entities.Values.Select(e => e.Instance));
                    var known = new HashSet<string>(listed.Where(ids.Contains));
                    var newIds = listed.Where(id => !known.Contains(id)).ToList();

                    Entities.Decrepit = Entities.Entities.Values
                        .Where(e => !known.Contains(e.Instance) && e.Instance != _world.Player.Instance)
                        .ToList();
                    Entities.Clean();

                    _world.Send(Packets.Who, newIds.Cast<object>().ToList());
                    break;
                }

                case Opcodes.List.Positions:
                {
                    foreach (var pair in (Dictionary<string, object>)info["positions"])
                    {
                        var position = (Dictionary<string, object>)pair.Value;
                        // `return`, not `continue`, in the original: the first missing or moving entity ends the loop.
                        if (!(Entities.Get(pair.Key) is Character entity) || entity.HasPath()) return;

                        var x = (int)(double)position["x"];
                        var y = (int)(double)position["y"];
                        if (entity.GridX != x || entity.GridY != y) _world.Teleport(entity, x, y);
                    }
                    break;
                }
            }
        }

        void HandleSync(Dictionary<string, object> data)
        {
            if (!(Entities.Get(Str(data, "instance")) is Player player) || player.Teleporting || player.Dead || !player.Ready) return;
            player.Load(data, true);
            player.SetSprite(_world.Sprites.Get(player.GetSpriteName()));
            _world.Lights.UpdatePlayerLight(player);
        }

        void HandleMovement(int opcode, Dictionary<string, object> info)
        {
            var entity = Entities.Get(Str(info, "instance")) as Character;

            if (entity == null)
            {
                if (!(_world.Clock.Now - _lastEntityListRequest > 5000)) return;
                _lastEntityListRequest = _world.Clock.Now;
                _world.Send(Packets.List, null);
                return;
            }

            switch ((Opcodes.Movement)opcode)
            {
                case Opcodes.Movement.Move:
                    if (Str(info, "target") == _world.Player.Instance && entity.Following) return;
                    entity.Go((int)Num(info, "x").Value, (int)Num(info, "y").Value,
                        info.TryGetValue("forced", out var forced) && forced is bool f && f,
                        Str(info, "instance") != _world.Player.Instance);
                    break;

                case Opcodes.Movement.Follow:
                    if (Entities.Get(Str(info, "target")) is Character target)
                    {
                        var isForced = info.TryGetValue("forced", out var fv) && fv is bool fb && fb;
                        if (entity.Instance == _world.Player.Instance && !_world.Player.HasTarget() && !isForced) return;
                        entity.Follow(target);
                        if (entity.IsPet) target.AddFollower(entity);
                    }
                    break;

                case Opcodes.Movement.Stop:
                    entity.Stop();
                    return;

                case Opcodes.Movement.Speed:
                    entity.MovementSpeed = Num(info, "movementSpeed");
                    break;
            }
        }

        void HandleTeleport(Dictionary<string, object> info)
        {
            var entity = Entities.Get(Str(info, "instance")) as Character;
            if (entity == null || (!entity.IsMob && !entity.IsPlayer)) return;

            var x = (int)Num(info, "x").Value;
            var y = (int)Num(info, "y").Value;

            entity.Stop(true);
            entity.Frozen = true;

            if (entity.Instance == _world.Player.Instance)
            {
                _world.CleanBubbles();
                _world.Player.DisableAction = true;
            }
            else _world.ClearBubble(entity.Instance);

            if (!(info.TryGetValue("withAnimation", out var animate) && animate is bool a && a))
            {
                _world.Teleport(entity, x, y);
                return;
            }

            var entitySprite = entity.Sprite.Key;
            entity.SetSprite(_world.Sprites.GetDeath());
            entity.Teleporting = true;
            entity.AnimateDeath(() =>
            {
                _world.Teleport(entity, x, y);
                entity.Animation = null;
                entity.SetSprite(_world.Sprites.Get(entitySprite));
                entity.Idle(entity.Orientation, true);
            }, 160);
        }

        void HandleDespawn(Dictionary<string, object> info)
        {
            var entity = Entities.Get(Str(info, "instance"));
            if (entity == null) return;

            if (info.TryGetValue("regions", out var regions) && regions is List<object> list &&
                !list.Any(r => r is double d && d == entity.Region))
                return;

            if (entity.IsItem)
            {
                Entities.RemoveItem(entity);
                return;
            }

            if (entity.IsChest)
            {
                Entities.RemoveChest(entity);
                return;
            }

            if (entity.IsNPC)
            {
                Entities.RemoveNPC(entity);
                return;
            }

            // The original calls entity.despawn() on any other entity; only characters have it.
            if (!(entity is Character character))
                throw new InvalidOperationException("TypeError: entity.despawn is not a function");

            character.Despawn();
            _world.ClearBubble(character.Instance);
            if (_world.Player.HasTarget(character)) _world.Player.RemoveTarget();
            _world.PlayKillSound(character);

            if (!character.Sprite.HasDeathAnimation) character.SetSprite(_world.Sprites.GetDeath());
            character.AnimateDeath(() => Entities.RemoveEntity(character));
        }

        void HandleAnimation(Dictionary<string, object> info)
        {
            if (Entities.Get(Str(info, "instance")) is Character character)
                character.PerformAction(character.Orientation, (Modules.Actions)(int)Num(info, "action").Value);

            var resourceInstance = Str(info, "resourceInstance");
            if (!string.IsNullOrEmpty(resourceInstance) && Entities.Get(resourceInstance) is Resource resource)
            {
                resource.Shake();
                if (resource.IsTree) _world.PlayChoppingSound(_world.Player);
                else if (resource.IsRock) _world.PlayMiningSound(_world.Player);
            }
        }

        void HandlePoints(Dictionary<string, object> info)
        {
            var entity = Entities.Get(Str(info, "instance"));
            if (entity == null || entity.Dead) return;

            if (Truthy(Num(info, "mana")))
            {
                // setMana is a Player method; on a mob the original throws.
                if (!(entity is Player player)) throw new InvalidOperationException("TypeError: character.setMana is not a function");
                player.SetMana(Num(info, "mana"), Num(info, "maxMana"));
            }

            if (Truthy(Num(info, "hitPoints")))
            {
                if (!(entity is Character character)) throw new InvalidOperationException("TypeError: character.setHitPoints is not a function");
                character.SetHitPoints(Num(info, "hitPoints"), Num(info, "maxHitPoints"));
            }
        }

        void HandleUpdate(List<object> updates)
        {
            Entities.CleanDisplayInfo();

            foreach (Dictionary<string, object> update in updates)
            {
                var instance = Str(update, "instance");
                var entity = Entities.Get(instance);
                if (entity == null)
                {
                    Entities.EntityUpdateQueue[instance] = update;
                    continue;
                }

                entity.UpdateDisplayInfo(update);
            }
        }

        // connection.ts handleCombat (only Hit is drawn)
        void HandleCombat(int opcode, Dictionary<string, object> info)
        {
            var attacker = Entities.Get(Str(info, "instance")) as Character;
            var target = Entities.Get(Str(info, "target")) as Character;
            if (attacker == null || target == null) return;
            if (opcode != (int)Opcodes.Combat.Hit) return;

            var hit = (Dictionary<string, object>)info["hit"];
            var type = (int)(Num(hit, "type") ?? -1);
            var damage = Num(hit, "damage") ?? double.NaN;
            var currentPlayerTarget = target.Instance == _world.Player.Instance;
            var currentPlayerAttacker = attacker.Instance == _world.Player.Instance;
            var isPoison = type == (int)Modules.Hits.Poison;
            var isFreezing = type == (int)Modules.Hits.Freezing;
            var isBurning = type == (int)Modules.Hits.Burning;

            if (isPoison) target.AddEffect((int)Modules.Effects.Poisonball);
            if (type == (int)Modules.Hits.Critical) target.AddEffect((int)Modules.Effects.Critical);

            if (!Truthy(Num(hit, "aoe")) && !isPoison && !isFreezing && !isBurning)
            {
                attacker.LookAt(target);
                attacker.PerformAction(attacker.Orientation, Modules.Actions.Attack);
            }

            if (currentPlayerAttacker && damage > 0) _world.PlayHitSound(target);
            if (currentPlayerTarget && damage > 0) _world.PlaySound("hurt", null);

            _world.Info.Create(type, damage, target.X, target.Y, currentPlayerTarget, -1, true, Skills(hit));

            if (damage > 0) target.ToggleHurt();

            attacker.TriggerHealthBar();
            target.TriggerHealthBar();
            target.AddAttacker(attacker);

            if (attacker.IsMob && attacker.GridX == target.GridX && attacker.GridY == target.GridY) _world.FindAdjacentTile(attacker);
        }

        static List<string> Skills(Dictionary<string, object> hit) =>
            hit.TryGetValue("skills", out var skills) && skills is List<object> list ? list.Cast<string>().ToList() : new List<string>();

        // connection.ts handleDeath
        void HandleDeath()
        {
            _world.PlaySound("death", null);
            var player = _world.Player;
            player.Stop(true);
            player.RemoveAllEffects();
            // Remove the minigame interfaces.
            _world.Minigame.Reset();
            player.SetSprite(_world.Sprites.GetDeath());
            player.Teleporting = true;
            player.SetHitPoints(0);
            player.SetMana(0);
            _world.StopMusic();
            player.AnimateDeath(() =>
            {
                Entities.UnregisterPosition(player);
                player.Despawn();
                _world.DeathShown?.Invoke();
            });
        }

        // connection.ts handleRespawn
        void HandleRespawn(Dictionary<string, object> info)
        {
            _world.PlaySound("revive", null);
            var player = _world.Player;
            player.SetGridPosition((int)Num(info, "x").Value, (int)Num(info, "y").Value);
            _world.CentreCamera(player);
            player.Animation = null;
            player.SetSprite(_world.Sprites.Get(player.GetSpriteName()));
            player.Idle();
            Entities.AddEntity(player);
            player.Dead = false;
            player.Teleporting = false;
        }

        // connection.ts handleHeal
        void HandleHeal(Dictionary<string, object> info)
        {
            if (!(Entities.Get(Str(info, "instance")) is Character character)) return;

            switch (Str(info, "type"))
            {
                case "hitpoints":
                    _world.Info.Create((int)Modules.Hits.Heal, Num(info, "amount") ?? double.NaN, character.X, character.Y);
                    character.AddEffect((int)Modules.Effects.Healing);
                    if (_world.Player.Instance == character.Instance) _world.PlaySound("heal", null);
                    break;
                case "mana":
                    _world.Info.Create((int)Modules.Hits.Mana, Num(info, "amount") ?? double.NaN, character.X, character.Y);
                    break;
            }

            character.TriggerHealthBar();
        }

        // connection.ts handleEffect
        void HandleEffect(int opcode, Dictionary<string, object> info)
        {
            var instance = Str(info, "instance");
            var entity = instance == _world.Player.Instance ? _world.Player : Entities.Get(instance) as Character;
            if (entity == null) return;

            var effect = (int)Num(info, "effect").Value;
            if (opcode == (int)Opcodes.Effect.Add) entity.AddEffect(effect);
            else if (opcode == (int)Opcodes.Effect.Remove) entity.RemoveEffect(effect);
        }

        // connection.ts handleExperience
        void HandleExperience(int opcode, Dictionary<string, object> info)
        {
            if (!(Entities.Get(Str(info, "instance")) is Player player)) return;
            var isPlayer = player.Instance == _world.Player.Instance;

            switch ((Opcodes.Experience)opcode)
            {
                case Opcodes.Experience.Sync:
                    player.Level = Num(info, "level");
                    break;
                case Opcodes.Experience.Skill:
                    if (isPlayer)
                        _world.Info.Create((int)Modules.Hits.Profession, Num(info, "amount") ?? double.NaN, player.X, player.Y,
                            false, (int)(Num(info, "skill") ?? -1), true);
                    break;
            }
        }

        /// <summary>updater.ts updateInfos.</summary>
        public void UpdateInfos() => _world.Info.Update(_world.Time);

        void HandleEquipment(int opcode, Dictionary<string, object> info)
        {
            var player = _world.Player;

            switch ((Opcodes.Equipment)opcode)
            {
                case Opcodes.Equipment.Batch:
                    foreach (Dictionary<string, object> equipment in (List<object>)((Dictionary<string, object>)info["data"])["equipments"])
                        player.Equip(equipment);
                    break;
                case Opcodes.Equipment.Equip:
                    player.Equip((Dictionary<string, object>)info["data"]);
                    break;
                case Opcodes.Equipment.Unequip:
                    player.Unequip((int)Num(info, "type").Value, Num(info, "count") ?? -1);
                    break;
                case Opcodes.Equipment.Style:
                    player.SetAttackStyle(Num(info, "attackStyle"));
                    player.AttackRange = Num(info, "attackRange");
                    break;
            }

            player.Sync();
            // Update the lighting for the player.
            _world.Lights.UpdatePlayerLight();
        }

        /// <summary>renderer/updater.ts updateEntities.</summary>
        public void UpdateEntities()
        {
            foreach (var entity in _world.Entities.Entities.Values)
            {
                if (entity.Sprite == null || !entity.Sprite.Loaded) continue;

                UpdateFading(entity);
                entity.Animation?.Update(_world.Time);

                if (entity is Projectile projectile)
                {
                    var mDistance = projectile.Speed * projectile.GetTimeDiff();
                    var dx = projectile.Target.X - projectile.X;
                    var dy = projectile.Target.Y - projectile.Y;
                    var tDistance = JsMath.Hypot(dx, dy);
                    var amount = mDistance / tDistance;

                    projectile.UpdateAngle();

                    if (amount > 1) amount = 1;

                    projectile.X += dx * amount;
                    projectile.Y += dy * amount;

                    if (tDistance < 5) projectile.ImpactCallback?.Invoke();

                    projectile.LastUpdate = _world.Time;
                    continue;
                }

                if (!(entity is Character character)) continue;

                if (character.Movement.InProgress)
                {
                    character.Movement.Step(_world.Time);
                    continue;
                }

                if (!character.HasPath()) continue;

                var horizontal = character.Orientation == (int)Modules.Orientation.Left || character.Orientation == (int)Modules.Orientation.Right;
                var vertical = character.Orientation == (int)Modules.Orientation.Up || character.Orientation == (int)Modules.Orientation.Down;
                var left = horizontal && character.Orientation == (int)Modules.Orientation.Left;
                var up = vertical && character.Orientation == (int)Modules.Orientation.Up;
                var tile = _world.TileSize;

                character.Movement.Start(
                    _world.Time,
                    horizontal ? character.X + (left ? -1 : 1) : character.Y + (up ? -1 : 1),
                    horizontal ? character.X + (left ? -tile : tile) : character.Y + (up ? -tile : tile),
                    character.MovementSpeed ?? double.NaN,
                    value =>
                    {
                        if (horizontal) character.X = value;
                        if (vertical) character.Y = value;
                        character.Moved();
                    },
                    () =>
                    {
                        if (horizontal) character.X = character.Movement.EndValue;
                        if (vertical) character.Y = character.Movement.EndValue;
                        character.Moved();
                        character.NextStep();
                    });
            }
        }

        /// <summary>updater.ts updateKeyboard.</summary>
        public void UpdateKeyboard() => _world.Input.UpdateKeyboard();

        /// <summary>updater.ts updateAnimations: the shared preloaded animations (sparks, exclamations).</summary>
        public void UpdateAnimations()
        {
            foreach (var animation in _world.Sprites.PreloadedAnimations) animation.Update(_world.Time);
        }

        void UpdateFading(Entity entity)
        {
            if (!entity.Fading) return;

            var dt = _world.Time - entity.FadingTime;
            if (dt > entity.FadingDuration)
            {
                entity.Fading = false;
                entity.FadingAlpha = 1;
            }
            else entity.FadingAlpha = dt / entity.FadingDuration;
        }
    }
}
