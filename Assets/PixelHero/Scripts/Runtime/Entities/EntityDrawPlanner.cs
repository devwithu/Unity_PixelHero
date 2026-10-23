using System;
using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>The entity canvases of the renderer (components/game.astro).</summary>
    public enum EntityCanvas
    {
        /// <summary>#entities: entities, below the foreground tiles.</summary>
        Entities,
        /// <summary>#entities-fore: tree tops and resources south of the player, above #entities.</summary>
        Fore,
        /// <summary>#entities-mask: a copy of #entities shown at 20% opacity above the foreground tiles.</summary>
        Mask
    }

    /// <summary>
    /// One drawImage of the entity renderer, with the 2D context state in effect: source and destination rects in
    /// the context's local space, the transform (canvas setTransform order a b c d e f), alpha and composite mode.
    /// <see cref="Sprite"/> is null for the mask copy (the whole #entities canvas).
    /// </summary>
    public sealed class EntityDraw
    {
        public EntityCanvas Canvas;
        public EntitySprite Sprite;
        /// <summary>Hurt / silhouette images are canvases the client generates from the sprite.</summary>
        public SpriteVariant Variant;
        public double Sx, Sy, Sw, Sh, Dx, Dy, Dw, Dh;
        public double A, B, C, D, E, F;
        public double Alpha;
        public string Composite;
    }

    /// <summary>A 2D context's transform / alpha / composite state with save and restore, recording drawImage.</summary>
    public sealed class DrawContext
    {
        struct State
        {
            public double A, B, C, D, E, F, Alpha;
            public string Composite;
        }

        readonly EntityCanvas _canvas;
        readonly Action<EntityDraw> _sink;
        State _state;
        readonly Stack<State> _stack = new Stack<State>();

        public DrawContext(EntityCanvas canvas, Action<EntityDraw> sink)
        {
            _canvas = canvas;
            _sink = sink;
            Reset();
        }

        public void Reset()
        {
            _state = new State { A = 1, D = 1, Alpha = 1, Composite = "source-over" };
            _stack.Clear();
        }

        public double GlobalAlpha { get => _state.Alpha; set => _state.Alpha = value; }
        public string GlobalCompositeOperation { get => _state.Composite; set => _state.Composite = value; }

        public void Save() => _stack.Push(_state);

        public void Restore()
        {
            if (_stack.Count > 0) _state = _stack.Pop();
        }

        public void Translate(double x, double y) => Multiply(1, 0, 0, 1, x, y);

        public void Scale(double x, double y) => Multiply(x, 0, 0, y, 0, 0);

        public void Rotate(double angle) => Multiply(Fdlibm.Cos(angle), Fdlibm.Sin(angle), -Fdlibm.Sin(angle), Fdlibm.Cos(angle), 0, 0);

        void Multiply(double a, double b, double c, double d, double e, double f)
        {
            var m = _state;
            _state.A = m.A * a + m.C * b;
            _state.B = m.B * a + m.D * b;
            _state.C = m.A * c + m.C * d;
            _state.D = m.B * c + m.D * d;
            _state.E = m.A * e + m.C * f + m.E;
            _state.F = m.B * e + m.D * f + m.F;
        }

        public void DrawImage(EntitySprite sprite, double sx, double sy, double sw, double sh, double dx, double dy, double dw, double dh,
            SpriteVariant variant = SpriteVariant.Normal) =>
            _sink(new EntityDraw
            {
                Canvas = _canvas, Sprite = sprite, Variant = variant,
                Sx = sx, Sy = sy, Sw = sw, Sh = sh, Dx = dx, Dy = dy, Dw = dw, Dh = dh,
                A = _state.A, B = _state.B, C = _state.C, D = _state.D, E = _state.E, F = _state.F,
                Alpha = _state.Alpha, Composite = _state.Composite
            });
    }

    /// <summary>
    /// client/src/renderer/renderer.ts drawEntities and what it calls (drawEntity, drawTree, drawEntityFore,
    /// drawEquipment, drawSprite, drawEffects, drawSparks, drawExclamation), call for call. Text (names, levels,
    /// health bars) is reported through <see cref="TextCall"/> for the UI stage to draw.
    /// </summary>
    public sealed class EntityDrawPlanner
    {
        readonly World _world;
        readonly Camera _camera;
        readonly List<EntityDraw> _draws = new List<EntityDraw>();
        readonly DrawContext _entities;
        readonly DrawContext _fore;
        readonly DrawContext _mask;

        /// <summary>game.overlays.hasOverlay() — names are hidden in dark areas.</summary>
        public Func<bool> HasOverlay;

        /// <summary>(kind, entity) for drawHealth / drawName / drawPlayerName, in call order.</summary>
        public Action<string, Entity> TextCall = (_, __) => { };

        /// <summary>Every drawImage as it happens (interleaves with <see cref="TextCall"/> in call order).</summary>
        public Action<EntityDraw> DrawRecorded = _ => { };

        public EntityDrawPlanner(World world, Camera camera)
        {
            _world = world;
            _camera = camera;
            HasOverlay = world.Lights.HasOverlay;
            void Sink(EntityDraw draw)
            {
                _draws.Add(draw);
                DrawRecorded(draw);
            }

            _entities = new DrawContext(EntityCanvas.Entities, Sink);
            _fore = new DrawContext(EntityCanvas.Fore, Sink);
            _mask = new DrawContext(EntityCanvas.Mask, Sink);
        }

        double Zoom => _camera.ZoomFactor;
        double ActualTileSize => _world.TileSize * Zoom;

        /// <summary>One frame: the renderer clears and saves the contexts, draws the entities, and restores.</summary>
        public List<EntityDraw> Draw()
        {
            _draws.Clear();
            foreach (var context in new[] { _entities, _fore, _mask })
            {
                context.Reset();
                context.Save();
            }

            DrawEntities();

            foreach (var context in new[] { _entities, _fore, _mask }) context.Restore();
            return new List<EntityDraw>(_draws);
        }

        void SetCameraView(DrawContext context) =>
            context.Translate(JsValue.ToInt32(-_camera.X * Zoom), JsValue.ToInt32(-_camera.Y * Zoom));

        void DrawEntities()
        {
            if (_world.Player.Dead) return;

            SetCameraView(_entities);
            SetCameraView(_fore);

            _camera.ForEachVisiblePosition((x, y) =>
            {
                foreach (var entity in _world.Entities.Grid.At(x, y))
                {
                    if (entity.Sprite == null || !entity.Sprite.Loaded || entity.Animation == null || !entity.IsVisible()) continue;

                    if (entity is Tree tree) DrawTree(tree);
                    else DrawEntity(entity);
                }
            }, 3, 6);

            // entitiesMaskContext.drawImage(this.entities, 0, 0): the whole #entities canvas.
            _mask.DrawImage(null, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        void DrawEntity(Entity entity)
        {
            var frame = entity.Animation.Frame;
            double dx = JsValue.ToInt32(entity.X * Zoom);
            double dy = JsValue.ToInt32(entity.Y * Zoom);
            var flipX = dx + ActualTileSize;
            var flipY = dy + entity.Sprite.Height;
            var context = entity.IsNonTreeResource && _world.Player.GridY < entity.GridY ? _fore : _entities;

            context.Save();

            if (entity.Fading) context.GlobalAlpha = entity.FadingAlpha;

            if (entity.SpriteFlipX)
            {
                context.Translate(flipX, dy);
                context.Scale(-1, 1);
            }
            else if (entity.SpriteFlipY)
            {
                context.Translate(dx, flipY);
                context.Scale(1, -1);
            }
            else context.Translate(dx, dy);

            context.Scale(Zoom, Zoom);

            if (entity.CustomScale is double scale && scale != 0 && !double.IsNaN(scale)) context.Scale(scale, scale);

            if (entity.Angle != 0) context.Rotate(entity.Angle);

            if (entity.HasShadow())
            {
                var shadow = _world.Sprites.Get("shadow");
                context.DrawImage(shadow, 0, 0, shadow.Width, shadow.Height, 0, entity.ShadowOffsetY, shadow.Width, shadow.Height);
            }

            context.DrawImage(entity.Sprite, frame.X, frame.Y, entity.Sprite.Width, entity.Sprite.Height,
                entity.Sprite.OffsetX, entity.Sprite.OffsetY + entity.OffsetY, entity.Sprite.Width, entity.Sprite.Height,
                entity.GetSpriteVariant());

            DrawEntityFore(entity);

            context.Restore();

            TextCall("health", entity);

            if (!entity.IsPlayer && !entity.IsMob && !entity.IsNPC && !entity.IsItem) return;

            if (!HasOverlay())
                TextCall(_world.Player.Instance == entity.Instance && _camera.IsCentered() ? "playerName" : "name", entity);
        }

        void DrawTree(Tree entity)
        {
            var frame = entity.Animation.Frame;
            var baseFrame = entity.Exhausted ? entity.ExhaustedFrame : entity.BaseFrame;
            double dx = JsValue.ToInt32(entity.X * Zoom);
            double dy = JsValue.ToInt32(entity.Y * Zoom);

            _fore.Save();
            _entities.Save();

            _fore.Translate(dx, dy);
            _entities.Translate(dx, dy);

            _fore.Scale(Zoom, Zoom);
            _entities.Scale(Zoom, Zoom);

            var variant = entity.GetSpriteVariant();

            if (!entity.Exhausted)
                _fore.DrawImage(entity.Sprite, frame.X, frame.Y, entity.Sprite.Width, entity.Sprite.Height,
                    entity.Sprite.OffsetX, entity.Sprite.OffsetY + entity.OffsetY, entity.Sprite.Width, entity.Sprite.Height, variant);

            _entities.GlobalCompositeOperation = "destination-over";
            _entities.DrawImage(entity.Sprite, baseFrame.X, baseFrame.Y, entity.Sprite.Width, entity.Sprite.Height,
                entity.Sprite.OffsetX, entity.Sprite.OffsetY + entity.OffsetY, entity.Sprite.Width, entity.Sprite.Height, variant);

            _fore.Restore();
            _entities.Restore();
        }

        /// <summary>renderer.ts drawEntityFore: always on #entities, in that context's current state.</summary>
        void DrawEntityFore(Entity entity)
        {
            if (entity.IsItem)
            {
                DrawPreloaded("sparks", 0);
                return;
            }

            if (entity.Exclamation) DrawPreloaded("exclamation", null);
            if (entity.BlueExclamation) DrawPreloaded("exclamationblue", null);

            if (!(entity is Character character)) return;

            if (character is Player player)
                foreach (var (equipment, _) in player.DrawnEquipments())
                    DrawEquipment(player, equipment);

            if (character.HasActiveEffect()) DrawEffects(character);
        }

        /// <summary>drawSparks (dy 0) and drawExclamation / drawBlueExclamation (dy = sprite.offsetY).</summary>
        void DrawPreloaded(string key, double? y)
        {
            var sprite = _world.Sprites.Get(key);
            var animation = sprite.Animations["idle_down"];
            _entities.DrawImage(sprite, sprite.Width * animation.Frame.Index, sprite.Height * animation.Row, sprite.Width, sprite.Height,
                0, y ?? sprite.OffsetY, sprite.Width, sprite.Height);
        }

        void DrawEquipment(Player player, Equipment equipment)
        {
            if (player.Dead || player.Teleporting) return;

            var sprite = _world.Sprites.Get(equipment.Key);
            if (sprite == null) return;

            if (!sprite.Loaded) sprite.Load();
            if (!sprite.ImageCreated) return;

            DrawSprite(player, sprite);
        }

        void DrawSprite(Character character, EntitySprite sprite)
        {
            var animation = character.Animation;
            if (!sprite.Animations.TryGetValue(animation.Name, out var data)) return;

            var index = animation.Frame.Index < data.Length ? animation.Frame.Index : animation.Frame.Index % data.Length;

            _entities.DrawImage(sprite, sprite.Width * index, sprite.Height * animation.Row, sprite.Width, sprite.Height,
                sprite.OffsetX, sprite.OffsetY, sprite.Width, sprite.Height);
        }

        void DrawEffects(Character character)
        {
            foreach (var key in character.StatusEffects.ToArray())
            {
                if (key == (int)Modules.Effects.Freezing && character.HasEffect((int)Modules.Effects.SnowPotion)) continue;
                if (key == (int)Modules.Effects.Burning && character.HasEffect((int)Modules.Effects.FirePotion)) continue;

                var effect = character.GetEffect(key);
                if (effect == null) continue;

                var sprite = _world.Sprites.Get(effect.Key);
                if (!sprite.Loaded) sprite.Load();

                var animation = effect.Animation;
                _entities.DrawImage(sprite, sprite.Width * animation.Frame.Index, sprite.Height * animation.Row, sprite.Width, sprite.Height,
                    sprite.OffsetX, sprite.OffsetY, sprite.Width, sprite.Height);

                animation.Update(_world.Time);
            }
        }
    }
}
