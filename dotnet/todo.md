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
StringTable lookups when internalizing (the AstRawString's hash is not
reused), RegisterInfo and the other per-function allocations of the
bytecode generator for lazy compiles, the script part of the compilation
cache.

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

## Phase 2: the fast tiers

Order (decided 2026-09-28): the interpreter is finished first — correctness
(test262/mjsunit) and interpreter performance (target: within 2x of V8
--jitless) — before any further work on the IL tiers. The baseline tier is
merged but off by default until then; the optimizing tier (Maglev) is in
progress, also off by default.

- [x] TieringManager: interrupt budget, OnInterruptTick, feedback allocation
      and the Sparkplug tier-up, InterruptBudgetFor with V8's flag defaults,
      NotifyICChanged; no optimizing tier, so use_optimizer() is false
      (Execution/TieringManager.cs). OSR urgency is ported but unused until
      the optimizing tier exists.
- [~] Baseline compiler: bytecode -> IL (Sparkplug analogue), src/V8Sharp/Baseline/
      (architecture.md 9.1): every bytecode compiles; entry at function start,
      exception handlers and loop headers (OSR from Ignition at JumpLoop);
      batch compilation, --sparkplug (V8's default on; off in V8Sharp for now), --always-sparkplug,
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
      Temporarily OFF by default (--sparkplug=false): enable with --sparkplug
      or --always-sparkplug. Off because of the compile cost on short runs
      (deviations.md); see the Octane table above.
- Baseline: open items
  - Bytecode flushing and baseline code flushing (mjsunit/baseline/flush-*)
    are not implemented (no bytecode aging).
  - No compilation cache, so closures from separately compiled identical
    sources do not share baseline code (mjsunit/baseline/cross-realm).
  - d8.test.verifySourcePositions (verify-bytecode-offsets) is not in the
    test host.
  - Compile cost: RyuJIT takes about 3-6 us per IL byte (about 10 us per
    bytecode byte, 23 IL bytes per bytecode byte with the inline fast
    paths); PdfJS and Box2D spend 2.5-3 s of background CPU compiling 170-290
    functions, which on a loaded machine slows their short Octane runs below
    the interpreter. Compact code (no inline paths) halves the IL but saves
    only 40% of the RyuJIT time and loses the speed-up; RyuJIT's tier 0 for
    the first version is slower than the interpreter. Waiting for more
    ticks before compiling (an experiment: 128 and 1000 invocations' worth)
    hardly reduced what is compiled in those benchmarks.
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
  - Not optimized (the compile bails out): generators and async functions,
    debug bytecodes; no inlining in or of try blocks; BigInt/Float16 and
    resizable-buffer typed arrays, DataView,
    Map/Set/iterators, string builders, array destructuring and for-of
    reductions are generic.
  - Missing reductions that mjsunit/maglev asserts (deopt policy and
    optimization status): ReceiverOrNullOrUndefined compare feedback,
    Array.prototype.push/pop as graph nodes, Math.min/max on mixed feedback,
    typed array length, collection iterators, string compare feedback,
    no stack-slot limit (regress-536945254).
  - Performance: calls not inlined cost ~60 ns (frame record, register
    window, write barriers); deopt exits are most of the IL of big functions,
    and RyuJIT compiles big methods without optimization (MinOpts) and only
    tiers them up late, so the tiering manager does not optimize graphs over
    500 nodes (V8SHARP_MAGLEV_MAX_NODES). No escape analysis, LICM, loop peeling or CSE of loads.
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
