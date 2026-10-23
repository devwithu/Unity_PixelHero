using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>player/skill/skill.ts. The skill classes carry a Skill suffix (Foraging is also a resource).</summary>
    public abstract class Skill
    {
        public string name = "";
        public double level = 1;
        public double experience;
        public double nextExperience;
        public bool combat;

        /// <summary>(type, name, withInfo, experience, level, newLevel).</summary>
        Action<Modules.Skills, string, bool, double, double, bool> experienceCallback;

        public Modules.Skills type;

        protected Skill(Modules.Skills type, bool combat = false)
        {
            this.type = type;
            name = type.ToString();
            // `public override combat = true` in the combat skills (a field initializer after super()).
            this.combat = combat;
        }

        public virtual void Stop() { }

        public double GetPercentage()
        {
            var prevExperience = Formulas.PrevExp(experience);
            var nextExperience = Formulas.NextExp(experience);
            var percentage = (experience - prevExperience) / (nextExperience - prevExperience);

            // Update the next experience for the skill.
            this.nextExperience = nextExperience;

            return percentage < 0 ? 0 : percentage;
        }

        public void AddExperience(double experience, bool withInfo = true)
        {
            var previousLevel = level;

            SetExperience(this.experience + experience);

            experienceCallback?.Invoke(type, name, withInfo, experience, level, level != previousLevel);
        }

        public void SetExperience(double experience)
        {
            level = Formulas.ExpToLevel(experience);
            this.experience = experience;
        }

        public JsObject Serialize(bool includeLevel = false)
        {
            var data = new JsObject("type", (double)type, "experience", experience);

            if (includeLevel)
            {
                data.Set("level", level);
                data.Set("percentage", GetPercentage());
                data.Set("nextExperience", nextExperience);
                data.Set("combat", combat);
            }

            return data;
        }

        public void OnExperience(Action<Modules.Skills, string, bool, double, double, bool> callback) =>
            experienceCallback = callback;
    }

    public sealed class AccuracySkill : Skill { public AccuracySkill() : base(Modules.Skills.Accuracy, true) { } }
    public sealed class AlchemySkill : Skill { public AlchemySkill() : base(Modules.Skills.Alchemy) { } }
    public sealed class ArcherySkill : Skill { public ArcherySkill() : base(Modules.Skills.Archery, true) { } }
    public sealed class CookingSkill : Skill { public CookingSkill() : base(Modules.Skills.Cooking) { } }
    public sealed class CraftingSkill : Skill { public CraftingSkill() : base(Modules.Skills.Crafting) { } }
    public sealed class DefenseSkill : Skill { public DefenseSkill() : base(Modules.Skills.Defense, true) { } }
    public sealed class EatingSkill : Skill { public EatingSkill() : base(Modules.Skills.Eating) { } }
    public sealed class FletchingSkill : Skill { public FletchingSkill() : base(Modules.Skills.Fletching) { } }
    public sealed class HealthSkill : Skill { public HealthSkill() : base(Modules.Skills.Health, true) { } }
    public sealed class LoiteringSkill : Skill { public LoiteringSkill() : base(Modules.Skills.Loitering) { } }
    public sealed class MagicSkill : Skill { public MagicSkill() : base(Modules.Skills.Magic, true) { } }
    public sealed class SmithingSkill : Skill { public SmithingSkill() : base(Modules.Skills.Smithing) { } }
    public sealed class StrengthSkill : Skill { public StrengthSkill() : base(Modules.Skills.Strength, true) { } }

    /// <summary>common/text/en/resource.ts.</summary>
    public static class ResourceText
    {
        public static string InvalidWeapon(Modules.Skills type) => type switch
        {
            Modules.Skills.Lumberjacking => "resource:MUST_EQUIP_AXE",
            Modules.Skills.Mining => "resource:MUST_EQUIP_PICKAXE",
            Modules.Skills.Fishing => "resource:MUST_EQUIP_FISHING",
            Modules.Skills.Foraging => "resource:MUST_EQUIP_FORAGING",
            _ => "INVALID_WEAPON() NOT IMPLEMENTED"
        };

        public static string InvalidLevel(Modules.Skills type, double level) => type switch
        {
            Modules.Skills.Lumberjacking => $"resource:INVALID_LEVEL_LUMBERJACKING;level={JsString.Of(level)}",
            Modules.Skills.Mining => $"resource:INVALID_LEVEL_MINING;level={JsString.Of(level)}",
            Modules.Skills.Fishing => $"resource:INVALID_LEVEL_FISHING;level={JsString.Of(level)}",
            Modules.Skills.Foraging => $"resource:INVALID_LEVEL_FORAGING;level={JsString.Of(level)}",
            _ => "INVALID_LEVEL() NOT IMPLEMENTED"
        };

        public static string UnableToInteract(Modules.Skills type) => type switch
        {
            Modules.Skills.Lumberjacking => "resource:UNABLE_TO_INTERACT_LUMBERJACKING",
            Modules.Skills.Mining => "resource:UNABLE_TO_INTERACT_MINING",
            Modules.Skills.Fishing => "resource:UNABLE_TO_INTERACT_FISHING",
            Modules.Skills.Foraging => "resource:UNABLE_TO_INTERACT_FORAGING",
            _ => "UNABLE_TO_INTERACT() NOT IMPLEMENTED"
        };
    }

    /// <summary>player/skill/resourceskill.ts.</summary>
    public class ResourceSkill : Skill
    {
        JsTimeout loop;
        string lastClickedResource = ""; // Instance of the last resource the player clicked on.
        double lastClickedCount; // Number of times the player has clicked on the resource.

        Action<Player, Resource> exhaustCallback;

        public bool randomDepletion;

        public ResourceSkill(Modules.Skills type) : base(type) { }

        public void Interact(Player player, Resource resource, double weaponLevel = 0)
        {
            if (resource.IsDepleted())
            {
                Log.Debug($"{player.username} attempted to interact with an exhausted resource.");
                return;
            }

            // Could not find resource interaction data for the resource.
            if (resource.data == null)
            {
                Log.Warning($"{player.username} attempted to interact with a resource with invalid data: {JsString.Of(resource.type)}");
                return;
            }

            var levelRequirement = resource.data.Num("levelRequirement") ?? double.NaN;

            // Level required for this resource is too high for the yplayer.
            if (levelRequirement > level)
            {
                player.Notify(ResourceText.InvalidLevel(type, levelRequirement));
                return;
            }

            // Unable to interact with the resource if the player hasn't completed the required achievement.
            if (JsData.Truthy(resource.data["reqAchievement"]) &&
                !(player.achievements.Get(resource.data.Str("reqAchievement"))?.IsFinished() ?? false))
            {
                player.Notify(ResourceText.UnableToInteract(type));
                return;
            }

            // Unable to interact with the resource if the player hasn't completed the required quest.
            if (JsData.Truthy(resource.data["reqQuest"]) &&
                !(player.quests.Get(resource.data.Str("reqQuest"))?.IsFinished() ?? false))
            {
                player.Notify(ResourceText.UnableToInteract(type));
                return;
            }

            if (!CanHold(player))
            {
                player.Notify("misc:NO_SPACE");
                return;
            }

            // Notification for the player to stop clicking on a resource repeatedly.
            if (lastClickedResource == resource.instance)
            {
                lastClickedCount++;

                if (lastClickedCount >= 2)
                {
                    player.Notify("misc:NO_NEED_CLICK_REPEATEDLY");

                    // Reset the counter.
                    lastClickedCount = 0;
                    lastClickedResource = "";
                }
            }
            else lastClickedCount = 0;

            if (loop != null) Stop();

            // Set the last clicked resource to the current resource.
            lastClickedResource = resource.instance;

            loop = Node.SetInterval(() =>
            {
                // Stops the loop when the resource is depleted or the player cannot hold the resource.
                if (resource.IsDepleted() || !CanHold(player))
                {
                    Stop();
                    return;
                }

                // Send the animation packet to the region player is in.
                player.SendToRegion(new AnimationPacket(new JsObject(
                    "instance", player.instance,
                    "resourceInstance", resource.instance,
                    "action", (double)Modules.Actions.Attack)));

                // Use probability to check if we can exhaust the resource.
                if (CanExhaustResource(weaponLevel, resource.data))
                {
                    // Add the logs to the inventory.
                    player.inventory.Add(GetItem(resource.data.Str("item")));

                    // Add experience to our skill.
                    AddExperience(resource.data.Num("experience") ?? double.NaN);

                    // Increment the statistics for the player.
                    player.statistics.HandleSkill(type);

                    // If resource has an achievement, attempt to award it if it hasn't been awarded yet.
                    if (JsData.Truthy(resource.data["achievement"]))
                        player.achievements.Get(resource.data.Str("achievement"))?.Finish();

                    // If the resource has a quest then we will call the resource callback.
                    if (JsData.Truthy(resource.data["quest"]))
                        player.quests.Get(resource.data.Str("quest"))?.resourceCallback?.Invoke(type, resource.key);

                    // Deplete the resource and send the signal to the region
                    if (ShouldDeplete()) resource.Deplete();

                    // Call the exhaust callback after we have successfully exhausted the resource.
                    exhaustCallback?.Invoke(player, resource);
                }
            }, Constants.SKILL_LOOP);
        }

        protected void HandleRandomItems(Player player, JsObject resourceInfo)
        {
            // If the resource doesn't have any random items then we can just return.
            if (!JsData.Truthy(resourceInfo?["randomItems"])) return;

            var randomItems = resourceInfo.Arr("randomItems");

            // Grab a random item from the list of random items and calculate the probability.
            var randomItem = randomItems.ElementAtOrDefault((int)Utils.RandomInt(0, randomItems.Count - 1)) as JsObject;
            var chance = Utils.RandomInt(0, Constants.DROP_PROBABILITY) < (randomItem?.Num("chance") ?? double.NaN);

            // Probability didn't work out so stop here.
            if (!chance) return;

            // Use the superclass `getItem` function to create the item instance since weekend events don't apply.
            var item = GetItem(randomItem.Str("key"), player.username);

            // If the player has space in their inventory add the item there, otherwise drop it on the ground.
            if (CanHold(player)) player.inventory.Add(item);
            else
            {
                // Set the item's position to the player's position.
                item.x = player.x;
                item.y = player.y;

                player.world.entities.AddItem(item);
            }
        }

        public override void Stop()
        {
            if (loop == null) return;

            Node.ClearTimeout(loop);
            loop = null;
        }

        /// <summary>getItem(key, owner = ''): Lumberjacking and Mining override the one-argument form.</summary>
        protected virtual Item GetItem(string key) => new(key, -1, -1, false, 1, new JsObject(), "");

        protected virtual Item GetItem(string key, string owner) => new(key, -1, -1, false, 1, new JsObject(), owner);

        public virtual bool CanHold(Player player) => player.inventory.HasSpace();

        protected virtual bool CanExhaustResource(double level, JsObject info)
        {
            // Subtract the product of the weapon's level and resource level from the resource's difficulty.
            var probability = (info.Num("difficulty") ?? double.NaN) - level * this.level;

            // Prevent probability from going too low.
            if (probability < 2) probability = 2;

            return Utils.RandomInt(0, probability) == 2;
        }

        bool ShouldDeplete()
        {
            // If the resource is not random, then we will always deplete it.
            if (!randomDepletion) return true;

            // 1 in 10 chance.
            return Utils.RandomInt(0, 10) == 4;
        }

        protected void OnExhaust(Action<Player, Resource> callback) => exhaustCallback = callback;
    }

    public sealed class FishingSkill : ResourceSkill
    {
        public FishingSkill() : base(Modules.Skills.Fishing)
        {
            // We want to randomize the depletion of the fishing spots.
            randomDepletion = true;

            OnExhaust((player, _) => HandleExhaust(player));
        }

        void HandleExhaust(Player player)
        {
            // Failed to roll against the chance of receiving kelp.
            if (!CanReceiveKelp()) return;

            // Create an item and make it belong to the player so others can't pick it up.
            var item = GetItem("kelp", player.username);

            // If the player has space in their inventory add the kelp there, otherwise drop it on the ground.
            if (CanHold(player)) player.inventory.Add(item);
            else player.world.entities.AddItem(item);
        }

        public void Catch(Player player, Resource spot)
        {
            var weapon = player.equipment.GetWeapon();

            // Player's weapon is not a valid fishing weapon.
            if (!weapon.IsFishing())
            {
                player.Notify(ResourceText.InvalidWeapon(type));
                return;
            }

            // Player is in the tutorial, we want to prevent them from fishing.
            if (!player.quests.CanFishInTutorial())
            {
                player.Notify("resource:NO_REASON");
                return;
            }

            Interact(player, spot, weapon.fishing);
        }

        bool CanReceiveKelp() => Utils.RandomInt(0, 100) <= 10;
    }

    public sealed class ForagingSkill : ResourceSkill
    {
        public ForagingSkill() : base(Modules.Skills.Foraging) { }

        // Nothing for now, add tools later.
        public void Harvest(Player player, Resource spot) => Interact(player, spot);

        // For now this defaults to true. we complicate foraging later.
        protected override bool CanExhaustResource(double level, JsObject info) => true;
    }

    public sealed class LumberjackingSkill : ResourceSkill
    {
        public LumberjackingSkill() : base(Modules.Skills.Lumberjacking) =>
            OnExhaust((player, resource) =>
                // Use the superclass logic to handle the random item selection.
                HandleRandomItems(player, resource.data));

        public void Cut(Player player, Resource tree)
        {
            var weapon = player.equipment.GetWeapon();

            // Player's weapon is not a valid lumberjacking weapon.
            if (!weapon.IsLumberjacking())
            {
                player.Notify(ResourceText.InvalidWeapon(type));
                return;
            }

            // Player is in the tutorial, we want to prevent them from cutting trees.
            if (!player.quests.CanCutTreesInTutorial())
            {
                player.Notify("resource:NO_REASON");
                return;
            }

            Interact(player, tree, weapon.lumberjacking);
        }

        protected override Item GetItem(string key) => new(key, -1, -1, false, Utils.doubleLumberjacking ? 2 : 1);

        /// <summary>The override takes one argument, so a call with an owner also lands here.</summary>
        protected override Item GetItem(string key, string owner) => GetItem(key);

        public override bool CanHold(Player player) => player.inventory.HasSpace(Utils.doubleLumberjacking ? 2 : 1);
    }

    public sealed class MiningSkill : ResourceSkill
    {
        public MiningSkill() : base(Modules.Skills.Mining) =>
            OnExhaust((player, resource) => HandleRandomItems(player, resource.data));

        public void Mine(Player player, Resource rock)
        {
            var weapon = player.equipment.GetWeapon();

            // Player's weapon is not a valid mining weapon.
            if (!weapon.IsMining())
            {
                player.Notify(ResourceText.InvalidWeapon(type));
                return;
            }

            // Pass the info onto the super class interact function.
            Interact(player, rock, weapon.mining);
        }

        protected override Item GetItem(string key) => new(key, -1, -1, false, Utils.doubleMining ? 2 : 1);

        protected override Item GetItem(string key, string owner) => GetItem(key);

        public override bool CanHold(Player player) => player.inventory.HasSpace(Utils.doubleMining ? 2 : 1);
    }

    /// <summary>player/skills.ts.</summary>
    public sealed class Skills
    {
        bool loaded;

        readonly AccuracySkill accuracy = new();
        readonly ArcherySkill archery = new();
        readonly HealthSkill health = new();
        readonly LumberjackingSkill lumberjacking = new();
        readonly MagicSkill magic = new();
        readonly StrengthSkill strength = new();
        readonly MiningSkill mining = new();
        readonly DefenseSkill defense = new();
        readonly FishingSkill fishing = new();
        readonly CookingSkill cooking = new();
        readonly SmithingSkill smithing = new();
        readonly CraftingSkill crafting = new();
        readonly FletchingSkill fletching = new();
        readonly ForagingSkill foraging = new();
        readonly EatingSkill eating = new();
        readonly LoiteringSkill loitering = new();
        readonly AlchemySkill alchemy = new();

        /// <summary>Keyed by the skill's number, so iteration is in ascending type order.</summary>
        readonly JsObject<Skill> skills = new();

        Action loadCallback;

        readonly Player player;

        public Skills(Player player)
        {
            this.player = player;

            foreach (var skill in new Skill[]
                     {
                         accuracy, archery, health, lumberjacking, magic, strength, mining, defense, fishing, cooking,
                         smithing, crafting, fletching, foraging, eating, loitering, alchemy
                     })
                skills.Set(((int)skill.type).ToString(), skill);
        }

        public void Load(List<object> data)
        {
            // Load each skill from the database (empty if new player).
            foreach (var entry in data)
            {
                var skillData = entry as JsObject;
                var skill = Get(skillData?["type"]);

                if (skill != null) skill.SetExperience(skillData.Num("experience") ?? double.NaN);
            }

            // Create a callback that links to `handleExperience` for every skill.
            ForEachSkill(skill => skill.OnExperience(HandleExperience));

            loaded = true;

            loadCallback?.Invoke();

            Sync();
        }

        public void Sync()
        {
            // Prevent a sync prior to loading from messing up player information.
            if (!loaded) return;

            var health = Get(Modules.Skills.Health);
            var magic = Get(Modules.Skills.Magic);

            // Update max hit points and mana.
            player.hitPoints.SetMaxHitPoints(Formulas.GetMaxHitPoints(health.level));
            player.mana.SetMaxMana(Formulas.GetMaxMana(magic.level));

            // Update the player's level.
            player.level = GetCombatLevel();

            // Synchronize the player's level packet.
            player.Send(new ExperiencePacket(Opcodes.Experience.Sync, new JsObject(
                "instance", player.instance,
                "level", player.level)));

            // Synchronize mana and hit points.
            player.Send(new PointsPacket(new JsObject(
                "instance", player.instance,
                "hitPoints", player.hitPoints.GetHitPoints(),
                "maxHitPoints", player.hitPoints.GetMaxHitPoints(),
                "mana", player.mana.GetMana(),
                "maxMana", player.mana.GetMaxMana())));
        }

        public void Stop() => ForEachSkill(skill => skill.Stop());

        void HandleExperience(Modules.Skills type, string name, bool withInfo, double experience, double level, bool newLevel)
        {
            if (newLevel)
            {
                player.Popup("misc:SKILL_LEVEL_UP", $"misc:SKILL_LEVEL_UP_DESC;name={name};level={JsString.Of(level)}", "#9933ff");

                // Update the player's max health if they have gained a level in health skill.
                if (type == Modules.Skills.Health) player.SetHitPoints(Formulas.GetMaxHitPoints(level));

                // Update the player's level if they have gained a level in a combat skill.
                Sync();
            }

            if (withInfo)
                player.Send(new ExperiencePacket(Opcodes.Experience.Skill, new JsObject(
                    "instance", player.instance,
                    "amount", experience,
                    "skill", (double)type)));

            player.Send(new SkillPacket(Opcodes.Skill.Update, skills[((int)type).ToString()].Serialize(true)));
        }

        /// <summary>this.skills[type] for a number, or undefined.</summary>
        public Skill Get(object type) => type switch
        {
            double d => skills[JsString.Of(d)],
            string s => skills[s],
            _ => null
        };

        public Skill Get(Modules.Skills type) => skills[((int)type).ToString()];

        public Skill Get(Modules.Skills? type) => type == null ? null : Get(type.Value);

        public List<Skill> GetCombatSkills() => skills.Values.Where(skill => skill.combat).ToList();

        public LumberjackingSkill GetLumberjacking() => lumberjacking;

        public MiningSkill GetMining() => mining;

        public FishingSkill GetFishing() => fishing;

        public ForagingSkill GetForaging() => foraging;

        public double GetCombatLevel()
        {
            double level = 1;

            foreach (var skill in GetCombatSkills()) level += skill.level - 1;

            return level;
        }

        public JsObject Serialize(bool includeLevel = false)
        {
            var list = new List<object>();

            ForEachSkill(skill => list.Add(skill.Serialize(includeLevel)));

            return new JsObject("skills", list, "cheater", player.IsCheater());
        }

        public void ForEachSkill(Action<Skill> callback)
        {
            foreach (var skill in skills.Values) callback(skill);
        }

        public void OnLoaded(Action callback) => loadCallback = callback;
    }
}
