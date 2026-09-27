using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// Who a name in her memory is: a colonist, one of our animals, a wild animal, an outsider (visitor or enemy), dead
    /// or alive. Events and person files only keep names, so the label is looked up from the game each time.
    /// </summary>
    public static class People
    {
        /// <summary>The pawn with this name: on her map first, then the corpses there, then the world. Null if none.</summary>
        public static Pawn Find(string name, Map map)
        {
            if (string.IsNullOrEmpty(name) || name == PersonFile.Player || name == PersonFile.Colony)
                return null;
            return map?.mapPawns.AllPawns.FirstOrDefault(p => p.LabelShort == name)
                   ?? map?.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Select(c => c.InnerPawn).FirstOrDefault(p => p?.LabelShort == name)
                   ?? Verse.Find.WorldPawns.AllPawnsAliveOrDead.FirstOrDefault(p => p.LabelShort == name);
        }

        /// <summary>"male, colonist, my lover", "our animal, a husky, dead", "female, outsider, enemy (Virus Pillers)".</summary>
        public static string Label(Pawn p, Pawn viewer)
        {
            string label;
            if (p.RaceProps.Humanlike)
            {
                string role = p.IsPrisonerOfColony ? "our prisoner"
                    : p.IsSlaveOfColony ? "our slave"
                    : p.IsColonist ? "colonist"
                    : p.Faction == null ? "outsider"
                    : p.Faction.HostileTo(Faction.OfPlayer) ? $"outsider, enemy ({p.Faction.Name})"
                    : $"outsider from {p.Faction.Name}";
                label = $"{p.gender.GetLabel()}, {role}";
            }
            else if (p.RaceProps.Animal)
                label = p.Faction == Faction.OfPlayer ? $"our animal, a {p.KindLabel}" : $"wild animal, a {p.KindLabel}";
            else
                label = p.KindLabel;
            string relation = viewer.GetMostImportantRelation(p)?.GetGenderSpecificLabel(p);
            if (relation != null)
                label += $", my {relation}";
            return p.Dead ? label + ", dead" : label;
        }

        /// <summary>The label for a name, or null when the game doesn't know it (the player, the colony, someone long gone).</summary>
        public static string Label(string name, Pawn viewer)
        {
            var p = Find(name, viewer.MapHeld);
            return p != null ? Label(p, viewer) : null;
        }
    }
}
