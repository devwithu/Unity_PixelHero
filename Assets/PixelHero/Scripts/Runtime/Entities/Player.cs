using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;

namespace PixelHero.Entities
{
    /// <summary>
    /// One equipment slot of a player (client entity/character/player/equipment.ts). Stats, bonuses and
    /// enchantments are kept as the packet's objects (null: the original's empty stats).
    /// </summary>
    public sealed class Equipment
    {
        public string Key = "";
        public string Name = "";
        public double? Count = -1;
        public bool Bow;
        public bool Drawable;
        public object Light;
        public Dictionary<string, object> Enchantments = new Dictionary<string, object>();
        public Dictionary<string, object> AttackStats, DefenseStats, Bonuses;
        public int AttackStyle = (int)Modules.AttackStyle.None;
        public List<int> AttackStyles = new List<int>();

        public void Update(string key = "", string name = "", double? count = -1, Dictionary<string, object> enchantments = null,
            Dictionary<string, object> attackStats = null, Dictionary<string, object> defenseStats = null, Dictionary<string, object> bonuses = null)
        {
            Key = key ?? "";
            Name = name ?? "";
            Count = count ?? -1;
            Enchantments = enchantments ?? new Dictionary<string, object>();
            AttackStats = attackStats;
            DefenseStats = defenseStats;
            Bonuses = bonuses;
            AttackStyle = (int)Modules.AttackStyle.None;
            AttackStyles = new List<int>();
            Light = null;
        }

        public bool Exists() => Key != "";
    }

    /// <summary>
    /// client/src/entity/character/player/player.ts, the parts that decide state and appearance: load, equip /
    /// unequip, which equipment is drawn (skins hide armour and weapons outside PvP), the base sprite, idle rules and
    /// the equipment draw order (cape goes on top when facing up).
    /// </summary>
    public sealed class Player : Character
    {
        public int ServerId = -1;
        public bool MoveLeft, MoveRight, MoveUp, MoveDown;
        public bool DisableAction;
        public bool HasPet;
        public bool Poison;
        public double? Rank = (int)Modules.Ranks.None;
        public readonly Dictionary<int, Equipment> Equipments = new Dictionary<int, Equipment>();
        public Action SyncCallback;
        public Action<double?, double?> ManaCallback;

        Modules.Equipment[] _equipmentOrder = DefaultOrder;

        static readonly Modules.Equipment[] DefaultOrder =
        {
            Modules.Equipment.Cape, Modules.Equipment.Legplates, Modules.Equipment.Chestplate, Modules.Equipment.Helmet,
            Modules.Equipment.ArmourSkin, Modules.Equipment.Shield, Modules.Equipment.Weapon, Modules.Equipment.WeaponSkin
        };

        static readonly Modules.Equipment[] UpOrder =
        {
            Modules.Equipment.Legplates, Modules.Equipment.Chestplate, Modules.Equipment.Helmet, Modules.Equipment.ArmourSkin,
            Modules.Equipment.Shield, Modules.Equipment.Weapon, Modules.Equipment.WeaponSkin, Modules.Equipment.Cape
        };

        protected override double AttackAnimationSpeed => 120;
        protected override double WalkAnimationSpeed => 160;

        public Player(World world, string instance) : base(world, instance, Modules.EntityType.Player)
        {
            foreach (Modules.Equipment type in Enum.GetValues(typeof(Modules.Equipment))) Equipments[(int)type] = new Equipment();
            Equipments[(int)Modules.Equipment.Weapon].Drawable = true;
            Equipments[(int)Modules.Equipment.Shield].Drawable = true;
            Equipments[(int)Modules.Equipment.Cape].Drawable = true;
        }

        public IReadOnlyList<Modules.Equipment> EquipmentOrder => _equipmentOrder;

