using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace PixelHero.UI
{
    /// <summary>
    /// client/src/utils/util.ts formatNotification and parseMessage, with the part of i18next 23 (common/i18n) they
    /// use: "namespace:KEY;name=value;..." messages from the server become text, and "@colour@" codes become nested
    /// HTML spans. <see cref="ToRichText"/> turns that HTML into uGUI rich text.
    /// </summary>
    public static class MessageFormat
    {
        // i18next looksLikeObjectPath: keys with these characters are natural language, not "ns:key.path".
        static readonly char[] NaturalLanguageChars = { ' ', ',', '?', '!', ';' };
        static readonly Regex Nesting = new Regex(@"\$t\((.+?)\)");
        static readonly Regex Interpolation = new Regex(@"\{\{(.+?)\}\}");

        /// <summary>The template of "namespace:KEY" in the current language (server tables), else English, else null.</summary>
        public static Func<string, string> Lookup = Localization.Find;

        public static string FormatNotification(string message)
        {
            // Raw notification text, no need to format.
            if (!message.Contains(":")) return message;

            // No ';' means no variables.
            if (!message.Contains(";")) return Translate(message, null);

            var blocks = message.Split(';');
            var data = new Dictionary<string, string>();
            for (var i = 1; i < blocks.Length; i++)
            {
                // let [key, value] = blocks[i].split('='): extra '=' parts are dropped, a missing value is undefined.
                var parts = blocks[i].Split('=');
                data[parts[0]] = parts.Length > 1 ? parts[1] : null;
            }

            return Translate(blocks[0], data);
        }

        /// <summary>i18next t(key, data) as configured in common/i18n (ns separator ':', key separator '.').</summary>
        public static string Translate(string key, IReadOnlyDictionary<string, string> data)
        {
            var lookupKey = key;
            var withNamespace = false;

            if (key.Contains(":") && LooksLikeObjectPath(key) && !Nesting.IsMatch(key))
            {
                var parts = key.Split(':');
                lookupKey = string.Join(".", parts, 1, parts.Length - 1);
                key = parts[0] + ":" + lookupKey;
                withNamespace = true;
            }

            var template = withNamespace ? Lookup(key) : null;
            // A missing key comes back as itself, without its namespace.
            if (template == null) return lookupKey;

            return Interpolate(template, data);
        }

        static bool LooksLikeObjectPath(string key)
        {
            if (key.IndexOfAny(NaturalLanguageChars) < 0) return true;
            var ki = key.IndexOf('.');
            return ki > 0 && key.Substring(0, ki).IndexOfAny(NaturalLanguageChars) < 0;
        }

        static string Interpolate(string template, IReadOnlyDictionary<string, string> data)
        {
            if (data == null) return template;
            return Interpolation.Replace(template, match =>
            {
                var name = match.Groups[1].Value.Trim();
                // A variable that was passed without a value is empty; one that was not passed stays as written.
                return data.TryGetValue(name, out var value) ? Escape(value ?? "") : match.Value;
            });
        }

        /// <summary>i18next's escape for interpolated values.</summary>
        public static string Escape(string value)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var c in value)
                switch (c)
                {
                    case '&': builder.Append("&amp;"); break;
                    case '<': builder.Append("&lt;"); break;
                    case '>': builder.Append("&gt;"); break;
                    case '"': builder.Append("&quot;"); break;
                    case '\'': builder.Append("&#39;"); break;
                    case '/': builder.Append("&#x2F;"); break;
                    default: builder.Append(c); break;
                }
            return builder.ToString();
        }

        /// <summary>util.ts parseMessage: "@colour@" codes as nested spans; an odd number of '@' joins with spaces.</summary>
        public static string ParseMessage(string message)
        {
            var blocks = message.Split('@');
            if (blocks.Length % 2 == 0) return string.Join(" ", blocks);

            var builder = new StringBuilder();
            for (var i = 0; i < blocks.Length; i++)
                builder.Append(i % 2 != 0 ? $"<span style=\"color:{blocks[i]};\">" : blocks[i]);

            // messageBlocks.length / 2 - 1 closing tags (a fraction rounds up in the loop condition).
            var codeCount = blocks.Length / 2.0 - 1;
            for (var i = 0; i < codeCount; i++) builder.Append("</span>");
            return builder.ToString();
        }

        static readonly Regex Html = new Regex(@"<span style=""color:([^;""]*);"">|</span>|&(amp|lt|gt|quot|#39|#x2F);");

        /// <summary>
        /// The HTML of <see cref="ParseMessage"/> as uGUI rich text: spans become colour tags (a colour the browser
        /// would not understand leaves the text as it was), entities become characters, and a '&lt;' in the text is
        /// followed by a zero-width space so that uGUI does not read it as a tag.
        /// </summary>
        public static string ToRichText(string html, string inherited = "#ffffff")
        {
            var builder = new StringBuilder();
            var open = new Stack<bool>();
            var last = 0;

            void Text(string text) => builder.Append(text.Replace("<", "<​"));

            foreach (Match match in Html.Matches(html))
            {
                Text(html.Substring(last, match.Index - last));
                last = match.Index + match.Length;

                if (match.Value == "</span>")
                {
                    if (open.Count > 0 && open.Pop()) builder.Append("</color>");
                }
                else if (match.Groups[1].Success)
                {
                    var known = CssColour.TryParse(match.Groups[1].Value, out var colour);
                    if (known) builder.Append($"<color=#{colour.r:x2}{colour.g:x2}{colour.b:x2}{colour.a:x2}>");
                    open.Push(known);
                }
                else
                    Text(match.Groups[2].Value switch
                    {
                        "amp" => "&", "lt" => "<", "gt" => ">", "quot" => "\"", "#39" => "'", _ => "/"
                    });
            }

            Text(html.Substring(last));
            while (open.Count > 0)
                if (open.Pop()) builder.Append("</color>");
            return builder.ToString();
        }
    }
}
