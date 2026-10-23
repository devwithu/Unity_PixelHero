using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;

namespace PixelHero.Server.Info
{
    /// <summary>info/formulas.ts.</summary>
    public static class Formulas
    {
        public static List<double> LevelExp = new();

        public static void Reset() => LevelExp = new List<double>();

        static double Stat(JsObject stats, string key) => stats.Num(key) ?? double.NaN;

        public static double GetDamage(Character attacker, Character target, bool critical = false)
        {
            var accuracyBonus = attacker.GetAccuracyBonus();
            var accuracyLevel = attacker.GetAccuracyLevel();
            var accuracyModifier = GetAccuracyWeight(attacker, target);
            var defenseLevel = target.GetDefenseLevel();
            var maxDamage = GetMaxDamage(attacker, critical);
            var accuracy = Constants.MAX_ACCURACY;

            // Linearly increase accuracy based on accuracy bonus, prevent from going over 50.
            accuracy += accuracyBonus > 70 ? 0 : 1 - accuracyBonus / 70;

            // Append the accuracy level bonus, we use a 1.75 modifier since skill level matters more.
            accuracy += (Constants.MAX_LEVEL - accuracyLevel + 1) * 0.01;

            // Append the defense level of the target to the accuracy modifier.
            accuracy += defenseLevel * 0.0175;

            // We use the scalar difference of the stats to append onto the accuracy.
            accuracy += accuracyModifier < 0 ? 1.5 : -(Math.Sqrt(accuracyModifier) / 22.36) + 1;

            // Critical damage boosts accuracy by a factor of 0.15;
            if (critical) accuracy -= 0.15;

            // Apply the attack style modifiers.
            switch (attacker.GetAttackStyle())
            {
                case Modules.AttackStyle.Fast:
                case Modules.AttackStyle.LongRange:
                    // Rapid attack style decreases accuracy by a factor of 0.05;
                    accuracy += 0.05;
                    break;

                case Modules.AttackStyle.Stab:
                case Modules.AttackStyle.Focused:
                case Modules.AttackStyle.Accurate:
                    // Rapid attack style increases accuracy by a factor of 0.11;
                    accuracy -= 0.11;
                    break;

                case Modules.AttackStyle.Crush:
                case Modules.AttackStyle.Chop:
                    // Rapid attack style increases accuracy by a factor of 0.07
                    accuracy -= 0.07;
                    break;

                case Modules.AttackStyle.Shared:
                    // Shared attack style increases accuracy by a factor of 0.05
                    accuracy -= 0.05;
                    break;
            }

            // Increase accuracy if the attacker has the accuracy potion effect.
            if (attacker.status.Has(Modules.Effects.AccuracyBuff)) accuracy -= 0.07;
            if (attacker.status.Has(Modules.Effects.AccuracySuperBuff)) accuracy -= 0.12;

            // Decrease accuracy if the target has the defense potion effect.
            if (target.status.Has(Modules.Effects.DefenseBuff)) accuracy += 0.08;
            if (target.status.Has(Modules.Effects.DefenseSuperBuff)) accuracy += 0.12;

            // Increase accuracy if the target has the terror effect.
            if (target.status.Has(Modules.Effects.Terror)) accuracy -= 0.4;

            // Terror decreases overall accuracy, so we increase it by 1.
            if (attacker.status.Has(Modules.Effects.Terror)) accuracy += 1;

            // We apply the damage absoprtion onto the max damage. See `getDamageReduction` for more information.
            maxDamage *= target.GetDamageReduction();

            // We use the weighted randomInt distribution to determine the damage.
            var randomDamage = Utils.RandomWeightedInt(0, maxDamage, accuracy);

            // Limit the damage to the target's remaining health.
            if (randomDamage > target.hitPoints.GetHitPoints()) randomDamage = target.hitPoints.GetHitPoints();

            return randomDamage;
        }

        public static double GetMaxDamage(Character character, bool critical = false)
        {
            var bonus = character.GetDamageBonus();
            var level = character.GetSkillDamageLevel();
            var damage = (bonus + level) * 1.25;

            // Apply a 50% max damage boost upon critical damage.
            if (critical) damage *= 1.5;

            // Player characters get a boost of 5 damage.
            if (character.IsPlayer()) damage += 5;

            // Different attack styles give different bonuses.
            switch (character.GetAttackStyle())
            {
                // Focused attack style boosts maximum damage by 10%
                case Modules.AttackStyle.Focused:
                case Modules.AttackStyle.Slash:
                    damage *= 1.1;
                    break;

                // Crush gives a 5% boost.
                case Modules.AttackStyle.Crush:
                case Modules.AttackStyle.Hack:
                    damage *= 1.05;
                    break;

                // Shared attack style gives a 3% boost.
                case Modules.AttackStyle.Shared:
                    damage *= 1.03;
                    break;
            }

            // Apply a 10% damage boost if the character has the strength potion effect.
            if (character.status.Has(Modules.Effects.StrengthBuff)) damage *= 1.1;

            // Apply 15% damage boost if the character has the super strength potion effect.
            if (character.status.Has(Modules.Effects.StrengthSuperBuff)) damage *= 1.15;

            // Ensure the damage is not negative.
            if (damage < 0) damage = 0;

            return damage;
        }

