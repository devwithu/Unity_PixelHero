using System;
using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>
    /// A floating number over a character (client/src/renderer/infos/splat.ts): damage, heals, experience. It rises
    /// one pixel (two for heals) per 100 ms and fades by 70 / duration per step until it is gone.
    /// </summary>
    public sealed class Splat
    {
        public readonly string Id;
        public readonly int Type;
        public readonly double Value;
        public double X;
        public double Y;
        public double Opacity = 1;
        public string SkillKey = "";
        public readonly List<string> Skills;
        public string Fill = "rgb(255, 50, 50)";
        public string Stroke = "rgb(255, 180, 180)";

        string _text;
        readonly string _prefix = "";
        readonly string _suffix = "";
        double _lastTime;
        const double Speed = 100;
        readonly double _updateY;
        readonly double _duration;
        public Action<string> DestroyCallback;

        public Splat(string id, int type, double value, double x, double y, bool isTarget = false, int skill = -1, List<string> skills = null)
        {
            Id = id;
            Type = type;
            Value = value;
            X = x;
            Y = y;
            Skills = skills ?? new List<string>();

            _text = JsValue.NumberToString(value);
            _updateY = IsHeal ? 2 : 1;
            _duration = IsHeal ? 400 : 1000;

            var colours = (Dictionary<string, object>)ModuleTables.DamageColours;
            var colour = Colour(colours, type) ?? (Dictionary<string, object>)colours[((int)Modules.Hits.Normal).ToString()];

            if (skill > -1)
            {
                colour = Colour((Dictionary<string, object>)ModuleTables.SkillExpColours, skill);
                SkillKey = $"skills/{((Modules.Skills)skill).ToString().ToLowerInvariant()}";
            }

            for (var i = 0; i < Skills.Count; i++) Skills[i] = $"skills/{Skills[i]}";

            if (colour != null && colour.TryGetValue("fill", out var fill) && fill is string f && f.Length > 0) Fill = f;
            if (colour != null && colour.TryGetValue("stroke", out var stroke) && stroke is string s && s.Length > 0) Stroke = s;

            if (isTarget && colour != null && colour.TryGetValue("inflicted", out var inflicted) && inflicted is Dictionary<string, object> hurt)
            {
                Fill = (string)hurt["fill"];
                Stroke = (string)hurt["stroke"];
            }

            if (IsDamage && value < 1)
            {
                _text = "MISS";
                Skills.Clear();
            }

            if (Type == (int)Modules.Hits.Poison || Type == (int)Modules.Hits.Freezing) _prefix = "--";
            if (Type == (int)Modules.Hits.Heal || Type == (int)Modules.Hits.Mana) _prefix = "++";
            if (Type == (int)Modules.Hits.Experience || Type == (int)Modules.Hits.Profession)
            {
                _prefix = "+";
                _suffix = "EXP";
            }
        }

        static Dictionary<string, object> Colour(Dictionary<string, object> table, int key) =>
            table.TryGetValue(key.ToString(), out var value) ? value as Dictionary<string, object> : null;

        bool IsHeal => Type == (int)Modules.Hits.Heal;

        bool IsDamage => Type == (int)Modules.Hits.Normal || Type == (int)Modules.Hits.Stun ||
                         Type == (int)Modules.Hits.Critical || Type == (int)Modules.Hits.Explosive;

        public void Update(double time)
        {
            if (!(time - _lastTime > Speed)) return;

            Y -= _updateY;
            _lastTime = time;
            Opacity -= 70 / _duration;

            if (Opacity < 0) DestroyCallback?.Invoke(Id);
        }

        public string GetText() => $"{_prefix}{_text} {_suffix}";
    }

    /// <summary>client/src/controllers/info.ts: the splats on screen, keyed by an id made of time and position.</summary>
    public sealed class InfoController
    {
        readonly World _world;

        /// <summary>id → splat; ids are not integers, so insertion order (a repeated id keeps its place).</summary>
        public readonly OrderedMap<Splat> Infos = new OrderedMap<Splat>();

        public InfoController(World world) => _world = world;

        public void Create(int type, double amount, double x, double y, bool isTarget = false, int skill = -1, bool randomize = false, List<string> skills = null)
        {
            if (randomize)
            {
                x += _world.RandomInt(-8, 8);
                y += _world.RandomInt(-6, 6);
            }

            // Utils.createId: `${time}${x}${y}`.
            var id = JsValue.NumberToString(_world.Clock.Now) + JsValue.NumberToString(x) + JsValue.NumberToString(y);
            var info = new Splat(id, type, amount, x, y, isTarget, skill, skills);
            Infos[info.Id] = info;
            info.DestroyCallback = Infos.Remove;
        }

        public bool IsEmpty => Infos.Count == 0;

        public void Update(double time)
        {
            foreach (var info in Infos.Values) info.Update(time);
        }
    }
}
