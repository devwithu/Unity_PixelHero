using PixelHero.Js;
using PixelHero.Server.Common;

namespace PixelHero.Server.Js
{
    /// <summary>
    /// JavaScript operations on untyped values (what JSON.parse gives for a client message): property reads,
    /// ToNumber, truthiness, strict equality and array destructuring. C# null stands for undefined; JSON null is also
    /// null here, so code that must tell them apart checks <see cref="JsObject.Has"/>.
    /// </summary>
    public static class JsAny
    {
        /// <summary>value[key]; reading a property of undefined or null throws a TypeError like JavaScript.</summary>
        public static object Get(object value, string key) => value switch
        {
            null => throw new JsTypeError($"Cannot read properties of undefined (reading '{key}')"),
            JsObject obj => obj[key],
            List<object> list when key == "length" => (double)list.Count,
            List<object> list when JsObject.IsArrayIndex(key, out var index) => index < list.Count ? list[(int)index] : null,
            string s when key == "length" => (double)s.Length,
            string s when JsObject.IsArrayIndex(key, out var index) => index < s.Length ? s[(int)index].ToString() : null,
            _ => null
        };

        public static object Get(object value, int index) => Get(value, index.ToString());

        /// <summary>const [a, b, ...] = value: arrays and strings are iterable; anything else throws.</summary>
        public static object[] Destructure(object value, int count)
        {
            var result = new object[count];
            switch (value)
            {
                case List<object> list:
                    for (var i = 0; i < count && i < list.Count; i++) result[i] = list[i];
                    return result;
                case string s:
                    var chars = s.EnumerateRunes().Select(rune => rune.ToString()).ToList();
                    for (var i = 0; i < count && i < chars.Count; i++) result[i] = chars[i];
                    return result;
                default:
                    throw new JsTypeError($"{Describe(value)} is not iterable");
            }
        }

        static string Describe(object value) => value switch
        {
            null => "undefined",
            JsObject => "object",
            double d => JsString.Of(d),
            bool b => b ? "true" : "false",
            _ => JsString.Of(value)
        };

        /// <summary>ToNumber.</summary>
        public static double ToNumber(object value) => value switch
        {
            null => double.NaN, // undefined (JSON null would be 0; client messages rarely carry it)
            Undefined => double.NaN,
            double d => d,
            int i => i,
            bool b => b ? 1 : 0,
            string s => StringToNumber(s),
            List<object> list => StringToNumber(JsString.Of(list)),
            _ => double.NaN
        };

        /// <summary>Number(string).</summary>
        public static double StringToNumber(string s)
        {
            var text = JsString.Trim(s);
            if (text.Length == 0) return 0;
            if (text.Length > 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X' || text[1] == 'o' || text[1] == 'O' || text[1] == 'b' || text[1] == 'B'))
            {
                var radix = text[1] is 'x' or 'X' ? 16 : text[1] is 'o' or 'O' ? 8 : 2;
                double result = 0;
                foreach (var c in text.Substring(2))
                {
                    var digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'z' ? c - 'a' + 10 : c >= 'A' && c <= 'Z' ? c - 'A' + 10 : 99;
                    if (digit >= radix) return double.NaN;
                    result = result * radix + digit;
                }
                return result;
            }
            if (text == "Infinity" || text == "+Infinity") return double.PositiveInfinity;
            if (text == "-Infinity") return double.NegativeInfinity;
            var parsed = JsString.ParseFloat(text);
            // ParseFloat stops at the first bad character; Number() needs the whole string.
            if (double.IsNaN(parsed)) return double.NaN;
            var j = 0;
            if (j < text.Length && (text[j] == '+' || text[j] == '-')) j++;
            var digits = 0;
            while (j < text.Length && char.IsAsciiDigit(text[j])) { j++; digits++; }
            if (j < text.Length && text[j] == '.')
            {
                j++;
                while (j < text.Length && char.IsAsciiDigit(text[j])) { j++; digits++; }
            }
            if (digits == 0) return double.NaN;
            if (j < text.Length && (text[j] == 'e' || text[j] == 'E'))
            {
                j++;
                if (j < text.Length && (text[j] == '+' || text[j] == '-')) j++;
                var expDigits = 0;
                while (j < text.Length && char.IsAsciiDigit(text[j])) { j++; expDigits++; }
                if (expDigits == 0) return double.NaN;
            }
            return j == text.Length ? parsed : double.NaN;
        }

        public static bool Truthy(object value) => JsData.Truthy(value);

        /// <summary>value === number (strict: only a number can equal a number).</summary>
        public static bool Is(object value, double number) => value is double d && d == number;

        public static bool Is(object value, Enum number) => value is double d && d == Convert.ToInt32(number);

        /// <summary>A string value or undefined (null) for anything else.</summary>
        public static string Str(object value) => value as string;

        /// <summary>
        /// The property key JS makes of a value used as an index (`object[value]`): String(value), so an array becomes
        /// its elements joined by commas (`['3-1']` finds "3-1") and undefined becomes "undefined".
        /// </summary>
        public static string Key(object value) => value == null ? "undefined" : ToText(value);

        static string ToText(object value) => value switch
        {
            null => "",
            string s => s,
            double d => JsString.Of(d),
            bool b => b ? "true" : "false",
            List<object> list => string.Join(",", list.Select(ToText)),
            _ => "[object Object]"
        };

        /// <summary>isNaN(value).</summary>
        public static bool IsNaN(object value) => double.IsNaN(ToNumber(value));

        /// <summary>a &lt; b etc. with ToNumber on both sides (no string comparison case: callers compare to numbers).</summary>
        public static double Num(object value) => ToNumber(value);
    }
}
