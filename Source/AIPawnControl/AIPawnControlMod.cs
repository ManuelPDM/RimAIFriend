using HarmonyLib;
using UnityEngine;
using Verse;

namespace AIPawnControl
{
    public class AIPawnControlMod : Mod
    {
        public static Settings Settings { get; private set; }
        public static string RootDir { get; private set; }

        public AIPawnControlMod(ModContentPack content) : base(content)
        {
            RootDir = content.RootDir;
            Settings = GetSettings<Settings>();
            ModLog.Init();
            new Harmony("mpreston.aipawncontrol").PatchAll();
            Log.Message("[AI Pawn Control] Loaded.");
        }

        public override string SettingsCategory() => "AIPawnControl_ModName".Translate();

        public override void DoSettingsWindowContents(Rect inRect) => Settings.DoWindowContents(inRect);
    }
}