        /// <summary>player.ts load (sync = a Sync packet for another player: the position is not touched).</summary>
        public void Load(Dictionary<string, object> data, bool sync = false)
        {
            Instance = (string)data["instance"];
            Name = data.TryGetValue("name", out var name) ? name as string : null;
            Level = Num(data, "level");
            MovementSpeed = Num(data, "movementSpeed");
            Orientation = (int)(Num(data, "orientation") ?? Orientation);
            AttackRange = Num(data, "attackRange");

            if (data.TryGetValue("displayInfo", out var displayInfo) && displayInfo is Dictionary<string, object> info)
                NameColour = info.TryGetValue("colour", out var colour) ? colour as string : null;

            SetRank(Num(data, "rank"));
            if (Num(data, "orientation") is double orientation) SetOrientation((int)orientation);

            if (!sync) SetGridPosition((int)(double)data["x"], (int)(double)data["y"]);

            SetHitPoints(Num(data, "hitPoints"), Num(data, "maxHitPoints"));
            SetMana(Num(data, "mana"), Num(data, "maxMana"));

            if (data.TryGetValue("equipments", out var equipments) && equipments is List<object> list)
                foreach (Dictionary<string, object> equipment in list) Equip(equipment);
        }

        public Modules.Crowns Crown = Modules.Crowns.None;

        /// <summary>player.ts setRank: the rank and its crown (undefined rank: no crown, but hasRank is true).</summary>
        public void SetRank(double? rank)
        {
            Rank = rank;
            Crown = GetRankCrown();
        }

        public bool HasRank() => Rank != (int)Modules.Ranks.None;

        public bool HasCrown() => Crown != Modules.Crowns.None;

        Modules.Crowns GetRankCrown()
        {
            if (!(Rank is double rank)) return Modules.Crowns.None;
            if (rank == (int)Modules.Ranks.Artist) return Modules.Crowns.Artist;
            if (rank >= (int)Modules.Ranks.TierOne && rank <= (int)Modules.Ranks.TierSeven && rank == Math.Floor(rank))
                return Modules.Crowns.Tier1 + (int)(rank - (int)Modules.Ranks.TierOne);
            return Modules.Crowns.None;
        }

        public string GetCrownKey()
        {
            switch (Crown)
            {
                case Modules.Crowns.Silver: return "silvermedal";
                case Modules.Crowns.Gold: return "goldmedal";
                case Modules.Crowns.Artist: return "crown-artist";
                case Modules.Crowns.None: return "";
                default: return "crown-tier" + ((int)Crown - (int)Modules.Crowns.Tier1 + 1);
            }
        }

        static double? Num(Dictionary<string, object> data, string key) =>
            data.TryGetValue(key, out var value) && value is double number ? number : (double?)null;

        public void Equip(Dictionary<string, object> equipment)
        {
            var type = (int)(double)equipment["type"];
            var key = equipment.TryGetValue("key", out var k) ? k as string : null;

            if (string.IsNullOrEmpty(key))
            {
                Unequip(type);
                return;
            }

            var prefix = type == (int)Modules.Equipment.WeaponSkin ? "weapon" : GetType((Modules.Equipment)type);
            Equipments[type].Update(
                prefix != "" ? $"player/{prefix}/{key}" : $"items/{key}",
                equipment.TryGetValue("name", out var name) ? name as string : "",
                Num(equipment, "count"),
                Obj(equipment, "enchantments"), Obj(equipment, "attackStats"), Obj(equipment, "defenseStats"), Obj(equipment, "bonuses"));

            if (type == (int)Modules.Equipment.Weapon)
            {
                var range = Num(equipment, "attackRange");
                AttackRange = range.HasValue && range.Value != 0 ? range : 1;
                SetAttackStyle(Num(equipment, "attackStyle"), equipment.TryGetValue("attackStyles", out var styles) ? styles as List<object> : null);
                Equipments[type].Bow = equipment.TryGetValue("bow", out var bow) && bow is bool b && b;
            }

            if (equipment.TryGetValue("light", out var light) && light != null) Equipments[type].Light = light;

            UpdateEquipmentAppearance();
        }

        /// <summary>player.ts abilities (keyed by ability key, JS object order).</summary>
        public readonly OrderedMap<Ability> Abilities = new OrderedMap<Ability>();

        /// <summary>player.ts onAbility (the original's quick slot bar never subscribes).</summary>
        public Action<string, double, double?> AbilityCallback;

        /// <summary>player.ts setAbility: updates an ability or creates it.</summary>
        public void SetAbility(string key, double? level, double? type = null, double? quickSlot = null)
        {
            var name = key ?? "undefined";
            var existing = Abilities[name];
            if (existing != null) existing.Update(level ?? double.NaN, quickSlot);
            else Abilities[name] = new Ability(type, key, level ?? double.NaN, quickSlot ?? -1);

            // undefined !== -1 is true.
            if (type == (int)Modules.AbilityType.Active || quickSlot != -1) AbilityCallback?.Invoke(key, level ?? double.NaN, quickSlot);
        }

