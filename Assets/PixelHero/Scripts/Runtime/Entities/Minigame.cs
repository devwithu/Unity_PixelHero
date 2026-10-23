using System.Collections.Generic;
using PixelHero.Common;

namespace PixelHero.Entities
{
    /// <summary>renderer/minigame.ts: the minigame the player is in (TeamWar or Coursing), drawn by drawMinigameGUI.</summary>
    public sealed class Minigame
    {
        public int Type = -1;
        /// <summary>'exit', 'lobby' or 'ingame'.</summary>
        public string Status = "exit";
        public double Countdown = 180;
        public double Score;
        public double RedTeamScore, BlueTeamScore;
        public bool Started;

        public void Reset()
        {
            Type = -1;
            Status = "exit";
            Started = false;
        }

        public void SetStatus(string status) => Status = status;

        /// <summary>A zero (or missing) score shows as -1 (<c>info.score || -1</c>).</summary>
        public void SetScore(Dictionary<string, object> info)
        {
            double Or(string key) => info.TryGetValue(key, out var value) && value is double d && d != 0 && !double.IsNaN(d) ? d : -1;

            switch ((Opcodes.Minigame)Type)
            {
                case Opcodes.Minigame.TeamWar:
                    RedTeamScore = Or("redTeamKills");
                    BlueTeamScore = Or("blueTeamKills");
                    return;
                case Opcodes.Minigame.Coursing:
                    Score = Or("score");
                    return;
            }
        }

        public bool Exists() => Type > -1;
    }

    /// <summary>entity/character/player/ability.ts.</summary>
    public sealed class Ability
    {
        /// <summary>Undefined (null) when an Update packet creates the ability.</summary>
        public readonly double? Type;
        public readonly string Key;
        public double Level;
        public double QuickSlot;
        public bool Active;

        public Ability(double? type, string key, double level, double quickSlot = -1)
        {
            Type = type;
            Key = key;
            Level = level;
            QuickSlot = quickSlot;
        }

        public void Update(double level, double? quickSlot = null)
        {
            Level = level;
            QuickSlot = quickSlot ?? QuickSlot;
        }

        public void Toggle() => Active = !Active;
    }
}
