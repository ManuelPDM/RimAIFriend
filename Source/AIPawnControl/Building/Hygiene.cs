using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Dubs Bad Hygiene's fixtures (HYGIENE.md): which ones need privacy (a stall, and a room nobody walks through) and what
    /// plumbing they need. DBH's own defNames; without DBH none of them exist and every check here passes. Plumbing is the
    /// player's, like power: a fixture is only offered once what it needs is built.
    /// </summary>
    public static class Hygiene
    {
        /// <summary>DBH checks privacy here (PrivacyUtil): toilets against anyone in sight, showers and baths against the other sex.</summary>
        private static readonly HashSet<string> Private = new HashSet<string>
            { "PitLatrine", "ToiletStuff", "ToiletAdvStuff", "ToiletSpacer", "ShowerSimple", "ShowerStuff", "ShowerAdvStuff", "BathtubStuff" };
        private static readonly HashSet<string> NeedSewage = new HashSet<string> { "ToiletStuff", "ToiletAdvStuff", "ToiletSpacer", "BasinStuff", "Fountain", "KitchenSink" };
        private static readonly HashSet<string> NeedWater = new HashSet<string>
            { "ToiletStuff", "ToiletAdvStuff", "ToiletSpacer", "ShowerSimple", "ShowerStuff", "ShowerAdvStuff", "BathtubStuff", "BasinStuff", "Fountain", "KitchenSink" };
        private static readonly string[] SewageSinks = { "SewageOutlet", "SewageSepticTank", "SewageTreatment" };
        private static readonly string[] WaterStores = { "WaterButt", "WaterTowerS", "WaterTowerL", "WaterTankSmall" };
        private const string WashBucket = "WashBucket", PrimitiveWell = "PrimitiveWell";

        public static ThingDef StallDoor => DefDatabase<ThingDef>.GetNamedSilentFail("ToiletStallDoor");

        /// <summary>A toilet, shower or bath: it goes in a stall, and its room isn't walked through.</summary>
        public static bool NeedsPrivacy(ThingDef d) => Private.Contains(d.defName);

        /// <summary>
        /// Part of the plumbing itself (a pipe, valve, well, tower, pump, outlet, boiler, radiator): on DBH's pipe network and
        /// not one of the fixtures above. The player's, like power conduits, so never offered.
        /// </summary>
        public static bool IsPlumbing(ThingDef d) =>
            d.comps.Any(c => c.GetType().FullName == "DubsBadHygiene.CompProperties_Pipe")
            && !Private.Contains(d.defName) && !NeedSewage.Contains(d.defName) && !NeedWater.Contains(d.defName);

        /// <summary>
        /// What it needs to work is on the map: a sewage outlet or tank for a toilet or basin, a water butt, tower or tank for
        /// anything that draws water. A water tub is refilled by hand from clean water (DBH's own clean-water area), a well
        /// or a water butt.
        /// </summary>
        public static bool Plumbed(ThingDef d, Map map)
        {
            if (d.defName == WashBucket)
                return Any(map, WaterStores.Append(PrimitiveWell)) || map.areaManager.AllAreas.Any(a => a.GetType().Name == "Area_WaterClean" && a.TrueCount > 0);
            return (!NeedSewage.Contains(d.defName) || Any(map, SewageSinks)) && (!NeedWater.Contains(d.defName) || Any(map, WaterStores));
        }

        private static bool Any(Map map, IEnumerable<string> defNames) =>
            defNames.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n)).Any(d => d != null && map.listerThings.ThingsOfDef(d).Any(t => t.Faction == Faction.OfPlayer));
    }
}
