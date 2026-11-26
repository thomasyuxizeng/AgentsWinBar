"""Where does AgentsWinBar's time go during a refresh? Reads a speedscope export of a dotnet-trace thread-time trace.

Usage: python -I analyse_trace.py <file.speedscope.json> <trace-start HH:MM:SS> <refresh HH:MM:SS> [<refresh> ...]
For each refresh, takes the window [refresh - 5 s, refresh + 25 s]; also takes idle windows of the same length
between refreshes. Replays each thread's open/close events and charges elapsed time to every frame on the stack
(inclusive) and to the leaf (exclusive). Prints frames whose average time per refresh window exceeds the average
idle window most. Thread-time sampling also counts blocked threads, so the refresh-minus-idle difference is what
matters, not the absolute numbers.
"""
import json, sys, collections, datetime

path, start = sys.argv[1], sys.argv[2]
refreshes = sys.argv[3:]
def secs(hms):
    h, m, s = hms.split(':'); return int(h) * 3600 + int(m) * 60 + float(s)
t0 = secs(start)
data = json.load(open(path, encoding='utf-8'))
frames = [f['name'] for f in data['shared']['frames']]
unit = data['profiles'][0].get('unit', 'milliseconds')
scale = 1000.0 if unit == 'seconds' else (1.0 if unit == 'milliseconds' else 1e-3)  # to ms

win_r = [((secs(r) - t0) * 1000 - 5000, (secs(r) - t0) * 1000 + 25000) for r in refreshes]
# Idle windows: midpoints between refreshes, same 30 s length.
win_i = []
rs = sorted(secs(r) for r in refreshes)
for a, b in zip(rs, rs[1:]):
    mid = (a + b) / 2 - t0
    win_i.append((mid * 1000 - 15000, mid * 1000 + 15000))

def charge(windows):
    incl = collections.Counter(); excl = collections.Counter(); threads = collections.Counter()
    for prof in data['profiles']:
        stack = []; last = None
        for ev in prof['events']:
            at = ev['at'] * scale
            if stack and last is not None:
                for lo, hi in windows:
                    a, b = max(last, lo), min(at, hi)
                    if b > a:
                        dt = b - a
                        for f in set(stack): incl[f] += dt
                        excl[stack[-1]] += dt
                        threads[prof['name']] += dt
            if ev['type'] == 'O': stack.append(ev['frame'])
            elif ev['type'] == 'C' and stack: stack.pop()
            last = at
    n = max(len(windows), 1)
    return ({k: v / n for k, v in incl.items()}, {k: v / n for k, v in excl.items()}, {k: v / n for k, v in threads.items()})

ri, rx, rt = charge(win_r)
ii, ix, it = charge(win_i)
print(f"refresh windows {len(win_r)}, idle windows {len(win_i)}; ms per 30 s window, refresh minus idle\n")
print("Threads (refresh - idle):")
for name in sorted(rt, key=lambda k: -(rt[k] - it.get(k, 0)))[:8]:
    print(f"  {rt[name] - it.get(name, 0):8.0f}  {name}")
print("\nExclusive (leaf) frames:")
for f in sorted(rx, key=lambda k: -(rx[k] - ix.get(k, 0)))[:25]:
    print(f"  {rx[f] - ix.get(f, 0):8.0f}  {frames[f][:150]}")
print("\nInclusive frames from this app or the runtime's JSON, IO and XAML layers:")
keep = ('AgentsWinBar', 'System.Text.Json', 'System.IO', 'Microsoft.UI.Xaml', 'System.Net', 'System.Diagnostics.Process', 'WinRT', 'GC', 'H.NotifyIcon')
for f in sorted(ri, key=lambda k: -(ri[k] - ii.get(k, 0))):
    if any(k in frames[f] for k in keep) and ri[f] - ii.get(f, 0) > 20:
        print(f"  {ri[f] - ii.get(f, 0):8.0f}  {frames[f][:150]}")
