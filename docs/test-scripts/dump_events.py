import xml.etree.ElementTree as ET, sys
t=ET.parse(sys.argv[1])
for mind in t.iter('li'):
    mem=mind.find('memory')
    if mem is None: continue
    f=mem.find('firsts')
    print('mind', mind.findtext('pawn'), 'nextId', mem.findtext('nextId'), 'firsts', [l.text for l in f] if f is not None else [])
    for e in mem.find('events'):
        p=e.find('people'); ppl=[l.text for l in p] if p is not None else []
        print(f"#{e.findtext('id')} t{e.findtext('tick')} {e.findtext('kind')}/{e.findtext('def')} [{e.findtext('source')}] imp{e.findtext('importance')} x{e.findtext('count') or 1} {ppl} @{e.findtext('place')}: {e.findtext('text')}")
