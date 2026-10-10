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

## Iterate small, measure the whole suite rarely

A full `octane-steady` session is about 18 minutes per run for V8Sharp plus
5 for V8 (zlib alone is 134 s), so three runs hold the shared lock for over an
hour and every other agent waits. Work in two loops:

- **Inner loop (each change):** pick the 2-5 benchmarks the change should
  move, plus one it should not, and A/B them with `octane-quick:<name>`
  (octane-steady with small fixed iteration counts, about 2-4 s per benchmark;
  zlib needs about 40 s, as one iteration is that long) and the relevant
  `micro:*` suite. Keep a session under about 15 minutes of lock time:
  ```bash
  tools/V8Sharp.Bench/bench-session.sh \
      --suites octane-quick:richards,octane-quick:deltablue,octane-quick:raytrace,micro:calls \
      --engines v8sharp@artifacts/a,v8sharp@artifacts/b,v8:jitless --runs 3
  ```
  V8Sharp.Bench builds made before octane-quick used fixed counts do not
  apply them and are reported
  as errors (rebuild them). Quick scores are comparable only within one
  session (different work from
  octane-steady); report them as ratios to the V8 column or to build A.
- **Outer loop (end of a pass):** one full `octane-steady` session on parity
  publishes for the headline number. Split it by benchmark groups into
  sessions under 2 hours if needed (background commands die at 2 h).
- Never queue more than one session at a time per agent, and cancel queued
  sessions you no longer need: a waiting session still takes its turn.

## Compile time is not scored (warm suites)

V8 ships its builtins precompiled and compiles JavaScript on background
threads; V8Sharp's tiers emit IL that RyuJIT compiles. The warm suites
(`octane-steady`, `octane-quick`) measure the generated code, not compilers:
each benchmark warms up for 2x its measured iterations
(`V8SHARP_BENCH_WARMUP`). Halfway through the warm-up both engines wait for
their background compiles to finish and install (`waitForCompilations()`:
V8Sharp's `Isolate.WaitForBackgroundCompilation`, V8's
`%WaitForBackgroundOptimization`); the second half then runs the installed
code several times, because the IL V8Sharp's tiers emit is itself compiled by
.NET in tiers (quick code first, optimized after call counting, plus OSR).
Before the measured runs both engines wait again and `settle()` (a sleep,
`V8SHARP_BENCH_SETTLE_MS`, default 250, which costs no thread CPU) so .NET's
background re-compilation finishes. Scores use main-thread CPU time only.
For any other measurement of generated code (micro-benchmarks, A/B of a
tier), do the same: warm up, wait for the tiers, run the compiled code a few
more times, then measure.
Cold `octane` still includes compilation: report it separately, as start-up.
Warm numbers taken before this rule (2026-10-04) are not comparable.

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