        public void ToggleAbility(string key) => Abilities[key ?? "undefined"]?.Toggle();

        /// <summary>player.ts getLight: the shield's light object.</summary>
        public object GetLight() => Equipments[(int)Modules.Equipment.Shield].Light;

        /// <summary>player.ts hasLight: an outer or inner light source.</summary>
        public bool HasLight() =>
            GetLight() is Dictionary<string, object> light &&
            (light.TryGetValue("outer", out var outer) && outer != null || light.TryGetValue("inner", out var inner) && inner != null);

        public void Unequip(int type, double count = -1)
        {
            if (count > 0) Equipments[type].Count = count;
            else Equipments[type].Update();

            UpdateEquipmentAppearance();
        }

        public void Sync() => SyncCallback?.Invoke();

        static Dictionary<string, object> Obj(Dictionary<string, object> data, string key) =>
            data.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

        /// <summary>player.ts setAttackStyle: the weapon's style, and its list when given.</summary>
        public void SetAttackStyle(double? style, List<object> styles = null)
        {
            GetWeapon().AttackStyle = style is double s ? (int)s : (int)Modules.AttackStyle.None;
            if (styles != null) GetWeapon().AttackStyles = styles.Select(v => (int)(double)v).ToList();
        }

        /// <summary>A skill's state (player/skill.ts), by Modules.Skills.</summary>
        public sealed class SkillState
        {
            public double? Experience, NextExperience, Level, Percentage;
        }

        public readonly Dictionary<int, SkillState> Skills = new Dictionary<int, SkillState>();

        /// <summary>player.ts setSkill.</summary>
        public void SetSkill(Dictionary<string, object> data)
        {
            var type = (int)(double)data["type"];
            if (!Skills.TryGetValue(type, out var skill)) Skills[type] = skill = new SkillState();
            skill.Experience = Num(data, "experience");
            skill.NextExperience = Num(data, "nextExperience");
            skill.Level = Num(data, "level");
            skill.Percentage = Num(data, "percentage");
        }

        public readonly OrderedMap<Task> Quests = new OrderedMap<Task>();

        /// <summary>player/friend.ts.</summary>
        public sealed class Friend
        {
            public int Id;
            public string Username;
            public bool Online;
            public double? ServerId;
        }

        public readonly OrderedMap<Friend> Friends = new OrderedMap<Friend>();

        /// <summary>A guild member (common/types/guild.ts Member).</summary>
        public sealed class GuildMember
        {
            public string Username;
            public double? Rank;
            public double? ServerId;
        }

        /// <summary>player.guild: the name and the members (undefined when not in a guild).</summary>
        public sealed class GuildInfo
        {
            public string Name;
            public List<GuildMember> Members = new List<GuildMember>();
        }

        public GuildInfo Guild;

        public static List<GuildMember> ParseMembers(object members) =>
            (members as List<object> ?? new List<object>()).OfType<Dictionary<string, object>>().Select(member => new GuildMember
            {
                Username = member.TryGetValue("username", out var name) ? name as string : null,
                Rank = Num(member, "rank"),
                ServerId = Num(member, "serverId")
            }).ToList();

        /// <summary>player.ts setGuild: from the Login packet, or none.</summary>
        public void SetGuild(Dictionary<string, object> packet = null)
        {
            if (packet == null)
            {
                Guild = null;
                return;
            }

            Guild = new GuildInfo
            {
                Name = packet.TryGetValue("name", out var name) ? name as string : null,
                Members = ParseMembers(packet.TryGetValue("members", out var members) ? members : null)
            };
        }

        /// <summary>player.ts hasMember.</summary>
        public bool HasMember(string username) => Guild != null && Guild.Members.Any(member => member.Username == username);

        /// <summary>
        /// player.ts addGuildMember: adds a member that is not in the list yet.
        /// </summary>
        public void AddGuildMember(string username, double? serverId)
        {
            // Port fix (ANALYSIS.md §9 #9; the fixtures patch the original the same way): the original's condition was
            // inverted and only re-added members already in the list.
            if (HasMember(username)) return;
            Guild.Members.Add(new GuildMember { Username = username, ServerId = serverId });
        }

