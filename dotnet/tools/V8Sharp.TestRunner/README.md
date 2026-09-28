# V8Sharp.TestRunner

The equivalent of V8's `tools/run-tests.py`: runs `test/mjsunit`,
`test/test262`, `test/message`, `test/webkit` and `test/mozilla` with their
`.status` files, `// Flags:` lines and harnesses, against either engine:

- `oracle`: real V8 14.7 in-process through ClearScript (`V8Sharp.Oracle`);
- `v8sharp`: the port, in-process.

## Usage

Run from anywhere inside the repository (it finds the V8 root by walking up):

```bash
cd dotnet
dotnet run -c Release --project tools/V8Sharp.TestRunner -- mjsunit --engine v8sharp
dotnet run -c Release --project tools/V8Sharp.TestRunner -- test262 --engine oracle --jobs 3
dotnet run -c Release --project tools/V8Sharp.TestRunner -- mjsunit/es6 test262/built-ins/Array --engine oracle
dotnet run -c Release --project tools/V8Sharp.TestRunner -- test262 --filter 'language/**/async-*' --engine v8sharp
dotnet run -c Release --project tools/V8Sharp.TestRunner -- message webkit --engine oracle --update-expectations
```

| option | |
|---|---|
| `<suite>` or `<suite>/<path>` | `mjsunit test262 message webkit mozilla`; a path filters like run-tests.py |
| `--engine oracle\|v8sharp` | default `v8sharp` |
| `--filter GLOB` | repeatable; matched against `suite/name` and `name`; `**` spans directories, `*` does not; a pattern without wildcards is a path prefix |
| `--jobs N` | parallel worker slots (default min(cores-1, 3); the machine is shared, use at most 3) |
| `--timeout S` | per test, default 60 s; x4 for `SLOW`, x2 for `--jitless` |
| `--update-expectations` | rewrite `expectations/<suite>.<engine>.txt` from this run |
| `--test262-root DIR` | default `test/test262/data`, then `/home/user/theolivenbaum/test262` |
| `--json FILE` | default `dotnet/artifacts/testrunner/<engine>-<suites>.json` |
| `--run-skipped`, `--list`, `--show-failures N` | as in run-tests.py |
| `--extra-flags "FLAGS"` | appended to every test's flags, as run-tests.py's `--extra-flags` (e.g. `"--always-sparkplug"`); the run is compared against the same expectation file, so any new failure is the flag's; pass `--json` to keep the plain run's results |

`V8Sharp.TestRunner shell [--engine E] [d8 args]` runs one d8 command line
in-process and prints its output, like d8 itself; handy to reproduce a test:
`--list` prints each test's exact command line.

Test ids are the status-file names (`es6/symbols`,
`built-ins/Array/prototype/map/create-species`); a test262 test that also runs
in strict mode has a second id with `@strict`.

## Outcomes and expectation files

A test's outcome (PASS, FAIL, CRASH, TIMEOUT) is decided per suite exactly as
V8's output processors do (`outproc/*.py`): the exit code for mjsunit, the
test262 negative/async rules (`--throws`, `Test262:AsyncTestComplete`,
`FAILED!`, the reported exception type), `.out` comparison with `*`,
`%(basename)s` and `{NUMBER}` for message tests, `-expected.txt` for webkit.
It is *unexpected* when the `.status` file does not list it
(`FAIL`, `FAIL_OK`, `FAIL_SLOPPY`, `CRASH` ...; `SKIP` tests are not run).

`expectations/<suite>.<engine>.txt` is the committed list of tests whose
outcome is currently unexpected on that engine: one id per line, sorted, with
an optional `# reason` (the runner writes the outcome; a hand-written reason is
kept across updates). A run reports **newly failing** tests (unexpected, not in
the file) and **newly passing** ones (in the file, now as expected), and exits
non-zero when anything is newly failing. This is how the port's progress is
tracked: after a change, run the suites and `--update-expectations`; the diff
of the expectation files is the progress. Both engines' files are
committed.

A glob line whose reason starts with `SKIP` does not only expect the tests
to fail: they are not run on that engine at all (reported as skipped, and
`--list` shows the reason). This is for a whole feature the engine does not
implement, e.g. `intl402/Temporal/**  # SKIP: intl402 needs ICU` in
`test262.v8sharp.txt`, so that full runs do not spend time on it.

## How it runs tests

