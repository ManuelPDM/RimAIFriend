using HarmonyLib;
using Verse;

namespace AIPawnControl
{
    public class AIPawnControlMod : Mod
    {
        public AIPawnControlMod(ModContentPack content) : base(content)
        {
            new Harmony("mpreston.aipawncontrol").PatchAll();
            Log.Message("[AI Pawn Control] Loaded.");
        }
    }
}
