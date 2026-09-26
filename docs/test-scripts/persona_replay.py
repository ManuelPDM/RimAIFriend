"""Replay every logged persona call (one per colonist, the latest) with the current Prompts/persona.txt against LM Studio,
to compare persona wording without the game: python persona_replay.py [runs-per-colonist]"""
import json, glob, re, sys, urllib.request

RUNS = int(sys.argv[1]) if len(sys.argv) > 1 else 1
TEMPLATE = open('C:/Users/manue/Coding Projects/rimworldmod/Prompts/persona.txt', encoding='utf-8').read()
COLD = re.compile(r'tool|manipulat|contempt|inferior|disdain|cold|detach|tolerat|dismiss|noise|variable|scheming|'
                  r'decept|domina|no joy|distant|aloof|superior', re.I)

latest = {}
for f in sorted(glob.glob('D:/RimDev/AIPawnControl/prompts-*.jsonl')):
    for line in open(f, encoding='utf-8'):
        if '"persona"' not in line:
            continue
        e = json.loads(line)
        if e.get('callType') != 'persona':
            continue
        user = e['request']['messages'][-1]['content']
        name = re.search(r'Name: ([^,]+)', user)
        if name:
            latest[name.group(1)] = e['request']

for name, req in latest.items():
    user = req['messages'][-1]['content']
    start = user.index('Name:')
    ends = [i for i in (user.find("\n\nPlayer's note", start), user.find('\n\nAlso write', start)) if i > 0]
    identity = user[start:min(ends)] if ends else user[start:]
    note_match = re.search(r'\(follow it if present\): (.*?)\n\n', user, re.S)
    note = note_match.group(1) if note_match else ''
    traits = re.search(r'Traits: (.*)', identity)
    body = dict(req)
    body['messages'] = [{'role': 'user', 'content': TEMPLATE.replace('{identity}', identity).replace('{note}', note)}]
    print(f"== {name} | traits: {traits.group(1) if traits else '-'}")
    for _ in range(RUNS):
        r = urllib.request.Request('http://localhost:1234/v1/chat/completions', json.dumps(body).encode(),
                                   {'Content-Type': 'application/json'})
        content = json.loads(urllib.request.urlopen(r, timeout=120).read())['choices'][0]['message']['content']
        try:
            persona = json.loads(content)['persona']
        except ValueError:
            print(f"   BROKEN JSON: {content[:200]!r}")
            continue
        if 'say' in persona.lower().split('\n')[-1] or '\n' in persona.strip():
            print('   LEAK: the persona has extra lines')
        hits = sorted(set(m.lower() for m in COLD.findall(persona)))
        print(f"   {'COLD ' + ','.join(hits) if hits else 'ok'}: {persona}")
