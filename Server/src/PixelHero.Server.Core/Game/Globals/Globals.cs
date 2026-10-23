using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/globals/globals.ts.</summary>
    public sealed class Globals
    {
        readonly Lights lights;
        readonly Signs signs;

        public Globals(World world)
        {
            lights = new Lights(world.map);
            signs = new Signs(world.map);
        }

        public Signs GetSigns() => signs;
    }

    /// <summary>game/globals/impl/light.ts.</summary>
    public sealed class Light
    {
        public double id;
        public double x;
        public double y;
        public object colour;
        public object diffuse;
        public object distance;
        public object flickerSpeed;
        public object flickerIntensity;

        /// <summary>Default parameters apply to undefined arguments only.</summary>
        public Light(double id, double x, double y, object colour, object diffuse, object distance, object flickerSpeed,
            object flickerIntensity)
        {
            this.id = id;
            this.x = x;
            this.y = y;
            this.colour = colour ?? "rgba(0, 0, 0, 0.2)";
            this.diffuse = diffuse ?? 0.2;
            this.distance = distance ?? 100.0;
            this.flickerSpeed = flickerSpeed ?? 300.0; // Use -1 to disable flickering.
            this.flickerIntensity = flickerIntensity ?? 1.0;
        }

        public JsObject Serialize() => new(
            "instance", JsString.Of(id),
            "x", x,
            "y", y,
            "colour", colour,
            "diffuse", diffuse,
            "distance", distance, // Temporarily hardcoded until distance bug is fixed.
            "flickerSpeed", flickerSpeed,
            "flickerIntensity", flickerIntensity);
    }

    /// <summary>game/globals/lights.ts.</summary>
    public sealed class Lights
    {
        public Lights(Map map)
        {
            foreach (JsObject light in map.lights)
            {
                var region = map.regions.Get(map.regions.GetRegion(light.Num("x") ?? double.NaN, light.Num("y") ?? double.NaN));

                if (region == null) continue;

                region.AddLight(new Light(
                    light.Num("id") ?? double.NaN,
                    light.Num("x") ?? double.NaN,
                    light.Num("y") ?? double.NaN,
                    light["colour"],
                    light["diffuse"],
                    light["distance"],
                    light["flickerSpeed"],
                    light["flickerIntensity"]));
            }

            Log.Info($"Loaded {map.lights.Count} light{(map.lights.Count > 1 ? "s" : "")}.");
        }
    }

    /// <summary>game/globals/impl/sign.ts.</summary>
    public sealed class Sign
    {
        public string instance; // Instance in this case are the object's coordinates.

        public double x;
        public double y;
        public List<string> text;

        public Sign(double x, double y, List<string> text)
        {
            this.x = x;
            this.y = y;
            this.text = text;

            instance = $"{JsString.Of(x)}-{JsString.Of(y)}";
        }

        public void Talk(Player player)
        {
            if (text.Count == 0) return;

            if (player.npcTalk != instance) player.ResetTalk(instance);

            var message = player.talkIndex >= 0 && player.talkIndex < text.Count ? text[(int)player.talkIndex] : null;

            if (player.talkIndex > text.Count - 1) player.talkIndex = 0;
            else player.talkIndex++;

            player.Send(new BubblePacket(new JsObject(
                "instance", $"{JsString.Of(x)}-{JsString.Of(y)}",
                "x", x,
                "y", y,
                "text", message), Opcodes.Bubble.Position));
        }
    }

    /// <summary>game/globals/signs.ts.</summary>
    public sealed class Signs
    {
        readonly JsObject<Sign> signs = new();

        public Signs(Map map)
        {
            foreach (JsObject sign in map.signs)
            {
                double x = sign.Num("x") ?? double.NaN, y = sign.Num("y") ?? double.NaN;
                var coordinate = $"{JsString.Of(x)}-{JsString.Of(y)}";

                if (!JsData.Truthy(sign["text"]))
                {
                    Log.Warning($"Sign at {coordinate} has no text.");
                    continue;
                }

                signs.Set(coordinate, new Sign(x, y, sign.Str("text").Split(',').ToList()));
            }

            Log.Info($"Loaded {map.signs.Count} sign{(map.signs.Count > 1 ? "s" : "")}.");
        }

        public Sign Get(string coordinate) => coordinate == null ? null : signs[coordinate];
    }
}
