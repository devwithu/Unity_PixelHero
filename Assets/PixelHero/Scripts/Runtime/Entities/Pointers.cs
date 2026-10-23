using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>
    /// renderer/pointers/arrow.ts: one blinking arrow (div.pointer, 4.5rem square) and the inline styles
    /// PointerController.set gives it. Every 600 ms the arrow toggles between fading out and fading in
    /// (<see cref="Visible"/>; the view animates the opacity).
    /// </summary>
    public sealed class PointerArrow
    {
        /// <summary>The hardcoded half width of the arrow element.</summary>
        public const double OffsetWidth = 36;

        public readonly Opcodes.Pointer Type;
        public readonly string Instance;
        public double X = -1, Y = -1;

        /// <summary>element.style left / top / right / bottom / transform ('' = unset).</summary>
        public string Left = "", Top = "", Right = "", Bottom = "", Transform = "";

        /// <summary>The blink state: true while fading in, false while fading out.</summary>
        public bool Visible = true;

        public int BlinkTimer;

        public PointerArrow(Opcodes.Pointer type, string instance)
        {
            Type = type;
            Instance = instance;
        }

        public void SetPosition(double x, double y)
        {
            X = x;
            Y = y;
        }

        public void Reset() => Left = Top = Right = Bottom = Transform = "";

        public override string ToString() =>
            $"{Instance ?? "undefined"} {(int)Type} left={Left} top={Top} right={Right} bottom={Bottom} transform={Transform}";
    }

    /// <summary>
    /// controllers/pointer.ts: quest pointers over an entity or a map position. On screen an arrow sits above the
    /// target; off screen it sticks to the edge it is past, turned towards the target. Positions are in canvas pixels
    /// (#bubbles is laid over the canvas).
    /// </summary>
    public sealed class Pointers
    {
        readonly World _world;
        readonly OrderedMap<PointerArrow> _pointers = new OrderedMap<PointerArrow>();
        double _scale = 1;

        /// <summary>renderer.canvasWidth / canvasHeight.</summary>
        public double CanvasWidth, CanvasHeight;

        public Pointers(World world) => _world = world;

        public IReadOnlyList<PointerArrow> All => _pointers.Values;

        /// <summary>connection.ts handlePointer.</summary>
        public void Handle(Opcodes.Pointer opcode, Dictionary<string, object> info)
        {
            var instance = info != null && info.TryGetValue("instance", out var value) ? value as string : null;
            switch (opcode)
            {
                case Opcodes.Pointer.Entity:
                    if (_world.Entities.Get(instance) == null) return;
                    Create(opcode, instance);
                    break;
                case Opcodes.Pointer.Location:
                    double Number(string key) => info.TryGetValue(key, out var n) && n is double d ? d : double.NaN;
                    Create(opcode, instance, Number("x") * _world.TileSize, Number("y") * _world.TileSize);
                    break;
                case Opcodes.Pointer.Remove:
                    Clean();
                    break;
            }
        }

        public void Create(Opcodes.Pointer type, string instance, double x = -1, double y = -1)
        {
            var key = instance ?? "undefined";
            if (_pointers.ContainsKey(key)) return;

            var pointer = new PointerArrow(type, instance);
            // Arrow's constructor: the blink interval.
            pointer.BlinkTimer = _world.Timers.SetInterval(() => pointer.Visible = !pointer.Visible, 600);
            pointer.SetPosition(x, y);
            _pointers[key] = pointer;
        }

        void Set(PointerArrow pointer, double x, double y)
        {
            pointer.SetPosition(x, y);

            var camera = _world.Camera;
            var actualTileSize = _world.TileSize * camera.ZoomFactor;
            var relativeX = (x - camera.X) * camera.ZoomFactor;
            var relativeY = (y - camera.Y) * camera.ZoomFactor - actualTileSize;
            var boundaryX = relativeX / CanvasWidth;
            var boundaryY = relativeY / CanvasHeight;

            pointer.Reset();

            var transform = $"scale({Js(_scale)})";

            if (boundaryX >= 0 && boundaryX <= 1 && boundaryY >= 0 && boundaryY <= 1)
            {
                var offsetX = PointerArrow.OffsetWidth - actualTileSize / 2;
                pointer.Left = Px(relativeX - offsetX);
                pointer.Top = Px(relativeY);
                pointer.Transform = transform;
                return;
            }

            if (boundaryX > 1)
            {
                pointer.Right = "0";
                pointer.Top = boundaryY > 1 ? "" : boundaryY < 0 ? "0" : Px(relativeY);
                pointer.Bottom = boundaryY > 1 ? "0" : "";
                pointer.Transform = $"{transform} rotate(-90deg)";
            }
            else if (boundaryX < 0)
            {
                pointer.Left = "0";
                pointer.Top = boundaryY > 1 ? "" : boundaryY < 0 ? "0" : Px(relativeY);
                pointer.Bottom = boundaryY > 1 ? "0" : "";
                pointer.Transform = $"{transform} rotate(90deg)";
            }
            else if (boundaryY > 1)
            {
                pointer.Bottom = "0";
                pointer.Left = boundaryX > 1 ? "" : boundaryX < 0 ? "0" : Px(relativeX);
                pointer.Right = boundaryX > 1 ? "0" : "";
                pointer.Transform = transform;
            }
            else if (boundaryY < 0)
            {
                pointer.Top = "0";
                pointer.Left = boundaryX > 1 ? "" : boundaryX < 0 ? "0" : Px(relativeX);
                pointer.Right = boundaryX > 1 ? "0" : "";
                pointer.Transform = $"{transform} rotate(180deg)";
            }
        }

        static string Js(double value) => JsNumber.ToJsString(value);

        /// <summary>A style length: `${value}px` (the browser drops a NaN assignment, keeping the reset '').</summary>
        static string Px(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "" : $"{Js(value)}px";

        /// <summary>pointer.ts update (from updater.ts updateBubbles).</summary>
        public void Update()
        {
            foreach (var pointer in _pointers.Values)
            {
                if (pointer.Type == Opcodes.Pointer.Location)
                {
                    Set(pointer, pointer.X, pointer.Y);
                    continue;
                }

                if (pointer.Type == Opcodes.Pointer.Entity)
                {
                    var entity = _world.Entities.Get(pointer.Instance);
                    if (entity != null) Set(pointer, entity.X, entity.Y);
                    else Remove(pointer.Instance);
                }
            }
        }

        /// <summary>The arrow's scale follows the zoom (relative to the default zoom).</summary>
        public void Resize() => _scale = _world.Camera.ZoomFactor / Camera.DefaultZoom;

        public void Clean()
        {
            foreach (var key in _pointers.Keys) Remove(key);
        }

        void Remove(string instance)
        {
            var key = instance ?? "undefined";
            var pointer = _pointers[key];
            if (pointer != null) _world.Timers.Clear(pointer.BlinkTimer);
            _pointers.Remove(key);
        }
    }
}
