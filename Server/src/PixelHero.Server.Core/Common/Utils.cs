using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    public sealed class Position
    {
        public double x;
        public double y;

        public Position(double x, double y)
        {
            this.x = x;
            this.y = y;
        }

        public JsObject Serialize() => new JsObject("x", x, "y", y);
    }

    /// <summary>common/util/utils.ts.</summary>
    public static class Utils
    {
        public static double counter = -1; // A counter to prevent conflicts in ids.
        public static bool doubleLumberjacking; // Whether or not the double lumberjacking event is active.
        public static bool doubleMining; // Whether or not the double mining event is active.

        /// <summary>Resets the module state (a fresh server, as each harness run imports a fresh bundle).</summary>
        public static void Reset()
        {
            counter = -1;
            doubleLumberjacking = false;
            doubleMining = false;
        }

        /// <summary>`${identifier}-${randomInt(1000, 100_000)}${++counter}`.</summary>
        public static string CreateInstance(double identifier = 0) =>
            JsNumber.ToJsString(identifier) + "-" + JsNumber.ToJsString(RandomInt(1000, 100_000)) +
            JsNumber.ToJsString(++counter);

        public static string CreateInstance(Modules.EntityType identifier) => CreateInstance((double)identifier);

        /// <summary>parseFloat((min + Math.random() * (max - min + 1)).toFixed(decimalPoint)).</summary>
        public static double RandomFloat(double min, double max, int decimalPoint = 4) =>
            JsNumber.ParseJsonNumber(JsNumber.ToFixed(min + Node.Random() * (max - min + 1), decimalPoint));

        public static double RandomInt(double min, double max) => min + Math.Floor(Node.Random() * (max - min + 1));

        public static double RandomWeightedInt(double min, double max, double weight) =>
            Math.Floor(JsMath.Pow(Node.Random(), weight) * (max - min + 1) + min);

        public static Position PositionOffset(double radius)
        {
            var x = RandomInt(0, radius);
            var y = RandomInt(0, radius);
            return new Position(x, y);
        }

        /// <summary>packet > -1 and packet below the last non-"Opcode" key of Packets + 1.</summary>
        public static bool ValidPacket(double packet)
        {
            var last = Enum.GetValues<Packets>().Max();
            return packet > -1 && packet < (double)last + 1;
        }

        static readonly Regex WordPattern = new(@"[A-Za-z0-9_][^\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff]*", RegexOptions.CultureInvariant);

        /// <summary>name.replace(/\w\S*/g, word => word.charAt(0).toUpperCase() + word.slice(1).toLowerCase()).</summary>
        public static string FormatName(string name = "") =>
            WordPattern.Replace(name ?? "", match =>
                Js.JsString.ToUpperCase(match.Value.Substring(0, 1)) + Js.JsString.ToLowerCase(match.Value.Substring(1)));

        public static string GetChecksum(string data) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

        public static double GetDistance(double startX, double startY, double toX, double toY) =>
            Math.Abs(startX - toX) + Math.Abs(startY - toY);

        /// <summary>parseInt(instance.split('-')[0]).</summary>
        public static double GetEntityType(string instance) => Js.JsString.ParseInt(instance.Split('-')[0]);

        public static bool TimePassed(double lastEvent, double threshold) => Node.DateNow() - lastEvent < threshold;

        /// <summary>
        /// Utils.hash: bcryptjs.hash(data, 10, callback). On the harness clock bcryptjs finishes after two rounds of
        /// immediates (genSalt, then the key rounds); an error (data not a string) is thrown from the callback.
        /// </summary>
        public static void Hash(string data, Action<string> callback) =>
            Node.SetImmediate(() =>
            {
                if (data == null) throw new Exception("Illegal arguments: undefined, string");
                var hash = BCrypt.Net.BCrypt.HashPassword(data, 10);
                Node.SetImmediate(() => callback(hash));
            });

        /// <summary>Utils.compare: bcryptjs.compare(data, hash, callback), one round of immediates.</summary>
        public static void Compare(string data, object hash, Action<bool> callback) =>
            Node.SetImmediate(() =>
            {
                if (data == null || hash is not string text)
                    throw new Exception($"Illegal arguments: {(data == null ? "undefined" : "string")}, {(hash is string ? "string" : "undefined")}");
                if (text.Length != 60)
                {
                    callback(false);
                    return;
                }
                bool result;
                try
                {
                    result = BCrypt.Net.BCrypt.Verify(data, text);
                }
                catch (BCrypt.Net.SaltParseException)
                {
                    result = false;
                }
                callback(result);
            });

        /// <summary>zlib.gzipSync(data).toString('base64'); '' for empty data. The bytes differ from Node's.</summary>
        public static string Compress(string data, string compression = "gzip")
        {
            if (string.IsNullOrEmpty(data)) return "";
            var bytes = Encoding.UTF8.GetBytes(data);
            using var output = new MemoryStream();
            using (Stream stream = compression == "gzip"
                       ? new GZipStream(output, CompressionLevel.Optimal, true)
                       : new ZLibStream(output, CompressionLevel.Optimal, true))
                stream.Write(bytes, 0, bytes.Length);
            return Convert.ToBase64String(output.ToArray());
        }

        public static double SanitizeNumber(double number, bool strict = false)
        {
            // If the number is not a number, we return 0.
            if (double.IsNaN(number)) return 0;

            // A fractional number is not allowed, we revert it to positive and floor it.
            if (number % 1 != 0 || strict) return Math.Floor(Math.Max(0, number));

            return Math.Floor(number);
        }

        // /^(([^\s"(),.:;<>@[\\\]]+(\.[^\s"(),.:;<>@[\\\]]+)*)|(".+"))@((\[(?:\d{1,3}\.){3}\d{1,3}])|(([\dA-Za-z-]+\.)+[A-Za-z]{2,}))$/
        const string JsSpace = @"\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";
        static readonly Regex EmailPattern = new(
            "^(([^" + JsSpace + "\"(),.:;<>@\\[\\\\\\]]+(\\.[^" + JsSpace + "\"(),.:;<>@\\[\\\\\\]]+)*)|(\"[^\\n\\r\\u2028\\u2029]+\"))@((\\[(?:[0-9]{1,3}\\.){3}[0-9]{1,3}])|(([0-9A-Za-z-]+\\.)+[A-Za-z]{2,}))\\z",
            RegexOptions.CultureInvariant);

        public static bool IsEmail(string email) => EmailPattern.IsMatch(email ?? "undefined");

        static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9_ ]+\z", RegexOptions.CultureInvariant);

        public static bool IsValidUsername(string text) => UsernamePattern.IsMatch(text ?? "undefined");

        public static bool IsValidPassword(string text = "") => (text ?? "").Length >= 3 && (text ?? "").Length <= 64;

        /// <summary>encodeURI(JSON.stringify(data)).split(/%..|./).length - 1: the UTF-8 length of the JSON.</summary>
        public static double GetBufferSize(object data) => Encoding.UTF8.GetByteCount(JsJson.Stringify(data));

        public static Position GetPositionFromString(string position)
        {
            var split = position.Split(',');
            return new Position(Js.JsString.ParseInt(split[0]), split.Length > 1 ? Js.JsString.ParseInt(split[1]) : double.NaN);
        }

        /// <summary>ipaddr.process(text).toString(), or 69.69.69.69 when the text is not an address.</summary>
        public static string BufferToAddress(string text)
        {
            if (!IPAddress.TryParse(text, out var address)) return "69.69.69.69";
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return address.ToString();
        }

        /// <summary>getUrl: https://host/path when config.ssl (and not `ssl`), otherwise http://host:port/path.</summary>
        public static string GetUrl(string host, double port, string path, bool ssl = false) =>
            Config.Current.ssl && !ssl ? $"https://{host}/{path}" : $"http://{host}:{JsNumber.ToJsString(port)}/{path}";

        public static Modules.Skills? GetSkill(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            key = Js.JsString.ToUpperCase(key.Substring(0, 1)) + Js.JsString.ToLowerCase(key.Substring(1));
            return Enum.TryParse<Modules.Skills>(key, false, out var skill) && Enum.IsDefined(skill) &&
                   skill.ToString() == key
                ? skill
                : null;
        }

        public static JsObject GetEmptyStats() => new("crush", 0.0, "slash", 0.0, "stab", 0.0, "archery", 0.0, "magic", 0.0);

        public static JsObject GetEmptyBonuses() => new("accuracy", 0.0, "strength", 0.0, "archery", 0.0, "magic", 0.0);

        public static string GetResourceType(Modules.Skills skill) => skill switch
        {
            Modules.Skills.Lumberjacking => "tree",
            Modules.Skills.Fishing => "fish",
            Modules.Skills.Mining => "rock",
            _ => "tree"
        };
    }
}
