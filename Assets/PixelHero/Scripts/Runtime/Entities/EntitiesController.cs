using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;
using PixelHero.Maps;

namespace PixelHero.Entities
{
    /// <summary>
    /// A string-keyed map with JavaScript object order for non-integer keys: insertion order, an existing key keeps
    /// its place when set again, a deleted key goes to the end when added back.
    /// </summary>
    public sealed class OrderedMap<TValue> where TValue : class
    {
        readonly Dictionary<string, LinkedListNode<KeyValuePair<string, TValue>>> _index =
            new Dictionary<string, LinkedListNode<KeyValuePair<string, TValue>>>();
        readonly LinkedList<KeyValuePair<string, TValue>> _order = new LinkedList<KeyValuePair<string, TValue>>();

        public int Count => _index.Count;

        public TValue this[string key]
        {
            get => _index.TryGetValue(key, out var node) ? node.Value.Value : null;
            set
            {
                if (_index.TryGetValue(key, out var node)) node.Value = new KeyValuePair<string, TValue>(key, value);
                else _index[key] = _order.AddLast(new KeyValuePair<string, TValue>(key, value));
            }
        }

        public bool ContainsKey(string key) => _index.ContainsKey(key);

        public void Remove(string key)
        {
            if (!_index.TryGetValue(key, out var node)) return;
            _order.Remove(node);
            _index.Remove(key);
        }

        public List<TValue> Values => _order.Select(pair => pair.Value).ToList();

        public List<string> Keys => _order.Select(pair => pair.Key).ToList();

        public void Clear()
        {
            _index.Clear();
            _order.Clear();
        }
    }

    /// <summary>client/src/map/grids.ts renderingGrid: per cell, the entities drawn there (object order).</summary>
    public sealed class RenderingGrid
    {
        readonly ClientMap _map;
        readonly Dictionary<int, OrderedMap<Entity>> _cells = new Dictionary<int, OrderedMap<Entity>>();

        public RenderingGrid(ClientMap map) => _map = map;

        public void Add(Entity entity)
        {
            if (_map.IsOutOfBounds(entity.GridX, entity.GridY)) return;
            var index = _map.CoordToIndex(entity.GridX, entity.GridY);
            if (!_cells.TryGetValue(index, out var cell)) _cells[index] = cell = new OrderedMap<Entity>();
            cell[entity.Instance] = entity;
        }

        public void Remove(Entity entity)
        {
            if (entity is Character character && character.HasPath())
                foreach (var tile in character.Path) Delete(tile[0], tile[1], entity.Instance);

            Delete(entity.GridX, entity.GridY, entity.Instance);
        }

        void Delete(int x, int y, string instance)
        {
            // JS `delete grid[y][x][instance]` throws on rows outside the map; such positions never occur.
            if (_map.IsOutOfBounds(x, y)) return;
            if (_cells.TryGetValue(_map.CoordToIndex(x, y), out var cell)) cell.Remove(instance);
        }

        public bool Contains(int x, int y, string instance) =>
            !_map.IsOutOfBounds(x, y) && _cells.TryGetValue(_map.CoordToIndex(x, y), out var cell) && cell.ContainsKey(instance);

        /// <summary>The entities in a cell, in drawing order.</summary>
        public List<Entity> At(int x, int y) =>
            !_map.IsOutOfBounds(x, y) && _cells.TryGetValue(_map.CoordToIndex(x, y), out var cell) ? cell.Values : new List<Entity>();
    }

    /// <summary>client/src/controllers/entities.ts.</summary>
    public sealed class EntitiesController
    {
        readonly World _world;

        public readonly OrderedMap<Entity> Entities = new OrderedMap<Entity>();
        public readonly Dictionary<string, Dictionary<string, object>> EntityUpdateQueue = new Dictionary<string, Dictionary<string, object>>();
        public List<Entity> Decrepit = new List<Entity>();
        public readonly RenderingGrid Grid;

        /// <summary>Reports a failure the original logs (log.error / console.trace) and carries on.</summary>
        public Action<string> Log = _ => { };

        public EntitiesController(World world)
        {
            _world = world;
            Grid = new RenderingGrid(world.Map);
        }