        /// <summary>player.ts removeGuildMember.</summary>
        public void RemoveGuildMember(string username)
        {
            if (!HasMember(username)) return;
            Guild.Members.RemoveAt(Guild.Members.FindIndex(member => member.Username == username));
        }

        /// <summary>player.ts getGuildMember.</summary>
        public GuildMember GetGuildMember(string username) => Guild?.Members.FirstOrDefault(member => member.Username == username);

        /// <summary>player.ts setGuildMember: a member's new rank.</summary>
        public void SetGuildMember(string username, double? rank)
        {
            var member = GetGuildMember(username);
            if (member != null) member.Rank = rank ?? (int)Modules.GuildRank.Fledgling;
        }

        /// <summary>player.ts loadFriends: the list is an object of username → {online, serverId}.</summary>
        public void LoadFriends(Dictionary<string, object> friends)
        {
            var i = 0;
            foreach (var pair in friends)
            {
                var info = pair.Value as Dictionary<string, object> ?? new Dictionary<string, object>();
                Friends[pair.Key] = new Friend
                {
                    Id = i++, Username = pair.Key, Online = info.TryGetValue("online", out var online) && online is bool o && o,
                    ServerId = Num(info, "serverId")
                };
            }
        }

        /// <summary>player.ts addFriend.</summary>
        public void AddFriend(string username, bool status, double? serverId) =>
            Friends[username ?? "undefined"] = new Friend { Id = Friends.Count, Username = username, Online = status, ServerId = serverId };

        /// <summary>player.ts setFriendStatus (throws like the original when the friend is unknown).</summary>
        public void SetFriendStatus(string username, bool status, double? serverId)
        {
            var friend = Friends[username ?? "undefined"] ?? throw new InvalidOperationException("TypeError: Cannot set properties of undefined (setting 'online')");
            friend.Online = status;
            friend.ServerId = serverId;
        }

        /// <summary>player.ts hasFriend: by the lower-case name.</summary>
        public bool HasFriend(string username) => Friends.ContainsKey((username ?? "undefined").ToLowerInvariant());
        public readonly OrderedMap<Task> Achievements = new OrderedMap<Task>();

        static string Text(Dictionary<string, object> data, string key) =>
            data.TryGetValue(key, out var value) ? value as string : null;

        /// <summary>player.ts loadQuests: one task per quest, its id the index in the batch.</summary>
        public void LoadQuests(List<object> quests)
        {
            for (var i = 0; i < quests.Count; i++)
            {
                var quest = (Dictionary<string, object>)quests[i];
                var task = new Task(i, Text(quest, "name"), Text(quest, "description"), Num(quest, "stage"), Num(quest, "stageCount"),
                    Num(quest, "subStage"));
                if (quest.TryGetValue("rewards", out var rewards) && rewards is List<object> list)
                    task.Rewards = list.Select(r => r as string).ToList();
                if (quest.TryGetValue("skillRequirements", out var skills) && skills is Dictionary<string, object> map)
                    foreach (var pair in map) task.SkillRequirements[pair.Key] = pair.Value;
                if (quest.TryGetValue("questRequirements", out var required) && required is List<object> keys)
                    task.QuestRequirements = keys.Select(k => k as string).ToList();
                Quests[(string)quest["key"]] = task;
            }
        }

        /// <summary>player.ts setQuest.</summary>
        public void SetQuest(string key, double? stage, double? subStage) => Quests[key ?? ""]?.Update(stage, subStage);

        /// <summary>player.ts loadAchievements: secret and region kept, id the index in the batch.</summary>
        public void LoadAchievements(List<object> achievements)
        {
            for (var i = 0; i < achievements.Count; i++)
            {
                var achievement = (Dictionary<string, object>)achievements[i];
                var task = new Task(i, Text(achievement, "name"), Text(achievement, "description"), Num(achievement, "stage"),
                    Num(achievement, "stageCount"));
                if (achievement.TryGetValue("secret", out var secret) && secret is bool s && s) task.Secret = true;
                if (!string.IsNullOrEmpty(Text(achievement, "region"))) task.Region = Text(achievement, "region");
                Achievements[(string)achievement["key"]] = task;
            }
        }

