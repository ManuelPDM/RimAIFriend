-- Save the current game: parameters { save = "aipc_test" }
rb.call("rimworld/save_game", { saveName = params.save })
local info = rb.call("rimworld/get_game_info")
return info.result.ticksGame
