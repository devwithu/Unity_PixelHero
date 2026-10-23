using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PixelHero.Common;
using PixelHero.Editor;
using PixelHero.Entities;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Network;
using PixelHero.Sprites;
using NUnit.Framework;
using UnityEditor;

namespace PixelHero.Tests
{
    /// <summary>
    /// S5: the recorded session replayed through the C# entity port on a virtual clock, frame by frame, against the
    /// same replay through the original client code (Tools/fixtures/gen_entity_replay.mts). Every change of the
    /// entity table and every packet the client sends must match.
    /// </summary>
    public class EntityReplayTests
    {
        const double T0 = 1_700_000_000_000;
        const int Frame = 16;

        [Test]
        public void RecordingReplayMatchesTheOriginalClientFrameByFrame()
        {
            var frames = new List<(double t, List<Packet> packets, List<object> input)>();
            foreach (var file in Directory.GetFiles(TestPaths.Recordings, "*.jsonl").OrderBy(f => Path.GetFileName(f), System.StringComparer.Ordinal))
            foreach (var line in File.ReadLines(file).Where(l => l.Trim().Length > 0))
            {
                var entry = (Dictionary<string, object>)JsJson.Parse(line);
                if (entry.TryGetValue("dir", out var dir) && (string)dir == "s2c")
                    frames.Add(((double)entry["t"], PacketFrames.Parse((string)entry["data"]), null));
            }

            Replay("entity_replay.json", frames);
        }

        /// <summary>Tools/fixtures/sessions/combat.jsonl: a scripted fight against the real server (Tools/net/combat_session.mjs).</summary>
        [Test]
        public void CombatSessionMatchesTheOriginalClientFrameByFrame() =>
            Replay("combat_session.json", RecordingFrames(Path.Combine(TestPaths.Fixtures, "..", "sessions", "combat.jsonl")));

        public static List<(double t, List<Packet> packets, List<object> input)> RecordingFrames(string file)
        {
            var frames = new List<(double t, List<Packet> packets, List<object> input)>();
            foreach (var line in File.ReadLines(file).Where(l => l.Trim().Length > 0))
            {
                var entry = (Dictionary<string, object>)JsJson.Parse(line);
                if (entry.TryGetValue("dir", out var dir) && (string)dir == "s2c")
                    frames.Add(((double)entry["t"], PacketFrames.Parse((string)entry["data"]), null));
            }
            return frames;
        }

        /// <summary>The harness's Math.random: Park–Miller from seed 1.</summary>
        public static System.Func<double> SeededRandom()
        {
            long seed = 1;
            return () => (seed = seed * 16807 % 2147483647) / 2147483647.0;
        }

        /// <summary>Tools/fixtures/scenes/entity_draw_scene.json: the drawing cases the recordings lack.</summary>
        [Test]
        public void DrawSceneMatchesTheOriginalClientFrameByFrame() => Replay("entity_draw_scene.json", SceneFrames("entity_draw_scene"));

        /// <summary>Tools/fixtures/scenes/movement_scene.json: clicks and WASD through the input and player handler.</summary>
        [Test]
        public void MovementSceneMatchesTheOriginalClientFrameByFrame() => Replay("movement_scene.json", SceneFrames("movement_scene"));

        /// <summary>Tools/fixtures/scenes/overlay_scene.json: the overlay, lamps, lanterns and the dark mask.</summary>
        [Test]
        public void OverlaySceneMatchesTheOriginalClientFrameByFrame() => Replay("overlay_scene.json", SceneFrames("overlay_scene"));

        /// <summary>Tools/fixtures/scenes/misc_scene.json: pointers, resources, PVP, commands and the camera packet.</summary>
        [Test]
        public void MiscSceneMatchesTheOriginalClientFrameByFrame() => Replay("misc_scene.json", SceneFrames("misc_scene"));

        public static List<(double t, List<Packet> packets, List<object> input)> SceneFrames(string name)
        {
            var scene = (Dictionary<string, object>)JsJson.Parse(File.ReadAllText(
                Path.Combine(TestPaths.Fixtures, "..", "scenes", name + ".json")));
            return ((List<object>)scene["frames"]).Cast<Dictionary<string, object>>()
                .Select(frame => ((double)frame["t"], PacketFrames.Parse(JsJson.Stringify(frame["packets"])),
                    frame.TryGetValue("input", out var input) ? (List<object>)input : null))
                .ToList();
        }

