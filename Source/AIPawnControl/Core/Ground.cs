using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// What a cell or room is, in one place: every site finder builds on these, so a new rule (a new DLC's no-build thing)
    /// is added once.
    /// </summary>
    public static class Ground
    {
        /// <summary>
        /// Open ground for anything we lay out (a room, a field, a stockpile): on the map, outside the no-build edge, seen,
        /// no zone or Plan, and nothing built, planned or being built. Callers add their own rules on top.
        /// </summary>
        public static bool Open(IntVec3 c, Map map)
        {
            if (!c.InBounds(map) || c.InNoBuildEdgeArea(map) || c.Fogged(map)
                || map.zoneManager.ZoneAt(c) != null || map.planManager.PlanAt(c) != null)
                return false;
            foreach (var t in c.GetThingList(map))
                if (t is Blueprint || t is Frame || t.def.category == ThingCategory.Building)
                    return false;
            return true;
        }

        /// <summary>A door is right next to it (4 neighbours): keep the way through a door clear.</summary>
        public static bool BesideDoor(IntVec3 c, Map map)
        {
            foreach (var d in GenAdj.CardinalDirections)
                if ((c + d).InBounds(map) && (c + d).GetEdifice(map) is Building_Door)
                    return true;
            return false;
        }

        /// <summary>
        /// A room with a roof over it that keeps the weather out: not a doorway, not outdoors by vanilla's temperature rule
        /// (a quarter of the roof open, or touching the map edge) or by its mood rule. A room still being roofed isn't.
        /// </summary>
        public static bool Indoor(Room room) =>
            room != null && !room.IsDoorway && !room.UsesOutdoorTemperature && !room.PsychologicallyOutdoors;

        /// <summary>Outdoor ground (a doorway is neither indoors nor out).</summary>
        public static bool Outdoors(Room room) => room != null && !room.IsDoorway && !Indoor(room);

        /// <summary>No role of its own: vanilla gives a proper room with nothing role-defining the generic "Room" role, and others None.</summary>
        public static bool NoRole(Room room) => room.Role == null || room.Role == RoomRoleDefOf.None || room.Role.defName == "Room";

        /// <summary>Vanilla gives it some role, the generic "Room" included (only None and no role at all don't count).</summary>
        public static bool AnyRole(Room room) => room.Role != null && room.Role != RoomRoleDefOf.None;
    }
}
