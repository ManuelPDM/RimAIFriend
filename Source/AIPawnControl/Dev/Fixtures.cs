using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Test setups built by god mode in a clear area near the colonists, so a check starts from the same layout on any
    /// map instead of from a save. Each returns what it built, or why not.
    /// </summary>
    public static class Fixtures
    {
        /// <summary>
        /// Every trap of the site finder (PHASE4.md §10): a stockpile with material, a player's Plan, rock, shallow water,
        /// rich soil, overhead mountain, a two-door hallway with a kitchen off it, a one-door room sharing its wall, and
        /// the anima tree (Royalty) off to one side.
        /// </summary>
        public static string MixedGround(Map map)
        {
            if (!TryFindArea(map, 30, 22, out IntVec3 o))
                return "No clear 30x22 area near the colonists.";
            IntVec3 At(int x, int z) => new IntVec3(o.x + x, 0, o.z + z);
            CellRect Rect(int x0, int z0, int x1, int z1) => CellRect.FromLimits(o.x + x0, o.z + z0, o.x + x1, o.z + z1);
            Clear(map, new CellRect(o.x, o.z, 30, 22));

            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(zone);
            foreach (var c in Rect(0, 0, 5, 5))
                zone.AddCell(c);
            for (int i = 0; i < 4; i++)
                Stack(map, ThingDefOf.WoodLog, 75, At(i, 0));
            Stack(map, ThingDefOf.BlocksGranite, 75, At(0, 1));
            Stack(map, ThingDefOf.BlocksGranite, 5, At(1, 1));

            var plan = new Plan(ColorDefOf.PlanGray, map.planManager);
            foreach (var c in Rect(8, 1, 13, 6))
                plan.AddCell(c);

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

            Room(map, Rect(0, 10, 13, 13), At(0, 11), At(13, 11));
            Room(map, Rect(2, 13, 8, 19), At(5, 13));
            Spawn(map, DefDatabase<ThingDef>.GetNamed("FueledStove"), At(5, 17), Rot4.North);
            Room(map, Rect(9, 13, 14, 18), At(11, 18));

            ThingDef anima = ModsConfig.RoyaltyActive ? DefDatabase<ThingDef>.GetNamedSilentFail("Plant_TreeAnima") : null;
            IntVec3 treeCell = At(60, 11);
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
            return Done(map, $"Mixed ground at ({o.x},{o.z})-({o.x + 29},{o.z + 21}); anima tree {(anima != null ? $"at ({treeCell.x},{treeCell.z})" : "skipped")}.");
        }

        /// <summary>
        /// A street: two rows of four 5×5 bedrooms facing each other across a 3-wide street, every door opening onto it
        /// (the case a hall along the street is for; was the save aipc_layout_boxes).
        /// </summary>
        public static string StreetBase(Map map)
        {
            const int Box = 7, Rows = 4, Street = 3;
            int width = Rows * (Box - 1) + 1, height = 2 * Box + Street;
            if (!TryFindArea(map, width, height, out IntVec3 o))
                return $"No clear {width}x{height} area near the colonists.";
            Clear(map, new CellRect(o.x, o.z, width, height));
            for (int row = 0; row < 2; row++)
                for (int i = 0; i < Rows; i++)
                {
                    int x0 = o.x + i * (Box - 1), z0 = row == 0 ? o.z : o.z + Box + Street;
                    var rect = new CellRect(x0, z0, Box, Box);
                    // South row: doors on its north wall; north row: on its south wall. Both face the street.
                    var door = new IntVec3(x0 + Box / 2, 0, row == 0 ? rect.maxZ : rect.minZ);
                    Room(map, rect, door);
                    Spawn(map, ThingDefOf.Bed, new IntVec3(x0 + 1, 0, row == 0 ? z0 + 1 : rect.maxZ - 1), row == 0 ? Rot4.North : Rot4.South);
                }
            return Done(map, $"Street base at ({o.x},{o.z}): 8 bedrooms, 8 doors onto a {Street}-wide street.");
        }

        /// <summary>
        /// A run-17-style base: a barracks, a dining room and a kitchen in a row, doors south, with a rice field and a
        /// stockpile right across the doors. No hub fits (zones are never built on); a door between the barracks and the
        /// dining room does (was the save aipc_run17_day14).
        /// </summary>
        public static string FieldsAtTheDoors(Map map)
        {
            const int Box = 7;
            int width = 3 * (Box - 1) + 1, height = Box + 6;
            if (!TryFindArea(map, width, height, out IntVec3 o))
                return $"No clear {width}x{height} area near the colonists.";
            Clear(map, new CellRect(o.x, o.z, width, height));
            int zRooms = o.z + 6;
            var rects = Enumerable.Range(0, 3).Select(i => new CellRect(o.x + i * (Box - 1), zRooms, Box, Box)).ToList();
            foreach (var rect in rects)
                Room(map, rect, new IntVec3(rect.minX + Box / 2, 0, rect.minZ));
            // Barracks, dining room (walk-through, beside the barracks), kitchen.
            Spawn(map, ThingDefOf.Bed, new IntVec3(rects[0].minX + 1, 0, rects[0].maxZ - 1), Rot4.South);
            Spawn(map, ThingDefOf.Bed, new IntVec3(rects[0].minX + 2, 0, rects[0].maxZ - 1), Rot4.South);
            Spawn(map, DefDatabase<ThingDef>.GetNamed("Table2x2c"), new IntVec3(rects[1].minX + 3, 0, rects[1].minZ + 3), Rot4.North);
            Spawn(map, DefDatabase<ThingDef>.GetNamed("FueledStove"), new IntVec3(rects[2].minX + 3, 0, rects[2].maxZ - 1), Rot4.South);

            var field = new Zone_Growing(map.zoneManager);
            map.zoneManager.RegisterZone(field);
            foreach (var c in new CellRect(o.x, o.z, width - 6, 5))
                if (c.GetTerrain(map).fertility > 0f)
                    field.AddCell(c);
            if (DefDatabase<ThingDef>.GetNamedSilentFail("Plant_Rice") is ThingDef rice)
                field.SetPlantDefToGrow(rice);
            var pile = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            map.zoneManager.RegisterZone(pile);
            foreach (var c in new CellRect(o.x + width - 6, o.z, 6, 5))
                pile.AddCell(c);
            return Done(map, $"Fields at the doors at ({o.x},{o.z}): barracks, dining room and kitchen, a field and a stockpile across their doors.");
        }

        private static string Done(Map map, string what)
        {
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            ModLog.Message(what);
            return what;
        }

        /// <summary>A clear area (plus a 2-cell margin) near the colonists: in bounds, visible, heavy ground, no buildings, zones or roof.</summary>
        private static bool TryFindArea(Map map, int width, int height, out IntVec3 origin)
        {
            origin = IntVec3.Invalid;
            Pawn anchor = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
            if (anchor == null)
                return false;
            IntVec3 near = anchor.Position;
            for (int radius = 0; radius <= 60; radius += 2)
                for (int dx = -radius; dx <= radius; dx += 2)
                    for (int dz = -radius; dz <= radius; dz += 2)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != radius)
                            continue;
                        origin = new IntVec3(near.x + dx - width / 2, 0, near.z + dz - height / 2);
                        if (IsClear(map, new CellRect(origin.x, origin.z, width, height).ExpandedBy(2)))
                            return true;
                    }
            return false;
        }

        private static bool IsClear(Map map, CellRect rect)
        {
            foreach (var c in rect)
            {
                if (!Ground.Open(c, map) || !c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy) || c.GetRoof(map) != null)
                    return false;
                foreach (var t in c.GetThingList(map))
                    if (t is Pawn)
                        return false;
            }
            return true;
        }

        /// <summary>Plants, items and filth go; the area becomes Home.</summary>
        private static void Clear(Map map, CellRect area)
        {
            foreach (var c in area)
            {
                foreach (var t in c.GetThingList(map).ToList())
                    if (t.def.category == ThingCategory.Plant || t.def.category == ThingCategory.Item || t.def.category == ThingCategory.Filth)
                        t.Destroy(DestroyMode.Vanish);
                map.areaManager.Home[c] = true;
            }
        }

        /// <summary>Walls on the rect's edge (a wall already there is shared), doors at the given cells, and a roof.</summary>
        private static void Room(Map map, CellRect rect, params IntVec3[] doors)
        {
            foreach (var c in rect.EdgeCells)
            {
                if (c.GetEdifice(map) != null && !doors.Contains(c))
                    continue;
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
