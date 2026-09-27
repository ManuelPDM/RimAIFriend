using System;
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
        /// <summary>
        /// "haul (steel 337, lightleather 11): 348 to do, 90 last night · Valentin 3, Sab 3", one line per work type,
        /// what grew most since last night first; skill and passion beside each colonist for work that uses a skill
        /// ("Sab 1 (skill 11, burning passion)"). Null if nothing waits.
        /// </summary>
        /// <param name="me">The mind reading it: her own entry says "me (Sab)", so she knows which priority is hers.</param>
        public static string Lines(Map map, Pawn me)
        {
            var waiting = new Dictionary<WorkTypeDef, List<string>>();
            var amount = new Dictionary<WorkTypeDef, int>();
            void Add(WorkTypeDef work, string what, int howMuch)
            {
                if (work == null || what == null)
                    return;
                if (!waiting.TryGetValue(work, out var list))
                    waiting[work] = list = new List<string>();
                list.Add(what);
                amount[work] = (amount.TryGetValue(work, out int had) ? had : 0) + howMuch;
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
                    if (bill is Bill_Production p && p.repeatMode == BillRepeatModeDefOf.TargetCount)
                    {
                        int made = p.recipe.WorkerCounter.CountProducts(p);
                        Add(work, $"{bill.recipe.label} {made} of {p.targetCount}", Math.Max(0, p.targetCount - made));
                    }
                    else
                        Add(work, bill.recipe.label, bill is Bill_Production r && r.repeatMode == BillRepeatModeDefOf.RepeatCount ? r.repeatCount : 1);
                }
            }
            int toBuild = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(t => t.Faction == Faction.OfPlayer)
                          + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(t => t.Faction == Faction.OfPlayer);
            if (toBuild > 0)
            {
                Add(WorkTypeDefOf.Construction, $"{toBuild} things to build", toBuild);
                string missing = MaterialsMissing(map);
                if (missing != null)
                    Add(WorkTypeDefOf.Construction, "waiting on " + missing, 0);
            }
            int plants = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.CutPlant).Count()
                         + map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.HarvestPlant).Count();
            if (plants > 0)
                Add(WorkTypeDefOf.PlantCutting, $"{plants} plants marked", plants);
            int mine = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Mine).Count();
            if (mine > 0)
                Add(WorkTypeDefOf.Mining, $"{mine} cells marked", mine);
            string haul = SnapshotBuilder.WaitingToBeHauled(map, out int hauls);
            if (haul != null)
                Add(WorkTypeDefOf.Hauling, haul, hauls);

            if (waiting.Count == 0)
                return null;
            string Key(WorkTypeDef work) => $"{map.uniqueID}:{work.defName}";
            var lastNight = ChoreManager.Instance?.WorkLastNight(PawnMind.NightOf(map), amount.ToDictionary(kv => Key(kv.Key), kv => kv.Value))
                            ?? new Dictionary<string, int>();
            int? Before(WorkTypeDef work) => lastNight.TryGetValue(Key(work), out int n) ? n : (int?)null;
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => p.workSettings != null).ToList();
            bool manual = ActionCatalog.ManualPriorities;
            return string.Join("\n", waiting.Keys
                .OrderByDescending(w => amount[w] - (Before(w) ?? amount[w]))
                .ThenByDescending(w => w.naturalPriority)
                .Select(work =>
                {
                    var who = colonists.Where(p => !p.WorkTypeIsDisabled(work)).Select(p =>
                    {
                        int priority = p.workSettings.GetPriority(work);
                        string skill = "";
                        if (!work.relevantSkills.NullOrEmpty() && p.skills != null)
                        {
                            var passion = work.relevantSkills.Max(s => p.skills.GetSkill(s).passion);
                            skill = $" (skill {Math.Round(work.relevantSkills.Average(s => p.skills.GetSkill(s).Level))}" +
                                    (passion != Passion.None ? $", {SnapshotBuilder.PassionLabel(passion)})" : ")");
                        }
                        return $"{(p == me ? $"me ({p.LabelShort})" : p.LabelShort)} {(priority == 0 ? "off" : manual ? priority.ToString() : "on")}{skill}";
                    });
                    int? before = Before(work);
                    string trend = before != null ? $", {before} last night" : "";
                    return $"{work.labelShort} ({string.Join("; ", waiting[work])}): {amount[work]} to do{trend} · {string.Join(", ", who)}";
                }));
        }

        /// <summary>What the colony's blueprints and frames still need beyond storage: "518 wood". Null if nothing's short.</summary>
        private static string MaterialsMissing(Map map)
        {
            var need = new Dictionary<ThingDef, int>();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)))
            {
                if (t.Faction != Faction.OfPlayer)
                    continue;
                var costs = t is Frame frame ? frame.TotalMaterialCost() : t is Blueprint blueprint ? blueprint.TotalMaterialCost() : null;
                foreach (var cost in costs ?? new List<ThingDefCountClass>())
                    need[cost.thingDef] = (need.TryGetValue(cost.thingDef, out int had) ? had : 0) + (t is Frame f ? f.ThingCountNeeded(cost.thingDef) : cost.count);
            }
            var shortBy = need.Select(kv => (def: kv.Key, n: kv.Value - map.resourceCounter.GetCount(kv.Key))).Where(x => x.n > 0).ToList();
            return shortBy.Count == 0 ? null : string.Join(", ", shortBy.OrderByDescending(x => x.n).Select(x => $"{x.n} {x.def.label}"));
        }
    }
}