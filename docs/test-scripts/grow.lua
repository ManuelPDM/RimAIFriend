-- Load a save, run up to four debug actions (full paths, or ""), then grow a test base with layout (base-map.txt):
-- parameters { save = "aipc_growth_fresh", setup = "Actions\\Test setup...\\add power", setup2 = "", setup3 = "", setup4 = "" }
rb.call("rimworld/load_game_ready", { saveName = params.save, readiness = "playable", pauseIfNeeded = true })
rb.call("rimbridge/wait_for_long_event_idle")
rb.call("rimworld/set_time_speed", { speed = "Paused" })
if params.setup ~= "" then
  rb.call("rimworld/execute_debug_action", { path = params.setup })
end
if params.setup2 ~= "" then
  rb.call("rimworld/execute_debug_action", { path = params.setup2 })
end
if params.setup3 ~= "" then
  rb.call("rimworld/execute_debug_action", { path = params.setup3 })
end
if params.setup4 ~= "" then
  rb.call("rimworld/execute_debug_action", { path = params.setup4 })
end
rb.call("rimworld/set_time_speed", { speed = "Paused" })
rb.call("rimworld/execute_debug_action", { path = "Actions\\Grow test base...\\with layout" })
return 1
