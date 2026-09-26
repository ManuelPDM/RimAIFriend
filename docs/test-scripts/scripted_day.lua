-- Phase 3 check 2: a scripted day for the mind "Ia" in aipc_voice_test.
rb.call("rimworld/set_time_speed", { speed = "Paused" })
rb.call("rimworld/select_pawn", { pawnName = "Ia" })
local g = rb.call("rimworld/list_selected_gizmos")
for i, z in ipairs(g.result.gizmos) do
  if z.label == "DEV: Try a talk" then
    rb.call("rimworld/execute_gizmo", { gizmoId = z.id })
  end
end
rb.call("rimworld/play_for", { durationMs = 8000, speed = "Normal" })
rb.call("rimworld/select_pawn", { pawnName = "Ia" })
g = rb.call("rimworld/list_selected_gizmos")
for i, z in ipairs(g.result.gizmos) do
  if z.label == "DEV: Others talk nearby" then
    rb.call("rimworld/execute_gizmo", { gizmoId = z.id })
  end
end
rb.call("rimworld/play_for", { durationMs = 4000, speed = "Normal" })
rb.call("rimworld/select_pawn", { pawnName = "Ia" })
g = rb.call("rimworld/list_selected_gizmos")
for i, z in ipairs(g.result.gizmos) do
  if z.label == "DEV: Others talk far away" then
    rb.call("rimworld/execute_gizmo", { gizmoId = z.id })
  end
end
rb.call("rimworld/execute_debug_action", { path = "Actions\\T: 10 damage", thingId = "Thing_Human799" })
rb.call("rimworld/execute_debug_action", { path = "Actions\\Do incident\\ResourcePodCrash" })
rb.call("rimworld/execute_debug_action", { path = "Actions\\Mental state...\\Tantrum", pawnName = "Ia" })
local m = rb.call("rimworld/list_messages")
return m.result.messages
