using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;

namespace PixelHero.Entities
{
    /// <summary>
    /// client/src/entity/character/character.ts: orientation-aware animations (left = right mirrored), the
    /// tile-by-tile path state machine (nextStep / resetMovement) and the callbacks a handler plugs in.
    /// </summary>
    public class Character : Entity
    {
        public bool Moving;
        public bool Following;
        public bool Stunned;
        public bool Forced;
        public bool Trading;
        bool _interrupted;
        public List<int[]> Path;
        public Entity Target;
        public double LastFollow;
        public readonly List<int> StatusEffects = new List<int>();
        readonly List<KeyValuePair<string, Character>> _attackers = new List<KeyValuePair<string, Character>>();
        readonly List<KeyValuePair<string, Character>> _followers = new List<KeyValuePair<string, Character>>();
        public Transition Movement = new Transition();
        public CharacterHandler Handler;

        protected virtual double AttackAnimationSpeed => 50;
        protected virtual double WalkAnimationSpeed => 120;

        public (int x, int y)? Destination;
        (int x, int y)? _newDestination;
        int _step;
        int _healthBarTimeout;

        public Func<int, int, List<int[]>> RequestPathCallback;
        public Action<List<int[]>> StartPathingCallback;
        public Action BeforeStepCallback;
        public Action StepCallback;
        public Action SecondStepCallback;
        public Action<int, int, bool> StopPathingCallback;
        public Action MoveCallback;
        public Action<int, int> FallbackCallback;
        public Action<double?, double?, bool> HitPointsCallback;

        /// <summary>A status effect overlay (character.ts effects table).</summary>
        public sealed class EffectInfo
        {
            public string Key;
            public Animation Animation;
            public bool Perpetual;
            public double? Speed;
        }

        /// <summary>Effect id → overlay, keyed by number (integer keys iterate ascending in the original).</summary>
        readonly SortedDictionary<int, EffectInfo> _effects = new SortedDictionary<int, EffectInfo>();

        public Character(World world, string instance, Modules.EntityType type) : base(world, instance, (int)type)
        {
            AddEffectInfo(Modules.Effects.Critical, "effects/critical", 10, 48);
            AddEffectInfo(Modules.Effects.Terror, "effects/terror", 8, 64);
            AddEffectInfo(Modules.Effects.TerrorStatus, "effects/terror2", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.Stun, "effects/stun", 6, 48, true);
            AddEffectInfo(Modules.Effects.Healing, "effects/heal", 8, 64);
            AddEffectInfo(Modules.Effects.Fireball, "effects/fireball", 8, 64);
            AddEffectInfo(Modules.Effects.Iceball, "effects/iceball", 8, 64);
            AddEffectInfo(Modules.Effects.Burning, "effects/burn", 5, 32, true, 150);
            AddEffectInfo(Modules.Effects.Freezing, "effects/freeze", 6, 32, true, 200);
            AddEffectInfo(Modules.Effects.Poisonball, "effects/poisonball", 10, 40, false, 175);
            AddEffectInfo(Modules.Effects.Boulder, "effects/boulder", 7, 32);
            AddEffectInfo(Modules.Effects.AccuracyBuff, "effects/accuracy", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.StrengthBuff, "effects/strength", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.DefenseBuff, "effects/defense", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.MagicBuff, "effects/magic", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.ArcheryBuff, "effects/archery", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.AccuracySuperBuff, "effects/accuracysuper", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.StrengthSuperBuff, "effects/strengthsuper", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.DefenseSuperBuff, "effects/defensesuper", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.MagicSuperBuff, "effects/magicsuper", 5, 32, true, 200);
            AddEffectInfo(Modules.Effects.ArcherySuperBuff, "effects/archerysuper", 5, 32, true, 200);

            // character.ts loadAnimations
            foreach (var pair in _effects)
            {
                var key = pair.Key;
                var effect = pair.Value;
                effect.Animation.SetSpeed(effect.Speed ?? 50);
                effect.Animation.SetCount(1, () =>
                {
                    if (!effect.Perpetual) RemoveEffect(key);
                    if (key == (int)Modules.Effects.Terror) AddEffect((int)Modules.Effects.TerrorStatus);
                    effect.Animation.Reset();
                    effect.Animation.Count = 1;
                });
            }

            Handler = new CharacterHandler(this, world);
        }

