namespace PixelHero
{
    /// <summary>
    /// Versions the original server checks. The Handshake must carry <see cref="Gver"/> exactly,
    /// or the server closes the socket with "updated" (server player/incoming.ts handleHandshake).
    /// </summary>
    public static class GameVersion
    {
        /// <summary>GVER in PixelHero-Data/.env.defaults.</summary>
        public const string Gver = "0.1.1";

        /// <summary>"version" in PixelHero-Data/server/data/map/world.json (and PixelHero-Data/client/maps/map.json).</summary>
        public const long MapVersion = 1695981410504;
    }
}
