using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Stockpiles (STREAMLINE.md §7): code lays them out, the colony's first one and one in each new kitchen and storeroom.
    /// Haulers fill them on their own.
    /// </summary>
    public static class Stockpiles
    {
        public static bool CellOk(IntVec3 c, Map map)
        {
            if (!c.InBounds(map) || c.InNoBuildEdgeArea(map) || c.Fogged(map) || !c.Standable(map) || c.GetTerrain(map).IsWater)
                return false;
            if (map.zoneManager.ZoneAt(c) != null || map.planManager.PlanAt(c) != null)
                return false;
            foreach (var t in c.GetThingList(map))
                if (t is Blueprint || t is Frame || t.def.category == ThingCategory.Building)
                    return false;
            Room room = c.GetRoom(map);
            if (room != null && !room.PsychologicallyOutdoors && !room.IsDoorway && !SiteFinder.NoRole(room))
                return false; // not in someone's bedroom, the kitchen or the hospital
            for (int r = 0; r < 4; r++)
            {
                var n = c + new Rot4(r).FacingCell;
                if (n.InBounds(map) && n.GetEdifice(map) is Building_Door)
                    return false;
            }
            return true;
        }

        /// <summary>Roofed ground keeps things from weathering: a bonus.</summary>
        public static int CellScore(IntVec3 c, Map map) => c.Roofed(map) ? 100 : 40;

        public static void SetFilter(Zone_Stockpile zone, string kind)
        {
            var filter = zone.settings.filter;
            switch (kind)
            {
                case "food":
                    filter.SetDisallowAll();
                    filter.SetAllow(ThingCategoryDefOf.Foods, true);
                    break;
                case "materials":
                    filter.SetDisallowAll();
                    filter.SetAllow(ThingCategoryDefOf.ResourcesRaw, true);
                    filter.SetAllow(ThingCategoryDefOf.Manufactured, true);
                    break;
                case "weapons and clothes":
                    filter.SetDisallowAll();
                    filter.SetAllow(ThingCategoryDefOf.Weapons, true);
                    filter.SetAllow(ThingCategoryDefOf.Apparel, true);
                    break;
            }
        }

        public static string Place(Pawn pawn, CellRect rect, string kind, string where)
        {
            Map map = pawn.Map;
            string why = ZoneSites.Check(rect, map, c => CellOk(c, map));
            if (why != null)
                return $"Couldn't lay out the stockpile: {why}.";
            return PlaceCells(pawn, rect.Cells.ToList(), kind, where, $"{rect.Width}×{rect.Height}");
        }

        /// <summary>Lays out the zone on cells already checked (a room's free floor, or a square), and records it as hers.</summary>
        public static string PlaceCells(Pawn pawn, List<IntVec3> cells, string kind, string where, string size)
        {
            Map map = pawn.Map;
            var preset = kind.StartsWith("dump") ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile;
            var zone = new Zone_Stockpile(preset, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in cells)
                zone.AddCell(c);
            SetFilter(zone, kind);
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Stockpile, kind);
            chore.zone = zone;
            chore.Remember($"I laid out a stockpile for {kind} ({size}), {where}.", 3);
            ModLog.Message($"{pawn.LabelShort} laid out a stockpile for {kind} ({size}), {where}.");
            return $"Laid out a stockpile for {kind} ({size}), {where}.";
        }
    }
}