        public void Create(Dictionary<string, object> info)
        {
            var instance = (string)info["instance"];
            if (IsPlayer(instance)) return;
            if (Entities.ContainsKey(instance)) return;

            Entity entity = null;
            var prefix = "";

            switch ((Modules.EntityType)(int)(double)info["type"])
            {
                case Modules.EntityType.Chest:
                    entity = new Chest(_world, instance);
                    prefix = "objects";
                    break;
                case Modules.EntityType.NPC:
                    entity = new Npc(_world, instance);
                    prefix = "npcs";
                    break;
                case Modules.EntityType.LootBag:
                case Modules.EntityType.Item:
                    entity = new Item(_world, instance, Num(info, "count"), info.TryGetValue("enchantments", out var enchantments) ? enchantments : null);
                    prefix = "items";
                    break;
                case Modules.EntityType.Mob:
                    entity = CreateMob(info);
                    prefix = "mobs";
                    break;
                case Modules.EntityType.Projectile:
                    entity = CreateProjectile(info);
                    prefix = "projectiles";
                    break;
                case Modules.EntityType.Player:
                    entity = CreatePlayer(info);
                    prefix = "player";
                    break;
                case Modules.EntityType.Pet:
                    entity = CreatePet(info);
                    prefix = "pets";
                    break;
                case Modules.EntityType.Effect:
                    entity = new Effect(_world, instance);
                    prefix = "effectentity";
                    break;
                case Modules.EntityType.Tree:
                    entity = new Tree(_world, instance) { Exhausted = Depleted(info) };
                    prefix = "trees";
                    break;
                case Modules.EntityType.Rock:
                    entity = new Rock(_world, instance) { Exhausted = Depleted(info) };
                    prefix = "rocks";
                    break;
                case Modules.EntityType.FishSpot:
                    entity = new FishSpot(_world, instance) { Exhausted = Depleted(info) };
                    prefix = "fishspots";
                    break;
                case Modules.EntityType.Foraging:
                    entity = new Foraging(_world, instance) { Exhausted = Depleted(info) };
                    prefix = "bushes";
                    break;
            }

            if (entity == null)
            {
                Log($"Failed to create entity {instance}");
                return;
            }

            var key = info.TryGetValue("key", out var k) ? k as string : null;
            var sprite = entity.Sprite ?? _world.Sprites.Get($"{prefix}/{(key ?? "undefined")}");
            if (sprite == null)
            {
                Log($"Failed to create sprite for entity {key}.");
                return;
            }

            entity.Name = info.TryGetValue("name", out var name) ? name as string : null;
            entity.SetGridPosition((int)(double)info["x"], (int)(double)info["y"]);
            entity.SetSprite(sprite);
            entity.Idle();

            AddEntity(entity);

            if (EntityUpdateQueue.TryGetValue(instance, out var update))
            {
                entity.UpdateDisplayInfo(update);
                EntityUpdateQueue.Remove(instance);
            }
        }

        static double? Num(Dictionary<string, object> data, string key) =>
            data.TryGetValue(key, out var value) && value is double number ? number : (double?)null;

        static bool Depleted(Dictionary<string, object> info) =>
            Num(info, "state") == (int)Modules.ResourceState.Depleted;

        Mob CreateMob(Dictionary<string, object> info)
        {
            var mob = new Mob(_world, (string)info["instance"]);
            mob.SetHitPoints(Num(info, "hitPoints"), Num(info, "maxHitPoints"));
            mob.AttackRange = Num(info, "attackRange");
            mob.Level = Num(info, "level");
            mob.HiddenName = info.TryGetValue("hiddenName", out var hidden) && hidden is bool h ? h : (bool?)null;
            mob.MovementSpeed = Num(info, "movementSpeed");
            if (Num(info, "orientation") is double orientation) mob.Orientation = (int)orientation;

            if (info.TryGetValue("displayInfo", out var display) && display is Dictionary<string, object> displayInfo)
            {
                if (displayInfo.TryGetValue("colour", out var colour) && colour is string c && c.Length > 0) mob.NameColour = c;
                if (displayInfo.TryGetValue("scale", out var scale) && scale is double s && s != 0) mob.CustomScale = s;
            }

            return mob;
        }

