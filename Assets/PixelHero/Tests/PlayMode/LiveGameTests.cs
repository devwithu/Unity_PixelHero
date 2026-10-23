using System.Collections;
using System.IO;
using System.Linq;
using PixelHero.Entities;
using PixelHero.Game;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PixelHero.Tests
{
    /// <summary>
    /// The Game scene against a running PixelHero server: log in, show the map and entities, walk by a click.
    /// Writes Snapshots/game_live_before.png and _after.png. Ignored when the server is down. The environment
    /// variable PIXELHERO_SERVER_URL points the tests at a server other than the Game scene's.
    /// </summary>
    public class LiveGameTests
    {
        /// <summary>
        /// The login for live tests. Admin commands (/spawn, /kill) need an admin: without a database every player is
        /// one, so a guest will do; with a database the tests log in to the admin test account in .test-account.json
        /// (project root, not committed: {"username", "password"} of an account registered in the game, with its rank set to 2
        /// in the player_info collection).
        /// </summary>
        static PixelHero.Network.LoginRequest Account()
        {
            var file = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".test-account.json"));
            if (!File.Exists(file)) return PixelHero.Network.LoginRequest.Guest();
            var account = (System.Collections.Generic.Dictionary<string, object>)PixelHero.Js.JsJson.Parse(File.ReadAllText(file));
            return PixelHero.Network.LoginRequest.Login((string)account["username"], (string)account["password"]);
        }

        /// <summary>The scene's game client, pointed at PIXELHERO_SERVER_URL when that is set.</summary>
        static GameClient FindGame()
        {
            var game = Object.FindFirstObjectByType<GameClient>();
            var url = System.Environment.GetEnvironmentVariable("PIXELHERO_SERVER_URL");
            if (game != null && !string.IsNullOrEmpty(url)) game.ServerUrl = url;
            return game;
        }

        static void Chat(GameClient game, string text) =>
            game.Connection.Send(PixelHero.Common.Packets.Chat, new System.Collections.Generic.List<object> { text });

        static int FindSlot(GameClient game, string key)
        {
            for (var i = 0; i < PixelHero.UI.InventoryView.Size; i++)
                if (game.Inventory.GetSlot(i).Key == key) return i;
            return -1;
        }

        static double CountOf(GameClient game, string key)
        {
            var slot = FindSlot(game, key);
            return slot < 0 ? 0 : game.Inventory.GetSlot(slot).Count ?? 0;
        }

        static IEnumerator Until(System.Func<bool> condition, float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline && !condition()) yield return null;
        }

        /// <summary>
        /// Warp, crafting, settings and the welcome window against the server: a warp either teleports or answers
        /// with the cooldown notice; /opencrafting opens the crafting window with options and the first one's details;
        /// the settings change the brightness filter and the names on the text canvas. Writes game_warp.png,
        /// game_crafting.png, game_settings.png, game_welcome.png.
        /// </summary>
        /// <summary>
        /// Music areas (world.json areas.music) through the Music packet, and the sounds of a death and a respawn.
        /// /tp home is in Mudwich (smalltown), /tp kok in the meadow (meadowofthepast).
        /// </summary>
        [UnityTest]
        public IEnumerator MusicAreasAndSounds()
        {
            yield return new WaitForSecondsRealtime(6);
            PixelHero.UI.GameSettings.AudioEnabled = true;
            PixelHero.UI.GameSettings.MusicVolume = 25;

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);
            var player = game.World.Player;

            Chat(game, "/tp kok");
            yield return Until(() => game.Audio.CurrentMusic == "meadowofthepast", 8);
            Assert.AreEqual("meadowofthepast", game.Audio.CurrentMusic, "no meadow music");

            Chat(game, "/tp home");
            yield return Until(() => game.Audio.CurrentMusic == "smalltown", 8);
            Assert.AreEqual("smalltown", game.Audio.CurrentMusic, "no Mudwich music");
            Assert.IsNotNull(Object.FindAnyObjectByType<AudioListener>(), "no AudioListener: Unity would play nothing");
            // The crossfade reaches the music volume (25 / 100 × 0.5 × 2) after 3 s.
            yield return new WaitForSecondsRealtime(3.5f);
            Assert.AreEqual(0.25f, game.Audio.MusicVolumeNow, 1e-4f);

            game.Audio.Played.Clear();
            Chat(game, $"/kill {player.Name}");
            yield return Until(() => game.Intro.Visible, 10);
            Assert.IsTrue(player.Dead, "the player did not die");
            CollectionAssert.Contains(game.Audio.Played, "death");
            Assert.IsNull(game.Audio.CurrentMusic, "death stops the music");

            game.Respawn();
            yield return Until(() => !player.Dead, 5);
            Debug.Log($"[LiveGameTests] sounds: {string.Join(", ", game.Audio.Played)}; music {game.Audio.CurrentMusic}");
            Assert.AreEqual(new[] { "death", "revive", "revive" }, game.Audio.Played.ToArray(),
                "game.ts respawn and the Respawn packet each play revive");

            // Leave the account in Mudwich for the other tests.
            Chat(game, "/tp home");
            yield return Until(() => game.Audio.CurrentMusic == "smalltown", 8);
        }

        /// <summary>
        /// The dark cave by the world's three lamps (world.json areas.overlay 310, darkness 0.75): Overlay Set, the
        /// lamps of the surrounding regions, the dark mask on the #overlay canvas, and Remove on the way out.
        /// </summary>
        [UnityTest]
        public IEnumerator CaveOverlayAndLamps()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);
            var lights = game.World.Lights;

            Chat(game, "/teleport 162 665");
            yield return Until(() => lights.HasOverlay() && lights.Lightings.Count >= 3, 8);
            Assert.IsTrue(lights.HasOverlay(), "no overlay in the cave");
            Assert.AreEqual("rgba(0, 0, 0, 0.75)", lights.DarkColour);
            Assert.GreaterOrEqual(lights.Lightings.Count, 3, "the cave lamps did not arrive");
            yield return new WaitForSecondsRealtime(1.5f);
            var stamps = lights.Draw();
            Debug.Log($"[LiveGameTests] overlay '{lights.CurrentOverlay}' {lights.DarkColour}; stamps {string.Join(" | ", stamps)}");
            Assert.IsNotEmpty(stamps, "no lamp is in view");
            Capture("game_overlay.png");

            Chat(game, "/tp home");
            yield return Until(() => !lights.HasOverlay(), 8);
            Assert.IsFalse(lights.HasOverlay(), "the overlay stayed outside the cave");
        }

        /// <summary>
        /// Quest arrows (/pointer x y: a location pointer, after a Remove) on screen and past the edge, and the PVP
        /// flag (/togglepvp sets it; a teleport back to Mudwich clears it again).
        /// </summary>
        [UnityTest]
        public IEnumerator PointersAndPvp()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            var player = game.World.Player;
            var pointers = game.World.Pointers;
            Chat(game, "/tp home");
            yield return Until(() => (player.GridX, player.GridY) == (191, 166) && !player.Teleporting, 8);
            yield return new WaitForSecondsRealtime(0.5f);

            // Two tiles to the right of the player: on screen, above the tile.
            Chat(game, $"/pointer {player.GridX + 2} {player.GridY}");
            yield return Until(() => pointers.All.Count == 1 && pointers.All[0].Left != "", 5);
            Assert.AreEqual(1, pointers.All.Count, "no pointer");
            var arrow = pointers.All[0];
            Debug.Log($"[LiveGameTests] pointer: {arrow}");
            StringAssert.EndsWith("px", arrow.Left);
            Assert.AreEqual("", arrow.Right);
            yield return null;
            Assert.AreEqual(1, game.PointerView.Count);
            Capture("game_pointer.png");

            // Far to the east: the arrow sticks to the right edge, turned.
            Chat(game, $"/pointer {player.GridX + 60} {player.GridY}");
            yield return Until(() => pointers.All.Count == 1 && pointers.All[0].Right == "0", 5);
            Debug.Log($"[LiveGameTests] pointer: {pointers.All[0]}");
            Assert.AreEqual("0", pointers.All[0].Right, "an off-screen pointer sticks to the edge");
            StringAssert.Contains("rotate(-90deg)", pointers.All[0].Transform);
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_pointer_edge.png");

            Chat(game, "/togglepvp");
            yield return Until(() => game.World.Pvp, 5);
            Assert.IsTrue(game.World.Pvp, "no PVP flag");
            Chat(game, "/tp home");
            yield return Until(() => !game.World.Pvp, 8);
            Assert.IsFalse(game.World.Pvp, "the PVP flag stayed outside a PVP area");
        }

        /// <summary>
        /// The TeamWar lobby (world.json areas.minigame 311, around 373,919): Minigame Lobby packets every second, the
        /// countdown on the text canvas (renderer.ts drawMinigameGUI), and Exit on leaving the area. The account's
        /// abilities arrive as an Ability Batch at login.
        /// </summary>
        [UnityTest]
        public IEnumerator MinigameLobbyAndAbilities()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);
            var minigame = game.World.Minigame;
            // /addability sends Ability Add (level 1), /setability an Update, /resetabilities clears the account again.
            Chat(game, "/resetabilities");
            Chat(game, "/addability run");
            yield return Until(() => game.World.Player.Abilities["run"] != null, 5);
            Assert.IsNotNull(game.World.Player.Abilities["run"], "no Ability Add");
            Chat(game, "/setability run 2");
            yield return Until(() => game.World.Player.Abilities["run"].Level == 2, 5);
            Assert.AreEqual(2, game.World.Player.Abilities["run"].Level);
            Debug.Log($"[LiveGameTests] abilities: {string.Join(", ", game.World.Player.Abilities.Values.Select(a => $"{a.Key} {a.Level} {a.QuickSlot}"))}");
            Chat(game, "/resetabilities");

            Chat(game, "/teleport 373 919");
            yield return Until(() => minigame.Status == "lobby", 8);
            Assert.AreEqual("lobby", minigame.Status, "no lobby packet");
            Assert.AreEqual((int)PixelHero.Common.Opcodes.Minigame.TeamWar, minigame.Type);
            var countdown = minigame.Countdown;
            yield return Until(() => minigame.Countdown != countdown, 3);
            Assert.AreNotEqual(countdown, minigame.Countdown, "the countdown did not tick");
            yield return null;
            var texts = game.TextPlanner.Draws.Select(t => t.Text).Where(t => t != null && t.Contains("seconds")).ToList();
            Debug.Log($"[LiveGameTests] minigame: {string.Join(" | ", texts.Distinct())}");
            Assert.IsNotEmpty(texts, "no lobby text on the text canvas");
            Capture("game_minigame.png");

            Chat(game, "/tp home");
            yield return Until(() => !minigame.Exists(), 8);
            Assert.IsFalse(minigame.Exists(), "leaving the lobby did not exit the minigame");
        }

        [UnityTest]
        public IEnumerator WarpCraftingSettingsAndWelcome()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);
            var player = game.World.Player;

            // Warp
            game.Warp.Show();
            yield return new WaitForSecondsRealtime(0.4f);
            Capture("game_warp.png");
            var start = (player.GridX, player.GridY);
            var chatBefore = game.Hud.ChatMessages.Count;
            game.Warp.Select(0);
            yield return Until(() => (player.GridX, player.GridY) != start || game.Hud.ChatMessages.Count > chatBefore, 5);
            Debug.Log($"[LiveGameTests] warp: {start} -> {(player.GridX, player.GridY)}; chat: {string.Join(" | ", game.Hud.ChatMessages.Skip(chatBefore))}");
            Assert.IsTrue((player.GridX, player.GridY) != start || game.Hud.ChatMessages.Count > chatBefore, "the warp got no answer");
            Assert.IsFalse(game.Warp.IsVisible, "the map closes after a warp");

            // Crafting
            Chat(game, "/opencrafting");
            yield return Until(() => game.Crafting.IsVisible && game.Crafting.OptionCount > 0, 5);
            Assert.Greater(game.Crafting.OptionCount, 0, "no crafting options");
            yield return Until(() => game.Crafting.SelectedKey != "", 5);
            Assert.AreNotEqual("", game.Crafting.SelectedKey, "the first option's details did not come");
            Assert.AreEqual(PixelHero.UI.Localization.Get("crafting.craft"), game.Crafting.CraftLabel);
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_crafting.png");
            game.Crafting.Hide();

            // Settings
            var brightness = PixelHero.UI.GameSettings.Brightness;
            var names = PixelHero.UI.GameSettings.ShowNames;
            try
            {
                game.Settings.Show();
                PixelHero.UI.GameSettings.Brightness = 50;
                PixelHero.UI.GameSettings.ShowNames = false;
                game.ApplySettings();
                Assert.AreEqual(0.5f, game.BrightnessAlpha, 0.001f);
                Assert.IsFalse(game.TextPlanner.DrawNames);
                yield return new WaitForSecondsRealtime(0.4f);
                Capture("game_settings.png");
            }
            finally
            {
                PixelHero.UI.GameSettings.Brightness = brightness;
                PixelHero.UI.GameSettings.ShowNames = names;
                game.ApplySettings();
            }
            game.Settings.Hide();

            // Welcome
            game.Welcome.Show();
            yield return new WaitForSecondsRealtime(0.4f);
            Assert.IsTrue(game.Welcome.IsVisible);
            Capture("game_welcome.png");
            game.Welcome.Hide();
        }

        /// <summary>
        /// Guilds against the server: the list is requested on show; a too-short name is refused on the client; a guild
        /// with a chosen banner is created (the info page shows its name and the test account as a member); a guild
        /// chat line comes back; leaving (disbanding, as the owner) returns to the list. The guild is gone again at the
        /// end. Writes game_guild_create.png, game_guild.png.
        /// </summary>
        [UnityTest]
        public IEnumerator GuildCreateChatAndLeave()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);
            var player = game.World.Player;

            // The server refuses guilds during the tutorial ("You must finish the tutorial before creating a guild.").
            if (player.Quests["tutorial"] is { } tutorial && !tutorial.IsFinished())
            {
                Chat(game, "/finishquest tutorial");
                yield return Until(() => tutorial.IsFinished(), 5);
            }

            // A guild costs 30,000 gold ("You need 30,000 gold in your inventory to create a guild.").
            Chat(game, "/spawn gold 30000");
            yield return Until(() => CountOf(game, "gold") >= 30000, 5);

            game.Guilds.Show();
            if (player.Guild != null)
            {
                // A guild left over from an earlier run.
                game.Guilds.Leave();
                yield return Until(() => player.Guild == null && game.Guilds.ListShown, 5);
            }
            Assert.IsTrue(game.Guilds.ListShown, "the guild list is not shown");

            game.Guilds.OpenCreate();
            Assert.IsTrue(game.Guilds.CreateShown);
            game.Guilds.TypeName("ab");
            game.Guilds.ConfirmCreate();
            Assert.AreEqual(PixelHero.UI.Localization.Get("guilds.nameLength"), game.Guilds.Error, "a short name is refused on the client");

            game.Guilds.ChooseColour(PixelHero.Common.Modules.BannerColour.Red);
            game.Guilds.NextOutline();
            game.Guilds.TypeName("unitytest");
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_guild_create.png");
            game.Guilds.ConfirmCreate();
            yield return Until(() => game.Guilds.InfoShown, 5);
            Assert.IsTrue(game.Guilds.InfoShown, $"no guild Login; error '{game.Guilds.Error}'");
            StringAssert.Contains("unitytest", game.Guilds.GuildName.ToLowerInvariant());
            Assert.Contains("unityadmin", game.Guilds.Members.ToList());
            Assert.IsNotNull(player.Guild);

            var guildPackets = new System.Collections.Generic.List<string>();
            game.Connection.PacketReceived += packet =>
            {
                if (packet.Id == PixelHero.Common.Packets.Guild) guildPackets.Add(packet.ToString());
            };
            // The server reads the guild back from the database for each chat line, and does not wait for the save
            // after creating it; a line sent right away can be dropped. Wait a moment and send again if nothing comes.
            game.Guilds.OpenChat();
            for (var attempt = 0; attempt < 3 && game.Guilds.ChatLines == 0; attempt++)
            {
                yield return new WaitForSecondsRealtime(1);
                game.Guilds.TypeChat("hello guild");
                game.Guilds.SendChatMessage();
                yield return Until(() => game.Guilds.ChatLines > 0, 3);
            }
            Debug.Log($"[LiveGameTests] guild packets: {string.Join(" | ", guildPackets)}");
            Assert.Greater(game.Guilds.ChatLines, 0, "the guild chat line did not come back");
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_guild.png");

            game.Guilds.Leave();
            yield return Until(() => player.Guild == null && game.Guilds.ListShown, 5);
            Assert.IsNull(player.Guild, "leaving did not clear the guild");
            Assert.IsTrue(game.Guilds.ListShown, "leaving did not return to the list");
            game.Guilds.Hide();
        }

        /// <summary>
        /// Friends, the interact menu and trading against the server, with a second player: a headless guest
        /// connection brought next to the test account by the admin /teletome. The interact menu on the guest offers
        /// follow, trade and add-friend; both players request the trade, the window opens, an offered burger shows in
        /// the offer slots and closing tells the other side. A friend (the offline unitytest1 account) is added and
        /// removed through the popup. Writes game_trade.png, game_friends.png.
        /// </summary>
        [UnityTest]
        public IEnumerator FriendsInteractAndTrade()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            var player = game.World.Player;

            // The second player: a guest on its own connection, answering only what it needs to.
            yield return new WaitForSecondsRealtime(6); // the server refuses a second login from this address within 5 s
            var other = new PixelHero.Network.GameConnection(PixelHero.Network.WebSocketTransportFactory.Create());
            string otherInstance = null, otherName = null;
            var otherPackets = new System.Collections.Generic.List<PixelHero.Network.Packet>();
            other.PacketReceived += packet =>
            {
                otherPackets.Add(packet);
                if (packet.Id == PixelHero.Common.Packets.Welcome && packet.Arg(0) is System.Collections.Generic.Dictionary<string, object> welcome)
                {
                    otherInstance = (string)welcome["instance"];
                    otherName = (string)welcome["name"];
                    other.Send(PixelHero.Common.Packets.Ready, new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["regionsLoaded"] = 0.0, ["userAgent"] = "unity-test"
                    });
                }
            };
            other.Connect(game.ServerUrl, PixelHero.Network.LoginRequest.Guest());
            try
            {
                var deadline = Time.realtimeSinceStartup + 15;
                while (Time.realtimeSinceStartup < deadline && otherInstance == null)
                {
                    other.Update();
                    yield return null;
                }
                Assert.IsNotNull(otherInstance, "the second player got no Welcome");

                Chat(game, $"/teletome {otherName}");
                deadline = Time.realtimeSinceStartup + 10;
                while (Time.realtimeSinceStartup < deadline && game.World.Entities.Get(otherInstance) == null)
                {
                    other.Update();
                    yield return null;
                }
                var guest = game.World.Entities.Get(otherInstance);
                Assert.IsNotNull(guest, "the second player did not appear next to the test account");

                // The interact menu on the other player.
                game.Interact.Show(new Vector2(100, 100), new Vector2(640, 480), guest, false);
                CollectionAssert.AreEqual(new[] { PixelHero.Common.Modules.MenuActions.Follow, PixelHero.Common.Modules.MenuActions.Trade,
                    PixelHero.Common.Modules.MenuActions.AddFriend }, game.Interact.Actions);
                game.Interact.Hide();

                // Trade: both request it (the interact Trade sends the request on arrival; both stand on one tile).
                Chat(game, "/spawn burger 2");
                other.Send(PixelHero.Common.Packets.Trade, new System.Collections.Generic.Dictionary<string, object>
                {
                    ["opcode"] = (double)(int)PixelHero.Common.Opcodes.Trade.Request, ["instance"] = player.Instance
                });
                for (var i = 0; i < 30; i++)
                {
                    other.Update();
                    yield return null;
                }
                game.Connection.Send(PixelHero.Common.Packets.Trade, new System.Collections.Generic.Dictionary<string, object>
                {
                    ["opcode"] = (double)(int)PixelHero.Common.Opcodes.Trade.Request, ["instance"] = otherInstance
                });
                deadline = Time.realtimeSinceStartup + 5;
                while (Time.realtimeSinceStartup < deadline && !game.Trade.IsVisible)
                {
                    other.Update();
                    yield return null;
                }
                Assert.IsTrue(game.Trade.IsVisible, "the trade window did not open");

                var burger = FindSlot(game, "burger");
                Assert.GreaterOrEqual(burger, 0);
                game.Trade.InventoryGrid.Click(burger);
                deadline = Time.realtimeSinceStartup + 5;
                while (Time.realtimeSinceStartup < deadline && game.Trade.PlayerGrid.KeyAt(0) != "burger")
                {
                    other.Update();
                    yield return null;
                }
                Assert.AreEqual("burger", game.Trade.PlayerGrid.KeyAt(0), "the offer did not show");
                Assert.IsTrue(otherPackets.Any(p => p.Id == PixelHero.Common.Packets.Trade), "the other side saw nothing");
                yield return new WaitForSecondsRealtime(0.3f);
                Capture("game_trade.png");

                otherPackets.Clear();
                game.Trade.Hide();
                deadline = Time.realtimeSinceStartup + 5;
                while (Time.realtimeSinceStartup < deadline &&
                       !otherPackets.Any(p => p.Id == PixelHero.Common.Packets.Trade && p.Arg(0) is double o && (int)o == (int)PixelHero.Common.Opcodes.Trade.Close))
                {
                    other.Update();
                    yield return null;
                }
                Assert.IsTrue(otherPackets.Any(p => p.Id == PixelHero.Common.Packets.Trade), "closing did not reach the other side");
            }
            finally
            {
                other.Dispose();
            }

            // Friends: add and remove an existing (offline) account through the popup.
            game.Friends.Show();
            if (game.Friends.Usernames.Contains("unitytest1"))
            {
                game.Friends.OpenPopup(true);
                game.Friends.Type("unitytest1");
                game.Friends.HandleConfirm();
                yield return Until(() => !game.Friends.Usernames.Contains("unitytest1"), 5);
            }
            game.Friends.OpenPopup(false);
            game.Friends.Type("unitytest1");
            game.Friends.HandleConfirm();
            yield return Until(() => game.Friends.Usernames.Contains("unitytest1"), 5);
            Assert.Contains("unitytest1", game.Friends.Usernames.ToList(), "the friend was not added");
            Assert.AreEqual(PixelHero.UI.Localization.Get("friends.offline"), game.Friends.WorldText("unitytest1"));
            yield return new WaitForSecondsRealtime(0.5f);
            Capture("game_friends.png");

            game.Friends.OpenPopup(true);
            game.Friends.Type("unitytest1");
            game.Friends.HandleConfirm();
            yield return Until(() => !game.Friends.Usernames.Contains("unitytest1"), 5);
            Assert.IsFalse(game.Friends.Usernames.Contains("unitytest1"), "the friend was not removed");
        }

        /// <summary>
        /// The profile's pages and the equipments window against the server: the skills page lists the skills sent at
        /// login and shows one's details; the equipments window draws the player with a worn weapon over the base
        /// sprite, turns it, and unequips from its slot. Writes game_skills.png, game_equipments.png.
        /// </summary>
        [UnityTest]
        public IEnumerator ProfilePagesAndEquipments()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);
            var player = game.World.Player;

            // Skills page
            game.Profile.Show();
            Assert.AreEqual(0, game.Profile.ActivePage);
            game.Profile.Navigate(1);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.AreEqual(1, game.Profile.ActivePage);
            Assert.Greater(player.Skills.Count, 0, "no skill batch");
            Assert.AreEqual(player.Skills.Count, game.Profile.Skills.RowCount, "one row per skill");
            game.Profile.Skills.ShowInfo((int)PixelHero.Common.Modules.Skills.Health);
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.IsTrue(game.Profile.Skills.InfoVisible);
            Capture("game_skills.png");
            game.Profile.Navigate(-1);
            Assert.AreEqual(0, game.Profile.ActivePage);
            Assert.IsFalse(game.Profile.Skills.InfoVisible, "leaving the page hides the info");
            game.Profile.Hide();

            // Equipments window with a worn weapon
            if (player.GetWeapon().Key == "")
            {
                Chat(game, "/spawn bronzebattleaxe 1");
                yield return Until(() => FindSlot(game, "bronzebattleaxe") >= 0, 5);
                game.Inventory.Click(FindSlot(game, "bronzebattleaxe"), true);
                yield return Until(() => player.GetWeapon().Key != "", 5);
            }
            Assert.AreNotEqual("", player.GetWeapon().Key, "no weapon to show");

            game.Equipments.Show();
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.GreaterOrEqual(game.Equipments.LayerCount, 2, "the base sprite and the weapon");
            game.Equipments.Navigate(1);
            Assert.AreEqual(PixelHero.Common.Modules.Orientation.Left, game.Equipments.Orientation, "down → left");
            game.Equipments.Navigate(-1);
            yield return new WaitForSecondsRealtime(0.2f);
            Capture("game_equipments.png");

            game.Equipments.ClickSlot(PixelHero.Common.Modules.Equipment.Weapon);
            yield return Until(() => player.GetWeapon().Key == "", 5);
            Assert.AreEqual("", player.GetWeapon().Key, "the equipments window did not unequip");
            game.Equipments.Hide();
        }

        /// <summary>
        /// Store and bank against the server (admin /store and /bank open them): the store lists its items and
        /// mirrors the inventory; selecting a burger puts it in the sell slot and selling it lowers the stack; buying
        /// one adds it back; the bank takes a click on an inventory slot and gives it back from the bank side. The
        /// enchant window mirrors the inventory. Writes game_store.png, game_bank.png, game_enchant.png.
        /// </summary>
        [UnityTest]
        public IEnumerator StoreBankAndEnchant()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);

            Chat(game, "/empty");
            yield return Until(() => Enumerable.Range(0, PixelHero.UI.InventoryView.Size).All(i => game.Inventory.IsEmpty(i)), 5);
            Chat(game, "/spawn gold 10000");
            Chat(game, "/spawn burger 3");
            yield return Until(() => FindSlot(game, "burger") >= 0 && FindSlot(game, "gold") >= 0, 5);
            var burger = FindSlot(game, "burger");

            // Store
            Chat(game, "/store startshop");
            yield return Until(() => game.Store.IsVisible && game.Store.ItemCount > 0, 5);
            Assert.AreEqual("startshop", game.Store.Key);
            Assert.AreEqual("burger", game.Store.InventoryGrid.KeyAt(burger), "the store mirrors the inventory");
            game.Store.InventoryGrid.Click(burger);
            yield return Until(() => game.Store.SelectedIndex == burger, 5);
            Assert.AreEqual(burger, game.Store.SelectedIndex, "no Store Select answer");
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_store.png");

            game.Store.ClickSell();
            // The store sells the selected slot's whole count.
            yield return Until(() => CountOf(game, "burger") != 3, 5);
            Debug.Log($"[LiveGameTests] after selling: burger {CountOf(game, "burger")}, gold {CountOf(game, "gold")}");
            Assert.AreEqual(0, CountOf(game, "burger"), "selling did not sell the stack");
            Assert.Greater(CountOf(game, "gold"), 10000, "selling paid nothing");

            var burgersBefore = CountOf(game, "burger");
            game.Store.ClickItem(4); // startshop item 4 is the burger
            game.Store.BuyDialog.Accept();
            yield return Until(() => CountOf(game, "burger") > burgersBefore, 5);
            Assert.Greater(CountOf(game, "burger"), burgersBefore, "buying did not add a burger");
            game.Store.Hide();

            // Bank
            Chat(game, "/bank");
            yield return Until(() => game.Bank.IsVisible, 5);
            var slot = FindSlot(game, "burger");
            game.Bank.InventoryGrid.Click(slot);
            yield return Until(() => FindSlot(game, "burger") < 0, 5);
            Assert.AreEqual(-1, FindSlot(game, "burger"), "the deposit did not leave the inventory");
            var banked = Enumerable.Range(0, PixelHero.UI.BankView.Size).FirstOrDefault(i => game.Bank.BankGrid.KeyAt(i) == "burger");
            Assert.AreEqual("burger", game.Bank.BankGrid.KeyAt(banked), "the burger is not in the bank");
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_bank.png");

            game.Bank.BankGrid.Click(banked);
            yield return Until(() => FindSlot(game, "burger") >= 0, 5);
            Assert.GreaterOrEqual(FindSlot(game, "burger"), 0, "the withdrawal did not come back");
            game.Bank.Hide();

            // Enchant: no command opens it; the window mirrors the inventory.
            game.Enchant.Show();
            yield return new WaitForSecondsRealtime(0.3f);
            Assert.AreEqual("gold", game.Enchant.Grid.KeyAt(FindSlot(game, "gold")));
            Capture("game_enchant.png");
            game.Enchant.Hide();
        }

        /// <summary>
        /// Quests and achievements against the server: the batches sent at login fill the quest list (the log shows
        /// the tutorial) and the achievements of the first region; the quest dialog shows a quest (not accepted).
        /// Writes game_quests.png, game_achievements.png, game_quest_dialog.png.
        /// </summary>
        [UnityTest]
        public IEnumerator QuestsAndAchievements()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);

            var player = game.World.Player;
            Assert.Greater(player.Quests.Count, 0, "no quest batch");
            Assert.Greater(player.Achievements.Count, 0, "no achievement batch");
            Assert.AreEqual(player.Quests.Count, game.Quests.Names.Count, "every quest gets a list row");
            Assert.AreEqual(player.Quests["tutorial"].Name, game.Quests.ShownName, "the log shows the tutorial");
            Debug.Log($"[LiveGameTests] quests: {string.Join(", ", game.Quests.Names)}; regions: {string.Join(", ", game.Achievements.Regions)}");

            game.Quests.Show();
            yield return new WaitForSecondsRealtime(0.5f);
            Capture("game_quests.png");

            game.Achievements.Show();
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(game.Quests.IsVisible);
            Assert.AreEqual("Mudwich", game.Achievements.CurrentRegion);
            Assert.Greater(game.Achievements.Titles.Count, 0, "no achievements in Mudwich");
            Capture("game_achievements.png");
            game.Achievements.Hide();

            var offered = player.Quests.Keys.First(key => key != "tutorial");
            game.QuestDialog.Handle(offered);
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsTrue(game.QuestDialog.IsVisible);
            Assert.AreEqual(offered, game.QuestDialog.ActiveQuest);
            Capture("game_quest_dialog.png");
            game.QuestDialog.Hide();
        }

        /// <summary>
        /// Inventory, equipment and bubbles against the server: items given by the admin /spawn command show up in
        /// the slots, a double click equips the weapon (profile slot), a click on the slot unequips it, dropping one
        /// lowers a stack, and a chat line puts a bubble over the player. Writes game_inventory.png, game_profile.png.
        /// </summary>
        [UnityTest]
        public IEnumerator InventoryEquipmentAndBubble()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());
            yield return Until(() => game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded, 20);
            if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);

            if (game.World.Player.GetWeapon().Key != "")
            {
                game.Profile.ClickSlot(PixelHero.Common.Modules.Equipment.Weapon);
                yield return Until(() => game.World.Player.GetWeapon().Key == "", 5);
            }
            Chat(game, "/empty");
            yield return Until(() => Enumerable.Range(0, PixelHero.UI.InventoryView.Size).All(i => game.Inventory.IsEmpty(i)), 5);

            Chat(game, "/spawn bronzebattleaxe 1");
            Chat(game, "/spawn burger 3");
            yield return Until(() => FindSlot(game, "bronzebattleaxe") >= 0 && FindSlot(game, "burger") >= 0, 5);
            var axe = FindSlot(game, "bronzebattleaxe");
            var burger = FindSlot(game, "burger");
            Assert.GreaterOrEqual(axe, 0, "the battleaxe did not arrive");
            Assert.GreaterOrEqual(burger, 0, "the burger did not arrive");
            Assert.AreEqual(3, game.Inventory.GetSlot(burger).Count);

            game.Inventory.Show();
            game.Inventory.Click(burger);
            yield return new WaitForSecondsRealtime(0.5f);
            Capture("game_inventory.png");

            // Drop one burger from the action menu.
            game.Inventory.Actions.Press(PixelHero.Common.Modules.MenuActions.DropOne);
            yield return Until(() => game.Inventory.GetSlot(burger).Count == 2, 5);
            Assert.AreEqual(2, game.Inventory.GetSlot(burger).Count, "drop one did not lower the stack");

            // A double click equips the weapon.
            game.Inventory.Click(axe, true);
            yield return Until(() => game.World.Player.GetWeapon().Key.EndsWith("bronzebattleaxe"), 5);
            StringAssert.EndsWith("bronzebattleaxe", game.World.Player.GetWeapon().Key, "the weapon was not equipped");
            Assert.AreEqual(-1, FindSlot(game, "bronzebattleaxe"));

            game.Profile.Show();
            yield return new WaitForSecondsRealtime(0.5f);
            Assert.IsFalse(game.Inventory.IsVisible, "showing the profile should hide the inventory");
            Capture("game_profile.png");

            game.Profile.ClickSlot(PixelHero.Common.Modules.Equipment.Weapon);
            yield return Until(() => FindSlot(game, "bronzebattleaxe") >= 0, 5);
            Assert.GreaterOrEqual(FindSlot(game, "bronzebattleaxe"), 0, "unequipping did not return the weapon");
            Assert.AreEqual("", game.World.Player.GetWeapon().Key);
            game.Profile.Hide();

            Chat(game, "bubble test");
            yield return Until(() => game.Bubbles.TextOf(game.World.Player.Instance) != null, 5);
            Assert.AreEqual("bubble test", game.Bubbles.TextOf(game.World.Player.Instance));
            yield return new WaitForSecondsRealtime(0.3f);
            Capture("game_bubble.png");
        }

        /// <summary>
        /// The HUD against the server: health and mana texts after Welcome, a chat message coming back, then death
        /// by the admin /kill command (every player is an admin without a database), the death scroll and respawn.
        /// Writes Snapshots/game_hud.png and game_death.png.
        /// </summary>
        [UnityTest]
        public IEnumerator HudShowsChatDeathAndRespawn()
        {
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;
            var game = FindGame();
            game.Login(Account());

            var deadline = Time.realtimeSinceStartup + 20;
            while (Time.realtimeSinceStartup < deadline && !(game.Entered && game.World.Player.Sprite != null && game.World.Player.Sprite.Loaded))
            {
                if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
                yield return null;
            }
            Assert.IsTrue(game.Entered, $"no Welcome; status '{game.Status}'");
            yield return new WaitForSecondsRealtime(1);

            var player = game.World.Player;
            StringAssert.IsMatch(@"^\d+/\d+$", game.Hud.HealthText);
            StringAssert.IsMatch(@"^\d+/\d+$", game.Hud.ManaText);

            game.Connection.Send(PixelHero.Common.Packets.Chat, new System.Collections.Generic.List<object> { "hello from unity" });
            deadline = Time.realtimeSinceStartup + 5;
            while (Time.realtimeSinceStartup < deadline && game.Hud.ChatMessages.Count == 0) yield return null;
            Assert.IsNotEmpty(game.Hud.ChatMessages, "nothing came back to the chat");
            Debug.Log($"[LiveGameTests] chat: {string.Join(" | ", game.Hud.ChatMessages)}");
            Capture("game_hud.png");

            game.Connection.Send(PixelHero.Common.Packets.Chat, new System.Collections.Generic.List<object> { $"/kill {player.Name}" });
            deadline = Time.realtimeSinceStartup + 10;
            while (Time.realtimeSinceStartup < deadline && !game.Intro.Visible) yield return null;
            Assert.IsTrue(player.Dead, "the player did not die");
            Assert.IsTrue(game.Intro.Visible, "the death scroll is not shown");
            yield return new WaitForSecondsRealtime(0.6f);
            Capture("game_death.png");

            game.Respawn();
            deadline = Time.realtimeSinceStartup + 5;
            while (Time.realtimeSinceStartup < deadline && player.Dead) yield return null;
            Assert.IsFalse(player.Dead, "no respawn");
            Assert.IsFalse(game.Intro.Visible, "the death scroll stayed");
        }

        [UnityTest]
        public IEnumerator GameSceneLogsInAndWalksOnAClick()
        {
            // The server rejects a reconnect from the same IP within 5 s (LiveServerTests may have just connected).
            yield return new WaitForSecondsRealtime(6);

            SceneManager.LoadScene("Game");
            yield return null;

            var game = FindGame();
            Assert.IsNotNull(game, "Game scene has no GameClient (run PixelHero.Editor.GameSceneBuilder.BuildBatch)");

            // The title screen first; then log in as a guest, as the guest checkbox and LOGIN do.
            yield return null;
            Assert.IsTrue(game.Intro.Visible, "the title screen is not shown");
            yield return new WaitForSecondsRealtime(0.6f);
            Capture("game_intro_ko.png");
            PixelHero.UI.Localization.SetLanguage("en");
            yield return null;
            Capture("game_intro_en.png");
            PixelHero.UI.Localization.SetLanguage("ko");
            game.Intro.OpenScroll("credits");
            yield return new WaitForSecondsRealtime(0.6f);
            Capture("game_intro_credits.png");
            game.Intro.OpenScroll("load-character");
            game.Login(Account());

            var deadline = Time.realtimeSinceStartup + 20;
            while (Time.realtimeSinceStartup < deadline &&
                   !(game.World?.Player.Sprite != null && game.World.Player.Sprite.Loaded && game.World.Entities.Entities.Count > 1))
            {
                if (game.Status.StartsWith("Couldn't connect")) Assert.Ignore($"Original server not reachable: {game.Status}");
                yield return null;
            }

            var world = game.World;
            Assert.IsNotNull(world.Player.Sprite, $"no Welcome; status '{game.Status}'");
            Assert.IsTrue(game.Entered);

            // Let fading and region loading settle, then picture the screen.
            yield return new WaitForSecondsRealtime(1.5f);
            Assert.IsFalse(game.Intro.Visible, "the title screen did not fade out");
            Capture("game_live_before.png");

            var player = world.Player;
            var startX = player.GridX;
            var startY = player.GridY;

            // A free tile two steps away (not an object such as a tree, whose click means "cut").
            var target = new[] { (2, 0), (-2, 0), (0, 2), (0, -2), (1, 1), (-1, -1) }
                .Select(d => (x: startX + d.Item1, y: startY + d.Item2))
                .First(t => !world.Map.IsColliding(t.x, t.y) && !world.Map.IsObject(t.x, t.y) && world.GetEntityAt(t.x, t.y) == null);

            world.Input.Click(new WorldPosition { X = target.x * 16 + 8, Y = target.y * 16 + 8, GridX = target.x, GridY = target.y });

            deadline = Time.realtimeSinceStartup + 5;
            while (Time.realtimeSinceStartup < deadline && (player.GridX != target.x || player.GridY != target.y || player.Moving))
                yield return null;

            yield return new WaitForSecondsRealtime(0.5f);
            Capture("game_live_after.png");

            Debug.Log($"[LiveGameTests] {player.Name} walked {startX},{startY} -> {player.GridX},{player.GridY}; entities {world.Entities.Entities.Count}; status '{game.Status}'");
            Assert.AreEqual((target.x, target.y), (player.GridX, player.GridY), "the player did not reach the clicked tile");
            Assert.IsFalse(game.Status.StartsWith("Disconnected"), game.Status);
        }

        static void Capture(string name)
        {
            var camera = UnityEngine.Camera.main;
            var texture = new RenderTexture(Screen.width, Screen.height, 24);
            camera.targetTexture = texture;
            camera.Render();
            RenderTexture.active = texture;
            var image = new Texture2D(texture.width, texture.height, TextureFormat.RGB24, false);
            image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            image.Apply();
            camera.targetTexture = null;
            RenderTexture.active = null;

            var directory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Snapshots"));
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name), image.EncodeToPNG());
        }
    }
}
