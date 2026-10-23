using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>
    /// The JSON the original imports (packages/server/data/*.json and the map). Like JSON module imports these are
    /// shared, mutable objects: the original changes some of them at run time (drop chances, store items), so each
    /// server (and each test scenario) loads its own copy with <see cref="Load"/>.
    /// </summary>
    public static class ServerData
    {
        /// <summary>PixelHero-Data/server/data (packages/server/data in the original).</summary>
        public static string Directory { get; private set; }

        public static JsObject Items, Mobs, Npcs, Trees, Rocks, Fishing, Foraging, Spawns, Tables, Stores, Achievements,
            Abilities, EffectEntities, Minigames;

        /// <summary>data/crafting/index.ts: {alchemy, chiseling, cooking, crafting, fletching, smelting, smithing}.</summary>
        public static JsObject Crafting;

        /// <summary>data/map/world.json.</summary>
        public static JsObject Map;

        static readonly Dictionary<string, object> Cache = new();

        static Dictionary<string, JsObject> _quests = new();

        /// <summary>data/quests/{key}.json, one shared object per server like the JSON import.</summary>
        public static JsObject Quest(string key)
        {
            if (!_quests.TryGetValue(key, out var data)) _quests[key] = data = Read(Path.Combine("quests", key + ".json"));
            return data;
        }

        /// <summary>Loads every data file from <paramref name="directory"/> (PixelHero-Data/server/data).</summary>
        public static void Load(string directory)
        {
            Directory = directory;

            Items = Read("items.json");
            Mobs = Read("mobs.json");
            Npcs = Read("npcs.json");
            Trees = Read("trees.json");
            Rocks = Read("rocks.json");
            Fishing = Read("fishing.json");
            Foraging = Read("foraging.json");
            Spawns = Read("spawns.json");
            Tables = Read("tables.json");
            Stores = Read("stores.json");
            Achievements = Read("achievements.json");
            Abilities = Read("abilities.json");
            EffectEntities = Read("effectentities.json");
            Minigames = Read("minigames.json");
            Map = Read(Path.Combine("map", "world.json"));
            _quests = new Dictionary<string, JsObject>();

            Crafting = new JsObject();
            foreach (var name in new[] { "alchemy", "chiseling", "cooking", "crafting", "fletching", "smelting", "smithing" })
                Crafting.Set(name, Read(Path.Combine("crafting", name + ".json")));
        }

        /// <summary>Reads (and caches the text of) a JSON file below the data directory, as a fresh object.</summary>
        public static JsObject Read(string relativePath)
        {
            var path = Path.Combine(Directory, relativePath);
            string text;
            lock (Cache)
            {
                if (Cache.TryGetValue(path, out var cached)) text = (string)cached;
                else Cache[path] = text = File.ReadAllText(path);
            }
            return JsData.ParseObject(text);
        }

        /// <summary>The JSON files in a data subdirectory (quests, crafting, ...), by file name without extension.</summary>
        public static List<string> List(string relativeDirectory) =>
            System.IO.Directory.GetFiles(Path.Combine(Directory, relativeDirectory), "*.json")
                .Select(Path.GetFileNameWithoutExtension).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }
}
