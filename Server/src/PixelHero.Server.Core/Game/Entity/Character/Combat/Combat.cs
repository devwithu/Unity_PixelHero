using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>character/combat/combat.ts.</summary>
    public sealed class Combat
    {
        public bool started;
        public double lastAttack;
        public double lastFollow;

        // The combat loop
        JsTimeout loop;

        Action startCallback;
        Action stopCallback;
        Action attackCallback;
        Action<double> loopCallback;

        readonly Character character;

        public Combat(Character character) => this.character = character;

        public void Start()
        {
            if (started) return;

            started = true;

            startCallback?.Invoke();

            if (loop != null) return;

            loop = Node.SetInterval(HandleLoop, character.GetAttackRate() / 4);
        }

        public void Stop()
        {
            // Clean up the combat loop.
            if (loop != null) Node.ClearInterval(loop);
            loop = null;

            // Clear target and attackers.
            character.ClearTarget();
            character.ClearAttackers();

            stopCallback?.Invoke();

            // Mark the combat as stopped.
            started = false;
        }

        public void UpdateLoop()
        {
            if (loop == null) return;

            Node.ClearInterval(loop);

            loop = Node.SetInterval(HandleLoop, character.GetAttackRate() / 4);
        }

        public void Attack(Entity entity)
        {
            if (entity == null) throw new JsTypeError("Cannot read properties of undefined (reading 'isCharacter')");

            // Cannot attack non-character targets. This is for extra safety.
            if (entity is not Character target)
            {
                Log.Warning($"Invalid target for {character.key}");
                return;
            }

            // Already started, let the loop handle it.
            character.SetTarget(target);

            if (started) return;

            Start();

            if (CanAttack()) Node.SetTimeout(HandleLoop, 450);
        }

        void HandleLoop()
        {
            // No target or target is dead, stop combat.
            if (!character.HasTarget() || character.target.IsDead() || character.teleporting)
            {
                Stop();
                return;
            }

            // Prevent combat loop from persisting during PVP flag changes.
            if (character.IsPlayer() && character.target.IsPlayer() && !character.target.pvp)
            {
                Stop();
                return;
            }

            loopCallback?.Invoke(lastAttack);

            CheckTargetPosition();

            if (character.IsNearTarget())
            {
                if (!CanAttack()) return;

                character.StopMovement();

                var hit = CreateHit();

                SendAttack(hit);

                lastAttack = Node.DateNow();

                attackCallback?.Invoke();
            }
            else
            {
                // Prevent follow spamming
                if (Node.DateNow() - lastFollow < 500) return;

                character.Follow();

                lastFollow = Node.DateNow();

                if (ShouldTeleportNearby()) character.SetPosition(character.target.x, character.target.y, false);
            }
        }

        void CheckTargetPosition()
        {
            if (!character.IsOnSameTile()) return;

            // Only move the character if it's a mob.
            if (!character.IsMob()) return;

            character.FindAdjacentTile();
        }

        void SendAttack(Hit hit)
        {
            // Ranged combat depends on when the projectile connects with the target.
            if (character.IsRanged())
            {
                SendRangedAttack(hit);
                return;
            }

            character.SendToRegions(new CombatPacket(Opcodes.Combat.Hit, new JsObject(
                "instance", character.instance,
                "target", character.target.instance,
                "hit", hit.Serialize())));

            // Handle combat damage here since melee is instant.
            character.target?.Hit(hit.GetDamage(), character, hit.aoe);

            // Apply effects based on hit types.
            character.target?.AddStatusEffect(hit);
        }

        void SendRangedAttack(Hit hit)
        {
            // Only archery based weapons check for arrows.
            if (!character.IsMagic() && !character.HasArrows())
            {
                Stop();
                return;
            }

            var projectile = character.world.entities.SpawnProjectile(character, character.target, hit);

            // Spawn the projectile in the game client.
            character.SendToRegions(new SpawnPacket(projectile));
        }

        Hit CreateHit()
        {
            var damageType = character.GetDamageType();

            return new Hit(
                damageType,
                Formulas.GetDamage(character, character.target, damageType == Modules.Hits.Critical),
                character.IsRanged(),
                character.GetAoE(),
                character.IsMagic(),
                character.IsArcher(),
                character.GetAttackStyle());
        }

        bool CanAttack() => Node.DateNow() - lastAttack >= character.GetAttackRate();

        public bool Expired() => Node.DateNow() - lastAttack > 10_000;

        public bool IsPvp() => character.IsPlayer() && character.target != null && character.target.IsPlayer() && character.target.pvp;

        bool ShouldTeleportNearby()
        {
            if (!character.IsMob()) return false;

            if (character.IsStunned() || character.IsDead()) return false;

            // Prevent teleporting when differences in plateau exist.
            if (character.plateauLevel != character.target?.plateauLevel) return false;

            return Node.DateNow() - character.lastMovement > 5000;
        }

        public void OnStart(Action callback) => startCallback = callback;

        public void OnStop(Action callback) => stopCallback = callback;

        public void OnAttack(Action callback) => attackCallback = callback;

        public void OnLoop(Action<double> callback) => loopCallback = callback;
    }
}
