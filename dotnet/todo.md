# V8Sharp: status and work queue

The plan, in order: (1) the unoptimized pipeline that starts JavaScript
execution in V8 (parser, Ignition bytecode, interpreter, runtime, builtins)
until mjsunit and test262 pass at a high rate; (2) the tiers that make V8
fast (baseline IL, then the feedback-driven optimizing tier with deopt).
See `docs/architecture.md` for the design.

Legend: `[ ]` not started, `[~]` in progress, `[x]` done (tests green).

## Infrastructure

- [x] Solution, props, project layout (`V8Sharp.slnx`)
- [x] Oracle: real V8 14.7 in-process via ClearScript, with V8 flags by P/Invoke
      (`V8Sharp.Oracle.ReferenceV8`)
- [x] test262 checkout at V8's pinned commit (`test/test262/data`, git-ignored)
- [x] TestRunner (`tools/V8Sharp.TestRunner`, see its README): `.status` files,
      `// Flags:`, mjsunit harness, test262 frontmatter and includes, message,
      webkit; worker processes per flag set with watchdog and crash isolation;
      the d8 shell over an engine interface; oracle engine; v8sharp stub
- [x] Baseline results of the oracle on mjsunit / test262 / message / webkit
      (`tools/V8Sharp.TestRunner/expectations/*.oracle.txt`)
- [ ] v8sharp engine behind the runner (`Engines/V8SharpEngine.cs`) and its
      expectation files; grow `tests/V8Sharp.Conformance.Tests/curated/v8sharp.txt`
- [ ] d8sharp shell: `print`, `load`, `read`, `quit`, `version`, `-e`, `--flags`,
      `%` natives, `d8.*` test helpers used by mjsunit
- [x] Golden bytecode test harness (`bytecode_expectations/*.golden`): parser,
      printer, assembler; waits for the BytecodeGenerator to compile snippets

## Phase 1: the unoptimized pipeline

