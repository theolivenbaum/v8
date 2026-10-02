# V8Sharp.Bench

Runs the same JavaScript workloads on V8Sharp and on the real V8 (the oracle,
V8 14.7 through ClearScript), one process per measurement, and prints scores
(higher is better) side by side.

```bash
tools/V8Sharp.Bench/fetch-octane.sh                      # Octane 2.0 -> dotnet/artifacts/octane
dotnet build -c Release tools/V8Sharp.Bench
dotnet tools/V8Sharp.Bench/bin/Release/net10.0/V8Sharp.Bench.dll list
dotnet tools/V8Sharp.Bench/bin/Release/net10.0/V8Sharp.Bench.dll compare \
    --suites octane:richards,perf:Operators --engines v8:jit,v8:jitless,v8sharp --runs 3
```

Engines: `v8:jit` (V8 as shipped), `v8:maglev` (`--no-turbofan`),
`v8:sparkplug` (`--no-maglev --no-turbofan`), `v8:jitless` (`--jitless`,
Ignition only), and V8Sharp in-process: `v8sharp` (its defaults: Ignition +
baseline IL), `v8sharp:jitless` (the interpreter only), `v8sharp:sparkplug`
(same as `v8sharp`) and `v8sharp:always-sparkplug`. `V8SHARP_BENCH_FLAGS`
adds V8 flags to the v8sharp runs. Suites: `octane` (all), `octane:<name>`,
`perf:<dir>` for a directory of `test/js-perf-test`, and `micro:<name>` /
`micro:all` for the interpreter micro-benchmarks in `micro/` (property
access, calls, closures, construction, arrays, arithmetic, string
concatenation; calls per second). Raw results go to `dotnet/artifacts/bench/`.

## Comparing builds, publishes and the shell

- `<engine>@<dir>` runs a measurement with the V8Sharp.Bench build in `<dir>`
  (an older revision's `bin/`, or a publish), so old and new builds are
  compared in one run: `--engines v8sharp@artifacts/old,v8sharp@artifacts/new,v8:jitless`.
  Runs are the outer loop, so the engines are interleaved and load changes
  on a shared machine hit every column alike. Copy a build out of `bin/`
  before rebuilding (a build found outside the tree locates `dotnet/` through
  `V8SHARP_BENCH_ROOT` or the working directory).
- `d8sharp[:mode]@<dir>` runs the `d8sharp` shell built or published in `<dir>`
  as its own process, with the files and the driver on its command line.
- `octane-cpu` / `octane-cpu:<name>`: Octane's deterministic mode with the
  iteration counts divided by `V8SHARP_BENCH_SCALE` (default 50), scored as
  1e6 / CPU milliseconds of the thread that runs the benchmark
  (`/proc/thread-self/schedstat`); `<name>.process` is the same for the whole
  process (start-up, the JIT's background threads, GC). On a loaded machine
  the thread's CPU time varies far less than Octane's wall-clock score; use it
  for A/B of V8Sharp builds, and the wall-clock `octane` suites against V8.

**Measure start-up-sensitive runs against a publish, not `bin/`.** From
`bin/`, every engine method is compiled by the JIT at tier 0 on first use
(the bootstrapper, parser, interpreter), which is most of a short run: a
plain `d8sharp -e 'print(1)'` takes about 340 ms, 140 ms published with
ReadyToRun and 110 ms ReadyToRun composite self-contained. Both
`V8Sharp.D8` and `V8Sharp.Bench` publish ReadyToRun when given a runtime:

```bash
dotnet publish -c Release src/V8Sharp.D8 -r linux-x64 --self-contained false -o artifacts/d8-r2r
dotnet publish -c Release tools/V8Sharp.Bench -r linux-x64 --self-contained false -o artifacts/bench-r2r
dotnet tools/V8Sharp.Bench/bin/Release/net10.0/V8Sharp.Bench.dll compare --suites octane \
    --engines v8sharp@artifacts/bench-r2r,d8sharp@artifacts/d8-r2r,v8:jitless --runs 3
```

**The parity configuration** (what V8Sharp is compared with V8 `--jitless`
on): a ReadyToRun composite, self-contained publish, as V8 ships its
builtins precompiled in its snapshot. Publishing for a runtime also turns
off `TieredPGO` and sets `System.Runtime.TieredCompilation.CallCountingDelayMs`
to 0 (`V8Sharp.D8.csproj`, `V8Sharp.Bench.csproj`):

```bash
dotnet publish -c Release tools/V8Sharp.Bench -r linux-x64 --self-contained true -o artifacts/bench-r2r
dotnet publish -c Release src/V8Sharp.D8 -r linux-x64 --self-contained true -o artifacts/d8-r2r
```

Why: Octane's large benchmarks (PdfJS, CodeLoad, Gameboy, TypeScript) run
12000-15000 engine methods through the JIT, 3-6.5 s of compile time per
CodeLoad or PdfJS process from `bin/`. ReadyToRun removes most tier-0
compiles, but with `TieredPGO` hot precompiled code is first rejitted with
instrumentation and only later at tier 1, and the 100 ms call-counting
delay restarts with every new tier-0 method, so a big script keeps hot
code unoptimized for seconds. Measured on CodeLoad, Octane-like short runs
(us per run, from `bin/` / R2R / R2R without PGO / R2R with delay 0 / both):
Closure 5085 / 3129 / 1953 / 2982 / 1686, jQuery 45667 / 47219 / 23857 /
23500 / 19190. The dispatch loop itself is `AggressiveOptimization`
(compiled once at full optimization in every configuration); the
NoInlining handlers only it calls are not precompiled (crossgen does not
compile the loop, so it never sees those instantiations) and start at
tier 0. From `bin/`, `TieredPGO` stays on: there it is worth about 20% of
the steady-state score of the eight classic benchmarks.

The yardsticks: phase 1 (interpreter) is measured against `v8:jitless`,
the baseline IL tier against `v8:sparkplug`, the optimizing tier against
`v8:maglev` and `v8:jit`.

First oracle baseline (4-core container, one run):

| benchmark | v8:jit | v8:sparkplug | v8:jitless |
|---|---|---|---|
| Richards | 36185 | 1786 | 1340 |
| DeltaBlue | 73179 | 1798 | 1515 |
| Crypto | 35001 | 1716 | 1227 |

Known limitation: `--jitless` in the ClearScript build sometimes crashes
(SIGSEGV) on Octane richards/crypto; rerun, or use `--runs 3` and read the
successful runs.