- **Status files** (`Status/`): a port of `statusfile.py`, including its
  Python-literal syntax and condition expressions (`variant == ...`, `arch`,
  `mode`, `not i18n`, `in (...)`); variant-dependent sections are kept per
  variant. Status variables come from `buildconfig/<engine>.json` (V8's
  `v8_build_config.json`) plus the runner's own (`mode`, `system` ...). The
  oracle is a release x64 build with ICU and WebAssembly; v8sharp is
  `i18n=false`, `has_webassembly=false`, so the status files skip wasm and
  expect the no-ICU failures. Only the `default` variant runs; the variant
  table and incompatible-flag rules of `variants.py` are ported, and tests
  whose flags contradict each other or the build are skipped (d8 would reject
  them).
- **Suites** (`Suites/`): the `testcfg.py` of each suite: mjsunit's harness,
  `// Files:`, `// Environment Variables:`, `// NO HARNESS`, `.mjs` modules;
  test262's frontmatter (`Test262Frontmatter`, the YAML subset of
  `parseTestRecord.py`), includes, `harness-*.js` adapters, feature flags,
  sloppy/strict runs, `raw`, `module`, `async`, `negative`, `CanBlockIsFalse`;
  message and webkit expectations; mozilla (skipped: `test/mozilla/data` is
  not checked out).
- **The d8 shell** (`Shell/`): `D8Shell` implements d8's run loop
  (`src/d8/d8.cc`): files in order in one realm until one throws, the
  `setTimeout` task queue, unhandled-rejection reporting, `--throws`,
  `quit()`, and `ReportException`'s output format. The globals (`print`,
  `printErr`, `write`, `read`, `readbuffer`, `load`, `quit`, `version`,
  `setTimeout`, `Realm.*`, `d8.file.*`, `d8.terminate*`, `d8.constants`,
  `performance.now`, `arguments`) come from `d8-shim.js` over one host
  dispatcher; `gc`, `%` natives and `v8GC` come from the engine's flags.
  d8's `--bundle` (`TryExecuteBundle`: scripts, modules and module entry
  points in one file) and `--compile-only` (v8sharp engine only) are
  supported.
- **Workers** (`Execution/`): V8 flags are process-global, so tests are
  grouped by (V8 flags, environment) and each group runs in a worker process
  (`--worker`) that sets the flags once and then runs its tests one at a time,
  each in a fresh isolate, on a 256 MB stack, with a watchdog that terminates
  the isolate on timeout. A worker that dies (V8 `CHECK`/fatal errors, signals)
  or hangs costs only its current test (CRASH, or FAIL for SIGABRT as in
  `output.py`, or TIMEOUT) and is restarted. Native stdout (V8's
  `--print-*` output, flag errors) is captured from the worker's file
  descriptors.

## The engine interface

`Engines/IJsEngine.cs`. `D8Shell` needs nothing else from an engine:

```csharp
public interface IJsEngine {
    string Name { get; }                      // "oracle" | "v8sharp"
    string Version { get; }                   // d8's version()
    string? UnavailableReason { get; }        // non-null: every test fails with it
    void SetFlags(IReadOnlyList<string> flags);  // once per process, before the first isolate
    IJsIsolate CreateIsolate(IJsHost host);   // host: LoadModule, OnPromiseRejection
}
public interface IJsIsolate : IDisposable {
    IJsRealm MainRealm { get; }
    IJsRealm CreateRealm(IJsRealm? shareSecurityTokenWith, bool ownMicrotaskQueue = false);  // Realm.create / createAllowCrossRealmAccess
    void TerminateExecution();                // thread-safe (watchdog, quit())
    void CancelTerminateExecution();
    void CollectGarbage();
}
public interface IJsRealm : IDisposable {
    IJsIsolate Isolate { get; }
    object GlobalObject { get; }
    Completion RunScript(string source, string name);   // + microtask checkpoint
    Completion RunModule(string source, string name);   // imports via IJsHost.LoadModule
    Completion Call(object function, object? receiver, params object?[] args);
    Completion GetProperty(object target, string name);
    object CreateFunction(string name, JsHostFunction function);  // (realm, args) => value
    void DetachGlobal();
}
```

Values crossing it are `JsUndefined.Value`, `null`, `bool`, `double`,
`string`, or opaque engine handles. A host function throws `JsHostError`
(throw `new TypeError(msg)` in the realm), `JsThrowValue` (rethrow a value)
or `JsTermination`. A `Completion` is Normal, Throw (with a `JsExceptionInfo`:
the exception and v8::Message's resource name, line, start/end column and
source line) or Terminated. `V8SharpEngine` is the stub to fill in.

## Oracle baseline

The oracle's run of every suite (`--jobs 3`, 2026-09-28), committed as
`expectations/<suite>.oracle.txt`. "As expected" means the outcome the
`.status` file expects for this tree (V8 15.6); test262 counts runs, so most
tests count twice (sloppy and `@strict`).