        void AddEffectInfo(Modules.Effects effect, string key, int length, int size, bool perpetual = false, double? speed = null) =>
            _effects[(int)effect] = new EffectInfo
            {
                Key = key,
                Animation = new Animation(World.Clock, "effect", length, 0, size, size),
                Perpetual = perpetual,
                Speed = speed
            };

        public EffectInfo GetEffect(int effect) => _effects.TryGetValue(effect, out var info) ? info : null;

        public bool HasActiveEffect() => StatusEffects.Count > 0;

        /// <summary>character.ts setAnimation: atk/bow_atk/walk/idle get a direction suffix; left uses right, mirrored.</summary>
        public override void SetAnimation(string name, double? speed = null, int count = 0, Action onEndCount = null, bool withStop = false)
        {
            if (Animation?.Name == "death") return;

            SpriteFlipX = false;
            SpriteFlipY = false;

            if (name == "atk" || name == "bow_atk" || name == "walk" || name == "idle")
            {
                name += "_" + (Orientation == (int)Modules.Orientation.Left ? "right" : OrientationToString());
                SpriteFlipX = Orientation == (int)Modules.Orientation.Left;
            }

            base.SetAnimation(name, speed ?? 120, count, onEndCount);
        }

        public void ToggleHurt()
        {
            if (Dead || Teleporting)
            {
                Hurt = false;
                return;
            }

            if (Sprite.HasHurtSprite && !Sprite.HurtVariant) Sprite.LoadHurtSprite();

            Hurt = true;
            World.Timers.SetTimeout(() => Hurt = false, 100);
        }

        public void Despawn()
        {
            HitPoints = 0;
            Dead = true;
            RemoveTarget();
            Stop();
            Orientation = (int)Modules.Orientation.Down;
        }

        public void Follow(Entity entity, bool forced = false)
        {
            if (World.Clock.Now - LastFollow < 300) return;
            if (Dead || IsStunned() || Teleporting) return;

            LastFollow = World.Clock.Now;
            Following = true;
            SetTarget(entity);
            Move(entity.GridX, entity.GridY, forced);
        }

        /// <summary>character.ts trade: follow the player and ask for the trade on arrival (player/handler.ts).</summary>
        public void Trade(Entity entity)
        {
            if (Dead || Teleporting) return;
            Trading = true;
            Follow(entity);
        }

        /// <summary>character.ts pursue: target the character, walk to it and become one of its followers.</summary>
        public void Pursue(Character character)
        {
            if (Dead || IsStunned() || Teleporting) return;
            SetTarget(character);
            Move(character.GridX, character.GridY);
            character.AddFollower(this);
        }

        public void AddEffect(int effect)
        {
            if (HasEffect(effect)) return;
            StatusEffects.Add(effect);
        }

        public void RemoveEffect(int effect)
        {
            if (effect == (int)Modules.Effects.Terror) RemoveEffect((int)Modules.Effects.TerrorStatus);
            StatusEffects.RemoveAll(e => e == effect);
        }

        public void RemoveAllEffects() => StatusEffects.Clear();

        public bool HasEffect(int effect) => StatusEffects.Contains(effect);

        // attackers/followers are objects keyed by instance: insertion order, a re-added key keeps its place.
        public void AddAttacker(Character character) => Put(_attackers, character);
        public void AddFollower(Character character) => Put(_followers, character);
        public void RemoveAttacker(Character character) => _attackers.RemoveAll(p => p.Key == character.Instance);
        public void RemoveFollower(Character character) => _followers.RemoveAll(p => p.Key == character.Instance);
        public bool HasAttackers() => _attackers.Count > 0;
        public bool HasFollowers() => _followers.Count > 0;

