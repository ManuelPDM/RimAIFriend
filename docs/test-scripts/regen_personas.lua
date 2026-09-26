-- Click "DEV: Regenerate persona" on each named pawn: parameters { pawns = { "Olaf", "Cissy" } }
local n = 0
for i, name in ipairs(params.pawns) do
  rb.call("rimworld/select_pawn", { pawnName = name })
  local g = rb.call("rimworld/list_selected_gizmos")
  for j, z in ipairs(g.result.gizmos) do
    if z.label == "DEV: Regenerate persona" then
      rb.call("rimworld/execute_gizmo", { gizmoId = z.id })
      n = n + 1
    end
  end
end
return n
