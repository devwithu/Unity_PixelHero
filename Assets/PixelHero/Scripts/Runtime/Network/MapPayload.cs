using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using PixelHero.Js;

namespace PixelHero.Network
{
    /// <summary>
    /// The Map packet payload: base64 of gzip of the region JSON (server common/util/utils.ts compress;
    /// client network/connection.ts handleMap inflates it with pako).
    /// </summary>
    public static class MapPayload
    {
        public static string Inflate(string base64)
        {
            var compressed = Convert.FromBase64String(base64);
            using var input = new MemoryStream(compressed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }

        /// <summary>Region id (as the JSON key string) → tile entries, each a JsJson object.</summary>
        public static Dictionary<string, object> Decode(string base64) =>
            (Dictionary<string, object>)JsJson.Parse(Inflate(base64));
    }
}
