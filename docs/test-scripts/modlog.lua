-- Our mod's game-log lines (optionally only those after a sequence number): parameters { after = 0 }
local logs = rb.call("rimbridge/list_logs", { afterSequence = params.after or 0 })
local lines = {}
local last = 0
for i, e in ipairs(logs.result.logs) do
  if e.Sequence > last then last = e.Sequence end
  if e.Level ~= "info" or e.Source == "unity" then
    rb.print("l", e.Message)
  end
end
return last