        Projectile CreateProjectile(Dictionary<string, object> info)
        {
            var attacker = Get(info.TryGetValue("ownerInstance", out var owner) ? owner as string : null) as Character;
            var target = Get(info.TryGetValue("targetInstance", out var t) ? t as string : null) as Character;
            if (target == null) return null;

            var hit = (Dictionary<string, object>)info["hit"];
            var projectile = new Projectile(_world, (string)info["instance"], (int)(double)hit["type"]);
            projectile.Name = info.TryGetValue("name", out var name) ? name as string : null;
            projectile.SetTarget(target);

            projectile.OnImpact(() =>
            {
                var impactEffect = projectile.GetImpactEffect();
                if (impactEffect != (int)Modules.Effects.None) target.AddEffect(impactEffect);

                _world.Info.Create((int)(double)hit["type"], hit.TryGetValue("damage", out var damage) && damage is double d ? d : double.NaN,
                    target.X, target.Y, IsPlayer(target.Instance), -1, true,
                    hit.TryGetValue("skills", out var skills) && skills is List<object> list ? list.Cast<string>().ToList() : new List<string>());
                target.TriggerHealthBar();
                target.ToggleHurt();
                UnregisterPosition(projectile);
                Entities.Remove(projectile.Instance);
            });

            if (attacker == null) return projectile;

            projectile.SetGridPosition(attacker.GridX, attacker.GridY);
            attacker.LookAt(target);
            attacker.PerformAction(attacker.Orientation, Modules.Actions.Attack);
            attacker.TriggerHealthBar();

            return projectile;
        }

        Player CreatePlayer(Dictionary<string, object> info)
        {
            var player = new Player(_world, (string)info["instance"]);
            player.Load(info);
            player.SetSprite(_world.Sprites.Get(player.GetSpriteName()));
            player.Ready = true;
            // If the player has a light source then we add it to the renderer.
            if (player.HasLight()) _world.Lights.AddPlayerLight(player);
            return player;
        }

        Pet CreatePet(Dictionary<string, object> info)
        {
            var ownerInstance = info.TryGetValue("owner", out var o) ? o as string : null;
            var pet = new Pet(_world, (string)info["instance"], ownerInstance);
            var owner = Get(ownerInstance) as Player;

            pet.MovementSpeed = Num(info, "movementSpeed");

            if (owner != null)
            {
                owner.HasPet = true;
                owner.AddFollower(pet);
                pet.SetTarget(owner);
                if (owner.Instance == _world.Player.Instance) _world.Player.Sync();
            }

            return pet;
        }

        bool IsPlayer(string instance) => _world.Player.Instance == instance;

        public Entity Get(string instance) => instance == null ? null : Entities[instance];

        public void AddEntity(Entity entity)
        {
            Entities[entity.Instance] = entity;
            RegisterPosition(entity);
            if (!(entity is Item item && item.Dropped)) entity.FadeIn(_world.Time);
        }

        public void RemoveEntity(Entity entity)
        {
            if (entity.IsPlayer) entity.Ready = false;

            if (entity is Pet pet && Get(pet.Owner) is Player owner)
            {
                owner.HasPet = false;
                owner.RemoveFollower(pet);
                if (owner.Instance == _world.Player.Instance) _world.Player.Sync();
            }

            UnregisterPosition(entity);
            Entities.Remove(entity.Instance);
        }

        public void RemoveItem(Entity item)
        {
            UnregisterPosition(item);
            Entities.Remove(item.Instance);
        }

        public void RemoveChest(Entity chest)
        {
            chest.SetSprite(_world.Sprites.GetDeath());
            chest.AnimateDeath(() =>
            {
                UnregisterPosition(chest);
                Entities.Remove(chest.Instance);
            });
        }

        public void RemoveNPC(Entity npc) => RemoveChest(npc);

        public void RegisterPosition(Entity entity)
        {
            if (entity.IsResource || entity.IsNPC) _world.Map.Grid[_world.Map.CoordToIndex(entity.GridX, entity.GridY)] = 2;
            Grid.Add(entity);
        }

        public void UnregisterPosition(Entity entity) => Grid.Remove(entity);

        public void Clean()
        {
            if (Decrepit.Count == 0) return;
            foreach (var entity in Decrepit)
                if (entity != null) RemoveEntity(entity);
        }

        public void CleanDisplayInfo()
        {
            foreach (var entity in Entities.Values)
            {
                entity.NameColour = "";
                entity.CustomScale = 0;
                entity.Exclamation = false;
                entity.BlueExclamation = false;
            }
        }
    }
}
