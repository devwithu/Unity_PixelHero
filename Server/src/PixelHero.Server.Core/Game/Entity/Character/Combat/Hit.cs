using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>character/combat/hit.ts.</summary>
    public sealed class Hit
    {
        public Modules.Hits type;
        double damage;
        readonly bool ranged;
        public double aoe;
        readonly bool magic;
        readonly bool archery;
        readonly Modules.AttackStyle? attackStyle;

        public Hit(Modules.Hits type, double damage = 0, bool ranged = false, double aoe = 0, bool magic = false,
            bool archery = false, Modules.AttackStyle? attackStyle = null)
        {
            this.type = type;
            this.damage = damage;
            this.ranged = ranged;
            this.aoe = aoe;
            this.magic = magic;
            this.archery = archery;
            this.attackStyle = attackStyle;
        }

        public double GetDamage() => damage;

        public bool IsAoE() => aoe > 0;

        public List<object> GetSkill()
        {
            if (magic) return new List<object> { "magic" };
            if (archery) return new List<object> { "archery" };

            if (attackStyle != Modules.AttackStyle.None)
                switch (attackStyle)
                {
                    case Modules.AttackStyle.Stab:
                        return new List<object> { "accuracy" };
                    case Modules.AttackStyle.Slash:
                        return new List<object> { "strength" };
                    case Modules.AttackStyle.Defensive:
                        return new List<object> { "defense" };
                    case Modules.AttackStyle.Crush:
                        return new List<object> { "accuracy", "strength" };
                    case Modules.AttackStyle.Shared:
                        return new List<object> { "accuracy", "strength", "defense" };
                    case Modules.AttackStyle.Hack:
                        return new List<object> { "strength", "defense" };
                    case Modules.AttackStyle.Chop:
                        return new List<object> { "accuracy", "defense" };
                }

            if (type == Modules.Hits.Normal) return new List<object> { "accuracy" };

            return new List<object>();
        }

        /// <summary>HitData: {type, damage, ranged, aoe, skills}.</summary>
        public JsObject Serialize() => new(
            "type", (double)type,
            "damage", damage,
            "ranged", ranged,
            "aoe", aoe,
            "skills", GetSkill());
    }
}
