"""Prompt tour: launch RimWorld (dev data folder, quicktest colony) with -aipcPromptTour. The mod runs a mind's LLM calls
in three world states (fresh landing, settled, raid), writes D:/RimDev/AIPawnControl/tours/<time>/tour.json and closes
the game; this then writes report.html next to it and opens it.
    python prompt_tour.py                  launch, wait, render
    python prompt_tour.py <tour>           render a saved tour again: its folder name (2026-10-16_18-19-29) or its tour.json path
Steam must be running, and the game closed (it locks the mod's DLL)."""
import glob, html, json, os, subprocess, sys, time, winreg

TOURS = 'D:/RimDev/AIPawnControl/tours'
ARGS = ['-savedatafolder=D:\\RimDev', '-quicktest', '-aipcPromptTour']
TIMEOUT = 60 * 60


def running():
    out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq RimWorld.exe'], capture_output=True, text=True).stdout
    return 'RimWorld.exe' in out


def launch():
    if running():
        sys.exit('RimWorld is running; close it first.')
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam') as key:
        steam = winreg.QueryValueEx(key, 'SteamExe')[0]
    start = time.time()
    subprocess.Popen([steam, '-applaunch', '294100', *ARGS])
    print('Launched; waiting for the tour (the game closes itself when it is done)...')
    seen = False
    while time.time() - start < TIMEOUT:
        time.sleep(5)
        new = [p for p in glob.glob(TOURS + '/*/tour.json') if os.path.getmtime(p) > start]
        if new:
            return max(new, key=os.path.getmtime)
        up = running()
        seen = seen or up
        if seen and not up:
            sys.exit('The game closed without writing a tour; see the game log (Player.log).')
    sys.exit('No tour after an hour.')


def pre(text):
    return f'<pre>{html.escape(text)}</pre>'


def pretty(text):
    try:
        return json.dumps(json.loads(text), indent=2, ensure_ascii=False)
    except (TypeError, ValueError):
        return text or ''


def card(n, c):
    head = f'<span class="call">{html.escape(c["call"])}</span>'
    if c.get('note'):
        head += f' <span class="note">{html.escape(c["note"])}</span>'
    if c.get('skipped'):
        return f'<section class="skip">{head} <span class="note">not run: {html.escape(c["skipped"])}</span></section>'
    meta = f'{c["seconds"]}s, {c["tokens"]} tokens'
    parts = [f'<h2>{n}. {head} <span class="meta">{meta}</span></h2>']
    for m in c['messages']:
        parts.append(f'<details><summary>{m["role"]} prompt ({len(m["content"])} chars)</summary>{pre(m["content"])}</details>')
    parts.append(f'<details><summary>schema</summary>{pre(json.dumps(c["schema"], indent=2))}</details>')
    if c.get('reasoning'):
        parts.append(f'<details><summary>reasoning</summary>{pre(c["reasoning"])}</details>')
    if c.get('error'):
        parts.append(f'<div class="error">{html.escape(c["error"])}</div>')
    if c.get('reply'):
        parts.append(f'<div class="reply"><div class="label">reply</div>{pre(pretty(c["reply"]))}</div>')
    return '<section>' + ''.join(parts) + '</section>'


def render(path):
    tour = json.load(open(path, encoding='utf-8'))
    sent = [c for s in tour['states'] for c in s['calls'] if not c.get('skipped')]
    n = 0
    cards = []
    for i, s in enumerate(tour['states'], 1):
        cards.append(f'<h1 class="state">State {i}: {html.escape(s["name"])}</h1><div class="sub">{html.escape(s["setup"])}</div>')
        for c in s['calls']:
            if not c.get('skipped'):
                n += 1
            cards.append(card(n, c))
    stamp = os.path.basename(os.path.dirname(path))
    page = f'''<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Prompt tour {stamp}</title><style>
:root {{ --bg:#fafaf8; --fg:#1d1d1b; --muted:#6b6b66; --card:#fff; --line:#e2e1dc; --reply:#eef5ee; --err:#fbeaea; --accent:#2f6f4f; }}
@media (prefers-color-scheme: dark) {{ :root {{ --bg:#161615; --fg:#e8e7e2; --muted:#9a9993; --card:#1f1f1d; --line:#33332f; --reply:#1c2a21; --err:#3a1f1f; --accent:#7cc39b; }} }}
body {{ background:var(--bg); color:var(--fg); font:15px/1.5 system-ui, sans-serif; max-width:1000px; margin:0 auto; padding:24px 16px; }}
h1 {{ font-size:22px; margin:0 0 4px; }} .sub {{ color:var(--muted); margin-bottom:24px; }}
h1.state {{ font-size:19px; margin-top:40px; padding-top:16px; border-top:2px solid var(--line); }}
section {{ background:var(--card); border:1px solid var(--line); border-radius:8px; padding:12px 16px; margin:12px 0; }}
section.skip {{ background:none; border-style:dashed; color:var(--muted); padding:8px 16px; }}
h2 {{ font-size:16px; margin:0 0 8px; font-weight:600; }} .call {{ color:var(--accent); font-weight:700; }}
.note, .meta {{ color:var(--muted); font-weight:400; }} .meta {{ float:right; font-size:13px; }}
details {{ margin:4px 0; }} summary {{ cursor:pointer; color:var(--muted); }}
pre {{ white-space:pre-wrap; word-break:break-word; font:13px/1.45 ui-monospace, Consolas, monospace; margin:6px 0; }}
.reply {{ background:var(--reply); border-radius:6px; padding:8px 12px; margin-top:8px; }} .label {{ font-size:12px; color:var(--muted); }}
.error {{ background:var(--err); border-radius:6px; padding:8px 12px; margin-top:8px; }}
</style></head><body>
<h1>Prompt tour</h1>
<div class="sub">{html.escape(tour["colonist"])} · {html.escape(tour["model"])} · temperature {tour["temperature"]} · thinking {"on" if tour["thinking"] else "off"} ·
{len(sent)} calls, {sum(c["seconds"] for c in sent):.0f}s · {stamp}</div>
{"".join(cards)}
</body></html>'''
    out = os.path.join(os.path.dirname(path), 'report.html')
    open(out, 'w', encoding='utf-8').write(page)
    return out


def find(arg):
    """A tour.json path, or a tour's folder name under TOURS."""
    for path in (arg, os.path.join(TOURS, arg, 'tour.json')):
        if os.path.isfile(path):
            return path
    tours = sorted(os.path.basename(os.path.dirname(p)) for p in glob.glob(TOURS + '/*/tour.json'))
    sys.exit(f'No tour at {arg}. Saved tours (pass one of these names, or a tour.json path):\n  ' + '\n  '.join(tours[-10:] or ['(none)']))


if __name__ == '__main__':
    tour = find(sys.argv[1]) if len(sys.argv) > 1 else launch()
    report = os.path.abspath(render(tour))
    print(report)
    os.startfile(report)
