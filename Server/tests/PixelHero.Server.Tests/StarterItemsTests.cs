using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Tests
{
    /// <summary>
    /// STARTER_ITEMS (C# server only): the register_login scenario's inputs (register, ready, log out, log in again)
    /// with the tutorial off. The new character gets each starter item once, and logging in again gives nothing more.
    /// </summary>
    [NonParallelizable]
    public class StarterItemsTests
    {
        static readonly string[] Keys = { "bronzeaxe", "knife", "fishingpole", "coppersword" };

        static List<string> InventoryKeys(bool tutorialEnabled)
        {
            var fixture = JsData.ParseObject(File.ReadAllText(Path.Combine(TestPaths.ServerFixtures, "register_login.json")));

            var harness = new ServerHarness(
                (long)fixture.Num("seed").Value,
                fixture.Num("epoch").Value,
                new JsObject("tutorialEnabled", tutorialEnabled, "starterItems", Keys.Cast<object>().ToList()),
                fixture.Obj("database"));

            harness.Replay(fixture.Arr("inputs"), fixture.Num("end") ?? 0);

            Assert.That(harness.Crash, Is.Null);

            var inventory = (JsObject)harness.Database.Arr("player_inventory").Single();

            return inventory.Arr("slots").Cast<JsObject>()
                .Where(slot => slot.Str("key") != "")
                .Select(slot => $"{slot.Str("key")}x{JsValue.NumberToString(slot.Num("count") ?? double.NaN)}")
                .ToList();
        }

        [Test]
        public void NewCharacterGetsThemOnceWhenTheTutorialIsOff() =>
            Assert.That(InventoryKeys(false), Is.EqualTo(Keys.Select(key => $"{key}x1").ToList()));

        [Test]
        public void NothingWhenTheTutorialIsOn() =>
            Assert.That(InventoryKeys(true), Is.Empty);
    }
}
