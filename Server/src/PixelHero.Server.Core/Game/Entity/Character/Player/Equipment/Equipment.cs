using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>player/equipment/equipment.ts.</summary>
    public class Equipment
    {
        // Properties
        public string name = "";
        public bool poisonous;
        public bool freezing;
        public bool burning;
        public double movementModifier = -1;
        public JsObject light = new();

        // Stats
        public JsObject attackStats = Utils.GetEmptyStats();
        public JsObject defenseStats = Utils.GetEmptyStats();
        public JsObject bonuses = Utils.GetEmptyBonuses();

        Action<Equipment> updateCallback;

        public Modules.Equipment type;
        public string key;
        public double count;
        public JsObject enchantments;

        public Equipment(Modules.Equipment type, string key = "", double count = -1, JsObject enchantments = null)
        {
            this.type = type;
            this.key = key;
            this.count = count;
            this.enchantments = enchantments ?? new JsObject();
        }

        public virtual void Update(Item item)
        {
            key = item.key;
            name = item.name;
            count = item.count;
            enchantments = item.enchantments;
            poisonous = item.poisonous;
            attackStats = item.attackStats;
            defenseStats = item.defenseStats;
            bonuses = item.bonuses;

            updateCallback?.Invoke(this);
        }

        public virtual void Empty()
        {
            key = "";
            count = -1;
            enchantments = new JsObject();
            name = "";
            poisonous = false;
            freezing = false;
            burning = false;
            movementModifier = -1;
            light = new JsObject();
            attackStats = Utils.GetEmptyStats();
            defenseStats = Utils.GetEmptyStats();
            bonuses = Utils.GetEmptyBonuses();
        }

        public bool IsEmpty() => !JsData.Truthy(key);

        public bool HasMovementModifier() => movementModifier != -1;

        protected bool HasEnchantment(Modules.Enchantment enchantment) => enchantments.Has(((int)enchantment).ToString());

        public virtual JsObject Serialize(bool clientInfo = false)
        {
            var data = new JsObject(
                "type", (double)type,
                "key", key,
                "count", count,
                "enchantments", enchantments);

            // Includes information only relevant to the client.
            if (clientInfo)
            {
                data.Set("name", name);
                data.Set("poisonous", poisonous);
                data.Set("attackStats", attackStats);
                data.Set("defenseStats", defenseStats);
                data.Set("bonuses", bonuses);
            }

            return data;
        }

        // Callback for when the currently equipped item is updated.
        public void OnUpdate(Action<Equipment> callback) => updateCallback = callback;
    }

    public sealed class ArmourSkin : Equipment
    {
        public ArmourSkin(string key = "", double count = -1) : base(Modules.Equipment.ArmourSkin, key, count) { }
    }

    public sealed class Arrows : Equipment
    {
        public string projectileName = "";

        public Arrows(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Arrows, key, count, enchantments) { }

        public override void Update(Item item)
        {
            base.Update(item);

            projectileName = item.projectileName;
            poisonous = item.poisonous;
            freezing = item.freezing;
            burning = item.burning;
        }
    }

    public sealed class Boots : Equipment
    {
        public Boots(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Boots, key, count, enchantments) { }

        public override void Update(Item item)
        {
            base.Update(item);

            movementModifier = item.movementModifier;
        }
    }

    public sealed class Cape : Equipment
    {
        public Cape(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Cape, key, count, enchantments) { }
    }

    public sealed class Chestplate : Equipment
    {
        public Chestplate(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Chestplate, key, count, enchantments) { }

        public bool HasThorns() => HasEnchantment(Modules.Enchantment.Thorns);

        public double GetThornsLevel()
        {
            if (!HasThorns()) return 0;

            return enchantments.Obj(((int)Modules.Enchantment.Thorns).ToString())?.Num("level") ?? double.NaN;
        }
    }

    public sealed class Helmet : Equipment
    {
        public Helmet(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Helmet, key, count, enchantments) { }
    }

    public sealed class Legplates : Equipment
    {
        public Legplates(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Legplates, key, count, enchantments) { }

        public override void Update(Item item)
        {
            base.Update(item);

            movementModifier = item.movementModifier;
        }
    }

    public sealed class Pendant : Equipment
    {
        public Pendant(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Pendant, key, count, enchantments) { }
    }

    public sealed class Ring : Equipment
    {
        public Ring(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Ring, key, count, enchantments) { }
    }

    public sealed class Shield : Equipment
    {
        public Shield(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Shield, key, count, enchantments) { }

        public override void Update(Item item)
        {
            base.Update(item);

            light = item.light;
        }

        public override JsObject Serialize(bool clientInfo = false)
        {
            var data = base.Serialize(clientInfo);

            if (clientInfo) data.Set("light", light);

            return data;
        }
    }

    public sealed class WeaponSkin : Equipment
    {
        public WeaponSkin(string key = "", double count = -1) : base(Modules.Equipment.WeaponSkin, key, count) { }
    }

    /// <summary>player/equipment/impl/weapon.ts.</summary>
    public sealed class Weapon : Equipment
    {
        public double attackRate = (double)Modules.Defaults.ATTACK_RATE;
        public Modules.AttackStyle attackStyle = Modules.AttackStyle.None;
        public string projectileName = "";
        public double attackRange = 1;
        public double lumberjacking = -1;
        public double mining = -1;
        public double fishing = -1;
        public double manaCost;

        // Default values for resetting variables when changing attack styles.
        public double defaultAttackRange = 1;
        public double defaultAttackRate = (double)Modules.Defaults.ATTACK_RATE;

        // Weapon type
        bool bow;
        bool archer;
        bool magic;
        bool twoHanded;
        List<Modules.AttackStyle> attackStyles = new();

        public Weapon(string key = "", double count = -1, JsObject enchantments = null)
            : base(Modules.Equipment.Weapon, key, count, enchantments) { }

        public override void Update(Item item) => Update(item, null);

        public void Update(Item item, double? attackStyle)
        {
            base.Update(item);

            attackRange = item.attackRange;
            defaultAttackRange = item.attackRange;
            attackRate = item.attackRate;
            attackStyles = item.GetAttackStyles();
            lumberjacking = item.lumberjacking;
            mining = item.mining;
            fishing = item.fishing;
            poisonous = item.poisonous;
            freezing = item.freezing;
            burning = item.burning;
            projectileName = item.projectileName;
            manaCost = item.manaCost;
            bow = item.IsBow();
            archer = item.IsArcherWeapon();
            magic = item.IsMagicWeapon();
            twoHanded = item.IsTwoHanded();

            if (attackStyle is double style && style != 0 && !double.IsNaN(style))
                UpdateAttackStyle((Modules.AttackStyle)(int)style);
            else UpdateAttackStyle(attackStyles.Count > 0 ? attackStyles[0] : null);
        }

        public override void Empty()
        {
            base.Empty();

            // Reset the weapon-specific properties
            attackStyles = new List<Modules.AttackStyle>();
            attackRange = 1;
            lumberjacking = -1;
            mining = -1;
            fishing = -1;
            bow = false;
            archer = false;
            magic = false;
            twoHanded = false;

            // Attack styles
            UpdateAttackStyle(Modules.AttackStyle.None);
        }

        /// <summary>updateAttackStyle(attackStyle); undefined (no styles) is kept as the weapon's style.</summary>
        public void UpdateAttackStyle(Modules.AttackStyle? attackStyle)
        {
            this.attackStyle = attackStyle ?? (Modules.AttackStyle)(-1);

            // Rapid attack style boosts attack speed by 20%
            attackRate = attackStyle == Modules.AttackStyle.Fast ? defaultAttackRate * 0.8 : defaultAttackRate;

            // Not applicable for ranged weapons.
            if (!archer && !magic) return;

            // Long range boosts attack range by 2 for bows and magic weapons
            attackRange = attackStyle == Modules.AttackStyle.LongRange ? defaultAttackRange + 2 : defaultAttackRange;
        }

        /// <summary>The style as the original holds it (undefined when a weapon has no styles).</summary>
        public object AttackStyleValue => (int)attackStyle == -1 ? null : (double)attackStyle;

        public bool IsStrength() => (bonuses.Num("strength") ?? double.NaN) > 0;

        public bool IsAccuracy() => (bonuses.Num("accuracy") ?? double.NaN) > 0;

        public bool IsArcher() => archer;

        public bool IsMagic() => magic;

        public bool IsLumberjacking() => lumberjacking > 0;

        public bool IsMining() => mining > 0;

        public bool IsFishing() => fishing > 0;

        public bool IsBloodsucking() => HasEnchantment(Modules.Enchantment.Bloodsucking);

        public bool IsCritical() => HasEnchantment(Modules.Enchantment.Critical);

        public bool IsDoubleEdged() => HasEnchantment(Modules.Enchantment.DoubleEdged);

        public bool IsStun() => HasEnchantment(Modules.Enchantment.Stun);

        public bool IsExplosive() => HasEnchantment(Modules.Enchantment.Explosive);

        public bool IsTwoHanded() => twoHanded;

        public bool HasAttackStyle(object attackStyle) =>
            attackStyle is double d && attackStyles.Any(style => (double)style == d);

        public override JsObject Serialize(bool clientInfo = false)
        {
            var data = base.Serialize(clientInfo);

            data.Set("attackStyle", AttackStyleValue);

            // Include additional properties to be sent to the client.
            if (clientInfo)
            {
                data.Set("attackRange", attackRange);
                data.Set("attackStyles", attackStyles.ConvertAll(style => (object)(double)style));
                data.Set("bow", bow);
                data.Set("archer", archer);
            }

            return data;
        }
    }
}
