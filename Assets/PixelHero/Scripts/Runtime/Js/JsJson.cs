using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PixelHero.Js
{
    /// <summary>
    /// JSON.parse and JSON.stringify with JavaScript semantics, for the wire protocol. A JSON library is not used for
    /// messages because the port has to accept exactly what JSON.parse accepts (no comments, single quotes, trailing
    /// commas or NaN; huge numbers become Infinity; lone surrogate escapes are kept) and has to write numbers exactly
    /// like JSON.stringify (80, not 80.0; 316333.25 as the browser writes it).
    /// From the BrowserQuest port (D:\Git\2dmmo Server/.../Js/JsJson.cs), adapted to Unity's .NET.
    /// </summary>
    public static class JsJson
    {
        public sealed class SyntaxError : Exception
        {
            public SyntaxError(string message) : base(message) { }
        }

        /// <summary>JSON.parse: returns null, bool, double, string, List&lt;object&gt; or Dictionary&lt;string, object&gt;.</summary>
        public static object Parse(string text)
        {
            var parser = new Parser(text);
            parser.SkipWhiteSpace();
            var value = parser.ParseValue(0);
            parser.SkipWhiteSpace();
            if (!parser.AtEnd)
                throw new SyntaxError($"Unexpected non-whitespace character after JSON at position {parser.Position}");
            return value;
        }

        /// <summary>JSON.stringify for the value kinds that messages contain.</summary>
        public static string Stringify(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                case Undefined _:
                case JsonNull _:
                    sb.Append("null");
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case double d:
                    sb.Append(double.IsNaN(d) || double.IsInfinity(d) ? "null" : JsValue.NumberToString(d));
                    break;
                case float f:
                    Write(sb, (double)f);
                    break;
                case int i:
                    sb.Append(i.ToString(CultureInfo.InvariantCulture));
                    break;
                case long l:
                    sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    break;
                case Enum e:
                    sb.Append(Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                    break;
                case string s:
                    Quote(sb, s);
                    break;
                case IDictionary dict:
                    sb.Append('{');
                    var firstEntry = true;
                    // JavaScript property order: array-index keys ascending, then the rest in insertion order.
                    var entries = new List<DictionaryEntry>();
                    var enumerator = dict.GetEnumerator();
                    while (enumerator.MoveNext()) entries.Add(enumerator.Entry);
                    var indexKeys = entries.Where(e => IsArrayIndex(e.Key as string))
                        .OrderBy(e => uint.Parse((string)e.Key, CultureInfo.InvariantCulture));
                    foreach (var entry in indexKeys.Concat(entries.Where(e => !IsArrayIndex(e.Key as string))))
                    {
                        if (entry.Value is Undefined) continue;
                        if (!firstEntry) sb.Append(',');
                        firstEntry = false;
                        Quote(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "");
                        sb.Append(':');
                        Write(sb, entry.Value);
                    }
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    var first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    break;
                default:
                    throw new ArgumentException($"Cannot serialize {value.GetType().Name}");
            }
        }

        static bool IsArrayIndex(string key) =>
            key != null && uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n) &&
            n != uint.MaxValue && n.ToString(CultureInfo.InvariantCulture) == key;

        static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>QuoteJSONString (well-formed JSON.stringify: lone surrogates are escaped).</summary>
        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                        {
                            sb.Append(c).Append(s[i + 1]);
                            i++;
                        }
                        else if (char.IsSurrogate(c))
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            const int MaxDepth = 10000;
            readonly string _s;
            int _i;

            public Parser(string s) => _s = s;

            public bool AtEnd => _i >= _s.Length;
            public int Position => _i;

            public void SkipWhiteSpace()
            {
                while (_i < _s.Length && (_s[_i] == ' ' || _s[_i] == '\t' || _s[_i] == '\n' || _s[_i] == '\r'))
                    _i++;
            }

            SyntaxError Unexpected() => _i >= _s.Length
                ? new SyntaxError("Unexpected end of JSON input")
                : new SyntaxError($"Unexpected token '{_s[_i]}' at position {_i}");

            public object ParseValue(int depth)
            {
                if (depth > MaxDepth) throw new SyntaxError("Maximum nesting depth exceeded");
                if (_i >= _s.Length) throw Unexpected();
                var c = _s[_i];
                switch (c)
                {
                    case '{': return ParseObject(depth);
                    case '[': return ParseArray(depth);
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || IsDigit(c)) return ParseNumber();
                        throw Unexpected();
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, _i, word, 0, word.Length) != 0)
                {
                    for (var k = 0; k < word.Length; k++, _i++)
                        if (_i >= _s.Length || _s[_i] != word[k]) throw Unexpected();
                }
                _i += word.Length;
            }

            List<object> ParseArray(int depth)
            {
                _i++; // [
                var list = new List<object>();
                SkipWhiteSpace();
                if (_i < _s.Length && _s[_i] == ']')
                {
                    _i++;
                    return list;
                }
                while (true)
                {
                    SkipWhiteSpace();
                    list.Add(ParseValue(depth + 1));
                    SkipWhiteSpace();
                    if (_i >= _s.Length) throw Unexpected();
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == ']') { _i++; return list; }
                    throw Unexpected();
                }
            }

            Dictionary<string, object> ParseObject(int depth)
            {
                _i++; // {
                var dict = new Dictionary<string, object>();
                SkipWhiteSpace();
                if (_i < _s.Length && _s[_i] == '}')
                {
                    _i++;
                    return dict;
                }
                while (true)
                {
                    SkipWhiteSpace();
                    if (_i >= _s.Length || _s[_i] != '"') throw Unexpected();
                    var key = ParseString();
                    SkipWhiteSpace();
                    if (_i >= _s.Length || _s[_i] != ':') throw Unexpected();
                    _i++;
                    SkipWhiteSpace();
                    // A repeated key keeps its first position but takes the last value, like JSON.parse.
                    dict[key] = ParseValue(depth + 1);
                    SkipWhiteSpace();
                    if (_i >= _s.Length) throw Unexpected();
                    if (_s[_i] == ',') { _i++; continue; }
                    if (_s[_i] == '}') { _i++; return dict; }
                    throw Unexpected();
                }
            }

            string ParseString()
            {
                _i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new SyntaxError("Unterminated string in JSON");
                    var c = _s[_i++];
                    if (c == '"') return sb.ToString();
                    if (c < 0x20) throw new SyntaxError($"Bad control character in string literal at position {_i - 1}");
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }
                    if (_i >= _s.Length) throw new SyntaxError("Unterminated string in JSON");
                    var e = _s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > _s.Length) throw new SyntaxError("Bad Unicode escape in JSON");
                            var code = 0;
                            for (var k = 0; k < 4; k++)
                            {
                                var h = _s[_i++];
                                int v = h >= '0' && h <= '9' ? h - '0'
                                    : h >= 'a' && h <= 'f' ? h - 'a' + 10
                                    : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                                if (v < 0) throw new SyntaxError("Bad Unicode escape in JSON");
                                code = code * 16 + v;
                            }
                            sb.Append((char)code);
                            break;
                        default:
                            throw new SyntaxError($"Bad escaped character in JSON at position {_i - 1}");
                    }
                }
            }

            double ParseNumber()
            {
                var start = _i;
                if (_s[_i] == '-') _i++;
                if (_i >= _s.Length) throw Unexpected();
                if (_s[_i] == '0')
                {
                    _i++;
                }
                else if (_s[_i] >= '1' && _s[_i] <= '9')
                {
                    while (_i < _s.Length && IsDigit(_s[_i])) _i++;
                }
                else throw Unexpected();
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    if (_i >= _s.Length || !IsDigit(_s[_i])) throw Unexpected();
                    while (_i < _s.Length && IsDigit(_s[_i])) _i++;
                }
                if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
                {
                    _i++;
                    if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                    if (_i >= _s.Length || !IsDigit(_s[_i])) throw Unexpected();
                    while (_i < _s.Length && IsDigit(_s[_i])) _i++;
                }

                // Not double.Parse: Unity's Mono is not correctly rounded and throws on overflow (see JsNumber).
                return JsNumber.ParseJsonNumber(_s.Substring(start, _i - start));
            }
        }
    }
}
