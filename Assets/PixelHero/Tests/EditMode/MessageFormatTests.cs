using System.Collections.Generic;
using System.IO;
using PixelHero.Js;
using PixelHero.UI;
using NUnit.Framework;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>
    /// MessageFormat against the client's own util.ts formatNotification / parseMessage with the common/i18n
    /// i18next setup (Tools/fixtures/gen_message_cases.mts), in English.
    /// </summary>
    public class MessageFormatTests
    {
        [SetUp]
        public void LoadEnglish()
        {
            Localization.Clear();
            var tables = Path.Combine(Application.dataPath, "PixelHero", "Localization");
            Localization.Load("en", File.ReadAllText(Path.Combine(tables, "en.json")));
            Localization.Load("en", File.ReadAllText(Path.Combine(tables, "server.en.json")));
            Localization.SetLanguage("en");
        }

        [TearDown]
        public void Clear() => Localization.Clear();

        [Test]
        public void FormatAndParseMatchTheOriginal()
        {
            var cases = (List<object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, "message_cases.json")));
            Assert.Greater(cases.Count, 200);

            var failures = new List<string>();
            foreach (Dictionary<string, object> c in cases)
            {
                var input = (string)c["input"];
                var format = MessageFormat.FormatNotification(input);
                if (format != (string)c["format"]) failures.Add($"format({input}): {c["format"]} vs {format}");
                var parse = MessageFormat.ParseMessage(input);
                if (parse != (string)c["parse"]) failures.Add($"parse({input}): {c["parse"]} vs {parse}");
                var both = MessageFormat.ParseMessage(format);
                if (both != (string)c["both"]) failures.Add($"both({input}): {c["both"]} vs {both}");
            }

            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void KoreanServerTextFallsBackToEnglish()
        {
            Localization.Load("ko", "{\"misc:CANNOT_TALK\": \"지금은 말할 수 없습니다.\"}");
            Localization.SetLanguage("ko");
            Assert.AreEqual("지금은 말할 수 없습니다.", MessageFormat.FormatNotification("misc:CANNOT_TALK"));
            Assert.AreEqual("You cannot do that right now.", MessageFormat.FormatNotification("misc:CANNOT_DO_THAT"));
        }

        [Test]
        public void KoreanServerTableFormatsServerMessages()
        {
            var tables = Path.Combine(Application.dataPath, "PixelHero", "Localization");
            Localization.Load("ko", File.ReadAllText(Path.Combine(tables, "server.ko.json")));
            Localization.SetLanguage("ko");

            Assert.AreEqual("이 문을 지나갈 수 없습니다.", MessageFormat.FormatNotification("misc:CANNOT_PASS_DOOR"));
            Assert.AreEqual("지금 2명이 접속해 있습니다.", MessageFormat.FormatNotification("misc:PEOPLE_ONLINE;population=2"));
            Assert.AreEqual("PixelHero에 다시 오신 것을 환영합니다!", MessageFormat.FormatNotification("misc:WELCOME_BACK;name=PixelHero"));
            Assert.AreEqual("@aquamarine@nobody@crimson@ 플레이어는 접속해 있지 않습니다.",
                MessageFormat.FormatNotification("misc:NOT_ONLINE;username=nobody"));
        }

        [Test]
        public void RichTextColoursAndEscapes()
        {
            Assert.AreEqual("Player <color=#7fffd4ff>Bob<color=#dc143cff> is not online.</color></color>",
                MessageFormat.ToRichText(MessageFormat.ParseMessage("Player @aquamarine@Bob@crimson@ is not online.")));
            Assert.AreEqual("<​b> & 'x'/", MessageFormat.ToRichText("&lt;b&gt; &amp; &#39;x&#39;&#x2F;"));
            Assert.AreEqual("a b", MessageFormat.ToRichText("<span style=\"color:nonsense;\">a b</span>"));
        }
    }
}
