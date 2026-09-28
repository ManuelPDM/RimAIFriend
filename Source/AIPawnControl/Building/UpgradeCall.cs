using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AIPawnControl
{
    /// <summary>
    /// The Upgrade call (FURNISHING.md §5): after the Base call picked a room, up to 3 concrete upgrades of different kinds
    /// plus "never mind". She picks one and makes one remark; code re-checks and places it.
    /// </summary>
    public static class UpgradeCall
    {
        /// <summary>The prompt, schema and upgrades for one room; null upgrades when there's nothing to add.</summary>
        public class Prepared
        {
            public List<Upgrades.Upgrade> upgrades;
            public List<KeyValuePair<string, string>> messages;
            public Dictionary<string, object> schema;
        }

        public static Prepared Prepare(PawnMind mind, Room room)
        {
            Pawn pawn = mind.pawn;
            var upgrades = Upgrades.For(room, pawn);
            if (upgrades.Count == 0)
                return new Prepared();
            string name = SnapshotBuilder.RoomName(room, pawn);
            string feel = $"{BuildManager.Impressiveness(room)}, {room.Temperature.ToStringTemperature("F0")}";
            var items = SnapshotBuilder.Furniture(room);
            return new Prepared
            {
                upgrades = upgrades,
                messages = PromptBuilder.Build("upgrade", mind, new Dictionary<string, string>
                {
                    ["room"] = name,
                    ["roomState"] = $"{name} ({feel}): " + (items.Count > 0 ? SnapshotBuilder.ItemList(items, int.MaxValue) : "nothing in it") + ".",
                    ["options"] = string.Join("\n", upgrades.Select((u, i) => $"{i + 1}: {u.label}")),
                }),
                schema = Schema.Obj(new Dictionary<string, object>
                {
                    ["reason"] = Schema.Reason(),
                    ["choice"] = Schema.Pick(upgrades.Count),
                    ["say"] = Schema.Say(),
                }),
            };
        }

        public static string Start(PawnMind mind, Room room)
        {
            Pawn pawn = mind.pawn;
            string name = SnapshotBuilder.RoomName(room, pawn);
            var prepared = Prepare(mind, room);
            if (prepared.upgrades == null)
                return $"Looked at {name}, but there's nothing to add right now.";
            Map map = pawn.Map;
            ModLog.Message($"{pawn.LabelShort}: upgrade call for {name} with {prepared.upgrades.Count} options.");
            mind.Send("upgrade", prepared.messages, prepared.schema, reply => OnReply(mind, reply, room, prepared.upgrades),
                stillValid: () => pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map ? "gone" : null);
            return $"Thinking about how to upgrade {name}.";
        }

        private static void OnReply(PawnMind mind, Dictionary<string, object> reply, Room room, List<Upgrades.Upgrade> upgrades)
        {
            Pawn pawn = mind.pawn;
            int pick = reply.Int("choice", -1);
            string say = reply.Str("say");
            if (pick == 0)
            {
                BaseCall.RemarkAndLog(mind, say, $"Looked at {SnapshotBuilder.RoomName(room, pawn)} and left it as it is.", "upgrade: never mind");
                return;
            }
            if (pick < 1 || pick > upgrades.Count)
            {
                ModLog.Warning($"{pawn.LabelShort}: upgrade reply chose {pick}, which isn't on the list.");
                return;
            }
            var upgrade = upgrades[pick - 1];
            // The room may have changed while she thought: it must still be a room, and the pick must still fit.
            string result = MindActions.Safely(pawn, upgrade.label, () =>
                room.Dereferenced || room.Map != pawn.Map ? "The room isn't there any more." : Upgrades.Place(pawn, room, upgrade));
            BaseCall.RemarkAndLog(mind, say, result, $"upgrade: {upgrade.label}");
        }
    }
}
