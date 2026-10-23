#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>
    /// Port of the <c>sanitizer</c> npm package (0.1.3, Google Caja's html-sanitizer) as utils.js uses it:
    /// <c>Utils.sanitize(s) = escape(sanitize(s))</c> with no URI rewriter (URI attributes are removed), no name
    /// policy (names kept), no CSS sanitizer (style attributes removed) and no DOM entity lookup (only the nine
    /// built-in named entities are decoded). Regular expressions are written with explicit ASCII/JavaScript character
    /// classes and <c>\z</c>, because .NET's <c>\s</c>, <c>\w</c>, <c>$</c> and IgnoreCase differ from JavaScript's.
    /// </summary>
    public static class HtmlSanitizer
    {
        const string W = "A-Za-z0-9_";
        const string S = SanitizerJs.RegexSpace;

        static readonly Dictionary<string, string> Entities = new()
        {
            ["lt"] = "<", ["LT"] = "<", ["gt"] = ">", ["GT"] = ">", ["amp"] = "&", ["AMP"] = "&",
            ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = "\u00a0",
        };

        static readonly Regex DecimalEscapeRe = new(@"^#([0-9]+)\z", RegexOptions.CultureInvariant);
        static readonly Regex HexEscapeRe = new(@"^#x([0-9A-Fa-f]+)\z", RegexOptions.CultureInvariant);
        static readonly Regex EntityRe1 = new($"&(#[0-9]+|#[xX][0-9A-Fa-f]+|[{W}]+);", RegexOptions.CultureInvariant);
        static readonly Regex EntityRe2 = new($"^(#[0-9]+|#[xX][0-9A-Fa-f]+|[{W}]+);", RegexOptions.CultureInvariant);
        static readonly Regex LooseAmpRe = new(@"&([^a-zA-Z#]|#(?:[^0-9xX]|[xX](?:[^0-9a-fA-F]|\z)|\z)|\z)", RegexOptions.CultureInvariant);
        static readonly Regex AttrRe = new(
            $"^[{S}]*([-.:{W}]+)(?:[{S}]*(=)[{S}]*((\")[^\"]*(\"|\\z)|(')[^']*('|\\z)|(?=[a-zA-Z][-{W}]*[{S}]*=)|[^\"'{S}]*))?",
            RegexOptions.CultureInvariant);
        static readonly Regex SplitRe = new("(</|<!--|<[!?]|[&<>])", RegexOptions.CultureInvariant);
        static readonly Regex EndTagFastRe = new($"^([-{W}:]+)[^'\"]*", RegexOptions.CultureInvariant);
        static readonly Regex StartTagFastRe = new($"^([-{W}:]+)[{S}]*/?", RegexOptions.CultureInvariant);
        static readonly Regex WordStartRe = new($"^[{W}]", RegexOptions.CultureInvariant);
        static readonly Regex CommentEndRe = new(@"--\z", RegexOptions.CultureInvariant);
        static readonly Regex TagNameRe = new($"^([-{W}:]+)", RegexOptions.CultureInvariant);
        static readonly Regex GarbageRe = new($@"^[\s\S][^a-z{S}]*", RegexOptions.CultureInvariant);
        static readonly Regex SelfClosingRe = new("<([a-zA-Z]+)([^>]*)/>", RegexOptions.CultureInvariant);
        static readonly Dictionary<string, Regex> EndTagRes = new();

        const int EflagsText = Html4.CDATA | Html4.RCDATA;

        /// <summary>utils.js Utils.sanitize: strip unsafe tags, then escape as HTML entities.</summary>
        public static string SanitizeAndEscape(string s) => Escape(Sanitize(s));

        /// <summary>Sanitizer.escape (= escapeAttrib).</summary>
        public static string Escape(string s)
        {
            if (s.Length == 0) return s;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&#34;");
        }

        /// <summary>Sanitizer.sanitize(input) with no policies.</summary>
        public static string Sanitize(string inputHtml)
        {
            // https://github.com/theSmaw/Caja-HTML-Sanitizer/issues/8
            inputHtml = SelfClosingRe.Replace(inputHtml, "<$1$2></$1>");
            if (inputHtml.Length == 0) return inputHtml;
            var output = new StringBuilder();
            new Run(output).Parse(inputHtml);
            return output.ToString();
        }

        static string LookupEntity(string name)
        {
            if (Entities.TryGetValue(name, out var e)) return e;
            var m = DecimalEscapeRe.Match(name);
            if (m.Success) return SanitizerJs.FromCharCode(JsString.ParseInt(m.Groups[1].Value)).ToString();
            m = HexEscapeRe.Match(name);
            if (m.Success) return SanitizerJs.FromCharCode(ParseHex(m.Groups[1].Value)).ToString();
            return "&" + name + ";";
        }

        static double ParseHex(string hex)
        {
            System.Numerics.BigInteger value = 0;
            foreach (var c in hex)
                value = value * 16 + (c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
            return (double)value;
        }

        static string UnescapeEntities(string s) =>
            s.Length == 0 ? s : EntityRe1.Replace(s, m => LookupEntity(m.Groups[1].Value));

        static string StripNuls(string s) => s.Replace("\0", "");

        static string NormalizeRcData(string rcdata)
        {
            if (rcdata.Length == 0) return rcdata;
            return LooseAmpRe.Replace(rcdata, "&amp;$1").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        static string DecodeValue(string v)
        {
            if (v.Length > 0 && (v[0] == '"' || v[0] == '\''))
                v = v.Length >= 2 ? v.Substring(1, v.Length - 2) : "";
            return UnescapeEntities(StripNuls(v));
        }

        static int ElementFlags(string? name) =>
            name != null && Html4.Elements.TryGetValue(name, out var flags) ? flags : 0;

        static bool Truthy(Group g) => g.Success && g.Value.Length > 0;

        static Regex EndTagRe(string tagName)
        {
            if (!EndTagRes.TryGetValue(tagName, out var re))
            {
                var pattern = new StringBuilder("^");
                foreach (var c in tagName)
                {
                    if (c >= 'a' && c <= 'z') pattern.Append('[').Append(c).Append(char.ToUpperInvariant(c)).Append(']');
                    else pattern.Append(Regex.Escape(c.ToString()));
                }
                pattern.Append($"(?:[{S}/]|\\z)");
                lock (EndTagRes)
                    EndTagRes[tagName] = re = new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
            }
            return re;
        }

        sealed class Tag
        {
            public string Name = "";
            public int Eflags;
            public List<string?> Attrs = new();
            public int Next;
        }

        /// <summary>One sanitize call: the SAX parser (parse/parseCPS) driving makeHtmlSanitizer's handler.</summary>
        sealed class Run
        {
            readonly StringBuilder _out;
            readonly List<(string Orig, string Rep)> _stack = new();
            bool _ignoring;
            bool _noMoreGt;
            bool _noMoreEndComments;

            public Run(StringBuilder output) => _out = output;

            void Emit(string text)
            {
                if (!_ignoring) _out.Append(text);
            }

            public void Parse(string htmlText)
            {
                var parts = SplitRe.Split(htmlText);
                var end = parts.Length;
                for (var pos = 0; pos < end;)
                {
                    var current = parts[pos++];
                    var next = pos < end ? parts[pos] : null;
                    Match m;
                    int p;
                    switch (current)
                    {
                        case "&":
                            if (EntityRe2.IsMatch(next ?? "undefined"))
                            {
                                Emit("&" + next);
                                pos++;
                            }
                            else
                            {
                                Emit("&amp;");
                            }
                            break;
                        case "</":
                            if ((m = EndTagFastRe.Match(next ?? "undefined")).Success)
                            {
                                if (next == null) throw new JsTypeError("Cannot read properties of undefined (reading 'length')");
                                if (m.Value.Length == next.Length && pos + 1 < end && parts[pos + 1] == ">")
                                {
                                    pos += 2;
                                    EndTag(m.Groups[1].Value.ToLowerInvariant());
                                }
                                else
                                {
                                    pos = ParseEndTag(parts, pos);
                                }
                            }
                            else
                            {
                                Emit("&lt;/");
                            }
                            break;
                        case "<":
                            if ((m = StartTagFastRe.Match(next ?? "undefined")).Success)
                            {
                                if (next == null) throw new JsTypeError("Cannot read properties of undefined (reading 'length')");
                                if (m.Value.Length == next.Length && pos + 1 < end && parts[pos + 1] == ">")
                                {
                                    pos += 2;
                                    var tagName = m.Groups[1].Value.ToLowerInvariant();
                                    StartTag(tagName, new List<string?>());
                                    var eflags = ElementFlags(tagName);
                                    if ((eflags & EflagsText) != 0)
                                        pos = ParseText(parts, new Tag { Name = tagName, Next = pos, Eflags = eflags });
                                }
                                else
                                {
                                    pos = ParseStartTag(parts, pos);
                                }
                            }
                            else
                            {
                                Emit("&lt;");
                            }
                            break;
                        case "<!--":
                            if (!_noMoreEndComments)
                            {
                                for (p = pos + 1; p < end; p++)
                                    if (parts[p] == ">" && CommentEndRe.IsMatch(parts[p - 1])) break;
                                if (p < end)
                                    pos = p + 1; // comments are dropped (the handler has no comment callback)
                                else
                                    _noMoreEndComments = true;
                            }
                            if (_noMoreEndComments)
                                Emit("&lt;!--");
                            break;
                        case "<!":
                            if (!WordStartRe.IsMatch(next ?? "undefined"))
                            {
                                Emit("&lt;!");
                            }
                            else
                            {
                                if (!_noMoreGt)
                                {
                                    for (p = pos + 1; p < end; p++)
                                        if (parts[p] == ">") break;
                                    if (p < end) pos = p + 1;
                                    else _noMoreGt = true;
                                }
                                if (_noMoreGt)
                                    Emit("&lt;!");
                            }
                            break;
                        case "<?":
                            if (!_noMoreGt)
                            {
                                for (p = pos + 1; p < end; p++)
                                    if (parts[p] == ">") break;
                                if (p < end) pos = p + 1;
                                else _noMoreGt = true;
                            }
                            if (_noMoreGt)
                                Emit("&lt;?");
                            break;
                        case ">":
                            Emit("&gt;");
                            break;
                        case "":
                            break;
                        default:
                            Emit(current);
                            break;
                    }
                }
                // endDoc
                for (; _stack.Count > 0; _stack.RemoveAt(_stack.Count - 1))
                    _out.Append("</").Append(_stack[^1].Rep).Append('>');
            }

            int ParseEndTag(string[] parts, int pos)
            {
                var tag = ParseTagAndAttrs(parts, pos);
                // drop unclosed tags
                if (tag == null) return parts.Length;
                EndTag(tag.Name);
                return tag.Next;
            }

            int ParseStartTag(string[] parts, int pos)
            {
                var tag = ParseTagAndAttrs(parts, pos);
                // drop unclosed tags
                if (tag == null) return parts.Length;
                StartTag(tag.Name, tag.Attrs);
                // tags like <script> and <textarea> have special parsing
                return (tag.Eflags & EflagsText) != 0 ? ParseText(parts, tag) : tag.Next;
            }

            int ParseText(string[] parts, Tag tag)
            {
                var end = parts.Length;
                var re = EndTagRe(tag.Name);
                var first = tag.Next;
                var p = tag.Next + 1;
                for (; p < end; p++)
                    if (parts[p - 1] == "</" && re.IsMatch(parts[p])) break;
                if (p < end) p -= 1;
                var buf = first < p ? string.Concat(parts.Skip(first).Take(p - first)) : "";
                if ((tag.Eflags & Html4.CDATA) != 0)
                    Emit(buf);
                else if ((tag.Eflags & Html4.RCDATA) != 0)
                    Emit(NormalizeRcData(buf));
                else
                    throw new InvalidOperationException("bug");
                return p;
            }

            // at this point, parts[pos-1] is either "<" or "</".
            static Tag? ParseTagAndAttrs(string[] parts, int pos)
            {
                var m = TagNameRe.Match(parts[pos]);
                var tag = new Tag { Name = m.Groups[1].Value.ToLowerInvariant() };
                tag.Eflags = ElementFlags(tag.Name);
                var buf = parts[pos].Substring(m.Value.Length);
                var p = pos + 1;
                var end = parts.Length;
                for (; p < end; p++)
                {
                    if (parts[p] == ">") break;
                    buf += parts[p];
                }
                if (end <= p) return null;
                var attrs = new List<string?>();
                while (buf != "")
                {
                    m = AttrRe.Match(buf);
                    if (!m.Success)
                    {
                        // No attribute found: skip garbage
                        buf = GarbageRe.Replace(buf, "", 1);
                    }
                    else if ((Truthy(m.Groups[4]) && !Truthy(m.Groups[5])) || (Truthy(m.Groups[6]) && !Truthy(m.Groups[7])))
                    {
                        // Unterminated quote: slurp to the next unquoted '>'
                        var quote = Truthy(m.Groups[4]) ? m.Groups[4].Value : m.Groups[6].Value;
                        var sawQuote = false;
                        var abuf = new StringBuilder(buf).Append(parts[p++]);
                        for (; p < end; p++)
                        {
                            if (sawQuote)
                            {
                                if (parts[p] == ">") break;
                            }
                            else if (parts[p].Contains(quote, StringComparison.Ordinal))
                            {
                                sawQuote = true;
                            }
                            abuf.Append(parts[p]);
                        }
                        // Slurp failed: lose the garbage
                        if (end <= p) break;
                        // Otherwise retry attribute parsing
                        buf = abuf.ToString();
                    }
                    else
                    {
                        // We have an attribute
                        var aName = m.Groups[1].Value.ToLowerInvariant();
                        var aValue = Truthy(m.Groups[2]) ? DecodeValue(m.Groups[3].Value) : "";
                        attrs.Add(aName);
                        attrs.Add(aValue);
                        buf = buf.Substring(m.Value.Length);
                    }
                }
                tag.Attrs = attrs;
                tag.Next = p + 1;
                return tag;
            }

            void StartTag(string tagNameOrig, List<string?> attribs)
            {
                if (_ignoring) return;
                if (!Html4.Elements.TryGetValue(tagNameOrig, out var eflagsOrig)) return;
                if ((eflagsOrig & Html4.FOLDABLE) != 0) return;

                // makeTagPolicy: unsafe elements are removed, others get their attributes sanitized.
                if ((eflagsOrig & Html4.UNSAFE) != 0)
                {
                    _ignoring = (eflagsOrig & Html4.EMPTY) == 0;
                    return;
                }
                attribs = SanitizeAttribs(tagNameOrig, attribs);
                var tagNameRep = tagNameOrig;
                var eflagsRep = eflagsOrig;

                if ((eflagsOrig & Html4.OPTIONAL_ENDTAG) != 0 && _stack.Count > 0)
                {
                    var onStack = _stack[^1];
                    if (onStack.Orig == tagNameOrig && (onStack.Rep != tagNameRep || tagNameOrig != tagNameRep))
                        _out.Append("</").Append(onStack.Rep).Append('>');
                }
                if ((eflagsOrig & Html4.EMPTY) == 0)
                    _stack.Add((tagNameOrig, tagNameRep));
                _out.Append('<').Append(tagNameRep);
                for (var i = 0; i < attribs.Count; i += 2)
                {
                    var value = attribs[i + 1];
                    if (value != null)
                        _out.Append(' ').Append(attribs[i]).Append("=\"").Append(Escape(value)).Append('"');
                }
                _out.Append('>');
                if ((eflagsOrig & Html4.EMPTY) != 0 && (eflagsRep & Html4.EMPTY) == 0)
                    _out.Append("</").Append(tagNameRep).Append('>');
            }

            void EndTag(string tagName)
            {
                if (_ignoring)
                {
                    _ignoring = false;
                    return;
                }
                if (!Html4.Elements.TryGetValue(tagName, out var eflags)) return;
                if ((eflags & (Html4.EMPTY | Html4.FOLDABLE)) != 0) return;

                int index;
                if ((eflags & Html4.OPTIONAL_ENDTAG) != 0)
                {
                    for (index = _stack.Count; --index >= 0;)
                    {
                        var stackElOrigTag = _stack[index].Orig;
                        if (stackElOrigTag == tagName) break;
                        if ((ElementFlags(stackElOrigTag) & Html4.OPTIONAL_ENDTAG) == 0)
                            return; // Don't pop non optional end tags looking for a match.
                    }
                }
                else
                {
                    for (index = _stack.Count; --index >= 0;)
                        if (_stack[index].Orig == tagName) break;
                }
                if (index < 0) return; // Not opened.
                for (var i = _stack.Count; --i > index;)
                {
                    var stackElRepTag = _stack[i].Rep;
                    if ((ElementFlags(stackElRepTag) & Html4.OPTIONAL_ENDTAG) == 0)
                        _out.Append("</").Append(stackElRepTag).Append('>');
                }
                if (index < _stack.Count) tagName = _stack[index].Rep;
                _stack.RemoveRange(index, _stack.Count - index);
                _out.Append("</").Append(tagName).Append('>');
            }

            static List<string?> SanitizeAttribs(string tagName, List<string?> attribs)
            {
                for (var i = 0; i < attribs.Count; i += 2)
                {
                    var attribName = attribs[i];
                    var value = attribs[i + 1];
                    int? atype = null;
                    if (Html4.Attribs.TryGetValue(tagName + "::" + attribName, out var t1)) atype = t1;
                    else if (Html4.Attribs.TryGetValue("*::" + attribName, out var t2)) atype = t2;

                    switch (atype)
                    {
                        case Html4.AtypeNONE:
                            break;
                        case Html4.AtypeID:
                        case Html4.AtypeIDREF:
                        case Html4.AtypeIDREFS:
                        case Html4.AtypeGLOBAL_NAME:
                        case Html4.AtypeLOCAL_NAME:
                        case Html4.AtypeCLASSES:
                            break; // no name policy: kept as is
                        case Html4.AtypeURI_FRAGMENT:
                            value = !string.IsNullOrEmpty(value) && value[0] == '#' ? "#" + value.Substring(1) : null;
                            break;
                        default:
                            // SCRIPT, STYLE (no CSS sanitizer), URI (no rewriter), FRAME_TARGET, unknown attributes.
                            value = null;
                            break;
                    }
                    attribs[i + 1] = value;
                }
                return attribs;
            }
        }
    }
}
