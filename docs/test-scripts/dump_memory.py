"""Dump a mind's lasting memory from a save: python dump_memory.py <save name> [--events]"""
import sys, xml.etree.ElementTree as ET
t = ET.parse(f'D:/RimDev/Saves/{sys.argv[1]}.rws')

def texts(el, tag):
    x = el.find(tag)
    return [l.text for l in x] if x is not None else []

for mind in t.iter('li'):
    mem = mind.find('memory')
    if mem is None:
        continue
    print('mind', mind.findtext('pawn'), '| lastReflectTick', mem.findtext('lastReflectTick'), 'night', mem.findtext('lastReflectNight'))
    print('LATELY:', mem.findtext('lately'))
    print('GOALS:')
    for g in (mem.find('goals') or []):
        print(f"  [{g.findtext('source')}] {g.findtext('text')} (why: {g.findtext('why')})")
    print('MEMORIES:')
    for m in (mem.find('memories') or []):
        v = m.findtext('vector') or ''
        print(f"  M{m.findtext('id')} imp{m.findtext('importance')} tick{m.findtext('tick')} events{texts(m, 'events')} people{texts(m, 'people')} "
              f"@{m.findtext('place')} used{m.findtext('usedCount')} tag={m.findtext('vectorTag')} veclen={len(v)}: {m.findtext('text')}")
    print('DIARY:')
    for d in (mem.find('diary') or []):
        print(f"  tick{d.findtext('tick')} tag={d.findtext('vectorTag')}: {d.findtext('text')}")
    print('FILES:')
    for f in (mem.find('files') or []):
        print(f"  [{f.findtext('name')}] {f.findtext('impression')} | threads: {f.findtext('threads')} | told {texts(f, 'toldThem')}")
        for fa in (f.find('facts') or []):
            print(f"     - {fa.findtext('text')} ({fa.findtext('source')}, since {fa.findtext('since')}, until {fa.findtext('until')})")
    if '--events' in sys.argv:
        print('EVENTS:')
        for e in mem.find('events'):
            print(f"  #{e.findtext('id')} {e.findtext('kind')} [{e.findtext('source')}] imp{e.findtext('importance')}: {e.findtext('text')}")