        /// <summary>What the harness's handleInput does: handleLeftClick, or handleKeyDown / handleKeyUp.</summary>
        public static void HandleInput(World world, Dictionary<string, object> action)
        {
            if (action.TryGetValue("click", out var click))
            {
                var tile = (List<object>)click;
                int gridX = (int)(double)tile[0], gridY = (int)(double)tile[1];
                var at = action.TryGetValue("at", out var a) ? (List<object>)a : null;
                world.Input.Click(new WorldPosition
                {
                    X = at != null ? (double)at[0] : gridX * 16 + 8,
                    Y = at != null ? (double)at[1] : gridY * 16 + 8,
                    GridX = gridX,
                    GridY = gridY
                });
            }

            if (action.TryGetValue("key", out var key))
            {
                var direction = (string)key switch
                {
                    "w" => Modules.Orientation.Up,
                    "a" => Modules.Orientation.Left,
                    "s" => Modules.Orientation.Down,
                    _ => Modules.Orientation.Right
                };
                world.Input.SetMovementKey(direction, action.TryGetValue("down", out var down) && down is bool d && d);
            }
        }

        static void Replay(string fixtureName, List<(double t, List<Packet> packets, List<object> input)> frames)
        {
            var fixture = (Dictionary<string, object>)JsJson.Parse(File.ReadAllText(Path.Combine(TestPaths.Fixtures, fixtureName)));
            var expectedChanges = ((List<object>)fixture["changes"]).Cast<List<object>>().ToList();
            var dumps = ((List<object>)fixture["dumps"]).Cast<List<object>>().ToDictionary(d => (int)(double)d[0], d => (string)d[1]);
            var expectedSent = ((List<object>)fixture["sent"]).Cast<List<object>>().Select(s => $"{(int)(double)s[0]} {s[1]}").ToList();
            var totalFrames = (int)(double)fixture["frames"];

            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            map.LoadRegions(RegionCache.FromGzip(catalog.RegionCacheGzip.bytes).GetAllRegions());

            var clock = new ManualClock { Now = T0 };
            var definitions = SpriteDefinitions.ParseAll(catalog.SpritesJson.text, map.Info.TileSize);
            var frameIndex = 0;
            var sent = new List<string>();

            var world = new World(clock, map, definitions, key => catalog.GetTexture("sprites/" + key) != null);
            world.Send = (id, data) => sent.Add($"{frameIndex} {PacketFrames.Serialize(id, data)}");
            var packets = new EntityPackets(world);

            // The original Camera on a 960x640 screen at zoom 3, centred by Welcome and the main player's teleports.
            var screen = (Dictionary<string, object>)fixture["screen"];
            var camera = new Camera(map.Width, map.Height, map.Info.TileSize, (double)screen["width"], (double)screen["height"]);
            camera.ZoomFactor = (double)fixture["zoom"];
            camera.Update();
            world.Camera = camera;
            world.Lights.CanvasWidth = (double)screen["width"];
            world.Lights.CanvasHeight = (double)screen["height"];
            world.Pointers.CanvasWidth = (double)screen["width"];
            world.Pointers.CanvasHeight = (double)screen["height"];
            world.PerformanceNow = () => clock.Now - T0 + 1000;
            world.Random = SeededRandom();
            var sounds = new List<string>();
            world.PlaySound = (sound, target) => sounds.Add($"{frameIndex} sound {sound} {target?.Instance ?? "-"}");
            world.PlayMusic = music => sounds.Add($"{frameIndex} music {music ?? "-"}");
            world.StopMusic = () => sounds.Add($"{frameIndex} stop");
            var expectedSounds = ((List<object>)fixture["sounds"]).Cast<List<object>>().Select(s => $"{(int)(double)s[0]} {s[1]}").ToList();

            var planner = new EntityDrawPlanner(world, camera);
            var drawLines = new List<string>();
            var textPlanner = new TextDrawPlanner(world, camera);
            var textLines = new List<string>();
            textPlanner.DrawRecorded = draw => textLines.Add(TextLine(draw));
            textPlanner.CanvasWidth = (double)screen["width"];
            planner.TextCall = (kind, entity) =>
            {
                drawLines.Add($"{kind} {entity.Instance}");
                textPlanner.Call(kind, entity);
            };
            var expectedTexts = ((List<object>)fixture["textChanges"]).Cast<List<object>>().ToList();
            var textDumps = ((List<object>)fixture["textDumps"]).Cast<List<object>>().ToDictionary(d => (int)(double)d[0], d => (string)d[1]);
            var textChanges = 0;
            var previousTexts = "";
            planner.DrawRecorded = draw => drawLines.Add(DrawLine(draw));
            var drawSample = (int)(double)fixture["drawSample"];
            var expectedDraws = ((List<object>)fixture["drawSamples"]).Cast<List<object>>().ToList();
            var drawDumps = ((List<object>)fixture["drawDumps"]).Cast<List<object>>().ToDictionary(d => (int)(double)d[0], d => (string)d[1]);
            var drawSamples = 0;
            var expectedOverlays = ((List<object>)fixture["overlayChanges"]).Cast<List<object>>().ToList();
            var overlayDumps = ((List<object>)fixture["overlayDumps"]).Cast<List<object>>().ToDictionary(d => (int)(double)d[0], d => (string)d[1]);
            var overlayChanges = 0;
            var previousOverlay = "";
            var expectedPointers = ((List<object>)fixture["pointerChanges"]).Cast<List<object>>().ToList();
            var pointerDumps = ((List<object>)fixture["pointerDumps"]).Cast<List<object>>().ToDictionary(d => (int)(double)d[0], d => (string)d[1]);
            var pointerChanges = 0;
            var previousPointers = "";
            var expectedMisc = ((List<object>)fixture["miscChanges"]).Cast<List<object>>().ToList();
            var miscDumps = ((List<object>)fixture["miscDumps"]).Cast<List<object>>().ToDictionary(d => (int)(double)d[0], d => (string)d[1]);
            var miscChanges = 0;
            var previousMisc = "";

            var changes = 0;
            var previous = "";
            var next = 0;

            for (frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                clock.Now = T0 + frameIndex * Frame;
                world.Timers.RunDue();

                while (next < frames.Count && frames[next].t <= frameIndex * Frame)
                {
                    foreach (var packet in frames[next].packets) packets.Handle(packet.Id, packet.Args);
                    foreach (Dictionary<string, object> action in frames[next].input ?? new List<object>()) HandleInput(world, action);
                    next++;
                }

                world.Time = clock.Now;
                if (!world.Player.Dead)
                {
                    packets.UpdateEntities();
                    packets.UpdateKeyboard();
                    packets.UpdateAnimations();
                    packets.UpdateInfos();
                    world.Pointers.Update();
                    world.Lights.UpdateLights(world.Time);
                }

                // renderer.ts drawOverlays, as the harness records it.
                var framePointers = string.Join("\n", world.Pointers.All.Select(pointer => pointer.ToString()));
                if (framePointers != previousPointers)
                {
                    Assert.Less(pointerChanges, expectedPointers.Count, $"frame {frameIndex}: more pointer changes than the original");
                    var expectedPointer = expectedPointers[pointerChanges++];
                    var expectedPointerFrame = (int)(double)expectedPointer[0];
                    if (expectedPointerFrame != frameIndex || (string)expectedPointer[1] != Sha256(framePointers))
                        Assert.Fail($"pointer change #{pointerChanges - 1}: original changed at frame {expectedPointerFrame}, port at frame {frameIndex}\n" +
                                    $"--- original @{expectedPointerFrame}\n" + (pointerDumps.TryGetValue(expectedPointerFrame, out var p) ? p : "(no dump kept)") +
                                    $"\n--- port @{frameIndex}\n{framePointers}");
                    previousPointers = framePointers;
                }

                var m = world.Minigame;
                var frameMisc = string.Join("\n", new[]
                {
                    $"minigame {m.Type} {m.Status} {Text(m.Countdown)} {Text(m.Started)} {Text(m.Score)} {Text(m.RedTeamScore)} {Text(m.BlueTeamScore)}"
                }.Concat(world.Player.Abilities.Values.Select(a => $"ability {Text(a.Key)} {Text(a.Type)} {Text(a.Level)} {Text(a.QuickSlot)} {Text(a.Active)}")));
                if (frameMisc != previousMisc)
                {
                    Assert.Less(miscChanges, expectedMisc.Count, $"frame {frameIndex}: more minigame/ability changes than the original");
                    var expectedMiscChange = expectedMisc[miscChanges++];
                    var expectedMiscFrame = (int)(double)expectedMiscChange[0];
                    if (expectedMiscFrame != frameIndex || (string)expectedMiscChange[1] != Sha256(frameMisc))
                        Assert.Fail($"minigame/ability change #{miscChanges - 1}: original changed at frame {expectedMiscFrame}, port at frame {frameIndex}\n" +
                                    $"--- original @{expectedMiscFrame}\n" + (miscDumps.TryGetValue(expectedMiscFrame, out var md) ? md : "(no dump kept)") +
                                    $"\n--- port @{frameIndex}\n{frameMisc}");
                    previousMisc = frameMisc;
                }

                var frameOverlay = OverlayLines(world);
                if (frameOverlay != previousOverlay)
                {
                    Assert.Less(overlayChanges, expectedOverlays.Count, $"frame {frameIndex}: more overlay changes than the original");
                    var expectedOverlay = expectedOverlays[overlayChanges++];
                    var expectedOverlayFrame = (int)(double)expectedOverlay[0];
                    if (expectedOverlayFrame != frameIndex || (string)expectedOverlay[1] != Sha256(frameOverlay))
                        Assert.Fail($"overlay change #{overlayChanges - 1}: original changed at frame {expectedOverlayFrame}, port at frame {frameIndex}\n" +
                                    $"--- original @{expectedOverlayFrame}\n" + (overlayDumps.TryGetValue(expectedOverlayFrame, out var o) ? o : "(no dump kept)") +
                                    $"\n--- port @{frameIndex}\n{frameOverlay}");
                    previousOverlay = frameOverlay;
                }

                drawLines.Clear();
                textLines.Clear();
                textPlanner.BeginFrame();
                planner.Draw();
                textPlanner.DrawInfos();
                textPlanner.DrawMinigameGUI();
                textPlanner.EndFrame();
                if (frameIndex % drawSample == 0)
                {
                    var text = string.Join("\n", new[]
                    {
                        $"camera {Text(camera.X)} {Text(camera.Y)} {camera.GridX} {camera.GridY} {camera.GridWidth} {camera.GridHeight}"
                    }.Concat(drawLines));
                    var expectedDraw = expectedDraws[drawSamples++];
                    Assert.AreEqual((int)(double)expectedDraw[0], frameIndex);
                    if ((string)expectedDraw[1] != Sha256(text))
                        Assert.Fail($"frame {frameIndex}: entity draws differ\n--- original\n" +
                                    (drawDumps.TryGetValue(frameIndex, out var d) ? d : "(no dump kept)") + $"\n--- port\n{text}");
                }

                var frameTexts = string.Join("\n", textLines);
                if (frameTexts != previousTexts)
                {
                    Assert.Less(textChanges, expectedTexts.Count, $"frame {frameIndex}: more text changes than the original");
                    var expectedText = expectedTexts[textChanges++];
                    var expectedTextFrame = (int)(double)expectedText[0];
                    if (expectedTextFrame != frameIndex || (string)expectedText[1] != Sha256(frameTexts))
                        Assert.Fail($"text change #{textChanges - 1}: original changed at frame {expectedTextFrame}, port at frame {frameIndex}\n" +
                                    $"--- original @{expectedTextFrame}\n" + (textDumps.TryGetValue(expectedTextFrame, out var t) ? t : "(no dump kept)") +
                                    $"\n--- port @{frameIndex}\n{frameTexts}");
                    previousTexts = frameTexts;
                }

                var state = Snapshot(world);
                if (state == previous) continue;

                Assert.Less(changes, expectedChanges.Count, $"frame {frameIndex}: more state changes than the original");
                var expected = expectedChanges[changes];
                var expectedFrame = (int)(double)expected[0];

                if (expectedFrame != frameIndex || (string)expected[1] != Sha256(state))
                {
                    var original = dumps.TryGetValue(expectedFrame, out var dump) ? dump : "(no dump kept for this frame)";
                    Assert.Fail($"change #{changes}: original changed at frame {expectedFrame}, port at frame {frameIndex}\n" +
                                $"--- original @{expectedFrame}\n{original}\n--- port @{frameIndex}\n{state}");
                }

                changes++;
                previous = state;
            }

            Assert.AreEqual(expectedChanges.Count, changes, "state changes");
            Assert.AreEqual(expectedDraws.Count, drawSamples, "draw samples");
            Assert.AreEqual(expectedTexts.Count, textChanges, "text changes");
            Assert.AreEqual(expectedOverlays.Count, overlayChanges, "overlay changes");
            Assert.AreEqual(expectedPointers.Count, pointerChanges, "pointer changes");
            Assert.AreEqual(expectedMisc.Count, miscChanges, "minigame/ability changes");
            CollectionAssert.AreEqual(expectedSent, sent, "sent packets");
            CollectionAssert.AreEqual(expectedSounds, sounds, "sounds");
        }

