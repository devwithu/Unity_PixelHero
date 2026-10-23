using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>data/plugins/items/index.ts Plugin.</summary>
    public interface IItemPlugin
    {
        bool OnUse(Player player);
    }

    /// <summary>data/plugins/items/index.ts.</summary>
    public static class ItemPlugins
    {
        static readonly Dictionary<string, Func<JsObject, IItemPlugin>> Index = new()
        {
            ["healingitem"] = data => new HealingItemPlugin(data),
            ["poisoncure"] = _ => new PoisonCurePlugin(),
            ["blackpotion"] = _ => new BlackPotionPlugin(),
            ["hotsauce"] = _ => new HotSaucePlugin(),
            ["snowpotion"] = _ => new SnowPotionPlugin(),
            ["firepotion"] = _ => new FirePotionPlugin(),
            ["knife"] = _ => new KnifePlugin(),
            ["chisel"] = _ => new ChiselPlugin(),
            ["effectpotion"] = data => new EffectPotionPlugin(data)
        };

        public static bool Has(string name) => name != null && Index.ContainsKey(name);

        public static IItemPlugin Create(string name, JsObject data) => Index[name](data);
    }

    public sealed class HealingItemPlugin : IItemPlugin
    {
        readonly double healAmount;
        readonly double healPercent;
        readonly double manaAmount;

        public HealingItemPlugin(JsObject data)
        {
            healAmount = JsData.Truthy(data["healAmount"]) ? (double)data["healAmount"] : 0;
            manaAmount = JsData.Truthy(data["manaAmount"]) ? (double)data["manaAmount"] : 0;
            healPercent = (JsData.Truthy(data["healPercent"]) ? (double)data["healPercent"] : 0) / 100;
        }

        public bool OnUse(Player player)
        {
            if (manaAmount != 0)
            {
                if (player.mana.IsFull())
                {
                    player.Notify("You are already at full mana.");
                    return false;
                }

                player.Heal(manaAmount, "mana");
            }

            if (healAmount != 0 || healPercent != 0)
            {
                if (player.hitPoints.IsFull())
                {
                    player.Notify("You are already at full health.");
                    return false;
                }

                if (healPercent != 0)
                {
                    player.Heal(player.hitPoints.GetMaxHitPoints() * healPercent, "hitpoints");
                    return true;
                }

                player.Heal(healAmount, "hitpoints");

                // Apply eating experience to the player.
                player.skills.Get(Modules.Skills.Eating).AddExperience(Math.Floor(healAmount / 10));
            }

            return true;
        }
    }

    public sealed class PoisonCurePlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            player.SetPoison();
            return true;
        }
    }

    public sealed class BlackPotionPlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            player.Notify(I18n.T("misc:BLACK_POTION"));

            Node.SetTimeout(() => player.Hit(player.hitPoints.GetHitPoints() - 1), 5000);

            return true;
        }
    }

    public sealed class HotSaucePlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            if (player.status.Has(Modules.Effects.HotSauce))
            {
                player.Notify("I really shouldn't be drinking multiple of these...");
                return false;
            }

            player.Notify("You feel an intense rush of adrenaline, you feel like you can run forever.");

            // Update the hot sauce effect.
            player.SetRunning(false, true);

            Node.SetTimeout(() =>
            {
                player.SetRunning(false, false);
                player.Notify("The hot sauce effect has faded.");
            }, 15_000);

            return true;
        }
    }

    public sealed class SnowPotionPlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            player.SetSnowPotion();
            return true;
        }
    }

    public sealed class FirePotionPlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            player.SetFirePotion();
            return true;
        }
    }

    public sealed class KnifePlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            if (player.InCombat())
            {
                player.Notify("You cannot activate the fletching menu while in combat.");
                return false;
            }

            player.world.crafting.Open(player, Modules.Skills.Fletching);

            return true;
        }
    }

    public sealed class ChiselPlugin : IItemPlugin
    {
        public bool OnUse(Player player)
        {
            if (player.InCombat())
            {
                player.Notify("You cannot activate the fletching menu while in combat.");
                return false;
            }

            player.world.crafting.Open(player, Modules.Skills.Chiseling);

            return true;
        }
    }

    public sealed class EffectPotionPlugin : IItemPlugin
    {
        readonly string effect = "";
        readonly double duration = 60_000;

        public EffectPotionPlugin(JsObject data)
        {
            effect = JsData.Truthy(data["effect"]) ? data.Str("effect") : effect;
            duration = JsData.Truthy(data["duration"]) ? (double)data["duration"] : duration;
        }

        public bool OnUse(Player player)
        {
            player.status.AddWithTimeout(GetEffect(), duration,
                () => player.Notify($"The effect of the {effect} potion has worn off."));

            player.Notify($"You drink the {effect} potion.");

            return true;
        }

        Modules.Effects GetEffect() => effect switch
        {
            "accuracy" => Modules.Effects.AccuracyBuff,
            "strength" => Modules.Effects.StrengthBuff,
            "defense" => Modules.Effects.DefenseBuff,
            "magic" => Modules.Effects.MagicBuff,
            "archery" => Modules.Effects.ArcheryBuff,
            "accuracysuper" => Modules.Effects.AccuracySuperBuff,
            "strengthsuper" => Modules.Effects.StrengthSuperBuff,
            "defensesuper" => Modules.Effects.DefenseSuperBuff,
            "magicsuper" => Modules.Effects.MagicSuperBuff,
            "archerysuper" => Modules.Effects.ArcherySuperBuff,
            _ => Modules.Effects.StrengthBuff
        };
    }
}
