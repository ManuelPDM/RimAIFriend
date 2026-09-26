using System;
using System.Collections.Generic;
using LudeonTK;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>Phase 0 checks: the settings "Test connection" button and dev-mode debug actions.</summary>
    public static class LlmTests
    {
        public static void Ping(Action<LlmResult> onResult)
        {
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("user", "Reply with one short friendly sentence."),
            };
            LlmClient.Send("ping", messages, null, onResult);
        }

        [DebugAction("AI Pawn Control", "Test LLM", allowedGameStates = AllowedGameStates.Playing)]
        private static void TestLlm()
        {
            int startTick = Find.TickManager.TicksGame;
            ModLog.Message($"Test LLM: sending (tick {startTick})");
            Ping(r => Report("Test LLM", r, startTick));
        }

        [DebugAction("AI Pawn Control", "Test LLM (JSON schema)", allowedGameStates = AllowedGameStates.Playing)]
        private static void TestLlmSchema()
        {
            int startTick = Find.TickManager.TicksGame;
            var messages = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("system", "You are Mara, a kind, neurotic ex-medic colonist in RimWorld."),
                new KeyValuePair<string, string>("user",
                    "[Needs] Food 25% (hungry), Rest 70%, Recreation 15%.\n[People nearby] Anna (friend, looks sad).\n" +
                    "Choose one option:\n1: keep going, check back in 4h\n4: talk to Anna: Deep talk\n7: recreation: Horseshoes\n9: rest"),
            };
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["reason"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["choice"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = new List<object> { 1, 4, 7, 9 } },
                },
                ["required"] = new List<object> { "reason", "choice" },
                ["additionalProperties"] = false,
            };
            ModLog.Message($"Test LLM (JSON schema): sending (tick {startTick})");
            LlmClient.Send("schema-test", messages, schema, r =>
            {
                Report("Test LLM (JSON schema)", r, startTick);
                if (!r.Ok)
                    return;
                try
                {
                    var parsed = (Dictionary<string, object>)Json.Parse(r.Content);
                    ModLog.Message($"Schema test parsed OK: choice={parsed["choice"]}, reason={parsed["reason"]}");
                }
                catch (Exception e)
                {
                    ModLog.Error("Schema test reply isn't valid JSON: " + e.Message);
                }
            });
        }

        [DebugAction("AI Pawn Control", "Reload prompts", allowedGameStates = AllowedGameStates.Playing)]
        private static void ReloadPrompts()
        {
            Prompts.Reload();
            ModLog.Message("Prompts will be re-read from " + Prompts.Folder);
        }

        /// <summary>Same as the Work tab's "Manual priorities" checkbox, which automation can't click.</summary>
        [DebugAction("AI Pawn Control", "Toggle manual priorities", allowedGameStates = AllowedGameStates.Playing)]
        private static void ToggleManualPriorities()
        {
            Find.PlaySettings.useWorkPriorities = !Find.PlaySettings.useWorkPriorities;
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
                if (pawn.Faction == Faction.OfPlayer)
                    pawn.workSettings?.Notify_UseWorkPrioritiesChanged();
            ModLog.Message("Manual priorities: " + (Find.PlaySettings.useWorkPriorities ? "ON" : "OFF"));
        }

        private static void Report(string label, LlmResult r, int startTick)
        {
            int ticks = Find.TickManager.TicksGame - startTick;
            if (r.Ok)
                ModLog.Message($"{label}: {r.Seconds:0.0}s, {r.CompletionTokens} tokens, game advanced {ticks} ticks meanwhile. Reply: {r.Content.Trim()}");
            else
                ModLog.Warning($"{label} failed after {r.Seconds:0.0}s (game advanced {ticks} ticks): {r.Error}");
        }
    }
}
