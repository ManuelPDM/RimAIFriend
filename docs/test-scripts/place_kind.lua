-- Place site A now as a given kind for a pawn (5x5, most-stocked material): parameters { pawn = "Sharp", kind = "Bedroom" }
rb.call("rimworld/select_pawn", { pawnName = params.pawn })
rb.call("rimworld/execute_debug_action", { path = "Actions\\Place site A now...\\" .. params.kind })
local info = rb.call("rimworld/get_game_info")
return info.result.ticksGame
