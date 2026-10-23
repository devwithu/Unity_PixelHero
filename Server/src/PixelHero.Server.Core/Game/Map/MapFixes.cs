using System.Reflection;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>
    /// Fixes for the original's world.json (Data/map_fixes.json): entities it lacks (the tutorial's trees and fishing
    /// spots) and object tiles missing from its objects list (a cooking pot). The server harness applies the same file to
    /// the original, so both start from the same map.
    /// </summary>
    public static class MapFixes
    {
        static JsObject _fixes;

        static JsObject Fixes
        {
            get
            {
                if (_fixes != null) return _fixes;

                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("map_fixes.json")
                    ?? throw new InvalidOperationException("map_fixes.json is not embedded.");
                using var reader = new StreamReader(stream);
                return _fixes = (JsObject)JsData.Parse(reader.ReadToEnd());
            }
        }

        /// <summary>Sets each fixed entity in world.json's entities (keyed by tile index), as the harness patch does.</summary>
        public static void Apply(JsObject entities, double width)
        {
            foreach (JsObject fix in Fixes.Arr("entities"))
            {
                var index = (fix.Num("y") ?? 0) * width + (fix.Num("x") ?? 0);
                entities.Set(JsString.Of(index), fix.Str("key"));
            }
        }

        /// <summary>Adds the fixed tile ids to world.json's objects (tiles that clients can click).</summary>
        public static void ApplyObjects(HashSet<double> objects)
        {
            foreach (double tileId in Fixes.Arr("objects")) objects.Add(tileId);
        }
    }
}
