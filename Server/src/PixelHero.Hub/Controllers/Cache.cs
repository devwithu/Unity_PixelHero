using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;

namespace PixelHero.Hub.Controllers
{
    /// <summary>
    /// controllers/cache.ts. The cache keeps track of database information that we extract. We use it so that we do
    /// not make database requests repeatedly, and instead use existing ones. The cache is updated after a specified
    /// amount of time (AGGREGATE_THRESHOLD).
    /// </summary>
    public sealed class Cache
    {
        public readonly IGameDatabase database;

        List<JsObject> pvpAggregate = new();
        List<JsObject> totalExperience = new();
        readonly Dictionary<double, List<JsObject>> skillsExperience = new();
        readonly Dictionary<string, List<JsObject>> mobAggregates = new();

        // List of all mobs that can be checked in the leaderboards
        public readonly JsObject availableMobs = new();

        // Last time we aggregated the total experience.
        readonly Dictionary<string, double> lastAggregates = new();

        /// <param name="store">The database's store (database.ts: MongoDB for 'mongo'/'mongodb', otherwise none).</param>
        /// <param name="mobs">server/data/mobs.json.</param>
        public Cache(IDocumentStore store, JsObject mobs)
        {
            database = new GameDatabase(store);

            // If we are skipping the database, then we do not need to initialize anything.
            if (Config.Current.skipDatabase) return;

            database.OnFail(HandleFail);

            // Initialize the available mobs.
            foreach (var pair in mobs) availableMobs.Set(pair.Key, (pair.Value as JsObject)?["name"]);
        }

        /// <summary>If we fail to connect to the database we must abort.</summary>
        static void HandleFail(Exception error)
        {
            Log.Critical("Could not connect to the MongoDB server.");
            Log.Critical($"Error: {error.Message}");

            // Exit the process.
            Environment.Exit(1);
        }

        /// <summary>Gathers the aggregate data for pvp kills. The data is ordered in descending order.</summary>
        public void GetPvpData(Action<List<JsObject>> callback = null)
        {
            if (!CanAggregateData(Last("pvp")))
            {
                callback?.Invoke(pvpAggregate);
                return;
            }

            database.GetPvpAggregate(data =>
            {
                callback?.Invoke(pvpAggregate = data);

                // Update the last aggregate time.
                lastAggregates["pvp"] = Node.DateNow();
            });
        }

        /// <summary>Uses the aggregate data to update the cache or return the cached data if we have not reached the threshold.</summary>
        public void GetTotalExperience(Action<List<JsObject>> callback = null)
        {
            if (!CanAggregateData(Last("total")))
            {
                callback?.Invoke(totalExperience);
                return;
            }

            database.GetTotalExperienceAggregate(data =>
            {
                foreach (var info in data)
                    if (!JsData.Truthy(info["cheater"]))
                        info.Delete("cheater");

                callback?.Invoke(totalExperience = data);

                // Update the last aggregate time.
                lastAggregates["total"] = Node.DateNow();
            });
        }

        /// <summary>Aggregates data for a specific skill. Data for each skill is then stored in its respective dictionary.</summary>
        public void GetSkillsExperience(double skill, Action<List<JsObject>> callback)
        {
            var key = JsString.Of(skill);

            if (!CanAggregateData(Last(key)))
            {
                callback(skillsExperience.GetValueOrDefault(skill));
                return;
            }

            database.GetSkillAggregate(skill, data =>
            {
                foreach (var info in data)
                    if (!JsData.Truthy(info["cheater"]))
                        info.Delete("cheater");

                callback(skillsExperience[skill] = data);

                // Update the last aggregate time.
                lastAggregates[key] = Node.DateNow();
            });
        }

        /// <summary>Aggregates mob kill data for a specified mob key.</summary>
        public void GetMobKills(string key, Action<List<JsObject>> callback)
        {
            if (!CanAggregateData(Last(key)))
            {
                callback(mobAggregates.GetValueOrDefault(key));
                return;
            }

            database.GetMobAggregate(key, data =>
            {
                callback(mobAggregates[key] = data);

                // Update the last aggregate time.
                lastAggregates[key] = Node.DateNow();
            });
        }

        /// <summary>lastAggregates[key]: NaN when it was never set (undefined).</summary>
        double Last(string key) => lastAggregates.TryGetValue(key, out var time) ? time : double.NaN;

        /// <summary>
        /// Uses the last aggregate dictionary to determine whether the aggregate we are checking from the parameters
        /// has been updated recently.
        /// </summary>
        static bool CanAggregateData(double aggregate)
        {
            // Doesn't matter since there is no aggregate data.
            if (Config.Current.skipDatabase) return true;

            var lastAggregate = double.IsNaN(aggregate) ? 0 : aggregate;

            return Node.DateNow() - lastAggregate > Config.Current.aggregateThreshold;
        }
    }
}
