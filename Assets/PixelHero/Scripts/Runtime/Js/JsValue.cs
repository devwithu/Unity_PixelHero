using System;

namespace PixelHero.Js
{
    /// <summary>
    /// JavaScript value semantics the port needs. Values decoded by <see cref="JsJson"/> are modelled like JS values:
    /// null, bool, double (every number), string, List&lt;object&gt; (array) and Dictionary&lt;string, object&gt; (object).
    /// From the BrowserQuest port (D:\Git\2dmmo Server/.../Js/Js.cs).
    /// </summary>
    public static class JsValue
    {
        /// <summary>Number::toString (also what JSON.stringify writes for finite numbers).</summary>
        public static string NumberToString(double value) => JsNumber.ToJsString(value);

        /// <summary>The <c>~~value</c> idiom (ToInt32): truncate, then wrap modulo 2^32 into the int range.</summary>
        public static int ToInt32(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
            var truncated = Math.Truncate(value);
            var wrapped = truncated % 4294967296.0;
            if (wrapped < 0) wrapped += 4294967296.0;
            return unchecked((int)(uint)wrapped);
        }
    }

    /// <summary>JavaScript <c>undefined</c> where it has to be distinguished from null (skipped as an object property,
    /// written as null in arrays).</summary>
    public sealed class Undefined
    {
        public static readonly Undefined Value = new Undefined();
        Undefined() { }
        public override string ToString() => "undefined";
    }

    /// <summary>JSON <c>null</c> where it has to be kept as an object property (C# null stands for undefined, which
    /// JSON.stringify leaves out): MongoDB aggregation results, e.g. $first of a missing field.</summary>
    public sealed class JsonNull
    {
        public static readonly JsonNull Value = new JsonNull();
        JsonNull() { }
        public override string ToString() => "null";
    }
}
