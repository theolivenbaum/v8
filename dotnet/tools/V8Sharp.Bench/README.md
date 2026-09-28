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
