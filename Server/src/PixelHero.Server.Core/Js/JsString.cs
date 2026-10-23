using System.Globalization;
using System.Text;
using PixelHero.Js;

namespace PixelHero.Server.Js
{
    /// <summary>String operations with JavaScript semantics.</summary>
    public static class JsString
    {
        /// <summary>JavaScript's WhiteSpace and LineTerminator characters (what trim() and parseInt skip).</summary>
        public static bool IsWhiteSpace(char c) =>
            c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r' || c == ' ' || c == '\u00a0' ||
            c == '\u1680' || (c >= '\u2000' && c <= '\u200a') || c == '\u2028' || c == '\u2029' || c == '\u202f' ||
            c == '\u205f' || c == '\u3000' || c == '\ufeff';

        public static string Trim(string s)
        {
            int start = 0, end = s.Length;
            while (start < end && IsWhiteSpace(s[start])) start++;
            while (end > start && IsWhiteSpace(s[end - 1])) end--;
            return s.Substring(start, end - start);
        }

        /// <summary>parseInt(string) with radix 10 (or 16 for a 0x prefix).</summary>
        public static double ParseInt(string s, int radix = 0)
        {
            if (s == null) return double.NaN;
            var i = 0;
            while (i < s.Length && IsWhiteSpace(s[i])) i++;
            var sign = 1;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
            {
                if (s[i] == '-') sign = -1;
                i++;
            }
            var stripPrefix = true;
            if (radix != 0)
            {
                if (radix < 2 || radix > 36) return double.NaN;
                if (radix != 16) stripPrefix = false;
            }
            else radix = 10;
            if (stripPrefix && i + 1 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X'))
            {
                i += 2;
                radix = 16;
            }
            var start = i;
            var digits = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i];
                int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
                if (d >= radix) break;
                digits.Append(c);
                i++;
            }
            if (i == start) return double.NaN;
            double value;
            if (radix == 10) value = JsNumber.ParseJsonNumber(digits.ToString().TrimStart('0') is { Length: > 0 } t ? t : "0");
            else
            {
                value = 0;
                foreach (var c in digits.ToString())
                {
                    int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c - 'A' + 10;
                    value = value * radix + d;
                }
            }
            return sign * value;
        }

        /// <summary>parseFloat(string).</summary>
        public static double ParseFloat(string s)
        {
            if (s == null) return double.NaN;
            var i = 0;
            while (i < s.Length && IsWhiteSpace(s[i])) i++;
            var rest = s.Substring(i);
            if (rest.StartsWith("Infinity") || rest.StartsWith("+Infinity")) return double.PositiveInfinity;
            if (rest.StartsWith("-Infinity")) return double.NegativeInfinity;
            var j = 0;
            if (j < rest.Length && (rest[j] == '+' || rest[j] == '-')) j++;
            var intStart = j;
            while (j < rest.Length && char.IsAsciiDigit(rest[j])) j++;
            var intDigits = j - intStart;
            var fracDigits = 0;
            if (j < rest.Length && rest[j] == '.')
            {
                var k = j + 1;
                while (k < rest.Length && char.IsAsciiDigit(rest[k])) k++;
                fracDigits = k - j - 1;
                if (intDigits > 0 || fracDigits > 0) j = k;
            }
            if (intDigits == 0 && fracDigits == 0) return double.NaN;
            if (j < rest.Length && (rest[j] == 'e' || rest[j] == 'E'))
            {
                var k = j + 1;
                if (k < rest.Length && (rest[k] == '+' || rest[k] == '-')) k++;
                var expStart = k;
                while (k < rest.Length && char.IsAsciiDigit(rest[k])) k++;
                if (k > expStart) j = k;
            }
            var text = rest.Substring(0, j);
            var negative = text.StartsWith("-");
            text = text.TrimStart('+', '-');
            if (text.StartsWith(".")) text = "0" + text;
            if (text.EndsWith(".")) text = text.Substring(0, text.Length - 1);
            text = text.Replace(".e", "e").Replace(".E", "E");
            // JSON numbers have no leading zeros.
            var integerEnd = 0;
            while (integerEnd < text.Length && char.IsAsciiDigit(text[integerEnd])) integerEnd++;
            var integer = text.Substring(0, integerEnd).TrimStart('0');
            text = (integer.Length > 0 ? integer : "0") + text.Substring(integerEnd);
            var value = JsNumber.ParseJsonNumber(text);
            return negative ? -value : value;
        }

        /// <summary>String.prototype.toUpperCase (locale-independent, full case mapping for common cases).</summary>
        public static string ToUpperCase(string s)
        {
            if (s.IndexOf('\u00df') < 0) return s.ToUpperInvariant();
            return s.Replace("\u00df", "SS").ToUpperInvariant();
        }

        /// <summary>String.prototype.toLowerCase.</summary>
        public static string ToLowerCase(string s) => s.ToLowerInvariant();

        /// <summary>`${value}` for a JavaScript number.</summary>
        public static string Of(double value) => JsNumber.ToJsString(value);

        /// <summary>String(value) for the value kinds the port uses.</summary>
        public static string Of(object value) => value switch
        {
            null => "null",
            Undefined => "undefined",
            string s => s,
            bool b => b ? "true" : "false",
            double d => JsNumber.ToJsString(d),
            int i => i.ToString(CultureInfo.InvariantCulture),
            List<object> list => string.Join(",", list.ConvertAll(item => item == null || item is Undefined ? "" : Of(item))),
            JsObject => "[object Object]",
            _ => value.ToString()
        };
    }
}
