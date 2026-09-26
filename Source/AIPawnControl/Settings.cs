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

        private string testStatus;
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
        }

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

            list.Gap();
            if (list.ButtonText("AIPawnControl_TestConnection".Translate()))
            {
                testStatus = "AIPawnControl_Testing".Translate();
                LlmTests.Ping(r => testStatus = r.Ok
                    ? "AIPawnControl_TestOk".Translate(r.Seconds.ToString("0.0"), r.Content.Trim())
                    : "AIPawnControl_TestFailed".Translate(r.Error));
            }
            if (testStatus != null)
                list.Label(testStatus);

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
