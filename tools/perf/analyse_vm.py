"""Split the WSL VM's memory during AgentsWinBar refreshes between page cache, process memory and the rest.

Usage: python analyse_vm.py [DIR] [PREFIX] [HOST_CSV]  (defaults: this folder, vmmon, warm-vm.csv)
Inputs: <PREFIX>.mem.csv, <PREFIX>.procs.csv (guest, from vm-monitor.sh) and HOST_CSV (host, optional).
Prints: guest memory over time at key moments, the processes that appeared during refreshes with their peak RSS
and CPU, and the host's vmmemWSL working set at the same moments.
"""
import csv, sys, os, collections, datetime

here = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
prefix = sys.argv[2] if len(sys.argv) > 2 else 'vmmon'
host_csv = sys.argv[3] if len(sys.argv) > 3 else 'warm-vm.csv'
mem = [r for r in csv.DictReader(open(os.path.join(here, prefix + '.mem.csv'))) if not r['t'].startswith('hz=')]
procs = []
for r in csv.DictReader(open(os.path.join(here, prefix + '.procs.csv'))):
    try:
        float(r['t']); int(r['pid']); int(r['rss_kb']); int(r['cpu_ticks']); int(r['child_cpu_ticks'])
    except (ValueError, TypeError):
        continue
    procs.append(r)
host_path = os.path.join(here, host_csv)
host = [r for r in csv.DictReader(open(host_path)) if r['proc'].startswith('vmmem')] if os.path.exists(host_path) else []

def ts(x): return float(x)
def local(t): return datetime.datetime.fromtimestamp(t).strftime('%H:%M:%S')

t0 = ts(mem[0]['t'])
print(f"guest samples {len(mem)} from {local(t0)} to {local(ts(mem[-1]['t']))}")
print("\nGuest memory (MB): used = total - free; cached = page cache; anon = process heap/stack")
rows = []
for r in mem:
    tot, free, cached, anon, slab = (int(r[k]) / 1024 for k in ('memtotal_kb', 'memfree_kb', 'cached_kb', 'anonpages_kb', 'slab_kb'))
    rows.append((ts(r['t']), tot - free, cached, anon, slab))
# Print a row every ~10 s plus every local max of anon.
last = -99
for t, used, cached, anon, slab in rows:
    if False:
        print(f"  {local(t)} used {used:7.0f}  cached {cached:6.0f}  anon {anon:6.0f}  slab {slab:5.0f}")
        last = t
print(f"\npeak used {max(r[1] for r in rows):.0f} MB, peak cached {max(r[2] for r in rows):.0f} MB, peak anon {max(r[3] for r in rows):.0f} MB")

# Processes: baseline set = pids seen in the first 3 s; the rest appeared later (refreshes).
first = collections.defaultdict(lambda: None)
for p in procs:
    if first[p['pid']] is None: first[p['pid']] = ts(p['t'])
base = {pid for pid, t in first.items() if t - t0 < 3}
agg = {}
for p in procs:
    pid = p['pid']
    a = agg.setdefault(pid, {'comm': p['comm'], 'args': p['args'][:110], 'first': ts(p['t']), 'last': ts(p['t']),
                             'rss': 0, 'cpu0': int(p['cpu_ticks']), 'cpu1': 0, 'ccpu': 0, 'ppid': p['ppid']})
    a['last'] = ts(p['t']); a['rss'] = max(a['rss'], int(p['rss_kb'])); a['cpu1'] = int(p['cpu_ticks'])
    a['ccpu'] = max(a['ccpu'], int(p['child_cpu_ticks']))
print(f"\nBaseline processes (present at start): {len(base)}; total peak RSS {sum(agg[p]['rss'] for p in base)/1024:.0f} MB")
for pid in sorted(base, key=lambda p: -agg[p]['rss'])[:6]:
    a = agg[pid]; print(f"  {a['comm']:<18} rss {a['rss']/1024:6.1f} MB  {a['args'][:80]}")
later = [p for p in agg if p not in base]
print(f"\nProcesses that appeared later: {len(later)} (CPU in seconds = own ticks seen + reaped children, hz=100)")
for pid in sorted(later, key=lambda p: agg[p]['first']):
    a = agg[pid]
    print(f"  {local(a['first'])}-{local(a['last'])} pid {pid:>6} ppid {a['ppid']:>6} {a['comm']:<16} rss {a['rss']/1024:6.1f} MB "
          f"cpu {a['cpu1']/100:5.2f}s child {a['ccpu']/100:5.2f}s | {a['args'][:90]}")

# Host vmmemWSL at the same moments.
print("\nHost vmmemWSL (MB) every ~10 s:")
last = -99
for r in host:
    h, m, s = r['clock'].split(':'); sec = int(h) * 3600 + int(m) * 60 + float(s)
    if sec - last >= 10:
        print(f"  {r['clock'][:8]} ws {float(r['ws_mb']):7.0f} private {float(r['private_mb']):7.0f}")
        last = sec
