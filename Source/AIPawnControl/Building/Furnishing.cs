using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Furnishing (PHASE4.md §9): adding one item to a room she built. Any buildable, unpowered furniture or recreation
    /// item that keeps the room's role (anything in a plain room), affordable from storage, with a free slot the placer
    /// finds. At most 2 options, the most beauty for the material first. Placed as a one-item project.
    /// </summary>
    public static class Furnishing
    {
        public const int MaxOptions = 2;

        public class Option
        {
            public BuildProject room;
            public PlanEntry entry;
            public string label; // "add to my bedroom: dresser (50 wood)"
        }

        private static List<ThingDef> candidates;

        /// <summary>Furniture and recreation buildings that need no power and fit a small room.</summary>
        private static List<ThingDef> Candidates => candidates ?? (candidates = DefDatabase<ThingDef>.AllDefsListForReading
            .Where(d => d.category == ThingCategory.Building && d.BuildableByPlayer && d.designationCategory != null
                        && (d.designationCategory.defName == "Furniture" || d.designationCategory.defName == "Joy")
                        && !d.HasComp(typeof(CompPowerTrader)) && d.GetCompProperties<CompProperties_Power>() == null
                        && d.size.x <= 3 && d.size.z <= 3 && !d.IsBed)
            .ToList());

        public static List<Option> Options(Pawn pawn)
        {
            var result = new List<Option>();
            var manager = BuildManager.Instance;
            if (manager == null || manager.CantPlanReason(pawn) != null)
                return result;
            // Ranked across all her rooms, so the first room doesn't take both slots.
            var ranked = new List<(BuildProject project, Room room, ThingDef def, ThingDef stuff, float value, float price)>();
            foreach (var project in manager.ProjectsOf(pawn).Where(p => p.state == BuildProject.State.Done && !p.furnishing && p.map == pawn.Map).ToList())
            {
                Room room = project.Room;
                if (room == null || !room.ProperRoom || room.PsychologicallyOutdoors)
                    continue;
                var present = new HashSet<ThingDef>(room.ContainedAndAdjacentThings.Select(t => t is Blueprint || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def));
                foreach (var def in Candidates)
                {
                    if (present.Contains(def) || present.Any(p => SameKind(p, def)) || !RoomKindDef.Buildable(def))
                        continue;
                    ThingDef stuff = RoomPlan.StuffFor(def, project.material);
                    var cost = def.CostListAdjusted(stuff);
                    if (cost.Any(c => pawn.Map.resourceCounter.GetCount(c.thingDef) < c.count))
                        continue;
                    if (project.kindDef?.role != null && !KeepsRole(room, def))
                        continue;
                    float beauty = def.GetStatValueAbstract(StatDefOf.Beauty, stuff);
                    // Ties (most furniture has beauty 0) go to the pricier item: wealth raises impressiveness too.
                    ranked.Add((project, room, def, stuff, beauty / (1 + cost.Sum(c => c.count)), def.GetStatValueAbstract(StatDefOf.MarketValue, stuff)));
                }
            }
            foreach (var r in ranked.OrderByDescending(r => r.value).ThenByDescending(r => r.price))
            {
                if (result.Count >= MaxOptions)
                    break;
                if (result.Any(o => o.room == r.project && SameKind(o.entry.def, r.def)))
                    continue;
                var entry = Slot(r.project, r.room, r.def);
                if (entry == null)
                    continue;
                entry.stuff = r.stuff;
                string where = r.project.kindDef != null && r.project.kindDef.owned ? $"my {r.project.Kind}" : $"the {r.project.Kind} I built";
                string cost = string.Join(", ", r.def.CostListAdjusted(r.stuff).Select(c => $"{c.count} {c.thingDef.label}"));
                result.Add(new Option { room = r.project, entry = entry, label = $"add to {where}: {r.def.label} ({cost})" });
            }
            return result;
        }

        /// <summary>Variants of one item (outfit stand / kid outfit stand): one's special building class is, or derives
        /// from, the other's. Plain buildings share the base class, so they never count as the same kind.</summary>
        private static bool SameKind(ThingDef a, ThingDef b)
        {
            if (a == null || b == null || a.thingClass == typeof(Building) || b.thingClass == typeof(Building))
                return false;
            return a.thingClass.IsAssignableFrom(b.thingClass) || b.thingClass.IsAssignableFrom(a.thingClass);
        }

        /// <summary>Vanilla's role workers: the room's winning role stays the same with the item in it.</summary>
        private static bool KeepsRole(Room room, ThingDef def)
        {
            RoomRoleDef best = null;
            float bestScore = float.MinValue;
            foreach (var role in DefDatabase<RoomRoleDef>.AllDefsListForReading)
            {
                float score = role.Worker.GetScore(room) + role.Worker.GetScoreDeltaIfBuildingPlaced(room, def);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = role;
                }
            }
            return best == room.Role;
        }

        /// <summary>A free slot by the placer's rules, around what's already in the room; seats go next to a table if there is one.</summary>
        private static PlanEntry Slot(BuildProject project, Room room, ThingDef def)
        {
            Map map = project.map;
            PlanEntry doorEntry = project.entries.Find(e => e.def == ThingDefOf.Door);
            IntVec3 door = doorEntry?.cell ?? project.footprint.EdgeCells.FirstOrDefault(c => c.GetDoor(map) != null);
            if (!door.IsValid)
                return null;
            Rot4 side = SiteFinder.SideOf(project.footprint, door);
            var plan = new RoomPlan
            {
                kind = project.kindDef, map = map, footprint = project.footprint,
                door = door, doorInside = door - side.FacingCell, doorOutside = door + side.FacingCell,
            };
            var existing = new List<PlanEntry>();
            foreach (var t in room.ContainedAndAdjacentThings)
            {
                if (!plan.Interior.Contains(t.Position))
                    continue;
                ThingDef d = t is Blueprint || t is Frame ? t.def.entityDefToBuild as ThingDef : t.def;
                if (d != null && d.category == ThingCategory.Building)
                    existing.Add(new PlanEntry(d, t.Position, t.Rotation));
            }
            PlanEntry table = def.building != null && def.building.isSittable ? existing.Find(e => e.def.surfaceType == SurfaceType.Eat) : null;
            var entry = RoomPlacer.PlaceOne(plan, def, existing, table);
            if (entry == null)
                return null;
            var report = GenConstruct.CanPlaceBlueprintAt(def, entry.cell, entry.rot, map, stuffDef: RoomPlan.StuffFor(def, project.material));
            return report.Accepted ? entry : null;
        }

        /// <summary>Places the chosen item as a blueprint and tracks it as a one-item project.</summary>
        public static string Place(Pawn pawn, Option option)
        {
            var e = option.entry;
            var report = GenConstruct.CanPlaceBlueprintAt(e.def, e.cell, e.rot, pawn.Map, stuffDef: e.stuff);
            if (!report.Accepted)
                return $"Couldn't place the {e.def.label}: {report.Reason}";
            GenConstruct.PlaceBlueprintForBuild(e.def, e.cell, pawn.Map, e.rot, Faction.OfPlayer, e.stuff);
            var room = option.room;
            string where = room.kindDef != null && room.kindDef.owned ? $"to my {room.Kind}" : $"to the {room.Kind} I built";
            BuildManager.Instance.Add(new BuildProject
            {
                pawn = pawn,
                map = pawn.Map,
                kindDef = room.kindDef,
                footprint = room.footprint,
                entries = new List<PlanEntry> { e },
                material = e.stuff,
                placedTick = Find.TickManager.TicksGame,
                where = where,
                byMind = true,
                furnishing = true,
            });
            ModLog.Message($"{pawn.LabelShort} is adding {where}: {e.def.label} at ({e.cell.x},{e.cell.z}) rot {e.rot}; impressiveness now {room.Room?.GetStat(RoomStatDefOf.Impressiveness):0.0}.");
            return $"Planned to add {where}: {e.def.label}.";
        }
    }
}
