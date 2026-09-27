using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Reflect's "who does what" (FURNISHING.md §6): each kind of work that has something waiting, what waits, and every
    /// colonist's priority for it, so a mind can see work nobody is doing and raise hers or ask someone.
    /// </summary>
    public static class WorkWaiting
    {
        /// <summary>"Crafting (make stone blocks 0 of 10): Wehner 3, Sab off, Valentin 4", one line per work type; null if nothing waits.</summary>
        public static string Lines(Map map)
        {
            var waiting = new Dictionary<WorkTypeDef, List<string>>();
            void Add(WorkTypeDef work, string what)
            {
                if (work == null || what == null)
                    return;
                if (!waiting.TryGetValue(work, out var list))
                    waiting[work] = list = new List<string>();
                list.Add(what);
            }

            foreach (var building in map.listerBuildings.allBuildingsColonist)
            {
                if (!(building is IBillGiver giver) || giver.BillStack == null)
                    continue;
                WorkTypeDef work = DefDatabase<WorkGiverDef>.AllDefsListForReading.FirstOrDefault(w => w.fixedBillGiverDefs != null && w.fixedBillGiverDefs.Contains(building.def))?.workType;
                foreach (var bill in giver.BillStack.Bills)
                {
                    if (bill.suspended || !bill.ShouldDoNow())
                        continue;
                    Add(work, bill is Bill_Production p && p.repeatMode == BillRepeatModeDefOf.TargetCount
                        ? $"{bill.recipe.label} {p.recipe.WorkerCounter.CountProducts(p)} of {p.targetCount}" : bill.recipe.label);
                }
            }
            int toBuild = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => t.Faction == Faction.OfPlayer)
                          + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(t => t.Faction == Faction.OfPlayer);
            if (toBuild > 0)
                Add(WorkTypeDefOf.Construction, $"{toBuild} things to build");
            int plants = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.CutPlant).Count()
                         + map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.HarvestPlant).Count();
            if (plants > 0)
                Add(WorkTypeDefOf.PlantCutting, $"{plants} plants marked");
            int mine = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Mine).Count();
            if (mine > 0)
                Add(WorkTypeDefOf.Mining, $"{mine} cells marked");
            string haul = SnapshotBuilder.WaitingToBeHauled(map);
            if (haul != null)
                Add(WorkTypeDefOf.Hauling, haul);

            if (waiting.Count == 0)
                return null;
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null).ToList();
            bool manual = ActionCatalog.ManualPriorities;
            return string.Join("\n", waiting.OrderBy(kv => kv.Key.naturalPriority * -1).Select(kv =>
            {
                var who = colonists.Where(p => !p.WorkTypeIsDisabled(kv.Key)).Select(p =>
                {
                    int priority = p.workSettings.GetPriority(kv.Key);
                    return $"{p.LabelShort} {(priority == 0 ? "off" : manual ? priority.ToString() : "on")}";
                });
                return $"{kv.Key.labelShort} ({string.Join("; ", kv.Value)}): {string.Join(", ", who)}";
            }));
        }
    }
}