        /// <summary>The harness's overlay record: the image, the dark mask fill and each mask stamp.</summary>
        static string OverlayLines(World world)
        {
            var lights = world.Lights;
            var stamps = lights.Draw();
            if (stamps == null) return "";

            string width = JsNumber.ToJsString(lights.CanvasWidth), height = JsNumber.ToJsString(lights.CanvasHeight);
            var lines = new List<string>();
            if (lights.OverlayImage != null) lines.Add($"image /img/{lights.OverlayImage}.png 0 0 {width} {height}");
            lines.Add($"dark {lights.DarkColour} {width} {height}");
            lines.AddRange(stamps.Select(stamp => $"mask {stamp}"));
            return string.Join("\n", lines);
        }

        /// <summary>The harness's record of a drawImage: canvas, image URL, arguments, transform, alpha, composite.</summary>
        static string DrawLine(EntityDraw d)
        {
            var name = d.Canvas == EntityCanvas.Entities ? "entities" : d.Canvas == EntityCanvas.Fore ? "fore" : "mask";
            // A generated hurt / silhouette canvas has no URL: the harness's stand-in records "".
            var url = d.Variant == SpriteVariant.Normal ? d.Sprite?.Path : "";
            var args = d.Sprite == null
                ? new[] { "entities-canvas", "0", "0" }
                : new[] { url, Text(d.Sx), Text(d.Sy), Text(d.Sw), Text(d.Sh), Text(d.Dx), Text(d.Dy), Text(d.Dw), Text(d.Dh) };
            var matrix = string.Join(",", new[] { d.A, d.B, d.C, d.D, d.E, d.F }.Select(Text));
            return string.Join(" ", new[] { name }.Concat(args).Concat(new[] { matrix, Text(d.Alpha), d.Composite }));
        }