### V8Sharp.Base
- [x] `src/base/numbers`: diy-fp, cached-powers, fast-dtoa, fixed-dtoa,
      bignum, bignum-dtoa, dtoa, strtod (+ base/*-dtoa, bignum, double,
      numbers/diy-fp, strtod unittests and the gay-* tables)
- [x] `src/numbers/conversions`: StringToDouble/Int/BigInt, DoubleToCString,
      DoubleToFixed/Exponential/Precision/Radix, DoubleToInt32 ... (+
      conversions-unittest; differential tests against the oracle)
- [x] `src/bigint`: digit arithmetic, mul (schoolbook/karatsuba/toom/fft),
      div (schoolbook/burnikel/barrett), tostring, fromstring, bitwise (+
      bigint-shell tests, System.Numerics and oracle differential tests).
      Missing: numbers/bigint-unittest `CompareToDouble`, which tests the
      engine's `BigInt::CompareToDouble` (src/objects/bigint.cc).
- [x] `src/strings/unicode*` (unibrow case mapping, utf8/utf16, utf8-decoder),
      `char-predicates` (ID_Start/ID_Continue via .NET Unicode data) (+
      unicode and char-predicates unittests, full-range oracle comparison).
      Missing: `Wtf8Decoder` / `StrictUtf8Decoder` (Wasm only).
- [x] hashing: `src/strings/string-hasher`, hash seed and rapidhash,
      `src/base/hashing`, `src/base/bits`, `src/base/ieee754` and
      `src/numbers/ieee754` (`math::pow`), `src/base/utils/random-number-generator`
      (+ bits, hashing, ieee754, random-number-generator unittests).
      Not in Base: `src/numbers/math-random` (native-context state; belongs
      to the engine together with the `Math.random` builtin).
- [x] Performance of the number and Math primitives (explicit benchmark:
      `dotnet test -c Release tests/V8Sharp.Base.Tests --filter
      "FullyQualifiedName~Benchmark" -- xUnit.Explicit=only`). Table-driven
      kernels put the correctly rounded Math functions at 15-45 ns, 1.3-4x
      the platform libm (were 300-700 ns); shortest digits 37 ns (Grisu3
      83 ns); StringToDouble 39 ns (Strtod path 93 ns, double.Parse 95 ns).
- [ ] Math: trigonometric arguments beyond 1.6e6 still take the
      double-double / BigInteger reduction (0.3-3 us); a Payne-Hanek table
      reduction would bring them to the kernel.

### V8Sharp.Parsing
- [ ] tokens, keywords, scanner, character streams, literal buffer
- [ ] AstValueFactory, AST nodes, AstNodeFactory
- [ ] scopes, variables, ScopeInfo-independent analysis
- [ ] parser-base, parser, preparser, expression scopes, func-name inferrer,
      rewriter, pending compilation errors
- [ ] tests: scanner, parsing, preparser, ast-value, scanner-streams

### V8Sharp.RegExp
- [x] regexp parser, AST, flags, errors, AST printer (regexp-parser.cc,
      regexp-ast.cc, regexp-flags.h, regexp-error.cc, regexp-ast-printer.cc)
- [x] compiler (regexp-compiler-tonode.cc, regexp-compiler.cc), bytecodes,
      bytecode generator, peephole (one-byte and two-byte compilation)
- [x] bytecode interpreter (RawMatch for one-byte and two-byte subjects);
      experimental (linear) engine: bytecode, compiler, NFA interpreter,
      /l flag, backtrack-limit fallback, capture-group-opt
- [x] regexp.cc compile/exec pipeline without the heap: atom fast path,
      lazy irregexp compilation, capture name map, EscapeRegExpSource,
      VerifyFlags (public API: RegExpEngine.Compile, CompiledRegExp.Exec)
- [x] case folding / property escapes / unicode sets without ICU: tables
      generated from UCD 17.0 and emoji 17.0 by
      tools/unicode/gen_regexp_unicode_tables.py
- [x] tests: regexp-unittest (62 pass, 3 engine-level skips), differential
      corpus vs the oracle (mjsunit, mjsunit/harmony, webkit, test262
      built-ins/RegExp and language/literals/regexp, experimental engine):
      100% agreement once the oracle's older V8/Unicode version is accounted
      for (reports in dotnet/artifacts/regexp-differential/)
- [x] native tier: RegExpMacroAssemblerIL (port of
      regexp-macro-assembler-x64.cc) emits IL into a collectible
      DynamicMethod, LATIN1 and UC16; V8's tier-up policy
      (--regexp-interpret-all, --regexp-tier-up, --regexp-tier-up-ticks,
      eager tier-up for long subjects and global execs) as RegExpTierPolicy.
      The native assembler unittests run on both backends; the differential
      corpora run on the native, interpreter and tier-up configurations
      (100%, modulo the known oracle-age cases). Benchmark on Octane
      regexp.js: tools/V8Sharp.RegExp.Bench (results in
      dotnet/artifacts/regexp-bench/results.md)
- [ ] TODO(merge): switch Unicode/*.cs to the shared unibrow port in
      V8Sharp.Base once it lands

### V8Sharp (engine)
- [x] objects: strings (table, flattening, hashing, compare, ConsStringIterator,
      StringCharacterStream), symbols, Map/descriptors/transitions, MapUpdater,
      PropertyDetails, FieldIndex, JSObject fast/dictionary properties, elements
      kinds and accessors, LookupIterator, property descriptors, keys
      (KeyAccumulator), hash tables, ordered hash tables.
      Tests: object-unittest, test-field-type-tracking, dictionary-unittest,
      hashcode-unittest, test-orderedhashtable (OrderedHashMap/Set),
      test-strings (non-JS), elements-kind-unittest, test-transitions.
      Missing: interceptors (access checks: global proxies only), shared/Atomics elements,
      SmallOrderedHashTable, the JS-running tests of those files
      (StoreToConstantField_*, HoleyHeapNumber, the JS parts of test-strings)
- [~] Isolate, Factory, roots, StringTable, MessageTemplate, error creation,
      stack traces (CallSiteInfo, Error.captureStackTrace, prepareStackTrace).
      Done: Isolate, Factory, roots, StringTable, error creation, CallSiteInfo,
      protectors, the Error builtins (Builtins.Error.cs)
- [x] conversions and operators (Object::ToNumber, ToPrimitive, Equals,
      StrictEquals, Compare, arithmetic helpers)
- [~] bootstrapper/Genesis: native context, intrinsics in V8's install order.
      Done: Object, Function, Array, Number, Boolean, String, Symbol, Date,
      Promise, RegExp, Errors, JSON, Math, Map/Set/WeakMap/WeakSet/WeakRef/
      FinalizationRegistry, BigInt, Iterator and helpers, Proxy, Reflect,
      bound/wrapped functions, arguments maps, API functions
      (FunctionTemplateInfo, HandleApiCallOrConstruct), ArrayBuffer/
      SharedArrayBuffer/Atomics, typed arrays, DataView
      (Init/Genesis.TypedArrays.cs, incl. the js_immutable_arraybuffer and
      sharedarraybuffer flag sections), DisposableStack/AsyncDisposableStack
      and Iterator.prototype[Symbol.dispose]/%AsyncIteratorPrototype%
      [Symbol.asyncDispose] (Init/Genesis.DisposableStack.cs),
      InitializeExperimentalGlobal (Iterator.concat/zip/zipKeyed,
      Iterator.prototype.join/includes, queueMicrotask behind
      --enable-queue-microtask; Init/Genesis.{Iterator,Promise}.cs).
      Missing: Intl, Temporal, shared structs, extras, extensions,
      the TemplateLiteral map (interpreter port)
- [x] interpreter: bytecodes, operands, array builder/writer, register
      optimizer, constant array builder, handler tables, control-flow builders,
      decoder, iterators, source-position table (292 tests; 100 golden files
      round-trip). Open: embedded operation hints, feedback-kind checks.
- [x] bytecode generator: bytecode-generator.cc, prototype-assignment-sequence-
      builder, block-coverage-builder.h, the unoptimized compile driver
      (`UnoptimizedCompiler.Compile`, `InterpreterCompilationJob`). All 100
      golden files match byte for byte (557/557 snippets);
      bytecode-generator-unittest.cc ported; oracle comparison on 72
      mjsunit-style snippets (176 functions) matches except for listed V8
      14.7 differences (`OracleBytecodeGeneratorTest`). Heap-facing parts sit
      behind `IBytecodeGeneratorHeap` (constants, SharedFunctionInfo,
      boilerplates, template objects, CoverageInfo) and `IScopeInfoProvider`.
      Open: ClassBoilerplate::New (the generator emits a
      `ClassBoilerplateDescription` stand-in); a heap-backed
      `IBytecodeGeneratorHeap` and real ScopeInfo; the lazy/parallel compile
      dispatcher (should_parallel_compile literals are compiled eagerly or not
      at all); eval code is compiled by the engine at run time (the golden
      harness compiles direct evals of string literals itself); block
      coverage is ported but has no golden coverage (only the oracle can
      check it).
- [ ] feedback vectors and ICs (load/store/keyed/global/call/binary op/compare)
- [ ] interpreter dispatch loop, generators, async functions
- [ ] runtime functions (`%` intrinsics used by bytecode and by mjsunit)
- [~] builtins: Object, Function, Reflect, Proxy, global functions (URI
      coding, escape/unescape, isNaN/isFinite, parseInt/parseFloat, eval),
      Error (+ AggregateError, SuppressedError, captureStackTrace, isError,
      CallSite methods), Boolean, Symbol: ported (Builtins/Builtins.{Object,
      Function,Reflect,Proxy,Global,Error,Boolean,Symbol}*.cs; tests in
      tests/V8Sharp.Tests/Builtins, URI/parse functions and the intrinsics'
      shapes checked against the oracle). ShadowRealm (builtins-shadow-realm.cc,
      InitializeGlobal_harmony_shadow_realm, behind the experimental
      --harmony-shadow-realm that test262 turns on; Builtins.ShadowRealm.cs):
      constructor, evaluate, importValue (rejects until there is a host
      dynamic import), CallSite boundary checks; hosts set
      Isolate.HostCreateShadowRealmContextCallback as d8 does (the runner
      does; d8sharp should too). `new Function` and indirect eval call
      Isolate.DynamicFunctionCompiler (IDynamicFunctionCompiler). The proxy
      trap stubs (ProxyGetProperty ...) and CallProxy/ConstructProxy are not
      registered: callers use JSProxy. test262 (v8sharp, 2026-09-28):
      built-ins/{Object,Function,Reflect,Proxy,Error,NativeErrors,Boolean,
      Symbol,global functions}/** 100% except the interpreter/module cases
      listed under "Interpreter failures seen by the builtins conformance
      pass".
- [~] Map, Set, WeakMap, WeakSet, WeakRef, FinalizationRegistry, Promise,
      Iterator, DisposableStack builtins and the microtask queue
      (Builtins/Builtins.{Collections,Set,WeakRefs,Promise*,Iterator*,
      DisposableStack}.cs, Objects/{JSCollection,JSPromise,JSWeakRefs,
      JSIteratorHelpers,JSDisposableStack}.cs, Execution/MicrotaskQueue.cs).
      Every builtin of builtins-collections-gen.cc, collections.tq,
      map-groupby.tq, set-*.tq, builtins-weak-refs.cc, weak-ref.tq,
      finalization-registry.tq, promise-*.tq (all, any, allSettled, race,
      finally, try, withResolvers, jobs, resolving functions, hooks,
      rejection tracking), iterator.tq, iterator-helpers.tq (incl.
      concat/zip/zipKeyed/join/includes), iterator-from.tq,
      builtins-async-iterator-gen.cc (%AsyncFromSyncIteratorPrototype%),
      async-disposable-stack.tq, builtins-disposable-stack.cc and
      GlobalQueueMicrotask is registered. The interpreter-facing APIs:
      PromiseBuiltins.{NewJSPromise, ResolvePromise, RejectPromise,
      PerformPromiseThen(Impl), NewPromiseCapability, PromiseResolve,
      EnqueueMicrotask, AsyncAwaitNonThenableFastPath}, the generator
      resume hooks (ResumeGeneratorTrampoline, AsyncGeneratorResumeNext,
      AsyncGeneratorResolve), IteratorBuiltins.{GetIterator, IteratorStep,
      IteratorStepValue, IteratorClose, CreateIterResultObject,
      IterableToList...}, AsyncFromSyncIteratorBuiltins.
      CreateAsyncFromSyncIterator, Isolate.{CollectGarbage, RunPendingTasks}.
      Also the runtime functions of runtime-promise.cc, runtime-collections.cc,
      runtime-weak-refs.cc and the protector queries of runtime-test.cc
      (Runtime/Runtime.Promise.cs), and gc() (src/extensions/gc-extension.cc,
      Init/GCExtension.cs); d8sharp and the runner's D8Shell pump the
      isolate's foreground tasks. 50 xUnit tests
      (tests/V8Sharp.Tests/Builtins/{Promise,Collections,Iterator,WeakRefs,
      DisposableStack}BuiltinsTest.cs), expectations from the oracle.
      test262 (v8sharp): built-ins/{Promise,Map,Set,WeakMap,WeakSet,WeakRef,
      FinalizationRegistry,Iterator,DisposableStack}/** 100% (4414 tests);
      AsyncDisposableStack 150/208, AsyncIteratorPrototype 18/26,
      AsyncFromSyncIteratorPrototype 0/76: every failure needs async
      functions/generators or for-await (Interpreter/InterpreterAsync.cs
      stubs). mjsunit: the remaining failures of es6/promise*, collection*,
      weakrefs/**, harmony/iterator* are the same async stubs, plus
      es6/collections-constructor-with-modified-protoype (an IC bug: the
      second `arr.length = 1` store with feedback does not truncate), the
      d8 Realm microtask-queue/onerror tests (runner d8 shim) and
      iterator-join (%ArrayBufferDetach). Missing: the `IteratorHelpers`
      forwarding shim in Builtins.Iterator.cs (remove once no caller uses it)
- [x] Array, ArrayBuffer, SharedArrayBuffer, TypedArray, DataView, Atomics
      builtins and the array iterators (Builtins/Builtins.{Array,ArrayBuffer,
      TypedArray,DataView,Atomics}*.cs; Objects/JSArrayBuffer.cs,
      JSTypedArray.cs, Elements.Typed.cs; Heap/Factory.TypedArrays.cs). Every
      builtin Genesis installs for these areas is registered, including
      Array.fromAsync (array-from-async.tq's promise-driven state machine,
      Builtins.Array.FromAsync.cs). Array.prototype.sort
      is the PowerSort of third_party/v8/builtins/array-sort.tq (the oracle's
      V8 14.7 still sorts with TimSort, so comparison traces are checked
      against the tree's algorithm, not the oracle); typed array sort, join
      with the cycle stack, base64/hex (with V8's simdutf truncation
      behaviour), resizable/growable buffers, transfer/detach, Float16.
      Atomics.wait blocks on a process-wide FutexEmulation; Atomics.waitAsync
      suspends with async waiters resolved from the isolate's foreground task
      runner (notify, delayed timeout tasks). Runtime functions:
      Runtime/Runtime.TypedArray.cs (runtime-typedarray.cc, runtime-futex.cc,
      %HasFixed*Elements). 69 xUnit tests (tests/V8Sharp.Tests/Builtins/{Array,
      TypedArray,DataView}*.cs), expectations from the oracle. test262
      (v8sharp, 2026-09-28): built-ins/{Array,ArrayBuffer,TypedArray,
      TypedArrayConstructors,DataView,Atomics,SharedArrayBuffer,Uint8Array}/**
      100%.
- [~] Number, Math, BigInt, JSON, Date builtins (Builtins/Builtins.{Number,
      Math,BigInt,Json,Date}*.cs, Json/, Date/, Objects/BigInt*.cs): every
      builtin of builtins-number.cc/number.tq (toString(radix), toFixed,
      toExponential, toPrecision, toLocaleString without ICU, is*, valueOf,
      the Number constructor), math.tq/builtins-math.cc (all functions,
      hypot fast/slow paths, xorshift128+ Math.random with --random-seed,
      Math.sumPrecise with Xsum, f16round), builtins-bigint.cc/.tq
      (constructor, asIntN/asUintN, toString, operators with the NoThrow
      stubs), src/objects/bigint.cc on V8Sharp.Base.BigInts (the System.Numerics
      bridge is gone), src/json (parser with the reviver context argument,
      stringifier, rawJSON/isRawJSON), src/date (DateCache with the offset
      cache, dateparser, MakeDay/MakeTime/TimeClip, ToDateString) and
      builtins-date.cc/-gen.cc. Tests (tests/V8Sharp.Tests/{Builtins,Date,
      Json}): bigint-unittest CompareToDouble, date-unittest (DST cache,
      legacy parser counter), json-unittest (seeded instead of fuzzed), and
      oracle differentials: BigInt ops on random values up to 800 digits,
      Date.parse over the mjsunit date strings plus extra formats, the Date
      string formats and getters/setters (also run under TZ=America/New_York,
      Europe/London, Asia/Kolkata, America/Sao_Paulo), 770 JSON texts through
      parse+stringify. test262 (v8sharp, 2026-09-28): built-ins/{Number,Math,
      BigInt,JSON,Date}/** 100% (Temporal's Date.prototype.toTemporalInstant
      skipped). JSON revivers, replacer
      functions and toJSON are checked against the oracle with API functions.
      Not ported: FastJsonStringifier and JSDataObjectBuilder (see
      deviations.md, JSON), the typed-array fast path of IterableForEach.
- [~] String and RegExp builtins (Builtins/Builtins.String*.cs,
      Builtins.RegExp*.cs, Runtime/Runtime.Regexp.cs, Runtime.Strings.cs,
      Objects/JSRegExp*.cs, Strings/StringSearch.cs): every String,
      String.prototype, String Iterator, RegExp, RegExp.prototype, RegExp
      String Iterator builtin and the legacy RegExp statics; JSRegExp::Initialize
      (+ CreateFromBoilerplate for CreateRegExpLiteral), the regexp compilation
      cache, RegExpMatchInfo, the fast-path checks (BranchIfFastRegExp,
      IsUnmodifiedRegExp), the batched global exec, CompiledReplacement,
      RegExpExecMultiple, the results caches (split, string split, multiple
      indices, global atom), RegExpSyntaxValidator for the parser. 40 xUnit
      tests (tests/V8Sharp.Tests/Builtins). test262 (v8sharp, 2026-09-28):
      built-ins/{String,RegExp,StringIteratorPrototype,
      RegExpStringIteratorPrototype}/** and annexB/built-ins/** 100%; the
      ICU-emulating case folding passes 9 tests V8's no-ICU build fails
      (listed as PASS in expectations/test262.v8sharp.txt). Missing:
      Intl-dependent behaviour (V8Sharp is the non-ICU build).
- [ ] modules (import/export, dynamic import, top-level await)
- [ ] eval / new Function / with

## Conformance progress (V8Sharp engine)

| date | suite | pass | run | rate | notes |
|---|---|---|---|---|---|
| 2026-09-28 | test262 | 67759 | 94901 | 71.4% | first run; 79.1% without Temporal. Failing: async functions/generators/for-await, modules, dynamic import (interpreter, in progress); Promise/Iterator/Map/Set/Weak*/DisposableStack (collections port, in progress); Temporal (9210) |
| 2026-09-28 | mjsunit | 6412 | 7597 | 84.4% | first run; clusters: regress (337), harmony (199, mostly async), maglev/compiler/turbolev (168, optimization-status asserts until the tiers exist), d8 (45) |

Performance (Octane scores; V8Sharp interpreter vs the oracle, 2026-09-28):

| benchmark | V8Sharp | V8 --jitless | V8 jit |
|---|---|---|---|
| Richards | 224 | 1351 | 35500 |
| DeltaBlue | 212 | 1425 | 61457 |
| Crypto | 171 | 1150 | 31208 |
| RayTrace | 504 | 2801 | 58903 |
| EarleyBoyer | 791 | 4711 | 36484 |
| RegExp | 780 | 1759 | 4595 |
| NavierStokes | 632 | 1441 | 27465 |

- [ ] Temporal: V8 15.6 implements it as a binding layer
      (`src/objects/js-temporal-objects.cc`, `builtins-temporal.cc`) over the
      Rust crate temporal_rs (`third_party/rust/temporal_capi`, not in this
      checkout). Needs a C# implementation of the temporal_rs surface V8 uses.

## Phase 2: the fast tiers

- [ ] TieringManager: interrupt budget, OSR triggers (port of tiering-manager.cc)
- [ ] Baseline compiler: bytecode -> IL (Sparkplug analogue)
- [ ] Optimizing compiler: SSA graph from bytecode + feedback, speculative
      representations, inlining, deoptimizer (Maglev analogue)
- [ ] SIMD fast paths: elements accessors, string search, typed arrays
- [ ] Benchmarks: test/js-perf-test, JetStream-like, against the oracle

## Merge cleanups

- TODO(merge) from the Ignition port: `NativeContextFields` moves to the
  objects code (Context::Field); the constant pool is `object[]` until heap
  constants exist. (Token, LanguageMode and the other shared enums now come
  from V8Sharp.Parsing; runtime ids are `V8Sharp.Runtime.FunctionId`.)
- TODO(merge) from the bytecode generator port: the object model implements
  `IBytecodeGeneratorHeap` (replacing `DefaultBytecodeGeneratorHeap`,
  `SharedFunctionInfoDescription`, the boilerplate `*Data` descriptions and
  `ClassBoilerplateDescription`); `FeedbackVectorSpec.cs` moves to
  src/objects/feedback-vector; `UnoptimizedCompilationInfo` and
  `UnoptimizedCompiler` move to the codegen/compiler port;
  `BytecodeGeneratorFlags` routes through the isolate's FlagList.
- TODO(merge): Parsing's `NumberConversions` and string hashing should use
  V8Sharp.Base (`Conversions`, `StringHasher`).

Stand-ins in the engine that go away when the component they wait for merges
(each marked `TODO(merge)` at the site):

- `src/V8Sharp/Objects/HashTable.cs` (`Hashing`) and
  `src/V8Sharp/Strings/StringHasher.cs`: use V8Sharp.Base's hashing
  (rapidhash with the isolate's hash seed).
- `src/V8Sharp/Objects/String.cs` (`CharPredicates`): use V8Sharp.Base's
  char-predicates.
- `src/V8Sharp/Objects/ScopeInfo.cs`: implement V8Sharp.Parsing's IScopeInfo.
- `src/V8Sharp/Objects/JSFunction.cs`: type the bytecode as
  V8Sharp.Interpreter.BytecodeArray.
- `src/V8Sharp/Objects/JSObjectShapes.cs`: module namespace as the module
  system's Module.


- Engine-wide: replace `double.NaN` (0xFFF8..., sign bit set) with
  `JSValue.QuietNaN` (V8's 0x7FF8...) wherever a NaN constant can reach a
  Float64Array/DataView store (ToNumber of non-numeric strings, Math results,
  Date invalid time value). `JSValue.NaN` is already fixed.

## Deviations

Recorded in `deviations.md`.
