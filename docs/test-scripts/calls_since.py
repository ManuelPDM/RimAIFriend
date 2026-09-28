"""Summarise the LLM calls (every call type) after a local time: python calls_since.py <HH:MM:SS>
Shows each call's memory sections ([On my mind] / [I remember]), and the reply's choice, memory and say/reply."""
import json, sys, glob
after = sys.argv[1]
f = sorted(glob.glob('D:/RimDev/AIPawnControl/prompts-*.jsonl'))[-1]
for e in (json.loads(l) for l in open(f, encoding='utf-8') if l.strip()):
    if e['time'][11:19] <= after:
        continue
    user = e['request']['messages'][-1]['content'] if 'request' in e else ''
    first = user.split('\n', 1)[0]
    mind = ''
    for tag in ('[On my mind] (', '[I remember]\n'):
        i = user.find(tag)
        if i >= 0:
            mind += ' | ' + user[i:user.find('\n\n', i)].replace('\n', ' / ')[:300]
    reply = e.get('content') or ''
    try:
        r = json.loads(reply)
        reply = {k: r[k] for k in ('choice', 'act', 'memory', 'say', 'reply') if k in r}
    except Exception:
        reply = reply[:100]
    line = f"{e['time'][11:19]} {e['callType']} {first[:60]}{mind} -> {reply}"
    print(line.encode('ascii', 'replace').decode())