        /// <summary>The harness's record of a text canvas call (gen_entity_replay.mts textContext).</summary>
        static string TextLine(TextDraw d)
        {
            var matrix = string.Join(",", new[] { d.A, d.B, d.C, d.D, d.E, d.F }.Select(Text));
            string[] args, style;
            var text = d.Text == null ? "undefined" : JsJson.Stringify(d.Text);
            var font = JsJson.Stringify(d.Font);
            switch (d.Kind)
            {
                case TextDrawKind.StrokeRect:
                    args = new[] { Text(d.X), Text(d.Y), Text(d.W), Text(d.H) };
                    style = new[] { d.Colour, Text(d.LineWidth) };
                    break;
                case TextDrawKind.FillRect:
                    args = new[] { Text(d.X), Text(d.Y), Text(d.W), Text(d.H) };
                    style = new[] { d.Colour };
                    break;
                case TextDrawKind.StrokeText:
                    args = new[] { text, Text(d.X), Text(d.Y) };
                    style = new[] { d.Colour, Text(d.LineWidth), font, d.TextAlign };
                    break;
                case TextDrawKind.FillText:
                    args = new[] { text, Text(d.X), Text(d.Y) };
                    style = new[] { d.Colour, font, d.TextAlign };
                    break;
                default:
                    args = (d.HasSource ? new[] { d.Sx, d.Sy, d.Sw, d.Sh, d.X, d.Y, d.W, d.H } : new[] { d.X, d.Y, d.W, d.H })
                        .Select(Text).Prepend(d.Sprite.Path).ToArray();
                    style = new[] { "" };
                    break;
            }
            var kind = char.ToLowerInvariant(d.Kind.ToString()[0]) + d.Kind.ToString().Substring(1);
            return string.Join(" ", new[] { kind }.Concat(args).Concat(new[] { "|", matrix, Text(d.Alpha) }).Concat(style));
        }

