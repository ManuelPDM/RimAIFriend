using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The base ladder (STREAMLINE.md §5.1): what an early colony should have, in order, each read from the map. The first
    /// unmet rung is "next for the base". A rung with a project underway counts as met; a rung that can't be built waits
    /// and says why. It never skips.
    /// </summary>
    public static class Ladder
    {
        public class Rung
        {
            public string label;       // "a kitchen"
            public RoomKindDef kind;   // what to build for it; null past the last rung
            public bool met;
            public bool underway;      // someone's project is on it
            public string waiting;     // why it can't be built now, or null
            public bool rooms;         // the last rung: better rooms, the worst one first; never met
        }

        /// <summary>Every rung and its verdict, in order.</summary>
        public static List<Rung> Evaluate(Map map)
        {
            var buildings = map.listerBuildings.allBuildingsColonist;
            bool Indoors(Thing t) => t.GetRoom() is Room r && !r.PsychologicallyOutdoors;
            var sleepers = Sleepers(map);
            int slots = BedSlots(map);
            int gap = sleepers.Count - slots;

            var rungs = new List<Rung>
            {
                new Rung { label = gap == 1 ? "a bed for everyone" : "beds for everyone", kind = Kind(gap == 1 ? "AIPC_Bedroom" : "AIPC_Barracks"), met = gap <= 0 },
                new Rung { label = "a kitchen", kind = Kind("AIPC_Kitchen"), met = buildings.Any(b => b.def.building != null && b.def.building.isMealSource && Indoors(b)) },
                new Rung { label = "a storeroom", kind = Kind("AIPC_Storeroom"), met = HasRole(map, DefDatabase<RoomRoleDef>.GetNamedSilentFail("Storeroom")) },
                new Rung { label = "a dining room", kind = Kind("AIPC_DiningRoom"), met = buildings.Any(b => b.def.surfaceType == SurfaceType.Eat && Indoors(b)) },
                new Rung { label = "a workshop", kind = Kind("AIPC_Workshop"), met = HasRole(map, DefDatabase<RoomRoleDef>.GetNamedSilentFail("Workshop")) },
                new Rung { label = "a hospital", kind = Kind("AIPC_Hospital"), met = buildings.OfType<Building_Bed>().Any(b => b.Medical && b.def.building.bed_humanlike) },
                new Rung { label = "private bedrooms", kind = Kind("AIPC_Bedroom"), met = sleepers.All(p => p.ownership?.OwnedRoom != null) },
                new Rung { label = "better rooms", rooms = true },
            };
            var active = BuildManager.Instance?.ActiveOn(map).Select(p => p.kindDef).ToList() ?? new List<RoomKindDef>();
            foreach (var rung in rungs)
            {
                if (rung.met || rung.rooms)
                    continue;
                // Beds are underway with any bedroom or barracks project, the rest with a project of their own kind.
                rung.underway = rung.kind != null && active.Any(k => k == rung.kind || (rungs.IndexOf(rung) == 0 && (k?.defName == "AIPC_Bedroom" || k?.defName == "AIPC_Barracks")));
                rung.waiting = rung.kind == null ? "no such room kind" : Waiting(rung.kind, map);
            }
            return rungs;
        }

        /// <summary>The first rung that's neither met nor underway; past the early base, better rooms.</summary>
        public static Rung Current(Map map) => Evaluate(map).FirstOrDefault(r => !r.met && !r.underway);

        /// <summary>For [Colony]: "beds for 6 of 3 (barracks) · next: a dining room", or "(waiting on research: …)".</summary>
        public static string Line(Map map)
        {
            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(IsColonistBed).ToList();
            var rooms = beds.Select(b => b.GetRoom()).Where(r => r != null && !r.PsychologicallyOutdoors)
                .Select(r => r.GetRoomRoleLabel()).Distinct().ToList();
            string line = $"beds for {BedSlots(map)} of {Sleepers(map).Count}" + (rooms.Count > 0 ? $" ({string.Join(", ", rooms)})" : "")
                          + " · " + Layout.Line(map);
            var rungs = Evaluate(map);
            var next = rungs.FirstOrDefault(r => !r.met && !r.underway);
            var underway = rungs.Where(r => !r.met && r.underway).Select(r => r.label).ToList();
            if (underway.Count > 0)
                line += " · being built: " + string.Join(", ", underway);
            if (next == null)
                return line;
            return line + $" · next: {next.label}" + (next.waiting != null ? $" ({next.waiting})" : "");
        }

        /// <summary>Why a kind can't be built now: its items wait on research (named), or walls can't be built. Null if it can.</summary>
        public static string Waiting(RoomKindDef kind, Map map)
        {
            if (kind.BuildableNow(map))
                return null;
            var research = kind.items.Where(i => !i.optional && i.Resolve(map) == null)
                .SelectMany(i => i.defs.Where(d => d != null).SelectMany(d => d.researchPrerequisites ?? new List<ResearchProjectDef>()))
                .Where(r => !r.IsFinished).Select(r => r.label).Distinct().ToList();
            return research.Count > 0 ? "waiting on research: " + string.Join(" or ", research) : "waiting: it can't be built yet";
        }

        private static RoomKindDef Kind(string defName) => DefDatabase<RoomKindDef>.GetNamedSilentFail(defName);

        /// <summary>Colonists who need a bed, counted as vanilla's "Need colonist beds" alert does.</summary>
        public static List<Pawn> Sleepers(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => !p.IsSlave && p.needs?.rest != null && !p.DevelopmentalStage.Baby()).ToList();

        private static bool IsColonistBed(Building_Bed b) => b.ForColonists && !b.Medical && b.def.building.bed_humanlike && !b.ForHumanBabies;

        public static int BedSlots(Map map) => map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(IsColonistBed).Sum(b => b.SleepingSlotsCount);

        private static bool HasRole(Map map, RoomRoleDef role) =>
            role != null && map.regionGrid.AllRooms.Any(r => r.Role == role && !r.PsychologicallyOutdoors);
    }
}
