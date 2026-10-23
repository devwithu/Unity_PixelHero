using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/character/mob/handler.ts.</summary>
    public class MobHandler
    {
        protected World world;
        protected Map map;
        protected Mob mob;

        public MobHandler(Mob mob)
        {
            this.mob = mob;
            world = mob.world;
            map = world.map;

            mob.OnMovement((_, _) => HandleMovement());
            mob.OnHit((damage, attacker, _) => HandleHit(damage, attacker));
            mob.OnDeath(HandleDeath);
            mob.OnRespawn(HandleRespawn);
            mob.OnRoaming(HandleRoaming);
            mob.OnTalk(HandleTalk);

            // Combat callbacks
            mob.combat.OnStart(HandleCombatStart);
            mob.combat.OnStop(HandleCombatStop);
            mob.combat.OnAttack(HandleAttack);
            mob.combat.OnLoop(_ => HandleCombatLoop());
        }

        protected virtual void HandleMovement()
        {
            // Mob is randomly roaming and exits the roaming area.
            if (!mob.HasTarget() && mob.OutsideRoaming())
            {
                mob.SendToSpawn();
                return;
            }

            // We double the roaming distance for the sake of combat.
            if (mob.OutsideRoaming(mob.target?.x, mob.target?.y, mob.roamDistance * 2))
                if (mob.GetAttackerCount() > 1) mob.SetTarget(mob.FindNearestTarget());
                else mob.SendToSpawn();
        }

        protected virtual void HandleHit(double damage, Character attacker)
        {
            if (attacker == null) return;

            if (attacker.IsPlayer()) ((Player)attacker).HandleExperience(damage);

            // This may get called simulatneously with the death callback, so we check here.
            if (mob.IsDead()) return;

            mob.AddAttacker(attacker);

            if (!mob.combat.started) mob.combat.Attack(mob.FindNearestTarget());
        }

        protected virtual void HandleDeath(Character attacker)
        {
            // The damage table is used to calculate who should receive priority over the mob's drop.
            var damageTable = mob.GetDamageTable();

            for (var index = 0; index < damageTable.Count; index++)
            {
                var instance = damageTable[index].Key;
                var entity = world.entities.Get(instance);

                if (entity == null || !entity.IsPlayer()) continue;

                // Kill callback is sent to the player who dealt most amount of damage.
                if (index == 0)
                {
                    var player = (Player)entity;

                    // Register the kill as belonging to the player who dealt most amount of damage.
                    player.killCallback?.Invoke(mob);

                    // Drop the mob's loot and pass the owner's username.
                    mob.Drop(player);
                }
            }

            // Stop the combat.
            mob.combat.Stop();

            // Clear status effects.
            mob.status.Clear();

            // Remove entity from chest area.
            mob.area?.RemoveEntity(mob, attacker);

            // Respawn the chest associated with the mob.
            mob.chest?.Respawn();

            // Call the secondary death callback.
            mob.deathICallback?.Invoke(attacker);

            // Despawn entity from the world.
            world.entities.Remove(mob);

            world.CleanCombat(mob);

            mob.Destroy();
        }

        protected virtual void HandleRespawn()
        {
            mob.dead = false;

            mob.hitPoints.Reset();

            world.entities.AddMob(mob);
        }

        protected virtual void HandleRoaming()
        {
            // Prevent roaming.
            if (!mob.roaming) return;

            // Ensure the mob isn't dead first.
            if (mob.dead) return;

            double x = mob.x, y = mob.y, spawnX = mob.spawnX, spawnY = mob.spawnY, roamDistance = mob.roamDistance;
            var plateauLevel = mob.plateauLevel;
            var combat = mob.combat;
            var newX = spawnX + Utils.RandomInt(-roamDistance, roamDistance);
            var newY = spawnY + Utils.RandomInt(-roamDistance, roamDistance);
            var distance = Utils.GetDistance(spawnX, spawnY, newX, newY);

            // Do not roam while in combat.
            if (combat.started) return;

            // Prevent mobs from going outside of their roaming radius.
            if (distance > roamDistance) return;

            // No need to move if the new position is the same as the current.
            if (newX == x && newY == y) return;

            if (plateauLevel != map.GetPlateauLevel(newX, newY)) return;

            // Check if the new position is a collision.
            if (map.IsColliding(newX, newY)) return;

            // Don't have mobs block a door.
            if (map.IsDoor(newX, newY)) return;

            mob.SetPosition(newX, newY);
        }

        protected virtual void HandleTalk(string text) =>
            mob.SendToRegions(new BubblePacket(new JsObject("instance", mob.instance, "text", text)));

        protected virtual void HandleCombatStart() { }

        protected virtual void HandleCombatStop() { }

        protected virtual void HandleAttack() { }

        protected virtual void HandleCombatLoop()
        {
            if (mob.instance == mob.target?.instance)
            {
                Log.General($"Mob {mob.key} is attacking itself.");
                mob.combat.Stop();
                return;
            }

            // Parses through the attackers and removes them if they are too far away.
            mob.ForEachAttacker(attacker =>
            {
                if (mob.GetDistance(attacker) > mob.roamDistance * 2 ||
                    (!mob.IsNearTarget() && attacker.GetLastAttack() > Constants.ATTACKER_TIMEOUT))
                    mob.RemoveAttacker(attacker);
            });

            // Ignore if we have only one attacker.
            if (mob.GetAttackerCount() < 2 || !mob.CanChangeTarget()) return;

            // Alternate targets if another one is nearby.
            var newTarget = mob.GetRandomAttacker();

            // New target is too far away, so we ignore it.
            if (mob.GetDistance(newTarget) >= mob.attackRange) return;

            // We have a new target, so we attack it.
            mob.combat.Attack(newTarget);
        }
    }
}
