-- Load a save and pause: parameters { save = "aipc_reflect_test" }
rb.call("rimworld/load_game_ready", { saveName = params.save, readiness = "playable", pauseIfNeeded = true })
rb.call("rimbridge/wait_for_long_event_idle")
rb.call("rimworld/set_time_speed", { speed = "Paused" })
local info = rb.call("rimworld/get_game_info")
return info.result.ticksGame
