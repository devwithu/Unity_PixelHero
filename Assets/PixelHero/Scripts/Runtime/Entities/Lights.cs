using System;
using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>One illuminated Lamp as renderer.ts extends it (position in overlay-canvas pixels).</summary>
    public sealed class Lamp
    {
        public double X, Y;
        public double Distance, Diffuse;
        public string Colour;
        public double GridX, GridY;
        public double Offset;
        public double OriginalX, OriginalY, OriginalDistance;
        /// <summary>resizeLights sets these; until then they are undefined (null).</summary>
        public double? ScaledDistance, ScaledFlickerIntensity;
        public string Entity;
        public double? FlickerSpeed, FlickerIntensity;
        public bool Hidden;
    }

    /// <summary>
    /// One stamp of the dark mask: illuminated's Lamp.mask draws its visible-mask cache (a radial gradient of radius
    /// <see cref="Radius"/>, opaque at the centre) with destination-out at (X, Y). NaN when the lamp's distance is NaN
    /// (see <see cref="Lights"/>): the original throws there, the port's view skips the stamp.
    /// </summary>
    public readonly struct LightStamp
    {
        public readonly double Radius, X, Y;

        public LightStamp(double radius, double x, double y)
        {
            Radius = radius;
            X = x;
            Y = y;
        }

        public bool IsFinite => !double.IsNaN(Radius) && !double.IsInfinity(Radius);

        public override string ToString() => $"{JsNumber.ToJsString(Radius)} {JsNumber.ToJsString(X)} {JsNumber.ToJsString(Y)}";
    }

    /// <summary>
    /// The overlay system of the client: overlays.ts (the current overlay image), and the lighting part of renderer.ts
    /// (lightings, the dark mask, addLight / addPlayerLight / updatePlayerLight / resizeLights / removeAllLights /
    /// updateDarkMask, drawLighting) plus updater.ts updateLights (flicker).
    /// <list type="bullet">
    /// <item>Only the dark mask is drawn. drawLighting computes positions and visibility but its lighting.render is
    /// commented out in the original, so the lamps' colours never show.</item>
    /// <item>A lamp added after Overlay Set has no scaled distance until the next resizeLights. If it flickers, its
    /// distance is then NaN. A player with a lantern who appears while the overlay is on is such a lamp. The
    /// original's createRadialGradient then throws every frame: render() stops after the overlay, so entities and
    /// text vanish until something resizes the lights (ANALYSIS.md §9 #11). The port keeps the NaN state and its view
    /// skips the stamp instead.</item>
    /// </list>
    /// </summary>
    public sealed class Lights
    {
        readonly World _world;

        /// <summary>overlays.ts currentOverlay ('' = none). Only "fog" has an image.</summary>
        public string CurrentOverlay = "";

        /// <summary>darkMask.color.</summary>
        public string DarkColour = "rgba(0, 0, 0, 0.84)";

        /// <summary>renderer.lightings (keyed by instance, JS key order).</summary>
        public readonly OrderedMap<Lamp> Lightings = new OrderedMap<Lamp>();

        /// <summary>darkMask.lights (in the order they were pushed).</summary>
        public readonly List<Lamp> MaskLights = new List<Lamp>();

        /// <summary>The #overlay canvas size in pixels.</summary>
        public double CanvasWidth, CanvasHeight;

        public Lights(World world) => _world = world;

        double TileSize => _world.TileSize;
        Camera Camera => _world.Camera;

        public bool HasOverlay() => CurrentOverlay != "";

        /// <summary>overlays.get(): the image key of the current overlay (null when it has none).</summary>
        public string OverlayImage => CurrentOverlay == "fog" ? "overlays/fog" : null;

        /// <summary>connection.ts handleOverlay.</summary>
        public void HandleOverlay(Opcodes.Overlay opcode, Dictionary<string, object> info)
        {
            switch (opcode)
            {
                case Opcodes.Overlay.Set:
                    CurrentOverlay = info != null && info.TryGetValue("image", out var image) && image is string name ? name : "";
                    DarkColour = info != null && info.TryGetValue("colour", out var colour) && colour is string c ? c : "rgba(0, 0, 0, 0.5)";
                    AddPlayerLight();
                    ResizeLights();
                    break;
                case Opcodes.Overlay.Remove:
                    RemoveAllLights();
                    CurrentOverlay = "";
                    break;
                case Opcodes.Overlay.Lamp:
                    AddLight((Dictionary<string, object>)info["light"]);
                    break;
            }
        }

        /// <summary>renderer.ts addLight (with getLightData's position: the centre of the tile).</summary>
        public void AddLight(Dictionary<string, object> info)
        {
            var instance = info.TryGetValue("instance", out var i) ? i is double number ? JsNumber.ToJsString(number) : i as string ?? "undefined" : "undefined";
            if (Lightings.ContainsKey(instance)) return;

            double Number(string key) => info.TryGetValue(key, out var value) && value is double d ? d : double.NaN;
            double? Optional(string key) => info.TryGetValue(key, out var value) && value is double d ? d : (double?)null;
            var x = Number("x");
            var y = Number("y");

            var lamp = new Lamp
            {
                X = x * TileSize + TileSize / 2,
                Y = y * TileSize + TileSize / 2,
                Distance = Number("distance"),
                Diffuse = Number("diffuse"),
                Colour = info.TryGetValue("colour", out var colour) ? colour as string : null,
                GridX = x,
                GridY = y,
                Offset = _world.RandomInt(0, 1000)
            };
            lamp.OriginalX = lamp.X;
            lamp.OriginalY = lamp.Y;
            lamp.OriginalDistance = lamp.Distance;
            lamp.Entity = info.TryGetValue("entity", out var entity) ? entity as string : null;
            lamp.FlickerSpeed = Optional("flickerSpeed");
            lamp.FlickerIntensity = Optional("flickerIntensity");

            Lightings[instance] = lamp;
            MaskLights.Add(lamp);
        }

        static Dictionary<string, object> LightPart(Player player, string part) =>
            player.GetLight() is Dictionary<string, object> light && light.TryGetValue(part, out var value) && Truthy(value)
                ? value as Dictionary<string, object>
                : null;

        static bool Truthy(object value) =>
            value != null && !(value is bool b && !b) && !(value is double d && (d == 0 || double.IsNaN(d))) && !(value is string s && s == "");

        /// <summary>renderer.ts addPlayerLight: the outer and inner lights of the shield, bound to the player.</summary>
        public void AddPlayerLight(Player player = null)
        {
            player ??= _world.Player;
            double x = CanvasWidth / TileSize / 2, y = CanvasHeight / TileSize / 2;

            var outer = LightPart(player, "outer");
            if (outer != null) AddLight(SerializedLight(player.Instance, x, y, outer));

            var inner = LightPart(player, "inner");
            if (inner != null) AddLight(SerializedLight($"{player.Instance}inner", x, y, inner));
        }

        /// <summary>renderer.ts getSerializedLight.</summary>
        static Dictionary<string, object> SerializedLight(string instance, double x, double y, Dictionary<string, object> lamp) =>
            new Dictionary<string, object>
            {
                ["instance"] = instance,
                ["x"] = x,
                ["y"] = y,
                ["distance"] = lamp.TryGetValue("distance", out var distance) ? distance : null,
                ["diffuse"] = 0.2,
                ["colour"] = lamp.TryGetValue("colour", out var colour) ? colour : null,
                ["entity"] = instance.Contains("inner") ? instance.Split(new[] { "inner" }, StringSplitOptions.None)[0] : instance,
                ["flickerSpeed"] = lamp.TryGetValue("flickerSpeed", out var speed) ? speed : null,
                ["flickerIntensity"] = lamp.TryGetValue("flickerIntensity", out var intensity) ? intensity : null
            };

        /// <summary>renderer.ts updatePlayerLight.</summary>
        public void UpdatePlayerLight(Player player = null)
        {
            player ??= _world.Player;
            var outer = Lightings[player.Instance];
            var inner = Lightings[$"{player.Instance}inner"];

            if (player.HasLight() && (outer == null || inner == null)) AddPlayerLight(player);

            Update(outer, LightPart(player, "outer"));
            Update(inner, LightPart(player, "inner"));

            ResizeLights();
        }

        static void Update(Lamp lamp, Dictionary<string, object> light)
        {
            if (lamp == null || light == null) return;
            double? Optional(string key) => light.TryGetValue(key, out var value) && value is double d ? d : (double?)null;

            lamp.Distance = Optional("distance") ?? double.NaN;
            lamp.OriginalDistance = lamp.Distance;
            lamp.Colour = light.TryGetValue("colour", out var colour) ? colour as string : null;
            lamp.FlickerSpeed = Optional("flickerSpeed");
            lamp.FlickerIntensity = Optional("flickerIntensity");
        }

        /// <summary>renderer.ts resizeLights: distances relative to the default zoom.</summary>
        public void ResizeLights()
        {
            var scale = (Camera?.ZoomFactor ?? Camera.DefaultZoom) / Camera.DefaultZoom;
            foreach (var lamp in Lightings.Values)
            {
                lamp.Distance = lamp.OriginalDistance * scale;
                lamp.ScaledDistance = lamp.Distance;
                // undefined * scale is NaN.
                lamp.ScaledFlickerIntensity = (lamp.FlickerIntensity ?? double.NaN) * scale;
            }
        }

        /// <summary>renderer.ts removeAllLights (the player's light comes back).</summary>
        public void RemoveAllLights()
        {
            Lightings.Clear();
            MaskLights.Clear();
            AddPlayerLight();
        }

        /// <summary>updater.ts updateLights: flicker (a light with a negative intensity does not flicker).</summary>
        public void UpdateLights(double time)
        {
            foreach (var lamp in Lightings.Values)
            {
                // undefined < 0 is false: an undefined intensity flickers too (to NaN).
                if (lamp.FlickerIntensity < 0) continue;

                lamp.Distance = (lamp.ScaledDistance ?? double.NaN) +
                                Fdlibm.Sin((time + lamp.Offset) / (lamp.FlickerSpeed ?? double.NaN)) * (lamp.ScaledFlickerIntensity ?? double.NaN);
            }
        }

        /// <summary>
        /// renderer.ts drawOverlays without the image: drawLighting for every lighting, then darkMask.compute's
        /// destination-out stamps. Null when there is no overlay.
        /// </summary>
        public List<LightStamp> Draw()
        {
            if (!HasOverlay()) return null;

            foreach (var lamp in Lightings.Values) DrawLighting(lamp);

            var stamps = new List<LightStamp>();
            foreach (var lamp in MaskLights)
            {
                if (lamp.Hidden) continue;

                // Light.getVisibleMaskCache: a 2t × 2t canvas, t = floor(distance × 1.4); Lamp.mask draws it centred
                // (angle and roughness are 0). Below 1 there is no cache and mask() throws destructuring it.
                var t = Math.Floor(lamp.Distance * 1.4);
                if (t < 1) throw new InvalidOperationException("TypeError: Cannot destructure the visible mask cache (undefined).");

                stamps.Add(new LightStamp(t, JsMath.Round(lamp.X - t), JsMath.Round(lamp.Y - t)));
            }
            return stamps;
        }

        void DrawLighting(Lamp lamp)
        {
            var camera = Camera;
            if (lamp.Entity != null)
            {
                var isPlayer = lamp.Entity == _world.Player.Instance;
                var found = isPlayer ? _world.Player : _world.Entities.Get(lamp.Entity);
                if (found != null && !(found is Player))
                    throw new InvalidOperationException("TypeError: entity.hasLight is not a function");
                var entity = (Player)found;

                lamp.Hidden = entity == null || !entity.HasLight();
                if (lamp.Hidden) return;

                if (!isPlayer) lamp.Hidden = !IsVisible(entity.GridX, entity.GridY);
                if (lamp.Hidden) return;

                // getLightPosition.
                lamp.X = (entity.X - camera.X) * camera.ZoomFactor + TileSize * camera.ZoomFactor / 2;
                lamp.Y = (entity.Y - camera.Y) * camera.ZoomFactor;
            }
            else
            {
                lamp.Hidden = !IsVisible(lamp.GridX, lamp.GridY);
                if (lamp.Hidden) return;

                lamp.X = (lamp.OriginalX - camera.X) * camera.ZoomFactor;
                lamp.Y = (lamp.OriginalY - camera.Y) * camera.ZoomFactor;
            }
        }

        /// <summary>camera.isVisible(x, y, 4, 8).</summary>
        bool IsVisible(double x, double y) =>
            x > Camera.GridX - 4 && x < Camera.GridX + Camera.GridWidth + 4 &&
            y > Camera.GridY - 8 && y < Camera.GridY + Camera.GridHeight + 8;
    }
}
