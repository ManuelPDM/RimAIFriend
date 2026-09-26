"""Wait until a new JSONL entry of a call type appears after a given time, then print it.
python wait_call.py <callType> <HH:MM:SS local, entries must be later> [timeoutSeconds]"""
import json, sys, glob, time
call, after = sys.argv[1], sys.argv[2]
timeout = float(sys.argv[3]) if len(sys.argv) > 3 else 90
deadline = time.time() + timeout
while True:
    f = sorted(glob.glob('D:/RimDev/AIPawnControl/prompts-*.jsonl'))[-1]
    ents = [json.loads(l) for l in open(f, encoding='utf-8') if l.strip()]
    hits = [e for e in ents if e.get('callType') == call and e['time'][11:19] > after]
    if hits:
        e = hits[-1]
        print(e['callType'], e['time'][11:19], 'seconds', e.get('seconds'), 'error', e.get('error'), 'cancelled', e.get('cancelled'))
        print(e.get('content'))
        break
    if time.time() > deadline:
        print('timeout'); break
    time.sleep(1.5)
