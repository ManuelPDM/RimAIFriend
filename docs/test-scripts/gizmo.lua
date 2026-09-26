-- Click a gizmo by label on a pawn: parameters { pawn = "Ia", label = "DEV: Reflect now" }
rb.call("rimworld/select_pawn", { pawnName = params.pawn })
local g = rb.call("rimworld/list_selected_gizmos")
local clicked = false
for i, z in ipairs(g.result.gizmos) do
  if z.label == params.label then
    rb.call("rimworld/execute_gizmo", { gizmoId = z.id })
    clicked = true
  end
end
rb.assert(clicked, "Gizmo not found.")
return clicked
