using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    public enum TextDrawKind
    {
        StrokeRect,
        FillRect,
        StrokeText,
        FillText,
        DrawImage
    }

    /// <summary>
    /// One call on #text-canvas with the context state in effect. Rects use X/Y/W/H; text uses Text/X/Y with the font
    /// size and alignment; an image (crown, skill icon) uses Sprite and the destination X/Y/W/H (a crown also has a
    /// source rect, always its whole image). Colour is the fill style for fills and the stroke style for strokes.
    /// </summary>
    public sealed class TextDraw
    {
        public TextDrawKind Kind;
        public string Text;
        public EntitySprite Sprite;
        public double Sx, Sy, Sw, Sh;
        public bool HasSource;
        public double X, Y, W, H;
        public double A, B, C, D, E, F;
        public double Alpha;
        public string Colour;
        public double LineWidth;
        public string Font;
        public string TextAlign;
    }

    /// <summary>
    /// The text canvas's 2D context state. Unlike the entity canvases it is never reset: render() only saves and
    /// restores it, so state left unbalanced would leak between frames as in the browser (the original's drawCrown
    /// did that; fixed, ANALYSIS.md §9 #7). A colour the canvas cannot parse is ignored (drawHealth's
    /// <c>'rbga(0, 0, 0, 1)'</c>).
    /// </summary>
    public sealed class TextContext
    {
        struct State
        {
            public double A, B, C, D, E, F, Alpha, LineWidth;
            public string FillStyle, StrokeStyle, Font, TextAlign;
        }

        static readonly Regex HexColour = new Regex("^#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase);
        static readonly Regex FunctionColour = new Regex(@"^(rgb|rgba|hsl|hsla)\(.*\)$", RegexOptions.IgnoreCase);
        static readonly HashSet<string> NamedColours = new HashSet<string>
            { "white", "black", "red", "blue", "green", "yellow", "orange", "purple", "gray", "grey" };

        readonly Action<TextDraw> _sink;
        State _state = new State
        {
            A = 1, D = 1, Alpha = 1, LineWidth = 1, FillStyle = "#000000", StrokeStyle = "#000000", Font = "10px sans-serif",
            TextAlign = "start"
        };
        readonly Stack<State> _stack = new Stack<State>();

        public TextContext(Action<TextDraw> sink) => _sink = sink;

        public static bool IsValidColour(string value) =>
            value != null && (HexColour.IsMatch(value) || FunctionColour.IsMatch(value) || NamedColours.Contains(value));

        public double GlobalAlpha { get => _state.Alpha; set => _state.Alpha = value; }
        public double LineWidth { get => _state.LineWidth; set => _state.LineWidth = value; }
        public string Font { get => _state.Font; set => _state.Font = value; }
        public string TextAlign { get => _state.TextAlign; set => _state.TextAlign = value; }

        public string FillStyle
        {
            get => _state.FillStyle;
            set
            {
                if (IsValidColour(value)) _state.FillStyle = value;
            }
        }

        public string StrokeStyle
        {
            get => _state.StrokeStyle;
            set
            {
                if (IsValidColour(value)) _state.StrokeStyle = value;
            }
        }

        public void Save() => _stack.Push(_state);

        public void Restore()
        {
            if (_stack.Count > 0) _state = _stack.Pop();
        }

        public void Translate(double x, double y)
        {
            _state.E += _state.A * x + _state.C * y;
            _state.F += _state.B * x + _state.D * y;
        }

        TextDraw Record(TextDrawKind kind, string colour) => new TextDraw
        {
            Kind = kind, Colour = colour, LineWidth = _state.LineWidth, Font = _state.Font, TextAlign = _state.TextAlign,
            A = _state.A, B = _state.B, C = _state.C, D = _state.D, E = _state.E, F = _state.F, Alpha = _state.Alpha
        };

        public void StrokeRect(double x, double y, double w, double h)
        {
            var draw = Record(TextDrawKind.StrokeRect, _state.StrokeStyle);
            draw.X = x; draw.Y = y; draw.W = w; draw.H = h;
            _sink(draw);
        }

        public void FillRect(double x, double y, double w, double h)
        {
            var draw = Record(TextDrawKind.FillRect, _state.FillStyle);
            draw.X = x; draw.Y = y; draw.W = w; draw.H = h;
            _sink(draw);
        }

        /// <param name="text">null is JavaScript's undefined, which the canvas draws as "undefined".</param>
        public void StrokeText(string text, double x, double y)
        {
            var draw = Record(TextDrawKind.StrokeText, _state.StrokeStyle);
            draw.Text = text; draw.X = x; draw.Y = y;
            _sink(draw);
        }

        public void FillText(string text, double x, double y)
        {
            var draw = Record(TextDrawKind.FillText, _state.FillStyle);
            draw.Text = text; draw.X = x; draw.Y = y;
            _sink(draw);
        }

        public void DrawImage(EntitySprite sprite, double x, double y, double w, double h)
        {
            var draw = Record(TextDrawKind.DrawImage, null);
            draw.Sprite = sprite; draw.X = x; draw.Y = y; draw.W = w; draw.H = h;
            _sink(draw);
        }

        public void DrawImage(EntitySprite sprite, double sx, double sy, double sw, double sh, double x, double y, double w, double h)
        {
            var draw = Record(TextDrawKind.DrawImage, null);
            draw.Sprite = sprite; draw.HasSource = true;
            draw.Sx = sx; draw.Sy = sy; draw.Sw = sw; draw.Sh = sh;
            draw.X = x; draw.Y = y; draw.W = w; draw.H = h;
            _sink(draw);
        }
    }

    /// <summary>
    /// client/src/renderer/renderer.ts drawHealth, drawName, drawPlayerName, drawCrown, drawPlayerCrown, drawInfos,
    /// drawInfoSkill and drawText on #text-canvas, call for call. Wire <see cref="EntityDrawPlanner.TextCall"/> to
    /// <see cref="Call"/>, and per frame: <see cref="BeginFrame"/>, the entity planner's Draw, <see cref="DrawInfos"/>,
    /// <see cref="EndFrame"/>.
    /// </summary>
    public sealed class TextDrawPlanner
    {
        const string PlayerColour = "rgba(252,218,92, 1)";

        readonly World _world;
        readonly Camera _camera;
        readonly List<TextDraw> _draws = new List<TextDraw>();
        public readonly TextContext Context;

        public bool DrawNames = true;
        public bool DrawLevels = true;
        public double FontSize = 12;
        public double StrokeSize = 4;

        /// <summary>Every call as it happens.</summary>
        public Action<TextDraw> DrawRecorded = _ => { };

        public TextDrawPlanner(World world, Camera camera)
        {
            _world = world;
            _camera = camera;
            Context = new TextContext(draw =>
            {
                _draws.Add(draw);
                DrawRecorded(draw);
            });
            UpdateFontSize();
        }

        double Zoom => _camera.ZoomFactor;

        /// <summary>renderer.ts camera.onZoom.</summary>
        public void UpdateFontSize()
        {
            FontSize = Math.Floor(12 + Zoom * 3);
            StrokeSize = Math.Floor(FontSize / 6);
        }

        /// <summary>This frame's calls so far.</summary>
        public IReadOnlyList<TextDraw> Draws => _draws;

        /// <summary>render(): clear (the canvas pixels only) and save.</summary>
        public void BeginFrame()
        {
            _draws.Clear();
            Context.Save();
        }

        public void EndFrame() => Context.Restore();

        /// <summary>The entity planner's TextCall.</summary>
        public void Call(string kind, Entity entity)
        {
            switch (kind)
            {
                case "health":
                    DrawHealth(entity);
                    break;
                case "name":
                    DrawName(entity);
                    break;
                case "playerName":
                    DrawPlayerName((Player)entity);
                    break;
            }
        }

        void SetCameraView() =>
            Context.Translate(JsValue.ToInt32(-_camera.X * Zoom), JsValue.ToInt32(-_camera.Y * Zoom));

        static bool Truthy(double? value) => value is double v && v != 0 && !double.IsNaN(v);

        /// <summary>The #text-canvas width in pixels (drawMinigameGUI centres its text at a sixth of it).</summary>
        public double CanvasWidth;

        /// <summary>renderer.ts drawMinigameGUI (drawTeamWarGUI, drawCoursingGUI), the last step of render().</summary>
        public void DrawMinigameGUI()
        {
            var minigame = _world.Minigame;
            if (!minigame.Exists()) return;

            var scoreX = CanvasWidth / 6;
            string N(double value) => JsValue.NumberToString(value);
            var waiting = minigame.Started
                ? $"There is a game in progress, remaining time: {N(minigame.Countdown)} seconds"
                : $"Game starts in {N(minigame.Countdown)} seconds";

            switch ((Opcodes.Minigame)minigame.Type)
            {
                case Opcodes.Minigame.TeamWar:
                    switch (minigame.Status)
                    {
                        case "lobby":
                            DrawText(waiting, scoreX, 30, true);
                            return;
                        case "ingame":
                            DrawText($"Red: {N(minigame.RedTeamScore)}", scoreX - 20, 30, true, false, "red");
                            DrawText($"Blue: {N(minigame.BlueTeamScore)}", scoreX + 20, 30, true, false, "blue");
                            DrawText($"Time left: {N(minigame.Countdown)} seconds", scoreX, 50, true, false, "white");
                            return;
                    }
                    return;
                case Opcodes.Minigame.Coursing:
                    switch (minigame.Status)
                    {
                        case "lobby":
                            DrawText(waiting, scoreX, 30);
                            return;
                        case "ingame":
                            DrawText($"Score: {N(minigame.Score)}", scoreX, 30, true);
                            return;
                    }
                    return;
            }
        }

        public void DrawInfos()
        {
            if (_world.Info.IsEmpty) return;

            foreach (var info in _world.Info.Infos.Values)
            {
                Context.Save();
                Context.GlobalAlpha = info.Opacity;

                var x = info.X + 8;
                var y = info.Y;
                var hasSkill = info.SkillKey != "" || info.Skills.Count > 0;

                if (hasSkill)
                {
                    SetCameraView();

                    double skillX = JsValue.ToInt32((x - 2) * Zoom);
                    x = JsValue.ToInt32(x * Zoom);
                    y = JsValue.ToInt32(y * Zoom);

                    if (info.SkillKey != "") DrawInfoSkill(info.SkillKey, skillX, y);
                    else
                        for (var i = 0; i < info.Skills.Count; i++)
                            DrawInfoSkill(info.Skills[i], skillX, y, i, info.Skills.Count);
                }

                DrawText(info.GetText(), x, y, true, !hasSkill, info.Fill, info.Stroke, 32);
                Context.Restore();
            }
        }

        void DrawInfoSkill(string skill, double x, double y, int index = 0, int total = 0)
        {
            var sprite = _world.Sprites.Get(skill);
            if (sprite == null) return;
            if (!sprite.Loaded) sprite.Load();

            if (total == 2)
            {
                if (index == 0) x -= sprite.Width;
                else x += sprite.Width / 1.5;
            }
            else if (total == 3)
            {
                if (index == 0) x -= sprite.Width * 2;
                else if (index == 2) x += sprite.Width * 2;
            }

            Context.DrawImage(sprite, x + sprite.OffsetX, y + sprite.OffsetY * 4, sprite.Width * 2, sprite.Height * 2);
        }

        void DrawCrown(string key, double x, double y)
        {
            var crown = GetCrown(key);
            if (crown == null) return;

            // Port fix (ANALYSIS.md §9 #7; the fixtures patch the original the same way): the original restored without
            // saving, so the camera transform piled up when two crowned players were drawn.
            Context.Save();
            SetCameraView();
            Context.DrawImage(crown, 0, 0, crown.Width, crown.Height, x * Zoom - crown.Width, y * Zoom - crown.Height * 3,
                crown.Width * 2, crown.Height * 2);
            Context.Restore();
        }

        void DrawPlayerCrown(string key)
        {
            var crown = GetCrown(key);
            if (crown == null) return;

            Context.DrawImage(crown, 0, 0, crown.Width, crown.Height,
                _camera.ScreenWidth / 2 - crown.Width + 8 * Zoom,
                _camera.ScreenHeight / 2 - crown.Height * 3 - 24 * Zoom,
                crown.Width * 2, crown.Height * 2);
        }

        void DrawHealth(Entity entity)
        {
            if (!Truthy(entity.HitPoints) || entity.HitPoints < 0 || !entity.HealthBarVisible) return;

            double barLength = _world.TileSize;
            var healthX = entity.X * Zoom - barLength / 2 + 8;
            var healthY = (entity.Y - entity.Sprite.Height / 4.0) * Zoom;
            double healthWidth = JsValue.ToInt32(entity.HitPoints.Value / (entity.MaxHitPoints ?? double.NaN) * barLength * Zoom);
            var healthHeight = 2 * Zoom;

            Context.Save();
            SetCameraView();
            Context.StrokeStyle = "rbga(0, 0, 0, 1)";
            Context.LineWidth = 1;
            Context.StrokeRect(healthX, healthY, barLength * Zoom, healthHeight);
            Context.FillStyle = "rgba(253, 0, 0, 1)";
            Context.FillRect(healthX, healthY, healthWidth, healthHeight);
            Context.Restore();
        }

        void DrawName(Entity entity)
        {
            var x = entity.X + 8;
            var y = entity.Y - 5;
            var colour = "white";

            if (entity.Counter > 0)
            {
                DrawText(JsValue.NumberToString(entity.Counter), x, y, true, true, colour);
                return;
            }

            if (entity is Item item && item.ItemCount > 1)
            {
                DrawText(JsValue.NumberToString(item.ItemCount.Value), x, y, true, true, colour);
                return;
            }

            if (entity.Hidden || entity.HealthBarVisible || entity.Exclamation || entity.BlueExclamation) return;

            var drawNames = DrawNames && entity.DrawNames() && !entity.IsItem;
            var drawLevels = DrawLevels && !entity.IsNPC && !entity.IsItem;
            var nameY = DrawLevels ? y - 10 : y - 4;
            var levelY = DrawLevels ? y : y - 7;
            var levelText = "Level " + (entity.Level is double level ? JsValue.NumberToString(level) : "undefined");

            if (entity.IsNPC) nameY = y - 2;

            if (entity is Player player)
            {
                if (player.HasRank()) colour = RankColour(player.Rank);
                if (drawNames && player.HasCrown()) DrawCrown(player.GetCrownKey(), x, nameY);
            }

            if (entity is Player ranked && ranked.Rank != (int)Modules.Ranks.None) colour = RankColour(ranked.Rank);

            if (entity.Instance == _world.Player.Instance) colour = PlayerColour;

            if (!string.IsNullOrEmpty(entity.NameColour)) colour = entity.NameColour;

            if (drawNames) DrawText(entity.Name, x, nameY, true, true, colour);

            if (drawLevels && Truthy(entity.Level)) DrawText(levelText, x, levelY, true, true, colour);
        }

        void DrawPlayerName(Player entity)
        {
            var nameOffset = DrawLevels ? 22 : 10;

            if (DrawNames)
            {
                DrawText(entity.Name, _camera.ScreenWidth / 2 + 8 * Zoom, _camera.ScreenHeight / 2 - nameOffset * Zoom, true, false,
                    PlayerColour);

                if (entity.HasCrown()) DrawPlayerCrown(entity.GetCrownKey());
            }

            if (DrawLevels && Truthy(entity.Level))
                DrawText("Level " + JsValue.NumberToString(entity.Level.Value), _camera.ScreenWidth / 2 + 8 * Zoom,
                    _camera.ScreenHeight / 2 - 10 * Zoom, true, false, PlayerColour);
        }

        /// <summary>Modules.RankColours[rank]: undefined (null) for a rank outside the table.</summary>
        static string RankColour(double? rank)
        {
            var table = (Dictionary<string, object>)ModuleTables.RankColours;
            return rank is double r && table.TryGetValue(JsValue.NumberToString(r), out var colour) ? (string)colour : null;
        }

        /// <param name="colour">null or "" fall back to white (<c>colour || 'white'</c>).</param>
        void DrawText(string text, double x, double y, bool centered = false, bool setViews = false, string colour = "white",
            string strokeColour = "rgba(0, 0, 0, 1)", double? fontSize = null)
        {
            Context.Save();

            if (centered) Context.TextAlign = "center";
            if (setViews)
            {
                SetCameraView();
                x = JsValue.ToInt32(x * Zoom);
                y = JsValue.ToInt32(y * Zoom);
            }

            Context.StrokeStyle = strokeColour;
            Context.LineWidth = StrokeSize;
            Context.Font = $"{JsValue.NumberToString(fontSize ?? FontSize)}px Quaver";
            Context.StrokeText(text, x, y);
            Context.FillStyle = string.IsNullOrEmpty(colour) ? "white" : colour;
            Context.FillText(text, x, y);

            Context.Restore();
        }

        EntitySprite GetCrown(string key)
        {
            switch (key)
            {
                case "goldmedal": return _world.Sprites.Get("crowns/goldmedal");
                case "silvermedal": return _world.Sprites.Get("crowns/silvermedal");
                case "crown-artist": return _world.Sprites.Get("crowns/artist");
                default:
                    return key.StartsWith("crown-tier", StringComparison.Ordinal)
                        ? _world.Sprites.Get("crowns/tier" + key.Substring("crown-tier".Length))
                        : null;
            }
        }
    }
}
