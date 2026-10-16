using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    public class Settings : ModSettings
    {
        public string endpoint = "http://localhost:1234/v1";
        public string apiKey = "";      // sent as a Bearer header; empty: none
        public string model = "qwen/qwen3.6-35b-a3b";
        public float temperature = 0.7f;
        public bool thinking;
        public int timeoutSeconds = 120;
        public int maxTokens = 1024;
        public bool logPrompts = true;
        public int periodicHours = 4;
        public int actsPerDay = 10;
        public int messagesPerDay = 20;    // Message calls that send something; 0: no private messages, and nobody answers a talk
        public bool speakLines = true;
        public bool chatBubbles = true;
        public bool memoryEnabled = true; // off: no Reflect and no retrieval; events are still kept
        public bool allowBuilding = true;  // off: no room projects offered; a running one is left as-is
        public bool allowChores = true;    // off: no colony chores offered (Phase 5); what's set up stays
        public bool answerChoices = true;  // off: joiners, ransoms, quest offers and beggars are the player's (WORLD.md)
        public bool gatherings = true;     // off: no rituals, roles, reforms, parties or weddings started by minds (IDEOLOGY.md)
        public int parallelRequests = 4;  // requests LM Studio runs at once (its own parallel setting must allow it)
        public string embedEndpoint = ""; // empty: same as the chat endpoint
        public string embedApiKey = "";   // used only when embedEndpoint is set
        public string embedModel = "google/embedding-gemma-300m";
        public int embedDims;              // the model's own size, as detected by "Test embeddings"
        public string embedDimsModel;      // the model embedDims was detected for

        private string testStatus;
        private string embedTestStatus;
        private string periodicBuffer;
        private string actsBuffer;
        private string messagesBuffer;
        private string timeoutBuffer;
        private string maxTokensBuffer;
        private bool showApiKey;
        private bool showEmbedApiKey;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref endpoint, "endpoint", "http://localhost:1234/v1");
            Scribe_Values.Look(ref apiKey, "apiKey", "");
            Scribe_Values.Look(ref model, "model", "qwen/qwen3.6-35b-a3b");
            Scribe_Values.Look(ref temperature, "temperature", 0.7f);
            Scribe_Values.Look(ref thinking, "thinking");
            Scribe_Values.Look(ref timeoutSeconds, "timeoutSeconds", 120);
            Scribe_Values.Look(ref maxTokens, "maxTokens", 1024);
            Scribe_Values.Look(ref logPrompts, "logPrompts", true);
            Scribe_Values.Look(ref periodicHours, "periodicHours", 4);
            Scribe_Values.Look(ref actsPerDay, "actsPerDay", 10);
            Scribe_Values.Look(ref messagesPerDay, "messagesPerDay", 20);
            Scribe_Values.Look(ref speakLines, "speakLines", true);
            Scribe_Values.Look(ref chatBubbles, "chatBubbles", true);
            Scribe_Values.Look(ref memoryEnabled, "memoryEnabled", true);
            Scribe_Values.Look(ref allowBuilding, "allowBuilding", true);
            Scribe_Values.Look(ref allowChores, "allowChores", true);
            Scribe_Values.Look(ref answerChoices, "answerChoices", true);
            Scribe_Values.Look(ref gatherings, "gatherings", true);
            Scribe_Values.Look(ref parallelRequests, "parallelRequests", 4);
            Scribe_Values.Look(ref embedEndpoint, "embedEndpoint", "");
            Scribe_Values.Look(ref embedApiKey, "embedApiKey", "");
            Scribe_Values.Look(ref embedModel, "embedModel", "google/embedding-gemma-300m");
            Scribe_Values.Look(ref embedDims, "embedDims");
            Scribe_Values.Look(ref embedDimsModel, "embedDimsModel");
        }

        public string EmbedEndpoint => string.IsNullOrWhiteSpace(embedEndpoint) ? endpoint : embedEndpoint;
        public string EmbedApiKey => string.IsNullOrWhiteSpace(embedEndpoint) ? apiKey : embedApiKey;

        /// <summary>A labeled key field that shows only asterisks until "Show" is pressed.</summary>
        private static string KeyEntry(Listing_Standard list, string label, string key, ref bool shown)
        {
            Rect row = list.GetRect(Text.LineHeight);
            list.Gap(list.verticalSpacing);
            Rect field = new Rect(row.x, row.y, row.width - 64f, row.height);
            Widgets.Label(field.LeftHalf(), label);
            if (shown)
                key = Widgets.TextField(field.RightHalf(), key);
            else
                Widgets.Label(field.RightHalf(), new string('*', System.Math.Min(key.Length, 24)));
            if (Widgets.ButtonText(row.RightPartPixels(60f), (shown ? "AIPawnControl_HideKey" : "AIPawnControl_ShowKey").Translate()))
                shown = !shown;
            return key;
        }

        public void DoWindowContents(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);

            endpoint = list.TextEntryLabeled("AIPawnControl_Endpoint".Translate(), endpoint);
            apiKey = KeyEntry(list, "AIPawnControl_ApiKey".Translate(), apiKey, ref showApiKey);
            model = list.TextEntryLabeled("AIPawnControl_Model".Translate(), model);
            list.Label("AIPawnControl_Temperature".Translate(temperature.ToString("0.00")));
            temperature = list.Slider(temperature, 0f, 1.5f);
            list.TextFieldNumericLabeled("AIPawnControl_Timeout".Translate(), ref timeoutSeconds, ref timeoutBuffer, 5, 600);
            list.TextFieldNumericLabeled("AIPawnControl_MaxTokens".Translate(), ref maxTokens, ref maxTokensBuffer, 64, 8192);
            list.CheckboxLabeled("AIPawnControl_Thinking".Translate(), ref thinking, "AIPawnControl_ThinkingTip".Translate());
            list.CheckboxLabeled("AIPawnControl_LogPrompts".Translate(), ref logPrompts, "AIPawnControl_LogPromptsTip".Translate());
            list.TextFieldNumericLabeled("AIPawnControl_PeriodicHours".Translate(), ref periodicHours, ref periodicBuffer, 1, 24);
            list.TextFieldNumericLabeled("AIPawnControl_ActsPerDay".Translate(), ref actsPerDay, ref actsBuffer, 0, 100);
            list.TextFieldNumericLabeled("AIPawnControl_MessagesPerDay".Translate(), ref messagesPerDay, ref messagesBuffer, 0, 100);
            list.CheckboxLabeled("AIPawnControl_SpeakLines".Translate(), ref speakLines, "AIPawnControl_SpeakLinesTip".Translate());
            list.CheckboxLabeled("AIPawnControl_ChatBubbles".Translate(), ref chatBubbles, "AIPawnControl_ChatBubblesTip".Translate());

            list.Gap();
            if (list.ButtonText("AIPawnControl_TestConnection".Translate()))
            {
                testStatus = "AIPawnControl_Testing".Translate();
                LlmClient.Ping(r => testStatus = r.Ok
                    ? "AIPawnControl_TestOk".Translate(r.Seconds.ToString("0.0"), r.Content.Trim())
                    : "AIPawnControl_TestFailed".Translate(r.Error));
            }
            if (testStatus != null)
                list.Label(testStatus);

            list.GapLine();
            list.CheckboxLabeled("AIPawnControl_Memory".Translate(), ref memoryEnabled, "AIPawnControl_MemoryTip".Translate());
            list.CheckboxLabeled("AIPawnControl_AllowBuilding".Translate(), ref allowBuilding, "AIPawnControl_AllowBuildingTip".Translate());
            list.CheckboxLabeled("AIPawnControl_AllowChores".Translate(), ref allowChores, "AIPawnControl_AllowChoresTip".Translate());
            list.CheckboxLabeled("AIPawnControl_AnswerChoices".Translate(), ref answerChoices, "AIPawnControl_AnswerChoicesTip".Translate());
            list.CheckboxLabeled("AIPawnControl_Gatherings".Translate(), ref gatherings, "AIPawnControl_GatheringsTip".Translate());
            var manager = MindManager.Instance;
            if (manager != null)
            {
                string none = "AIPawnControl_PlayAsNone".Translate();
                if (list.ButtonTextLabeled("AIPawnControl_PlayAs".Translate(), manager.PlayingAs?.LabelShort ?? none, tooltip: "AIPawnControl_PlayAsTip".Translate()))
                {
                    var options = new List<FloatMenuOption> { new FloatMenuOption(none, () => manager.PlayingAs = null) };
                    foreach (Pawn p in PawnsFinder.AllMaps_FreeColonists.Where(p => manager.MindOf(p) == null))
                        options.Add(new FloatMenuOption(p.LabelShort, () => manager.PlayingAs = p));
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            }
            list.Label("AIPawnControl_ParallelRequests".Translate(parallelRequests), tooltip: "AIPawnControl_ParallelRequestsTip".Translate());
            parallelRequests = (int)System.Math.Round(list.Slider(parallelRequests, 1, 4));
            embedEndpoint = list.TextEntryLabeled("AIPawnControl_EmbedEndpoint".Translate(), embedEndpoint);
            if (!string.IsNullOrWhiteSpace(embedEndpoint))
                embedApiKey = KeyEntry(list, "AIPawnControl_EmbedApiKey".Translate(), embedApiKey, ref showEmbedApiKey);
            embedModel = list.TextEntryLabeled("AIPawnControl_EmbedModel".Translate(), embedModel);
            if (list.ButtonText("AIPawnControl_TestEmbeddings".Translate()))
            {
                embedTestStatus = "AIPawnControl_Testing".Translate();
                string testedModel = embedModel;
                EmbedClient.Embed(new List<string> { "We survived our first winter together." }, query: false, r =>
                {
                    if (!r.Ok)
                    {
                        embedTestStatus = "AIPawnControl_EmbedTestFailed".Translate(r.Error);
                        return;
                    }
                    embedDims = r.NativeDims;
                    embedDimsModel = testedModel;
                    embedTestStatus = "AIPawnControl_EmbedTestOk".Translate(r.Seconds.ToString("0.0"), r.NativeDims, r.Vectors[0].Length);
                });
            }
            list.Label(embedTestStatus ?? (embedDimsModel == embedModel && embedDims > 0
                ? "AIPawnControl_EmbedDetected".Translate(embedDims).ToString()
                : "AIPawnControl_EmbedNotTested".Translate().ToString()));

            list.Gap();
            if (list.ButtonText("AIPawnControl_ReloadPrompts".Translate()))
            {
                Prompts.Reload();
                Messages.Message("AIPawnControl_PromptsReloaded".Translate(Prompts.Folder), MessageTypeDefOf.NeutralEvent, false);
            }

            list.End();
        }
    }
}
