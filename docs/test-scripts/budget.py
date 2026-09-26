"""Budget check (PHASE3 §10 item 13) over a local time window of today's JSONL:
python budget.py <HH:MM:SS from> <HH:MM:SS to>. Counts calls by type (cancelled ones apart), embed calls per Act/Chat/
Reflect, and the longest Act memory block ([About X] + [On my mind])."""
import json, sys, glob, collections
a, b = sys.argv[1], sys.argv[2]
f = sorted(glob.glob('D:/RimDev/AIPawnControl/prompts-*.jsonl'))[-1]
ents = [json.loads(l) for l in open(f, encoding='utf-8') if l.strip()]
ents = [e for e in ents if a <= e['time'][11:19] <= b]
count = collections.Counter((e.get('callType'), bool(e.get('cancelled'))) for e in ents)
for (ct, cancelled), n in sorted(count.items(), key=str):
    print(f"{ct}{' (cancelled)' if cancelled else ''}: {n}")
longest = 0
for e in ents:
    if e.get('callType') != 'act' or 'request' not in e:
        continue
    u = e['request']['messages'][-1]['content']
    i = u.find('[About ')
    if i < 0:
        i = u.find('[On my mind] (')
    if i >= 0:
        longest = max(longest, len(u[i:u.find('What do you do', i)]))
print('longest Act memory block (chars):', longest)
