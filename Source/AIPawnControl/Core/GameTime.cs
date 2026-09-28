using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Game time in the map's local time: day numbers, nights, clock and day labels.</summary>
    public static class GameTime
    {
        private static float Longitude(Map map) => map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;

        /// <summary>The local day number (year × days per year + day of year) of a game tick.</summary>
        public static int Day(int tick, Map map)
        {
            long abs = GenDate.TickGameToAbs(tick);
            float longitude = Longitude(map);
            return GenDate.Year(abs, longitude) * GenDate.DaysPerYear + GenDate.DayOfYear(abs, longitude);
        }

        public static int Today(Map map) => Day(Find.TickManager.TicksGame, map);

        /// <summary>The night that began most recently at 22:00, as a local day number.</summary>
        public static int Night(Map map) => GenLocalDate.HourOfDay(map) >= 22 ? Today(map) : Today(map) - 1;

        /// <summary>"Today", "Yesterday" or "3 days ago".</summary>
        public static string DayLabel(int tick, Map map)
        {
            int days = Today(map) - Day(tick, map);
            return days <= 0 ? "Today" : days == 1 ? "Yesterday" : $"{days} days ago";
        }

        /// <summary>"14:05".</summary>
        public static string Clock(int tick, Map map)
        {
            float hour = GenDate.HourFloat(GenDate.TickGameToAbs(tick), Longitude(map));
            return $"{(int)hour:00}:{(int)(hour % 1f * 60f):00}";
        }

        /// <summary>The full date and clock now: "5th of Aprimay, 5500, 14:05".</summary>
        public static string Now(Map map) =>
            $"{GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile))}, {Clock(Find.TickManager.TicksGame, map)}";
    }
}
