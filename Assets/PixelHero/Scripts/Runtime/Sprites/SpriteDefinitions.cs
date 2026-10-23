using System.Collections.Generic;
using PixelHero.Js;

namespace PixelHero.Sprites
{
    /// <summary>One animation row of a sprite sheet (client entity/animation.ts): frames run left to right.</summary>
    public sealed class AnimationInfo
    {
        public string Name;
        public int Length;
        public int Row;
        public int Width;
        public int Height;

        /// <summary>Pixel position of a frame in the sheet, top-left origin like the canvas (animation.ts update/reset).</summary>
        public (int x, int y) FramePosition(int index) => (index * Width, Row * Height);
    }

    /// <summary>A sprites.json entry with every default resolved, as client entity/sprite.ts constructs it.</summary>
    public sealed class SpriteInfo
    {
        public string Key;
        /// <summary>The image the client loads: <c>/img/sprites/{key}.png</c> (may not exist; the original 404s).</summary>
        public string Path;
        public int Width;
        public int Height;
        public int OffsetX;
        public int OffsetY;
        public int IdleSpeed;
        public bool Preload;
        public bool HasDeathAnimation;
        public bool HasHurtSprite;
        public bool HasSilhouette;
        /// <summary>In the order of the JSON object (or of the default table).</summary>
        public List<AnimationInfo> Animations = new List<AnimationInfo>();

        public AnimationInfo GetAnimation(string name) => Animations.Find(animation => animation.Name == name);

        /// <summary>sprite.ts getType: the first path block, or "items" for keys without a folder.</summary>
        public static string TypeOf(string key) => key.Contains("/") ? key.Split('/')[0] : "items";

        /// <summary>sprite.ts getSubType: the second path block when there are at least three.</summary>
        public static string SubTypeOf(string key)
        {
            var blocks = key.Split('/');
            return blocks.Length < 3 ? "" : blocks[1];
        }
    }

    /// <summary>
    /// Reads client/data/sprites.json like client controllers/sprites.ts and entity/sprite.ts, with the defaults of
    /// client utils/util.ts. <c>tileSize</c> is the map's (Utils.tileSize is set by map.ts before sprites load).
    /// </summary>
    public static class SpriteDefinitions
    {
        const int DefaultIdleSpeed = 250;

        static readonly string[] SilhouetteTypes = { "mobs", "player", "npcs", "trees", "rocks", "fishspots", "bushes" };

        /// <summary>Every entry in file order (keys may repeat).</summary>
        public static List<SpriteInfo> ParseAll(string json, int tileSize)
        {
            var result = new List<SpriteInfo>();
            foreach (var entry in (List<object>)JsJson.Parse(json))
                result.Add(Resolve((Dictionary<string, object>)entry, tileSize));
            return result;
        }

        /// <summary>Key → sprite; a repeated key keeps the last entry (sprites.ts: <c>this.sprites[sprite.key] = sprite</c>).</summary>
        public static Dictionary<string, SpriteInfo> Parse(string json, int tileSize)
        {
            var sprites = new Dictionary<string, SpriteInfo>();
            foreach (var sprite in ParseAll(json, tileSize)) sprites[sprite.Key] = sprite;
            return sprites;
        }

        public static SpriteInfo Resolve(Dictionary<string, object> data, int tileSize)
        {
            var key = (string)data["id"];
            var type = SpriteInfo.TypeOf(key);
            var dimension = DefaultEquipmentDimension(SpriteInfo.SubTypeOf(key), tileSize);
            var offset = DefaultOffset(type, tileSize);

            var sprite = new SpriteInfo
            {
                Key = key,
                Path = $"/img/sprites/{key}.png",
                // `??` keeps 0; `||` (idleSpeed) does not.
                Width = NumberOr(data, "width", dimension),
                Height = NumberOr(data, "height", dimension),
                Preload = data.TryGetValue("preload", out var preload) && preload is bool flag ? flag : false,
                OffsetX = NumberOr(data, "offsetX", offset.x),
                OffsetY = NumberOr(data, "offsetY", offset.y),
                HasHurtSprite = type == "mobs" || type == "player",
                HasSilhouette = System.Array.IndexOf(SilhouetteTypes, type) >= 0
            };

            sprite.IdleSpeed = data.TryGetValue("idleSpeed", out var idle) && idle is double speed && speed != 0 && !double.IsNaN(speed)
                ? (int)speed
                : DefaultIdleSpeed;

            var animations = data.TryGetValue("animations", out var raw) && raw is Dictionary<string, object> table
                ? ReadAnimations(table)
                : DefaultAnimations(type);

            foreach (var (name, length, row) in animations)
            {
                if (name == "death") sprite.HasDeathAnimation = true;
                sprite.Animations.Add(new AnimationInfo
                {
                    Name = name, Length = length, Row = row, Width = sprite.Width, Height = sprite.Height
                });
            }

            return sprite;
        }

        static int NumberOr(Dictionary<string, object> data, string name, int fallback) =>
            data.TryGetValue(name, out var value) && value is double number ? (int)number : fallback;

        static List<(string, int, int)> ReadAnimations(Dictionary<string, object> table)
        {
            var list = new List<(string, int, int)>();
            foreach (var pair in table)
            {
                var info = (Dictionary<string, object>)pair.Value;
                list.Add((pair.Key, (int)(double)info["length"], (int)(double)info["row"]));
            }
            return list;
        }

        /// <summary>util.ts getDefaultOffset.</summary>
        public static (int x, int y) DefaultOffset(string type, int tileSize)
        {
            switch (type)
            {
                case "items": return (0, 0);
                case "npcs": return (-8, -14);
                default: return (-tileSize, -tileSize);
            }
        }

        /// <summary>util.ts getDefaultEquipmentDimension.</summary>
        public static int DefaultEquipmentDimension(string subType, int tileSize)
        {
            switch (subType)
            {
                case "effects":
                case "skin":
                case "cape":
                case "legplates":
                case "chestplate":
                case "helmet":
                    return 32;
                case "shield":
                case "weapon":
                    return 48;
                default:
                    return tileSize;
            }
        }

        /// <summary>util.ts getDefaultAnimations: (name, length, row) in the original object order.</summary>
        public static List<(string name, int length, int row)> DefaultAnimations(string type)
        {
            switch (type)
            {
                case "items":
                case "cursors":
                    return new List<(string, int, int)> { ("idle", 1, 0) };

                case "npcs":
                    return new List<(string, int, int)> { ("idle_down", 2, 0) };

                case "trees":
                case "rocks":
                case "fishspots":
                case "bushes":
                    return new List<(string, int, int)> { ("idle", 1, 0), ("shake", 1, 1), ("exhausted", 1, 2) };

                case "mobs":
                    return new List<(string, int, int)>
                    {
                        ("atk_right", 5, 0), ("walk_right", 4, 1), ("idle_right", 2, 2),
                        ("atk_up", 5, 3), ("walk_up", 4, 4), ("idle_up", 2, 5),
                        ("atk_down", 5, 6), ("walk_down", 4, 7), ("idle_down", 2, 8)
                    };

                default: // a player character
                    return new List<(string, int, int)>
                    {
                        ("idle_down", 4, 0), ("idle_right", 4, 1), ("idle_up", 4, 2),
                        ("walk_down", 4, 3), ("walk_right", 4, 4), ("walk_up", 4, 5),
                        ("atk_down", 4, 6), ("atk_right", 4, 7), ("atk_up", 4, 8),
                        ("bow_atk_down", 4, 9), ("bow_atk_right", 4, 10), ("bow_atk_up", 4, 11)
                    };
            }
        }
    }
}
