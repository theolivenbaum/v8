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
      Temporal (lazily, Init/Genesis.Temporal.cs).
      Missing: Intl, shared structs, extras, extensions,
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
- [x] feedback vectors and ICs (src/ic, Objects/Feedback*.cs): FeedbackVector,
      FeedbackNexus, closure feedback cells, lazy feedback allocation;
      LoadIC/StoreIC/KeyedLoadIC/KeyedStoreIC/LoadGlobalIC/StoreGlobalIC/
      DefineNamedOwnIC/DefineKeyedOwnIC/StoreInArrayLiteralIC/HasIC with
      monomorphic, polymorphic and megamorphic (stub cache) states and C#
      handler objects; element handlers with validity cells and
      ElementsTransitionAndStore; typed array element loads; call, construct
      (AllocationSite for Array), instanceof, binary-op and compare feedback;
      allocation mementos (JSArray.AllocationMementoSite) with
      DigestTransitionFeedback; CloneObjectIC with FastCloneJSObject and the
      kCloneObject side-step transitions (IC/CloneObjectIC.cs). Tests:
      tests/V8Sharp.Tests/IC. Open: LoadSuperIC handlers, typed array element
      stores in the IC, pretenuring.
- [x] interpreter dispatch loop (Interpreter/InterpreterLoop.cs, one loop per
      operand scale, rare bytecodes in LoopCold), frames on the register
      stack in V8's layout with bytecode-to-bytecode calls and constructs in
      the caller's loop (InterpreterInlineCalls), exceptions and handler
      tables, generators, async functions, async generators and for-await
      (InterpreterAsync, Builtins.Async.cs, port of builtins-async-*-gen.cc),
      disposable stacks (using / await using), stack traces through
      interpreter frames incl. async frames (CaptureAsyncStackTrace).
      Tests: tests/V8Sharp.Tests/Interpreter. Open: baseline tier (in
      progress elsewhere), debugger hooks.
- [x] runtime functions (Runtime/, RuntimeTable): every function the
      bytecode generator emits, plus the mjsunit test natives (%Prepare/
      Optimize* answer as --jitless V8, elements-kind queries, protectors,
      %HasCowElements, %NormalizeElements, %HasFixed*Elements ...). Open:
      %GetFeedback, %RuntimeEvaluateREPL, block coverage (%DebugToggleBlock
      Coverage, %DebugCollectCoverage), %ShareObject/shared structs, the
      runtime.cc IsEnabledForFuzzing allowlist in the parser (until then the
      compiler does not pass --fuzzing to the parser).
