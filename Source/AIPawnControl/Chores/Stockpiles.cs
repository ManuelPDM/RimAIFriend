using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Stockpiles (PHASE5.md §4.7): "make a stockpile" (the Colony call scans for sites, outdoors or in a room with no role,
    /// roofed a bonus, and asks what it holds, the size and the site). Haulers fill it on their own.
    /// </summary>
    public static class Stockpiles
    {
        public static readonly int[] Sizes = { 3, 5, 7 };
        public static readonly string[] Kinds = { "everything", "food", "materials", "weapons and clothes", "dump for corpses and chunks" };

        public static string SizeName(int size) => Fields.SizeNames[System.Array.IndexOf(Sizes, size)];

        public static IEnumerable<ChoreOption> Options(ChoreScan scan)
        {
            if (!scan.SomeoneCanDo(WorkTypeDefOf.Hauling))
                yield break;
            bool none = scan.map.haulDestinationManager.AllGroupsListForReading.Count == 0;
            yield return new ChoreOption
            {
                kind = Chore.Kind.Stockpile,
                label = "make a stockpile",
                useful = none ? 3f : 0.3f,
                needs = ChoreNeeds.Stockpile,
                check = () => null, // the Colony call scans for sites; its reply is validated when placed
                apply = (mind, choice) =>
                {
                    int size = System.Math.Min(Sizes[choice.size], choice.site.maxSize);
                    var rect = choice.site.Rect(size);
                    string result = Place(mind.pawn, rect, choice.holds, choice.finder.Where(rect, choice.site.steps));
                    return size < Sizes[choice.size] ? result + $" Only {SizeName(size)} fit there." : result;
                },
            };
        }

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
            var preset = kind.StartsWith("dump") ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile;
            var zone = new Zone_Stockpile(preset, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in rect)
                zone.AddCell(c);
            SetFilter(zone, kind);
            var chore = ChoreManager.Instance.Add(pawn, Chore.Kind.Stockpile, kind);
            chore.zone = zone;
            string size = $"{rect.Width}×{rect.Height}";
            chore.Remember($"I laid out a stockpile for {kind} ({size}), {where}.", 3);
            ModLog.Message($"{pawn.LabelShort} laid out a stockpile for {kind} ({size}) at {rect}, {where}.");
            return $"Laid out a stockpile for {kind} ({size}), {where}.";
        }
    }
}
