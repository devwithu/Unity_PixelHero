using System.Text;
using System.Text.RegularExpressions;
using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>common/util/filter.ts: the profanity filter (common/text/profanity.json, used as regular expressions).</summary>
    public static class Filter
    {
        static List<string> _profanities;
        static List<Regex> _patterns;

        /// <summary>Loads common/text/profanity.json.</summary>
        public static void Load(string profanityJsonPath)
        {
            _profanities = ((List<object>)JsData.Parse(File.ReadAllText(profanityJsonPath))).ConvertAll(o => (string)o);
            // new RegExp(profanity, 'gi'): ECMAScript syntax, case-insensitive.
            _patterns = _profanities.ConvertAll(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.ECMAScript));
        }

        static List<string> Profanities => _profanities ?? throw new InvalidOperationException("Filter.Load was not called");

        public static bool IsProfane(string message)
        {
            var lower = JsString.ToLowerCase(message);
            foreach (var profanity in Profanities)
                if (lower.Contains(profanity, StringComparison.Ordinal))
                    return true;

            return false;
        }

        static readonly Regex CombiningSpace = new("(%CC|%E0%B9)(%[0-9A-Z]{2})+%20", RegexOptions.ECMAScript);
        static readonly Regex CombiningWord = new("(%CC|%E0%B9)(%[0-9A-Z]{2})+([A-Za-z0-9_])", RegexOptions.ECMAScript);
        static readonly Regex CombiningEnd = new("(%CC|%E0%B9)(%[0-9A-Z]{2})+$", RegexOptions.ECMAScript);

        public static string Clean(string message)
        {
            var encoded = EncodeURIComponent(message.Normalize(NormalizationForm.FormC));

            encoded = CombiningSpace.Replace(encoded, " ");
            encoded = CombiningWord.Replace(encoded, "$2");
            encoded = CombiningEnd.Replace(encoded, "");

            message = DecodeURIComponent(encoded);

            for (var i = 0; i < _patterns.Count; i++)
                message = _patterns[i].Replace(message, new string('*', Profanities[i].Length));

            return message;
        }

        const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.!~*'()";

        /// <summary>encodeURIComponent: throws URIError on a lone surrogate.</summary>
        public static string EncodeURIComponent(string text)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (Unreserved.IndexOf(c) >= 0)
                {
                    builder.Append(c);
                    continue;
                }
                string chunk;
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) throw new JsTypeError("URI malformed");
                    chunk = text.Substring(i, 2);
                    i++;
                }
                else if (char.IsLowSurrogate(c)) throw new JsTypeError("URI malformed");
                else chunk = c.ToString();
                foreach (var b in Encoding.UTF8.GetBytes(chunk)) builder.Append('%').Append(b.ToString("X2"));
            }
            return builder.ToString();
        }

        /// <summary>decodeURIComponent (the input here always comes from EncodeURIComponent plus the replacements).</summary>
        public static string DecodeURIComponent(string text)
        {
            var bytes = new List<byte>();
            var builder = new StringBuilder();

            void Flush()
            {
                if (bytes.Count == 0) return;
                var decoder = new UTF8Encoding(false, true);
                try
                {
                    builder.Append(decoder.GetString(bytes.ToArray()));
                }
                catch (DecoderFallbackException)
                {
                    throw new JsTypeError("URI malformed");
                }
                bytes.Clear();
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '%')
                {
                    if (i + 2 >= text.Length || !IsHex(text[i + 1]) || !IsHex(text[i + 2])) throw new JsTypeError("URI malformed");
                    bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 2;
                    continue;
                }
                Flush();
                builder.Append(text[i]);
            }
            Flush();
            return builder.ToString();
        }

        static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    }
}
