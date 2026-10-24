using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PixelHero.Js;
using PixelHero.Network;
using PixelHero.UI;
using NUnit.Framework;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>The string tables (Assets/PixelHero/Localization) and the lookup of English sentences.</summary>
    public class LocalizationTests
    {
        static string TablePath(string language) => Path.Combine(Application.dataPath, "PixelHero", "Localization", language + ".json");

        static Dictionary<string, string> Table(string language) =>
            ((Dictionary<string, object>)JsJson.Parse(File.ReadAllText(TablePath(language)))).ToDictionary(p => p.Key, p => (string)p.Value);

        [SetUp]
        public void LoadTables()
        {
            Localization.Clear();
            foreach (var language in new[] { "ko", "en" }) Localization.Load(language, File.ReadAllText(TablePath(language)));
            Localization.SetLanguage("ko");
        }

        [TearDown]
        public void ClearTables() => Localization.Clear();

        [Test]
        public void KoreanAndEnglishHaveTheSameKeysAndPlaceholders()
        {
            var ko = Table("ko");
            var en = Table("en");
            CollectionAssert.AreEquivalent(en.Keys, ko.Keys);

            var placeholder = new Regex(@"\{\{\s*(\w+)\s*\}\}");
            foreach (var key in en.Keys)
            {
                var expected = placeholder.Matches(en[key]).Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n);
                var actual = placeholder.Matches(ko[key]).Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n);
                CollectionAssert.AreEqual(expected, actual, key);
            }
        }

        /// <summary>
        /// server.ko.json translates every server message key and keeps what the client relies on: the same
        /// placeholders, the same @colour@ tags in the same order, and the edge spaces of messages that are joined.
        /// </summary>
        [Test]
        public void KoreanServerTableMatchesTheEnglishOne()
        {
            var ko = Table("server.ko");
            var en = Table("server.en");
            CollectionAssert.AreEquivalent(en.Keys, ko.Keys);

            var placeholder = new Regex(@"\{\{\s*([\w.]+)\s*\}\}");
            var colour = new Regex(@"@[a-z]+@");
            foreach (var key in en.Keys)
            {
                CollectionAssert.AreEqual(
                    placeholder.Matches(en[key]).Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n),
                    placeholder.Matches(ko[key]).Cast<Match>().Select(m => m.Groups[1].Value).OrderBy(n => n), key);
                CollectionAssert.AreEqual(colour.Matches(en[key]).Cast<Match>().Select(m => m.Value),
                    colour.Matches(ko[key]).Cast<Match>().Select(m => m.Value), key);
                Assert.AreEqual(en[key].StartsWith(" "), ko[key].StartsWith(" "), key + " leading space");
                Assert.AreEqual(en[key].EndsWith(" "), ko[key].EndsWith(" "), key + " trailing space");
            }
        }

        /// <summary>
        /// npc.en.json holds every line of dialogue in the server data (NPCs, quests, achievements, signs), so a line
        /// changed there is not left untranslated; npc.ko.json translates only keys of it, with the same edge spaces.
        /// </summary>
        [Test]
        public void NpcTableCoversTheServerDialogue()
        {
            var en = Table("npc.en");
            var lines = new HashSet<string>(en.Values);
            var missing = new List<string>();
            void Check(string where, object line)
            {
                if (line is string text && text != "" && !lines.Contains(text)) missing.Add($"{where}: {text}");
            }

            object Read(string relative) => JsJson.Parse(File.ReadAllText(TestPaths.Data(relative)));

            foreach (var npc in (Dictionary<string, object>)Read("server/data/npcs.json"))
                if (((Dictionary<string, object>)npc.Value).TryGetValue("text", out var text))
                    foreach (var line in (List<object>)text) Check("npcs.json " + npc.Key, line);

            var fields = new[] { "text", "completedText", "hasItemText", "dialogueHidden", "dialogueStarted" };
            void Walk(string where, object value)
            {
                if (!(value is Dictionary<string, object> obj)) return;
                foreach (var pair in obj)
                    if (pair.Value is List<object> list && fields.Contains(pair.Key))
                        foreach (var line in list) Check(where, line);
                    else Walk(where, pair.Value);
            }

            foreach (var path in Directory.GetFiles(TestPaths.Data("server/data/quests"), "*.json"))
                Walk(Path.GetFileName(path), JsJson.Parse(File.ReadAllText(path)));
            Walk("achievements.json", Read("server/data/achievements.json"));

            var areas = (Dictionary<string, object>)((Dictionary<string, object>)Read("server/data/map/world.json"))["areas"];
            foreach (Dictionary<string, object> sign in (List<object>)areas["signs"])
                foreach (var line in ((string)sign["text"]).Split(',')) Check("sign " + sign["id"], line);

            CollectionAssert.IsEmpty(missing);

            foreach (var pair in Table("npc.ko"))
            {
                Assert.IsTrue(en.ContainsKey(pair.Key), pair.Key);
                Assert.AreEqual(en[pair.Key].StartsWith(" "), pair.Value.StartsWith(" "), pair.Key + " leading space");
                Assert.AreEqual(en[pair.Key].EndsWith(" "), pair.Value.EndsWith(" "), pair.Key + " trailing space");
            }
        }

        [Test]
        public void NpcLinesAreTranslated()
        {
            Localization.Load("ko", File.ReadAllText(TablePath("npc.ko")));
            Localization.Load("en", File.ReadAllText(TablePath("npc.en")));

            Assert.AreEqual("튜토리얼에서 빠져나오는 길을 찾았구나.", Localization.TranslateEnglish("find your way out of that tutorial.", "npc."));
            // A line npc.ko.json leaves out (the song lyrics) stays English.
            Assert.AreEqual("Never gonna give you up...", Localization.TranslateEnglish("Never gonna give you up...", "npc."));
            // A UI string is not an NPC line.
            Assert.AreEqual("Back", Localization.TranslateEnglish("Back", "npc."));

            Localization.SetLanguage("en");
            Assert.AreEqual("find your way out of that tutorial.", Localization.TranslateEnglish("find your way out of that tutorial.", "npc."));
        }

        [Test]
        public void EveryCloseReasonIsTranslated()
        {
            foreach (var pair in CloseReasons.Messages)
                Assert.AreEqual(Localization.Get("reason." + pair.Key), Localization.TranslateEnglish(pair.Value), pair.Key);
            Assert.AreEqual(Localization.Get("reason.unknown"), Localization.TranslateEnglish(CloseReasons.Unknown));
        }

        [Test]
        public void EnglishTemplatesFillTheirPlaceholders()
        {
            Assert.AreEqual("ws://127.0.0.1:9011에 접속할 수 없습니다", Localization.TranslateEnglish("Couldn't connect to ws://127.0.0.1:9011"));
            Assert.AreEqual("A sentence nobody wrote down.", Localization.TranslateEnglish("A sentence nobody wrote down."));

            Localization.SetLanguage("en");
            Assert.AreEqual("Couldn't connect to x", Localization.TranslateEnglish("Couldn't connect to x"));
        }

        [Test]
        public void MissingKeysFallBackToEnglishThenTheKey()
        {
            Localization.Load("ko", "{\"only.ko\": \"한국어\"}");
            Assert.AreEqual("한국어", Localization.Get("only.ko"));
            Assert.AreEqual("PixelHero", Localization.Get("intro.title"));
            Assert.AreEqual("no.such.key", Localization.Get("no.such.key"));
        }

        [Test]
        public void SplitSeparatesTextAndPlaceholders()
        {
            var parts = Localization.Split("Music by {{a}}, & {{b}}");
            CollectionAssert.AreEqual(new (string, string)[] { ("Music by ", null), (null, "a"), (", & ", null), (null, "b") }, parts);
        }

        [Test]
        public void HangulPicksGalmuriSizes()
        {
            Assert.IsTrue(PixelText.HasHangul("로그인"));
            Assert.IsTrue(PixelText.HasHangul("Asky 팀"));
            Assert.IsFalse(PixelText.HasHangul("LOGIN"));
            Assert.AreEqual(12, PixelText.KoreanSize(16));
            Assert.AreEqual(12, PixelText.KoreanSize(14.4f));
            Assert.AreEqual(24, PixelText.KoreanSize(28));
        }

        /// <summary>
        /// The minigame lines on the text canvas (renderer.ts drawTeamWarGUI / drawCoursingGUI, written as
        /// TextDrawPlanner.DrawMinigameGUI writes them) become Korean in the view; anything else on the text canvas
        /// (names, levels) is left alone even when it equals a UI string.
        /// </summary>
        [Test]
        public void MinigameLinesAreTranslatedOnTheTextCanvas()
        {
            string N(double value) => JsValue.NumberToString(value);
            var cases = new (string english, string korean)[]
            {
                ($"Game starts in {N(12)} seconds", "12초 뒤에 게임이 시작됩니다"),
                ($"There is a game in progress, remaining time: {N(37)} seconds", "게임이 진행 중입니다. 남은 시간: 37초"),
                ($"Red: {N(3)}", "빨강: 3"),
                ($"Blue: {N(0)}", "파랑: 0"),
                ($"Time left: {N(-1)} seconds", "남은 시간: -1초"),
                ($"Score: {N(-1)}", "점수: -1")
            };

            foreach (var (english, korean) in cases) Assert.AreEqual(korean, Localization.TranslateEnglish(english, "minigame."));

            // "Back" is a UI string (guilds.back) and could be a player's name.
            Assert.AreEqual("Back", Localization.TranslateEnglish("Back", "minigame."));
            Assert.AreEqual("Level 3", Localization.TranslateEnglish("Level 3", "minigame."));

            Localization.SetLanguage("en");
            foreach (var (english, _) in cases) Assert.AreEqual(english, Localization.TranslateEnglish(english, "minigame."));
        }
    }
}