- [~] builtins: Object, Function, Reflect, Proxy, global functions (URI
      coding, escape/unescape, isNaN/isFinite, parseInt/parseFloat, eval),
      Error (+ AggregateError, SuppressedError, captureStackTrace, isError,
      CallSite methods), Boolean, Symbol: ported (Builtins/Builtins.{Object,
      Function,Reflect,Proxy,Global,Error,Boolean,Symbol}*.cs; tests in
      tests/V8Sharp.Tests/Builtins, URI/parse functions and the intrinsics'
      shapes checked against the oracle). ShadowRealm (builtins-shadow-realm.cc,
      InitializeGlobal_harmony_shadow_realm, behind the experimental
      --harmony-shadow-realm that test262 turns on; Builtins.ShadowRealm.cs):
      constructor, evaluate, importValue (the ExportGetter
      ShadowRealmImportValueFulfilled over the host's dynamic import),
      CallSite boundary checks; hosts set
      Isolate.HostCreateShadowRealmContextCallback to d8's
      HostCreateShadowRealmContext (ModuleLoader, a module map per realm). `new Function` and indirect eval call
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
      BigInt,JSON,Date}/** 100% (Date.prototype.toTemporalInstant: see
      Temporal below). JSON revivers, replacer
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
- [x] modules (Objects/Module.cs: module.cc, source-text-module.cc,
      synthetic-module.cc; Runtime/RuntimeModules.cs): instantiate/link,
      evaluate with top-level await and async module evaluation, namespaces,
      import.meta, dynamic import with phases, import attributes (JSON, text
      and bytes modules in the d8sharp loader, V8Sharp.D8/ModuleLoader.cs),
      import defer (JSDeferredModuleNamespace and its lookup hooks,
      GatherAsynchronousTransitiveDependencies, ReadyForSyncExecution,
      EvaluateForImportDefer), source phase imports (module sources exist
      only for WebAssembly, not ported: d8's SyntaxError), d8's --bundle
      (V8Sharp.D8/Bundle.cs, in d8sharp and the TestRunner). test262
      language/module-code, language/import, language/expressions/dynamic-
      import, staging/source-phase-imports: 100%.
- [x] eval / new Function / with (Compiler.GetFunctionFromEval with the eval
      origin, CreateDynamicFunction, lookup slots, sourceURL comments).
- [x] d8sharp shell (src/V8Sharp.D8): print/write/read/load/quit, Realm,
      d8.file/d8.test basics, performance.now, modules (.mjs), the message
      loop; TestRunner engine V8SharpEngine and the Bench host.

### Builtin failures seen by the interpreter port

Resolved by the engine conformance pass (2026-09-28): Object.keys/JSON.stringify
of typed arrays, holey `new Array(n)`, `console` installed in Genesis, `print`
as an API function in the TestRunner, Atomics.waitAsync, Worker and the d8
serializer. The typed array species protectors match the oracle (V8Sharp
invalidates %TypedArray%'s one more often than V8, which only costs speed).

## Conformance progress (V8Sharp engine)

| date | suite | pass | run | rate | notes |
|---|---|---|---|---|---|
| 2026-09-28 | test262 | 67759 | 94901 | 71.4% | first run; 79.1% without Temporal. Failing: async functions/generators/for-await, modules, dynamic import (interpreter, in progress); Promise/Iterator/Map/Set/Weak*/DisposableStack (collections port, in progress); Temporal (9210) |
| 2026-09-28 | mjsunit | 6412 | 7597 | 84.4% | first run; clusters: regress (337), harmony (199, mostly async), maglev/compiler/turbolev (168, optimization-status asserts until the tiers exist), d8 (45) |
| 2026-09-28 | test262 | 85313 | 85891 | 99.3% | as expected, Temporal skipped (after engine conformance and interpreter perf passes) |
| 2026-09-28 | mjsunit | 7262 | 7597 | 95.6% | as expected; remaining: opt-proto-seq (Sparkplug off), optimization-status asserts, import defer, unported d8 hooks |
| 2026-09-28 | test262 | 85649 | 85891 | 99.7% | modules pass: import defer, source phase and bytes imports, dynamic-import/catch, ShadowRealm importValue (+336); the 242 unexpected are expected-PASS lines (ICU emulation), none failing; Temporal skipped |
| 2026-09-28 | mjsunit | 7321 | 7597 | 96.4% | modules pass (+58): import defer, --bundle, --compile-only, clone-ic-regressions, spread without kMaxArguments, ShadowRealm importValue |
| 2026-09-28 | message | 333 | 333 | 100% | message and webkit pass (was 319/334): Python `%%` in .out files, d8's D8Console, `--json`, `--trace-config`, missing command-line file, stalled top-level await location, message listener reports (FinalizationRegistry callbacks), `%DebugTraceMinimal`, fast class constructor maps (inferred names in stack frames), rejection messages without location (`undefined:0`); js-wasm-wrapper-inlining-turbolev* SKIP (Wasm + Turbolev traces); expectations message.v8sharp.txt |
| 2026-09-28 | webkit | 543 | 543 | 100% | same pass (was 542/543): `f.arguments.callee` (Factory::NewArgumentsObject stores the callee for sloppy functions); expectations webkit.v8sharp.txt; mjsunit +2 (7331/7602), test262 unchanged |
| 2026-09-28 | test262 | 82005 | 94901 | 86.4% | --no-sparkplug and --always-sparkplug: identical results (0 differences) |
| 2026-09-28 | mjsunit | 6767 | 7581 | 89.3% | --no-sparkplug (the interpreter only) |
| 2026-09-28 | mjsunit | 6822 | 7582 | 90.0% | --always-sparkplug: +60 (opt-proto-seq tests call %CompileBaseline, which needs Sparkplug), -6: element-read-only, ic-lookup-on-receiver, regress-4296, regress-crbug-1003732, -1259950, -662907 fail in the interpreter too with --no-lazy-feedback-allocation (IC bugs that eager feedback exposes) |
| 2026-09-28 | test262 built-ins+annexB+staging (no Temporal) | 39189 | 39268 | 99.8% | builtins conformance pass, after merging the interpreter branch (was 98.1%, 760 unexpected). Left: modules, `accessor`, two realm cases (see "Interpreter failures seen by the builtins conformance pass") |
| 2026-09-28 | test262 | 82412 | 85669 | 96.2% | Temporal marked SKIP in test262.v8sharp.txt (11448 skipped with the status file's); expectations regenerated |
| 2026-09-28 | mjsunit | 6953 | 7597 | 91.5% | builtins conformance pass (6926 in the full run, +27 on rerunning its failures after access checks landed); expectations file mjsunit.v8sharp.txt generated |

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
| 2026-09-28 | test262 | 84670 | 94901 | 89.2% | interpreter port complete (async, modules, eval); 98.8% without Temporal (9210). Remaining: import defer and source phase imports (not ported), ShadowRealm (124), Array.fromAsync, Atomics.waitAsync; expectations: tools/V8Sharp.TestRunner/expectations/test262.v8sharp.txt |
| 2026-09-28 | mjsunit | 7074 | 7597 | 93.1% | clusters: Worker and d8 host features, optimization-status asserts, import defer (43), ShadowRealm/Wasm/shared structs; expectations: mjsunit.v8sharp.txt |
| 2026-09-28 | test262 | 85199 | 85891 | 99.2% | engine conformance pass; left: import defer (180), dynamic-import/catch (128, modules), decorators (34, not in V8 either), bytes imports, ShadowRealm importValue, RegExp legacy accessors; expectations regenerated |
| 2026-09-28 | test262 | 94545 | 95123 | 99.4% | Temporal ported: built-ins/Temporal 9210/9210, staging/Temporal 4/4, Date.prototype.toTemporalInstant 16/16; Temporal SKIP globs removed (intl402 still not run without i18n); 0 newly failing elsewhere |
| 2026-09-28 | mjsunit | 7259 | 7597 | 95.6% | engine conformance pass (Sparkplug off by default: opt-proto-seq/* fail); see "Engine-side conformance: what is left"; expectations regenerated |
| 2026-10-02 | test262 | 94881 | 95123 | 99.7% | baseline performance pass, --always-sparkplug: 0 newly failing, 0 newly passing against test262.v8sharp.txt (run as built-ins and the rest) |
| 2026-10-02 | mjsunit | 7378 | 7587 | 97.2% | baseline performance pass, --always-sparkplug: +3 (opt-proto-seq/**, private_fields/test_private_fields, regress-484904778), -3: regress-class-initializer-eval (fails in the interpreter too with --no-lazy-feedback-allocation), unicode-case-overoptimization0/1 (timeouts under load; 40 s alone in both tiers) |
| 2026-10-02 | mjsunit | 7386 | 7600 | 97.2% | baseline performance pass, --sparkplug (tiering with concurrent compilation, feedback-guided code): +3 as above, -1 baseline/test-baseline (fixed since: the size limit no longer applies to %CompileBaseline) |
| 2026-10-02 | mjsunit | 7330 | 7602 | 96.4% | baseline performance pass, default flags (Sparkplug off): 0 newly failing, +1 regress-484904778 |

Octane, interpreter only, after the interpreter performance pass
(2026-09-28, 4-core container shared with a test262 run; mean of 4 runs,
RegExp 2; orig and new interleaved with V8 --jitless. "orig" is the
interpreter at f584ccaf; both V8Sharp columns run `--engine v8sharp`, which
keeps compiled regexps; `v8sharp:jitless` also interprets regexps and scores
RegExp about 20% lower):

| benchmark | orig | now | V8 --jitless | now / jitless |
|---|---|---|---|---|
| Richards | 339 | 450 | 1361 | 33% |
| DeltaBlue | 322 | 361 | 1420 | 25% |
| Crypto | 239 | 434 | 1069 | 41% |
| RayTrace | 697 | 1026 | 3182 | 32% |
| EarleyBoyer | 935 | 1401 | 4799 | 29% |
| RegExp | 840 | 821 | 1959 | 42% |
| Splay | 1185 | 1516 | 2465 | 61% |
| NavierStokes | 619 | 1080 | 1462 | 74% |
| geomean | 566 | 779 | 1965 | 40% |

The pass (deviations.md, Interpreter): a dispatch loop small enough for
RyuJIT to enregister acc/pc/fp, with cold bytecodes in NoInlining handlers;
SaveBytecodeOffset instead of a per-bytecode pc store; inline monomorphic and
polymorphic named/keyed loads and stores, global cells, Smi/number
arithmetic, comparisons and int32 bitwise ops; unchanged reference stores
skipped (write barriers); register stack and frame records on the pinned
object heap (gen-0 GCs 3.3 ms to 0.4 ms); shallow literal cloning with copy
constructors; CSA-style instanceof/OrdinaryHasInstance; f.call/f.apply
entered without a builtin frame. Micro (tools/V8Sharp.Bench micro:all,
v8sharp / jitless, calls per second): ArithLoop 53/412, CallLoop 61/274,
PropertyLoad 175/351, ObjectLiteral 66/218, ArrayPush 21/227.

What is left before 2x of jitless: per-call cost (~55 ns vs ~12 ns: frame
record and register writes with GC barriers, argument copying), builtin
calls through CallBuiltin (Array.prototype.push ~200 ns), object
allocation (JSObject + JSValue[] fields, GC), and JIT warm-up (libclrjit is
20-25% of a short Octane run; ReadyToRun or a longer run would cut it).

Octane, interpreter only, after the second interpreter performance pass
(2026-09-28, 4-core container, load 1-3; mean of 3 interleaved runs of
V8Sharp.Bench `octane:<name>` with `v8sharp@<build>`; "before" is 757f225f,
"now" is this pass):

| benchmark | before | now | V8 --jitless | now / jitless |
|---|---|---|---|---|
| Richards | 509 | 572 | 1319 | 43% |
| DeltaBlue | 418 | 528 | 1371 | 39% |
| Crypto | 484 | 476 | 1238 | 38% |
| RayTrace | 1118 | 1169 | 3406 | 34% |
| EarleyBoyer | 1532 | 1809 | 5428 | 33% |
| RegExp | 1019 | 1039 | 2258 | 46% |
| Splay | 1543 | 2373 | 3569 | 67% |
| NavierStokes | 1175 | 1097 | 1498 | 73% |
| geomean | 964 | 1081 | 2369 | 46% |

(With a quiet machine V8 --jitless scores about 20% higher than in the
table above this one; both V8Sharp columns run `--engine v8sharp`.) By
thread CPU time (`octane-cpu`, fixed work) the pass is +13% on the same
eight: Richards +13%, DeltaBlue +20%, RayTrace +15%, EarleyBoyer +35%,
NavierStokes +6%, Crypto 0, RegExp -5%, Splay -1%. The pass, one commit
each (git log): in-object properties in object slots (V8's layout, neutral
for speed), `InterpreterState` as a ref struct (no write barriers on it),
inline calls that leave their frame's slots for the next call at the depth
and compare before each reference store (micro CallLoop +25%), frame
records not cleared on return, FastNewObject for `new F`, CSA-style
fast paths for instanceof (EarleyBoyer +7%) and push/pop/shift (micro
ArrayPush +48%), compare feedback by instance type, a dispatch loop whose
hot helpers all fit RyuJIT's inlining budget again (Richards +9%,
NavierStokes +11%), and prototype-field loads on the inline IC path.
ReadyToRun for `d8sharp` publishes cuts start-up (print(1) 339 to 138 ms)
but not Octane scores; TieredPGO is worth about 20% and stays on.

What is left before 2x of jitless (46% now): the dispatch loop itself
(Crypto is 73% in the loop: 16-byte JSValue registers, Smi checks on
doubles for every arithmetic feedback update), calls (~45 ns vs ~20 ns:
CallUndefinedReceiver2 is still ~200 instructions and the register stack
stores of object arguments pay GC write barriers), allocation (a plain
object is 56 bytes of header plus 16-byte slots; JSObject header fields
_dictionary and _identityHash could fold into V8's properties_or_hash),
builtin calls through CallBuiltin (Math.*, charCodeAt: 3-4.5x slower than
V8 jitless), and string concatenation (4.5x).

Octane, interpreter only, after the third interpreter performance pass
(2026-09-29, 4-core container shared with other jobs; mean of 3
interleaved runs of V8Sharp.Bench `octane:<name>`; "before" is 01fa85dd,
"now" is this pass; wall scores are noisy by 5-15% per benchmark, Splay
most):

| benchmark | before | now | V8 --jitless | now / jitless |
|---|---|---|---|---|
| Richards | 580 | 581 | 1382 | 42% |
| DeltaBlue | 495 | 558 | 1455 | 38% |
| Crypto | 448 | 519 | 1201 | 43% |
| RayTrace | 1189 | 1277 | 2986 | 43% |
| EarleyBoyer | 1809 | 2084 | 5581 | 37% |
| RegExp | 1098 | 1274 | 2202 | 58% |
| Splay | 2398 | 2007 | 3443 | 58% |
| NavierStokes | 1132 | 1306 | 1584 | 82% |
| geomean | 976 | 1051 | 2165 | 48.6% |

By thread CPU time (`octane-cpu`, V8SHARP_BENCH_SCALE=10, fixed work, mean
of 3) the pass is +7.1% (geomean 132.6 to 142.0; V8 --jitless 326.6, so
43.5% of jitless): Richards +2.5%, DeltaBlue +6.4%, Crypto +4.4%,
RayTrace +4.9%, EarleyBoyer +7.9%, RegExp +7.7%, Splay +18%,
NavierStokes +5.4%. The pass, one commit each (git log): the
number-string cache (NumberToString +15%), frameless builtin calls
(V8's frameless builtins list), string length and prototype loads on the
inline LoadIC path, CSA-style fast paths for Math.*, charCodeAt/charAt/
codePointAt, push/pop/shift and Number toString, IsString by instance
type (string concat 89 to 69 ns), SmiMod, String+Number in AddSlow via
the cache, the hash word shared by Name and JSReceiver on HeapObject
(strings 8 bytes smaller), classifying Smis once in arithmetic feedback,
no baseline OSR probe in JumpLoop until something has baseline code
(empty loop -9%), in-object slot classes for 1-4 fields (Splay +21% CPU),
array length on the inline IC path (micro -38%), mono/polymorphic
StoreIC from feedback on the inline path (Splay +6%), the megamorphic
load stub cache and F.prototype on the inline path, polymorphic keyed
element stores (1.2M slow stores to under 1K per run), and an
OrdinaryHasInstance check cached on the function's map.

Tried and dropped (no measurable effect or slower): folding _dictionary
into a properties_or_hash field, dropping ConsString's flat cache, a
stack-base local in the dispatch loop (-30%: RyuJIT stops tracking
locals past its limit, DOTNET_JitMaxLocalsToTrack=0x1800 gains ~10% on
micro loops but cannot be set from runtimeconfig), outlining the OSR and
wide-prefix paths, trimming barriers in CallBuiltinWithFrame, GC knobs
(gen0size, RetainVM).

What is left before 2x of jitless (48.6% now): builtins and string
concatenation turned out to be a small share of Octane; the gap is the
dispatch loop and calls. The loop sits at RyuJIT's local-tracking limit,
so any new local spills the accumulator; the single indirect dispatch
jump predicts poorly; calls are ~45-50 ns against ~20 ns (frame record,
register stack stores with GC write barriers, argument copying); property
access is pointer chasing (JSObject -> Map -> FixedArray feedback). The
interpreter can probably reach 55-60% with a leaner frame protocol
(register window without barriers, e.g. a struct-of-arrays with object
and number halves); beyond that the IL tiers are the lever.

Octane, interpreter only, after the fourth interpreter performance pass
(2026-10-02; all 15 benchmarks, 17 scores; 4-core container shared by three
agents, every run under the benchmark lock; mean of 3 interleaved runs of
V8Sharp.Bench `octane:<name>`). "bin" is a `dotnet build` of this pass;
"publish" is the parity configuration (tools/V8Sharp.Bench/README.md: a
ReadyToRun composite, self-contained publish, TieredPGO off, call counting
without delay). Wall scores are noisy, 5-25% per benchmark (Splay most), and
V8 --jitless in ClearScript sometimes crashes (the mean is over the runs
that finished):

| benchmark | bin | publish | V8 --jitless | publish / jitless |
|---|---|---|---|---|
| Richards | 703 | 654 | 1344 | 49% |
| DeltaBlue | 494 | 540 | 1446 | 37% |
| Crypto | 561 | 570 | 1307 | 44% |
| RayTrace | 1249 | 1187 | 3456 | 34% |
| EarleyBoyer | 2377 | 2128 | 6021 | 35% |
| RegExp | 1233 | 1189 | 2406 | 49% |
| Splay | 2656 | 2536 | 2974 | 85% |
| SplayLatency | 2386 | 2430 | 3138 | 77% |
| NavierStokes | 1289 | 1636 | 1427 | 115% |
| PdfJS | 1457 | 2326 | 7944 | 29% |
| Mandreel | 445 | 408 | 1041 | 39% |
| MandreelLatency | 1474 | 2179 | 6257 | 35% |
| Gameboy | 2452 | 2523 | 7479 | 34% |
| CodeLoad | 2300 | 5869 | 16289 | 36% |
| Box2D | 1842 | 2058 | 3765 | 55% |
| zlib | 723 | 652 | 2160 | 30% |
| Typescript | 5853 | 5751 | 15588 | 37% |
| geomean | 1382 | 1534 | 3445 | 44.5% (bin 40.1%) |

By thread CPU time (`octane-cpu`, V8SHARP_BENCH_SCALE=10, one interleaved
run; the latency scores are not meaningful in this mode and are left out),
as a share of V8 --jitless, for be13ca51 (before this pass), this pass from
bin, and the publish:

| benchmark | before | bin | publish |
|---|---|---|---|
| Richards | 50% | 52% | 47% |
| DeltaBlue | 43% | 41% | 40% |
| Crypto | 42% | 45% | 45% |
| RayTrace | 47% | 49% | 41% |
| EarleyBoyer | 40% | 41% | 38% |
| RegExp | 37% | 36% | 42% |
| Splay | 40% | 47% | 44% |
| NavierStokes | 84% | 79% | 97% |
| PdfJS | 11% | 19% | 26% |
| Mandreel | 21% | 42% | 41% |
| Gameboy | 19% | 29% | 33% |
| CodeLoad | 10% | 11% | 24% |
| Box2D | 44% | 44% | 47% |
| zlib | 20% | 33% | 32% |
| Typescript | crashed | 27% | 33% |
| geomean | 31.3% (14) | 36.5% | 39.7% |

On the 14 benchmarks that ran before, the pass is +19% (bin) and +28%
(publish) of thread CPU. The pass, one commit each (git log be13ca51..):
the current bytecode as a `ref byte` and the accumulator's payload as a
long, so it stays in callee-saved general registers (System V has no
callee-saved XMM; +3.2% CPU on the eight classic benchmarks); element
store handlers with a prototype validity cell on the inline path (an
array element store took the slow handler, 11% of NavierStokes);
polymorphic feedback on the inline GetNamedProperty path; ToBoolean by
instance type; Smi and index tests by an int round trip;
DescriptorArray::Append's collision check (Octane TypeScript threw
"duplicate descriptor"); typed array element store handlers
(StoreElementHandler returned the slow stub and
MayHaveTypedArrayInPrototypeChain started at the receiver, so every typed
array store missed: PdfJS 588 to 380 ms per run) and per-kind typed
element access; the scanner copying a large source once instead of per
lazy compile; bitwise bytecodes on Numbers that are not int32 (asm.js
`x | 0`); the typed array length getter without a builtin frame (Gameboy);
the publish configuration. Tooling: `octane-steady` (a warm-up pass before
the measured one) and `micro/cpu.js` (CPU-time micro-benchmarks).

Tried and dropped, measured: the Star lookahead of V8's dispatch
(IsStarLookahead: -2.6%); bytecode quickening with feedback embedded in
the bytecode (0%; with duplicated handlers the loop passes RyuJIT's
local limit and slows down); threaded dispatch through function pointers
(a prototype with tail calls runs at the switch's speed; it needs unsafe);
a different BytecodeArray layout (-5%); the loop without
AggressiveOptimization (-5.3%); caching the inline-call entry on the
SharedFunctionInfo (-0.7%); the segments GC (`System.GC.Name=libclrgc.so`:
write barriers half the cost, +10% on Richards/DeltaBlue/EarleyBoyer, -11%
Splay, +2.6% geomean, a non-default GC); `System.GC.LOHThreshold` 2 MB
(PdfJS gen-2 GCs 15 to 3 per process and -7.5% per run steady, mixed on
the rest); ReadyToRun without composite and with TieredPGO (mixed: CodeLoad
1.8x, PdfJS and Mandreel -10%); bringing the loop under RyuJIT's
local-tracking limit (it had about 1050 locals against 1024; moving two
inlined helpers out got it under, as `DOTNET_JitMaxLocalsToTrack` scans of
the listing show) did not help: `DOTNET_JitMaxLocalsToTrack=0x1800` gives
the old loop +7% on loop micros, the trimmed loop -4% with or without it,
another case of code generation and placement deciding, not the work.

What blocks parity (44.5% of jitless by Octane's own score), measured:
- The dispatch loop costs about 2x V8's bytecode handlers per bytecode.
  `micro/cpu.js` (best of 5, CPU): V8 --jitless is 2.25x on the geomean of
  18 constructs: empty loop 2.2x, int arithmetic 2.9x, property load 1.7x,
  property store 2.1x, prototype method call 2.9x, call 2.1x, array read
  2.3x, typed array read 2.7x, `new` 3.4x, object literal 3.0x, closure
  2.9x. The loop sits at RyuJIT's local-tracking limit (512): handlers
  spill to the stack, and code placement alone moves the micros by 20-40%
  (adding a never-executed block before the loop changed CpuArrayRead
  from 19 to 31 and CpuPropLoad from 27 to 39 with identical handler
  code). zlib and Mandreel are dispatch-bound (63% of zlib is the loop).
- Calls: about 250 instructions for a call and return (frame record,
  register window, argument copies), 20% of Richards and DeltaBlue.
- Allocation and GC: write barriers are 10-12% of Richards, DeltaBlue and
  EarleyBoyer; a new object is two allocations (JSObject and its field
  array) against V8's one bump allocation.
- Parsing and compiling: CodeLoad and TypeScript (see the front-end pass
  below: compiles are now 38-60% of V8 --jitless on the large sources,
  CodeLoad 48%; what is left is the scanner and preparser per token, GC of
  the AST and of the large eval'd source strings, and StringTable lookups
  when constants are internalized).
- The .NET JIT: 12000-15000 engine methods compiled per large benchmark;
  the publish configuration removes most of it, the handlers only the
  AggressiveOptimization loop calls are still compiled at tier 0 first.
Beyond the interpreter, the IL tiers remain the lever.

Fifth interpreter performance pass: calls and dispatch (2026-10-02; parity
publish, under the benchmark lock; "before" is 2a352966, "after" this pass;
micro best of 5 x 3 runs, octane-cpu SCALE=10 mean of 2, wall Octane mean
of 2; V8 --jitless in ClearScript crashes on some runs, its mean is over
the runs that finished):

| | before | after | V8 --jitless | before / after of jitless |
|---|---|---|---|---|
| micro/cpu.js geomean (18) | 22.17 | 23.98 | 44.95 | 49.3% / 53.3% |
| octane-cpu geomean (14, no zlib/latency) | 230.4 | 239.5 | 548.9 | 42.0% / 43.6% |
| Octane wall geomean (17 scores) | 1790 | 1843 | 3934 | 45.5% / 46.8% |

octane-cpu per benchmark (after/before): Richards +3.7%, DeltaBlue +8.1%,
Crypto +2.2%, RayTrace +2.1%, EarleyBoyer +5.0%, RegExp -3.3%, Splay -1.3%,
NavierStokes +9.9%, PdfJS +2.7%, Mandreel +8.8%, Gameboy +9.6%, CodeLoad
+4.9%, Box2D -2.6%, zlib +3.4%, Typescript +6.9% (run-to-run noise is
3-10% per benchmark on this shared host).

The pass, one commit each (git log 2a352966..):
- Inline calls (InterpreterInlineCalls.EnterInline): a call and return
  took about 490 instructions (CanInline read six SharedFunctionInfo
  fields per call; PushFrameCore was a 12-parameter call taking the
  receiver and arguments by value, with the frame's addresses spilled
  around every write barrier; the return went through two calls). The
  SharedFunctionInfo now caches its call mode (InterpreterCallMode,
  reset by the setters of what it depends on) and the call handlers push
  the frame straight-line per argument form; `new`, f.call and f.apply
  share the entry. octane-cpu +6.1% on 8 call-heavy benchmarks
  (Richards +4.6%, DeltaBlue +5.2%, EarleyBoyer +7.2%). The call path is
  now about 200 instructions per call and return, flat (no single hot
  spot): CallProperty0/1 and the return are 15% of Richards (21% before).
- Number fast paths that never call out (Add, Sub, Mul, Inc, Dec, AddSmi,
  SubSmi, comparisons): inline only when the embedded feedback already
  covers the operation, so RyuJIT no longer spills the operands on every
  execution (Add went from ~25 instructions with three stack stores and a
  store-to-load round trip to ~12 with none). micro +10%, octane-cpu +2%
  (Crypto +13%).
- GetNamedProperty: only monomorphic hits inline (the polymorphic lookup
  call made the JIT spill the receiver on the monomorphic path): micro
  ProtoMethod +41%, octane neutral.
- StoreRegister stores the payload before the reference (no spill around
  the barrier): 2 instructions less per Star, neutral.

Tried and dropped, measured:
- Threaded dispatch by replicated dispatch switches (a `switch` with a
  `goto` per bytecode at the end of each hot handler, so every handler has
  its own indirect jump, as V8's Dispatch does): with 14 handlers it was
  within noise (+2% micro geomean); with 87 handlers the loop runs 2.5x
  slower (RyuJIT stops optimizing a method of that size; the loop needs
  a different shape for this, not more blocks).
- A 256-case dispatch switch: RyuJIT keeps the range check (`cmp edi,
  255; ja`) although the operand is a zero-extended byte.
- GetKeyedProperty monomorphic only inline (as GetNamedProperty): mixed,
  Crypto -9%, NavierStokes +13%; context slot stores through StoreRegister
  (skipping unchanged references): +1.7% octane, -7% micro, i.e. noise
  and code placement.

What blocks parity now (micro: V8 --jitless is 1.87x on the geomean; empty
loop 1.1x, add loop 1.7x, int arithmetic 2.6x, call 2.0x, prototype method
2.1x, `new` 3.6x, object literal 2.7x):
- Dispatch: ~25% of the loop's time in dispatch-heavy code is the
  9-instruction shared dispatch (bounds check, relative jump table, one
  indirect jump for all handlers) plus a jump back to it from every
  handler; V8 needs 3-4 instructions and a jump per handler. RyuJIT gives
  no way to emit V8's form from C# (see the threaded dispatch attempt).
- Calls: ~200 instructions per call and return, spread over the frame
  push (receiver/argument copies, register file clearing, ~10 record
  fields, the state update) and the call feedback; the next step is
  fewer fields per frame (the record, InterpreterState and the fixed slots
  hold the function, bytecode, context and feedback vector two or three
  times).
- Write barriers: 11% of Richards (field stores of objects; the register
  stack's are mostly skipped now).
- Allocation (`new` 3.6x, object literal 2.7x) and closures (2.5x) are the
  other agents' (object layout, Factory).
Conformance at the end of the pass: mjsunit 7602 run, 0 newly failing, 0
newly passing; test262 95123 run, 0 newly failing, 0 newly passing.

Sixth interpreter performance pass: frame shape and object creation
(2026-10-03; parity publish, `bench-session.sh`, V8 --jitless in the same
session; "before" is b214fa4f built with this branch's V8Sharp.Bench,
"after" 3f13618b, which also contains the merged front-end pass, so
CodeLoad's gain is mostly that pass's). The host was loaded during both
sessions (load 13-21, idle 0-23% in the fingerprints, cpu-cal 1.65-1.75 s,
mem-bw 25-34 GB/s); the columns are interleaved.

| | before | after | V8 --jitless | before / after of jitless |
|---|---|---|---|---|
| micro cpu + calls + objects geomean (24 scores, 3 runs) | 36.9 | 37.8 | 61.7 | 59.8% / 61.3% |
| octane-steady geomean (15, no latency scores, 2 runs) | 226.6 | 235.4 | 482.5 | 47.0% / 48.8% |

Object creation (micro, after/before): ClosureCreate +48%, CpuClosure
+28%, ObjectLiteral +28%, CpuObjectLiteral +26%, NewClass +34%,
NewFunction +9%, CpuNewObject +11%. octane-steady: Richards +2.7%,
DeltaBlue +2.8%, Crypto +1.8%, RayTrace +4.6%, EarleyBoyer +8.7%, RegExp
+3.4%, Splay +4.7%, Box2D +8.9%, CodeLoad +34.7% (front-end pass), PdfJS
-6.7%, NavierStokes -2.5%, Gameboy -2.0%, the rest within 2%.

The pass, one commit each (git log b214fa4f..):
- FastNewClosure: the JSFunction allocated from the native context's
  function map with its fields written once, the closure feedback cell
  array read from the feedback vector, no SavePc (CpuClosure +14%); call
  feedback tests the closures' FeedbackCell first.
- CreateShallowObjectLiteral's fast case: a boilerplate with only
  in-object properties and no elements is copied by its copy constructor;
  DefineNamedOwnProperty shares SetNamedProperty's inline monomorphic store.
- Construct: the construct mode cached on the SharedFunctionInfo beside
  the call mode, the monomorphic construct feedback hit inline, the
  feedback vector passed from the frame pointer, the runtime allocation out
  of line; FastNewObject (constructors that write only the map and the
  empty elements); the in-object StoreIC transition without the property
  array and double cases.
- The interpreter frame in V8's shape (architecture.md section 7): the
  bytecode array, bytecode offset and argument count in their fixed slots
  (fp - 3, fp - 2, fp - 4), every frame value held once; InterpreterState
  keeps the accumulator, offset and frame indices; the record is the frame
  pointer, a flags byte and the inline call's return state. Baseline,
  Maglev and the deoptimizer use the slots. For calls this was about
  neutral (CallLoop, MethodCall, CpuCall within noise): most of the
  removed stores were the compare-and-skip kind that cost little.
- A sloppy call's global proxy receiver inline (ConvertReceiver was a call
  per call); Context.NativeContext inline.

What is left: a call and return are still about 2x V8 --jitless (CpuCall
1.6x, MethodCall 1.8x, CpuProtoMethod 2.2x): the call handler's operand
decoding and feedback, EnterInline's interrupt, stack-limit and
dirty-register checks, the receiver and argument copies, the loop reload
after the call and return. `new` (2.7x), object literals (2.9x) and
closures (2.5x) allocate through the CLR allocator (header, zeroing) and
pay a write barrier for every reference field store into the new object.
Conformance at the end of the pass: mjsunit 7602 run, 0 newly failing, 0
newly passing; test262 95123 run, 0 newly failing, 0 newly passing.
Seventh interpreter performance pass: call entry, IC handler paths, frame
resume (2026-10-03; "before" is 6a480c2f, the branch with the Maglev
correctness merge). Parity publishes, `bench-session.sh`, 3 interleaved
runs with V8 --jitless in the same session (session-20261003-102501,
before/after fingerprint: load 1.9 / 1.1, steal 0%, idle 98%, cpu-cal
1852 / 1771 ms, mem-bw 32.7 / 32.8 GB/s; V8 exited 139 on some runs, its
means there are over 1-2 runs). "after" is 3a5e180f; the last commit
(4928034b, routing, below) is measured only unlocked: the locked session
for it never got the lock in 2 h.

| | before | after | V8 --jitless | before / after of jitless |
|---|---|---|---|---|
| octane-steady geomean (15, no latency scores) | 263.8 | 274.9 | 501.0 | 52.7% / 54.9% |
| micro accessors + cpu geomean (26 scores) | 17.8 | 19.5 | 35.3 | 50.5% / 55.4% |

octane-steady after/before: Richards +16%, DeltaBlue +11%, Crypto +10%,
Gameboy +8%, CodeLoad +8%, Mandreel +7%, zlib +7%, Box2D +6%, EarleyBoyer
+4%, Typescript +3%, RayTrace +2%, RegExp and NavierStokes flat, Splay -7%
(noisy across sessions), PdfJS -7% (the declined fast handlers' extra call
layer; addressed by 4928034b). Accessors (M ops/s, before / after / V8):
ProtoGetter 13.1 / 17.2 / 29.4 (76 -> 58 ns, V8 34 ns), ProtoSetter 13.4 /
16.0 / 27.4, ClassGetter 13.3 / 16.5 / 30.1, ClassSetter 13.0 / 15.9 /
26.8, InheritedGetter 13.5 / 16.7 / 30.2, OwnDefinedGetter 13.5 / 16.9 /
30.7, MethodCall 12.0 / 15.4 / 27.3, LiteralGetter 10.7 / 9.4 / 25.6 (a
dictionary-mode holder; 4928034b). CpuCall 14.9 / 17.6 / 28.1,
CpuProtoMethod 12.6 / 15.7 / 25.3.

The pass, one commit each (git log 6a480c2f..), with the step's effect in
the per-step sessions (bin builds, 3 interleaved runs; ab2: Richards,
DeltaBlue, RayTrace, EarleyBoyer, Gameboy, Mandreel, zlib geomean; ab3:
the micro geomean against the step before):
- Call entry fast path (`InterpreterInlineCalls.TryEnterFast`): a call
  with exact argc to a function with bytecode and feedback enters the frame
  with no calls (the interrupt check folded into one compare against
  `Isolate.RegisterStackInterruptLimit`, which `StackGuard` lowers to 0 when
  an interrupt is requested), scalar slots stored before references; the
  call handlers split into a fast front and a `*Slow` handler. +4.6%.
- GetNamedProperty fast handler: monomorphic, polymorphic and megamorphic
  (stub cache) hits, own and prototype fields, prototype constants, getters
  entered through TryEnterFast. +1.3% (cumulative +5.9%).
- GetKeyedProperty / SetKeyedProperty typed-array fast handlers with no
  calls (zlib, Mandreel). +2.0% (cumulative +7.9%).
- `ReturnInline`: the return to an inline caller without a call, the
  context restored last. +1.7% (cumulative +9.6%).
- `InterpreterState.ResumeFp` / `ResumeIp`: the loop resumes from refs
  set at entry and return instead of recomputing them. Micro +1.3%.
- SetNamedProperty fast handler: polymorphic fields, megamorphic store
  stub cache, setters entered inline. Setters +31% (13.3 -> 17.5 M/s).
- Bitwise operators on Smis inline (`TryAnySmi`). CpuIntArith +7.5%.
- Load and store feedback encoding: an own-field handler's index carried in
  the feedback pair's JSValue payload (`FeedbackNexus.EncodeHandler`,
  `StoreIC.EncodeFieldStore`), read without dereferencing a handler.
  Within noise (the octane geomean swings about 3% between steps).
- Megamorphic stub cache with `Map.StubCacheHash` precomputed. Within noise.
- StrictEquals switched on instance type. Within noise.
- GetNamedProperty and the call handlers return the value or a
  `FrameEntered` marker instead of writing the accumulator through
  InterpreterState. Micro -3% / -0.2% (the extra call layer when the fast
  handler declined; see the routing commit).
- Function.prototype.apply with an array or arguments object entered
  through TryEnterFast from CallProperty2 (`TryApplyFast`). Within noise.
- The loop routes to a fast handler only when it can apply (callee is an
  inline-able function, receiver is a JSObject / typed array) and calls the
  general handler directly otherwise; dictionary-mode holders (kNormal) read
  in the fast GetNamedProperty, their getters entered inline (4928034b).
  Unlocked d8sharp micro, ns per iteration before the pass / after:
  literal getter 104 / 85, prototype getter 68 / 54, Math.floor 56 / 55,
  string length 31 / 31.

Profiling method (scripts in dotnet/artifacts/scratch, not committed): perf
cpu-clock samples with DOTNET_PerfMapEnabled, bucketed by the basic blocks
of the method's JIT listing (DOTNET_JitDisasm from the same process),
which shows per handler block where the dispatch loop and the handlers
spend their time.

Tried and dropped, measured: IsSmiDouble as a bit-pattern test (slower);
a Construct fast front (no gain); the apply frame through TryEnterFast in
TryPushApplyFrame (no gain); vectorized register-file fill (no gain);
ReturnInline inlined into the loop (mixed micros, more spills in the loop).

What is left (profiles of the final build): dispatch is 15-27% of the loop's
samples (RyuJIT's switch shape); write barriers 7-12% on Richards,
DeltaBlue, RayTrace and EarleyBoyer; a call and return are still about
1.7-1.9x V8 --jitless (getters ~58 ns against 34 ns); allocation (`new`
~2.5x). Not reached: 70% of V8 --jitless (54.9% measured).

Conformance at the end of the pass (4928034b): V8Sharp.Tests 1061/1061;
mjsunit 7602 run, 0 newly failing, 60 newly passing (Maglev expectations,
not this pass); test262 95123 run, 0 newly failing, 0 newly passing;
bytecode goldens 100/100 files, 557/557 snippets (bytecode generation untouched by the pass). regress/regress-1236560
ran 73 s at the base and 77 s at 3a5e180f alone (exception unwinding
through ExInfo); it can time out at 60 s under load in both.
Eighth interpreter performance pass: dispatch of prefixed and paired
bytecodes, the loop's compile-time state (2026-10-03; "before" is b1fa1fae,
main with no engine change; all V8Sharp numbers are `v8sharp:jitless`, the
interpreter alone, with V8's --jitless regexp interpreter; the baseline tier
is still off by default on this branch, its default-on change is on the
unmerged tier-performance branch).

Parity publishes (ReadyToRun composite, self-contained), `bench-session.sh`,
octane-steady in two sessions (session A: Richards .. NavierStokes plus
micro:accessors and micro:cpu; session B: PdfJS .. TypeScript), 3
interleaved runs each with V8 --jitless in the same session. Fingerprints
(before / after): A load 2.76 / 1.26, steal 0%, idle 97 / 99%, cpu-cal
1700 / 1828 ms, mem-bw 33.1 / 32.2 GB/s; B load 0.83 / 1.13, steal 0%, idle
93 / 98%, cpu-cal 1814 / 1740 ms, mem-bw 34.3 / 32.9 GB/s (Intel Xeon @
2.80GHz, 4 CPUs). V8 exited 139 on one run of Richards, DeltaBlue,
NavierStokes and Mandreel and two of CodeLoad and TypeScript; its means
there are over the runs that finished.

| benchmark | before | after | V8 --jitless | before / after of jitless | after/before |
|---|---|---|---|---|---|
| Richards | 434.3 | 407.9 | 758.6 | 57% / 54% | -6.1% |
| DeltaBlue | 356.5 | 387.5 | 872.5 | 41% / 44% | +8.7% |
| Crypto | 55.5 | 55.3 | 98.4 | 56% / 56% | -0.4% |
| RayTrace | 183.4 | 202.2 | 464.8 | 39% / 44% | +10.3% |
| EarleyBoyer | 79.0 | 82.3 | 185.5 | 43% / 44% | +4.2% |
| RegExp | 200.9 | 208.9 | 499.2 | 40% / 42% | +4.0% |
| Splay | 3636 | 3664 | 3798 | 96% / 96% | +0.8% |
| NavierStokes | 239.0 | 237.8 | 219.3 | 109% / 108% | -0.5% |
| PdfJS | 745.8 | 787.0 | 1942.7 | 38% / 41% | +5.5% |
| Mandreel | 69.5 | 80.3 | 141.2 | 49% / 57% | +15.5% |
| Gameboy | 323.6 | 346.1 | 714.5 | 45% / 48% | +7.0% |
| CodeLoad | 3198 | 3343 | 4153 | 77% / 80% | +4.5% |
| Box2D | 574.7 | 639.9 | 897.0 | 64% / 71% | +11.3% |
| zlib | 15.82 | 18.06 | 39.88 | 40% / 45% | +14.1% |
| Typescript | 250.4 | 288.3 | 629.8 | 40% / 46% | +15.1% |
| **geomean (15, no latency scores)** | 268.1 | 284.4 | 511.4 | **52.4% / 55.6%** | +6.1% |
| micro:cpu geomean (18) | 22.9 | 24.1 | 40.3 | 56.9% / 59.7% | +4.8% |
| micro:accessors geomean (8) | 15.4 | 15.3 | 28.1 | 54.8% / 54.5% | -0.5% |

Richards is 6% slower in all three runs of the parity build (the bin
sessions put it at -4% for the Star lookahead step, within noise); a
profile of both parity builds shows no handler slower and the dispatch
jump down from 18.6% to 11.7% of the loop's samples (the lookahead block
6.1%), i.e. layout of the loop, not work. micro:cpu after/before:
EmptyLoop +13%, AddLoop +18%, PropLoad +12%, NullCheck +10%, DoubleArray
+12%; accessors flat.

The pass, one commit each (git log b1fa1fae..). Per-step A/B sessions on
bin builds, `octane-quick` (fixed small iteration counts) and micro:cpu,
3 interleaved runs with V8 --jitless in the same session; a step under
~5% on one benchmark is noise on this host:
- Star lookahead (V8's StarDispatchLookahead after the
  Bytecodes::IsStarLookahead bytecodes, and after an inline return), and
  one case for the 16 short Stars. Richards -4%, DeltaBlue +4%, EarleyBoyer
  +2%, micro:cpu +0.5%: neutral. Kept as V8's design (it removes one
  dispatch per X+StarN pair, 20-27% of the bytecodes of the OO benchmarks).
- Wide/ExtraWide register moves (Ldar, Star, Mov), current context loads,
  the shift Smi operators, Wide GetKeyedProperty, SetKeyedProperty,
  JumpLoop, and JumpConstant / JumpIf{True,False}Constant decoded by the
  single-scale loop: Emscripten functions have hundreds of registers and
  long bodies, and zlib ran 5.6% of its bytecodes through RunPrefixed (one
  step of Loop<DoubleScale>) and its constant-pool jumps through LoopCold.
- The typed-array keyed load calls nothing (GetKeyedTypedArray returns
  NotHandled and the loop calls the general handler): it pushed six
  registers for the call it rarely made.
  zlib +6.6% for these three steps (base 16.98, after 18.11, 2 runs).
- TestInstanceOf: OrdinaryHasInstance's prototype walk inline when the
  feedback needs no update (`ObjectOps.FastInstanceOf` returns 1/0/-1, no
  out parameter; Map.IsSpecialReceiverMap inlines). EarleyBoyer's
  instanceof was 5.7% of its samples over four call layers, 4.1% after.
- Wide GetNamedProperty in the loop (TypeScript runs 1.4% of its bytecodes
  as Wide GetNamedProperty, Box2D 0.5%), ToBoolean of objects and strings
  without a second call.
- `Isolate` runs InterpreterInlineCalls' class constructor: the loop is
  compiled once on its first call, and with the class not yet initialized
  RyuJIT put 17 class-initialization checks into it, one after every call
  handler's FrameEntered compare. micro:cpu +3% for the last three steps
  (38.6 -> 40.6 geomean with the steps before).
- The long Star runs a following Ldar (a deviation: V8's lookahead is for
  the short Stars only). The pair is 11% of zlib's bytecodes: zlib +5.5%
  (16.82 -> 17.73, 3 runs), the rest within noise.
- Wide AddSmi/SubSmi/MulSmi/DivSmi/ModSmi (Gameboy runs 0.9% of its
  bytecodes as Wide ModSmi), Wide SetNamedProperty and
  JumpIfToBoolean{True,False}Constant (TypeScript) in the loop.
  Session for all the steps against the base: zlib +8.7%, TypeScript
  +11.5%, Gameboy +6.4%, Mandreel +16.9%, Box2D +8.8% (octane-quick, 3
  runs).

Tried and dropped, measured:
- A branch lookahead after the tests (TestEqual, TestLessThan,
  TestUndetectable, LogicalNot ... followed by JumpIfTrue/JumpIfFalse take
  the branch without its dispatch): micro:cpu +2.7% (loop conditions), but
  Octane mixed (Richards +3%, Crypto -3%, DeltaBlue, RayTrace, EarleyBoyer
  within 1%), so not worth a deviation from V8.
- Write barriers: the barrier for a store of `NumberTag.Instance` cannot be
  skipped safely. RyuJIT omits the barrier only for objects on the frozen
  (non-GC) heap, and a `static readonly` instance of a class is not
  allocated there (checked: the JIT loads the static and calls
  CORINFO_HELP_CHECKED_ASSIGN_REF); a store without a barrier through
  `Unsafe.As` would be a GC hole while the object can move (gen 0) and
  under background GC. Attribution of the barrier samples (perf with 8
  bytes of user stack: the return address at [rsp] of the frameless
  helper) on Richards: 9.3% of all samples are barriers, 48% of them in the
  short Star stores of objects (the register file is on the pinned heap,
  the stored objects young) and 50% in the call handlers' frame entry (the
  context, closure, bytecode array and feedback vector slots when a call
  at the same depth enters a different function). Both are reference
  stores V8 makes without a barrier (its frames are on the machine stack);
  the compare-and-skip of the earlier passes is all that applies.
- The call path, Construct and TryApplyFast (RayTrace's
  `this.initialize.apply(this, arguments)`) were profiled again: no single
  hot spot (Construct runs with a 232-byte frame and spills; a fast front
  was tried in the seventh pass).

What is left (profiles of the final build): the dispatch jump is still
10-15% of the loop's samples on the OO benchmarks; write barriers 6-10% on
Richards, DeltaBlue, RayTrace, EarleyBoyer; calls and returns; `new`
(Construct 5-7% of EarleyBoyer and RayTrace); GC (PdfJS ~5%). PdfJS's
profile is flat (the loop is 17%, nothing else above 1.5%). Not reached:
70% of V8 --jitless (55.6% measured).

Conformance at the end of the pass (98e54502): V8Sharp.Tests 1061/1061
(Baseline and Maglev tests included); mjsunit 7602 run, 0 newly failing, 0
newly passing; test262 95123 run, 0 newly failing, 0 newly passing (both
under the shared lock, 2 jobs each, run side by side); bytecode goldens
pass (bytecode generation untouched by the pass).
Front end and bytecode compilation pass (2026-10-02; parity publish,
thread CPU, under the benchmark lock, mean of 3 interleaved runs).
`micro:compile` (tools/V8Sharp.Bench/micro/compile.js: Octane's sources
compiled through indirect eval with a salt, compiles per CPU second; Run*
evaluates Closure / jQuery as CodeLoad does) and octane-cpu, V8Sharp before
(the branch at 2a352966) and after this pass, against V8 --jitless:

| benchmark | before | after | V8 --jitless | before / after of jitless |
|---|---|---|---|---|
| CompileTypeScript | 24.4 | 40.2 | 73.6 | 33% / 55% |
| CompilePdfJS | 14.5 | 16.0 | 42.0 | 35% / 38% |
| CompileClosure | 2222 | 3368 | 5659 | 39% / 60% |
| CompileJQuery | 153 | 221 | 376 | 41% / 59% |
| RunClosure | 1673 | 2506 | 3901 | 43% / 64% |
| RunJQuery | 63.3 | 100.4 | 92.2 | 69% / 109% |
| CodeLoad (octane-cpu) | 1504 | 2110 | 4421 | 34% / 48% |
| Typescript (octane-cpu) | 218 | 225 | 679 | 32% / 33% |
| PdfJS (octane-cpu) | 541 | 515 | 1840 | 29% / 28% (noise) |
| Box2D (octane-cpu) | 492 | 499 | 951 | 52% / 52% |

Octane TypeScript, PdfJS and Box2D hardly move: their time is in running
the code, not compiling it (TypeScript compiles its input in JS).

Changes, one commit each (git log 858a5ebc..): ParserBase<Parser> and
ParserBase<PreParser> generated as non-generic classes from the generic
template at build time (ParserBase.Specialize.targets; -11% of a
typescript-compiler.js compile, harness, bin); expression scopes, Targets,
FunctionStates and preparser expression lists recycled through free lists
(-10%, 24.9 to 16.6 MB allocated per typescript-compiler.js compile);
VariableMap linear for small maps (-3%, to 13.9 MB); the scanner reads the
source string in place (no large-object copy per compile, to 11.4 MB);
an open-addressing AstValueFactory string table probing the isolate's
constants instead of copying them; one AstStringConstants per isolate (it
was built per parse: 2.6% of a CodeLoad-like loop); AstRawStrings keep
their internalized string; preparse data kept on UncompiledData and
consumed by lazy compiles (inner functions are skipped, not preparsed
again: CodeLoad-like loop 21.4 to 18.5 ms per run); the runtime stack
check only when deeper; ExpressionScope casts without type checks; the
scanner's literal buffers pooled per thread (lazy compiles of small
functions: 56.5 to 41.0 MB per 2000); CompilationCacheEval (20000 evals of
one source: 3644 to 1080 ms; aged by full GCs; sources over 16K characters
are not cached, since keeping large scripts alive across gen-2 collections
cost 25-30% on large distinct evals).

Open, the next levers: the scanner and preparser
per token (40% of a TypeScript compile, a third of it the scanner),
RegisterInfo and the other per-function allocations of the
bytecode generator for lazy compiles.

Compilation cache, script part (2026-10-02): CompilationCacheScript ported
(Compiler::GetSharedFunctionInfoForScript, ScriptCacheKey: source, name,
offsets, origin options; scripts and modules), so compiling the same
script again reuses its Script and SharedFunctionInfos (d8 load() of one
file, Realm.eval of one source in several realms). The eval cache now
keys by source hash (no strong copy of the source), demotes unused
entries to weak references at full collections, and caches a source over
16K when it is compiled a second time (marked as seen on the first). Tests:
tests/V8Sharp.Tests/Codegen/CompilationCacheUnitTest.cs. Finding: Octane
CodeLoad does not hit the compilation cache in V8 either: every run
evaluates a source salted with a new value (the steady harness does not
reset the salt between its warm-up and measured halves), and V8 --jitless
scores the same with --no-compilation-cache (2300 vs 2538 steady, 15373 vs
16717 wall, one unlocked run each). The CodeLoad gap is compile speed of
fresh sources (indirect eval of the salted Closure source: V8 --jitless
about 0.4 ms, V8Sharp about 3.2-4 ms per eval).

CodeLoad pass (2026-10-03; parity publish, one bench-session under the
lock, 3 interleaved runs; "base" is fc298e30, "final" 0cfb0a18; V8
--jitless crashed once on steady CodeLoad and once on wall TypeScript, its
mean is over the other runs):

| benchmark | base | final | V8 --jitless | final / jitless |
|---|---|---|---|---|
| CodeLoad (octane-steady, fixed harness) | 3300 | 3514 | 4250 | 83% |
| CodeLoad (octane, wall) | 9677 | 10054 | 17693 | 57% |
| micro:codeload CodeLoadClosure | 1115 | 1225 | 2561 | 48% |
| micro:codeload CodeLoadJQuery | 80.8 | 84.5 | 81.1 | 104% |
| micro:codeload CacheBustJQuery | 3211 | 3074 | 50714 | 6% |
| Typescript (steady / wall) | 254 / 6090 | 254 / 6150 | 585 / 14685 | 43% / 42% |
| EarleyBoyer (steady / wall) | 76.4 / 2269 | 76.5 / 2213 | 188 / 5687 | 41% / 39% |

Finding: octane-steady keyed its warm start by benchmark name while Octane
reports a suite by the suite's name; CodeLoad (CodeLoadClosure /
CodeLoadJQuery), Crypto (Encrypt / Decrypt) and EarleyBoyer (Earley /
Boyer) never found the start and measured the whole cold pass, .NET tier-up
included (CodeLoad about 1200 instead of 3400). The "CodeLoad at 20% of V8
--jitless warm" figure was mostly that. Fixed in V8Sharp.Bench (the suite's
score is over its benchmarks' measured runs); steady numbers for those three
suites before 2026-10-03 are cold numbers. Warm, jQuery's salted eval now
runs at V8 --jitless's speed; Closure's is at half.

Changes, one commit each (git log fc298e30..): CompilationCacheEval key
computed once per eval from the string's cached hash (a source over
kMaxHashCalcLength hashes its ends; it was hashed twice in full); inferred
names (`a.b.c = function`) not internalized, as V8 (6% of a Closure eval
was the StringTable lookup and flattening of them); the StringTable an open
addressing table keyed by V8's hash field, the engine's StringHasher on
V8Sharp.Base's rapidhash port and AstRawStrings carrying the hash field, so
internalizing does not hash again (each new string was hashed three times:
FNV in the factory, the Dictionary's, Jenkins for the hash field); the
table's slots keep the hash beside the string (probes no longer load other
strings: StringTable lookups were 1% of CodeLoad in cache misses);
VariableProxy 64 to 48 bytes (union of name and variable, flags in a byte;
20% of the bytes a preparse allocates); Scope::Snapshot a struct;
VariableMap's index open addressing by the AstRawString hash instead of a
Dictionary; AccessorTable allocated with its first accessor; the feedback
slot cache and the constant pool map hashing names by their string hash
instead of the runtime identity hash. micro:codeload, interleaved, unlocked:
Closure +6-10%, jQuery +4-6%; allocation of a steady CodeLoad 595 to 560 MB.

Tried and dropped: DOTNET_GCgen0size 8, 32, 64 and 256 MB and
GCLOHThreshold 1 MB (the replace result then lives in gen0: CacheBust 4x,
the evals unchanged).

What is left (profile of a CodeLoad loop, bin build, share of the main
thread): preparsing 24%, the full parser 16%, bytecode generation 14%,
running the evaluated code 21% (IC misses: a LoadIC/StoreIC/LoadGlobalIC
object per miss, 4.5% of the bytes of a Closure eval), GC 12-24% (every
iteration keeps its Closure library alive through a new `goog<hash>`
global, as in V8, so the heap grows and gen-1/2 collections are not cheap).
All flat: the scanner is 5-6%, no method above 2.5%. The rest of the
allocation per compile is per node (Variable, DeclarationScope and its
VariableMap, ThreadedLists and params list: five objects per scope against
V8's one zone allocation; RegisterInfo per register; ParsePropertyInfo per
property). CacheBust (`str.replace(new RegExp("jQuery", "g"), s)` over 190K
characters) is 16x V8's: the global replace of an atom builds the result
through IncrementalStringBuilder and flattens the cons source, both
large-object allocations that trigger gen-2 collections. Octane's wall
CodeLoad (57%) still pays .NET tier-up in its short timed runs.

Runtime slow paths outside the dispatch loop on zlib, Mandreel, Gameboy,
PdfJS and Box2D (2026-10-02, after the fourth pass; thread CPU,
`octane-cpu`, V8SHARP_BENCH_SCALE=50, under the benchmark lock, 2-3
interleaved runs per A/B; these benchmarks vary 3-5% run to run):
- typed array element loads and stores read the backing array, offset
  and length cached in the JSTypedArray (V8's data pointer) on the
  monomorphic handler path: zlib +3.4%, Mandreel +3%, Gameboy +8%;
- Wide Star/Ldar/Mov/GetKeyedProperty/SetKeyedProperty and the plain
  JumpLoop run from RunPrefixed instead of a single step of
  Loop<DoubleScale> (zlib executes ~185M Wide bytecodes per run in its
  huge functions; the scaled loop's prologue cost more than the
  bytecode): zlib +2.4%;
- field and element stores skip the reference half (and its write
  barrier) when it is unchanged, as register stores do: Box2D +5%,
  DeltaBlue +2%, Gameboy +2%.
Together, against the branch at b0e12315: zlib +2%, Mandreel +9%, Gameboy
+7%, Typescript +1.5%, PdfJS and Box2D within noise. Still 33% (zlib), 38%
(Mandreel), 33% (Gameboy), 19% (PdfJS), 43% (Box2D) of V8 --jitless.
Measured and left: inlining the typed array element hit into the loop's
GetKeyedProperty case (within noise); slow IC entries are rare everywhere
(Mandreel under 4K keyed per run). What remains outside the loop, by
count per run at SCALE=5: PdfJS 430K polymorphic typed array loads and
410K ConsString[i] loads through KeyedLoadIC.LoadSlow (hits, not misses),
105K dictionary-mode named loads; Box2D 264K setter and 113K getter calls
(JS accessors on prototypes: a re-entry through Execution.Call, 120 ns
against V8 --jitless's 28 ns per access in a micro-benchmark; small in
Box2D, about 1% of its time, but it is the frame protocol's to fix: V8
calls accessors like any other call, without a new dispatch loop).
On the short benchmarks the .NET tiering dominates the main thread in a
`dotnet build` (TieredPGO's count probes alone are 8.7% of CodeLoad); see
the publish configuration above.
Conformance with these commits merged at b0e12315: test262 95123 run,
0 newly failing; mjsunit 53 newly failing, 8 newly passing, the same 53
fail at b0e12315 without them (maglev/, turboshaft/ and for-of/
destructuring deopt tests: the expectations predate the Maglev merge).

Runtime paths pass: accessors, builtins, strings, regexp glue, IC slow
paths (2026-10-02/03; parity publish, bench-session.sh, V8 --jitless in the
same session). Counted per run first (temporary counters on builtin calls,
runtime calls, IC misses/slow entries, stub cache, flattening): the
runtime paths of the furthest-behind benchmarks are a small share of their
time; the bulk is the dispatch loop, calls and allocation. The biggest
counts per SCALE-10 run: pdf.js 1.5M String.fromCharCode through the
builtin frame, 345K polymorphic typed array and 330K ConsString index loads
through KeyedLoadIC.LoadSlow, 148K indexOf; TypeScript 2.2M megamorphic
named stores through StoreNamedSlow, 186K keyed loads with non-internalized
string keys missing the stub cache; RegExp 2.8M exec and 1.2M replace
through the builtin frame; DeltaBlue 730K `new Array()`; zlib 280K
Math.imul; EarleyBoyer hardly any runtime calls (all loop, calls, allocation).

Changes, one commit each (git log 613b6894..): fast paths for
String.fromCharCode, indexOf (with and without a position),
substring/slice/substr, Math.imul; polymorphic typed array and String index
hits in KeyedLoadIC.LoadSlow without the miss; ConsString flattening by
WriteToFlat (no Stack per flatten); JavaScript getters and setters entered
in the dispatch loop through EnterInline (own, prototype and
dictionary-mode holders, the last through LoadNormal handlers as in V8,
which V8Sharp sent to the slow stub); the megamorphic stub cache on the
SetNamedProperty path; keyed loads probing with the internalized copy of a
run-time string key; kNonExistent hits inline; JSObject/String receiver
tests by instance type in the IC entries (CastHelpers was 1.4-1.7% of
TypeScript and pdf.js); `new Array()`/`new Array(n)` and the regexp
builtins (exec, test, match, replace, split) without the builtin frame.

micro/accessors.js (M accesses per CPU s; base / after / V8 --jitless):
prototype getter 7.2 / 19.6 / 34.8, prototype setter 8.3 / 17.2 / 33.2,
class getter 7.4 / 17.9 / 35.3, class setter 7.5 / 15.4 / 38.0, inherited
getter 8.7 / 17.7 / 36.2, object literal getter 5.0 / 15.9 / 33.6, own
defined getter 7.6 / 19.2 / 41.7; an accessor now costs what a method call
costs (AccMethodCall 15.5 / 35.1), so the rest of the gap is the call
protocol. micro/runtime.js geomean 9.9 -> 11.2 (V8 --jitless 28.1):
fromCharCode +19%, ConsIndex +45%, IndexOf +13%, ConsBuild +14%.

Octane (octane-steady, TypeScript octane-cpu, 3 interleaved runs, one
session; the host was slow in this session, cpu-cal 1.8 s against 0.6 s
earlier, so absolute scores are low, the ratios hold): pdf.js 623 -> 637
(+2.2%), EarleyBoyer +1.7%, RegExp 373 -> 398 (+6.8%), zlib +2.6%,
DeltaBlue +0.6%, Crypto -4.0% (noise: its paths did not change),
TypeScript +1.5%; geomean of the seven 165.6 -> 168.2 (V8 --jitless 402.1,
41.2% -> 41.8%). Gameboy and Box2D fail in octane-steady on every engine
(the second pass of the harness), so they were not measured there.
Tried and dropped: polymorphic typed array and String index cases in
KeyedLoadIC.Load (inlined into the interpreter's handler) made it too large
to inline: zlib -9%; a dedicated dispatch-loop fast path for prototype
accessors (handler fields with the accessor): within noise of the
EnterInline path, the call itself dominates.
Open: Map/Set get/set (3x), splice (3.2x), StringAdd with numbers (3x),
substring allocation (3.5x), regexp exec result construction (~2x).

Performance with the baseline tier (Octane, 2026-09-28, 4-core container
shared with other jobs, mean of 2 runs; V8Sharp.Bench):

| benchmark | v8sharp:jitless | v8sharp (tiering) | v8sharp:always-sparkplug | V8 --jitless | V8 sparkplug |
|---|---|---|---|---|---|
| Richards | 158 | 425 | 415 | 1248 | 1695 |
| DeltaBlue | 174 | 382 | 381 | 1314 | 1694 |
| Crypto | 173 | 384 | 407 | 1074 | 1444 |
| RayTrace | 474 | 689 | 669 | 2792 | 3609 |
| EarleyBoyer | 709 | 1049 | 831 | 4541 | 7075 |
| RegExp | 552 | 1122 | 1134 | 2072 | 3150 |
| Splay | 753 | 1152 | 1515 | 2529 | 1910 |
| NavierStokes | 594 | 1000 | 806 | 1287 | 1646 |
| geomean | 442 | 798 | 781 | 1953 | 2353 |

Baseline is 1.8x the interpreter (geomean; 2.2-2.7x on Richards, DeltaBlue,
Crypto, RegExp). RayTrace, Splay and EarleyBoyer are dominated by allocation
and GC (object = JSObject + JSValue[] fields) and by runtime paths
(instanceof's @@hasInstance lookup), which the tier does not change.

Performance with the baseline tier after the baseline performance pass
(2026-10-02; the interpreter had meanwhile had three performance passes, so
the 2026-09-28 ratios above no longer hold). Changes: the interpreter's fast
paths emitted as IL and chosen from the feedback, registers in IL locals,
lean baseline-to-baseline calls (also through call/apply), compact code for
huge functions, concurrent compilation fully optimized by RyuJIT off the main
thread. 4-core container; every benchmark ran under the machine-wide
exclusive lock (/home/user/locks/bench.lock), so no other benchmark ran at
the same time (builds and unit tests of other agents could).

Thread CPU of the benchmark thread (`octane-cpu`, V8SHARP_BENCH_SCALE=50,
fixed work, median of 3 interleaved runs, zlib 2; background compilation is
not counted here, the `.process` figures in artifacts count it):

| benchmark | interpreter | sparkplug (tiering) | always-sparkplug | sparkplug / interpreter |
|---|---|---|---|---|
| Richards | 542 | 766 | 693 | 1.41 |
| DeltaBlue | 408 | 524 | 497 | 1.28 |
| Crypto | 68.1 | 89.9 | 99.2 | 1.32 |
| RayTrace | 264 | 340 | 292 | 1.29 |
| EarleyBoyer | 122 | 157 | 140 | 1.29 |
| RegExp | 246 | 282 | 247 | 1.15 |
| Splay | 1145 | 1132 | 1120 | 0.99 |
| NavierStokes | 288 | 482 | 422 | 1.67 |
| PdfJS | 237 | 215 | 176 | 0.91 |
| Mandreel | 27.6 | 28.1 | 26.3 | 1.02 |
| Gameboy | 139 | 137 | 104 | 0.98 |
| CodeLoad | 530 | 472 | 172 | 0.89 |
| Box2D | 413 | 338 | 227 | 0.82 |
| zlib | 10.5 | 11.2 | 10.8 | 1.07 |
| geomean | 190 | 214 | 178 | 1.13 |

Octane's own wall-clock scores (V8Sharp.Bench `octane`, median of 2
interleaved runs; V8 is the oracle, V8 14.7 through ClearScript, mean of 2;
Typescript fails in V8Sharp with "duplicate descriptor" in every mode, and
zlib takes 5 minutes per run in V8Sharp, so it is only in the table above):

| benchmark | interpreter | sparkplug (tiering) | always-sparkplug | V8 --jitless | V8 sparkplug |
|---|---|---|---|---|---|
| Richards | 686 | 940 | 984 | 1375 | 1794 |
| DeltaBlue | 710 | 939 | 889 | 1344 | 1696 |
| Crypto | 586 | 463 | 684 | 1258 | 1852 |
| RayTrace | 1488 | 2094 | 1862 | 3340 | 4302 |
| EarleyBoyer | 2282 | 2756 | 2858 | 5761 | 8518 |
| RegExp | 1415 | 1418 | 1466 | 2438 | 4490 |
| Splay | 2728 | 2802 | 2707 | 2101 | 3015 |
| NavierStokes | 1460 | 2968 | 2351 | 1683 | 1956 |
| PdfJS | 1078 | 965 | 991 | 7867 | 10597 |
| Mandreel | 225 | 258 | 242 | 1144 | 1535 |
| Gameboy | 1666 | 1668 | 1166 | 7577 | 9792 |
| CodeLoad | 2206 | 2388 | 526 | 16362 | 17784 |
| Box2D | 1810 | 1730 | 1076 | 4146 | 4868 |

Sparkplug / interpreter: 1.14x geomean on wall-clock, 1.13x on thread CPU.
The tier is 1.3-2x the interpreter on the long-running benchmarks
(Richards, DeltaBlue, RayTrace, EarleyBoyer, NavierStokes) and slower on
the short ones that make many functions hot (PdfJS, Box2D, and CodeLoad by
thread CPU): their Octane runs last about two seconds, and compiling the
170-290 functions they make hot costs 2.5-3 s of RyuJIT time on the
background thread, while the code that runs meanwhile is still the
interpreter's. Crypto's wall-clock score is bimodal in every build tried
(sparkplug runs of 373-933; the thread-CPU figure is stable at 1.3x). V8's
own Sparkplug is 1.1-1.9x its interpreter on the same runs. Sparkplug
stays off by default (deviations.md).

The interpreter micro-benchmarks (`micro:all`, calls per second, 2 runs,
under the lock): the tier is 1.75x the interpreter (geomean 476 vs 272;
1.6-4.5x on the arithmetic and empty loops, 1.65-2.6x on array indexing and
property access, 1.2-1.9x on calls and closures, 1.05-1.1x on object and
class construction, 1.0x on ArrayIndexOf, which is builtin-bound).

- [x] Temporal (`--harmony-temporal`, shipped and on by default in this
      revision). The binding layer is ported from
      `src/objects/js-temporal-objects.{h,cc,tq}` and
      `src/builtins/builtins-temporal.cc` (`Objects/JSTemporalObjects.cs`,
      `Builtins/Builtins.Temporal*.cs`: argument processing, option reading
      order, MessageTemplate errors, CHECK_RECEIVER names), the Genesis install
      and the lazy `Temporal` / `Date.prototype.toTemporalInstant` accessors
      from `bootstrapper.cc` (`Init/Genesis.Temporal.cs`, including
      InitializeLazyPartOfContext for GetDerivedMap), and
      Date.prototype.toTemporalInstant from builtins-date.cc. The engine V8
      calls (the Rust crate temporal_rs, not in this checkout) is implemented
      in C# from the Temporal specification (`Temporal/`: ISO date/time
      records and arithmetic, exact Int128/BigInteger durations, rounding
      modes, the relative rounding machinery, the ISO 8601 / RFC 9557
      grammar, formatting, offset and named time zones). test262
      (v8sharp, 2026-09-28): built-ins/Temporal 9210/9210 runs,
      staging/Temporal 4/4, built-ins/Date/prototype/toTemporalInstant 16/16,
      staging/sm/Date/to-temporal-instant 2/2; mjsunit's temporal tests
      (regress-temporal-zoneinfo, regress-46*, regress-49*, harmony/builtins-harmony-*)
      pass. Missing / deviating (deviations.md "Temporal"): calendars other
      than iso8601 (V8 has them from ICU4X), time zone data from .NET's
      TimeZoneInfo instead of zoneinfo64, engine error message texts,
      toLocaleString without Intl (as V8 without V8_INTL_SUPPORT), the
      embedder's temporal_get_epoch_nanoseconds_callback.

### Engine-side conformance: what is left (2026-09-28)

Fixed by the engine conformance pass: `accessor` class elements and the
%-call arity leniency under the fuzzing flags (runtime.cc's allowlist);
the derived-constructor TypeError realm; `var`/function declarations on a
non-extensible global (script and eval); --disallow-code-generation-from-strings
for eval and Function; stack overflow through builtins and JSON.stringify
(register-stack reservation, see deviations.md); allocation-site feedback for
`Array(n)`, map and filter; console; ValueSerializer, Worker, d8.serializer,
$262.agent and per-realm microtask queues in the TestRunner host;
FutexEmulation::IsolateDeinit; cross-origin [[Get]] of well-known symbols;
the store IC's lookup on dictionary receivers (--no-lazy-feedback-allocation
cases); a handful of test natives. Fixed by the modules pass: import defer,
source phase and bytes imports, dynamic import with phases, ShadowRealm
importValue, d8's --bundle and --compile-only, the CloneObjectIC fast path,
no kMaxArguments cap on spread calls, %GetPrivateMember/%SetPrivateMember by
description (runtime-object.cc).

Still failing (mjsunit clusters, v8sharp engine):
- opt-proto-seq/* (57): %CompileBaseline needs Sparkplug, which is off by
  default for now (they pass with --sparkplug).
- Optimization-status asserts in maglev/, turbolev/, compiler/, baseline/
  (about 60): no optimizing tier.
- ArrayBuffers of 2^31 bytes or more (25, deviation).
- FastCAPI, d8.dom (ic-megadom*), the inspector `send`, async_hooks,
  code coverage (%DebugToggleBlockCoverage/%DebugCollectCoverage),
  %RuntimeEvaluateREPL, os, writeFile, getV8Statistics, d8.test.* interceptors,
  d8.getExtrasBindingObject (continuation-preserved embedder data), the
  d8 `-C` working directory, Intl, WebAssembly, shared structs.
- `%IsSmi(%AllocateHeapNumberWithValue(1))` (call-intrinsic-fuzzing, deviation).

### Headline, 2026-10-03 (a885b53c)

octane-steady after the harness fix (79e3f6e6: CodeLoad, Crypto and
EarleyBoyer were scored over the cold pass before it, so earlier steady
numbers for them are cold), parity publish, 3 interleaved runs with V8
--jitless in the same session (session-20261003-051949; load 4.1 -> 1.3,
steal 0%, cpu-cal 1.77-1.81 s, mem-bw 32.4 GB/s; V8 crashed with exit 139 on
one run of 7 benchmarks, its means there are over 2 runs):
**52.0%** of V8 --jitless (geomean of 15 scores, latency excluded).

| benchmark | v8sharp | V8 --jitless | share |
|---|---|---|---|
| Richards | 406 | 792 | 51% |
| DeltaBlue | 355 | 854 | 42% |
| Crypto | 53.2 | 104 | 51% |
| RayTrace | 193 | 463 | 42% |
| EarleyBoyer | 78.0 | 193 | 40% |
| RegExp | 278 | 506 | 55% |
| Splay | 3481 | 4021 | 87% |
| NavierStokes | 258 | 223 | 116% |
| PdfJS | 744 | 2004 | 37% |
| Mandreel | 66.7 | 145 | 46% |
| Gameboy | 316 | 750 | 42% |
| CodeLoad | 3448 | 4083 | 84% |
| Box2D | 565 | 899 | 63% |
| zlib | 15.0 | 41.8 | 36% |
| Typescript | 246 | 628 | 39% |

After the seventh interpreter performance pass (3a5e180f, parity publish,
session-20261003-102501, same harness): **54.9%** of V8 --jitless
(274.9 / 501.0; the base of that pass, 6a480c2f, measured 52.7% in the
same session). See "Seventh interpreter performance pass" above.

After the eighth interpreter performance pass (98e54502, parity publish,
two sessions, 3 runs): **55.6%** of V8 --jitless (284.4 / 511.4; its base,
b1fa1fae, measured 52.4% in the same sessions). These and later numbers
are `v8sharp:jitless` (the interpreter alone, as V8 --jitless; the baseline
tier will be on by default once 2b647a54 is merged), and V8Sharp's --jitless also runs regular expressions in the bytecode
interpreter, as V8's does (RegExp 201 here against 278 with compiled
regular expressions in the seventh pass's sessions). See "Eighth
interpreter performance pass" above.

## Phase 2: the fast tiers

Order (decided 2026-09-28): the interpreter is finished first — correctness
(test262/mjsunit) and interpreter performance (target: within 2x of V8
--jitless) — before any further work on the IL tiers. The baseline tier is
on by default since 2026-10-03; the optimizing tier (Maglev) is in
progress, off by default.

- [x] TieringManager: interrupt budget, OnInterruptTick, feedback allocation
      and the Sparkplug tier-up, InterruptBudgetFor with V8's flag defaults,
      NotifyICChanged; no optimizing tier, so use_optimizer() is false
      (Execution/TieringManager.cs). OSR urgency is ported but unused until
      the optimizing tier exists.
- [~] Baseline compiler: bytecode -> IL (Sparkplug analogue), src/V8Sharp/Baseline/
      (architecture.md 9.1): every bytecode compiles; entry at function start,
      exception handlers and loop headers (OSR from Ignition at JumpLoop);
      batch compilation, --sparkplug (on by default, as in V8), --always-sparkplug,
      --sparkplug-filter, %CompileBaseline, %ActiveTierIsSparkplug,
      %BaselineOsr, %GetOptimizationStatus baseline bits; the interpreter's
      fast paths emitted as IL (number arithmetic and comparisons fused with
      the conditional jumps, monomorphic named/keyed loads and stores, global
      loads, context slots), chosen per bytecode from the feedback at compile
      time; registers in IL locals; lean baseline-to-baseline calls
      (BaselineCalls, also through Function.prototype.call/apply); compact
      code for functions beyond RyuJIT's optimization limits; concurrent
      compilation on a background thread (--concurrent-sparkplug, on as in
      V8). Tests: tests/V8Sharp.Tests/Baseline (interpreter vs
      --always-sparkplug, and the same feedback in both tiers).
      Open: see "Baseline: open items" below.
      On by default (--sparkplug, as V8 on x64) since 2026-10-03: mjsunit and
      test262 with the tier on, 0 newly failing against the expectations;
      Octane in "Phase 2 measurements" below.
- Baseline: open items
  - Bytecode flushing and baseline code flushing (mjsunit/baseline/flush-*)
    are not implemented (no bytecode aging).
  - mjsunit/baseline/cross-realm: identical sources in two realms now share
    the SharedFunctionInfo (script compilation cache), but a closure whose
    SharedFunctionInfo already has baseline code does not get it on its
    lazy compile (isBaseline(f2) is false after f2(0), line 35).
  - d8.test.verifySourcePositions (verify-bytecode-offsets) is not in the
    test host.
  - Compile cost: RyuJIT takes about 11 ms of CPU per function in PdfJS
    (183 functions, 2.0 s on the Sparkplug thread; 25 IL bytes per bytecode
    byte), 85% of it in RyuJIT itself. Measured alternatives (2026-10-03):
    compact code for every function (V8SHARP_BASELINE_COMPACT=1) 1.5 s and a
    slower tier; registers in the frame (V8SHARP_BASELINE_NO_REGISTER_CACHE=1)
    1.9 s; RyuJIT tier 0 first (V8SHARP_BASELINE_TIERED=1) 0.6 s but a slower
    short run. On an idle 4-core host cold Octane runs are now within noise of
    the interpreter (the compile threads use idle cores); on a loaded host
    they are still slower.
  - Functions with more than 5000 bytes of bytecode stay in the interpreter
    (one IL method beyond RyuJIT's limits even in compact form): TypeScript's
    and zlib's biggest functions. Splitting a function into several IL
    methods would let them tier up.
  - In big methods RyuJIT stops inlining (inline budget, 512 locals): what
    the code relies on being inlined must be IL (the bytecode offset store,
    Smi constants are; FieldAt, Map.IsUndetectable, FromNumber are calls).
  - Performance: calls still pay the interpreter frame's setup (register
    file clear, frame record, write barriers: about 20% of DeltaBlue in
    BaselineCalls.Enter and the write barrier); a leaner frame protocol
    shared with the interpreter would help both tiers.
- [~] Optimizing compiler (Maglev analogue), src/V8Sharp/Maglev/ and
      Deoptimizer/ (architecture.md 9.2). OFF by default (`--maglev`):
      - Graph builder from bytecode + feedback: abstract frame, merge
        points and loop phis, liveness (BytecodeAnalysis), Int32/Float64
        speculation with overflow/-0 checks and kSignedSmall Smi
        assumptions, compares and fused branches, map checks with known
        map tracking, mono/polymorphic named loads/stores from IC handlers
        (fields, constants from prototypes, transitions, array/string
        length), fast element loads/stores (holes as undefined under the
        NoElements protector, growing stores), global property cells,
        context slots, known-target calls, small-function inlining with
        lazily pushed inlined frames, `new` of known constructors
        (FastNewObject + inlined constructor), Math.*, charCodeAt,
        f.apply(thisArg, arguments) forwarding with arguments-object
        elision, builtin fast paths for calls; try/catch/finally (catch
        blocks with exception phis); string/number element keys
        (CheckedObjectToIndex), out-of-bounds loads, polymorphic element
        loads/stores, elements kind transitions, holey/growing stores
        guarded by prototype maps, typed array loads/stores (number kinds),
        KeyedStoreIC_Megamorphic's typed array fast path; call
        speculation modes updated by deopts (out of bounds, disallow);
        generators and async functions (generator switch, GeneratorStore,
        GeneratorRestoreRegister, resumable loops; inlined generator
        initialization); inlining of calls inside try blocks; deprecated
        feedback maps (updated maps, CheckMapsWithMigrationAndDeopt,
        MigrateMapIfNeeded); typed array length (the accessor, with
        prototype-chain and detaching dependencies), CheckTypedArrayValid
        (detaching/immutable protectors); Array.prototype.push/pop;
        ReceiverOrNullOrUndefined compare feedback; string + string wrapper;
        the kMaxStackSlots bailout (most values live at once); the
        truncation pass for int32 add/sub/mul (MaglevTruncation);
        everything else through the baseline builtins (generic nodes).
      - Phi representation selector (untagged Int32/Float64 phis).
      - IL code generator: values in IL locals, deopt exits shared per frame
        state, lazy deopt checks after calls, OSR entry.
      - Deoptimizer: eager and lazy deopts, inlined frames, materialized
        arguments objects; OSR early exits; kMaxDeoptCount.
      - Dependencies: stable maps, property cells (incl. read-only on
        freeze), initial maps, protectors (DependentCode).
      - Tiering: invocation count/interrupt budget, OSR at JumpLoop budget
        interrupts; natives %OptimizeFunctionOnNextCall,
        %OptimizeMaglevOnNextCall, %OptimizeOsr, %PrepareFunctionForOptimization,
        %NeverOptimizeFunction, %DeoptimizeFunction, %DeoptimizeNow,
        %ActiveTierIsMaglev, %GetOptimizationStatus bits.
      Tests: tests/V8Sharp.Tests/Maglev (interpreter vs forced optimization).
- Maglev: open items
  - Not optimized (the compile bails out): debug bytecodes; functions with
    exception handlers are not inlined; BigInt/Float16 and resizable-buffer
    typed arrays, DataView, Map/Set/iterators, string builders, array
    destructuring and for-of reductions are generic.
  - Missing for mjsunit/maglev's optimization-status asserts (each listed
    in mjsunit.v8sharp.txt): range analysis (the truncation pass covers
    int32 add/sub/mul with static input ranges only); CSE
    (regress-536945254 needs CSE'd values to exceed kMaxStackSlots);
    function-context specialization of one-closure feedback cells (needs
    code on the closure, not only on the feedback vector:
    omit-default-ctors's FindNonDefaultConstructorOrConstruct reduction);
    ThinStrings (string-compare); undefined in double arrays
    (V8_ENABLE_UNDEFINED_DOUBLE: float64-conversions, turbolev
    holey-double-load-arith); script context slot type tracking
    (typed-array-length-store-script-context); Turbofan
    (osr-from-ml-to-tf, osr-to-tf, osr-multiple-loops, osr-only-interrupt,
    regress-2618's Turbofan OSR); block coverage (%DebugToggleBlockCoverage,
    code-coverage-block-opt); super property ICs (super-ic-opt's
    const-field dependency: GetNamedPropertyFromSuper records no feedback).
  - Performance: calls not inlined cost ~60 ns (frame record, register
    window, write barriers). Deopt exits are a third of the IL (0.55 of the
    body's in PdfJS after literals, shared spill code and DeoptN). Values
    share IL locals by live range, so RyuJIT optimizes and inlines big
    graphs; the tiering limits are 2000 nodes and 36000 bytes of IL. No
    escape analysis, LICM, loop peeling or CSE of loads.
  - Conformance under forced optimization (`--maglev
    --invocation-count-for-maglev=4 --optimize-on-next-call-optimizes-to-maglev`,
    2026-10-02, after concurrent compilation): test262 0 newly failing
    against the expectations (95123 run); mjsunit 82 failures vs 53 in the
    plain run, 29 of them only with optimization: 11 typed array length
    tests need `--mock-arraybuffer-allocator`; IC work (deprecated-map
    migration in the IC: checkmaps-with-migration-and-deopt-poly/poly2;
    LoadIC feedback for undefined receivers: misc-ensure-no-deopt,
    load-named-generic); optimization status asserts of Turbofan/turbolev
    behaviour (int32-mul-truncation, holey-double-load-arith,
    elide-double-hole-check-12, new-obj, super-ic-opt's const-field
    dependency, immutable-ab-regress); generators are not optimized
    (regress-2618, regress-6989, regress-794825, generator-loop-peel);
    code-coverage-block-opt, regress-v8-5697 and
    turboshaft/regress-380487911 (65536 locals).
  - Octane (V8Sharp.Bench `compare`, median of 2, under the bench lock,
    2026-10-02, shared 4-core host; V8 is the 14.7 oracle's d8):

    | benchmark | v8sharp | v8sharp:maglev | v8:jitless | v8:maglev | v8:jit |
    |---|---|---|---|---|---|
    | Richards | 734 | 2297 | 1319 | 28268 | 36436 |
    | DeltaBlue | 501 | 1135 | 1395 | 35538 | 74918 |
    | Crypto | 595 | 1318 | 1370 | 20801 | 35670 |
    | RayTrace | 1381 | 1691 | 3189 | 39996 | 63565 |
    | EarleyBoyer | 2011 | 2774 | 5428 | 29100 | 42352 |
    | RegExp | 1462 | 1429 | 2111 | 6056 | 7319 |
    | Splay | 2949 | 2557 | 2554 | 5660 | 6054 |
    | SplayLatency | 2352 | 2019 | 2818 | 3664 | 4341 |
    | NavierStokes | 1713 | 6735 | 1609 | 23292 | 44104 |
    | PdfJS | 1259 | 813 | 6983 | 27188 | 32534 |
    | Box2D | 1896 | 2111 | 3726 | 72217 | 80262 |
    | Gameboy | 2176 | 1988 | 7884 | 54497 | 51353 |
    | Mandreel | 471 | 1529 | 1141 | 28109 | 36491 |
    | MandreelLatency | 1564 | 2073 | 6424 | 39728 | 45450 |
    | CodeLoad | 2061 | 2077 | 17542 | 18632 | 17114 |
    | zlib | 770 | 765 | 2224 | 73558 | 79018 |
    | Typescript | 6006 | 5938 | 16472 | 53366 | 54936 |
    | geomean | 1419 | 1945 | 3389 | 25097 | 31903 |

    The tier is 1.37x the interpreter (2-4x on Richards, DeltaBlue, Crypto,
    NavierStokes, Mandreel) and 57% of V8 --jitless; it is above V8
    --jitless on Richards, NavierStokes and Mandreel. Losses: PdfJS (many
    functions get hot in a short run, RyuJIT compile time on the background
    thread), Splay and Gameboy (allocation- and call-heavy code that is
    still generic). The rest of the gap to V8 --jitless is the interpreter
    (V8Sharp's interpreter is 42% of V8's on this run).
  - The tier stays off by default until it is conformance-clean under
    forced optimization and a net win on Octane.
- Phase 2 measurements (2026-10-03; V8Sharp.Bench `compare`, octane-steady
  (thread CPU after a warm pass), 3 interleaved runs, parity publishes,
  bench-session.sh under the lock; host load 1.1-1.8, steal 0%, cpu-cal
  1855/1710 ms, mem-bw 30.8/31.1 GB/s before/after). "base" is 3f13618b
  (with the Bench scoring fix), "final" this branch (5c8f2e12 + the
  tier-up delay); sparkplug = `--sparkplug --no-maglev`, maglev =
  `--sparkplug --maglev`:

  | benchmark | base jitless | base sparkplug | base maglev | final jitless | final sparkplug | final maglev | v8:sparkplug | v8:maglev |
  |---|---|---|---|---|---|---|---|---|
  | Richards | 376 | 455 | 1083 | 388 | 468 | 942 | 860 | 10129 |
  | DeltaBlue | 326 | 396 | 1105 | 330 | 396 | 1096 | 943 | 10104 |
  | Crypto | 53.6 | 53.8 | 114 | 51.9 | 56.3 | 111 | 130 | 1719 |
  | RayTrace | 188 | 234 | 183 | 188 | 236 | 270 | 545 | 4749 |
  | EarleyBoyer | 76.1 | 90.0 | 114 | 75.9 | 88.5 | 135 | 246 | 1200 |
  | RegExp | 199 | 264 | 272 | 206 | 273 | 284 | 868 | 1422 |
  | Splay | 3073 | 3606 | 3737 | 3066 | 3461 | 4473 | 4733 | 16413 |
  | NavierStokes | 251 | 323 | 1086 | 242 | 335 | 1056 | 257 | 2763 |
  | PdfJS | 688 | 812 | 908 | 695 | 846 | 979 | 2471 | 7983 |
  | Mandreel | 65.6 | 57.2 | 266 | 66.4 | 69.5 | 438 | 199 | 4379 |
  | Gameboy | 312 | 372 | 838 | 315 | 372 | 1012 | 988 | 7530 |
  | CodeLoad | 3081 | 2710 | 2689 | 3300 | 3034 | 2699 | 4173 | 3676 |
  | Box2D | 534 | 544 | 573 | 537 | 550 | 1254 | 1076 | 15901 |
  | zlib | 15.3 | 15.5 | 16.0 | 15.2 | 16.3 | 25.3 | 1817 | 1826 |
  | Typescript | 238 | 261 | 229 | 248 | 249 | 249 | 901 | 2519 |
  | geomean (with latencies) | 573 | 650 | 1011 | 590 | 650 | 1153 | 1428 | 6238 |

  Final: the baseline tier beats the interpreter on every benchmark but
  CodeLoad (-8%) and Typescript (equal); Maglev beats the baseline tier on
  every benchmark but CodeLoad (-11%) and Typescript (equal). CodeLoad
  compiles fresh code all the time: the compile threads' work slows the
  main thread (thread CPU, R2R build; under full JIT, where the runtime's
  own tiering thread is busy too, the tiers are even). Cold (octane, wall-clock
  scores, same builds, 3 runs, 13:37-15:07Z, load 1.7-3.0, steal 0%):

  | benchmark | base jitless | base sparkplug | base maglev | final jitless | final sparkplug | final maglev | v8:sparkplug | v8:maglev |
  |---|---|---|---|---|---|---|---|---|
  | Richards | 676 | 808 | 2076 | 677 | 863 | 1578 | 1775 | 23870 |
  | DeltaBlue | 558 | 708 | 2217 | 582 | 675 | 2462 | 1783 | 34159 |
  | Crypto | 605 | 658 | 1338 | 625 | 691 | 1380 | 1648 | 21397 |
  | RayTrace | 1267 | 1682 | 1210 | 1305 | 1587 | 1821 | 4087 | 39552 |
  | EarleyBoyer | 2150 | 2450 | 2910 | 2081 | 2481 | 3282 | 7468 | 30863 |
  | RegExp | 891 | 1179 | 1144 | 905 | 1150 | 1232 | 4023 | 6007 |
  | Splay | 2120 | 2342 | 2508 | 2121 | 2626 | 3414 | 3820 | 7414 |
  | NavierStokes | 1676 | 2340 | 7452 | 1738 | 2475 | 7400 | 1771 | 20954 |
  | PdfJS | 2290 | 2480 | 2382 | 2304 | 2461 | 2966 | 8981 | 31029 |
  | Mandreel | 428 | 365 | 1307 | 429 | 453 | 2348 | 1251 | 25813 |
  | Gameboy | 3054 | 3298 | 3546 | 2872 | 3461 | 5233 | 8667 | 67388 |
  | CodeLoad | 9642 | 8627 | 9043 | 9679 | 8902 | 8958 | 17603 | 17357 |
  | Box2D | 2063 | 2031 | 1950 | 2032 | 2058 | 2207 | 4636 | 71094 |
  | zlib | 689 | 698 | 702 | 679 | 732 | 1127 | 72865 | 73992 |
  | Typescript | 5782 | 5930 | 5494 | 5641 | 5571 | 5937 | 20941 | 54448 |
  | geomean (with latencies) | 1608 | 1772 | 2458 | 1614 | 1832 | 2896 | 5211 | 25983 |

  Cold, the final baseline tier is ahead of the interpreter on every
  benchmark but CodeLoad (-8%) and Typescript (-1%), and Maglev is
  ahead of the baseline tier everywhere but CodeLoad (+1%, equal).

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
