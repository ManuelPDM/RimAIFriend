-- Skip whole days with vanilla's "Increment time" (moves the game clock; memory, cooldowns and nights all age):
-- parameters { days = 7 }. Needs play (not paused) for a moment afterwards for triggers like the night Reflect.
for i = 1, params.days do
  rb.call("rimworld/execute_debug_action", { path = "Actions\\Increment time...\\1 day" })
end
local info = rb.call("rimworld/get_game_info")
return info.result.ticksGame
