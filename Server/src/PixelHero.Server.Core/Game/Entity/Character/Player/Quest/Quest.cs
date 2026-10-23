using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>player/quest/quest.ts. Stage data stays JSON (RawStage/StageData objects).</summary>
    public class Quest
    {
        public string name = "";
        string description = "";
        List<object> rewards = new();
        JsObject skillRequirements = new();
        List<object> questRequirements = new(); // List of quests required to start this quest.
        string difficulty = ""; // Difficulty of the quest.
        JsObject hideNPCs = new(); // NPCs to hide after quest.
        List<object> completedSubStages = new(); // The substages that the player has completed, used for cases such as talking to an NPC.

        protected double stage; // How far along in the quest we are.
        double subStage; // Progress in the substage (say we're tasked to kill 20 rats, or talk to multiple NPCs).
        protected double stageCount; // How long the quest is.

        double timer; // Used by timer to indicate duration amount of stage.
        double expiration; // Used by timer to indicate expiration date.
        bool pendingStart; // Whether or not the quest is pending start.
        protected bool noPrompts; // Used to skip the interface prompts for the quest.

        JsObject stageData; // Current stage data, constantly updated when progression occurs.
        readonly JsObject stages; // All the stages from the JSON data.

        // Store all NPCs involved in the quest.
        readonly List<string> npcs = new();

        JsTimeout timerTimeout;

        Action<string> startCallback;
        Action<string, double, double> progressCallback;
        Action<JsObject> pointerCallback;
        Action<JsObject> popupCallback;

        public Action<NPC, Player> talkCallback;
        public Action<JsObject, Player> doorCallback;
        public Action<Mob> killCallback;
        public Action<Modules.Skills, string> resourceCallback;

        readonly string key;

        public Quest(string key, JsObject rawData)
        {
            this.key = key;

            name = rawData.Str("name");
            description = rawData.Str("description");
            rewards = JsData.Truthy(rawData["rewards"]) ? rawData.Arr("rewards") : new List<object>();
            skillRequirements = JsData.Truthy(rawData["skillRequirements"]) ? rawData.Obj("skillRequirements") : new JsObject();
            questRequirements = JsData.Truthy(rawData["questRequirements"]) ? rawData.Arr("questRequirements") : new List<object>();
            difficulty = JsData.Truthy(rawData["difficulty"]) ? rawData.Str("difficulty") : "";
            hideNPCs = JsData.Truthy(rawData["hideNPCs"]) ? rawData.Obj("hideNPCs") : new JsObject();
            stageCount = rawData.Obj("stages").Count;
            stages = rawData.Obj("stages");
            stageData = GetStageData();

            LoadNPCs();

            // Callbacks
            OnTalk(HandleTalk);
            OnDoor(HandleDoor);
            OnKill(HandleKill);
            OnResource(HandleResource);
        }

        public virtual void Loaded() { }

        void LoadNPCs()
        {
            // Iterate through the stages and extract the NPCs.
            foreach (var value in stages.Values)
            {
                var stage = (JsObject)value;

                // If the stage has sub-stages, iterate through those as well and extract the NPCs.
                if (JsData.Truthy(stage["subStages"]))
                    foreach (var sub in stage.Arr("subStages"))
                    {
                        var subNpc = ((JsObject)sub).Str("npc");
                        if (JsData.Truthy(subNpc) && !HasNPC(subNpc)) npcs.Add(subNpc);
                    }

                // Add each NPC to the list if the stage has one and it is not already in the list.
                var npc = stage.Str("npc");
                if (JsData.Truthy(npc) && !HasNPC(npc)) npcs.Add(npc);
            }

            // Look through the hideNPCs object and extract the NPCs.
            foreach (var pair in hideNPCs)
                if (!HasNPC(pair.Key))
                    npcs.Add(pair.Key);
        }

        void HandleTalk(NPC npc, Player player)
        {
            // Extract the dialogue for the NPC.
            var dialogue = GetNPCDialogue(npc, player);

            if (dialogue == null)
            {
                Log.Warning($"[{name}] No dialogue found for NPC: {npc.key}.");
                return;
            }

            // Attempt to check for substage component and use that information instead.
            var subStageNPC = GetSubStageByNPC(npc.key);
            var stageInfo = subStageNPC ?? stageData; // Use whichever stage data is present.

            if (stageInfo.Str("npc") == npc.key && dialogue.Count == player.talkIndex &&
                !completedSubStages.Contains(npc.key))
            {
                if (HasItemRequirement(stageInfo)) HandleItemRequirement(player, stageInfo);
                else if (HasItemToGive(stageInfo)) GivePlayerRewards(player, stageInfo.Arr("itemRewards"), true);
                else if (HasAbility(stageInfo)) GivePlayerAbility(player);
                else if (HasExperience(stageInfo)) GivePlayerExperience(player);
                else Progress();
            }

            // Talk to the NPC and progress the dialogue.
            npc.Talk(player, dialogue);
        }

        protected virtual void HandleDoor(JsObject door, Player player)
        {
            Log.Debug($"[{name}] Door: {JsString.Of(door["x"])}-{JsString.Of(door["y"])} - stage: {JsString.Of(stage)}.");

            if (stage < JsAny.ToNumber(door["stage"]))
            {
                player.Notify("misc:CANNOT_PASS_DOOR");
                return;
            }

            player.Teleport(door.Num("x") ?? double.NaN, door.Num("y") ?? double.NaN);

            // Progress only if the door is a task.
            if (IsDoorTask()) Progress();
        }

        void HandleKill(Mob mob)
        {
            Log.Debug($"[{name}] Killing mob: {mob.key}, stage: {JsString.Of(stage)}, subStage: {JsString.Of(subStage)}.");

            if (!JsData.Truthy(stageData["mob"]))
            {
                Log.Error($"[{name}] No mob data for stage: {JsString.Of(stage)}.");
                return;
            }

            // Stage progression has expired, so we don't progress.
            if (timer != 0 && !double.IsNaN(timer) && Node.DateNow() - timer > expiration) return;

            // Substage progression when the mob killed matches the quest's list of mobs.
            if (Includes(stageData["mob"], mob.key)) Progress(true);

            // Progress to the next stage after we reach the `mobCountRequirement`.
            if (subStage >= (stageData.Num("mobCountRequirement") ?? double.NaN)) Progress();
        }

        /// <summary>value.includes(key) for a string (substring) or an array (element).</summary>
        static bool Includes(object value, string key) => value switch
        {
            string s => s.Contains(key ?? "undefined", StringComparison.Ordinal),
            List<object> list => list.Any(item => item is string s && s == key),
            _ => throw new JsTypeError("includes is not a function")
        };

        void HandleResource(Modules.Skills type, string subType)
        {
            // Stop if the quest has already been completed.
            if (IsFinished()) return;

            // Extract the type of resource based on the skill.
            var resourceType = Utils.GetResourceType(type);
            var resource = stageData[resourceType];
            var resourceCount = stageData[$"{resourceType}Count"];

            // Stage data does not require a tree to be cut.
            if (!JsData.Truthy(resource)) return;

            // Don't progress if we are not cutting the tree specified.
            if (!(resource is string r && r == subType)) return;

            // Progress the quest if no tree count is specified but a tree stage is present.
            if (!JsData.Truthy(resourceCount))
            {
                Progress();
                return;
            }

            // Progress substage.
            Progress(true);

            // Progress to next stage if we fulfill the tree count requirement.
            if (subStage >= JsAny.ToNumber(resourceCount)) Progress();
        }

        void HandleItemRequirement(Player player, JsObject stageData)
        {
            // Extract the item key and count requirement.
            var itemRequirements = stageData.Arr("itemRequirements");
            var itemRewards = stageData.Arr("itemRewards");
            var npc = stageData.Str("npc");

            // Skip if the player does not have the required item and count in the inventory.
            if (!HasAllItems(player, itemRequirements)) return;

            // Skip if the player does not have enough space in the inventory for the item rewards.
            if (itemRewards != null && !player.inventory.HasSpace(itemRewards.Count))
            {
                player.Notify("misc:NO_SPACE");
                return;
            }

            // Iterate through the items and remove them from the player's inventory.
            foreach (JsObject item in itemRequirements)
                player.inventory.RemoveItem(item.Str("key"), item.Num("count") ?? double.NaN);

            // If the stage contains item rewards, we give it to the player.
            if (HasItemToGive(stageData)) GivePlayerRewards(player, itemRewards);

            // Grant the experience from the stage.
            if (HasExperience(stageData)) GivePlayerExperience(player);

            // If the stage rewards an ability, we give it to the player.
            if (HasAbility(stageData)) GivePlayerAbility(player);

            var subStages = this.stageData.Arr("subStages");

            // If we're dealing with substages, we add the NPC to the completed list.
            if (subStages != null && !completedSubStages.Contains(npc)) completedSubStages.Add(npc);

            var isSubStage = subStages != null && completedSubStages.Count < subStages.Count;

            Progress(isSubStage);
        }

        public void HandlePrompt()
        {
            if (!pendingStart || IsStarted()) return;

            // Progress the stage by skipping prompts.
            SetStage(stage + 1, 0, true, true);
        }

        void Progress(bool subStage = false)
        {
            // Apply the expiration timer if it's present in the current stage.
            if (JsData.Truthy(stageData["timer"]))
            {
                timer = stageData.Num("timer") ?? double.NaN;
                expiration = Node.DateNow() + timer;
            }

            // Progress substage only if the parameter is defined.
            if (subStage) SetStage(stage, this.subStage + 1);
            else SetStage(stage + 1);
        }

        void GivePlayerRewards(Player player, List<object> itemRewards = null, bool progress = false)
        {
            itemRewards ??= new List<object>();

            // We play it extra safe by ensuring there are at least as many empty spaces as there are reward items.
            if (!player.inventory.HasSpace(itemRewards.Count))
            {
                player.Notify("misc:PLEASE_MAKE_ROOM_REWARD");
                return;
            }

            // Check if the player has enough inventory space for the item rewards.
            foreach (JsObject item in itemRewards)
                player.inventory.Add(new Item(item.Str("key"), -1, -1, false, item.Num("count") ?? -1));

            // Progress to the next stage if the parameter is true.
            if (progress) Progress();
        }

        void GivePlayerAbility(Player player) =>
            player.abilities.Add(stageData.Str("ability"),
                JsData.Truthy(stageData["abilityLevel"]) ? (double)stageData["abilityLevel"] : 1);

        void GivePlayerExperience(Player player)
        {
            foreach (JsObject skillReward in stageData.Arr("skillRewards"))
            {
                var skill = player.skills.Get(Utils.GetSkill(skillReward.Str("key")));

                skill?.AddExperience(skillReward.Num("experience") ?? double.NaN);
            }
        }

        void ClearTimer()
        {
            Node.ClearTimeout(timerTimeout);
            timerTimeout = null;
        }

        public bool HasNPC(string key) => npcs.Contains(key);

        static bool HasItemRequirement(JsObject stageData) => (stageData.Arr("itemRequirements") ?? new List<object>()).Count > 0;

        static bool HasItemToGive(JsObject stageData) => (stageData.Arr("itemRewards") ?? new List<object>()).Count > 0;

        static bool HasAbility(JsObject stageData) => JsData.Truthy(stageData["ability"]);

        static bool HasExperience(JsObject stageData) => (stageData.Arr("skillRewards")?.Count ?? 0) > 0;

        static bool HasAllItems(Player player, List<object> items = null)
        {
            var hasItems = true;

            // Iterate and ensure that the player has all the items.
            foreach (JsObject item in items ?? new List<object>())
                if (!player.inventory.HasItem(item.Str("key"), item.Num("count") ?? 1))
                {
                    hasItems = false;
                    break;
                }

            return hasItems;
        }

        public bool HasRequirements(Player player)
        {
            // Iterate through the skills and check if the player has the required level.
            foreach (var pair in skillRequirements)
                if (player.skills.Get(Utils.GetSkill(pair.Key)).level < JsAny.ToNumber(pair.Value))
                    return false;

            // Iterate through the quests and check if the player has completed them.
            foreach (var quest in questRequirements)
                if (!player.quests.Get(quest as string).IsFinished())
                    return false;

            return true;
        }

        public bool IsStarted() => stage > 0;

        public bool IsFinished(bool nextStage = false) => (nextStage ? stage + 1 : stage) >= stageCount;

        public bool IsNPCVisible(string key)
        {
            var npc = hideNPCs[key];

            if (!JsData.Truthy(npc)) return true;

            return npc is string s && s == "before" ? IsFinished() : !IsFinished();
        }

        public bool IsNPCForStage(Player player, NPC npc)
        {
            var stage = GetStageData();

            // NPC is not in the current stage so we just skip.
            if (stage.Str("npc") != npc.key) return false;

            // If the stage doesn't have any item requirements, then the NPC belongs to the stage and the player must talk to them.
            if (!HasItemRequirement(stage)) return true;

            // If the player has all the requirements for the stage.
            if (HasAllItems(player, stage.Arr("itemRequirements"))) return true;

            return false;
        }

        bool IsDoorTask() => stageData.Str("task") == "door";

        public bool IsKillTask() => stageData.Str("task") == "kill";

        public bool IsCutTreeTask() => stageData.Str("task") == "tree";

        public bool IsFishingTask() => stageData.Str("task") == "fish";

        public JsObject GetSubStageByNPC(string key)
        {
            if (!JsData.Truthy(stageData["subStages"])) return null;

            return stageData.Arr("subStages").Cast<JsObject>().FirstOrDefault(subStage => subStage.Str("npc") == key);
        }

        public double GetStage() => stage;

        /// <summary>StageData for the current stage (the stage must exist, as in the original).</summary>
        public JsObject GetStageData()
        {
            var stage = stages.Obj(JsString.Of(this.stage)) ??
                        throw new JsTypeError("Cannot read properties of undefined (reading 'task')");

            object Or(string field, object fallback) => JsData.Truthy(stage[field]) ? stage[field] : fallback;

            return new JsObject(
                "task", stage["task"],
                "subStages", Or("subStages", new List<object>()),
                "npc", Or("npc", ""),
                "mob", Or("mob", ""),
                "mobCountRequirement", Or("mobCountRequirement", 0.0),
                "itemRequirements", Or("itemRequirements", new List<object>()),
                "itemRewards", Or("itemRewards", new List<object>()),
                "text", Or("text", new List<object> { "" }),
                "pointer", Or("pointer", null),
                "popup", Or("popup", null),
                "tree", Or("tree", ""),
                "treeCount", Or("treeCount", 0.0),
                "fish", Or("fish", ""),
                "fishCount", Or("fishCount", 0.0),
                "rock", Or("rock", ""),
                "rockCount", Or("rockCount", 0.0),
                "skillRewards", Or("skillRewards", new List<object>()),
                "timer", Or("timer", 0.0));
        }

        List<object> GetNPCDialogue(NPC npc, Player player)
        {
            // Iterate backwards, last reference of the NPC is the text we grab.
            for (var i = stage; i > -1; i--)
            {
                // We do not count iterations of stages above stage we are currently on.
                if (stage < i) continue;

                var stageObject = stages.Obj(JsString.Of(i));
                var subStage = GetSubStageByNPC(npc.key);

                // If no key is found, continue iterating.
                if ((stageObject ?? throw new JsTypeError("Cannot read properties of undefined (reading 'npc')"))
                    .Str("npc") != npc.key && subStage == null)
                    continue;

                // Substitute the sub stage information into the stage and use that to process the dialogue.
                if (subStage != null) stageObject = subStage;

                if (subStage != null && completedSubStages.Contains(subStage.Str("npc")))
                    return stageObject.Arr("completedText");

                // Ensure we are on the correct stage and that it has an item requirement, otherwise skip.
                if (HasItemRequirement(stageObject) && stage == i)
                {
                    // Verify that the player has the required items and return the dialogue for it.
                    if (HasAllItems(player, stageObject.Arr("itemRequirements"))) return stageObject.Arr("hasItemText");

                    // Skip to next stage iteration.
                    continue;
                }

                if (stage > i) return stageObject.Arr("completedText");

                return stageObject.Arr("text");
            }

            // The default text witll be the `text` array of strings.
            if (stageData.Str("npc") == npc.key) return stageData.Arr("text");

            return new List<object> { "" };
        }

        public void SetStage(double stage, double subStage = 0, bool progressCallback = true, bool skipPrompts = false)
        {
            var isProgress = this.stage != stage;

            // Send popup before setting the new stage.
            if (JsData.Truthy(stageData["popup"])) popupCallback?.Invoke(stageData.Obj("popup"));

            // Clear pointer preemptively if the current stage data contains it.
            if (JsData.Truthy(stageData["pointer"])) pointerCallback?.Invoke(new JsObject("type", 3.0));

            // Clear the timer timeout.
            ClearTimer();

            // Certain quests can skip the prompts (like the tutorial for example).
            if (!noPrompts && !skipPrompts && progressCallback && !IsStarted())
            {
                pendingStart = true;
                startCallback?.Invoke(key);
                return;
            }

            // Reset the pending flags.
            pendingStart = false;

            this.stage = stage;
            this.subStage = subStage;

            if (isProgress && progressCallback)
            {
                // Clear the completed substages.
                completedSubStages = new List<object>();

                // Callback so that we send information to the player.
                this.progressCallback?.Invoke(key, stage, stageCount);
            }

            if (IsFinished()) return;

            // Update the latest stage data.
            stageData = GetStageData();

            // Handle loading a stage that has a timer
            if (JsData.Truthy(stageData["timer"]))
            {
                if (progressCallback)
                {
                    timer = stageData.Num("timer") ?? double.NaN;
                    expiration = Node.DateNow() + timer;

                    // Create a timeout to roll back the stage if the timer expires without progression.
                    timerTimeout = Node.SetTimeout(() => SetStage(stage - 1), timer);
                }
                else SetStage(stage - 1);
            }

            // Check if the current stage has any pointer information.
            if (JsData.Truthy(stageData["pointer"])) pointerCallback?.Invoke(stageData.Obj("pointer"));
        }

        public void SetCompletedSubStages(List<object> subStages = null) => completedSubStages = subStages ?? new List<object>();

        public JsObject Serialize(bool batch = false)
        {
            var data = new JsObject(
                "key", key,
                "stage", stage,
                "subStage", subStage,
                "completedSubStages", completedSubStages);

            if (batch)
            {
                data.Set("name", name);
                data.Set("description", description);
                data.Set("rewards", rewards);
                data.Set("skillRequirements", skillRequirements);
                data.Set("questRequirements", questRequirements);
                data.Set("difficulty", difficulty);
                data.Set("stageCount", stageCount);
            }

            return data;
        }

        public void OnStart(Action<string> callback) => startCallback = callback;

        public void OnProgress(Action<string, double, double> callback) => progressCallback = callback;

        public void OnPointer(Action<JsObject> callback) => pointerCallback = callback;

        public void OnPopup(Action<JsObject> callback) => popupCallback = callback;

        public void OnTalk(Action<NPC, Player> callback) => talkCallback = callback;

        public void OnDoor(Action<JsObject, Player> callback) => doorCallback = callback;

        public void OnKill(Action<Mob> callback) => killCallback = callback;

        public void OnResource(Action<Modules.Skills, string> callback) => resourceCallback = callback;
    }

    /// <summary>quest/impl/tutorial.ts.</summary>
    public sealed class TutorialQuest : Quest
    {
        public TutorialQuest(string key, JsObject data) : base(key, data)
        {
            // protected override noPrompts = true (field initializer, after super()).
            noPrompts = true;

            if (!Config.Current.tutorialEnabled) SetStage(stageCount, 0, false);
        }

        public override void Loaded()
        {
            // Skip if tutorial is not enabled.
            if (!Config.Current.tutorialEnabled) return;

            SetStage(0, 0, false);
        }
    }

    /// <summary>quest/impl/anvilsechoes.ts.</summary>
    public sealed class AnvilsEchoesQuest : Quest
    {
        public AnvilsEchoesQuest(string key, JsObject data) : base(key, data) { }

        protected override void HandleDoor(JsObject door, Player player)
        {
            if (stage < JsAny.ToNumber(door["stage"]))
            {
                var npc = player.world.entities.GetNPCByKey(door.Str("npc"));

                npc?.Talk(player, new List<object> { "Hey, don't go in there, it's dangerous!" });

                return;
            }

            base.HandleDoor(door, player);
        }
    }

    /// <summary>quest/impl/evilsanta.ts.</summary>
    public sealed class EvilSantaQuest : Quest
    {
        public EvilSantaQuest(string key, JsObject data) : base(key, data) { }

        protected override void HandleDoor(JsObject door, Player player)
        {
            Log.Debug($"[{name}] Door: {JsString.Of(door["x"])}-{JsString.Of(door["y"])} - stage: {JsString.Of(stage)}.");

            if (stage == 0)
            {
                player.Notify("misc:WHY_GO_THERE");
                return;
            }

            // If the player is not on the correct stage, don't let them through.
            if (stage < JsAny.ToNumber(door["stage"]))
            {
                player.Notify("misc:DONT_THINK_GO_IN");
                return;
            }

            // Handle door requiring an item to proceed (and remove the item from the player's inventory).
            if (JsData.Truthy(door["reqItem"]))
            {
                var count = JsData.Truthy(door["reqItemCount"]) ? (double)door["reqItemCount"] : 1;

                if (!player.inventory.HasItem(door.Str("reqItem"), count))
                {
                    player.Notify("misc:NO_KEY_DOOR");
                    return;
                }

                player.inventory.RemoveItem(door.Str("reqItem"), count);

                player.Notify("misc:DOOR_KEY_CRUMBLES");
            }

            // Let the super class handle the rest.
            base.HandleDoor(door, player);
        }
    }
}
