using PixelHero.Common;
using PixelHero.Server.Common;
using PixelHero.Server.Game;
using PixelHero.Server.Js;

namespace PixelHero.Server.Controllers
{
    /// <summary>controllers/events.ts: weekend events (double drops, 1.5x experience, double lumberjacking/mining).</summary>
    public sealed class Events
    {
        // The list of events to pick from (in the original's order).
        readonly string[] events = { "double drops", "1.5x experience", "lumberjacking", "mining" };

        public string activeEvent = "";

        // Store constants of the altered values.
        public double doubleDropProbability = Constants.DROP_PROBABILITY / 2.0;
        public double experiencePerHit = Constants.EXPERIENCE_PER_HIT * 1.5;

        readonly World world;

        public Events(World world)
        {
            this.world = world;

            Check();

            // Check the activity of the event every hour.
            Node.SetInterval(Check, Constants.EVENTS_CHECK_INTERVAL);
        }

        void Check()
        {
            var isWeekend = (int)Node.Date().DayOfWeek % 6 == 0;

            // Skip if it is weekend.
            if (!isWeekend)
            {
                Disable();
                return;
            }

            // An event is active so we stop here.
            if (JsData.Truthy(activeEvent)) return;

            // Pick an event based on the remainder of the week number.
            activeEvent = events[(int)(GetWeekNumber() % events.Length)];

            // Broadcast the event to all the players.
            world.GlobalMessage("WORLD", $"The {activeEvent} event has started.");

            // Enable the double harvesting.
            if (activeEvent == "lumberjacking") Utils.doubleLumberjacking = true;
            if (activeEvent == "mining") Utils.doubleMining = true;
        }

        void Disable()
        {
            if (JsData.Truthy(activeEvent)) world.GlobalMessage("WORLD", $"The {activeEvent} event has ended.");

            activeEvent = "";

            // Disable the double harvesting.
            Utils.doubleLumberjacking = false;
            Utils.doubleMining = false;
        }

        /// <summary>The week number: days since 1 January (local midnight) plus the weekday, over 7.</summary>
        double GetWeekNumber()
        {
            var current = Node.Date();
            var yearStart = new DateTimeOffset(current.Year, 1, 1, 0, 0, 0, Node.TimeZone.GetUtcOffset(new DateTime(current.Year, 1, 1)));
            var days = Math.Floor((current.ToUnixTimeMilliseconds() - yearStart.ToUnixTimeMilliseconds()) / 86_400_000.0);

            return Math.Ceiling(((int)current.DayOfWeek + days + 1) / 7);
        }

        public bool IsDoubleDrop() => activeEvent == "double drops";

        public bool IsIncreasedExperience() => activeEvent == "1.5x experience";
    }
}
