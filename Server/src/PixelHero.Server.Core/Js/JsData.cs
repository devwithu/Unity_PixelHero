using PixelHero.Js;

namespace PixelHero.Server.Js
{
    /// <summary>
    /// JSON data as the original sees it after JSON.parse or a JSON import: objects are <see cref="JsObject"/> (with
    /// JavaScript key order), arrays are List&lt;object&gt;, numbers are double. Plus small readers for that data.
    /// </summary>
    public static class JsData
    {
        /// <summary>JSON.parse with objects as <see cref="JsObject"/>.</summary>
        public static object Parse(string text) => Convert(JsJson.Parse(text));

        public static JsObject ParseObject(string text) => (JsObject)Parse(text);

        public static object Convert(object value)
        {
            switch (value)
            {
                case Dictionary<string, object> dictionary:
                {
                    var result = new JsObject();
                    foreach (var pair in dictionary) result.Set(pair.Key, Convert(pair.Value));
                    return result;
                }
                case List<object> list:
                {
                    for (var i = 0; i < list.Count; i++) list[i] = Convert(list[i]);
                    return list;
                }
                default:
                    return value;
            }
        }

        /// <summary>A deep copy (structuredClone for JSON-like values).</summary>
        public static object Clone(object value)
        {
            switch (value)
            {
                // ObjectIds are immutable values.
                case Database.ObjectIdValue id:
                    return id;
                case JsObject obj:
                {
                    var copy = new JsObject();
                    foreach (var pair in obj) copy.Set(pair.Key, Clone(pair.Value));
                    return copy;
                }
                case List<object> list:
                    return list.ConvertAll(Clone);
                default:
                    return value;
            }
        }

        public static object Get(this JsObject obj, string key) => obj?[key];

        public static string Str(this JsObject obj, string key) => obj?[key] as string;

        /// <summary>The number at key, or null when it is missing or not a number (undefined).</summary>
        public static double? Num(this JsObject obj, string key) => obj?[key] is double d ? d : null;

        public static bool? Bool(this JsObject obj, string key) => obj?[key] is bool b ? b : null;

        public static JsObject Obj(this JsObject obj, string key) => obj?[key] as JsObject;

        public static List<object> Arr(this JsObject obj, string key) => obj?[key] as List<object>;

        /// <summary>JavaScript truthiness.</summary>
        public static bool Truthy(object value) => value switch
        {
            null => false,
            Undefined => false,
            JsonNull => false,
            bool b => b,
            double d => d != 0 && !double.IsNaN(d),
            int i => i != 0,
            string s => s.Length > 0,
            _ => true
        };
    }
}
