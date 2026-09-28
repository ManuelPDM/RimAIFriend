using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Puts our lines into vanilla log entries without any custom entry class (PHASE2.md §1), so removing the mod
    /// can't break saves. Talks: capture the entry TryInteractWith adds. Solo speech: a vanilla single-pawn entry
    /// that is left out of saves. The text itself lives in MindManager, keyed by LogEntry.LogID.
    /// </summary>
    public static class SpeechLog
    {
        [ThreadStatic] private static string pendingLine;
        [ThreadStatic] private static Pawn pendingSpeaker;
        internal static bool inSocialLog;

        internal static readonly AccessTools.FieldRef<PlayLogEntry_Interaction, Pawn> TalkInitiator =
            AccessTools.FieldRefAccess<PlayLogEntry_Interaction, Pawn>("initiator");
        internal static readonly AccessTools.FieldRef<PlayLogEntry_InteractionSinglePawn, Pawn> SoloInitiator =
            AccessTools.FieldRefAccess<PlayLogEntry_InteractionSinglePawn, Pawn>("initiator");

        /// <summary>Runs a vanilla interaction; if it adds a talk entry started by this pawn, that entry shows our line.</summary>
        public static bool WithLine(Pawn speaker, string line, Func<bool> interact)
        {
            if (string.IsNullOrEmpty(line) || !AIPawnControlMod.Settings.speakLines)
                return interact();
            pendingLine = line;
            pendingSpeaker = speaker;
            try
            {
                return interact();
            }
            finally
            {
                pendingLine = null;
                pendingSpeaker = null;
            }
        }

        /// <summary>A line said out loud to nobody in particular: a single-pawn entry (bubble + her log), never saved.
        /// Callers check the settings: remarks follow "Speak lines", chat replies follow "Chat bubbles".</summary>
        public static void Say(Pawn speaker, string line)
        {
            var lines = MindManager.Instance?.Lines;
            if (string.IsNullOrEmpty(line) || lines == null || !speaker.Spawned)
                return;
            var entry = new PlayLogEntry_InteractionSinglePawn(InteractionDefOf.Chitchat, speaker, new List<RulePackDef>());
            lines.Register(entry, line, solo: true);
            Find.PlayLog.Add(entry);
        }

        public const int MaxSpokenLength = 160;
        private static readonly Regex Actions = new Regex(@"\*[^*]*\*");
        private static readonly Regex Spaces = new Regex(@"\s+");

        /// <summary>
        /// Model text → a speakable line, or null for silence: drops *actions* and surrounding quotes, then trims at the
        /// last sentence end within maxLength (the schema's maxLength cuts mid-sentence, so it's set looser).
        /// </summary>
        public static string Clean(string raw, int maxLength = MaxSpokenLength)
        {
            if (raw == null)
                return null;
            string text = Spaces.Replace(Actions.Replace(raw, " "), " ").Trim().Trim('"', '“', '”', '\'').Trim();
            if (text.Length > maxLength)
            {
                int end = text.LastIndexOfAny(new[] { '.', '!', '?', '…' }, maxLength - 1);
                if (end > 0)
                    text = text.Substring(0, end + 1);
                else
                {
                    int space = text.LastIndexOf(' ', maxLength - 2);
                    text = text.Substring(0, space > 0 ? space : maxLength - 1) + "…";
                }
            }
            return text.Length > 0 ? text : null;
        }

        internal static void OnPlayLogAdd(LogEntry entry)
        {
            if (pendingLine == null || !(entry is PlayLogEntry_Interaction talk) || TalkInitiator(talk) != pendingSpeaker)
                return;
            MindManager.Instance?.Lines.Register(entry, pendingLine, solo: false);
            pendingLine = null;
            pendingSpeaker = null;
        }

        internal static Pawn Speaker(LogEntry entry)
        {
            switch (entry)
            {
                case PlayLogEntry_Interaction talk: return TalkInitiator(talk);
                case PlayLogEntry_InteractionSinglePawn solo: return SoloInitiator(solo);
                default: return null;
            }
        }
    }

    /// <summary>Our lines by LogEntry.LogID. Talk lines are saved; solo ones aren't, because their entries never are.</summary>
    public class SpokenLines : IExposable
    {
        private Dictionary<int, string> lines = new Dictionary<int, string>();
        private readonly HashSet<int> soloIds = new HashSet<int>();

        public bool TryGet(int logId, out string line) => lines.TryGetValue(logId, out line);

        public bool IsSolo(LogEntry entry) => soloIds.Contains(entry.LogID);

        public void Register(LogEntry entry, string line, bool solo)
        {
            Prune();
            lines[entry.LogID] = line;
            if (solo)
                soloIds.Add(entry.LogID);
        }

        /// <summary>Keeps only ids still in the PlayLog (it holds at most 150 entries).</summary>
        public void Prune()
        {
            var live = new HashSet<int>();
            foreach (var entry in Find.PlayLog.AllEntries)
                live.Add(entry.LogID);
            var dead = new List<int>();
            foreach (int id in lines.Keys)
                if (!live.Contains(id))
                    dead.Add(id);
            foreach (int id in dead)
            {
                lines.Remove(id);
                soloIds.Remove(id);
            }
        }

        public void ExposeData()
        {
            var scribed = lines;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                scribed = new Dictionary<int, string>();
                foreach (var pair in lines)
                    if (!soloIds.Contains(pair.Key))
                        scribed[pair.Key] = pair.Value;
            }
            Scribe_Collections.Look(ref scribed, "lines", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                lines = scribed ?? new Dictionary<int, string>();
        }
    }

    [HarmonyPatch(typeof(PlayLog), nameof(PlayLog.Add))]
    internal static class Patch_PlayLog_Add
    {
        private static void Postfix(LogEntry entry)
        {
            SpeechLog.OnPlayLogAdd(entry);
            MemoryCapture.OnPlayLog(entry); // after the line is registered, so her own line is recorded
        }
    }

    /// <summary>Every reader (social log, Log tab, Interaction Bubbles) goes through here. Skipping the original avoids
    /// resolving grammar we'd throw away, and vanilla's "POV who isn't initiator" error for solo entries.</summary>
    [HarmonyPatch(typeof(LogEntry), nameof(LogEntry.ToGameStringFromPOV))]
    internal static class Patch_LogEntry_ToGameStringFromPOV
    {
        private static bool Prefix(LogEntry __instance, ref string __result)
        {
            var lines = MindManager.Instance?.Lines;
            if (lines == null || !lines.TryGet(__instance.LogID, out string line))
                return true;
            __result = "\"" + line + "\"";
            if (SpeechLog.inSocialLog)
                __result = (SpeechLog.Speaker(__instance)?.LabelShort ?? "?") + ": " + __result;
            return false;
        }
    }

    /// <summary>The social log shows everyone's lines in one list, so it gets the speaker's name; bubbles don't.</summary>
    [HarmonyPatch(typeof(InteractionCardUtility), nameof(InteractionCardUtility.DrawInteractionsLog))]
    internal static class Patch_InteractionCardUtility_DrawInteractionsLog
    {
        private static void Prefix() => SpeechLog.inSocialLog = true;

        private static Exception Finalizer(Exception __exception)
        {
            SpeechLog.inSocialLog = false;
            return __exception;
        }
    }

    /// <summary>
    /// Solo entries use Chitchat, whose rules need a recipient, so they must never reach a save (the text would
    /// break with the mod removed). Taken out of the list while saving and put back at the same positions.
    /// Works on the list directly, not through PlayLog.Add, so Bubbles doesn't draw them again.
    /// </summary>
    [HarmonyPatch(typeof(PlayLog), nameof(PlayLog.ExposeData))]
    internal static class Patch_PlayLog_ExposeData
    {
        private static void Prefix(PlayLog __instance, out List<KeyValuePair<int, LogEntry>> __state)
        {
            __state = null;
            var lines = MindManager.Instance?.Lines;
            if (Scribe.mode != LoadSaveMode.Saving || lines == null)
                return;
            var entries = __instance.AllEntries;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!lines.IsSolo(entries[i]))
                    continue;
                __state = __state ?? new List<KeyValuePair<int, LogEntry>>();
                __state.Add(new KeyValuePair<int, LogEntry>(i, entries[i]));
            }
            if (__state != null)
                entries.RemoveAll(lines.IsSolo);
        }

        private static void Postfix(PlayLog __instance, List<KeyValuePair<int, LogEntry>> __state)
        {
            if (__state == null)
                return;
            foreach (var pair in __state) // ascending positions, so each insert lands where it was
                __instance.AllEntries.Insert(Math.Min(pair.Key, __instance.AllEntries.Count), pair.Value);
        }
    }
}
