using System.Numerics;
using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>The JavaScript helpers the sanitizer port (from the BrowserQuest port) uses.</summary>
    static class SanitizerJs
    {
        /// <summary>The \s class of a JavaScript (non-unicode) regular expression, for .NET patterns.</summary>
        public const string RegexSpace = @"\t\n\v\f\r \u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000\ufeff";

        /// <summary>String.fromCharCode(number): ToUint16.</summary>
        public static char FromCharCode(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return '\0';
            var truncated = new BigInteger(Math.Truncate(value));
            var mod = (int)(((truncated % 65536) + 65536) % 65536);
            return (char)mod;
        }
    }

    /// <summary>
    /// The `sanitizer` npm package (0.1.3) as incoming.ts uses it: escape(sanitize(message)). The port comes from the
    /// BrowserQuest port (D:\Git\2dmmo, checked there on 4,076 cases); the package version and source are the same.
    /// </summary>
    public static class Sanitizer
    {
        /// <summary>sanitize(input): a non-string input throws like `inputHtml.replace` on it.</summary>
        public static string Sanitize(object input) =>
            HtmlSanitizer.Sanitize(input as string ?? throw new JsTypeError("inputHtml.replace is not a function"));

        public static string Escape(string input) => HtmlSanitizer.Escape(input);
    }
}
