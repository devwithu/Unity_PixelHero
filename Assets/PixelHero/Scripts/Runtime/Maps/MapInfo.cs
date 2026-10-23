using System.Collections.Generic;
using System.Linq;
using PixelHero.Js;

namespace PixelHero.Maps
{
    /// <summary>A tileset range of the client map (map.json "tilesets"), ids 0-based like the region data.</summary>
    public sealed class TilesetRange
    {
        public int FirstGid;
        public int LastGid;
        /// <summary>"tilesets/tilesheet-1.png" (below client/public/img).</summary>
        public string Path;
        public string RelativePath;

        /// <summary>The SpriteCatalog texture key: "tilesets/tilesheet-1".</summary>
        public string TextureKey => "tilesets/" + System.IO.Path.GetFileNameWithoutExtension(RelativePath);
    }

    /// <summary>One frame of an animated tile (map.json "animations").</summary>
    public readonly struct TileAnimationFrame
    {
        public readonly int Duration;
        public readonly int TileId;

        public TileAnimationFrame(int duration, int tileId)
        {
            Duration = duration;
            TileId = tileId;
        }
    }

    /// <summary>client/data/maps/map.json: what the client knows about the map before any region arrives.</summary>
    public sealed class MapInfo
    {
        public int Width;
        public int Height;
        public int TileSize;
        public long Version;
        public HashSet<int> High = new HashSet<int>();
        /// <summary>Sorted by first gid, as map.ts sorts the loaded tilesets.</summary>
        public List<TilesetRange> Tilesets = new List<TilesetRange>();
        /// <summary>Tile id (as stored in the region data) → frames.</summary>
        public Dictionary<int, List<TileAnimationFrame>> Animations = new Dictionary<int, List<TileAnimationFrame>>();

        public static MapInfo Parse(string json)
        {
            var data = (Dictionary<string, object>)JsJson.Parse(json);
            var info = new MapInfo
            {
                Width = (int)(double)data["width"],
                Height = (int)(double)data["height"],
                TileSize = (int)(double)data["tileSize"],
                Version = (long)(double)data["version"]
            };

            foreach (var id in (List<object>)data["high"]) info.High.Add((int)(double)id);

            info.Tilesets = ((List<object>)data["tilesets"])
                .Cast<Dictionary<string, object>>()
                .Select(t => new TilesetRange
                {
                    FirstGid = (int)(double)t["firstGid"],
                    LastGid = (int)(double)t["lastGid"],
                    Path = (string)t["path"],
                    RelativePath = (string)t["relativePath"]
                })
                .OrderBy(t => t.FirstGid)
                .ToList();

            foreach (var pair in (Dictionary<string, object>)data["animations"])
                info.Animations[int.Parse(pair.Key)] = ((List<object>)pair.Value)
                    .Cast<Dictionary<string, object>>()
                    .Select(f => new TileAnimationFrame((int)(double)f["duration"], (int)(double)f["tileId"]))
                    .ToList();

            return info;
        }
    }
}
