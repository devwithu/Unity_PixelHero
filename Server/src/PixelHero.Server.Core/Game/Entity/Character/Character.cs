using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server.Game
{
    /// <summary>game/entity/character/character.ts.</summary>
    public abstract class Character : Entity
    {
        public double level = 1;
        public double attackRange = 1;
        public double plateauLevel;

        public Combat combat;
        public HitPoints hitPoints;

        public double healRate = Constants.HEAL_RATE;
        public double movementSpeed = (double)Modules.Defaults.MOVEMENT_SPEED;
        public double attackRate = (double)Modules.Defaults.ATTACK_RATE;
        public double orientation = (double)Modules.Orientation.Down;
        public Modules.Hits damageType = Modules.Hits.Normal;

        /* States */
        public Poison poison;
        public Status status;

        // Character that is currently being targeted.
        public Character target;

        // List of entities attacking this character.
        public List<Character> attackers = new(); // Used by combat to determine which character to target.
        public JsObject<double?> damageTable = new();

        public bool moving;
        public bool pvp;
        public bool teleporting;
        public double aoe;

        // Effects applied onto the character.
        public List<Modules.Effects> statusEffects = new();

        public string projectileName = "arrow";

        public double lastStep = -1;
        public double lastMovement;
        public double lastRegionChange = -1;

        JsTimeout healingInterval;
        JsTimeout effectInterval;
        JsTimeout poisonInterval;

        Action<double, bool> poisonCallback;
        public Action<double, Character, bool> hitCallback;
        public Action<Character> deathCallback;
        public Action<Character> deathICallback;

        public World world;

        protected Character(string instance, World world, string key, double x, double y) : base(instance, key, x, y)
        {
            // Parameter property, then the field initializers with side effects, then the constructor body.
            this.world = world;
            status = new Status();
            lastMovement = Node.DateNow();

            combat = new Combat(this);
            hitPoints = new HitPoints(Formulas.GetMaxHitPoints(level));

            hitPoints.OnHitPoints(HandleHitPoints);

            status.OnAdd(HandleStatusEffectAdd);
            status.OnRemove(HandleStatusEffectRemove);

            healingInterval = Node.SetInterval(() => Heal(), Constants.HEAL_RATE);
            effectInterval = Node.SetInterval(Effects, Constants.EFFECT_RATE);
        }

        void HandleHitPoints()
        {
            // Sync the change in hitpoints to nearby entities.
            SendToRegions(new PointsPacket(new JsObject(
                "instance", instance,
                "hitPoints", hitPoints.GetHitPoints(),
                "maxHitPoints", hitPoints.GetMaxHitPoints())));
        }

        void HandleStatusEffectAdd(Modules.Effects effect)
        {
            // Synchronize the movement speed of the player when freezing applies.
            if (IsPlayer() && effect == Modules.Effects.Freezing) ((Player)this).Sync();

            SendToRegions(new EffectPacket(Opcodes.Effect.Add, new JsObject("instance", instance, "effect", (double)effect)));
        }

        void HandleStatusEffectRemove(Modules.Effects effect)
        {
            // Synchronize the movement speed of the player when freezing is removed.
            if (IsPlayer() && effect == Modules.Effects.Freezing)
            {
                // Synchronize the movement speed of the player.
                ((Player)this).Sync();

                // Cannot remove freezing effect if the player is in a freezing area.
                if (((Player)this).InFreezingArea())
                {
                    status.Add(Modules.Effects.Freezing);
                    return;
                }
            }

            SendToRegions(new EffectPacket(Opcodes.Effect.Remove, new JsObject("instance", instance, "effect", (double)effect)));
        }

        void HandlePoison()
        {
            if (poison == null) return;

            // Remove the poison if it has expired.
            if (poison.Expired())
            {
                SetPoison();
                return;
            }

            // Create a hit object for poison damage and serialize it.
            var hit = new Hit(Modules.Hits.Poison, poison.damage).Serialize();

            // Send a hit packet to display the info to the client.
            SendToRegions(new CombatPacket(Opcodes.Combat.Hit, new JsObject(
                "instance", instance,
                "target", instance,
                "hit", hit)));

            // Do the actual damage to the character.
            Hit(poison.damage);
        }

        void HandleAoE(double damage, Character attacker, double range = 1)
        {
            ForEachNearbyCharacter(character =>
            {
                var distance = GetDistance(character) + 1;
                var hit = new Hit(Modules.Hits.Normal, Math.Floor(damage / distance), false, distance).Serialize();

                // Create a hit packet and send it to the nearby regions.
                SendToRegions(new CombatPacket(Opcodes.Combat.Hit, new JsObject(
                    "instance", JsData.Truthy(attacker?.instance) ? attacker.instance : "",
                    "target", character.instance,
                    "hit", hit)));

                // Apply the damage to the character.
                character.Hit(hit.Num("damage") ?? double.NaN, attacker);
            }, range);
        }

        public void HandleColdDamage()
        {
            // Only players that do not have the snow potion can be affected.
            if (status.Has(Modules.Effects.SnowPotion)) return;

            // Create a hit object for cold damage and serialize it.
            var hit = new Hit(Modules.Hits.Freezing, Constants.COLD_EFFECT_DAMAGE).Serialize();

            // Send a hit packet to display the info to the client.
            SendToRegions(new CombatPacket(Opcodes.Combat.Hit, new JsObject(
                "instance", instance,
                "target", instance,
                "hit", hit)));

            // Do the actual damage to the character.
            Hit(Constants.COLD_EFFECT_DAMAGE);
        }

        public void HandleBurningDamage()
        {
            // Only players that do not have the fire potion can be affected.
            if (status.Has(Modules.Effects.FirePotion)) return;

            // Create a hit object for burning damage and serialize it.
            var hit = new Hit(Modules.Hits.Burning, Constants.BURNING_EFFECT_DAMAGE).Serialize();

            // Send a hit packet to display the info to the client.
            SendToRegions(new CombatPacket(Opcodes.Combat.Hit, new JsObject(
                "instance", instance,
                "target", instance,
                "hit", hit)));

            // Do the actual damage to the character.
            Hit(Constants.BURNING_EFFECT_DAMAGE);
        }

        void HandlePoisonDamage(Character attacker)
        {
            // Poison is related to the strength or archery level.
            var isPoisoned = Formulas.GetPoisonChance(GetSkillDamageLevel()) < attacker.GetPoisonChance();

            // Use venom as default for now.
            if (isPoisoned) SetPoison((double)Modules.PoisonTypes.Venom);
        }

        void HandleBloodsucking(Character attacker, double damage)
        {
            // Blood sucking has a 30% chance of occurring, so we return 70% of the time.
            if (Utils.RandomInt(0, 100) > 30) return;

            // 5% of the damage dealt per level of bloodsucking is healed.
            var heal = Math.Floor(damage * (0.05 * attacker.GetBloodsuckingLevel()));

            // Prevent healing if the amount is less than 1.
            if (heal < 1) return;

            // Players heal non-passively (heal packet is sent).
            if (attacker.IsPlayer()) ((Player)attacker).Heal(heal, "hitpoints");
            else attacker.Heal(heal);
        }

        public virtual void Heal(double amount = 1)
        {
            // Cannot heal if dead or poisoned.
            if (IsDead() || poison != null) return;

            // Cannot heal if engaged in combat.
            if (combat.started) return;

            // Cannot heal if character is being attacked.
            if (GetAttackerCount() > 0) return;

            // Stops the character from healing if they are at max hitpoints.
            if (hitPoints.IsFull()) return;

            // Certain status effects prevent the character from healing.
            if (status.Has(Modules.Effects.Freezing) || status.Has(Modules.Effects.Burning) ||
                status.Has(Modules.Effects.Terror))
                return;

            hitPoints.Increment(amount);
        }

        public void Effects()
        {
            status.ForEachEffect(effect =>
            {
                switch (effect)
                {
                    case Modules.Effects.Freezing:
                        HandleColdDamage();
                        return;
                    case Modules.Effects.Burning:
                        HandleBurningDamage();
                        return;
                }
            });
        }

        public virtual void FindAdjacentTile()
        {
            if (!world.map.IsColliding(x + 1, y)) SetPosition(x + 1, y);
            else if (!world.map.IsColliding(x - 1, y)) SetPosition(x - 1, y);
            else if (!world.map.IsColliding(x, y + 1)) SetPosition(x, y + 1);
            else if (!world.map.IsColliding(x, y - 1)) SetPosition(x, y - 1);
        }

        public virtual void Stop()
        {
            Node.ClearInterval(healingInterval);
            Node.ClearInterval(effectInterval);

            healingInterval = null;
            effectInterval = null;
        }

        public virtual void Hit(double damage, Character attacker = null, double aoe = 0, bool isThorns = false)
        {
            // Stop hitting if entity is dead.
            if (IsDead() || status.Has(Modules.Effects.Invincible)) return;

            // Add an entry to the damage table.
            if (attacker != null && attacker.IsPlayer()) AddToDamageTable(attacker, damage);

            // Decrement health by the damage amount.
            hitPoints.Decrement(damage);

            // If this is an AoE attack, we will damage all nearby characters.
            if (aoe != 0 && !double.IsNaN(aoe)) HandleAoE(damage, attacker, aoe);

            // Hit callback on each hit.
            hitCallback?.Invoke(damage, attacker, isThorns);

            // If the character has bloodsucking, we let the handler take care of it.
            if (attacker != null && attacker.HasBloodsucking()) HandleBloodsucking(attacker, damage);

            // Call the death callback if the character reaches 0 hitpoints.
            if (IsDead())
            {
                // Clear the status effects.
                status.Clear();

                deathCallback?.Invoke(attacker);
                return;
            }

            // Poison only occurs when we land a hit and attacker has a poisonous weapon.
            if (attacker != null && attacker.IsPoisonous() && damage > 0) HandlePoisonDamage(attacker);
        }

        public void Follow(Character target = null)
        {
            // If the character is stunned, we stop.
            if (IsStunned()) return;

            // If no target is specified and we don't have a target, we stop.
            if (target == null && !HasTarget()) return;

            SendToRegions(new MovementPacket(Opcodes.Movement.Follow, new JsObject(
                "instance", instance,
                "target", JsData.Truthy(target?.instance) ? target.instance : this.target.instance)));
        }

        public virtual void Teleport(double x, double y, bool withAnimation = false)
        {
            SetPosition(x, y, true);

            SendToRegions(new TeleportPacket(new JsObject(
                "instance", instance,
                "x", x,
                "y", y,
                "withAnimation", withAnimation)));

            // Untoggle the teleporting flag after 500ms.
            Node.SetTimeout(() => teleporting = false, 500);
        }

        public void Countdown(double time) =>
            SendToRegions(new CountdownPacket(new JsObject("instance", instance, "time", time)));

        public void StopMovement() =>
            SendToRegions(new MovementPacket(Opcodes.Movement.Stop, new JsObject("instance", instance)));

        public void ClearTarget() => target = null;

        public void ClearAttackers() => attackers = new List<Character>();

        public void RemoveAttacker(Character attacker) =>
            attackers = attackers.Where(a => a.instance != attacker.instance).ToList();

        public double GetAttackerCount() => attackers.Count;

        public virtual double GetAttackRate() => attackRate;

        public virtual JsObject GetAttackStats() => Utils.GetEmptyStats();

        public virtual JsObject GetDefenseStats() => Utils.GetEmptyStats();

        public virtual JsObject GetBonuses() => Utils.GetEmptyBonuses();

        public virtual double GetAccuracyBonus() => GetBonuses().Num("accuracy") ?? double.NaN;

        public virtual double GetAccuracyLevel() => 1;

        public virtual double GetStrengthLevel() => 1;

        public virtual double GetArcheryLevel() => 1;

        public virtual double GetDefenseLevel() => 1;

        public virtual double GetDamageBonus() =>
            (IsRanged() ? GetBonuses().Num("archery") : GetBonuses().Num("strength")) ?? double.NaN;

        public virtual double GetSkillDamageLevel() => IsRanged() ? GetArcheryLevel() : GetStrengthLevel();

        public virtual double GetDamageReduction() => 1;

        public virtual Modules.AttackStyle GetAttackStyle() => Modules.AttackStyle.None;

        public virtual Modules.Hits GetDamageType() => damageType;

        public virtual double GetPoisonChance() => (double)Modules.Defaults.POISON_CHANCE;

        public Character GetRandomAttacker()
        {
            var index = Utils.RandomInt(0, attackers.Count - 1);
            return index >= 0 && index < attackers.Count ? attackers[(int)index] : null;
        }

        public double GetAoE()
        {
            var aoeVal = aoe;

            if (aoe != 0 && !double.IsNaN(aoe)) aoe = 0;

            return aoeVal;
        }

        public virtual string GetProjectileName() => projectileName;

        public double GetLastAttack() => Node.DateNow() - combat.lastAttack;

        public virtual double GetBloodsuckingLevel() => 1;

        public virtual bool HasSpecialAttack() => false;

        public bool HasTarget() => target != null;

        public bool HasAttacker(Character attacker) => attackers.Any(a => a.instance == attacker.instance);

        public virtual bool HasArrows() => true;

        public virtual bool HasBloodsucking() => false;

        public bool InCombat() => combat.started || attackers.Count > 0 || HasTarget() || !combat.Expired();

        public bool IsRanged() => attackRange > 1;

        public virtual bool IsArcher() => IsRanged() && !IsMagic();

        public virtual bool IsMagic() => false;

        public bool IsDead() => hitPoints.IsEmpty() || dead;

        public bool IsNearTarget()
        {
            if (IsRanged())
                return GetDistance(target) <= attackRange && plateauLevel >= target.plateauLevel;

            return IsAdjacent(target);
        }

        public virtual bool IsPoisonous() => false;

        public bool IsOnSameTile() => target != null && x == target.x && y == target.y;

        public bool IsStunned() => status.Has(Modules.Effects.Stun);

        protected bool CanAttack(Character target)
        {
            // Prevent pets from being attacked.
            if (target.IsPet())
            {
                if (IsPlayer()) ((Player)this).Notify("misc:CANNOT_ATTACK_PET");
                return false;
            }

            if (target.IsMob())
            {
                // Restrict the mobs in tutorial from being attacked by the player.
                if (IsPlayer() && !((Player)this).quests.CanAttackInTutorial())
                {
                    ((Player)this).Notify("misc:CANNOT_ATTACK_MOB");
                    return false;
                }

                return true;
            }

            // If either of the entities are not players, we don't want to handle this.
            if (!IsPlayer() || !target.IsPlayer()) return false;

            var self = (Player)this;
            var other = (Player)target;

            // Prevent cheaters from being targeted by other players.
            if (other.IsCheater())
            {
                self.Notify("misc:CANNOT_ATTACK_CHEATER");
                return false;
            }

            // Prevent cheaters from starting a fight with other players.
            if (self.IsCheater())
            {
                self.Notify("misc:CANNOT_ATTACK_CHEATER_SELF");
                return false;
            }

            // Handle logic for in-minigame combat.
            if (self.InMinigame())
            {
                if (self.team == Team.Prey)
                {
                    self.Notify("misc:CANNOT_ATTACK_PREY");
                    return false;
                }

                if (self.team == Team.Hunter && target.instance != self.coursingTarget)
                {
                    self.Notify("misc:CANNOT_ATTACK_TARGET");
                    return false;
                }

                // Default implementation (used for team vs team minigames);
                if (other.InMinigame()) return self.team != other.team;
            }

            // Prevent attacking in non-pvp areas.
            if (!pvp && !target.pvp) return false;

            // Prevent attacking when level difference is too great.
            if (Math.Abs(level - target.level) > 30)
            {
                self.Notify("misc:CANNOT_ATTACK_LEVEL_DIFF");
                return false;
            }

            return true;
        }

        public override void SetPosition(double x, double y) => SetPosition(x, y, false);

        public virtual void SetPosition(double x, double y, bool withTeleport)
        {
            if (teleporting) return;

            base.SetPosition(x, y);

            // Updates the character's position in the grid.
            world.map.grids.UpdateEntity(this);

            // Update the teleporting flag if we're teleporting.
            if (withTeleport) teleporting = true;
        }

        public void SetTarget(Character target) => this.target = target;

        public void AddAttacker(Character attacker)
        {
            // Prevent adding yourself as an attacker.
            if (attacker.instance == instance || HasAttacker(attacker)) return;

            attackers.Add(attacker);
        }

        public void AddToDamageTable(Character attacker, double damage)
        {
            // Max out the damage to the remaining hit points.
            if (damage >= hitPoints.GetHitPoints()) damage = hitPoints.GetHitPoints();

            // Add a new entry to the damage table if it doesn't exist.
            if (!damageTable.Has(attacker.instance)) damageTable.Set(attacker.instance, damage);

            // Otherwise, add the damage to the existing entry.
            damageTable.Set(attacker.instance, (damageTable[attacker.instance] ?? double.NaN) + damage);
        }

        public void AddStatusEffect(Hit hit)
        {
            if (IsDead() || hit.type == Modules.Hits.Normal) return;

            switch (hit.type)
            {
                case Modules.Hits.Stun:
                    status.AddWithTimeout(Modules.Effects.Stun, Constants.STUN_DURATION);
                    return;

                case Modules.Hits.Terror:
                    status.AddWithTimeout(Modules.Effects.Terror, Constants.TERROR_DURATION);
                    return;

                case Modules.Hits.Freezing:
                    if (status.Has(Modules.Effects.SnowPotion)) return;
                    status.AddWithTimeout(Modules.Effects.Freezing, Constants.FREEZING_DURATION);
                    return;

                case Modules.Hits.Burning:
                    if (status.Has(Modules.Effects.FirePotion)) return;
                    status.AddWithTimeout(Modules.Effects.Burning, Constants.BURNING_DURATION);
                    return;
            }
        }

        public Character FindNearestTarget()
        {
            // Finds a Character within the list of attackers that is closest to the current character.
            if (attackers.Count == 0) throw new JsTypeError("Reduce of empty array with no initial value");

            return attackers.Aggregate((prev, curr) => prev.GetDistance(this) < curr.GetDistance(this) ? prev : curr);
        }

        public void SetHitPoints(double hitPoints) => this.hitPoints.SetHitPoints(hitPoints);

        public void SetPoison(double type = -1, double? start = null)
        {
            var remove = type == -1;

            // No need to remove a non-existant status.
            if (remove && poison == null) return;

            // Used to prevent poison status stacking.
            var exists = type != -1 && poison != null;

            // Set or remove the poison status.
            poison = remove ? null : new Poison((Modules.PoisonTypes)(int)type, start);

            // Remove the poison status or create an interval for the poison.
            if (remove)
            {
                Node.ClearInterval(poisonInterval);
                poisonInterval = null;
            }
            else if (!exists) poisonInterval = Node.SetInterval(HandlePoison, poison?.rate ?? double.NaN);

            poisonCallback?.Invoke(type, !exists);
        }

        public void SetOrientation(double orientation) => this.orientation = orientation;

        // Packet sending functions

        public void SendToRegion(Packet packet, bool ignore = false) =>
            world.Push(Modules.PacketType.Region, new PacketData
            {
                region = region,
                packet = packet,
                ignore = ignore ? instance : ""
            });

        public void SendToRegions(Packet packet, bool ignore = false) =>
            world.Push(Modules.PacketType.Regions, new PacketData
            {
                region = region,
                packet = packet,
                ignore = ignore ? instance : ""
            });

        public void SendBroadcast(Packet packet) =>
            world.Push(Modules.PacketType.Broadcast, new PacketData { packet = packet });

        // End of packet sending functions

        public void ForEachNearbyCharacter(Action<Character> callback, double range = 1)
        {
            world.GetGrids().ForEachEntityNear(x, y, entity =>
            {
                // Ignores the current character.
                if (entity.instance == instance) return;

                // Ignore non-character entities and pets.
                if (entity is not Character character || entity.IsPet()) return;

                // Ignore mobs if the character is a mob.
                if (IsMob() && entity.IsMob()) return;

                // Ignore player-on-player AoE if the players are not in a PvP area.
                if (IsPlayer() && entity.IsPlayer() && !pvp && !character.pvp) return;

                callback(character);
            }, range);
        }

        public void ForEachAttacker(Action<Character> callback)
        {
            foreach (var attacker in attackers) callback(attacker);
        }

        public override JsObject Serialize()
        {
            var data = base.Serialize();

            data.Set("movementSpeed", movementSpeed);
            data.Set("orientation", orientation);

            return data;
        }

        public void OnPoison(Action<double, bool> callback) => poisonCallback = callback;

        public void OnHit(Action<double, Character, bool> callback) => hitCallback = callback;

        public void OnDeath(Action<Character> callback) => deathCallback = callback;

        public void OnDeathImpl(Action<Character> callback) => deathICallback = callback;
    }
}
