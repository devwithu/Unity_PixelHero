using System.Collections.Generic;

namespace PixelHero.Entities
{
    /// <summary>client/src/entity/character/player/task.ts: a quest or an achievement of the player.</summary>
    public sealed class Task
    {
        public int Id;
        public string Name;
        public string Description;
        public double? Stage;
        public double? StageCount;
        public double? SubStage;
        public List<string> Rewards;
        /// <summary>Skill key → level (an object keyed by skill name in the packet).</summary>
        public OrderedMap<object> SkillRequirements = new OrderedMap<object>();
        public List<string> QuestRequirements = new List<string>();
        public bool Secret;
        public string Region = "";

        public Task(int id, string name, string description, double? stage = 1, double? stageCount = 1, double? subStage = null)
        {
            Id = id;
            Name = name;
            Description = description;
            Stage = stage;
            StageCount = stageCount;
            SubStage = subStage;
        }

        /// <summary>task.ts update: a truthy sub-stage, name or description replaces the current one.</summary>
        public void Update(double? stage, double? subStage = null, string name = null, string description = null)
        {
            Stage = stage;
            if (subStage is double s && s != 0 && !double.IsNaN(s)) SubStage = subStage;
            if (!string.IsNullOrEmpty(name)) Name = name;
            if (!string.IsNullOrEmpty(description)) Description = description;
        }

        /// <summary>stage &gt;= stageCount (false when either is undefined).</summary>
        public bool IsFinished() => Stage >= StageCount;

        public bool IsStarted() => Stage > 0;
    }
}
