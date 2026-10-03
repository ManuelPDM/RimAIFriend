using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Stockpiles (STREAMLINE.md §7): code lays them out, the colony's first one and one in each new storeroom, for
    /// everything (vanilla's default stockpile). Haulers fill them on their own.
    /// </summary>
    public static class Stockpiles
    {
        public static bool CellOk(IntVec3 c, Map map)
        {
            if (!Ground.Open(c, map) || !c.Standable(map) || c.GetTerrain(map).IsWater || Ground.BesideDoor(c, map))
                return false;
            Room room = c.GetRoom(map);
            return !(Ground.Indoor(room) && !Ground.NoRole(room)); // not in someone's bedroom, the kitchen or the hospital
        }

        /// <summary>The best spot for the colony's first stockpile (3 up to 6 cells square), and where it is in words. False if none.</summary>
        public static bool FindSite(Pawn pawn, out CellRect rect, out string where)
        {
            Map map = pawn.Map;
            var zones = new ZoneSites(new ChoreScan(pawn), c => CellOk(c, map), c => CellScore(c, map));
            var site = zones.Find(new[] { 3, 6 }, 1).FirstOrDefault();
            rect = site?.Rect(site.maxSize) ?? CellRect.Empty;
            where = site != null ? zones.Where(rect, site.steps) : null;
            return site != null;
        }

        /// <summary>Roofed ground keeps things from weathering: a bonus.</summary>
        public static int CellScore(IntVec3 c, Map map) => c.Roofed(map) ? 100 : 40;

        public static string Place(Pawn pawn, CellRect rect, string where)
        {
            Map map = pawn.Map;
            string why = ZoneSites.Check(rect, map, c => CellOk(c, map));
            if (why != null)
                return $"Couldn't lay out the stockpile: {why}.";
            return PlaceCells(pawn, rect.Cells.ToList(), where, $"{rect.Width}×{rect.Height}");
        }

        /// <summary>
        /// Lays out the zone on cells already checked (a room's free floor, or a square), and records it as hers. With a
        /// filter, it takes only that, at the given priority.
        /// </summary>
        public static string PlaceCells(Pawn pawn, List<IntVec3> cells, string where, string size, ThingFilter filter = null,
                                        StoragePriority priority = StoragePriority.Normal, string what = "everything")
        {
            Map map = pawn.Map;
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            if (filter != null)
                zone.settings.filter.CopyAllowancesFrom(filter);
            zone.settings.Priority = priority;
            map.zoneManager.RegisterZone(zone);
            foreach (var c in cells)
                zone.AddCell(c);
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Stockpile, what);
            chore.zone = zone;
            chore.Remember($"I laid out a stockpile for {what} ({size}), {where}.", 3);
            ModLog.Message($"{pawn.LabelShort} laid out a stockpile for {what} ({size}), {where}.");
            return $"Laid out a stockpile for {what} ({size}), {where}.";
        }
    }
}
