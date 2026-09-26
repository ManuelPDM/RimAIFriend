-- Skip params.days days (for the cooldown), then click "DEV: Furnish now" on a pawn: parameters { pawn = "Sharp", days = 2 }
for i = 1, params.days do
  rb.call("rimworld/execute_debug_action", { path = "Actions\\Increment time...\\1 day" })
end
rb.call("rimworld/play_for", { durationMs = 1500, speed = "Superfast" })
rb.call("rimworld/set_time_speed", { speed = "Paused" })
rb.call("rimworld/select_pawn", { pawnName = params.pawn })
local g = rb.call("rimworld/list_selected_gizmos")
local clicked = false
for i, z in ipairs(g.result.gizmos) do
  if z.label == "DEV: Furnish now" then
    rb.call("rimworld/execute_gizmo", { gizmoId = z.id })
    clicked = true
  end
end
return clicked
