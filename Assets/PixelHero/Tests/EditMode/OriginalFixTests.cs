using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;
using PixelHero.Editor;
using PixelHero.Entities;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Sprites;
using PixelHero.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace PixelHero.Tests
{
    /// <summary>
    /// Fixes of the original client's bugs that the replay fixtures do not reach (ANALYSIS.md §9): the guild member list
    /// (#9, also patched in the fixtures' original) and the achievements window's rows (#8, DOM code the fixtures do
    /// not run).
    /// </summary>
    public class OriginalFixTests
    {
        static World CreateWorld()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            return new World(new ManualClock { Now = 0 }, map, SpriteDefinitions.ParseAll(catalog.SpritesJson.text, map.Info.TileSize), _ => false);
        }

        [Test]
        public void GuildMembersAreAddedOnce()
        {
            var player = CreateWorld().Player;
            player.SetGuild(new Dictionary<string, object>
            {
                ["name"] = "Night Owls",
                ["members"] = new List<object> { new Dictionary<string, object> { ["username"] = "keeper", ["rank"] = 7.0, ["serverId"] = 1.0 } }
            });

            player.AddGuildMember("helper", 2);
            player.AddGuildMember("keeper", 1);

            Assert.AreEqual(new[] { "keeper", "helper" }, player.Guild.Members.Select(member => member.Username).ToArray());
            Assert.AreEqual(2.0, player.Guild.Members[1].ServerId);
        }

        GameObject _canvas;

        [TearDown]
        public void DestroyCanvas()
        {
            if (_canvas != null) Object.DestroyImmediate(_canvas);
        }

        [Test]
        public void AchievementProgressUpdatesItsOwnRow()
        {
            var world = CreateWorld();
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            _canvas = new GameObject("canvas", typeof(RectTransform), typeof(Canvas));
            var view = new AchievementsView(new UiKit(catalog), (RectTransform)_canvas.transform, world.Player);

            Dictionary<string, object> Achievement(string key, string name, string region) => new Dictionary<string, object>
            {
                ["key"] = key, ["name"] = name, ["description"] = "", ["stage"] = 0.0, ["stageCount"] = 3.0, ["region"] = region
            };

            // Mudwich shows "first" and "third"; "second" (index 1 among all) is in another region.
            world.Player.LoadAchievements(new List<object>
            {
                Achievement("first", "First", "Mudwich"), Achievement("second", "Second", "Lakesworld"), Achievement("third", "Third", "Mudwich")
            });
            view.Handle(Opcodes.Achievement.Batch, null);
            Assert.AreEqual(new[] { "First", "Third" }, view.Titles.ToArray());

            // The original looked the row up by the index among all achievements (2): no row, nothing changed.
            world.Player.SetAchievement("third", 1, "Third (1/2)", null);
            view.Handle(Opcodes.Achievement.Progress, "third");
            Assert.AreEqual(new[] { "First", "Third (1/2)" }, view.Titles.ToArray());

            // A discovered secret achievement gets one row, and later updates change it.
            world.Player.SetAchievement("secret", null, "Hidden", "");
            view.Handle(Opcodes.Achievement.Progress, "secret");
            world.Player.SetAchievement("secret", 2, "Hidden (done)", null);
            view.Handle(Opcodes.Achievement.Progress, "secret");
            Assert.AreEqual(new[] { "First", "Third (1/2)", "Hidden (done)" }, view.Titles.ToArray());
        }
    }
}
