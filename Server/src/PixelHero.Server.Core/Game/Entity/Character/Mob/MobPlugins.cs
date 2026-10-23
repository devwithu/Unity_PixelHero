using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>data/plugins/mobs/index.ts.</summary>
    public static class MobPlugins
    {
        static readonly Dictionary<string, Func<Mob, MobHandler>> Index = new()
        {
            ["ogrelord"] = mob => new OgreLordPlugin(mob),
            ["skeletonking"] = mob => new SkeletonKingPlugin(mob),
            ["queenant"] = mob => new QueenAntPlugin(mob),
            ["ant"] = mob => new AntPlugin(mob),
            ["forestdragon"] = mob => new ForestDragonPlugin(mob),
            ["santa"] = mob => new SantaPlugin(mob),
            ["piratecaptain"] = mob => new PirateCaptainPlugin(mob),
            ["spider"] = mob => new SpiderPlugin(mob),
            ["hellhound"] = mob => new HellhoundPlugin(mob)
        };

        /// <summary>`name in PluginIndex` (an object's inherited keys never name a plugin here).</summary>
        public static bool Has(string name) => name != null && Index.ContainsKey(name);

        public static MobHandler Create(string name, Mob mob) => Index[name](mob);
    }

    /// <summary>data/plugins/mobs/default.ts.</summary>
    public class DefaultMobPlugin : MobHandler
    {
        // Support for minion spawning. Since a lot of bosses may spawn minions.
        protected JsObject<Mob> minions = new();

        public DefaultMobPlugin(Mob mob) : base(mob) { }

        protected void AttackAll(Modules.Hits hitType = Modules.Hits.Normal, double? aoe = null)
        {
            var area = aoe ?? mob.aoe;

            mob.ForEachAttacker(attacker =>
            {
                var hit = new Hit(hitType, Formulas.GetDamage(mob, attacker), true, area, true);
                var projectile = world.entities.SpawnProjectile(mob, attacker, hit);

                mob.SendToRegions(new SpawnPacket(projectile));
            });
        }

        protected Mob Spawn(string key, double x, double y, bool plugin = false)
        {
            var minion = world.entities.SpawnMob(key, x, y, plugin);

            // Prevent minion from respawning after death.
            minion.respawnable = false;

            // Apply the same aggro range as the boss.
            minion.aggroRange = mob.aggroRange;

            // Make minions always aggressive
            minion.alwaysAggressive = true;

            // Remove the minion from the list when it dies.
            minion.OnDeathImpl(_ => minions.Delete(minion.instance));

            // Add the minion to the list.
            minions.Set(minion.instance, minion);

            return minion;
        }

        protected Character GetTarget()
        {
            // Skip if no attackers but somehow the boss got hit.
            if (mob.attackers.Count == 0) return null;

            // Find an attacker out of the list of attackers.
            return mob.GetRandomAttacker();
        }

        protected bool IsHalfHealth() => mob.hitPoints.GetHitPoints() / mob.hitPoints.GetMaxHitPoints() <= 0.5;

        protected bool IsQuarterHealth() => mob.hitPoints.GetHitPoints() / mob.hitPoints.GetMaxHitPoints() <= 0.25;

        /// <summary>for (let minion of Object.values(this.minions)) minion.deathCallback?.();</summary>
        protected void KillMinions()
        {
            foreach (var minion in minions.Values) minion.deathCallback?.Invoke(null);
        }
    }

    public sealed class SpiderPlugin : DefaultMobPlugin
    {
        public SpiderPlugin(Mob mob) : base(mob) { }
    }

    public sealed class AntPlugin : DefaultMobPlugin
    {
        JsTimeout healInterval;

        public AntPlugin(Mob mob) : base(mob)
        {
            healInterval = Node.SetInterval(HandleHeal, Utils.RandomInt(1000, 5000));
        }

        protected override void HandleHit(double damage, Character attacker) { }

        protected override void HandleDeath(Character attacker)
        {
            base.HandleDeath(attacker);

            if (healInterval == null) return;

            // Clear the interval.
            Node.ClearInterval(healInterval);
            healInterval = null;
        }

        void HandleHeal()
        {
            // Heal only if a target is present.
            if (!mob.HasTarget()) return;

            // Heal only if the target is a mob.
            if (!(mob.target?.IsMob() ?? false)) return;

            // Update the target following.
            mob.Follow(mob.target);

            // Heal only if the target is near.
            if (!mob.IsNearTarget()) return;

            ((Mob)mob.target).Heal(35, "hitpoints");
        }
    }

    public sealed class SantaPlugin : DefaultMobPlugin
    {
        bool healed;

        public SantaPlugin(Mob mob) : base(mob) { }

        protected override void HandleHit(double damage, Character attacker)
        {
            base.HandleHit(damage, attacker);

            if (healed) return;

            if (!IsHalfHealth()) return;

            healed = true;

            // Heals santa by 33% of his max health.
            var healAmount = Math.Floor(mob.hitPoints.GetMaxHitPoints() / 3);

            mob.Heal(healAmount);

            // Send a message to the player.
            mob.talkCallback?.Invoke("The power of Christmas heals me!");
        }

        protected override void HandleAttack()
        {
            base.HandleAttack();

            // Generate a random projectile between 1 and 6 and set the projectile name.
            var projectile = Utils.RandomInt(1, 6);
            var name = $"gift{(projectile == 1 ? "" : JsString.Of(projectile))}";

            mob.projectileName = name;

            // Add aoe to the projectile number 6.
            if (projectile == 6) mob.aoe = 4;
        }
    }

    public sealed class ForestDragonPlugin : DefaultMobPlugin
    {
        bool specialAttack;

        public ForestDragonPlugin(Mob mob) : base(mob) { }

        protected override void HandleAttack()
        {
            base.HandleAttack();

            // Ignore the special attack if one is already active.
            if (specialAttack)
            {
                ResetSpecialAttack();
                return;
            }

            // 1 in 6 chance to trigger a special attack.
            if (Utils.RandomInt(1, 6) != 2) return;

            // Inflict terror on the target.
            mob.attackRange = 9;
            mob.damageType = Modules.Hits.Terror;
            specialAttack = true;
        }

        protected override void HandleCombatLoop()
        {
            base.HandleCombatLoop();

            if (specialAttack) return;

            // Determine whether or not to use ranged attacks.
            var useRanged = mob.GetDistance(mob.target) > 1 || mob.target.IsRanged() || mob.target.moving;

            mob.damageType = Modules.Hits.Normal;

            // Update the mob's range distance.
            mob.attackRange = useRanged ? 10 : 1;

            // Updates the projectile per combat loop to reset the special attack.
            if (useRanged) mob.projectileName = "fireball";
        }

        void ResetSpecialAttack()
        {
            specialAttack = false;
            mob.attackRange = 1;
        }
    }

    public sealed class SkeletonKingPlugin : DefaultMobPlugin
    {
        const int MaxMinions = 6;

        // Two positions where the minions will spawn.
        readonly Position[] positions = { new(139, 782), new(125, 785) };

        int minionsSpawned;

        public SkeletonKingPlugin(Mob mob) : base(mob) { }

        protected override void HandleHit(double damage, Character attacker)
        {
            base.HandleHit(damage, attacker);

            // Add a random chance (1/4) to spawn a minion.
            if (Utils.RandomInt(1, 4) == 2) SpawnMob();
        }

        protected override void HandleDeath(Character attacker)
        {
            base.HandleDeath(attacker);

            // Clear all the minions from the list.
            KillMinions();

            // Reset minion spawn count.
            minionsSpawned = 0;
        }

        void SpawnMob()
        {
            // Maximum number of minions has been reached.
            if (minionsSpawned >= MaxMinions) return;

            var position = positions[(int)Utils.RandomInt(0, positions.Length - 1)];
            var minion = Spawn("skeleton", position.x, position.y);
            var target = GetTarget();

            // Minions have the same roaming distance as the skeleton king.
            minion.roamDistance = 24;

            if (target != null) minion.combat.Attack(target);

            minionsSpawned++;
        }
    }

    public sealed class HellhoundPlugin : DefaultMobPlugin
    {
        const int MaxMinions = 8;

        // Positions where minions will spawn.
        readonly Position[] positions = { new(1059, 765), new(1047, 768) };

        int minionsSpawned;

        public HellhoundPlugin(Mob mob) : base(mob) { }

        protected override void HandleHit(double damage, Character attacker)
        {
            base.HandleHit(damage, attacker);

            // Add a random chance (1/4) to spawn a minion.
            if (Utils.RandomInt(1, 6) == 2) SpawnMob();
        }

        protected override void HandleCombatLoop()
        {
            base.HandleCombatLoop();

            // Determine whether or not to use ranged attacks.
            var useRanged = mob.GetDistance(mob.target) > 1 || mob.target.IsRanged() || mob.target.moving;

            // Update the mob's range distance.
            mob.attackRange = useRanged ? 12 : 1;
        }

        void SpawnMob()
        {
            // Maximum number of minions has been reached.
            if (minionsSpawned >= MaxMinions) return;

            var minionKey = Utils.RandomInt(0, 1) == 0 ? "darkwolf" : "blackwizard";
            var position = minionKey == "blackwizard"
                ? positions[(int)Utils.RandomInt(0, positions.Length - 1)]
                : new Position(mob.x, mob.y);
            var minion = Spawn(minionKey, position.x, position.y);
            var target = GetTarget();

            // Minions have the same roaming distance as the skeleton king.
            minion.roamDistance = mob.roamDistance;

            // Increase the attack range of the black wizard minion.
            if (minionKey == "blackwizard") minion.attackRange = 16;

            if (target != null) minion.combat.Attack(target);

            minionsSpawned++;
        }

        protected override void HandleDeath(Character attacker)
        {
            base.HandleDeath(attacker);

            // Clear all the minions from the list.
            KillMinions();

            // Reset minion spawn count.
            minionsSpawned = 0;
        }
    }

    public sealed class OgreLordPlugin : DefaultMobPlugin
    {
        readonly string[] dialogues =
        {
            "The great ogre lord will trample over you!",
            "No, do not touch my onions!",
            "Me smash you!"
        };

        readonly Position[] positions = { new(338, 160), new(346, 164), new(342, 169), new(335, 164) };

        // Waves of minions the boss spawns.
        bool firstWaveMinions;
        bool secondWaveMinions;

        // Minion keys for waves
        readonly string[] minionKeys = { "ogre", "ironogre" };

        public OgreLordPlugin(Mob mob) : base(mob)
        {
            // Start the random dialogue interval and run it every 15 seconds.
            Node.SetInterval(RandomDialogue, 15_000);
        }

        protected override void HandleHit(double damage, Character attacker)
        {
            base.HandleHit(damage, attacker);

            HandleMinions();
        }

        protected override void HandleDeath(Character attacker)
        {
            base.HandleDeath(attacker);

            // Removes all the minions from the list.
            KillMinions();

            // Clear all boss properties.
            firstWaveMinions = false;
            secondWaveMinions = false;
        }

        void HandleMinions()
        {
            if (IsHalfHealth() && !firstWaveMinions) SpawnMinions(0);
            else if (IsQuarterHealth() && !secondWaveMinions) SpawnMinions(1);
        }

        void SpawnMinions(int wave)
        {
            var key = minionKeys[wave];

            // Iterate through the positions and spawn a mob at each one.
            foreach (var position in positions)
            {
                var minion = Spawn(key, position.x, position.y);
                var target = GetTarget();

                minion.roamDistance = 24;

                // Have the minions attack one of the boss' attackers.
                if (target != null) minion.combat.Attack(target);
            }

            // Prevent the boss from spawning more minions afterwards.
            if (wave == 0) firstWaveMinions = true;
            else if (wave == 1) secondWaveMinions = true;

            // Spawn minions text message.
            mob.talkCallback?.Invoke("My minions will surely help defeat you!");
        }

        void RandomDialogue()
        {
            if (!mob.combat.started) return;

            var dialogue = dialogues[(int)Utils.RandomInt(0, dialogues.Length - 1)];

            mob.talkCallback?.Invoke(dialogue);
        }
    }

    public sealed class PirateCaptainPlugin : DefaultMobPlugin
    {
        const int MaxMinions = 8, TeleportChance = 12, SpawnChance = 8;

        int minionsSpawned;
        readonly Position lastPickedTeleport = new(0, 0);

        readonly Position[] teleportSpots = { new(935, 743), new(930, 755), new(920, 753), new(918, 743) };

        readonly Position[] minionSpots = { new(923, 745), new(931, 746), new(929, 754), new(923, 752) };

        public PirateCaptainPlugin(Mob mob) : base(mob) { }

        protected override void HandleDeath(Character attacker)
        {
            base.HandleDeath(attacker);

            KillMinions();

            minionsSpawned = 0;

            mob.attackRange = 1;
        }

        protected override void HandleHit(double damage, Character attacker)
        {
            base.HandleHit(damage, attacker);

            if (CanSpawnMob())
            {
                SpawnMob();
                return;
            }

            if (CanTeleport()) Teleport();
        }

        void Teleport()
        {
            var position = teleportSpots[(int)Utils.RandomInt(0, teleportSpots.Length - 1)];

            if (position.x == lastPickedTeleport.x && position.y == lastPickedTeleport.y) return;

            // Stop all players from attacking the pirate captain.
            mob.world.CleanCombat(mob);

            mob.attackRange = 11;

            // Update the position and teleport the captain with an animation.
            mob.Teleport(position.x, position.y, true);
        }

        void SpawnMob()
        {
            if (minionsSpawned >= MaxMinions) return;

            var position = minionSpots[(int)Utils.RandomInt(0, minionSpots.Length - 1)];
            var minion = Spawn("pirateskeleton", position.x, position.y);
            var target = GetTarget(); // Pick a random target from the attackers

            // Update the roam distance for minions (so they don't go back to spawn point).
            minion.roamDistance = 20;

            // Set the target for the minion.
            if (target != null) minion.combat.Attack(target);

            minionsSpawned++;
        }

        bool CanTeleport() => Utils.RandomInt(0, TeleportChance) == 4;

        bool CanSpawnMob() => Utils.RandomInt(0, SpawnChance) == 4;
    }

    public sealed class QueenAntPlugin : DefaultMobPlugin
    {
        readonly Position[] positions;
        bool minionsSpawned;
        bool specialAttack;

        public QueenAntPlugin(Mob mob) : base(mob)
        {
            // Spawn the mobs around the Queen Ant.
            positions = new[]
            {
                new Position(mob.spawnX + 3, mob.spawnY),
                new Position(mob.spawnX - 3, mob.spawnY),
                new Position(mob.spawnX, mob.spawnY + 3),
                new Position(mob.spawnX, mob.spawnY - 3)
            };
        }

        protected override void HandleMovement()
        {
            base.HandleMovement();

            foreach (var minion in minions.Values) minion.Follow(mob);
        }

        protected override void HandleHit(double damage, Character attacker)
        {
            base.HandleHit(damage, attacker);

            if (!IsHalfHealth() || minionsSpawned) return;

            // Spawn minions using the positions surrounding the Queen Ant.
            foreach (var position in positions)
            {
                var minion = Spawn("ant", position.x, position.y, true);

                // Prevent minion from roaming.
                minion.roaming = false;

                // Queen Ant is the target.
                minion.SetTarget(mob);

                // Minions immediately start following the Queen Ant.
                minion.Follow(mob);

                minion.OnDeathImpl(_ =>
                {
                    if (minions.Count > 0) return;

                    mob.attackRate = ServerData.Mobs.Obj(mob.key).Num("attackRate") ?? double.NaN;
                });
            }

            minionsSpawned = true;

            mob.attackRate = 750;
        }

        protected override void HandleDeath(Character attacker)
        {
            base.HandleDeath(attacker);

            // Removes all the minions from the list.
            KillMinions();

            minionsSpawned = false;

            mob.attackRate = ServerData.Mobs.Obj(mob.key).Num("attackRate") ?? double.NaN;
        }

        protected override void HandleAttack()
        {
            base.HandleAttack();

            // Ignore the special attack if one is already active.
            if (specialAttack)
            {
                ResetSpecialAttack();
                return;
            }

            // 1 in 6 chance to trigger a special attack.
            if (Utils.RandomInt(1, 6) != 2) return;

            // 1 in 12 chance to trigger an AoE attack alongside special attack.
            if (Utils.RandomInt(1, 12) == 3) mob.aoe = 4;

            // Queen ant attacks with range and inflicts terror.
            AttackAll(Modules.Hits.Terror);

            specialAttack = true;
        }

        protected override void HandleCombatLoop()
        {
            base.HandleCombatLoop();

            if (specialAttack) return;

            // Determine whether or not to use ranged attacks.
            var useRanged = mob.GetDistance(mob.target) > 1 || mob.target.IsRanged() || mob.target.moving;

            // Update the mob's range distance.
            mob.attackRange = useRanged ? 10 : 1;
        }

        void ResetSpecialAttack()
        {
            specialAttack = false;
            mob.attackRange = 1;
        }
    }
}
