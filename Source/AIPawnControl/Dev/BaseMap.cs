using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The whole base as text (BASE_GROWTH.md, build checks), in base-map.txt: one map of what stands where, one of which
    /// room each cell belongs to, and the rooms with their role, size and doors. For checking a grown base's shape without
    /// screenshots. North is up.
    /// </summary>
    public static class BaseMap
    {
        private const string Legend = "# wall, D door, B bed, H table, h seat, S shelf, W work table, C cooler/heater, L light, F other furniture, "
            + "s stockpile, . indoor floor, , outdoors, g growing zone, t tree, ^ rock, ~ water, X other building, + blueprint or frame";

        [DebugAction(DevTools.Category, "Base map", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BaseMapAction() => ModLog.Message("Base map: " + Write(Find.CurrentMap));

        /// <summary>Writes base-map.txt and returns its path.</summary>
        public static string Write(Map map)
        {
            var buildings = map.listerBuildings.allBuildingsColonist.Where(b => b.def.IsEdifice() || b.def.building != null).ToList();
            if (buildings.Count == 0)
                return "no colony buildings";
            CellRect area = CellRect.FromCellList(buildings.Select(b => b.Position)).ExpandedBy(3).ClipInsideMap(map);
            var rooms = map.regionGrid.AllRooms.Where(r => Ground.Indoor(r) && r.ProperRoom && area.Contains(r.Cells.First()))
                .OrderBy(r => r.Cells.Min(c => -c.z * 1000 + c.x)).ToList();
            var letter = new Dictionary<Room, char>();
            for (int i = 0; i < rooms.Count; i++)
                letter[rooms[i]] = (char)(i < 26 ? 'A' + i : 'a' + (i - 26) % 26);

            var sb = new StringBuilder($"Base map, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, area ({area.minX},{area.minZ})-({area.maxX},{area.maxZ}); {Layout.Line(map)}\n");
            sb.AppendLine("[Colony] Base: " + Ladder.Line(map));
            sb.AppendLine($"Heart: {(SiteFinder.Heart(map) is Room heart ? Layout.Name(heart) : "none")}, centre {SiteFinder.BaseCenter(map)}");
            sb.AppendLine("\n== Things (" + Legend + ")");
            Draw(sb, area, c => Symbol(c, map));
            sb.AppendLine("\n== Rooms (a letter per room, # wall, D door)");
            Draw(sb, area, c =>
            {
                Building e = c.GetEdifice(map);
                if (e is Building_Door) return 'D';
                if (e != null && e.def.IsWall) return '#';
                Room r = c.GetRoom(map);
                return r != null && letter.TryGetValue(r, out char l) ? l : e != null ? 'X' : ',';
            });
            // What vanilla says of the outdoor ground at the area's corner: the open-air rule rests on it.
            Room corner = area.EdgeCells.Where(c => c.Walkable(map)).Select(c => c.GetRoom(map)).FirstOrDefault(r => r != null && !r.IsDoorway);
            if (corner != null)
                sb.AppendLine($"Outdoors at the area's edge: {corner.CellCount} cells, {corner.RegionCount} regions, {corner.Districts.Count} districts, touches the map edge {corner.TouchesMapEdge}, "
                              + $"outdoor temperature {corner.UsesOutdoorTemperature}, psychologically outdoors {corner.PsychologicallyOutdoors}, open air {Ground.OpenAir(corner)}");
            // Every room must be walked out of to open air that reaches the map edge (doors pass).
            var openAir = new Dictionary<Room, bool>();
            var outside = Flood.Run(Flood.All(map), area.EdgeCells.Where(c => c.Walkable(map) && Ground.OpenAir(c.GetRoom(map), openAir)), c => c.Walkable(map));
            var sealedRooms = rooms.Where(r => !r.Cells.Any(outside.Has)).ToList();
            sb.AppendLine("\n== Rooms" + (sealedRooms.Count > 0 ? $" (SEALED, no way out: {string.Join(", ", sealedRooms.Select(r => letter[r]))})" : " (all reach the open air)"));
            foreach (var r in rooms)
            {
                var bounds = CellRect.FromCellList(r.Cells);
                bool rect = bounds.Area == r.CellCount;
                int doors = r.ContainedAndAdjacentThings.OfType<Building_Door>().Distinct().Count();
                sb.AppendLine($"  {letter[r]}: {Layout.Name(r)}, {(rect ? $"{bounds.Width}x{bounds.Height}" : $"{r.CellCount} cells, odd-shaped")}, {doors} doors, "
                              + $"{(Layout.WalkThrough(r) ? "walk-through" : "end room")}, {BuildManager.Impressiveness(r)}, {r.Temperature:0}°C");
            }
            sb.AppendLine("\n== Storage (priority, takes food, takes wood, cells)");
            foreach (var group in map.haulDestinationManager.AllGroupsListForReading)
            {
                var s = group.Settings;
                Room r = group.CellsList.FirstOrDefault().GetRoom(map);
                sb.AppendLine($"  {group.parent.SlotYielderLabel()} in {(r != null && letter.TryGetValue(r, out char l) ? l.ToString() : "the open")}: {s.Priority}, "
                              + $"food {s.filter.Allows(ThingDefOf.MealSimple)}, wood {s.filter.Allows(ThingDefOf.WoodLog)}, {group.CellsList.Count} cells");
            }
            var power = map.powerNetManager.AllNetsListForReading;
            sb.AppendLine($"\n== Power: {power.Count} nets, spare {power.Sum(n => n.CurrentEnergyGainRate()) / CompPower.WattsToWattDaysPerTick:0} W");
            foreach (var control in map.listerBuildings.allBuildingsColonist.Select(b => b.TryGetComp<CompTempControl>()).Where(c => c != null))
                sb.AppendLine($"  {control.parent.def.label} at {control.parent.Position}: target {control.targetTemperature:0}°C, powered {control.parent.TryGetComp<CompPowerTrader>()?.PowerOn}");
            var asks = AskedFor.Current(map);
            sb.AppendLine("\n== Asked for: " +(asks.Count > 0 ? string.Join("; ", asks.Select(a => $"{a.kind.label} ({a.why})")) : "nothing")
                          + (AskedFor.Dropped.Count > 0 ? " | not offered: " + string.Join("; ", AskedFor.Dropped) : ""));
            string path = Path.Combine(DevTools.Folder, "base-map.txt");
            DevTools.WriteFile("base-map.txt", sb.ToString());
            return path;
        }

        private static void Draw(StringBuilder sb, CellRect area, Func<IntVec3, char> symbol)
        {
            for (int z = area.maxZ; z >= area.minZ; z--)
            {
                sb.Append(z.ToString().PadLeft(4)).Append(' ');
                for (int x = area.minX; x <= area.maxX; x++)
                    sb.Append(symbol(new IntVec3(x, 0, z)));
                sb.AppendLine();
            }
            sb.AppendLine($"     x from {area.minX}");
        }

        private static char Symbol(IntVec3 c, Map map)
        {
            var things = c.GetThingList(map);
            if (things.Any(t => t is Blueprint || t is Frame))
                return '+';
            Building e = c.GetEdifice(map);
            if (e is Building_Door) return 'D';
            if (e != null && e.def.IsWall) return '#';
            if (e != null && e.def.building.isNaturalRock) return '^';
            foreach (var t in things.OfType<Building>())
            {
                var def = t.def;
                if (def.IsBed) return 'B';
                if (def.surfaceType == SurfaceType.Eat) return 'H';
                if (def.building.isSittable) return 'h';
                if (t is Building_Storage) return 'S';
                if (t is IBillGiver) return 'W';
                if (t.TryGetComp<CompTempControl>() != null || t.TryGetComp<CompHeatPusher>() != null) return 'C';
                if (t.TryGetComp<CompGlower>() != null) return 'L';
                if (def.category == ThingCategory.Building && t.Faction == Faction.OfPlayer) return 'F';
            }
            if (e != null) return 'X';
            if (things.Any(t => t.def.category == ThingCategory.Plant && t.def.plant.IsTree)) return 't';
            Zone zone = map.zoneManager.ZoneAt(c);
            if (zone is Zone_Stockpile) return 's';
            if (zone is Zone_Growing) return 'g';
            if (c.GetTerrain(map).IsWater) return '~';
            return Ground.Indoor(c.GetRoom(map)) ? '.' : ',';
        }
    }
}