        public static double GetAccuracyWeight(Character attacker, Character target)
        {
            var attackerStats = attacker.GetAttackStats();
            var targetStats = target.GetDefenseStats();
            var attackStyle = GetPrimaryStyle(attackerStats); // primary attack style of the attacker
            var defenseStyle = GetPrimaryStyle(targetStats); // primary defense style of the target

            double crush = (Stat(attackerStats, "crush") - Stat(targetStats, "crush")) / 3,
                slash = (Stat(attackerStats, "slash") - Stat(targetStats, "slash")) / 3,
                stab = (Stat(attackerStats, "stab") - Stat(targetStats, "stab")) / 3,
                magic = (Stat(attackerStats, "magic") - Stat(targetStats, "magic")) / 3,
                archery = (Stat(attackerStats, "archery") - Stat(targetStats, "archery")) / 3;

            // Object.values(weights).reduce((acc, curr) => (curr < 0 ? acc : acc + curr), 0)
            var totalWeight = 0.0;
            foreach (var weight in new[] { crush, slash, stab, magic, archery })
                if (!(weight < 0))
                    totalWeight += weight;

            // Respective classes use their own weights.
            if (attacker.IsArcher()) totalWeight = archery;
            if (attacker.IsMagic()) totalWeight = magic;

            // If our attack style is the same or none then we do not have any advantage in our accuracy.
            if (attackStyle == defenseStyle || attackStyle == Modules.DamageStyle.None)
                return totalWeight != 0 && !double.IsNaN(totalWeight) ? totalWeight : 1;

            switch (attackStyle)
            {
                case Modules.DamageStyle.Crush:
                    totalWeight += defenseStyle == Modules.DamageStyle.Slash ? crush : -crush / 2;
                    break;

                case Modules.DamageStyle.Slash:
                    totalWeight += defenseStyle == Modules.DamageStyle.Stab ? slash : -slash / 2;
                    break;

                case Modules.DamageStyle.Stab:
                    totalWeight += defenseStyle == Modules.DamageStyle.Crush ? stab : -stab / 2;
                    break;

                case Modules.DamageStyle.Magic:
                    totalWeight += magic;
                    break;

                case Modules.DamageStyle.Archery:
                    totalWeight += archery;
                    break;
            }

            // Ensure the weight is always positive and more than 1 for the sake of combat mechanics
            return JsMath.Max(totalWeight, 1);
        }

        public static Modules.DamageStyle GetPrimaryStyle(JsObject stats)
        {
            double crush = Stat(stats, "crush"), slash = Stat(stats, "slash"), stab = Stat(stats, "stab"),
                magic = Stat(stats, "magic"), archery = Stat(stats, "archery");

            return crush > slash && crush > stab && crush > magic && crush > archery ? Modules.DamageStyle.Crush
                : slash > crush && slash > stab && slash > magic && slash > archery ? Modules.DamageStyle.Slash
                : stab > crush && stab > slash && stab > magic && stab > archery ? Modules.DamageStyle.Stab
                : magic > crush && magic > slash && magic > stab && magic > archery ? Modules.DamageStyle.Magic
                : archery > crush && archery > slash && archery > stab && archery > magic ? Modules.DamageStyle.Archery
                : Modules.DamageStyle.None;
        }

        public static bool? GetWeaponBreak(Character attacker, Character target)
        {
            if (attacker == null || target == null) return null;

            var breakChance = Utils.RandomFloat(1, 100);

            return breakChance > 75;
        }

        public static double NextExp(double experience)
        {
            if (experience < 0) return -1;

            for (var i = 1; i < LevelExp.Count; i++)
                if (experience < LevelExp[i])
                    return LevelExp[i];

            return -1;
        }

        public static double PrevExp(double experience)
        {
            if (experience < 0) return 0;

            for (var i = Constants.MAX_LEVEL; i > 0; i--)
                if (experience >= Exp(i))
                    return LevelExp[i];

            return 0;
        }

        /// <summary>LevelExp[i], or undefined (NaN in comparisons) past the end.</summary>
        static double Exp(int i) => i >= 0 && i < LevelExp.Count ? LevelExp[i] : double.NaN;

        public static double ExpToLevel(double experience)
        {
            if (experience < 0) return -1;

            for (var i = 1; i < LevelExp.Count; i++)
                if (experience < LevelExp[i])
                    return i;

            return Constants.MAX_LEVEL;
        }

        public static double LevelsToExperience(double startLevel, double endLevel) =>
            ExpAt(endLevel) - ExpAt(startLevel);

        static double ExpAt(double level) =>
            level >= 0 && level % 1 == 0 && level < LevelExp.Count ? LevelExp[(int)level] : double.NaN;

        public static double GetMaxHitPoints(double level) => 39 + level * 30;

        public static double GetMaxMana(double level) => 20 + level * 24;

        public static double GetPoisonChance(double level) =>
            // Chance is per 235 - level, each level increases the chance in poisioning.
            Utils.RandomInt(0, 235 - level);

        public static bool GetEffectChance() => Utils.RandomInt(0, 100) < 5;

        public static bool GetEnchantChance(double tier) => Utils.RandomInt(0, 100) < 8 * tier;
    }

    /// <summary>info/loader.ts: fills Formulas.LevelExp.</summary>
    public static class Loader
    {
        public static void Load()
        {
            Formulas.LevelExp = new List<double> { 0 };

            for (var i = 1; i < Constants.MAX_LEVEL; i++)
            {
                var points = Math.Floor(0.25 * Math.Floor(i + 300 * JsMath.Pow(2, i / 7.0)));
                Formulas.LevelExp.Add(points + Formulas.LevelExp[i - 1]);
            }
        }
    }
}
