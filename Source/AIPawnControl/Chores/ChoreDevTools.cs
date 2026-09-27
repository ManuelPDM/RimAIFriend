using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Dev tools for colony chores (PHASE5.md §7), as debug actions so RimBridge can run them by path. No LLM.</summary>
    public static class ChoreDevTools
    {
        public static string ReportPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "AIPawnControl", "chore-report.txt");

        private static Pawn Selected()
        {
            if (Find.Selector.SingleSelectedThing is Pawn pawn && pawn.IsColonist && pawn.Map != null)
                return pawn;
            Messages.Message("Select one colonist first.", MessageTypeDefOf.RejectInput, false);
            return null;
        }

        /// <summary>Logs every option the finders have for the selected colonist, the validator's verdict, and what the menu would show.</summary>
        [DebugAction("AI Pawn Control", "Chore options now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OptionsNow()
        {
            Pawn pawn = Selected();
            if (pawn == null)
                return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var all = ChoreOptions.All(pawn, ignoreLimits: true);
            long ms = clock.ElapsedMilliseconds;
            var sb = new StringBuilder($"{pawn.LabelShort} chore options ({all.Count} found in {ms} ms, limits ignored):\n");
            foreach (var o in all.Concat(ChoreOptions.Stops(pawn)))
                sb.AppendLine($"  [{o.kind}] {o.useful:0.0} {o.label}{(o.AmountText() is string amounts ? ". " + amounts : "")} ({o.needs}) | check: {o.check() ?? "ok"}");
            sb.AppendLine("The Act menu shows \"work on the colony\" (limits applied): " + (ChoreOptions.AnythingToDo(pawn) ? "yes" : "no"));
            foreach (Chore.Kind kind in System.Enum.GetValues(typeof(Chore.Kind)))
                sb.AppendLine($"  {kind}: {ChoreManager.Instance?.CantReason(pawn, kind) ?? "may start"}");
            sb.Append("[Colony work] " + ColonyWork.Line(pawn));
            ModLog.Message(sb.ToString());
        }

        /// <summary>Applies the most useful option of one kind for the selected colonist, ignoring cooldowns and caps.</summary>
        [DebugAction("AI Pawn Control", "Apply chore", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static List<DebugActionNode> ApplyChore()
        {
            return System.Enum.GetValues(typeof(Chore.Kind)).Cast<Chore.Kind>()
                .Select(kind => new DebugActionNode(kind.ToString(), DebugActionType.Action, () =>
                {
                    Pawn pawn = Selected();
                    if (pawn == null)
                        return;
                    if (kind == Chore.Kind.Field || kind == Chore.Kind.Stockpile)
                    {
                        ModLog.Message($"Apply chore {kind} for {pawn.LabelShort}: {PlaceDefault(pawn, kind)}");
                        return;
                    }
                    var option = ChoreOptions.All(pawn, ignoreLimits: true).Where(o => o.kind == kind).OrderByDescending(o => o.useful).FirstOrDefault();
                    if (option == null)
                    {
                        ModLog.Message($"Apply chore {kind}: no option for {pawn.LabelShort}.");
                        return;
                    }
                    var mind = MindManager.Instance?.MindOf(pawn) ?? new PawnMind(pawn, null);
                    var choice = new ColonyChoice { size = 1, count = option.counts?[1] ?? 0 };
                    if (option.needs == ChoreNeeds.Crop)
                        choice.crop = Fields.Crops(pawn.Map).FirstOrDefault(c => !option.label.Contains(c.label));
                    ModLog.Message($"Apply chore {kind} for {pawn.LabelShort}: {option.label} (medium) → {option.apply(mind, choice)}");
                }))
                .ToList();
        }

        /// <summary>No LLM: site A, medium, the fastest food crop (field) or "everything" (stockpile).</summary>
        private static string PlaceDefault(Pawn pawn, Chore.Kind kind)
        {
            Map map = pawn.Map;
            var scan = new ChoreScan(pawn);
            if (kind == Chore.Kind.Field)
            {
                var crop = Fields.Crops(map).FirstOrDefault(c => Fields.Purpose(c) == "food") ?? Fields.Crops(map).FirstOrDefault();
                var foci = SiteFinder.NoBuildFoci(map);
                var finder = new ZoneSites(scan, c => Fields.CellOk(c, map, foci), c => Fields.CellScore(c, map));
                var site = finder.Find(Fields.Sizes).FirstOrDefault();
                if (crop == null || site == null)
                    return "no crop or no site";
                var rect = site.Rect(System.Math.Min(6, site.maxSize));
                return Fields.Place(pawn, rect, crop, finder.Where(rect, site.steps));
            }
            var piles = new ZoneSites(scan, c => Stockpiles.CellOk(c, map), c => Stockpiles.CellScore(c, map));
            var pile = piles.Find(Stockpiles.Sizes).FirstOrDefault();
            if (pile == null)
                return "no site";
            var r = pile.Rect(System.Math.Min(5, pile.maxSize));
            return Stockpiles.Place(pawn, r, "everything", piles.Where(r, pile.steps));
        }

        /// <summary>The real Colony call for the selected mind, ignoring budget, cooldowns and caps.</summary>
        [DebugAction("AI Pawn Control", "Colony call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void ColonyCallNow()
        {
            Pawn pawn = Selected();
            var mind = pawn != null ? MindManager.Instance?.MindOf(pawn) : null;
            if (mind == null || mind.persona == null || mind.Thinking)
            {
                Messages.Message("Select a colonist with a mind that isn't thinking.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            mind.AddDecision("DEV colony call now: " + ColonyCall.Start(mind, dev: true), importance: 0);
        }

        /// <summary>Stops every chore the selected colonist has running (her "stop …" path).</summary>
        [DebugAction("AI Pawn Control", "Stop my chores", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void StopAll()
        {
            Pawn pawn = Selected();
            if (pawn == null)
                return;
            foreach (var chore in ChoreManager.Instance.ActiveOf(pawn).ToList())
                ModLog.Message($"{pawn.LabelShort}: {ChoreOptions.StopLabel(chore)} → {chore.Stop()}");
        }

        /// <summary>Removes the selected colonist's chores the way the player would (designations, bills, zones), to test the veto.</summary>
        [DebugAction("AI Pawn Control", "Player removes my chores", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void PlayerRemoves()
        {
            Pawn pawn = Selected();
            if (pawn == null)
                return;
            var dm = pawn.Map.designationManager;
            foreach (var chore in ChoreManager.Instance.ActiveOf(pawn).ToList())
            {
                foreach (var t in chore.things)
                    dm.RemoveAllDesignationsOn(t);
                foreach (var c in chore.cells)
                    dm.TryRemoveDesignation(c, DesignationDefOf.Mine);
                if (chore.bill != null && !chore.bill.DeletedOrDereferenced)
                    chore.bill.billStack.Delete(chore.bill);
                if (chore.zone != null && chore.zone.cells.Count > 0)
                    chore.zone.Delete();
                ModLog.Message($"DEV: the player removed {pawn.LabelShort}'s {chore.kind} ({chore.label}).");
            }
        }

        /// <summary>Every option on the map (for the first colonist), each checked by its validator; a report file and a summary line.</summary>
        [DebugAction("AI Pawn Control", "Run chore checks", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void RunChecks()
        {
            Map map = Find.CurrentMap;
            var sb = new StringBuilder($"Chore checks, {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}, map {map.Size.x}x{map.Size.z}\n");
            int options = 0, failures = 0;
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList()) // the finders re-read the cached list
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var all = ChoreOptions.All(pawn, ignoreLimits: true);
                sb.AppendLine($"\n{pawn.LabelShort}: {all.Count} options in {clock.ElapsedMilliseconds} ms");
                foreach (var o in all.OrderBy(o => o.kind).ThenByDescending(o => o.useful))
                {
                    string check = o.check();
                    options++;
                    if (check != null)
                        failures++;
                    sb.AppendLine($"  [{o.kind}] {o.useful:0.0} {o.label}{(o.AmountText() is string amounts ? ". " + amounts : "")}{(check != null ? " | VALIDATOR FAILED: " + check : "")}");
                }
                sb.AppendLine("  [Colony work] " + ColonyWork.Line(pawn));
            }
            sb.AppendLine("\nWork orders by table:");
            foreach (var line in WorkOrders.Explain(map))
                sb.AppendLine("  " + line);
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
            File.WriteAllText(ReportPath, sb.ToString());
            ModLog.Message($"Chore checks: {options} options, {failures} validator failures. Report: {ReportPath}");
        }
    }
}
