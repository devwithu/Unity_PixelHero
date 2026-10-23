using System;
using System.Collections.Generic;
using PixelHero.Js;
using PixelHero.Sprites;

namespace PixelHero.Entities
{
    /// <summary>
    /// A sprite as the client holds it at run time (client/src/entity/sprite.ts): resolved definition, the
    /// animations it owns (shared by everyone using the sprite), loading state, and offsets that a custom-scaled
    /// entity may multiply in place when the image loads (the original mutates the shared sprite).
    /// </summary>
    public sealed class EntitySprite
    {
        readonly JsTimers _timers;
        readonly Func<string, bool> _imageExists;

        public readonly SpriteInfo Info;
        public string Key => Info.Key;
        public int Width => Info.Width;
        public int Height => Info.Height;
        public double OffsetX;
        public double OffsetY;
        public int IdleSpeed => Info.IdleSpeed;
        public bool Preload => Info.Preload;
        public bool HasDeathAnimation => Info.HasDeathAnimation;
        public bool Loaded;

        /// <summary>sprite.image exists once load() has been called, whether or not the file loads.</summary>
        public bool ImageCreated;

        /// <summary>The URL the client loads: /img/sprites/{key}.png.</summary>
        public string Path => Info.Path;

        /// <summary>Name → animation, in definition order.</summary>
        public readonly Dictionary<string, Animation> Animations = new Dictionary<string, Animation>();

        Action _loadCallback;

        public EntitySprite(SpriteInfo info, JsTimers timers, Func<string, bool> imageExists)
        {
            Info = info;
            _timers = timers;
            _imageExists = imageExists;
            OffsetX = info.OffsetX;
            OffsetY = info.OffsetY;

            foreach (var animation in info.Animations)
                Animations[animation.Name] = new Animation(timers.Clock, animation.Name, animation.Length, animation.Row, info.Width, info.Height);
        }

        /// <summary>
        /// sprite.ts load: a new Image every call until one has loaded; each image that loads sets the flag and calls
        /// the load callback (one turn later, like the browser's load event). Missing images never load.
        /// </summary>
        public void Load()
        {
            if (Loaded) return;
            ImageCreated = true;
            if (!_imageExists(Key)) return;

            _timers.SetTimeout(() =>
            {
                Loaded = true;
                _loadCallback?.Invoke();
            });
        }

        public void OnLoad(Action callback) => _loadCallback = callback;

        public bool HasHurtSprite => SpriteInfo.TypeOf(Key) == "mobs" || SpriteInfo.TypeOf(Key) == "player";

        /// <summary>sprite.hurtSprite exists (made once by loadHurtSprite: visible pixels turned red).</summary>
        public bool HurtVariant;

        /// <summary>sprite.silhouetteSprite exists (made once by loadSilhouetteSprite: a yellow outline).</summary>
        public bool SilhouetteVariant;

        /// <summary>sprite.ts loadHurtSprite.</summary>
        public void LoadHurtSprite()
        {
            if (HurtVariant || !HasHurtSprite || !Loaded) return;
            HurtVariant = true;
        }

        /// <summary>sprite.ts loadSilhouetteSprite (desktop: isMobile() is false).</summary>
        public void LoadSilhouetteSprite()
        {
            if (SilhouetteVariant || !HasSilhouette || !Loaded) return;
            SilhouetteVariant = true;
        }

        public bool HasSilhouette => Info.HasSilhouette;
    }

    /// <summary>client/src/controllers/sprites.ts: every sprites.json entry built up front, preloads started.</summary>
    public sealed class SpritesController
    {
        public readonly Dictionary<string, EntitySprite> Sprites = new Dictionary<string, EntitySprite>();
        public readonly List<Animation> PreloadedAnimations = new List<Animation>();

        public SpritesController(IEnumerable<SpriteInfo> definitions, JsTimers timers, Func<string, bool> imageExists)
        {
            foreach (var info in definitions)
            {
                var sprite = new EntitySprite(info, timers, imageExists);
                Sprites[sprite.Key] = sprite;

                if (!sprite.Preload) continue;

                sprite.Load();
                if (sprite.Animations.TryGetValue("idle_down", out var idleDown) && idleDown.Length > 1)
                {
                    PreloadedAnimations.Add(idleDown);
                    idleDown.SetSpeed(sprite.IdleSpeed != 0 ? sprite.IdleSpeed : 200);
                }
            }
        }

        public EntitySprite Get(string name) => name != null && Sprites.TryGetValue(name, out var sprite) ? sprite : null;

        public EntitySprite GetDeath() => Get("death");
    }
}