        static void Put(List<KeyValuePair<string, Character>> list, Character character)
        {
            var index = list.FindIndex(p => p.Key == character.Instance);
            if (index >= 0) list[index] = new KeyValuePair<string, Character>(character.Instance, character);
            else list.Add(new KeyValuePair<string, Character>(character.Instance, character));
        }

        public void ForEachAttacker(Action<Character> callback)
        {
            foreach (var pair in _attackers.ToList()) callback(pair.Value);
        }

        public void ForEachFollower(Action<Character> callback)
        {
            foreach (var pair in _followers.ToList()) callback(pair.Value);
        }

        public bool IsStunned() => StatusEffects.Contains((int)Modules.Effects.Stun);

        public void PerformAction(int orientation, Modules.Actions action)
        {
            SetOrientation(orientation);

            switch (action)
            {
                case Modules.Actions.Idle:
                case Modules.Actions.Orientate:
                    SetAnimation("idle", Sprite.IdleSpeed);
                    return;
                case Modules.Actions.Attack:
                    SetAnimation("atk", AttackAnimationSpeed, 1);
                    return;
                case Modules.Actions.Walk:
                    SetAnimation("walk", WalkAnimationSpeed);
                    return;
            }
        }

        public override void Idle() => Idle(null, false);

        public virtual void Idle(int? orientation, bool force)
        {
            if ((HasPath() || Moving) && !force) return;

            // `o || this.orientation`: Orientation.Up (0) is falsy and falls back to the current one.
            var o = orientation.HasValue && orientation.Value != 0 ? orientation.Value : Orientation;
            PerformAction(o, Modules.Actions.Idle);
        }

        string OrientationToString()
        {
            switch ((Modules.Orientation)Orientation)
            {
                case Modules.Orientation.Left: return "left";
                case Modules.Orientation.Right: return "right";
                case Modules.Orientation.Up: return "up";
                case Modules.Orientation.Down: return "down";
                default: return "undefined";
            }
        }

        public void LookAt(Entity entity)
        {
            if (entity.GridX > GridX) SetOrientation((int)Modules.Orientation.Right);
            else if (entity.GridX < GridX) SetOrientation((int)Modules.Orientation.Left);
            else if (entity.GridY > GridY) SetOrientation((int)Modules.Orientation.Down);
            else if (entity.GridY < GridY) SetOrientation((int)Modules.Orientation.Up);

            Idle();
        }

        public void LookAtPosition(int x, int y)
        {
            if (x > GridX) SetOrientation((int)Modules.Orientation.Right);
            else if (x < GridX) SetOrientation((int)Modules.Orientation.Left);
            else if (y > GridY) SetOrientation((int)Modules.Orientation.Down);
            else if (y < GridY) SetOrientation((int)Modules.Orientation.Up);

            Idle();
        }

        public void FollowPosition(int x, int y)
        {
            Following = true;
            Move(x, y);
        }

        /// <summary>character.ts setObjectTarget: a stand-in Object character on the tile becomes the target.</summary>
        public void SetObjectTarget(int gridX, int gridY)
        {
            var character = new Character(World, $"{gridX}-{gridY}", Modules.EntityType.Object);
            character.SetGridPosition(gridX, gridY);
            SetTarget(character);
            FollowPosition(gridX, gridY);
        }

        public void Go(int x, int y, bool forced = false, bool fallback = false)
        {
            if (Frozen) return;

            if (Following)
            {
                Following = false;
                RemoveTarget();
            }

            Move(x, y, forced, fallback);
        }

        void Move(int x, int y, bool forced = false, bool fallback = false)
        {
            if (HasPath() && !forced) _newDestination = (x, y);
            else
            {
                var path = RequestPathfinding(x, y);

                if ((path == null || path.Count < 2) && fallback)
                {
                    FallbackCallback?.Invoke(x, y);
                    return;
                }

                FollowPath(path);
            }

            Destination = (x, y);
        }

