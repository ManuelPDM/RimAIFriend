-- Build room now: a given kind for a pawn, as the Base call would (site A, code's size, materials marked): parameters { pawn = "Sharp", kind = "Bedroom" }
rb.call("rimworld/select_pawn", { pawnName = params.pawn })
rb.call("rimworld/execute_debug_action", { path = "Actions\\Build room now...\\" .. params.kind })
local info = rb.call("rimworld/get_game_info")
return info.result.ticksGame
