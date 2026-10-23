using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>player/achievement/achievement.ts.</summary>
    public sealed class Achievement
    {
        public string name = "";
        readonly string description = "";
        readonly string region = "";
        readonly bool hidden;
        public bool secret;

        double stage; // Current stage of the achievement.
        readonly double stageCount; // How long the achievement is.

        readonly string npc = "";
        readonly List<object> dialogueHidden = new(); // Before achievement is started.
        readonly List<object> dialogueStarted = new(); // After achievement is started.
        readonly object mob = ""; // string | string[]
        readonly string item = ""; // Item required for completion
        readonly double itemCount = 1; // How much of the item for completion.
        readonly string rewardItem = "";
        readonly double rewardItemCount = 1;
        readonly Modules.Skills? rewardSkill;
        readonly double rewardExperience;
        readonly string rewardAbility = "";
        readonly double rewardAbilityLevel = 1;

        /// <summary>(skill, experience, item, itemCount, ability, abilityLevel).</summary>
        Action<Modules.Skills?, double, string, double, string, double> finishCallback;
        Action<string, double, string, string> progressCallback;
        Action<JsObject> popupCallback;

        public Action<NPC, Player> talkCallback;
        public Action<Mob> killCallback;

        readonly string key;

        static object Or(object a, object b) => JsData.Truthy(a) ? a : b;

        public Achievement(string key, JsObject rawData)
        {
            this.key = key;

            // Load all the data from the raw information.
            name = rawData.Str("name");
            description = Or(rawData["description"], "") as string;
            region = Or(rawData["region"], "") as string;
            hidden = JsData.Truthy(rawData["hidden"]);
            secret = JsData.Truthy(rawData["secret"]);
            npc = Or(rawData["npc"], "") as string;
            dialogueHidden = Or(rawData["dialogueHidden"], new List<object>()) as List<object>;
            dialogueStarted = Or(rawData["dialogueStarted"], new List<object>()) as List<object>;
            mob = Or(rawData["mob"], "");

            // Stage count is how many mobs we must kill or a single stage (e.g. discover door).
            stageCount = JsData.Truthy(rawData["mobCount"]) ? (double)rawData["mobCount"] + 1 : 1; // Increment by 1 to include discovery stage.

            item = Or(rawData["item"], "") as string;
            itemCount = (double)Or(rawData["itemCount"], 1.0);
            rewardItem = Or(rawData["rewardItem"], "") as string;
            rewardItemCount = (double)Or(rawData["rewardItemCount"], 1.0);
            rewardSkill = Utils.GetSkill(rawData.Str("rewardSkill"));
            rewardExperience = (double)Or(rawData["rewardExperience"], 0.0);
            rewardAbility = Or(rawData["rewardAbility"], "") as string;
            rewardAbilityLevel = (double)Or(rawData["rewardAbilityLevel"], 1.0);

            // Callbacks for the achievement.
            OnTalk(HandleTalk);
            OnKill(HandleKill);
        }

        void HandleTalk(NPC npc, Player player)
        {
            Log.Debug($"[{name}] Player {player.username} talked to {npc.key}");

            // This theoretically shouldn't happen but it can't hurt to be careful.
            if (npc.key != this.npc) return;

            var dialogue = IsStarted() ? dialogueStarted : dialogueHidden;

            // End of dialogue, check if the achievement requires an item.
            if (dialogue.Count == player.talkIndex)
            {
                if (HasItemRequirement()) HandleItemRequirement(player);
                else Progress();
            }

            npc.Talk(player, dialogue);
        }

        void HandleKill(Mob mob)
        {
            Log.Debug($"[{name}] Player killed {mob.key}");

            // Increment by one for each mob that is killed.
            Progress();

            Log.Debug($"Stage: {JsString.Of(stage)}");
        }

        void HandleItemRequirement(Player player)
        {
            var index = player.inventory.GetIndex(item, itemCount);

            // No item found.
            if (index == -1) return;

            player.inventory.Remove(index, itemCount);

            // Progress after removing the item.
            Progress();
        }

        public void Progress() => SetStage(stage + 1);

        public void Finish()
        {
            if (IsFinished()) return;

            SetStage(stageCount);
        }

        public void SetStage(double stage, bool loading = false)
        {
            // Is the new stage different from current stage?
            var isProgress = this.stage != stage;

            this.stage = stage;

            // Don't send callbacks when loading from database.
            if (loading) return;

            // Handle quest progress callback after updating stage to grab latest name.
            if (isProgress) progressCallback?.Invoke(key, stage, GetName(), GetDescription());

            if (this.stage >= stageCount)
            {
                // Achievement is finished!
                popupCallback?.Invoke(GetFinishPopup());

                finishCallback?.Invoke(rewardSkill, rewardExperience, rewardItem, rewardItemCount, rewardAbility,
                    rewardAbilityLevel);
            }
            else if (this.stage == 1) popupCallback?.Invoke(GetDiscoveredPopup());
        }

        public bool IsStarted() => stage > 0;

        public bool IsFinished() => stage >= stageCount;

        public bool HasNPC(NPC npc) => npc.key == this.npc;

        public bool HasMob(Mob mob) =>
            mob is not null && (this.mob is List<object> list ? list.Any(m => m is string s && s == mob.key) : this.mob is string s2 && mob.key == s2);

        bool HasItemRequirement() => item != "";

        string GetName() => hidden && !IsStarted() ? "????????" : name;

        string GetDescription() => hidden && !IsStarted() ? "????????" : description;

        JsObject GetDiscoveredPopup() => new(
            "title", "Achievement Discovered",
            "text", $"{name} has been discovered!",
            "colour", "#33cc33");

        JsObject GetFinishPopup()
        {
            var text = $"@green@You have completed the achievement @crimson@{GetName()}@green@!";

            if (rewardExperience > 0)
                text = $"@green@ You have received @crimson@{JsString.Of(rewardExperience)} experience@green@.";
            else if (JsData.Truthy(rewardAbility))
                text = rewardAbilityLevel == 1
                    ? $"@green@ You have unlocked the @crimson@{rewardAbility}@green@ ability."
                    : $"@green@ Your @crimson@{rewardAbility} ability is now level {JsString.Of(rewardAbilityLevel)}@green@.";
            else if (secret)
                text = $"@green@You have completed the secret achievement @crimson@{GetName()}@green@!";

            return new JsObject(
                "title", "Achievement Completed!",
                "text", text,
                "colour", "#33cc33",
                "soundEffect", "achievement");
        }

        public JsObject Serialize(bool withInfo = false)
        {
            var data = new JsObject("key", key, "stage", stage);

            if (withInfo)
            {
                data.Set("name", GetName());
                data.Set("description", GetDescription());
                data.Set("region", region);
                data.Set("stageCount", stageCount);

                // Only send secret achievement information if the achievement is secret.
                if (secret) data.Set("secret", secret);
            }

            return data;
        }

        public void OnFinish(Action<Modules.Skills?, double, string, double, string, double> callback) => finishCallback = callback;

        public void OnProgress(Action<string, double, string, string> callback) => progressCallback = callback;

        public void OnPopup(Action<JsObject> callback) => popupCallback = callback;

        void OnTalk(Action<NPC, Player> callback) => talkCallback = callback;

        void OnKill(Action<Mob> callback) => killCallback = callback;
    }

    /// <summary>player/achievements.ts.</summary>
    public sealed class Achievements
    {
        readonly JsObject<Achievement> achievements = new();

        Action loadCallback;

        readonly Player player;

        public Achievements(Player player)
        {
            this.player = player;

            // Iterates through the raw achievement information from the JSON.
            foreach (var pair in ServerData.Achievements)
            {
                var achievement = new Achievement(pair.Key, (JsObject)pair.Value);

                achievements.Set(pair.Key, achievement);

                achievement.OnFinish(HandleFinish);
                achievement.OnProgress(HandleProgress);
                achievement.OnPopup(HandlePopup);
            }
        }

        public void Load(List<object> achievementInfo)
        {
            // Iterates through the achievement information and updates data.
            foreach (var entry in achievementInfo)
            {
                var info = entry as JsObject;
                var achievement = Get(info?.Str("key"));

                // Update stage if achievement exists.
                achievement?.SetStage(info.Num("stage") ?? double.NaN, true);
            }

            loadCallback?.Invoke();
        }

        void HandleFinish(Modules.Skills? skill, double experience, string itemKey, double itemCount, string ability,
            double abilityLevel)
        {
            // Handles an item reward.
            if (JsData.Truthy(itemKey))
            {
                var item = new Item(itemKey, -1, -1, true, itemCount);

                // Check if we can add to the inventory, then the bank, and if both fail just drop the item.
                if (player.inventory.Add(item) < 1 && player.bank.Add(item) < 1)
                    player.world.entities.SpawnItem(itemKey, player.x, player.y, true, itemCount);
            }

            // Add experience if it exists.
            if (skill != null && (double)skill.Value > -1 && experience != 0 && !double.IsNaN(experience))
                player.skills.Get(skill.Value)?.AddExperience(experience);

            // Add ability if it exists.
            if (JsData.Truthy(ability)) player.abilities.Add(ability, abilityLevel);

            // Update dynamic tiles.
            player.UpdateRegion();

            player.Save();
        }

        void HandleProgress(string key, double stage, string name, string description)
        {
            player.Send(new AchievementPacket(Opcodes.Achievement.Progress, new JsObject(
                "key", key,
                "stage", stage,
                "name", name,
                "description", description)));

            player.UpdateEntities();

            player.Save();
        }

        void HandlePopup(JsObject popup) =>
            player.Popup(popup.Str("title"), popup.Str("text"), popup.Str("colour") ?? "#00000", popup.Str("soundEffect") ?? "");

        public Achievement Get(string key) => key == null ? null : achievements[key];

        public Achievement GetAchievementFromEntity(Entity entity)
        {
            Achievement achievement = null;

            ForEachAchievement(a =>
            {
                if (a.IsFinished()) return;

                if (entity.IsNPC() && !a.HasNPC((NPC)entity)) return;

                if (entity.IsMob() && (!a.HasMob((Mob)entity) || !a.IsStarted())) return;

                achievement = a;
            });

            return achievement;
        }

        public void ForEachAchievement(Action<Achievement> callback)
        {
            foreach (var achievement in achievements.Values) callback(achievement);
        }

        public JsObject Serialize(bool withInfo = false)
        {
            var list = new List<object>();

            ForEachAchievement(achievement =>
            {
                // Skip secret achievements that are not finished.
                if (achievement.secret && !achievement.IsFinished()) return;

                list.Add(achievement.Serialize(withInfo));
            });

            return new JsObject("achievements", list);
        }

        public void OnLoaded(Action callback) => loadCallback = callback;
    }
}
