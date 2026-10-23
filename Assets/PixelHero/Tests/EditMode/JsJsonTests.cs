using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PixelHero.Js;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace PixelHero.Tests
{
    /// <summary>PixelHero.Js against Node (Tools/fixtures/gen_js_json_cases.mjs) and against recorded wire frames.</summary>
    public class JsJsonTests
    {
        static JObject cases;

        static JObject Cases =>
            cases ??= JObject.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "js_json_cases.json")));

        static double FromHex(string hex)
        {
            var bytes = Enumerable.Range(0, 8)
                .Select(i => byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber))
                .ToArray();
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        [Test]
        public void NumberToStringMatchesNode()
        {
            var failures = Cases["numbers"]
                .Select(c => (value: FromHex((string)c["hex"]), expected: (string)c["string"]))
                .Where(c => JsValue.NumberToString(c.value) != c.expected)
                .Take(10)
                .Select(c => $"{c.value:R}: expected {c.expected}, got {JsValue.NumberToString(c.value)}")
                .ToList();

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void ToInt32MatchesNode()
        {
            var failures = Cases["numbers"]
                .Select(c => (value: FromHex((string)c["hex"]), expected: (int)c["int32"]))
                .Where(c => JsValue.ToInt32(c.value) != c.expected)
                .Take(10)
                .Select(c => $"{c.value:R}: expected {c.expected}, got {JsValue.ToInt32(c.value)}")
                .ToList();

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void ParsedNumbersMatchNode()
        {
            // JSON.parse(String(x)) === x for every finite double, so parsing Node's text must give the same bits.
            var failures = Cases["numbers"]
                .Select(c => (value: FromHex((string)c["hex"]), text: (string)c["string"]))
                .Where(c => !c.text.Contains("Infinity"))
                .Select(c => (c.value, c.text, parsed: (double)JsJson.Parse(c.text)))
                .Where(c => BitConverter.DoubleToInt64Bits(c.parsed) != BitConverter.DoubleToInt64Bits(c.value == 0 ? 0 : c.value))
                .Take(10)
                .Select(c => $"{c.text}: parsed {c.parsed:R}")
                .ToList();

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void StringifiedStringsMatchNode()
        {
            foreach (var c in Cases["strings"])
            {
                var text = new string(c["units"].Select(u => (char)(int)u).ToArray());
                var expected = (string)c["json"];

                Assert.AreEqual(expected, JsJson.Stringify(text), Describe(text));
                Assert.AreEqual(text, JsJson.Parse(expected), Describe(text));
            }
        }

        [Test]
        public void ParseAcceptsAndRejectsLikeNode()
        {
            foreach (var c in Cases["parse"])
            {
                var text = (string)c["text"];

                if ((bool)c["ok"])
                    Assert.AreEqual((string)c["result"], JsJson.Stringify(JsJson.Parse(text)), $"parse {text}");
                else
                    Assert.Throws<JsJson.SyntaxError>(() => JsJson.Parse(text), $"should reject {text}");
            }
        }

        [Test]
        public void RecordedFramesRoundTripExactly()
        {
            // Every frame was written by JSON.stringify (server or browser), so parse + stringify must reproduce it.
            var files = Directory.GetFiles(TestPaths.Recordings, "*.jsonl");
            Assert.IsNotEmpty(files);

            var frames = 0;
            foreach (var file in files)
            foreach (var line in File.ReadLines(file).Where(l => l.Trim().Length > 0))
            {
                var entry = JObject.Parse(line);
                if (entry["data"] == null) continue;

                var data = (string)entry["data"];
                Assert.AreEqual(data, JsJson.Stringify(JsJson.Parse(data)), $"{Path.GetFileName(file)} t={entry["t"]}");
                frames++;
            }

            Assert.Greater(frames, 100);
        }

        static string Describe(string text) =>
            string.Join(" ", text.Select(ch => ((int)ch).ToString("x4", CultureInfo.InvariantCulture)));
    }
}
