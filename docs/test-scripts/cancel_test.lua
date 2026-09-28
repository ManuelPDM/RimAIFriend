-- Play in short chunks until Ia's Reflect starts, then send a chat at once (Phase 3 check 3: a chat cancels Reflect).
local logs = rb.call("rimbridge/list_logs", { afterSequence = 0 })
local last = 0
for i, e in ipairs(logs.result.logs) do
  if e.Sequence > last then last = e.Sequence end
end
local found = false
local rounds = 0
while found == false and rounds < 45 do
  rounds = rounds + 1
  rb.call("rimworld/play_for", { durationMs = 1000, speed = "Fast" })
  local fresh = rb.call("rimbridge/list_logs", { afterSequence = last })
  for i, e in ipairs(fresh.result.logs) do
    if e.Sequence > last then last = e.Sequence end
    if e.Message == "[AI Pawn Control] Ia: Reflect started." then found = true end
  end
end
if found then
  rb.call("rimworld/select_pawn", { pawnName = "Ia" })
  rb.call("rimworld/execute_debug_action", { path = "Actions\\Chat as player...\\ask for a talk" })
end
local info = rb.call("rimworld/get_game_info")
return { found = found, rounds = rounds, tick = info.result.ticksGame }
