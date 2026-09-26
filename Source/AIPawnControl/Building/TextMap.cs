using System.Text;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// A small text map of a planned room for the dev log only, so Claude Code can check geometry without screenshots.
    /// Never shown to the player or the model. North is up.
    /// </summary>
    public static class TextMap
    {
        public const string Legend = "W new wall, # reused wall, D door, o outside the door, B bed head, b bed foot, T end table, "
            + "F other furniture, . free inside, t tree, i item, d existing door, X building, ~ water/no heavy ground, z zone, p Plan, ? fog, , open";

        public static string Draw(RoomPlan plan)
        {
            Map map = plan.map;
            CellRect r = plan.footprint;
            CellRect view = r.ExpandedBy(2).ClipInsideMap(map);
            var sb = new StringBuilder();
            sb.AppendLine($"{plan.kind.label} {plan.InteriorSize}x{plan.InteriorSize}, footprint ({r.minX},{r.minZ})-({r.maxX},{r.maxZ}), score {plan.score:0.0}");
            for (int z = view.maxZ; z >= view.minZ; z--)
            {
                sb.Append(z.ToString().PadLeft(4)).Append(' ');
                for (int x = view.minX; x <= view.maxX; x++)
                    sb.Append(Symbol(plan, new IntVec3(x, 0, z), map));
                sb.AppendLine();
            }
            sb.Append("     x from ").Append(view.minX).Append(". ").Append(Legend);
            return sb.ToString();
        }

        private static char Symbol(RoomPlan plan, IntVec3 c, Map map)
        {
            foreach (var e in plan.entries)
            {
                if (!e.Rect.Contains(c))
                    continue;
                if (e.def == ThingDefOf.Wall) return 'W';
                if (e.def == ThingDefOf.Door) return 'D';
                if (e.def == ThingDefOf.Bed) return c == e.cell ? 'B' : 'b';
                if (e.def.defName == "EndTable") return 'T';
                return 'F';
            }
            if (plan.reusedWalls.Contains(c)) return '#';
            if (c == plan.doorOutside) return 'o';
            bool inside = plan.Interior.Contains(c);
            if (c.Fogged(map)) return '?';
            foreach (var t in c.GetThingList(map))
            {
                if (t is Building_Door) return 'd';
                if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame) return 'X';
                if (t.def.category == ThingCategory.Plant && t.def.plant.IsTree) return 't';
                if (t.def.category == ThingCategory.Item) return 'i';
            }
            if (!c.SupportsStructureType(map, TerrainAffordanceDefOf.Heavy)) return '~';
            if (map.zoneManager.ZoneAt(c) != null) return 'z';
            if (map.planManager.PlanAt(c) != null) return 'p';
            return inside ? '.' : ',';
        }
    }
}
