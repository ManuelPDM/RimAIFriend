using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Dev tools for the base and colony chores (STREAMLINE.md §10), as debug actions so RimBridge can run them by path.</summary>
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

        /// <summary>Logs every stock-up option for the selected colonist, the validator's verdict, and whether the Act menu shows "work on the base".</summary>
        [DebugAction("AI Pawn Control", "Chore options now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OptionsNow()
        {
            Pawn pawn = Selected();
            if (pawn == null)
                return;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var all = ChoreOptions.All(pawn, ignoreLimits: true);
            long ms = clock.ElapsedMilliseconds;
            var sb = new StringBuilder($"{pawn.LabelShort} stock-up options ({all.Count} found in {ms} ms, limits ignored):\n");
            foreach (var o in all)
                sb.AppendLine($"  [{o.kind}] {o.useful:0.0} {o.label} | check: {o.check() ?? "ok"}");
            sb.AppendLine($"The Act menu shows \"{BaseCall.MenuLabel(pawn)}\" (limits applied): " + (BaseCall.AnythingToDo(pawn) ? "yes" : "no"));
            foreach (Chore.Kind kind in System.Enum.GetValues(typeof(Chore.Kind)))
                sb.AppendLine($"  {kind}: {ChoreManager.Instance?.CantReason(pawn, kind) ?? "may start"}");
            sb.Append("[Colony work] " + ColonyWork.Line(pawn));
            ModLog.Message(sb.ToString());
        }

        /// <summary>Applies the most useful stock-up option of one kind for the selected colonist, ignoring cooldowns and caps. No LLM.</summary>
        [DebugAction("AI Pawn Control", "Apply chore", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static List<DebugActionNode> ApplyChore()
        {
            return new[] { Chore.Kind.Cut, Chore.Kind.Mine, Chore.Kind.Bill, Chore.Kind.Hunt, Chore.Kind.Gather }
                .Select(kind => new DebugActionNode(kind.ToString(), DebugActionType.Action, () =>
                {
                    Pawn pawn = Selected();
                    if (pawn == null)
                        return;
                    var option = ChoreOptions.All(pawn, ignoreLimits: true).Where(o => o.kind == kind).OrderByDescending(o => o.useful).FirstOrDefault();
                    if (option == null)
                    {
                        ModLog.Message($"Apply chore {kind}: no option for {pawn.LabelShort}.");
                        return;
                    }
                    var mind = MindManager.Instance?.MindOf(pawn) ?? new PawnMind(pawn, null);
                    ModLog.Message($"Apply chore {kind} for {pawn.LabelShort}: {option.label} → {option.apply(mind)}");
                }))
                .ToList();
        }

        /// <summary>The real Base call for the selected mind, ignoring budget, cooldowns and caps.</summary>
        [DebugAction("AI Pawn Control", "Base call now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void BaseCallNow()
        {
            Pawn pawn = Selected();
            var mind = pawn != null ? MindManager.Instance?.MindOf(pawn) : null;
            if (mind == null || mind.persona == null || mind.Thinking)
            {
                Messages.Message("Select a colonist with a mind that isn't thinking.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            mind.AddDecision("DEV base call now: " + BaseCall.Start(mind, dev: true), importance: 0);
        }

        /// <summary>Every ladder rung and its verdict, and the [Colony] line.</summary>
        [DebugAction("AI Pawn Control", "Ladder now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void LadderNow()
        {
            Map map = Find.CurrentMap;
            var sb = new StringBuilder("Ladder:\n");
            foreach (var r in Ladder.Evaluate(map))
                sb.AppendLine($"  {r.label} ({r.kind?.label ?? "-"}): {(r.met ? "met" : r.underway ? "underway" : "NOT MET")}{(r.waiting != null ? " | " + r.waiting : "")}");
            sb.Append("[Colony] Base: " + Ladder.Line(map));
            ModLog.Message(sb.ToString());
        }

        /// <summary>The food outlook's numbers, for checking them by hand.</summary>
        [DebugAction("AI Pawn Control", "Food outlook now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void FoodOutlookNow()
        {
            Map map = Find.CurrentMap;
            var o = FoodOutlook.For(map);
            var sb = new StringBuilder($"Food outlook: {o.colonists} colonists, need {o.needPerDay:0.00}/day, fields grow {o.growPerDay:0.00}/day, stores {o.stores:0}\n");
            foreach (var zone in map.zoneManager.AllZones.OfType<Zone_Growing>())
            {
                ThingDef plant = zone.GetPlantDefToGrow();
                if (plant == null)
                    continue;
                float fertility = zone.cells.Average(c => map.fertilityGrid.FertilityAt(c));
                sb.AppendLine($"  {plant.label}: {zone.cells.Count} cells, fertility {fertility:0.00}, {Fields.Purpose(plant)}, harvest {FoodOutlook.PerHarvest(plant):0.00}/plant " +
                              $"every {FoodOutlook.CycleDays(plant, fertility):0.0} days → {zone.cells.Count * FoodOutlook.PerCellPerDay(plant, fertility):0.00}/day");
            }
            sb.AppendLine($"  crop for a new field: {o.crop?.label ?? "none"} (in season: {o.cropInSeason}), winter: {(o.hasWinter ? $"in {o.daysToWinter} days for {o.winterDays}, cover {o.WinterCover:0}" : "none")}");
            sb.AppendLine($"  unsown field: {o.unsownField}, a new field: {o.cellsWanted} cells ({o.FieldSide}x{o.FieldSide})");
            sb.Append("[Colony] Food: " + o.Line());
            ModLog.Message(sb.ToString());
        }

        /// <summary>Outfits the selected colonist's finished rooms again (bills and stockpiles they don't have yet).</summary>
        [DebugAction("AI Pawn Control", "Outfit my rooms now", allowedGameStates = AllowedGameStates.PlayingOnMap)]
        public static void OutfitNow()
        {
            Pawn pawn = Selected();
            if (pawn == null || BuildManager.Instance == null)
                return;
            foreach (var project in BuildManager.Instance.ProjectsOf(pawn).Where(p => p.state == BuildProject.State.Done && !p.furnishing && p.map == pawn.Map).ToList())
            {
                project.outfitted = true;
                Outfitting.Outfit(project);
            }
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

        /// <summary>Every stock-up option on the map (per colonist), each checked by its validator; a report file and a summary line.</summary>
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
                    sb.AppendLine($"  [{o.kind}] {o.useful:0.0} {o.label}{(check != null ? " | VALIDATOR FAILED: " + check : "")}");
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
