using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Dev gizmos for building (PHASE4.md §10). A second GetGizmos postfix, so Patch_PawnGizmos stays untouched.</summary>
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    internal static class Patch_BuildGizmos
    {
        private static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (var gizmo in gizmos)
                yield return gizmo;
            var manager = BuildManager.Instance;
            if (!Prefs.DevMode || manager == null || !__instance.IsColonistPlayerControlled || !__instance.RaceProps.Humanlike)
                yield break;

            Pawn pawn = __instance;
            var icon = ThingDefOf.Bed.uiIcon;
            yield return new Command_Action
            {
                defaultLabel = "DEV: Run building checks",
                defaultDesc = "Site options, stress test, score heatmap and report file. No LLM.",
                icon = icon,
                action = () => BuildDevTools.RunChecks(pawn.Map),
            };
            BuildProject project = manager.ActiveProject(pawn);
            if (project == null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Place site A now",
                    defaultDesc = "Places the best site in the most-stocked material as blueprints. No LLM.",
                    icon = icon,
                    action = () => BuildDevTools.PlaceBest(pawn),
                };
                yield break;
            }
            yield return new Command_Action
            {
                defaultLabel = "DEV: Finish project instantly",
                icon = icon,
                action = () => BuildDevTools.FinishInstantly(project),
            };
            yield return new Command_Action
            {
                defaultLabel = "DEV: Cancel project",
                icon = icon,
                action = () => BuildDevTools.Cancel(project),
            };
        }
    }

    public static class BuildDevTools
    {
        private const int StressAnchors = 20;
        private const int StressSamplePerAnchor = 200;
        private const int HeatmapTicks = 3000;

        public static string ReportPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "AIPawnControl", "building-report.txt");

        [DebugAction("AI Pawn Control", "Run building checks", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunChecksAction() => RunChecks(Find.CurrentMap);

        [DebugAction("AI Pawn Control", "Build test colony", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void BuildTestColonyAction() => TestColony.Build(Find.CurrentMap);

        /// <summary>Site options + stress test + heatmap, written to one report file (and a short log line).</summary>
        public static void RunChecks(Map map)
        {
            map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
            var report = new StringBuilder();
            report.AppendLine($"Building checks, {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}, map {map.Size.x}x{map.Size.z}, tuning {SiteWeights.FilePath}");
            report.AppendLine();
            var (sites, sitesSummary) = SiteOptions(map, report);
            report.AppendLine();
            string stressSummary = StressTest(map, report);

            string path = ReportPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, report.ToString());
            ModLog.Message($"Building checks: {sitesSummary}; {stressSummary}. Report: {path}");
            Messages.Message($"Building checks: {sitesSummary}; {stressSummary}.", MessageTypeDefOf.NeutralEvent, false);
        }

        private static (List<RoomPlan>, string) SiteOptions(Map map, StringBuilder report)
        {
            var clock = Stopwatch.StartNew();
            IntVec3 center = SiteFinder.BaseCenter(map);
            var finder = new SiteFinder(map, center);
            var candidates = finder.Candidates(RoomKind.Bedroom);
            var materials = SiteFinder.Materials(map);
            var top = SiteFinder.TopSites(candidates, 3, new RoomValidator(map, center, finder.weights.maxWalk), materials[0]);
            clock.Stop();

            report.AppendLine($"== Site options: base centre ({center.x},{center.z}), {candidates.Count} candidates, {clock.ElapsedMilliseconds} ms");
            if (finder.foci.Count > 0)
                report.AppendLine($"No-build foci (anima tree etc.): {string.Join(", ", finder.foci.Select(f => $"({f.pos.x},{f.pos.z}) r{f.radius:0.#}"))}");
            report.AppendLine(SiteFinder.StockLine(map, materials));
            if (top.Count == 0)
                report.AppendLine("No site passes.");
            for (int i = 0; i < top.Count; i++)
            {
                RoomPlan p = top[i];
                report.AppendLine(finder.Describe(p, (char)('A' + i), materials));
                report.AppendLine("  " + p.TermsText());
                report.AppendLine(TextMap.Draw(p));
            }
            report.AppendLine("Best 10 candidates (overlapping ones included):");
            foreach (var p in candidates.Take(10))
                report.AppendLine($"  {p.InteriorSize}x{p.InteriorSize} ({p.footprint.minX},{p.footprint.minZ})-({p.footprint.maxX},{p.footprint.maxZ}) door ({p.door.x},{p.door.z}): {p.TermsText()}");
            Heatmap(map, center, candidates, top);
            return (top, $"{top.Count} sites from {candidates.Count} candidates");
        }

        /// <summary>Each candidate's centre coloured by its best score; the top sites outlined. Stays while paused.</summary>
        private static void Heatmap(Map map, IntVec3 center, List<RoomPlan> candidates, List<RoomPlan> top)
        {
            if (candidates.Count == 0)
                return;
            var best = new Dictionary<IntVec3, float>();
            foreach (var p in candidates)
            {
                IntVec3 c = p.footprint.CenterCell;
                if (!best.TryGetValue(c, out float s) || p.score > s)
                    best[c] = p.score;
            }
            float min = best.Values.Min(), max = best.Values.Max(), span = max - min < 0.01f ? 1f : max - min;
            foreach (var kv in best)
                map.debugDrawer.FlashCell(kv.Key, (kv.Value - min) / span, null, HeatmapTicks);
            foreach (var p in top)
            {
                CellRect r = p.footprint;
                var corners = new[] { new IntVec3(r.minX, 0, r.minZ), new IntVec3(r.maxX, 0, r.minZ), new IntVec3(r.maxX, 0, r.maxZ), new IntVec3(r.minX, 0, r.maxZ) };
                for (int i = 0; i < 4; i++)
                    map.debugDrawer.FlashLine(corners[i], corners[(i + 1) % 4], HeatmapTicks, SimpleColor.White);
                map.debugDrawer.FlashCell(r.CenterCell, 1f, p.score.ToString("0"), HeatmapTicks);
            }
            map.debugDrawer.FlashCell(center, 0.5f, "centre", HeatmapTicks);
        }

        private static string StressTest(Map map, StringBuilder report)
        {
            var clock = Stopwatch.StartNew();
            var anchors = new List<IntVec3> { SiteFinder.BaseCenter(map) };
            for (int i = 0; i < StressAnchors; i++)
                if (CellFinder.TryFindRandomCell(map, c => c.Standable(map) && !c.Fogged(map), out IntVec3 c2))
                    anchors.Add(c2);

            var perRule = new Dictionary<string, int>();
            var examples = new Dictionary<string, List<string>>();
            int checkedCount = 0, total = 0;
            ThingDef material = SiteFinder.Materials(map)[0];
            for (int a = 0; a < anchors.Count; a++)
            {
                var finder = new SiteFinder(map, anchors[a]);
                var validator = new RoomValidator(map, anchors[a], finder.weights.maxWalk);
                var candidates = finder.Candidates(RoomKind.Bedroom);
                total += candidates.Count;
                // Every candidate from the base centre; a random sample from the other anchors.
                IEnumerable<RoomPlan> sample = a == 0 ? candidates : candidates.InRandomOrder().Take(StressSamplePerAnchor);
                foreach (var plan in sample)
                {
                    checkedCount++;
                    foreach (var failure in validator.Check(plan, material))
                    {
                        string rule = failure.Substring(0, 2);
                        perRule.TryGetValue(rule, out int n);
                        perRule[rule] = n + 1;
                        if (!examples.TryGetValue(rule, out var list))
                            examples[rule] = list = new List<string>();
                        if (list.Count < 2)
                            list.Add($"{failure} (anchor {anchors[a].x},{anchors[a].z}), {plan.TermsText()}\n{TextMap.Draw(plan)}");
                    }
                }
            }
            clock.Stop();

            report.AppendLine($"== Stress test: {anchors.Count} anchors, {total} candidates, {checkedCount} validated, {clock.ElapsedMilliseconds} ms");
            if (perRule.Count == 0)
                report.AppendLine("Zero validator failures.");
            foreach (var kv in perRule.OrderBy(kv => kv.Key))
                report.AppendLine($"{kv.Key}: {kv.Value} failures");
            foreach (var kv in examples.OrderBy(kv => kv.Key))
                foreach (var ex in kv.Value)
                    report.AppendLine(ex);
            return perRule.Count == 0 ? $"stress test zero failures in {checkedCount}" : $"stress test {perRule.Values.Sum()} FAILURES in {checkedCount}";
        }

        public static void PlaceBest(Pawn pawn)
        {
            Map map = pawn.Map;
            IntVec3 center = SiteFinder.BaseCenter(map);
            var finder = new SiteFinder(map, center);
            var materials = SiteFinder.Materials(map);
            var validator = new RoomValidator(map, center, finder.weights.maxWalk);
            var top = SiteFinder.TopSites(finder.Candidates(RoomKind.Bedroom), 1, validator, materials[0]);
            if (top.Count == 0)
            {
                Messages.Message("No site passes.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Place(pawn, top[0], materials[0], validator, finder.Describe(top[0], 'A', materials));
        }

        /// <summary>Re-validates, then places every entry as an ordinary player blueprint and records the project.</summary>
        public static bool Place(Pawn pawn, RoomPlan plan, ThingDef material, RoomValidator validator, string description)
        {
            var failures = validator.Check(plan, material);
            if (failures.Count > 0)
            {
                ModLog.Warning($"Not placed, the validator failed: {string.Join("; ", failures)}\n{TextMap.Draw(plan)}");
                return false;
            }
            plan.ApplyMaterial(material);
            foreach (var e in plan.entries)
                GenConstruct.PlaceBlueprintForBuild(e.def, e.cell, plan.map, e.rot, Faction.OfPlayer, e.stuff);
            BuildManager.Instance.Add(new BuildProject
            {
                pawn = pawn,
                map = plan.map,
                kind = plan.kind.label,
                footprint = plan.footprint,
                entries = plan.entries,
                material = material,
                placedTick = Find.TickManager.TicksGame,
            });
            ModLog.Message($"{pawn.LabelShort} laid out a {plan.kind.label} in {material.label}: {description}\n{TextMap.Draw(plan)}");
            return true;
        }

        /// <summary>God-mode build of every entry plus a roof, to test done and bed claiming.</summary>
        public static void FinishInstantly(BuildProject project)
        {
            Map map = project.map;
            foreach (var e in project.entries)
            {
                foreach (var c in e.Rect)
                    foreach (var t in c.GetThingList(map).ToList())
                        if (t is Blueprint || t is Frame)
                            t.Destroy(DestroyMode.Vanish);
                if (project.Built(e))
                    continue;
                Thing thing = ThingMaker.MakeThing(e.def, e.stuff);
                thing.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(thing, e.cell, map, e.rot, WipeMode.Vanish);
            }
            foreach (var c in project.footprint.ContractedBy(1))
                map.roofGrid.SetRoof(c, RoofDefOf.RoofConstructed);
            ModLog.Message($"Finished {project.pawn.LabelShort}'s {project.kind} instantly.");
        }

        public static void Cancel(BuildProject project)
        {
            foreach (var e in project.entries)
                foreach (var t in e.cell.GetThingList(project.map).ToList())
                    if (t is Blueprint)
                        t.Destroy(DestroyMode.Cancel);
            project.state = BuildProject.State.Abandoned;
            ModLog.Message($"Cancelled {project.pawn.LabelShort}'s {project.kind}.");
        }
    }
}
