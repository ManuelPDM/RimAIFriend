-- Play at a speed until ticksGame reaches params.target (or about 50 s pass): { target = 300000, speed = "Ultrafast" }
local info = rb.call("rimworld/get_game_info")
local tick = info.result.ticksGame
local rounds = 0
while tick < params.target and rounds < 10 do
  rounds = rounds + 1
  rb.call("rimworld/play_for", { durationMs = 5000, speed = params.speed or "Ultrafast" })
  info = rb.call("rimworld/get_game_info")
  tick = info.result.ticksGame
end
rb.call("rimworld/set_time_speed", { speed = "Paused" })
return tick