| suite | run | as expected | unexpected | skipped | time |
|---|---:|---:|---:|---:|---:|
| mjsunit | 8908 | 8138 (91.4%) | 770 | 764 | ~5-10 min |
| test262 | 102953 | 88601 (86.1%) | 14352 | 898 | ~5-6 min |
| test262 without Temporal and ShadowRealm | 89495 | 88601 (99.0%) | 894 | | |
| message | 373 | 260 (69.7%) | 113 | 13 | <1 min |
| webkit | 543 | 543 (100%) | 0 | 1 | <1 min |
| mozilla | - | - | - | - | `test/mozilla/data` is not checked out |

Skipped: `SKIP` in the status files, flag contradictions (d8 would refuse
the flags), and test262's `$262.agent` tests (below). About 8 mjsunit
results per run are flaky and pass when rerun (WebAssembly tests that crash
the worker now and then, GC timing).

Where the unexpected outcomes come from (classified from the results JSON):

| class | mjsunit | test262 | message |
|---|---:|---:|---:|
| V8 14.7 lacks a 15.6 feature: Temporal (13364 runs, glob lines in the file), `Iterator.zip`/`includes`/`zipKeyed`, `import defer`/`source`, newer wasm proposals | 8 | 12444 | 2 |
| V8 14.7 does not know a 15.6 flag the test needs (d8 ignores unknown flags, so the feature stays off) | 176 | 268 | 7 |
| other version skew: behaviour changed between 14.7 and 15.6 (optimization-status asserts, `Math.expm1`, `Date`, TypedArray species, ...) | 173 | ~40 | |
| V8 14.7 `CHECK`/fatal errors, mostly on regression tests for bugs fixed after 14.7 | 109 | 54 | 5 |
| ICU and Unicode data older than 15.6's (RegExp property escapes, Unicode 17 identifiers, `intl402`) | | 354 | |
| d8 features the host does not provide: `Worker` (128), `d8.test.FastCAPI`, `d8.serializer`, `d8.dom`, `d8.profiler`, `d8.debugger`, `async_hooks`, `os`, the inspector's `send`, `--bundle` | 264 | | 1 |
| ClearScript limitations: no ShadowRealm host callback (94 runs); a dynamic `import()` of a module whose evaluation throws does not reject properly; `import()` evaluates synchronously | 37 | 292 | 18 |
| the uncaught-exception report: ClearScript exposes no `v8::Message` range, so the `^^^` underline and some positions differ from d8's | | | 80 |
| timeouts (`es6/promises` clobbers every global, including ClearScript's own) | 3 | | |

So nearly everything the oracle gets "wrong" is version skew or a d8 feature
the ClearScript host cannot provide; the runner's own classes (missing d8
builtins it could provide, module resolution, cross-realm access, flag
handling, output formats) were fixed along the way.

## Known limitations

- Only the `default` variant runs. `variants.py`'s tables are ported
  (`Status/Variants.cs`) but other variants would need per-variant flags and
  process groups.
- No test combining (`TestCombiner`), sharding, `--isolates`, `--stress-*`
  runs, or the num-fuzzer.
- Oracle host, compared with d8:
  - Values cross ClearScript as .NET objects, so a symbol returned by
    `Realm.eval` arrives as `undefined` (`Realm.shared` is kept in JS and is
    fine).
  - `Realm.owner` is approximate (by prototype chain), `Realm.navigate`
    creates a fresh global instead of reusing the global proxy, and
    `Realm.create`'s `create_own_microtask_queue` option is ignored.
  - `performance.mark/measure` are stubs; `d8.log.getAndStop` returns "";
    `readline` returns undefined; `writeFile` and `os` are not installed.
  - Uncaught-exception reports: the location comes from ClearScript's error
    details, the underline length from a token heuristic.
  - A module that imports itself is loaded through a one-line importer unless
    it uses `await` (quit() during a pending top-level await under the
    importer hits a CHECK in ClearScript's V8).
  - JSON modules are recognised by the `.json` extension (ClearScript does
    not pass import attributes).
  - `EngineInternal`, ClearScript's helper object, is a non-enumerable global
    it cannot remove; it is frozen so tests that clobber globals cannot break
    the host.
  - Unhandled rejections of non-Error values are reported without a source
    location.
- Worker processes are started per distinct (flags, environment) set:
  mjsunit has about 1200 of them, so a full run starts that many processes.
  Setting flags per test in one process (`--no-freeze-flags-after-init`)
  works for simple flags but cannot undo implications, and ClearScript's
  library does not export `FlagList::ResetAllFlags`, so it is not used.
- The v8sharp engine (`Engines/V8SharpEngine.cs`) runs V8Sharp in-process;
  its host provides d8's `--no-can-block` (Isolate.AllowAtomicsWait) and a
  ShadowRealm context callback like d8's.
