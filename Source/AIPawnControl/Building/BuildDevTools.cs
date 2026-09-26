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
                PawnMind mind = MindManager.Instance?.MindOf(pawn);
                if (mind != null && mind.persona != null)
                    yield return new Command_Action
                    {
                        defaultLabel = "DEV: Plan a room now",
                        defaultDesc = "Runs the site scan and the Project call (kind, size, site, material), ignoring the budget and cooldown.",
                        icon = icon,
                        action = () =>
                        {
                            if (mind.Thinking)
                            {
                                Messages.Message($"{pawn.LabelShort} is thinking already.", MessageTypeDefOf.RejectInput, false);
                                return;
                            }
                            mind.AddDecision("DEV plan a room now: " + ProjectCall.Start(mind, dev: true), importance: 0);
                        },
                    };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Furnish now",
                    defaultDesc = "Logs the furnishing options her Act menu would show and places the first one. No LLM; the cooldown applies.",
                    icon = icon,
                    action = () =>
                    {
                        var options = Furnishing.Options(pawn);
                        ModLog.Message($"{pawn.LabelShort} furnishing options ({BuildManager.Instance.CantPlanReason(pawn) ?? "may build"}): " +
                                       (options.Count > 0 ? string.Join(" | ", options.Select(o => o.label)) : "none"));
                        if (options.Count > 0)
                            ModLog.Message(Furnishing.Place(pawn, options[0]));
                    },
                };
                yield return new Command_Action
                {
                    defaultLabel = "DEV: Place site A now",
                    defaultDesc = "Pick a room kind; places it at the best site, 5×5, in the most-stocked material as blueprints. No LLM.",
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
        private const int StressSampleAtCentre = 40;
        private const int HeatmapTicks = 3000;

        public static string ReportPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "AIPawnControl", "building-report.txt");

        [DebugAction("AI Pawn Control", "Run building checks", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunChecksAction() => RunChecks(Find.CurrentMap);

        [DebugAction("AI Pawn Control", "Build test colony", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void BuildTestColonyAction() => TestColony.Build(Find.CurrentMap);

        /// <summary>"Place site A now" for the selected pawn, one child per kind, so RimBridge can pick a kind by path
        /// (it can't click a float menu).</summary>
        [DebugAction("AI Pawn Control", "Place site A now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static List<DebugActionNode> PlaceBestAction()
        {
            return DefDatabase<RoomKindDef>.AllDefsListForReading
                .Select(kind => new DebugActionNode(kind.LabelCap, DebugActionType.Action, () =>
                {
                    if (Find.Selector.SingleSelectedThing is Pawn pawn && BuildManager.Instance.ActiveProject(pawn) == null)
                        PlaceBest(pawn, kind);
                    else
                        Messages.Message("Select one colonist with no active project.", MessageTypeDefOf.RejectInput, false);
                }))
                .ToList();
        }

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
            long tGrids = clock.ElapsedMilliseconds;
            var materials = SiteFinder.Materials(map);
            var validator = new RoomValidator(map, center, finder.weights.maxWalk);
            long tValidator = clock.ElapsedMilliseconds;
            var sites = finder.Sites(validator, materials[0], out var candidates);
            long tSites = clock.ElapsedMilliseconds;
            var fits = sites.Select(finder.MaxFit).ToList();
            long tMaxFit = clock.ElapsedMilliseconds;

            // What placing each kind at site A costs (the step after her reply): templates, fit and validation.
            var kinds = DefDatabase<RoomKindDef>.AllDefsListForReading;
            var kindPlans = new List<(RoomKindDef kind, RoomPlan plan, string note, long ms)>();
            if (sites.Count > 0)
                foreach (var kind in kinds)
                {
                    long before = clock.ElapsedMilliseconds;
                    var plan = finder.Fit(sites[0], kind, SiteFinder.StandardSize, SiteFinder.StandardSize, validator, materials[0], out string note);
                    kindPlans.Add((kind, plan, note, clock.ElapsedMilliseconds - before));
                }
            clock.Stop();
            long scan = tMaxFit; // what runs when she picks "plan a new room"

            report.AppendLine($"== Site options: base centre ({center.x},{center.z}), {candidates.Count} candidates at {SiteFinder.StandardSize}x{SiteFinder.StandardSize}");
            report.AppendLine($"Timing: scan {scan} ms = grids {tGrids} (walk {finder.timings["walk"]}, other things' work spots to {finder.timings["spots"]}, cells to {finder.timings["cells"]}) + validator setup {tValidator - tGrids} + candidates and top sites {tSites - tValidator} + room to grow {tMaxFit - tSites}; " +
                              $"then per kind at site A: {string.Join(", ", kindPlans.Select(k => $"{k.kind.label} {k.ms}"))} ms");
            if (finder.foci.Count > 0)
                report.AppendLine($"No-build foci (anima tree etc.): {string.Join(", ", finder.foci.Select(f => $"({f.pos.x},{f.pos.z}) r{f.radius:0.#}"))}");
            report.AppendLine(SiteFinder.StockLine(map, materials));
            if (sites.Count == 0)
                report.AppendLine("No site passes.");
            for (int i = 0; i < sites.Count; i++)
            {
                RoomPlan p = sites[i];
                report.AppendLine(finder.Describe(p, (char)('A' + i), materials, fits[i]));
                report.AppendLine("  " + p.TermsText());
                report.AppendLine(TextMap.Draw(p));
            }
            report.AppendLine("Each kind at site A, 5x5 asked:");
            foreach (var (kind, plan, note, _) in kindPlans)
            {
                if (plan == null)
                {
                    report.AppendLine($"  {kind.label}: doesn't fit{(kind.BuildableNow(map) ? "" : " (not buildable now)")}");
                    continue;
                }
                report.AppendLine($"  {kind.label} {plan.SizeLabel}{(note != null ? $" ({note})" : "")}: furniture {SiteFinder.CostText(FurnitureOnly(plan), materials)}");
                report.AppendLine(TextMap.Draw(plan));
            }
            report.AppendLine("Best 10 candidates (overlapping ones included):");
            foreach (var c in candidates.Take(10))
                report.AppendLine($"  {c.Width}x{c.Height} ({c.rect.minX},{c.rect.minZ})-({c.rect.maxX},{c.rect.maxZ}) door ({c.door.x},{c.door.z}): score {c.score:0.0}");
            Heatmap(map, center, candidates, sites);
            return (sites, $"{sites.Count} sites from {candidates.Count} candidates in {scan} ms");
        }

        private static RoomPlan FurnitureOnly(RoomPlan plan)
        {
            var copy = new RoomPlan { kind = plan.kind, map = plan.map, footprint = plan.footprint };
            copy.entries.AddRange(plan.Furniture);
            return copy;
        }

        /// <summary>Each candidate's centre coloured by its best score; the top sites outlined. Stays while paused.</summary>
        private static void Heatmap(Map map, IntVec3 center, List<SiteFinder.Candidate> candidates, List<RoomPlan> top)
        {
            if (candidates.Count == 0)
                return;
            var best = new Dictionary<IntVec3, float>();
            foreach (var p in candidates)
            {
                IntVec3 c = p.rect.CenterCell;
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

        /// <summary>
        /// Every kind in every size: from the base centre, a sample of candidates per (kind, size); from ~20 random anchors,
        /// one per (kind, size). Each is laid out as a full plan and validated. Expected: zero failures.
        /// </summary>
        private static string StressTest(Map map, StringBuilder report)
        {
            var clock = Stopwatch.StartNew();
            var anchors = new List<IntVec3> { SiteFinder.BaseCenter(map) };
            for (int i = 0; i < StressAnchors; i++)
                if (CellFinder.TryFindRandomCell(map, c => c.Standable(map) && !c.Fogged(map), out IntVec3 c2))
                    anchors.Add(c2);

            var perRule = new Dictionary<string, int>();
            var examples = new Dictionary<string, List<string>>();
            var perKind = new Dictionary<string, int>();
            int checkedCount = 0, total = 0, noFit = 0;
            ThingDef material = SiteFinder.Materials(map)[0];
            var kinds = DefDatabase<RoomKindDef>.AllDefsListForReading;
            for (int a = 0; a < anchors.Count; a++)
            {
                var finder = new SiteFinder(map, anchors[a]);
                var validator = new RoomValidator(map, anchors[a], finder.weights.maxWalk);
                foreach (var (sw, sh) in SiteFinder.Shapes)
                {
                    var candidates = finder.Candidates(sw, sh);
                    total += candidates.Count;
                    int sample = a == 0 ? StressSampleAtCentre : 1;
                    foreach (var kind in kinds)
                    {
                        if (!kind.Fits(sw, sh))
                            continue;
                        foreach (var c in candidates.InRandomOrder().Take(sample))
                        {
                            var plan = finder.Plan(c, kind);
                            if (plan == null)
                            {
                                noFit++;
                                continue;
                            }
                            checkedCount++;
                            perKind.TryGetValue(kind.label, out int k);
                            perKind[kind.label] = k + 1;
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
                }
            }
            clock.Stop();

            report.AppendLine($"== Stress test: {anchors.Count} anchors, {SiteFinder.Shapes.Count} sizes, {kinds.Count} kinds, {total} candidates, " +
                              $"{checkedCount} validated ({string.Join(", ", perKind.Select(kv => $"{kv.Key} {kv.Value}"))}), {noFit} where the kind's items didn't fit, {clock.ElapsedMilliseconds} ms");
            if (perRule.Count == 0)
                report.AppendLine("Zero validator failures.");
            foreach (var kv in perRule.OrderBy(kv => kv.Key))
                report.AppendLine($"{kv.Key}: {kv.Value} failures");
            foreach (var kv in examples.OrderBy(kv => kv.Key))
                foreach (var ex in kv.Value)
                    report.AppendLine(ex);
            return perRule.Count == 0 ? $"stress test zero failures in {checkedCount}" : $"stress test {perRule.Values.Sum()} FAILURES in {checkedCount}";
        }

        /// <summary>A float menu of kinds; the chosen one goes at the best site, 5×5, in the most-stocked material.</summary>
        public static void PlaceBest(Pawn pawn)
        {
            var options = DefDatabase<RoomKindDef>.AllDefsListForReading
                .Select(kind => new FloatMenuOption(kind.LabelCap, () => PlaceBest(pawn, kind)))
                .ToList();
            Find.WindowStack.Add(new FloatMenu(options));
        }

        public static void PlaceBest(Pawn pawn, RoomKindDef kind)
        {
            Map map = pawn.Map;
            IntVec3 center = SiteFinder.BaseCenter(map);
            var finder = new SiteFinder(map, center);
            var materials = SiteFinder.Materials(map);
            var validator = new RoomValidator(map, center, finder.weights.maxWalk);
            var sites = finder.Sites(validator, materials[0], out _);
            RoomPlan plan = sites.Count > 0 ? finder.Fit(sites[0], kind, SiteFinder.StandardSize, SiteFinder.StandardSize, validator, materials[0], out _) : null;
            if (plan == null)
            {
                Messages.Message($"No site fits a {kind.label}.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            BuildManager.Instance.Place(pawn, plan, materials[0], validator, finder.Where(plan));
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
            ModLog.Message($"Finished {project.pawn.LabelShort}'s {project.Kind} instantly.");
        }

        public static void Cancel(BuildProject project)
        {
            foreach (var e in project.entries)
                foreach (var t in e.cell.GetThingList(project.map).ToList())
                    if (t is Blueprint)
                        t.Destroy(DestroyMode.Cancel);
            project.state = BuildProject.State.Abandoned;
            ModLog.Message($"Cancelled {project.pawn.LabelShort}'s {project.Kind} (dev, no memory event).");
        }
    }
}
