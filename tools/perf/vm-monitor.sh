#!/usr/bin/env bash
# Read-only probe for the WSL VM: every 0.5 s, record each process's resident memory and CPU ticks, plus
# /proc/meminfo, so an AgentsWinBar refresh can be split between the kernel, page cache and each process.
# Usage: vm-monitor.sh <seconds> <output-prefix>   (writes <prefix>.procs.csv and <prefix>.mem.csv)
# Note: while this runs the VM stays warm, so a refresh in this window does not include a cold boot.
dur=${1:-420}
prefix=${2:-/tmp/vm-monitor}
hz=$(getconf CLK_TCK)
procs="$prefix.procs.csv"
mem="$prefix.mem.csv"
echo "t,pid,ppid,rss_kb,cpu_ticks,child_cpu_ticks,comm,args" > "$procs"
echo "t,memtotal_kb,memfree_kb,memavailable_kb,cached_kb,anonpages_kb,shmem_kb,slab_kb,cpu_busy_ticks,cpu_idle_ticks" > "$mem"
end=$((SECONDS + dur))
self=$$
while [ "$SECONDS" -lt "$end" ]; do
  t=$(date +%s.%N | cut -c1-14)
  # Whole-VM CPU from the first line of /proc/stat (all CPUs): busy = user+nice+system+irq+softirq+steal.
  read -r _ u n s idle iow irq sirq steal _ < /proc/stat
  busy=$(( u + n + s + irq + sirq + steal )); idl=$(( idle + iow ))
  awk -v t="$t" -v busy="$busy" -v idl="$idl" '/^MemTotal:/{a=$2}/^MemFree:/{b=$2}/^MemAvailable:/{c=$2}/^Cached:/{d=$2}/^AnonPages:/{e=$2}/^Shmem:/{f=$2}/^Slab:/{g=$2}
    END{printf "%s,%s,%s,%s,%s,%s,%s,%s,%s,%s\n",t,a,b,c,d,e,f,g,busy,idl}' /proc/meminfo >> "$mem"
  for d in /proc/[0-9]*; do
    pid=${d#/proc/}
    [ "$pid" = "$self" ] && continue
    stat=$(cat "$d/stat" 2>/dev/null) || continue
    comm=${stat#*(}; comm=${comm%)*}
    rest=${stat##*) }
    # shellcheck disable=SC2086
    set -- $rest
    # rest starts at field 3: ${2}=ppid, ${12}=utime, ${13}=stime, ${14}=cutime, ${15}=cstime, ${22}=rss pages.
    # Braces matter: in bash $12 means ${1} followed by "2".
    rss_kb=$(( ${22:-0} * 4 ))
    [ "$rss_kb" -eq 0 ] && continue
    args=$(tr '\0\n\r' '   ' < "$d/cmdline" 2>/dev/null | tr ',' ';' | cut -c1-240)
    echo "$t,$pid,${2},$rss_kb,$(( ${12} + ${13} )),$(( ${14} + ${15} )),${comm//,/;},$args" >> "$procs"
  done
  sleep 0.5
done
echo "hz=$hz" >> "$mem"
