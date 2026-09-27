using System;
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
        public static string Start(PawnMind mind, Room room)
        {
            Pawn pawn = mind.pawn;
            var upgrades = Upgrades.For(room, pawn);
            string name = SnapshotBuilder.RoomName(room, pawn);
            if (upgrades.Count == 0)
                return $"Looked at {name}, but there's nothing to add right now.";
            string feel = $"{BuildManager.Impressiveness(room)}, {room.Temperature.ToStringTemperature("F0")}";
            var items = SnapshotBuilder.Furniture(room);
            var messages = PromptBuilder.Build("upgrade", mind, new Dictionary<string, string>
            {
                ["room"] = name,
                ["roomState"] = $"{name} ({feel}): " + (items.Count > 0 ? SnapshotBuilder.ItemList(items, int.MaxValue) : "nothing in it") + ".",
                ["options"] = string.Join("\n", upgrades.Select((u, i) => $"{i + 1}: {u.label}")),
            });
            var schema = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["reason"] = new Dictionary<string, object> { ["type"] = "string", ["maxLength"] = 400 },
                    ["choice"] = new Dictionary<string, object> { ["type"] = "integer", ["enum"] = Enumerable.Range(0, upgrades.Count + 1).Cast<object>().ToList() },
                    ["say"] = ActionCatalog.SaySchema(),
                },
                ["required"] = new List<object> { "reason", "choice", "say" },
                ["additionalProperties"] = false,
            };
            Map map = pawn.Map;
            ModLog.Message($"{pawn.LabelShort}: upgrade call for {name} with {upgrades.Count} options.");
            mind.Send("upgrade", messages, schema, reply => OnReply(mind, reply, room, upgrades),
                stillValid: () => pawn.Destroyed || pawn.Dead || !pawn.Spawned || pawn.Map != map ? "gone" : null);
            return $"Thinking about how to upgrade {name}.";
        }

        private static void OnReply(PawnMind mind, Dictionary<string, object> reply, Room room, List<Upgrades.Upgrade> upgrades)
        {
            Pawn pawn = mind.pawn;
            int pick = reply.TryGetValue("choice", out object c) && c is double d ? (int)d : -1;
            string say = reply.TryGetValue("say", out object s) ? s as string : null;
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
            string result;
            try
            {
                // The room may have changed while she thought: it must still be a room, and the pick must still fit.
                result = room.Dereferenced || room.Map != pawn.Map ? "The room isn't there any more." : Upgrades.Place(pawn, room, upgrades[pick - 1]);
            }
            catch (Exception e)
            {
                result = "That went wrong: " + e.Message;
                ModLog.Error($"{pawn.LabelShort}'s upgrade threw: {e}");
            }
            BaseCall.RemarkAndLog(mind, say, result, $"upgrade: {upgrades[pick - 1].label}");
        }
    }
}
