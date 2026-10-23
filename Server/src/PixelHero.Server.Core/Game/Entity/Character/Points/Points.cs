namespace PixelHero.Server.Game
{
    /// <summary>character/points/points.ts.</summary>
    public abstract class Points
    {
        public double points;
        public double maxPoints;

        protected Points(double points, double maxPoints)
        {
            this.points = points;
            this.maxPoints = maxPoints;
        }

        public virtual void Increment(double amount) => SetPoints(points + amount);

        public virtual void Decrement(double amount) => SetPoints(points - amount);

        protected void UpdatePoints(double points, double? maxPoints = null)
        {
            // !maxPoints: undefined, 0 and NaN all fall back to points.
            var max = maxPoints is double m && m != 0 && !double.IsNaN(m) ? m : points;

            SetMaxPoints(max);
            SetPoints(points);
        }

        protected void SetPoints(double points)
        {
            this.points = points;

            if (this.points >= maxPoints || double.IsNaN(points)) this.points = maxPoints;
            if (this.points < 0) this.points = 0;
        }

        protected void SetMaxPoints(double maxPoints)
        {
            if (points > maxPoints) points = maxPoints;

            this.maxPoints = maxPoints;
        }

        public void Reset() => SetPoints(maxPoints);

        public bool IsFull() => points >= maxPoints;

        public bool IsEmpty() => points <= 0;

        public List<object> Serialize() => new() { points, maxPoints };
    }

    /// <summary>character/points/hitpoints.ts.</summary>
    public sealed class HitPoints : Points
    {
        Action hitPointsCallback;

        /// <summary>maxHitPoints || hitPoints.</summary>
        public HitPoints(double hitPoints, double? maxHitPoints = null)
            : base(hitPoints, maxHitPoints is double m && m != 0 && !double.IsNaN(m) ? m : hitPoints) { }

        public override void Increment(double amount)
        {
            base.Increment(amount);
            hitPointsCallback?.Invoke();
        }

        public override void Decrement(double amount)
        {
            base.Decrement(amount);
            hitPointsCallback?.Invoke();
        }

        public void UpdateHitPoints(double hitPoints, double? maxHitPoints = null) => UpdatePoints(hitPoints, maxHitPoints);

        public void SetHitPoints(double hitPoints)
        {
            SetPoints(hitPoints);
            hitPointsCallback?.Invoke();
        }

        public void SetMaxHitPoints(double maxHitPoints) => SetMaxPoints(maxHitPoints);

        public double GetHitPoints() => points;

        public double GetMaxHitPoints() => maxPoints;

        public void OnHitPoints(Action callback) => hitPointsCallback = callback;
    }

    /// <summary>character/points/mana.ts.</summary>
    public sealed class Mana : Points
    {
        Action manaCallback;

        public Mana(double mana, double? maxMana = null)
            : base(mana, maxMana is double m && m != 0 && !double.IsNaN(m) ? m : mana) { }

        public override void Increment(double amount)
        {
            base.Increment(amount);
            manaCallback?.Invoke();
        }

        public override void Decrement(double amount)
        {
            base.Decrement(amount);
            manaCallback?.Invoke();
        }

        public void UpdateMana(double mana, double? maxMana = null) => UpdatePoints(mana, maxMana);

        public void SetMana(double mana)
        {
            SetPoints(mana);
            manaCallback?.Invoke();
        }

        public void SetMaxMana(double maxMana) => SetMaxPoints(maxMana);

        public double GetMana() => points;

        public double GetMaxMana() => maxPoints;

        public void OnMana(Action callback) => manaCallback = callback;
    }
}
