using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>player/quests.ts.</summary>
    public sealed class Quests
    {
        /// <summary>quest/impl/index.ts, in its order.</summary>
        static readonly string[] Index =
        {
            "tutorial", "foresting", "desertquest", "seaactivities", "ancientlands", "sorcery", "minersquest",
            "evilsanta", "artsandcrafts", "scavenger", "minersquest2", "royaldrama", "clamchowder", "royalpet",
            "scientistspotion", "anvilsechoes", "herbalistdesperation", "ricksroll", "codersglitch", "codersglitch2",
            "codersfallacy"
        };

        static Quest Create(string key)
        {
            var data = ServerData.Quest(key);
            return key switch
            {
                "tutorial" => new TutorialQuest(key, data),
                "anvilsechoes" => new AnvilsEchoesQuest(key, data),
                "evilsanta" => new EvilSantaQuest(key, data),
                _ => new Quest(key, data)
            };
        }

        // All the quests are contained here.
        readonly JsObject<Quest> quests = new();

        Action loadCallback;

        readonly Player player;

        public Quests(Player player)
        {
            this.player = player;

            // Iterates through the quest indices and initializes them.
            foreach (var key in Index)
            {
                var quest = Create(key);

                quests.Set(key, quest);

                quest.OnProgress(HandleProgress);
                quest.OnPointer(HandlePointer);
                quest.OnPopup(HandlePopup);
                quest.OnStart(HandleInterface);
            }
        }

        public void Load(List<object> questInfo)
        {
            foreach (var entry in questInfo)
            {
                var info = entry as JsObject;
                var quest = Get(info?.Str("key"));

                if (quest != null)
                {
                    quest.SetStage(info.Num("stage") ?? double.NaN, info.Num("subStage") ?? 0, false);
                    quest.SetCompletedSubStages(info.Arr("completedSubStages"));
                }
            }

            // Trigger `loaded()` when we have no database information.
            if (questInfo.Count == 0) ForEachQuest(quest => quest.Loaded());

            loadCallback?.Invoke();
        }

        void HandleProgress(string key, double stage, double subStage)
        {
            player.Send(new QuestPacket(Opcodes.Quest.Progress, new JsObject(
                "key", key,
                "stage", stage,
                "subStage", subStage)));

            // Update region when quest is completed.
            if (Get(key).IsFinished()) player.UpdateRegion();

            // Stop skills when quest progress is made.
            player.skills.Stop();

            player.UpdateEntities();

            player.Save();
        }

        void HandlePointer(JsObject pointer) => player.Pointer(pointer);

        void HandlePopup(JsObject popup) => player.Popup(popup.Str("title"), popup.Str("text"), popup.Str("colour") ?? "#00000");

        void HandleInterface(string key) => player.Send(new QuestPacket(Opcodes.Quest.Start, new JsObject("key", key)));

        public Quest Get(string key) => key == null ? null : quests[key];

        public Quest GetQuestFromNPC(NPC npc, bool includeComplete = false)
        {
            Quest quest = null;

            ForEachQuest(q =>
            {
                if (q.IsFinished() && !includeComplete) return;

                if (!q.HasNPC(npc.key)) return;

                if (!q.HasRequirements(player)) return;

                // Return only the first quest found,
                quest ??= q;
            });

            return quest;
        }

        public Quest GetQuestFromMob(Mob mob)
        {
            Quest quest = null;

            ForEachQuest(q =>
            {
                // Skip if the quest isn't started or if the quest is finished.
                if (q.IsFinished() || !q.IsStarted()) return;

                var stage = q.GetStageData();

                // Skip if the stage is not to kill a mob.
                if (stage.Str("task") != "kill") return;

                var stageMobs = stage["mob"];

                // Skip if we don't have a mob for the given stage.
                if (!JsData.Truthy(stageMobs) || JsAny.ToNumber(JsAny.Get(stageMobs, "length")) == 0) return;

                // Check if the mob matches the mobs and check the array if it's an array for mob key.
                var includes = stageMobs switch
                {
                    string s => s.Contains(mob.key, StringComparison.Ordinal),
                    List<object> list => list.Any(item => item is string s && s == mob.key),
                    _ => false
                };

                if (includes) quest = q;
            });

            return quest;
        }

        public bool IsTutorialFinished()
        {
            // Uses the default key from the constants.
            var quest = quests[Constants.TUTORIAL_QUEST_KEY];

            // Default to true if the quest is not found.
            if (quest == null) return true;

            // If the quest exists we return its status.
            return quest.IsFinished();
        }

        public bool CanAttackInTutorial()
        {
            if (IsTutorialFinished()) return true;

            return Get(Constants.TUTORIAL_QUEST_KEY)?.IsKillTask() ?? false;
        }

        public bool CanCutTreesInTutorial()
        {
            if (IsTutorialFinished()) return true;

            return Get(Constants.TUTORIAL_QUEST_KEY)?.IsCutTreeTask() ?? false;
        }

        public bool CanFishInTutorial()
        {
            if (IsTutorialFinished()) return true;

            return Get(Constants.TUTORIAL_QUEST_KEY)?.IsFishingTask() ?? false;
        }

        public JsObject Serialize(bool batch = false)
        {
            var list = new List<object>();

            ForEachQuest(quest => list.Add(quest.Serialize(batch)));

            return new JsObject("quests", list);
        }

        public void ForEachQuest(Action<Quest> callback)
        {
            foreach (var pair in quests) callback(pair.Value);
        }

        public void OnLoaded(Action callback) => loadCallback = callback;
    }
}
