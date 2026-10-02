---
name: v8sharp-benchmark
description: How to benchmark V8Sharp against V8 (Octane, js-perf-test, micro-benchmarks) so the numbers mean something - warm-up, the parity build, interleaving V8 and V8Sharp in one session, the shared-host lock, and recording the VM's CPU/memory/disk state. Use before any performance measurement, A/B of two builds, or claim about speed relative to V8.
---

# Benchmarking V8Sharp against V8

Every performance number in this project is a comparison with real V8 (the
oracle, V8 14.7 through ClearScript) on the same machine. Numbers taken any
other way have misled us before; follow all of the rules below.

## The one-command way

```bash
cd dotnet
dotnet build -c Release tools/V8Sharp.Bench
tools/V8Sharp.Bench/bench-session.sh                       # octane-steady, v8sharp vs v8:jitless, 3 runs
tools/V8Sharp.Bench/bench-session.sh --suites octane-steady:richards,octane-steady:deltablue \
    --engines v8sharp@artifacts/bench-r2r,v8:jitless --runs 3
```

`bench-session.sh` takes the machine-wide lock, prints a fingerprint of the
VM (CPU model/MHz/caches, cgroup CPU quota, load, steal time, free memory, a
CPU calibration, memory bandwidth, disk bandwidth and IOPS), runs one
`V8Sharp.Bench compare` with V8 and V8Sharp interleaved, then fingerprints
again. The log goes to `dotnet/artifacts/bench/session-*.log`.

## Rules

1. **Warm up before measuring.** A cold run measures .NET's JIT compiling
   the engine (12-15K methods on PdfJS/CodeLoad, seconds of tier-0 and
   instrumented code), not V8Sharp. Use the `octane-steady` suites (one
   unmeasured pass, then the measured one) for interpreter/tier comparisons,
   and `micro:*` / `micro/cpu.js` which repeat until stable. Use cold
   `octane` (wall) only when start-up is the thing being measured, and say so.
2. **Run V8 and V8Sharp in the same session.** Always put the V8 reference
   (`v8:jitless`, `v8:sparkplug`, `v8:maglev`, `v8:jit`) in the same
   `compare` invocation as the V8Sharp engines. `compare` interleaves runs
   (runs are the outer loop), so load and VM changes hit every column alike.
   Never compare a V8Sharp number from one session with a V8 number from
   another: the VM can be rescheduled between sessions (we measured memory
   bandwidth moving 34 -> 58 GB/s within one session).
3. **Take the lock.** On a shared host, every benchmark runs under
   `flock -x /home/user/locks/bench.lock` (`bench-session.sh` does it) and
   every full conformance run under `flock -s` on the same file; builds and
   unit tests need no lock. Numbers taken under load are not evidence.
4. **Check the fingerprint.** Compare the before/after fingerprints: a
   changed CPU model or MHz, steal time above a few percent, a slower
   `cpu-cal`, or a big memory-bandwidth drop means the run is suspect -
   repeat it. Report the fingerprint lines with the results.
5. **Use the parity build for claims against V8.** V8 ships its builtins
   precompiled; the fair V8Sharp build is the ReadyToRun composite,
   self-contained publish (TieredPGO off, CallCountingDelayMs=0), passed as
   `v8sharp@<publish dir>`. See `tools/V8Sharp.Bench/README.md`. A `bin/`
   build is fine for A/B of two V8Sharp revisions, as long as both are
   `bin/` builds.
6. **A/B of builds: copy them out of `bin/` first** (`v8sharp@artifacts/a`,
   `v8sharp@artifacts/b`) and compare in one invocation, with the V8
   reference included.
7. **Prefer CPU-time scores for A/B** (`octane-cpu`, `octane-steady`: thread
   CPU time via schedstat); they vary far less than wall clock on a busy VM.
   Report wall-clock `octane` scores when quoting Octane itself.
8. **Repeat and report the noise.** At least 3 interleaved runs; per-run
   noise on this host is 3-10% per benchmark, so a single-row change under
   ~10% is not a result. Report means (or medians) and which runs failed.
9. **The V8 reference sometimes crashes** (`exit 139`, ClearScript with
   `--jitless`/`--sparkplug`); its mean is then over fewer runs - say so.
10. **Latency scores** (SplayLatency, MandreelLatency) are not meaningful in
    the CPU-time suites; leave them out of geomeans there.
11. **Clean up.** Profiler dumps (`perf`, EventPipe) are hundreds of MB to
    GB; delete them when done (the disk filled up once).

## Which yardstick

| tier being measured | compare against |
|---|---|
| interpreter (Ignition port) | `v8:jitless` |
| baseline IL tier (`--sparkplug`) | `v8:sparkplug` |
| optimizing tier (Maglev port) | `v8:maglev`, then `v8:jit` |

Headline metric: the geomean of Octane scores as a share of the V8 column,
with the suite (steady/cpu/wall), the build (bin/parity publish) and the
number of runs stated.
