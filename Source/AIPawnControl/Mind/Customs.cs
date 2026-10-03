using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The colony's ideoligion as its customs (IDEOLOGY.md §4, §15): the rules everyone who follows them lives under, never
    /// what a mind believes. [Colony customs], ideoligion roles, and the two-step reform choice.
    /// </summary>
    public static class Customs
    {
        /// <summary>Ideology is on, the game isn't in classic mode, and the colony has an ideoligion.</summary>
        public static bool Active => ModsConfig.IdeologyActive && Find.IdeoManager != null && !Find.IdeoManager.classicMode
                                     && Faction.OfPlayer?.ideos?.PrimaryIdeo != null;

        public static Ideo Primary => Faction.OfPlayer.ideos.PrimaryIdeo;

        /// <summary>A custom as vanilla names it (Thought.cs:95): "Male clothing: Pants and shirt".</summary>
        public static string Label(Precept precept) => $"{precept.def.issue.LabelCap}: {precept.def.LabelCap}";

        /// <summary>The colony's customs that give moods: plain precepts with a thought, in the ideoligion's own order.</summary>
        public static List<Precept> MoodCustoms() =>
            Primary.PreceptsListForReading.Where(p => p.GetType() == typeof(Precept) && p.def.issue != null && p.def.visible
                                                      && p.def.comps.Any(c => c is PreceptComp_Thought)).ToList();

        /// <summary>[Colony customs]: "Male clothing: Pants and shirt · Cannibalism: Abhorrent · …", or null without Ideology.</summary>
        public static string Section(Pawn pawn)
        {
            if (!Active || pawn.Map == null)
                return null;
            var customs = MoodCustoms();
            if (customs.Count == 0)
                return null;
            return string.Join(" · ", customs.Select(Label)) + "."
                   + (pawn.Ideo != null && pawn.Ideo != Primary ? " I don't follow them." : "")
                   + (CanReform ? " One can be changed now." : "");
        }

        /// <summary>Her role in the colony's ideoligion, or null.</summary>
        public static Precept_Role RoleOf(Pawn pawn) => Active ? Primary.GetRole(pawn) : null;

        /// <summary>The leader and the moral guide (vanilla's role tags): the roles with standing, not a skill.</summary>
        public static bool IsStatusRole(Precept_Role role) => role.def.leaderRole || role.def.roleTags?.Contains("Moralist") == true;

        /// <summary>"funeral for Yury": vanilla's word for the ritual and its own label part.</summary>
        public static string ObligationLabel(Precept_Ritual ritual, RitualObligation obligation)
        {
            string extra = obligation != null ? ritual.obligationTargetFilter?.LabelExtraPart(obligation) : null;
            return extra.NullOrEmpty() ? ritual.def.label : $"{ritual.def.label} for {extra}";
        }

        // ---------- Reform (IDEOLOGY.md §5.4, §15) ----------

        /// <summary>Whether the colony's ideoligion can reform now.</summary>
        public static bool CanReform => Active && Primary.Fluid && Primary.development?.CanReformNow == true;

        /// <summary>
        /// The defNames of the customs giving someone a mood now: the reform choice is asked again when these change
        /// (after "keep the customs as they are").
        /// </summary>
        public static string FeltKey(Map map)
        {
            var felt = new HashSet<string>();
            var groups = new List<Thought>();
            foreach (var pawn in map.mapPawns.FreeColonistsSpawned.ToList())
            {
                var thoughts = pawn.needs?.mood?.thoughts;
                if (thoughts == null)
                    continue;
                thoughts.GetDistinctMoodThoughtGroups(groups);
                foreach (var group in groups.Where(g => g.sourcePrecept != null && g.sourcePrecept.GetType() == typeof(Precept)))
                    felt.Add(group.sourcePrecept.def.defName);
            }
            return string.Join(",", felt.OrderBy(n => n));
        }

        /// <summary>The other values a custom can take: vanilla's precept menu (Precept.cs:666-690). Empty if a meme requires it.</summary>
        public static List<PreceptDef> Alternatives(Precept from)
        {
            var ideo = Primary;
            if (ideo.GetMemeThatRequiresPrecept(from.def) != null)
                return new List<PreceptDef>();
            return DefDatabase<PreceptDef>.AllDefs
                .Where(d => d != from.def && d.issue == from.def.issue && d.visible && ideo.CanAddPreceptAllFactions(d).Accepted)
                .OrderBy(d => d.displayOrderInIssue).ToList();
        }

        /// <summary>Step 1's customs: every custom that gives moods and can change.</summary>
        public static List<Precept> Changeable() => MoodCustoms().Where(p => Alternatives(p).Count > 0).ToList();

        public const string PickText = "The colony's customs can change: pick one to change, or keep them as they are.";

        public static string ChangeText(Precept from) => $"You're changing the colony's custom on {from.def.issue.LabelCap}. It's now: {from.def.LabelCap}.";

        /// <summary>
        /// Vanilla's reform path without its window (Dialog_ReformIdeo, Precept.cs:678-680): change a copy, then apply it, which
        /// notifies, resets the points and counts the reform. Null if it can't any more.
        /// </summary>
        public static string Apply(string fromDef, string toDef)
        {
            var ideo = Primary;
            if (!CanReform)
                return null;
            var from = ideo.PreceptsListForReading.FirstOrDefault(p => p.def.defName == fromDef);
            var to = DefDatabase<PreceptDef>.GetNamedSilentFail(toDef);
            if (from == null || to == null || !ideo.CanAddPreceptAllFactions(to).Accepted)
                return null;
            string label = $"{Label(from)} → {to.LabelCap}";
            var copy = IdeoGenerator.MakeIdeo(ideo.foundation.def);
            ideo.CopyTo(copy);
            var old = copy.PreceptsListForReading.First(p => p.def == from.def);
            copy.AddPrecept(PreceptMaker.MakePrecept(to), init: true);
            copy.RemovePrecept(old, replacing: true);
            IdeoDevelopmentUtility.ApplyChangesToIdeo(ideo, copy);
            return $"Changed a colony custom: {label}.";
        }
    }
}
