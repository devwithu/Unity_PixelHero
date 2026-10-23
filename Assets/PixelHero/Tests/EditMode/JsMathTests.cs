using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PixelHero.Entities;
using PixelHero.Js;
using NUnit.Framework;

namespace PixelHero.Tests
{
    /// <summary>JsMath against Node (Tools/fixtures/gen_js_math_cases.mjs): bit-identical Math.round and Math.hypot.</summary>
    public class JsMathTests
    {
        static Dictionary<string, object> cases;

        static Dictionary<string, object> Cases =>
            cases ??= (Dictionary<string, object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "js_math_cases.json")));

        static double FromHex(object hex)
        {
            var text = (string)hex;
            var bytes = Enumerable.Range(0, 8).Select(i => byte.Parse(text.Substring(i * 2, 2), NumberStyles.HexNumber)).ToArray();
            return BitConverter.ToDouble(bytes, 0);
        }

        static long Bits(double value) => BitConverter.DoubleToInt64Bits(value);

        [Test]
        public void RoundMatchesNode()
        {
            foreach (List<object> c in (List<object>)Cases["round"])
            {
                var x = FromHex(c[0]);
                Assert.AreEqual(Bits(FromHex(c[1])), Bits(JsMath.Round(x)), $"Math.round({x:R})");
            }
        }

        [Test]
        public void SinCosMatchNode()
        {
            var failures = 0;
            string first = null;
            foreach (List<object> c in (List<object>)Cases["trig"])
            {
                var x = FromHex(c[0]);
                if (Bits(FromHex(c[1])) == Bits(Js.Fdlibm.Sin(x)) && Bits(FromHex(c[2])) == Bits(Js.Fdlibm.Cos(x))) continue;
                failures++;
                first ??= $"x={x:R}: sin {FromHex(c[1]):R} vs {Js.Fdlibm.Sin(x):R}, cos {FromHex(c[2]):R} vs {Js.Fdlibm.Cos(x):R}";
            }
            Assert.AreEqual(0, failures, first);
        }

        [Test]
        public void Atan2MatchesNode()
        {
            var failures = 0;
            string first = null;
            foreach (List<object> c in (List<object>)Cases["atan2"])
            {
                double y = FromHex(c[0]), x = FromHex(c[1]);
                if (Bits(FromHex(c[2])) == Bits(Js.Fdlibm.Atan2(y, x))) continue;
                failures++;
                first ??= $"atan2({y:R}, {x:R}): {FromHex(c[2]):R} vs {Js.Fdlibm.Atan2(y, x):R}";
            }
            Assert.AreEqual(0, failures, first);
        }

        [Test]
        public void HypotMatchesNode()
        {
            foreach (List<object> c in (List<object>)Cases["hypot"])
            {
                double a = FromHex(c[0]), b = FromHex(c[1]);
                Assert.AreEqual(Bits(FromHex(c[2])), Bits(JsMath.Hypot(a, b)), $"Math.hypot({a:R}, {b:R})");
            }
        }
    }
}
