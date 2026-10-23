using PixelHero.Server.Controllers;
using PixelHero.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Game
{
    /// <summary>character/effect/status.ts.</summary>
    public sealed class Status
    {
        sealed class Duration
        {
            public JsTimeout timeout;
            public double startTime;
            public double duration;
        }

        List<Modules.Effects> effects = new();
        readonly JsObject<Duration> durations = new();

        Action<Modules.Effects> addCallback;
        Action<Modules.Effects> removeCallback;

        static string Key(Modules.Effects effect) => ((int)effect).ToString();

        /// <summary>Loads serialized effects: {[type]: {remainingTime}}.</summary>
        public void Load(JsObject effects)
        {
            foreach (var pair in effects)
            {
                var effect = pair.Value as JsObject;

                // Effect has already passed our current time, just ignore it.
                if (effect.Num("remainingTime") is double remaining && remaining < 100) continue;

                AddWithTimeout((Modules.Effects)(int)JsString.ParseInt(pair.Key), effect.Num("remainingTime") ?? double.NaN);
            }
        }

        public void Add(params Modules.Effects[] statusEffect)
        {
            foreach (var status in statusEffect)
            {
                // Don't add the effect if it already exists.
                if (Has(status)) continue;

                effects.Add(status);

                addCallback?.Invoke(status);
            }
        }

        public void AddWithTimeout(Modules.Effects statusEffect, double duration, Action callback = null)
        {
            // A temporary freezing effect cannot be added if the player has a permanent one.
            if (statusEffect == Modules.Effects.Freezing && HasPermanentFreezing()) return;

            Add(statusEffect);

            // Clear existing timeouts.
            if (durations[Key(statusEffect)] != null)
            {
                Node.ClearTimeout(durations[Key(statusEffect)].timeout);
                durations.Delete(Key(statusEffect));
            }

            // Start a new effect duration handler.
            durations.Set(Key(statusEffect), new Duration
            {
                // Begin the timeout and remove the status effect once it is up.
                timeout = Node.SetTimeout(() =>
                {
                    Remove(statusEffect);

                    callback?.Invoke();
                }, duration),
                startTime = Node.DateNow() - 1000,
                duration = duration
            });
        }

        public void Remove(params Modules.Effects[] statusEffect)
        {
            foreach (var status in statusEffect)
            {
                effects = effects.Where(effect => effect != status).ToList();

                // Remove the status effect from the list of durations.
                if (durations[Key(status)] != null) durations.Delete(Key(status));

                removeCallback?.Invoke(status);
            }
        }

        public void Clear()
        {
            effects = new List<Modules.Effects>();

            // Clear all the timeouts.
            foreach (var pair in durations)
            {
                Node.ClearTimeout(pair.Value.timeout);
                durations.Delete(pair.Key);
            }
        }

        public bool Has(Modules.Effects status) => effects.Contains(status);

        public bool HasTimeout(Modules.Effects status) => durations[Key(status)]?.timeout != null;

        bool HasPermanentFreezing() => Has(Modules.Effects.Freezing) && !HasTimeout(Modules.Effects.Freezing);

        public JsObject Serialize()
        {
            var serialized = new JsObject();

            foreach (var pair in durations)
            {
                var duration = pair.Value;

                serialized.Set(pair.Key,
                    new JsObject("remainingTime", duration.startTime + duration.duration - Node.DateNow()));
            }

            return serialized;
        }

        public void ForEachEffect(Action<Modules.Effects> callback)
        {
            foreach (var status in effects) callback(status);
        }

        public void OnAdd(Action<Modules.Effects> callback) => addCallback = callback;

        public void OnRemove(Action<Modules.Effects> callback) => removeCallback = callback;
    }
}
