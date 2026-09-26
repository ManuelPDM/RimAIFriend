-- Keep a test colony alive: survival meals (stacks of 10) around a cell, and heal the colonists' wounds.
-- parameters { x = 130, z = 145, stacks = 12 }
local x = params.x
local z = params.z
local n = params.stacks or 12
for i = 1, n do
  rb.call("rimworld/spawn_thing", { defName = "MealSurvivalPack", stackCount = 10, x = x + (i % 4), z = z + (i - (i % 4)) / 4 })
end
local c = rb.call("rimworld/list_colonists", { currentMapOnly = true })
for i, p in ipairs(c.result.colonists) do
  rb.call("rimworld/execute_debug_action", { path = "Actions\\T: Heal random injury (10)", pawnName = p.name })
end
return n
