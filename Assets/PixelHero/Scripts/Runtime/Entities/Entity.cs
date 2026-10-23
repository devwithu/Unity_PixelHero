using System;
using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>
    /// client/src/entity/entity.ts. Numbers that the original can leave undefined (unset in a packet) are
    /// <c>double?</c>, null standing for undefined; positions are doubles like JavaScript numbers.
    /// </summary>
    public abstract class Entity
    {
        protected readonly World World;

        public string Instance;
        public int Type;

        public double X;
        public double Y;
        public int GridX;
        public int GridY;
        public int NextGridX = -1;
        public int NextGridY = -1;
        public double? MovementSpeed = -1;
        public double? AttackRange = -1;
        public bool Frozen;
        public bool Dead;
        public string Name = "";
        public int Region = -1;
        public bool HealthBarVisible;
        public EntitySprite Sprite;
        public bool SpriteFlipX;
        public bool SpriteFlipY;
        public Animation Animation;
        public double OffsetY;
        public double ShadowOffsetY;
        public bool Hidden;
        bool _visible = true;
        public bool Exclamation;
        public bool BlueExclamation;
        public bool Fading;
        public bool Hurt;
        public bool Silhouette;
        public double Angle;
        public double Counter;
        int _counterInterval;
        public virtual double FadingDuration => 1000;
        public int Orientation = (int)Modules.Orientation.Down;
        public double FadingTime;
        int _blinking;
        public bool Ready;
        public double? HitPoints = 0;
        public double? MaxHitPoints = 0;
        public double? Mana = 0;
        public double? MaxMana = 0;
        public double? Level = 1;
        public bool Teleporting;
        public string NameColour = "";
        /// <summary>Undefined until display info sets it.</summary>
        public double? CustomScale;
        public double FadingAlpha;
        public double LastUpdate;
        /// <summary>Set by game.ts searchForEntityAt on the entity it picks.</summary>
        public double Distance = -1;

        protected Entity(World world, string instance, int type)
        {
            World = world;
            Instance = instance;
            Type = type;
            LastUpdate = world.Clock.Now;
        }

        public void FadeIn(double time)
        {
            Fading = true;
            FadingTime = time;
        }

        /// <summary>
        /// entity.ts setCountdown: counts down once a second until 0. A second countdown adds another interval, and
        /// the one that reaches 0 first clears only the latest (the original's).
        /// </summary>
        public void SetCountdown(double count)
        {
            Counter = count;
            _counterInterval = World.Timers.SetInterval(() =>
            {
                if (Counter <= 0)
                {
                    World.Timers.Clear(_counterInterval);
                    _counterInterval = 0;
                    return;
                }
                Counter--;
            }, 1000);
        }

        public void Blink(double speed = 150) => _blinking = World.Timers.SetInterval(ToggleVisibility, speed);

        protected void StopBlinking()
        {
            if (_blinking != 0) World.Timers.Clear(_blinking);
            SetVisible(true);
        }

        public virtual void Idle() { }

        public void AnimateDeath(Action callback = null, double speed = 120, int count = 1) =>
            SetAnimation("death", speed, count, callback);

        /// <summary>entity.ts updateDisplayInfo (falsy values are ignored).</summary>
        public void UpdateDisplayInfo(Dictionary<string, object> displayInfo)
        {
            if (displayInfo.TryGetValue("colour", out var colour) && colour is string c && c.Length > 0) NameColour = c;
            if (displayInfo.TryGetValue("scale", out var scale) && scale is double s && s != 0 && !double.IsNaN(s)) CustomScale = s;

            displayInfo.TryGetValue("exclamation", out var exclamation);
            if (exclamation as string == "achievement") Exclamation = true;
            else if (exclamation as string == "blue") BlueExclamation = true;
        }

        /// <summary>entity.ts setSprite.</summary>
        public virtual void SetSprite(EntitySprite sprite)
        {
            if (Teleporting) return;

            if (!sprite.Loaded)
            {
                sprite.Load();
                if (Sprite?.Key == sprite.Key) return;
            }

            Sprite = sprite;

            sprite.OnLoad(() =>
            {
                if (CustomScale == null || CustomScale == 0 || double.IsNaN(CustomScale.Value)) return;
                Sprite.OffsetX *= CustomScale.Value;
                Sprite.OffsetY *= CustomScale.Value;
            });
        }

        /// <summary>
        /// entity.ts setAnimation: keeps a running animation of the same name; a missing name throws like the
        /// original's destructuring of undefined.
        /// </summary>
        public virtual void SetAnimation(string name, double? speed = null, int count = 1, Action onEndCount = null, bool withStop = false)
        {
            if (Animation?.Name == name) return;

            if (!Sprite.Animations.TryGetValue(name, out var data))
                throw new InvalidOperationException($"TypeError: sprite {Sprite.Key} has no animation '{name}'");

            Animation = new Animation(World.Clock, name, data.Length, data.Row, data.Width, data.Height, withStop);

            if (name.StartsWith("atk")) Animation.Reset();

            Animation.SetSpeed(speed ?? Sprite.IdleSpeed);
            Animation.SetCount(count, onEndCount ?? (() => Idle()));
        }

        public virtual void SetGridPosition(int gridX, int gridY)
        {
            GridX = gridX;
            GridY = gridY;
            Region = World.GetRegion(gridX, gridY);
            X = gridX * World.TileSize;
            Y = gridY * World.TileSize;
        }

        public void SetVisible(bool visible) => _visible = visible;

        public bool IsVisible() => _visible;

        void ToggleVisibility() => SetVisible(!_visible);

        /// <summary>entity.ts updateSilhouette (the silhouette sprite itself is made by the renderer stage).</summary>
        public virtual void UpdateSilhouette(bool state = false)
        {
            if (Dead || Teleporting)
            {
                Silhouette = false;
                return;
            }

            if (state && Sprite.HasSilhouette && !Sprite.SilhouetteVariant) Sprite.LoadSilhouetteSprite();

            Silhouette = state;
        }

        /// <summary>entity.ts getSprite: the hurt image while hurt, else the silhouette image while hovered.</summary>
        public SpriteVariant GetSpriteVariant()
        {
            if (Hurt && Sprite.HurtVariant) return SpriteVariant.Hurt;
            if (Silhouette && Sprite.SilhouetteVariant) return SpriteVariant.Silhouette;
            return SpriteVariant.Normal;
        }

        /// <summary>entity.ts getBoundingBox.</summary>
        public (double x, double y, double width, double height) GetBoundingBox() =>
            (X + Sprite.OffsetX / 2, Y + Sprite.OffsetY / 2, Sprite.Width + Sprite.OffsetX, Sprite.Height + Sprite.OffsetY);

        public int GetDistance(Entity entity) => Math.Abs(GridX - entity.GridX) + Math.Abs(GridY - entity.GridY);

        public virtual bool HasShadow() => false;

        public virtual bool DrawNames() => true;

        public bool IsPlayer => Type == (int)Modules.EntityType.Player;
        public bool IsMob => Type == (int)Modules.EntityType.Mob;
        public bool IsNPC => Type == (int)Modules.EntityType.NPC;
        public bool IsItem => Type == (int)Modules.EntityType.Item;
        public bool IsChest => Type == (int)Modules.EntityType.Chest;
        public bool IsProjectile => Type == (int)Modules.EntityType.Projectile;
        public bool IsPet => Type == (int)Modules.EntityType.Pet;
        public bool IsTree => Type == (int)Modules.EntityType.Tree;
        public bool IsRock => Type == (int)Modules.EntityType.Rock;
        public bool IsFishSpot => Type == (int)Modules.EntityType.FishSpot;
        public bool IsForaging => Type == (int)Modules.EntityType.Foraging;
        public bool IsNonTreeResource => IsRock || IsForaging;
        public bool IsResource => IsTree || IsRock || IsFishSpot || IsForaging;
        public bool IsObject => Type == (int)Modules.EntityType.Object;
    }

    /// <summary>Which image of a sprite is drawn (entity.ts getSprite).</summary>
    public enum SpriteVariant
    {
        Normal,
        Hurt,
        Silhouette
    }

    public sealed class Npc : Entity
    {
        public Npc(World world, string instance) : base(world, instance, (int)Modules.EntityType.NPC) { }
        public override void Idle() => SetAnimation("idle_down", Sprite.IdleSpeed);
        public override bool HasShadow() => true;
    }

    public sealed class Chest : Entity
    {
        public Chest(World world, string instance) : base(world, instance, (int)Modules.EntityType.Chest) { }
        public override void Idle() => SetAnimation("idle_down", 150);
    }

    public sealed class Item : Entity
    {
        public bool Dropped;
        public double OriginalY = -1;
        public double? ItemCount;
        public object Enchantments;

        public Item(World world, string instance, double? count, object enchantments) : base(world, instance, (int)Modules.EntityType.Item)
        {
            ItemCount = count ?? 1;
            Enchantments = enchantments;
        }

        public override void SetAnimation(string name, double? speed = null, int count = 1, Action onEndCount = null, bool withStop = false)
        {
            base.SetAnimation(name, speed, count, onEndCount);
            if (Animation.Length > 1) return;

            OriginalY = Sprite.OffsetY;
            Animation.OnBop(bopIndex => OffsetY = OriginalY - bopIndex);
        }

        public override void Idle() => SetAnimation("idle", 150);
        public override bool HasShadow() => true;
    }

    public sealed class Effect : Entity
    {
        public Effect(World world, string instance) : base(world, instance, (int)Modules.EntityType.Effect) { }
        public override void Idle() => SetAnimation("idle", 150, 1, () => { }, true);
    }

    public abstract class Resource : Entity
    {
        public bool Exhausted;

        protected Resource(World world, string instance, Modules.EntityType type) : base(world, instance, (int)type) { }

        public void Shake() => SetAnimation("shake", 150, 1, Idle);

        public override void Idle() => SetAnimation(Exhausted ? "exhausted" : "idle", 150, 1, () => { });

        public override void UpdateSilhouette(bool state = false)
        {
            if (Exhausted)
            {
                Silhouette = false;
                return;
            }

            base.UpdateSilhouette(state);
        }

        public void SetExhausted(bool state = false)
        {
            Exhausted = state;
            Idle();
        }
    }

    public sealed class Tree : Resource
    {
        public AnimationFrame BaseFrame;
        public AnimationFrame ExhaustedFrame;

        public Tree(World world, string instance) : base(world, instance, Modules.EntityType.Tree) { }

        public override void SetSprite(EntitySprite sprite)
        {
            base.SetSprite(sprite);
            BaseFrame = Sprite.Animations["exhausted"].Frame;
            ExhaustedFrame = Sprite.Animations["exhausted"].GetSecondFrame();
        }
    }

    public sealed class Rock : Resource
    {
        public Rock(World world, string instance) : base(world, instance, Modules.EntityType.Rock) { }
    }

    public sealed class FishSpot : Resource
    {
        public FishSpot(World world, string instance) : base(world, instance, Modules.EntityType.FishSpot) { }
    }

    public sealed class Foraging : Resource
    {
        public Foraging(World world, string instance) : base(world, instance, Modules.EntityType.Foraging) { }
    }

    public sealed class Projectile : Entity
    {
        public override double FadingDuration => 100;
        public double Speed = 150;
        public bool HasSpawned;
        public Character Target;
        public Action ImpactCallback;
        readonly int _hitType;

        public Projectile(World world, string instance, int hitType) : base(world, instance, (int)Modules.EntityType.Projectile) => _hitType = hitType;

        public override void Idle() => SetAnimation("travel");

        public void SetTarget(Character target)
        {
            Target = target;
            UpdateAngle();
        }

        public override void SetGridPosition(int gridX, int gridY)
        {
            if (HasSpawned) return;
            base.SetGridPosition(gridX, gridY);
            HasSpawned = true;
        }

        public double GetTimeDiff() => (World.Clock.Now - LastUpdate) / 1000;

        public int GetImpactEffect()
        {
            if (_hitType == (int)Modules.Hits.Explosive) return (int)Modules.Effects.Fireball;

            switch (Sprite.Key)
            {
                case "projectiles/boulder": return (int)Modules.Effects.Boulder;
                case "projectiles/poisonball": return (int)Modules.Effects.Poisonball;
                case "projectiles/fireball":
                case "projectiles/gift6": return (int)Modules.Effects.Fireball;
                case "projectiles/iceball": return (int)Modules.Effects.Iceball;
                case "projectiles/terror": return (int)Modules.Effects.Terror;
            }

            return (int)Modules.Effects.None;
        }

        public void UpdateAngle()
        {
            if (Target == null) return;
            Angle = Fdlibm.Atan2(Target.Y - Y, Target.X - X) - Math.PI / 2;
        }

        public void OnImpact(Action callback) => ImpactCallback = callback;
    }
}
