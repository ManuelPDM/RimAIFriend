"""Print the last JSONL entry of a call type: python last_call.py reflect [index-from-end] [--prompt]"""
import json, sys, glob, os
files = sorted(glob.glob('D:/RimDev/AIPawnControl/prompts-*.jsonl'))
ents = [json.loads(l) for l in open(files[-1], encoding='utf-8') if l.strip()]
call = sys.argv[1]
back = int(sys.argv[2]) if len(sys.argv) > 2 and sys.argv[2].isdigit() else 1
r = [e for e in ents if e.get('callType') == call]
if len(r) < back:
    print('none'); sys.exit()
e = r[-back]
print('time', e.get('time'), 'seconds', e.get('seconds'), 'tokens', e.get('completionTokens'), 'error', e.get('error'), 'cancelled', e.get('cancelled'))
if '--prompt' in sys.argv:
    msgs = e['request']['messages']
    if len(msgs) > 1:
        print('----- SYSTEM tail'); print(msgs[0]['content'][-500:])
    print('----- USER'); print(msgs[-1]['content'])
print('----- REPLY'); print(e.get('content'))
