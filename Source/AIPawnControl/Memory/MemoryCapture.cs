using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace AIPawnControl
{
    /// <summary>
    /// Records what happens to our minds as raw events (PHASE3.md §3). Code only: no LLM and no vectors. Every hook
    /// first asks "is this one of our minds?", so it costs next to nothing for everyone else.
    /// </summary>
    public static class MemoryCapture
    {
        private const float WitnessRange = 8f;
        private const float MinThoughtOffset = 3f;

        private static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, InteractionDef> TalkDef =
            AccessTools.FieldRefAccess<PlayLogEntry_Interaction, InteractionDef>("intDef");
        private static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, Pawn> TalkRecipient =
            AccessTools.FieldRefAccess<PlayLogEntry_Interaction, Pawn>("recipient");
        internal static readonly AccessTools.FieldRef<MentalStateHandler, Pawn> BreakPawn =
            AccessTools.FieldRefAccess<MentalStateHandler, Pawn>("pawn");
        internal static readonly AccessTools.FieldRef<Pawn_RelationsTracker, Pawn> RelationsPawn =
            AccessTools.FieldRefAccess<Pawn_RelationsTracker, Pawn>("pawn");

        internal static PawnMind MindOf(Pawn pawn) => pawn != null ? MindManager.Instance?.MindOf(pawn) : null;

        private static string Name(Pawn pawn, Pawn me) => pawn == me ? "me" : pawn?.LabelShort;

        private static string Short(string text, int max = 200)
        {
            text = text?.StripTags().Replace("\n", " ").Trim();
            return text == null || text.Length <= max ? text : text.Substring(0, max - 1).TrimEnd() + "…";
        }

        /// <summary>Mood (or, for social thoughts, opinion) offsets of 3+. |3| → 3 up to |20| → 8.</summary>
        internal static void OnThought(Pawn pawn, Thought_Memory thought)
        {
            var mind = MindOf(pawn);
            if (mind == null || !ThoughtUtility.CanGetThought(pawn, thought.def))
                return;
            float mood = thought.MoodOffset();
            float opinion = thought is Thought_MemorySocial social ? social.OpinionOffset() : 0f;
            float offset = Math.Abs(opinion) > Math.Abs(mood) ? opinion : mood;
            if (Math.Abs(offset) < MinThoughtOffset)
                return;
            int importance = 3 + (int)Math.Round((Math.Min(Math.Abs(offset), 20f) - 3f) * 5f / 17f);
            Pawn other = thought.otherPawn;
            string effect = offset == opinion && other != null ? $"opinion of {other.LabelShort} {opinion:+0;-0}" : $"mood {mood:+0;-0}";
            mind.memory.Record(pawn, "thought", thought.def.defName, $"{thought.LabelCap} ({effect})", importance, MemoryEvent.TookPart,
                other != null ? new[] { other.LabelShort } : null, merge: true);
        }

        /// <summary>Talks she's part of, and talks between two others she can see from nearby while awake.</summary>
        internal static void OnPlayLog(LogEntry entry)
        {
            var manager = MindManager.Instance;
            if (manager == null || !(entry is PlayLogEntry_Interaction talk))
                return;
            Pawn initiator = SpeechLog.TalkInitiator(talk), recipient = TalkRecipient(talk);
            InteractionDef def = TalkDef(talk);
            if (initiator == null || recipient == null || def == null)
                return;
            int importance = ActionCatalog.LifeChangingInteractions.Contains(def.defName) ? 7
                : ActionCatalog.IsNegative(def) ? 5
                : def == InteractionDefOf.DeepTalk ? 3 : 2;
            foreach (var mind in manager.Minds)
            {
                Pawn me = mind.pawn;
                if (me == initiator || me == recipient)
                {
                    Pawn other = me == initiator ? recipient : initiator;
                    bool spoken = manager.Lines.TryGet(entry.LogID, out string line); // our line replaces the vanilla text
                    string text = spoken ? $"{def.LabelCap} with {Name(other, me)}, I said: \"{line}\"" : Short(entry.ToGameStringFromPOV(me));
                    mind.memory.Record(me, "talk", def.defName, text, importance, MemoryEvent.TookPart, new[] { other.LabelShort }, merge: !spoken);
                }
                else if (Witnesses(me, initiator, recipient))
                {
                    bool spoken = manager.Lines.TryGet(entry.LogID, out string line);
                    string text = spoken ? $"{def.LabelCap}: {initiator.LabelShort} said to {recipient.LabelShort}: \"{line}\"" : Short(entry.ToGameStringFromPOV(initiator));
                    mind.memory.Record(me, "talk", def.defName, text, importance - 1, MemoryEvent.Saw,
                        new[] { initiator.LabelShort, recipient.LabelShort }, merge: !spoken);
                }
            }
        }

        private static bool Witnesses(Pawn me, Pawn a, Pawn b)
        {
            if (!me.Spawned || me.Map != a.Map || !me.Awake() || me.Downed)
                return false;
            foreach (var p in new[] { a, b })
                if (p.Spawned && p.Position.InHorDistOf(me.Position, WitnessRange) && GenSight.LineOfSight(me.Position, p.Position, me.Map))
                    return true;
            return false;
        }

        /// <summary>Colony news: every mind hears about every letter that shows up, except letters about herself.</summary>
        internal static void OnLetter(Letter letter)
        {
            var manager = MindManager.Instance;
            if (manager == null || !letter.CanShowInLetterStack)
                return;
            LetterDef def = letter.def;
            int importance = def == LetterDefOf.Death || def == LetterDefOf.ThreatBig ? 8
                : def == LetterDefOf.ThreatSmall ? 6
                : def == LetterDefOf.NegativeEvent ? 5
                : def == LetterDefOf.PositiveEvent ? 4 : 3;
            string text = letter.Label.ToString();
            if (letter is ChoiceLetter choice)
            {
                string body = choice.Text.ToString().StripTags();
                int end = body.IndexOfAny(new[] { '.', '!', '?', '\n' });
                if (end > 0)
                    text += ": " + body.Substring(0, end + 1);
            }
            var pawns = letter.lookTargets?.targets.Select(t => t.Thing as Pawn).Where(p => p != null && p.RaceProps.Humanlike).ToList() ?? new List<Pawn>();
            foreach (var mind in manager.Minds)
            {
                if (pawns.Contains(mind.pawn))
                    continue; // news about her own break, illness or wedding: the first-hand hooks already have it
                mind.memory.Record(mind.pawn, "letter", def.defName, Short(text), importance, MemoryEvent.News,
                    pawns.Select(p => p.LabelShort), place: "");
            }
        }

        /// <summary>A bad, visible hediff on her. Injuries from the same attacker within 2 hours merge.</summary>
        internal static void OnHediff(Hediff hediff, DamageInfo? dinfo)
        {
            Pawn pawn = hediff.pawn;
            var mind = MindOf(pawn);
            if (mind == null || !hediff.def.isBad || !hediff.Visible)
                return;
            Thing attacker = dinfo?.Instigator;
            string part = hediff.Part != null ? $" ({hediff.Part.Label})" : "";
            bool injury = hediff is Hediff_Injury || hediff is Hediff_MissingPart;
            string text = injury ? $"I got hurt: {hediff.Label}{part}" : $"New condition: {hediff.Label}{part}";
            if (attacker != null && attacker != pawn)
                text += $", by {attacker.LabelShort}";
            var people = attacker is Pawn by && by != pawn ? new[] { by.LabelShort } : null;
            mind.memory.Record(pawn, "hurt", injury ? "injury" : hediff.def.defName, text, 6, MemoryEvent.TookPart, people, merge: true);
        }

        internal static void OnMentalState(Pawn pawn, MentalStateDef def, string reason, Pawn otherPawn)
        {
            var mind = MindOf(pawn);
            if (mind == null)
                return;
            string text = $"I had a mental break: {def.label}" + (string.IsNullOrEmpty(reason) ? "" : $" ({Short(reason, 120)})");
            mind.memory.Record(pawn, "break", def.defName, text, 6, MemoryEvent.TookPart, otherPawn != null ? new[] { otherPawn.LabelShort } : null);
        }

        internal static void OnRelation(Pawn pawn, Pawn other, PawnRelationDef def, bool added)
        {
            if (Current.ProgramState != ProgramState.Playing || PawnGenerator.IsBeingGenerated(pawn) || PawnGenerator.IsBeingGenerated(other))
                return;
            foreach (var (me, them) in new[] { (pawn, other), (other, pawn) })
            {
                var mind = MindOf(me);
                if (mind == null)
                    continue;
                string label = def.GetGenderSpecificLabel(them);
                string text = added ? $"{them.LabelShort} is now my {label}" : $"{them.LabelShort} is no longer my {label}";
                mind.memory.Record(me, "relation", def.defName, text, 7, MemoryEvent.TookPart, new[] { them.LabelShort });
            }
        }

        /// <summary>Tales name their pawns in a def-specific order (actor first, usually), so they're listed as given.</summary>
        internal static void OnTale(TaleDef def, object[] args)
        {
            if (args == null || MindManager.Instance == null)
                return;
            var pawns = args.OfType<Pawn>().ToList();
            foreach (var me in pawns)
            {
                var mind = MindOf(me);
                if (mind == null)
                    continue;
                string text = $"{def.LabelCap}: {string.Join(", ", pawns.Select(p => Name(p, me)))}";
                mind.memory.Record(me, "tale", def.defName, text, 4, MemoryEvent.TookPart, pawns.Where(p => p != me).Select(p => p.LabelShort), merge: true);
            }
        }
    }

    [HarmonyPatch(typeof(MemoryThoughtHandler), nameof(MemoryThoughtHandler.TryGainMemory), typeof(Thought_Memory), typeof(Pawn))]
    internal static class Patch_MemoryThoughtHandler_TryGainMemory
    {
        private static void Postfix(MemoryThoughtHandler __instance, Thought_Memory newThought) => MemoryCapture.OnThought(__instance.pawn, newThought);
    }

    [HarmonyPatch(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), typeof(Letter), typeof(string), typeof(int), typeof(bool))]
    internal static class Patch_LetterStack_ReceiveLetter
    {
        private static void Postfix(Letter let, int delayTicks)
        {
            if (delayTicks <= 0) // a delayed letter comes back through here when it arrives
                MemoryCapture.OnLetter(let);
        }
    }

    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff), typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult))]
    internal static class Patch_Pawn_HealthTracker_AddHediff
    {
        private static void Postfix(Hediff hediff, DamageInfo? dinfo) => MemoryCapture.OnHediff(hediff, dinfo);
    }

    [HarmonyPatch(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState))]
    internal static class Patch_MentalStateHandler_TryStartMentalState
    {
        private static void Postfix(MentalStateHandler __instance, bool __result, MentalStateDef stateDef, string reason, Pawn otherPawn)
        {
            if (__result)
                MemoryCapture.OnMentalState(MemoryCapture.BreakPawn(__instance), stateDef, reason, otherPawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.AddDirectRelation))]
    internal static class Patch_Pawn_RelationsTracker_AddDirectRelation
    {
        private static void Prefix(Pawn_RelationsTracker __instance, PawnRelationDef def, Pawn otherPawn, out bool __state) =>
            __state = otherPawn != null && !__instance.DirectRelationExists(def, otherPawn); // vanilla warns and returns on duplicates

        private static void Postfix(Pawn_RelationsTracker __instance, PawnRelationDef def, Pawn otherPawn, bool __state)
        {
            if (__state && __instance.DirectRelationExists(def, otherPawn))
                MemoryCapture.OnRelation(MemoryCapture.RelationsPawn(__instance), otherPawn, def, added: true);
        }
    }

    [HarmonyPatch(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.TryRemoveDirectRelation))]
    internal static class Patch_Pawn_RelationsTracker_TryRemoveDirectRelation
    {
        private static void Postfix(Pawn_RelationsTracker __instance, bool __result, PawnRelationDef def, Pawn otherPawn)
        {
            if (__result)
                MemoryCapture.OnRelation(MemoryCapture.RelationsPawn(__instance), otherPawn, def, added: false);
        }
    }

    [HarmonyPatch(typeof(TaleRecorder), nameof(TaleRecorder.RecordTale))]
    internal static class Patch_TaleRecorder_RecordTale
    {
        private static void Postfix(TaleDef def, object[] args) => MemoryCapture.OnTale(def, args);
    }
}
