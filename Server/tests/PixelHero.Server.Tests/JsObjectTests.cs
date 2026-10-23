using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    public class JsObjectTests
    {
        [Test]
        public void KeysFollowJavaScriptOrder()
        {
            var obj = new JsObject();
            obj.Set("b", 1.0);
            obj.Set("10", 2.0);
            obj.Set("a", 3.0);
            obj.Set("2", 4.0);
            obj.Set("01", 5.0);
            obj.Set("4294967295", 6.0);

            Assert.That(obj.Keys, Is.EqualTo(new[] { "2", "10", "b", "a", "01", "4294967295" }));
            Assert.That(JsJson.Stringify(obj), Is.EqualTo("{\"2\":4,\"10\":2,\"b\":1,\"a\":3,\"01\":5,\"4294967295\":6}"));
        }

        [Test]
        public void ReassigningKeepsThePlaceAndDeletingMovesToTheEnd()
        {
            var obj = new JsObject("a", 1.0, "b", 2.0, "c", 3.0);
            obj.Set("a", 9.0);
            Assert.That(obj.Keys, Is.EqualTo(new[] { "a", "b", "c" }));

            obj.Delete("a");
            obj.Set("a", 1.0);
            Assert.That(obj.Keys, Is.EqualTo(new[] { "b", "c", "a" }));
        }

        [Test]
        public void ForInSkipsKeysDeletedDuringTheWalk()
        {
            var obj = new JsObject("a", 1.0, "b", 2.0, "c", 3.0);
            var seen = new List<string>();
            foreach (var pair in obj)
            {
                seen.Add(pair.Key);
                if (pair.Key == "a") obj.Delete("b");
                if (pair.Key == "a") obj.Set("d", 4.0);
            }
            Assert.That(seen, Is.EqualTo(new[] { "a", "c" }));
        }

        [Test]
        public void PropertyKeysFollowString()
        {
            // String(value) as node prints it: String(['3-1']), String([1, [2, null], true]), String(undefined), ...
            Assert.That(JsAny.Key(new List<object> { "3-1" }), Is.EqualTo("3-1"));
            Assert.That(JsAny.Key(new List<object> { 1.0, new List<object> { 2.0, null }, true }), Is.EqualTo("1,2,,true"));
            Assert.That(JsAny.Key(null), Is.EqualTo("undefined"));
            Assert.That(JsAny.Key(1e21), Is.EqualTo("1e+21"));
            Assert.That(JsAny.Key(new JsObject("a", 1.0)), Is.EqualTo("[object Object]"));
        }
    }
}
