using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PixelHero.Js;

namespace PixelHero.UI
{
    /// <summary>
    /// The UI strings by key (Assets/PixelHero/Localization/&lt;language&gt;.json), Korean by default with English
    /// selectable. Arguments fill <c>{{name}}</c> placeholders, as i18next does in the original.
    ///
    /// English sentences that reach the screen from elsewhere (the server, the network layer) are translated by
    /// <see cref="TranslateEnglish"/>: the sentence is matched against the English table's templates, and the same
    /// key's text in the current language is filled with the matched values. A sentence that matches nothing is
    /// shown as it came.
    /// </summary>
    public static class Localization
    {
        public const string DefaultLanguage = "ko";
        public const string Fallback = "en";

        static readonly Regex Placeholder = new Regex(@"\{\{\s*(\w+)\s*\}\}");
        static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        static List<(Regex pattern, string key, string[] names)> _englishTemplates;

        public static string Language { get; private set; } = DefaultLanguage;

        public static event Action LanguageChanged;

        /// <summary>Languages in load order (the default first).</summary>
        public static IReadOnlyList<string> Languages => Tables.Keys.ToList();

        /// <summary>
        /// Adds a JSON table (an object of key → text) to a language; a language can have several tables
        /// (en.json for the UI, server.en.json for the server's "namespace:KEY" messages).
        /// </summary>
        public static void Load(string language, string json)
        {
            if (!Tables.TryGetValue(language, out var table)) Tables[language] = table = new Dictionary<string, string>();
            foreach (var pair in (Dictionary<string, object>)JsJson.Parse(json)) table[pair.Key] = (string)pair.Value;
            if (language == Fallback) _englishTemplates = null;
        }

        /// <summary>The language of a table asset: "ko" → ko, "server.ko" → ko.</summary>
        public static string LanguageOf(string tableName)
        {
            var dot = tableName.LastIndexOf('.');
            return dot < 0 ? tableName : tableName.Substring(dot + 1);
        }

        /// <summary>The text of <paramref name="key"/> in the current language, then in English; null when neither has it.</summary>
        public static string Find(string key) => Lookup(Language, key) ?? Lookup(Fallback, key);

        /// <summary>
        /// Drops the tables and every LanguageChanged listener: the UI of a scene subscribes while it builds, and the
        /// next scene (GameClient loads the tables first) must not call into destroyed views.
        /// </summary>
        public static void Clear()
        {
            LanguageChanged = null;
            Tables.Clear();
            _englishTemplates = null;
            Language = DefaultLanguage;
        }

        public static bool Has(string language) => Tables.ContainsKey(language);

        public static void SetLanguage(string language)
        {
            if (!Tables.ContainsKey(language) || language == Language) return;
            Language = language;
            LanguageChanged?.Invoke();
        }

        /// <summary>The text of <paramref name="key"/> in the current language, then in English, else the key.</summary>
        public static string Get(string key, IReadOnlyDictionary<string, string> args = null)
        {
            var text = Lookup(Language, key) ?? Lookup(Fallback, key) ?? key;
            return args == null ? text : Fill(text, args);
        }

        public static string Get(string key, params (string name, string value)[] args) =>
            Get(key, args.ToDictionary(a => a.name, a => a.value));

        static string Lookup(string language, string key) =>
            Tables.TryGetValue(language, out var table) && table.TryGetValue(key, out var text) ? text : null;

        /// <summary>Replaces each <c>{{name}}</c>; unknown names are left as they are.</summary>
        public static string Fill(string template, IReadOnlyDictionary<string, string> args) =>
            Placeholder.Replace(template, m => args.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);

        /// <summary>
        /// Splits a template into literal text and placeholders, e.g. "Music by {{a}}, {{b}}" →
        /// ("Music by ", null), (null, "a"), (", ", null), (null, "b").
        /// </summary>
        public static List<(string text, string placeholder)> Split(string template)
        {
            var parts = new List<(string, string)>();
            var last = 0;
            foreach (Match match in Placeholder.Matches(template))
            {
                if (match.Index > last) parts.Add((template.Substring(last, match.Index - last), null));
                parts.Add((null, match.Groups[1].Value));
                last = match.Index + match.Length;
            }
            if (last < template.Length) parts.Add((template.Substring(last), null));
            return parts;
        }

        /// <summary>An English sentence in the current language (see the class summary).</summary>
        public static string TranslateEnglish(string english) => TranslateEnglish(english, null);

        /// <summary>
        /// <see cref="TranslateEnglish(string)"/> against the keys that start with <paramref name="keyPrefix"/> only
        /// (null: every key), for text that may also be a name, such as the text canvas's.
        /// </summary>
        public static string TranslateEnglish(string english, string keyPrefix)
        {
            if (string.IsNullOrEmpty(english) || Language == Fallback) return english;

            foreach (var (pattern, key, names) in EnglishTemplates())
            {
                if (keyPrefix != null && !key.StartsWith(keyPrefix, StringComparison.Ordinal)) continue;
                var match = pattern.Match(english);
                if (!match.Success) continue;

                var text = Lookup(Language, key);
                if (text == null) return english;

                var args = new Dictionary<string, string>();
                for (var i = 0; i < names.Length; i++) args[names[i]] = match.Groups[i + 1].Value;
                return Fill(text, args);
            }

            return english;
        }

        /// <summary>The English templates as anchored patterns, the longest literal text first.</summary>
        static List<(Regex, string, string[])> EnglishTemplates()
        {
            if (_englishTemplates != null) return _englishTemplates;

            _englishTemplates = new List<(Regex, string, string[])>();
            if (!Tables.TryGetValue(Fallback, out var english)) return _englishTemplates;

            foreach (var pair in english.OrderByDescending(p => Placeholder.Replace(p.Value, "").Length).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                var pattern = new StringBuilder("^");
                var names = new List<string>();
                foreach (var (text, placeholder) in Split(pair.Value))
                {
                    if (text != null) pattern.Append(Regex.Escape(text));
                    else
                    {
                        pattern.Append("(.+?)");
                        names.Add(placeholder);
                    }
                }
                pattern.Append('$');
                _englishTemplates.Add((new Regex(pattern.ToString(), RegexOptions.CultureInvariant), pair.Key, names.ToArray()));
            }

            return _englishTemplates;
        }
    }
}
