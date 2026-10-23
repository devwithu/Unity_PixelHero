using System.IO;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>Paths outside Assets/ that tests read: the game data and the recorded fixtures.</summary>
    public static class TestPaths
    {
        static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>PixelHero-Data/: the game data (server/data, common/text, client/maps) and the .env files.</summary>
        public static readonly string Root = Path.Combine(ProjectRoot, "PixelHero-Data");

        /// <summary>A file in PixelHero-Data/, e.g. "client/maps/map.json".</summary>
        public static string Data(string relative) => Path.Combine(Root, relative);

        /// <summary>Tools/fixtures/out: recorded reference data the tests compare against.</summary>
        public static readonly string Fixtures = Path.Combine(ProjectRoot, "Tools", "fixtures", "out");

        /// <summary>Tools/fixtures/recordings: kept recorder sessions (one JSON object per line).</summary>
        public static readonly string Recordings = Path.Combine(ProjectRoot, "Tools", "fixtures", "recordings");
    }
}
