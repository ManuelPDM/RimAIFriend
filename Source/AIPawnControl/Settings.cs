using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    public class Settings : ModSettings
    {
        public string endpoint = "http://localhost:1234/v1";
        public string model = "qwen/qwen3.6-35b-a3b";
        public float temperature = 0.7f;
        public bool thinking;
        public int timeoutSeconds = 120;
        public int maxTokens = 1024;
        public bool logPrompts = true;
        public int periodicHours = 4;
        public int actsPerDay = 10;
        public bool speakLines = true;
        public bool chatBubbles = true;
        public bool memoryEnabled = true; // off: no Reflect and no retrieval; events are still kept
        public bool allowBuilding = true;  // off: no room projects offered; a running one is left as-is
        public bool allowChores = true;    // off: no colony chores offered (Phase 5); what's set up stays
        public bool mindsAnswer = true;    // off: a talk between two minds carries only the first line (PHASE6.md §4)
        public int parallelRequests = 1;   // requests LM Studio runs at once (its own parallel setting must allow it)
        public string embedEndpoint = ""; // empty: same as the chat endpoint
        public string embedModel = "google/embedding-gemma-300m";
        public int embedDims;              // the model's own size, as detected by "Test embeddings"
        public string embedDimsModel;      // the model embedDims was detected for

        private string testStatus;
        private string embedTestStatus;
        private string periodicBuffer;
        private string actsBuffer;
        private string timeoutBuffer;
        private string maxTokensBuffer;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref endpoint, "endpoint", "http://localhost:1234/v1");
            Scribe_Values.Look(ref model, "model", "qwen/qwen3.6-35b-a3b");
            Scribe_Values.Look(ref temperature, "temperature", 0.7f);
            Scribe_Values.Look(ref thinking, "thinking");
            Scribe_Values.Look(ref timeoutSeconds, "timeoutSeconds", 120);
            Scribe_Values.Look(ref maxTokens, "maxTokens", 1024);
            Scribe_Values.Look(ref logPrompts, "logPrompts", true);
            Scribe_Values.Look(ref periodicHours, "periodicHours", 4);
            Scribe_Values.Look(ref actsPerDay, "actsPerDay", 10);
            Scribe_Values.Look(ref speakLines, "speakLines", true);
            Scribe_Values.Look(ref chatBubbles, "chatBubbles", true);
            Scribe_Values.Look(ref memoryEnabled, "memoryEnabled", true);
            Scribe_Values.Look(ref allowBuilding, "allowBuilding", true);
            Scribe_Values.Look(ref allowChores, "allowChores", true);
            Scribe_Values.Look(ref mindsAnswer, "mindsAnswer", true);
            Scribe_Values.Look(ref parallelRequests, "parallelRequests", 1);
            Scribe_Values.Look(ref embedEndpoint, "embedEndpoint", "");
            Scribe_Values.Look(ref embedModel, "embedModel", "google/embedding-gemma-300m");
            Scribe_Values.Look(ref embedDims, "embedDims");
            Scribe_Values.Look(ref embedDimsModel, "embedDimsModel");
        }

        public string EmbedEndpoint => string.IsNullOrWhiteSpace(embedEndpoint) ? endpoint : embedEndpoint;

        public void DoWindowContents(Rect rect)
        {
            var list = new Listing_Standard();
            list.Begin(rect);

            endpoint = list.TextEntryLabeled("AIPawnControl_Endpoint".Translate(), endpoint);
            model = list.TextEntryLabeled("AIPawnControl_Model".Translate(), model);
            list.Label("AIPawnControl_Temperature".Translate(temperature.ToString("0.00")));
            temperature = list.Slider(temperature, 0f, 1.5f);
            list.TextFieldNumericLabeled("AIPawnControl_Timeout".Translate(), ref timeoutSeconds, ref timeoutBuffer, 5, 600);
            list.TextFieldNumericLabeled("AIPawnControl_MaxTokens".Translate(), ref maxTokens, ref maxTokensBuffer, 64, 8192);
            list.CheckboxLabeled("AIPawnControl_Thinking".Translate(), ref thinking, "AIPawnControl_ThinkingTip".Translate());
            list.CheckboxLabeled("AIPawnControl_LogPrompts".Translate(), ref logPrompts, "AIPawnControl_LogPromptsTip".Translate());
            list.TextFieldNumericLabeled("AIPawnControl_PeriodicHours".Translate(), ref periodicHours, ref periodicBuffer, 1, 24);
            list.TextFieldNumericLabeled("AIPawnControl_ActsPerDay".Translate(), ref actsPerDay, ref actsBuffer, 0, 100);
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
            list.CheckboxLabeled("AIPawnControl_MindsAnswer".Translate(), ref mindsAnswer, "AIPawnControl_MindsAnswerTip".Translate());
            list.Label("AIPawnControl_ParallelRequests".Translate(parallelRequests), tooltip: "AIPawnControl_ParallelRequestsTip".Translate());
            parallelRequests = (int)System.Math.Round(list.Slider(parallelRequests, 1, 4));
            embedEndpoint = list.TextEntryLabeled("AIPawnControl_EmbedEndpoint".Translate(), embedEndpoint);
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
