using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// "Build test colony" (PHASE4.md §10): god-mode builds every trap of check 1 in a clear 30×22 area near the
    /// colonists, so a site-finder test run starts from the same layout each time. Save it once, then just load it.
    /// </summary>
    public static class TestColony
    {
        private const int Width = 30, Height = 22;

        public static void Build(Map map)
        {
            Pawn anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (anchor == null || !TryFindArea(map, anchor.Position, out IntVec3 o))
            {
                Messages.Message("No clear 30x22 area near the colonists.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            var area = new CellRect(o.x, o.z, Width, Height);
            foreach (var c in area)
            {
                foreach (var t in c.GetThingList(map).ToList())
                    if (t.def.category == ThingCategory.Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth)
                        t.Destroy(DestroyMode.Vanish);
                map.areaManager.Home[c] = true;
            }
            IntVec3 At(int x, int z) => new IntVec3(o.x + x, 0, o.z + z);
            CellRect Rect(int x0, int z0, int x1, int z1) => CellRect.FromLimits(o.x + x0, o.z + z0, o.x + x1, o.z + z1);

            // Stockpile with material, so the material list has stock.
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in Rect(0, 0, 5, 5))
                zone.AddCell(c);
            for (int i = 0; i < 4; i++)
                Stack(map, ThingDefOf.WoodLog, 75, At(i, 0));
            Stack(map, ThingDefOf.BlocksGranite, 75, At(0, 1));
            Stack(map, ThingDefOf.BlocksGranite, 5, At(1, 1));

            // A Plan the player drew.
            var plan = new Plan(ColorDefOf.PlanGray, map.planManager);
            foreach (var c in Rect(8, 1, 13, 6))
                plan.AddCell(c);

            // Rock, shallow water, rich soil, a cave patch under overhead mountain.
            ThingDef granite = DefDatabase<ThingDef>.GetNamed("Granite");
            foreach (var c in Rect(16, 1, 18, 3))
                GenSpawn.Spawn(ThingMaker.MakeThing(granite), c, map, WipeMode.Vanish);
            foreach (var c in Rect(16, 6, 19, 8))
                map.terrainGrid.SetTerrain(c, TerrainDefOf.WaterShallow);
            TerrainDef richSoil = DefDatabase<TerrainDef>.GetNamed("SoilRich");
            foreach (var c in Rect(20, 10, 27, 17))
                map.terrainGrid.SetTerrain(c, richSoil);
            foreach (var c in Rect(20, 0, 27, 7))
                map.roofGrid.SetRoof(c, RoofDefOf.RoofRockThick);

            // A two-door hallway (no role, 2 doors), a kitchen off it, and a one-door empty room sharing its wall.
            Room(map, Rect(0, 10, 13, 13), At(0, 11), At(13, 11));
            Room(map, Rect(2, 13, 8, 19), At(5, 13));
            Spawn(map, DefDatabase<ThingDef>.GetNamed("FueledStove"), At(5, 17), Rot4.North);
            Room(map, Rect(9, 13, 14, 18), At(11, 18));

            // An anima tree off to one side (Royalty), so its radius removes some sites.
            ThingDef anima = ModsConfig.RoyaltyActive ? DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima") : null;
            IntVec3 treeCell = At(Width + 30, Height / 2);
            if (anima != null && treeCell.InBounds(map) && !treeCell.InNoBuildEdgeArea(map))
            {
                foreach (var t in treeCell.GetThingList(map).ToList())
                    if (!(t is Pawn))
                        t.Destroy(DestroyMode.Vanish);
                map.terrainGrid.SetTerrain(treeCell, TerrainDefOf.Soil);
                var tree = (Plant)ThingMaker.MakeThing(anima);
                tree.Growth = 1f;
                GenSpawn.Spawn(tree, treeCell, map, WipeMode.Vanish);
            }

            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            ModLog.Message($"Test colony built at ({o.x},{o.z})-({o.x + Width - 1},{o.z + Height - 1}); anima tree {(anima != null ? $"at ({treeCell.x},{treeCell.z})" : "skipped")}.");
            Messages.Message("Test colony built. Save it, e.g. as aipc_build_test.", MessageTypeDefOf.PositiveEvent, false);
        }

        /// <summary>A clear area (plus a 2-cell margin) near a cell: in bounds, visible, heavy ground, no buildings or zones.</summary>
        private static bool TryFindArea(Map map, IntVec3 near, out IntVec3 origin)
        {
            for (int radius = 0; radius <= 60; radius += 2)
                for (int dx = -radius; dx <= radius; dx += 2)
                    for (int dz = -radius; dz <= radius; dz += 2)
                    {
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dz)) != radius)
                            continue;
                        origin = new IntVec3(near.x + dx - Width / 2, 0, near.z + dz - Height / 2);
                        if (Clear(map, new CellRect(origin.x, origin.z, Width, Height).ExpandedBy(2)))
                            return true;
                    }
            origin = IntVec3.Invalid;
            return false;
        }

        private static bool Clear(Map map, CellRect rect)
        {
            foreach (var c in rect)
            {
                if (!c.InBounds(map) || c.InNoBuildEdgeArea(map) || c.Fogged(map) || !c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy)
                    || map.zoneManager.ZoneAt(c) != null || c.GetRoof(map) != null)
                    return false;
                foreach (var t in c.GetThingList(map))
                    if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame || t is Pawn)
                        return false;
            }
            return true;
        }

        private static void Room(Map map, CellRect rect, params IntVec3[] doors)
        {
            foreach (var c in rect.EdgeCells)
            {
                if (c.GetEdifice(map) != null && !doors.Contains(c))
                    continue; // a wall shared with a room built earlier
                Spawn(map, doors.Contains(c) ? ThingDefOf.Door : ThingDefOf.Wall, c, Rot4.North);
            }
            foreach (var c in rect.ContractedBy(1))
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
        }

        private static void Spawn(Map map, ThingDef def, IntVec3 c, Rot4 rot)
        {
            Thing t = ThingMaker.MakeThing(def, RoomPlan.StuffFor(def, ThingDefOf.BlocksGranite));
            t.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(t, c, map, rot, WipeMode.Vanish);
        }

        private static void Stack(Map map, ThingDef def, int count, IntVec3 c)
        {
            Thing t = ThingMaker.MakeThing(def);
            t.stackCount = count;
            GenSpawn.Spawn(t, c, map, WipeMode.Vanish);
        }
    }
}
