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
      %BaselineOsr, %GetOptimizationStatus baseline bits; Smi fast paths for
      arithmetic, a baseline-to-baseline call path (BaselineCalls). Tests:
      tests/V8Sharp.Tests/Baseline (interpreter vs --always-sparkplug).
      Open: see "Baseline: open items" below.
      Temporarily OFF by default (--sparkplug=false): enable with --sparkplug
      or --always-sparkplug. With it on (tiering, V8's defaults) mjsunit had 3
      new failures against mjsunit.v8sharp.txt (harmony/global, which fails
      interpreted too, and weakrefs/cleanup-from-different-realm,
      cleanup-proxy-from-different-realm, not investigated).
- Baseline: open items
  - Bytecode flushing and baseline code flushing (mjsunit/baseline/flush-*)
    are not implemented (no bytecode aging).
  - No compilation cache, so closures from separately compiled identical
    sources do not share baseline code (mjsunit/baseline/cross-realm).
  - d8.test.verifySourcePositions (verify-bytecode-offsets) is not in the
    test host.
  - Concurrent Sparkplug (background compile) is not ported.
  - Performance: calls still pay the interpreter frame's setup (register
    file clear, frame record, write barriers); a leaner frame protocol
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
    1200 nodes. No escape analysis, LICM, loop peeling or CSE of loads.
  - Conformance under forced optimization (`--maglev
    --invocation-count-for-maglev=4 --optimize-on-next-call-optimizes-to-maglev`,
    2026-10-02): test262 0 newly failing against the expectations; mjsunit
    103 failures vs 60 in the plain run, 39 of them only with optimization:
    11 need `--mock-arraybuffer-allocator`, most others are optimization
    status asserts of Turbofan/turbolev behaviour (truncation analysis,
    undefined doubles, deopt-free generic paths) or need IC changes (store
    handlers for typed arrays, LoadIC feedback for undefined receivers,
    deprecated-map migration in the IC); generators are not optimized.
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
