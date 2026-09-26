"""Check 9 (PHASE4.md §12): no prompt has a grid or raw cell coordinates. python no_coords.py [HH:MM:SS local, only later entries]
Flags "(x, z)" / "(x,z)" pairs and lines that look like a text map. Prints the call type and the offending text."""
import json, re, sys, glob
after = sys.argv[1] if len(sys.argv) > 1 else '00:00:00'
pair = re.compile(r'\(\s*\d{1,3}\s*,\s*\d{1,3}\s*\)')
gridrow = re.compile(r'^\s*\d{1,3} [.,WD#BbTFox+tidXz~p?]{6,}$', re.M)
f = sorted(glob.glob('D:/RimDev/AIPawnControl/prompts-*.jsonl'))[-1]
n = bad = 0
for line in open(f, encoding='utf-8'):
    if not line.strip():
        continue
    e = json.loads(line)
    if e['time'][11:19] <= after:
        continue
    n += 1
    req = e.get('request')
    req = json.loads(req) if isinstance(req, str) else (req or {})
    text = '\n'.join(m.get('content', '') for m in req.get('messages', []) if isinstance(m, dict))
    if not text:
        n -= 1  # embeddings and other entries without chat messages
        continue
    hits = pair.findall(text) + gridrow.findall(text)
    if hits:
        bad += 1
        print(e['callType'], e['time'][11:19], hits[:5])
print(f'{n} prompts checked, {bad} with coordinates or grids')
