using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>player/ability/ability.ts (and the impl subclasses as overrides).</summary>
    public class Ability
    {
        readonly JsObject data;
        double lastActivated;

        Action<Player> deactivateCallback;
        Action<string, double, double> updateCallback;

        public string key;
        double level;
        double quickSlot;

        public Ability(string key, double level = 1, double quickSlot = -1)
        {
            this.key = key;
            this.level = level;
            this.quickSlot = quickSlot;

            data = ServerData.Abilities.Obj(key);
        }

        public virtual bool Activate(Player player)
        {
            // Passive abilities are not activated.
            if (data.Str("type") != "active" || !JsData.Truthy(data["levels"])) return false;

            var levelData = data.Obj("levels").Obj(JsString.Of(level)) ??
                            throw new JsTypeError("Cannot destructure property 'cooldown' of undefined");
            var cooldown = levelData["cooldown"];
            var duration = levelData["duration"];
            var mana = levelData["mana"];

            // Someone somewhere forgot to specify a mana cost for the ability.
            if (!JsData.Truthy(mana))
            {
                Log.Warning($"Ability {key} has no mana cost.");
                return false;
            }

            // Ensure active abilities have a cooldown and duration.
            if (!JsData.Truthy(cooldown) || !JsData.Truthy(duration))
            {
                Log.Warning($"Ability {key} has no cooldown or duration.");
                return false;
            }

            double manaCost = (double)mana, cooldownTime = (double)cooldown, durationTime = (double)duration;

            // Player doesn't have enough mana.
            if (player.mana.GetMana() < manaCost)
            {
                player.Notify("misc:NOT_ENOUGH_MANA");
                return false;
            }

            var formattedDuration = Math.Floor((cooldownTime - (Node.DateNow() - lastActivated)) / 1000);

            // Ensure the ability is not on cooldown.
            if (IsCooldown(cooldownTime))
            {
                player.Notify($"misc:NEED_WAIT_ABILITY;duration={JsString.Of(formattedDuration)}");
                return false;
            }

            player.abilities.toggleCallback?.Invoke(key);

            // Remove the ability mana cost from the player.
            player.mana.Decrement(manaCost);

            // Ability will deactivate and create a callback after `duration` milliseconds.
            Node.SetTimeout(() =>
            {
                deactivateCallback?.Invoke(player);

                // Send a packet to the client to untoggle the ability.
                player.abilities.toggleCallback?.Invoke(key);
            }, durationTime);

            // Update the date of the last time the ability was activated.
            lastActivated = Node.DateNow();

            return true;
        }

        public bool IsValid() => data != null;

        bool IsCooldown(double cooldown) => Node.DateNow() - lastActivated < cooldown;

        public bool HasQuickSlot(double? quickSlot = null) => quickSlot == null ? this.quickSlot > -1 : this.quickSlot == quickSlot;

        public Modules.AbilityType GetAbilityType() =>
            data.Str("type") == "active" ? Modules.AbilityType.Active : Modules.AbilityType.Passive;

        public void SetLevel(double level)
        {
            // Ability levels range from 1-4.
            if (level < 1) level = 1;
            if (level > 4) level = 4;

            this.level = level;

            updateCallback?.Invoke(key, level, quickSlot);
        }

        public void SetQuickSlot(double quickSlot)
        {
            if (quickSlot < 0) quickSlot = -1;
            if (quickSlot > 4) quickSlot = 4;

            this.quickSlot = quickSlot;

            updateCallback?.Invoke(key, level, quickSlot);
        }

        public JsObject Serialize(bool includeType = false)
        {
            var data = new JsObject("key", key, "level", level, "quickSlot", quickSlot);

            if (includeType) data.Set("type", (double)GetAbilityType());

            return data;
        }

        public void OnDeactivate(Action<Player> callback) => deactivateCallback = callback;

        public void OnUpdate(Action<string, double, double> callback) => updateCallback = callback;
    }

    /// <summary>The impl abilities that need a target to activate (hotshot, intimidate, precognition, secretcalling).</summary>
    public sealed class CombatAbility : Ability
    {
        public CombatAbility(string key, double level, double quickSlot = -1) : base(key, level, quickSlot) { }

        public override bool Activate(Player player)
        {
            if (!player.HasTarget())
            {
                player.Notify("misc:NEED_COMBAT");
                return false;
            }

            return base.Activate(player);
        }
    }

    public sealed class DualistsMarkAbility : Ability
    {
        public DualistsMarkAbility(double level, double quickSlot = -1) : base("dualistsmark", level, quickSlot) =>
            OnDeactivate(player => player.SetDualistsMark(false));

        public override bool Activate(Player player)
        {
            if (base.Activate(player)) player.SetDualistsMark(true);

            return false;
        }
    }

    public sealed class RunAbility : Ability
    {
        public RunAbility(double level, double quickSlot = -1) : base("run", level, quickSlot) =>
            // Revert the player's speed back to normal when ability is deactivated.
            OnDeactivate(player => player.SetRunning(false));

        public override bool Activate(Player player)
        {
            if (base.Activate(player)) player.SetRunning(true);

            return false;
        }
    }

    public sealed class ThickSkinAbility : Ability
    {
        public ThickSkinAbility(double level, double quickSlot = -1) : base("thickskin", level, quickSlot) =>
            OnDeactivate(player => player.status.Remove(Modules.Effects.ThickSkin));

        public override bool Activate(Player player)
        {
            if (base.Activate(player)) player.status.Add(Modules.Effects.ThickSkin);

            return false;
        }
    }

    /// <summary>player/abilities.ts.</summary>
    public sealed class Abilities
    {
        /// <summary>ability/impl/index.ts.</summary>
        static readonly Dictionary<string, Func<double, double, Ability>> Index = new()
        {
            ["intimidate"] = (level, slot) => new CombatAbility("intimidate", level, slot),
            ["run"] = (level, slot) => new RunAbility(level, slot),
            ["awareness"] = (level, slot) => new Ability("awareness", level, slot),
            ["dualistsmark"] = (level, slot) => new DualistsMarkAbility(level, slot),
            ["hotshot"] = (level, slot) => new CombatAbility("hotshot", level, slot),
            ["thickskin"] = (level, slot) => new ThickSkinAbility(level, slot),
            ["precognition"] = (level, slot) => new CombatAbility("precognition", level, slot),
            ["secretcalling"] = (level, slot) => new CombatAbility("secretcalling", level, slot)
        };

        JsObject<Ability> abilities = new(); // All the abilities that the player has.

        Action loadCallback;
        Action<Ability> addCallback;
        public Action<string> toggleCallback;

        readonly Player player;

        public Abilities(Player player) => this.player = player;

        public void Load(JsObject info)
        {
            foreach (var entry in info.Arr("abilities") ??
                                  throw new JsTypeError("info.abilities is not iterable"))
            {
                var ability = entry as JsObject;

                Add(ability?.Str("key"), ability?.Num("level") ?? double.NaN, ability?.Num("quickSlot") ?? -1, true);
            }

            loadCallback?.Invoke();
        }

        void HandleUpdate(string key, double level, double quickSlot) =>
            player.Send(new AbilityPacket(Opcodes.Ability.Update, new JsObject(
                "key", key,
                "level", level,
                "quickSlot", quickSlot)));

        public void Use(string key)
        {
            if (key != null) abilities[key]?.Activate(player);
        }

        public void Add(string key, double level, double quickSlot = -1, bool skipAddCallback = false)
        {
            // Ensure the ability exists within the index.
            if (key == null || !Index.ContainsKey(key))
            {
                Log.Warning($"Ability {key ?? "undefined"} does not exist.");
                return;
            }

            // If the ability already exists, we just update the level.
            if (Has(key))
            {
                SetLevel(key, level);
                return;
            }

            // Create ability based on the key from index of ability classes.
            var ability = Index[key](level, quickSlot);

            // Ensure the ability has valid data.
            if (!ability.IsValid())
            {
                Log.Warning($"[{player.username}] Invalid ability: {key}");
                return;
            }

            // On ability update listener.
            ability.OnUpdate(HandleUpdate);

            // Add the ability to the player's abilities.
            abilities.Set(key, ability);

            if (!skipAddCallback) addCallback?.Invoke(ability);
        }

        public bool Has(string key) => abilities.Has(key);

        public void SetLevel(string key, double level) => abilities[key]?.SetLevel(level);

        public void SetQuickSlot(string key, double quickSlot)
        {
            // Clears a quick slot from another ability if it is found.
            foreach (var ability in abilities.Values)
                if (ability.HasQuickSlot(quickSlot))
                    ability.SetQuickSlot(-1);

            // Updates the quick slot of the ability.
            if (key != null) abilities[key]?.SetQuickSlot(quickSlot);
        }

        public void Reset()
        {
            abilities = new JsObject<Ability>();

            // Signal to the client to reload the abilities.
            loadCallback?.Invoke();
        }

        public JsObject Serialize(bool includeType = false)
        {
            var list = new List<object>();

            foreach (var ability in abilities.Values) list.Add(ability.Serialize(includeType));

            return new JsObject("abilities", list);
        }

        public void OnLoaded(Action callback) => loadCallback = callback;

        public void OnAdd(Action<Ability> callback) => addCallback = callback;

        public void OnToggle(Action<string> callback) => toggleCallback = callback;
    }
}