        static string Text(double? value) => value.HasValue ? JsValue.NumberToString(value.Value) : "undefined";
        static string Text(double value) => JsValue.NumberToString(value);
        static string Text(bool value) => value ? "true" : "false";
        static string Text(string value) => value ?? "undefined";

        /// <summary>The same line per entity as gen_entity_replay.mts snapshot().</summary>
        static string Snapshot(World world)
        {
            var entities = world.Entities.Entities.Values.OrderBy(e => e.Instance, System.StringComparer.Ordinal);
            return string.Join("\n", entities.Select(e =>
            {
                var character = e as Character;
                var cellIndex = world.Map.IsOutOfBounds(e.GridX, e.GridY) ? -1 : world.Map.CoordToIndex(e.GridX, e.GridY);
                return string.Join("|",
                    e.Instance,
                    e.Type.ToString(CultureInfo.InvariantCulture),
                    Text(e.Sprite?.Key),
                    e.Sprite == null ? "undefined" : Text(e.Sprite.Loaded),
                    $"{e.GridX},{e.GridY}",
                    $"{Text(e.X)},{Text(e.Y)}",
                    e.Orientation.ToString(CultureInfo.InvariantCulture),
                    e.Animation == null ? "undefined:undefined" : $"{e.Animation.Name}:{e.Animation.Frame.Index}",
                    Text(e.SpriteFlipX),
                    e.Fading ? Text(e.FadingAlpha) : "-",
                    $"{Text(e.HitPoints)}/{Text(e.MaxHitPoints)}",
                    Text(e.Dead) + Text(e.Teleporting) + Text(e.Frozen) + (character == null ? "undefined" : Text(character.Moving)),
                    character?.Path == null ? "-" : character.Path.Count.ToString(CultureInfo.InvariantCulture),
                    Text(e.NameColour),
                    Text(e.CustomScale),
                    Text(e.Exclamation) + Text(e.BlueExclamation),
                    Text(e.IsVisible()),
                    Text(e.HealthBarVisible),
                    Text(e.OffsetY),
                    e.Region.ToString(CultureInfo.InvariantCulture),
                    world.Entities.Grid.Contains(e.GridX, e.GridY, e.Instance) ? "g" : "-",
                    cellIndex < 0 ? "undefined" : world.Map.Grid[cellIndex].ToString(CultureInfo.InvariantCulture),
                    (character?.Target ?? (e as Projectile)?.Target)?.Instance ?? "undefined",
                    character == null ? "undefined" : Text(character.Following));
            }).Concat(new[]
            {
                $"input {world.Input.SelectedX},{world.Input.SelectedY} {Text(world.Input.SelectedCellVisible)} camera {Text(world.Camera.X)},{Text(world.Camera.Y)}"
            }).Concat(world.Info.Infos.Values.Select(i =>
                $"splat {i.Id}|{i.GetText()}|{Text(i.X)},{Text(i.Y)}|{Text(i.Opacity)}|{i.Fill}|{i.Stroke}|{i.SkillKey}|{string.Join(",", i.Skills)}")));
        }

        static string Sha256(string text)
        {
            using var sha = SHA256.Create();
            return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        }
    }
}