        public void NextStep()
        {
            var stop = false;

            if (_step % 2 == 0) SecondStepCallback?.Invoke();

            if (!HasPath()) return;

            BeforeStepCallback?.Invoke();
            UpdateGridPosition();

            if (_interrupted) stop = true;

            if (HasNextStep())
            {
                NextGridX = Path[_step + 1][0];
                NextGridY = Path[_step + 1][1];
            }

            if (!stop)
            {
                StepCallback?.Invoke();

                if (ChangedPath())
                {
                    var (x, y) = _newDestination.Value;
                    var path = RequestPathfinding(x, y);
                    if (path == null) return;

                    _newDestination = null;

                    if (path.Count < 2) stop = true;
                    else
                    {
                        StartPathingCallback?.Invoke(path);
                        FollowPath(path);
                    }
                }
                else if (HasNextStep())
                {
                    _step++;
                    UpdateMovement();
                }
                else stop = true;
            }

            if (stop) ResetMovement();
        }

        void ResetMovement(bool withCallback = true)
        {
            Path = null;
            Idle();
            Destination = null;
            _newDestination = null;
            Movement = new Transition();
            NextGridX = GridX;
            NextGridY = GridY;
            Forced = false;
            _interrupted = false;

            if (withCallback) StopPathingCallback?.Invoke(GridX, GridY, Forced);
        }

        void UpdateMovement()
        {
            if (Path == null) return;
            var current = Path[_step];
            var previous = Path[_step - 1];

            if (current[0] < previous[0]) PerformAction((int)Modules.Orientation.Left, Modules.Actions.Walk);
            if (current[0] > previous[0]) PerformAction((int)Modules.Orientation.Right, Modules.Actions.Walk);
            if (current[1] < previous[1]) PerformAction((int)Modules.Orientation.Up, Modules.Actions.Walk);
            if (current[1] > previous[1]) PerformAction((int)Modules.Orientation.Down, Modules.Actions.Walk);
        }

        void FollowPath(List<int[]> path)
        {
            if (path == null || path.Count < 2) return;

            Path = path;
            _step = 0;

            if (Following) path.RemoveAt(path.Count - 1);

            if (!Moving && !ChangedPath()) StartPathingCallback?.Invoke(path);

            NextStep();
        }

        public void Stop(bool force = false)
        {
            if (!HasPath()) return;

            if (force)
            {
                ResetMovement(false);
                return;
            }

            _interrupted = true;
        }

        public bool CanAttackTarget()
        {
            if (!HasTarget()) return false;
            if (!Target.IsMob && !Target.IsPlayer) return false;
            // `distance > this.attackRange` with an undefined range is false.
            if (AttackRange.HasValue && GetDistance(Target) > AttackRange.Value) return false;
            return true;
        }

        public void TriggerHealthBar()
        {
            HealthBarVisible = true;
            if (_healthBarTimeout != 0) World.Timers.Clear(_healthBarTimeout);
            _healthBarTimeout = World.Timers.SetTimeout(() => HealthBarVisible = false, 7000);
        }

        public void ClearHealthBar()
        {
            HealthBarVisible = false;
            World.Timers.Clear(_healthBarTimeout);
            _healthBarTimeout = 0;
        }

        List<int[]> RequestPathfinding(int x, int y) => RequestPathCallback?.Invoke(x, y);

        void UpdateGridPosition()
        {
            if (Path == null || Path.Count == 0) return;
            SetGridPosition(Path[_step][0], Path[_step][1]);
        }

        public override bool HasShadow() => true;

        public bool HasPath() => Path != null;

        public bool HasNextStep() => Path.Count - 1 > _step;

        public bool ChangedPath() => _newDestination != null;

        public void RemoveTarget() => Target = null;

        public void Moved()
        {
            if (!World.IsMainPlayer(Instance)) return;
            MoveCallback?.Invoke();
        }

        public void SetTarget(Entity target) => Target = target;

        public bool HasTarget(Entity target = null) => target != null ? Target == target : Target != null;

