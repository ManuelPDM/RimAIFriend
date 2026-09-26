-- Play at Superfast for params.ticks more ticks (or about 50 s): { ticks = 20000 }
local info = rb.call("rimworld/get_game_info")
local tick = info.result.ticksGame
local target = tick + params.ticks
local rounds = 0
while tick < target and rounds < 10 do
  rounds = rounds + 1
  rb.call("rimworld/play_for", { durationMs = 5000, speed = "Superfast" })
  info = rb.call("rimworld/get_game_info")
  tick = info.result.ticksGame
end
rb.call("rimworld/set_time_speed", { speed = "Paused" })
return tick
