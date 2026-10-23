using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PixelHero.Common;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace PixelHero.Tests
{
    /// <summary>
    /// Common/Generated against the protocol values recorded in Tools/fixtures/out/common_enums.json. A protocol change
    /// must update both.
    /// </summary>
    public class CommonEnumsTests
    {
        static JObject fixture;

        static JObject Fixture =>
            fixture ??= JObject.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "common_enums.json")));

        [Test]
        public void PacketsMatch() => AssertEnum(typeof(Packets), Fixture["packets"]);

        [Test]
        public void OpcodesMatch() => AssertContainer(typeof(Opcodes), (JArray)Fixture["opcodes"]);

        [Test]
        public void ModulesMatch() => AssertContainer(typeof(Modules), (JArray)Fixture["modules"]);

        [Test]
        public void ConstantsMatch()
        {
            var expected = (JArray)Fixture["constants"];
            var fields = LiteralFields(typeof(Constants));

            Assert.AreEqual(expected.Count, fields.Count, "constant count");

            foreach (var entry in expected)
            {
                var name = (string)entry["name"];
                Assert.IsTrue(fields.TryGetValue(name, out var actual), $"Constants.{name} missing");
                AssertSameValue(entry["value"], actual, $"Constants.{name}");
            }
        }

        [Test]
        public void SpotChecksAgainstKnownProtocolValues()
        {
            // Values seen on the wire in Tools/fixtures/recordings/2026-10-09_browser_tutorial.jsonl.
            Assert.AreEqual(2, (int)Packets.Login);
            Assert.AreEqual(11, (int)Packets.Movement);
            Assert.AreEqual(60, (int)Packets.AdminSync);
            Assert.AreEqual(2, (int)Opcodes.Login.Guest);
            Assert.AreEqual(6, (int)Opcodes.Movement.Entity);
            Assert.AreEqual(48, Constants.MAP_DIVISION_SIZE);
            Assert.AreEqual(0x80000000u, (uint)Modules.MapFlags.HORIZONTAL_FLAG);
        }

        static void AssertContainer(Type container, JArray enums)
        {
            var nested = container.GetNestedTypes(BindingFlags.Public);
            Assert.AreEqual(enums.Count, nested.Length, $"{container.Name}: nested type count");

            foreach (var dump in enums)
            {
                var name = (string)dump["name"];
                var type = container.GetNestedType(name, BindingFlags.Public);
                Assert.IsNotNull(type, $"{container.Name}.{name} missing");
                AssertEnum(type, dump);
            }
        }

        static void AssertEnum(Type type, JToken dump)
        {
            var members = (JArray)dump["members"];
            var label = type.FullName;

            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                Assert.AreEqual(members.Count, names.Length, $"{label}: member count");

                foreach (var member in members)
                {
                    var name = (string)member[0];
                    var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
                    Assert.IsNotNull(field, $"{label}.{name} missing");

                    var value = Convert.ToInt64(field.GetRawConstantValue());
                    Assert.AreEqual((long)member[1], value, $"{label}.{name}");
                }

                return;
            }

            // String enums are generated as static classes of string constants.
            var constants = LiteralFields(type);
            Assert.AreEqual(members.Count, constants.Count, $"{label}: member count");

            foreach (var member in members)
            {
                var name = (string)member[0];
                Assert.IsTrue(constants.TryGetValue(name, out var actual), $"{label}.{name} missing");
                AssertSameValue(member[1], actual, $"{label}.{name}");
            }
        }

        static Dictionary<string, object> LiteralFields(Type type) =>
            type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .ToDictionary(field => field.Name, field => field.GetRawConstantValue());

        static void AssertSameValue(JToken expected, object actual, string label)
        {
            switch (expected.Type)
            {
                case JTokenType.String:
                    Assert.AreEqual((string)expected, actual, label);
                    break;
                case JTokenType.Integer:
                    Assert.IsTrue(actual is int || actual is long, $"{label}: expected an integer constant, got {actual?.GetType()}");
                    Assert.AreEqual((long)expected, Convert.ToInt64(actual), label);
                    break;
                case JTokenType.Float:
                    Assert.IsInstanceOf<double>(actual, label);
                    Assert.AreEqual((double)expected, (double)actual, label);
                    break;
                default:
                    Assert.Fail($"{label}: unexpected fixture value type {expected.Type}");
                    break;
            }
        }
    }
}