        /// <summary>character.ts setHitPoints (undefined values follow JavaScript comparisons).</summary>
        public void SetHitPoints(double? hitPoints, double? maxHitPoints = null)
        {
            var decrease = false;

            if (hitPoints < 0) hitPoints = 0;
            if (hitPoints < HitPoints) decrease = true;

            HitPoints = hitPoints;

            if (maxHitPoints.HasValue && maxHitPoints.Value != 0 && !double.IsNaN(maxHitPoints.Value)) MaxHitPoints = maxHitPoints;

            HitPointsCallback?.Invoke(HitPoints, maxHitPoints ?? MaxHitPoints, decrease);
        }

        public virtual void SetOrientation(int orientation) => Orientation = orientation;
    }

    public sealed class Mob : Character
    {
        public bool? HiddenName = false;

        public Mob(World world, string instance) : base(world, instance, Modules.EntityType.Mob) { }

        public override bool HasShadow() => HiddenName != true;
        public override bool DrawNames() => HiddenName != true;
    }

    public sealed class Pet : Character
    {
        public readonly string Owner;

        public Pet(World world, string instance, string owner) : base(world, instance, Modules.EntityType.Pet) => Owner = owner;

        public override void Idle(int? orientation, bool force)
        {
            if (Moving) return;
            base.Idle(orientation, force);
        }
    }

    /// <summary>client/src/entity/character/handler.ts: the base handler every character gets.</summary>
    public class CharacterHandler
    {
        protected readonly Character Character;
        protected readonly World World;

        public CharacterHandler(Character character, World world)
        {
            Character = character;
            World = world;

            character.RequestPathCallback = HandleRequestPath;
            character.StartPathingCallback = HandleStartPathing;
            character.BeforeStepCallback = HandleBeforeStep;
            character.StepCallback = HandleStep;
            character.SecondStepCallback = HandleSecondStep;
            character.StopPathingCallback = HandleStopPathing;
            character.MoveCallback = HandleMove;
            character.FallbackCallback = HandleFallback;
        }

        protected virtual List<int[]> HandleRequestPath(int x, int y)
        {
            if (Character.GridX == x && Character.GridY == y) return new List<int[]>();
            if (World.Map.IsColliding(x, y)) return new List<int[]>();
            return World.FindPath(Character, x, y);
        }

        protected virtual void HandleStartPathing(List<int[]> path) => Character.Moving = true;

        protected virtual void HandleBeforeStep() => World.Entities.UnregisterPosition(Character);

        protected virtual void HandleStep()
        {
            World.Entities.RegisterPosition(Character);

            HandleAttackers();
            HandleFollowers();

            if ((Character.IsMob || Character.IsPet) && (Character.HasTarget() || Character.HasAttackers()))
                World.Send(Packets.Movement, new Dictionary<string, object>
                {
                    ["opcode"] = Opcodes.Movement.Entity,
                    ["targetInstance"] = Character.Instance,
                    ["requestX"] = Character.GridX,
                    ["requestY"] = Character.GridY
                });

            if (Character.Moving && Character.CanAttackTarget()) Character.Stop();
        }

        protected virtual void HandleSecondStep() { }

        protected virtual void HandleStopPathing(int x, int y, bool forced)
        {
            World.Entities.RegisterPosition(Character);
            Character.Moving = false;
        }

        protected virtual void HandleMove() { }

        protected virtual void HandleFallback(int x, int y) => World.Teleport(Character, x, y);

        protected void HandleAttackers() =>
            Character.ForEachAttacker(attacker =>
            {
                if (attacker.Target?.Instance != Character.Instance)
                {
                    Character.RemoveAttacker(attacker);
                    return;
                }

                if (!attacker.CanAttackTarget()) attacker.Follow(Character);
            });

        protected void HandleFollowers() =>
            Character.ForEachFollower(follower =>
            {
                if (follower.Target == null || follower.Target.Instance != Character.Instance)
                {
                    Character.RemoveFollower(follower);
                    return;
                }

                follower.Follow(Character);
            });
    }
}