        /// <summary>player.ts setAchievement: an unknown key is a discovered secret achievement.</summary>
        public void SetAchievement(string key, double? stage, string name, string description)
        {
            key ??= "undefined";
            if (!Achievements.ContainsKey(key))
            {
                // new Task(Object.keys(achievements).length - 1, name, description): stage and stageCount default to 1.
                Achievements[key] = new Task(Achievements.Count - 1, name, description) { Secret = true };
                return;
            }

            Achievements[key].Update(stage, null, name, description);
        }

        /// <summary>player.ts getTotalExperience: the sum of the skills' experience (NaN when one is missing).</summary>
        public double GetTotalExperience()
        {
            var total = 0.0;
            foreach (var skill in Skills.Values) total += skill.Experience ?? double.NaN;
            return total;
        }

        public void UpdateEquipmentAppearance()
        {
            GetArmourSkin().Drawable = !World.Pvp;
            GetWeaponSkin().Drawable = !World.Pvp;

            var weaponHidden = GetWeaponSkin().Key != "" && GetWeaponSkin().Drawable;
            var armourHidden = GetArmourSkin().Key != "" && GetArmourSkin().Drawable;

            GetWeapon().Drawable = !weaponHidden;

            Equipments[(int)Modules.Equipment.Helmet].Drawable = !armourHidden;
            Equipments[(int)Modules.Equipment.Chestplate].Drawable = !armourHidden;
            Equipments[(int)Modules.Equipment.Legplates].Drawable = !armourHidden;
        }

        public override void Idle(int? orientation, bool force)
        {
            if ((HasKeyboardMovement() || HasAttackers() || HasTarget()) && Moving) return;
            if (HasPath()) return;
            base.Idle(orientation, force);
        }

        public string GetSpriteName()
        {
            var armourSkin = GetArmourSkin();
            return armourSkin.Key != "" && armourSkin.Drawable ? armourSkin.Key : "player/base";
        }

        public Equipment GetWeapon() => Equipments[(int)Modules.Equipment.Weapon];
        public Equipment GetWeaponSkin() => Equipments[(int)Modules.Equipment.WeaponSkin];
        public Equipment GetArmourSkin() => Equipments[(int)Modules.Equipment.ArmourSkin];

        public void SetMana(double? mana, double? maxMana = null)
        {
            Mana = mana;
            if (maxMana.HasValue && maxMana.Value != 0 && !double.IsNaN(maxMana.Value)) MaxMana = maxMana;
            ManaCallback?.Invoke(Mana, maxMana ?? MaxMana);
        }

        public override void SetAnimation(string name, double? speed = null, int count = 0, Action onEndCount = null, bool withStop = false)
        {
            if (name == "atk" && GetWeapon().Bow) name = "bow_atk";
            base.SetAnimation(name, speed, count, onEndCount);
        }

        public override void SetOrientation(int orientation)
        {
            base.SetOrientation(orientation);
            _equipmentOrder = orientation == (int)Modules.Orientation.Up ? UpOrder : DefaultOrder;
        }

        public bool HasKeyboardMovement() => MoveLeft || MoveRight || MoveUp || MoveDown;

        /// <summary>player.ts forEachEquipment: drawable (and, if asked, non-empty) slots in draw order.</summary>
        public IEnumerable<(Equipment equipment, Modules.Equipment type)> DrawnEquipments(bool ignoreEmpty = true)
        {
            foreach (var type in _equipmentOrder)
            {
                var equipment = Equipments[(int)type];
                if (!equipment.Drawable) continue;
                if (ignoreEmpty && !equipment.Exists()) continue;
                yield return (equipment, type);
            }
        }

        static string GetType(Modules.Equipment type)
        {
            switch (type)
            {
                case Modules.Equipment.Helmet: return "helmet";
                case Modules.Equipment.Chestplate: return "chestplate";
                case Modules.Equipment.Legplates: return "legplates";
                case Modules.Equipment.Weapon: return "weapon";
                case Modules.Equipment.WeaponSkin: return "weaponskin";
                case Modules.Equipment.ArmourSkin: return "skin";
                case Modules.Equipment.Shield: return "shield";
                case Modules.Equipment.Cape: return "cape";
                default: return "";
            }
        }
    }
}
