namespace PixelHero.Server.Tests
{
    /// <summary>Finds the repository folders from the test's working directory.</summary>
    public static class TestPaths
    {
        static string _root;

        /// <summary>The repository root (the folder that holds Server/ and Tools/).</summary>
        public static string Root
        {
            get
            {
                if (_root != null) return _root;
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                    if (Directory.Exists(Path.Combine(dir.FullName, "Tools", "fixtures")) &&
                        Directory.Exists(Path.Combine(dir.FullName, "Server")))
                        return _root = dir.FullName;
                throw new DirectoryNotFoundException("Repository root not found above " + AppContext.BaseDirectory);
            }
        }

        /// <summary>Tools/fixtures/out/server: reference data recorded from the original server.</summary>
        public static string ServerFixtures => Path.Combine(Root, "Tools", "fixtures", "out", "server");

        /// <summary>PixelHero-Data: the game data (server/data, common/text, client/maps) and the .env files.</summary>
        public static string Data => Path.Combine(Root, "PixelHero-Data");
    }
}
