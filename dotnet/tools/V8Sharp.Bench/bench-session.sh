#!/usr/bin/env bash
# One benchmark session: V8Sharp and V8 measured in the same process tree,
# warmed up, with a fingerprint of the VM taken before and after so a run on
# a slow or noisy host is recognisable afterwards.
#
#   tools/V8Sharp.Bench/bench-session.sh [compare args...]
#
# Defaults: --suites octane-steady --engines v8sharp,v8:jitless --runs 3
# Any arguments replace the defaults and are passed to `V8Sharp.Bench compare`
# (e.g. --suites octane-steady:richards --engines v8sharp@artifacts/bench-r2r,v8:jitless).
# Environment: BENCH_DLL (default: the Release build of V8Sharp.Bench),
# BENCH_LOCK (default /home/user/locks/bench.lock), V8SHARP_BENCH_SCALE.
# Output: dotnet/artifacts/bench/session-<timestamp>.log (and the JSON that
# `compare` writes next to it).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
dotnet_root="$(cd "$here/../.." && pwd)"
dll="${BENCH_DLL:-$dotnet_root/tools/V8Sharp.Bench/bin/Release/net10.0/V8Sharp.Bench.dll}"
lock="${BENCH_LOCK:-/home/user/locks/bench.lock}"
out_dir="$dotnet_root/artifacts/bench"
mkdir -p "$out_dir"
log="$out_dir/session-$(date -u +%Y%m%d-%H%M%S).log"
scratch="$(mktemp -d "${TMPDIR:-/tmp}/bench-session.XXXXXX")"
trap 'rm -rf "$scratch"' EXIT

if [ $# -eq 0 ]; then
  set -- --suites octane-steady --engines v8sharp,v8:jitless --runs 3
fi

fingerprint() {
  echo "=== machine fingerprint ($1) $(date -u +%FT%TZ)"
  echo "kernel:   $(uname -r)"
  lscpu | grep -E '^(Model name|CPU\(s\)|Thread|Core|Socket|CPU MHz|CPU max MHz|L1d|L2|L3|Hypervisor)' | sed 's/^/cpu:      /'
  [ -r /sys/fs/cgroup/cpu.max ] && echo "cgroup:   cpu.max=$(cat /sys/fs/cgroup/cpu.max)"
  echo "load:     $(cut -d' ' -f1-3 /proc/loadavg)"
  # steal: CPU time taken by the hypervisor for other guests, sampled over 1 s.
  read -r _ u1 n1 s1 i1 w1 q1 sq1 st1 _ < /proc/stat; sleep 1
  read -r _ u2 n2 s2 i2 w2 q2 sq2 st2 _ < /proc/stat
  tot=$(( (u2+n2+s2+i2+w2+q2+sq2+st2) - (u1+n1+s1+i1+w1+q1+sq1+st1) ))
  echo "steal:    $(( tot > 0 ? 100 * (st2 - st1) / tot : 0 ))% over 1 s; idle $(( tot > 0 ? 100 * (i2 - i1) / tot : 0 ))%"
  free -m | awk 'NR==2 {print "memory:   total " $2 " MB, available " $7 " MB"}'
  # CPU throughput: hash 512 MB of zeros (single thread).
  local t0 t1
  t0=$(date +%s%N); head -c 536870912 /dev/zero | sha256sum > /dev/null; t1=$(date +%s%N)
  echo "cpu-cal:  sha256 512 MB in $(( (t1 - t0) / 1000000 )) ms"
  # Memory bandwidth: dd /dev/zero -> /dev/null with 1 MB blocks.
  echo "mem-bw:   $(dd if=/dev/zero of=/dev/null bs=1M count=4096 2>&1 | tail -1 | sed 's/.*, //')"
  # Disk: sequential write/read bandwidth (direct I/O) and 4 KB random-ish IOPS.
  local f="$scratch/io.bin"
  echo "disk-wr:  $(dd if=/dev/zero of="$f" bs=1M count=256 oflag=direct conv=fsync 2>&1 | tail -1 | sed 's/.*, //')"
  echo "disk-rd:  $(dd if="$f" of=/dev/null bs=1M iflag=direct 2>&1 | tail -1 | sed 's/.*, //')"
  t0=$(date +%s%N); dd if=/dev/zero of="$f" bs=4k count=4096 oflag=direct,dsync 2>/dev/null; t1=$(date +%s%N)
  echo "disk-iops: 4 KB sync writes: $(( 4096 * 1000000000 / (t1 - t0 + 1) )) IOPS"
  rm -f "$f"
}

{
  echo "bench-session: $*"
  echo "dll: $dll"
  echo "git: $(git -C "$dotnet_root" rev-parse --short HEAD 2>/dev/null || echo '?')"
  echo "waiting for the bench lock ($lock) ..."
  flock -x "$lock" bash -c '
    fp() { '"$(declare -f fingerprint | tail -n +2)"'; }
    scratch='"'$scratch'"'
    fp before
    echo "=== benchmark"
    dotnet "'"$dll"'" compare "$@"
    fp after
  ' _ "$@"
} 2>&1 | tee "$log"
echo "session log: $log"
