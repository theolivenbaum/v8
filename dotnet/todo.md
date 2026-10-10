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
      Missing: `Wtf8Decoder` / `StrictUtf8Decoder` (used by V8 for the
      wasm UTF-8 string builtins, which are not implemented).
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
      only for WebAssembly, whose source phase is not implemented: d8's
      SyntaxError), d8's --bundle
      (V8Sharp.D8/Bundle.cs, in d8sharp and the TestRunner). test262
      language/module-code, language/import, language/expressions/dynamic-
      import, staging/source-phase-imports: 100%.
- [x] eval / new Function / with (Compiler.GetFunctionFromEval with the eval
      origin, CreateDynamicFunction, lookup slots, sourceURL comments).
- [x] d8sharp shell (src/V8Sharp.D8): print/write/read/load/quit, Realm,
      d8.file/d8.test basics, performance.now, modules (.mjs), the message
      loop; TestRunner engine V8SharpEngine and the Bench host.

### WebAssembly (src/V8Sharp.Wasm, src/V8Sharp/Wasm)

Design and every deviation: deviations.md, "WebAssembly".

- [x] Engine: WACS (github.com/kelnishi/WACS, commit cad6530, Apache-2.0)
      vendored as `src/V8Sharp.Wasm` (Wacs.Core: decoder, validator, store,
      polymorphic interpreter, the switch runtime; WASIp1, the text format,
      the source generators, the transpiler, the component model and WASI
      host bindings dropped; src/V8Sharp.Wasm/README.md). No package
      references (FluentValidation, Microsoft.Extensions.ObjectPool,
      Fody/InlineIL and CompilerServices.Unsafe uses replaced); safe C# (the
      native-pointer memory mode removed). Fixes made in the
      vendored code carry `V8Sharp:` comments: branches to the function
      label, unwinding, legacy EH (try/catch/catch_all/delegate/rethrow),
      cross-module reference typing by canonical type, tables written in
      place, nested instantiation, trap order, V8's limits.
- [x] JS API (src/V8Sharp/Wasm, port of wasm-js.cc, the JS parts of
      wasm-objects.cc, module-instantiate.cc, the wrappers):
      WebAssembly.{Module, Instance, Memory, Table, Global, Tag, Exception,
      CompileError, LinkError, RuntimeError, validate, compile, instantiate,
      compileStreaming/instantiateStreaming (--wasm-test-streaming),
      Suspending, promising, JSTag}, Module.{exports, imports,
      customSections}, Memory.{grow, buffer, toFixedLengthBuffer,
      toResizableBuffer}, type reflection, compile-time imports
      (wasm:js-string builtins, importedStringConstants), shared memories
      with Atomics and workers, d8 serialization of modules and memories,
      wasm frames in stack traces and Error.captureStackTrace/CallSite, the
      d8 message format for uncaught wasm errors, the %Wasm* test natives
      the mjsunit tests use (tier queries answer as an interpreter).
- [x] Tests: tests/V8Sharp.Wasm.Tests (37 facts: decoder, validator, JS
      API, traps, legacy EH, stack traces, string builtins; expected texts
      taken from the oracle). Conformance (2026-10-04): mjsunit/wasm 341/511
      as expected (all 539 were SKIP before: has_webassembly was false; 221
      on the first run with it), mjsunit/regress/wasm 627/782, message
      356/371 (was 333/333: 38 wasm message tests were SKIP before). The
      failures are listed in expectations/mjsunit.v8sharp.txt. Clusters of
      the 327: about 120 CompileErrors from unimplemented proposals (types,
      opcodes and imports below), about 50 error-message texts, then d8
      hooks (FastCAPI, worker `send`, profiler), JSPI suspension, tier
      assertions and memories above 2 GiB.
- [x] Compiled tier (src/V8Sharp/Wasm/Baseline, ModuleCompiler.cs,
      WasmCodeManager.cs, RuntimeWasm*.cs, WasmSimd.cs, WasmJs.FastWrappers.cs;
      ports of liftoff-compiler.cc, liftoff-assembler.h, function-compiler.cc,
      the lazy-compile and tiering parts of module-compiler.cc and the
      specialized JS-to-wasm wrappers): one IL compiler (DynamicMethod per
      function, RyuJIT optimizes it in place of TurboFan), lazy by default,
      eager with --no-wasm-lazy-compilation; the interpreter runs everything
      with --wasm-jitless/--jitless or V8SHARP_WASM_INTERPRETER=1.
      Covered in IL: all numeric ops (exact trap, NaN and signed-zero
      semantics), control flow (br_table as switch), loads/stores on all
      memories incl. memory64 and shared memories, memory.size/grow/copy/fill,
      globals, direct/indirect/ref calls and tail calls, imports, exceptions
      (exnref try_table and legacy try/catch/delegate/rethrow as IL exception
      filters), reference types, SIMD on Vector128 (about 190 ops), interrupt
      checks in loops, stack checks at entries, wasm frames in stack traces.
      Through the interpreter's instruction objects from compiled code: GC
      instructions, table and bulk-memory ops other than memory.copy/fill,
      atomics, relaxed SIMD, shared/thread-local globals. Coverage over the
      whole mjsunit run (2026-10-09, V8SHARP_WASM_COMPILE_LOG): 32654
      functions compiled, 0 declined; 12217 of 19.7M instructions (0.06%) run
      as interpreter instructions, in 7732 functions. Tests:
      tests/V8Sharp.Wasm.Tests/WasmCompilerTests.cs (16 differential facts,
      interpreter vs compiled, 53 facts in the project). Interpreter bugs the
      differential tests found are fixed in the vendored code (i64.trunc range
      checks, negative SIMD shift counts, racing memory.grow of shared
      memories). Open: tier queries and --trace-wasm* (no second tier),
      call_indirect through a per-site inline cache rather than V8's dispatch table,
      JS-to-wasm calls of non-numeric signatures through the generic wrapper,
      GC instructions inline in IL.
      Speed (2026-10-09, micro:wasm, bench-session.sh, parity publish, 3
      interleaved runs, warm: compiled, .NET-tiered and settled before
      measuring): compiled vs the interpreter (v8sharp:jitless) geomean 37x
      (WasmLoop 669 vs 6.7, WasmMemory 3708 vs 30, WasmFib 605 vs 18,
      WasmCalls 185 vs 8, WasmFloat 873 vs 8.3, WasmJSCalls 39 vs 16); vs
      V8 jit 13% geomean (Loop 28%, Memory 61%, Fib 22%, Float 65%, Calls
      2.3%: TurboFan inlines the small callee, RyuJIT does not inline across
      DynamicMethods, and call_indirect costs about 3x a direct call; JSCalls
      1.1%: the JS-side call path of API functions dominates, not the
      wrapper). V8 --jitless has no WebAssembly (no DrumBrake in the oracle).
- [x] Inlining (Baseline/WasmInliningTree.cs, LiftoffCompiler.Inlining.cs;
      ports src/wasm/inlining-tree.h and the inlining parts of
      turboshaft-graph-interface.cc; deviations.md, "WebAssembly",
      "Inlining"). RyuJIT never inlines one DynamicMethod into another, so the
      compiler inlines wasm callees into the caller's IL itself, with V8's
      budget and flags (--wasm-inlining, --wasm-inlining-budget,
      --wasm-inlining-max-size, --wasm-inlining-factor,
      --wasm-inlining-min-budget, --wasm-inlining-ignore-call-counts,
      --wasm-inlining-call-indirect, --wasm-tiering-budget): direct calls at
      the first compile; call_indirect/call_ref targets speculatively, from
      the feedback of per-site inline caches, when the function tiers up
      (Liftoff's TierupCheck at returns and loop back edges,
      %WasmTierUpFunction). Inlined frames are inlined positions that stack
      traces expand (traps in inlined callees show V8's frames, tail calls
      included). --trace-wasm-inlining prints V8's trace for the tier-up:
      test/message/wasm-speculative-inlining.js matches V8's .out exactly
      under d8sharp (the TestRunner does not capture engine output on
      Console, so it stays listed). mjsunit/wasm/inlining.js passes up to
      the parts that need d8.wasm.serializeModule and two validator gaps
      (call_ref typing, a subtype check). Tests: WasmInliningTests.cs (6
      facts: interpreter vs compiled vs inlining vs eager tier-up).
      Evaluated and not taken: emitting functions as static methods of
      collectible AssemblyBuilder types so RyuJIT inlines them (prototype:
      RyuJIT inlines a trivial callee across separately created types, 4.0
      -> 0.47 ns per call, but with the frame bookkeeping wasm frames need
      (about 60 bytes of IL and a stack-check call) it inlines only with
      AggressiveInlining and keeps the bookkeeping: 4.4 -> 3.2 ns; such
      methods start at tier 0 and reach tier 1 later (DynamicMethods are
      optimized at once), a lazily compiled function needs a type of its
      own, and the 60 KB MinOpts limit applies alike). Open: inline callees
      with exception handlers; polymorphic call_indirect beyond 4 targets
      (V8 deopts, V8Sharp falls back to the cache); passing the instance
      data as an argument instead of reloading it from the WasmCode at
      every entry (about 1 ns per call); splitting functions over about 60 KB
      of IL, which RyuJIT compiles with MinOpts (inlining stops at 30 KB of
      IL so as not to push a method there).
      Speed (2026-10-09, bench-session.sh, parity publishes of main aea50a9d
      and this branch, 3 interleaved runs, warm; fingerprint: Xeon 2.8 GHz,
      load 2.0, steal 0%, cpu-cal 2086/1842 ms, mem-bw 18.6/19.4 GB/s):
      micro:wasm main -> inlining (v8:jit): WasmCalls 195 -> 1712 (8963,
      2.2% -> 19%), WasmFib 681 -> 1531 (2919, 23% -> 52%), WasmLoop 942 ->
      911, WasmFloat 955 -> 883, WasmJSCalls 37 -> 39 (unchanged within
      noise), WasmMemory 4049 -> 3315 (6317; runs 4402/3466/4280 vs
      3856/3338/2752: the loops' machine code is the same but for register
      names, so this is open, not explained); octane-quick zlib 2710 -> 2615
      and Mandreel 457 -> 496 (noise; their hot functions are large and
      call little). Indicative ns per operation (shell, not under the lock):
      a loop calling a 1-instruction function 5.0 -> 0.62 ns (the empty loop
      is 0.61), through call_indirect 8.5 -> 3.75 (inline cache) -> 2.4
      (speculatively inlined), recursive fib 5.7 -> 2.2 ns per call.
- [ ] Validation message texts: V8 names the operand and the instruction
      that produced it ("expected type i32, found local.get of type i64");
      WACS's validator does not track producers. Some mjsunit
      assertThrows/assertCompileError texts fail on this.
- [ ] --trace-wasm* outputs and tier assertions: the 15 message tests
      left in expectations/message.v8sharp.txt.
- [ ] JSPI suspension (needs a resumable interpreter frame stack).
- [ ] Proposals not implemented (CompileError): stringref, custom
      descriptors, shared-everything, WasmFX, exact types, fp16, wide
      arithmetic, compact imports, acquire/release atomics, memory control,
      wasm:text-encoder/decoder, wasm source phase imports.
- [ ] Memories above 2 GiB (managed byte[] backing; memory64 tests).
- [x] asm.js (src/V8Sharp/AsmJs, port of V8 14.7.173.23's src/asmjs:
      asm-scanner, asm-types, asm-parser, asm-js; Wasm/WasmModuleBuilder.cs
      and Wasm/AsmJsOffsets.cs from wasm-module-builder and the asm.js offset
      table; the 0xfa asm opcodes in WACS and the IL compiler). This tree's
      V8 removed src/asmjs; V8Sharp keeps 14.7's pipeline, as the oracle
      does (deviations.md, "asm.js"). Hooked as in 14.7: "use asm" scopes,
      AsmJsCompilationJob in the unoptimized compile, InstantiateAsmJs,
      fallback to bytecode with 14.7's messages and --validate-asm,
      --suppress-asm-messages, --trace-asm-*; asm.js frames in stack traces
      at their JavaScript positions. Tests: tests/V8Sharp.AsmJs.Tests (14.7's
      asm-scanner and asm-types unittests; 14.7's mjsunit asm/,
      regress/asm/, wasm/asm-*, asm-directive: 171/172, the other needs
      d8.profiler and fails on the oracle too; message asm-*: 18/18).
      The instances of a module share its compiled code (WasmSharedCode).
      Speed (2026-10-09, bench-session.sh, parity publishes, 3 interleaved
      runs, CPU-time scores; Mandreel is not asm.js and does not change):
      Octane zlib, before (asm.js run as JS) -> now, vs V8 jit / maglev:
      octane-quick 434 -> 2246 (V8 4862 / 4322: 46% / 52%), octane-steady
      793 (V8 1446 / 1378: 55% / 58%), cold octane (wall, compiles
      included) 4107 -> 25263 (V8 54478 / 57109: 46% / 44%).
      Open: each instance decodes the wire bytes again (WACS links
      instruction objects in place); RyuJIT compiles functions above 60 KB
      of IL with MinOpts (zlib's largest function, 21.7K instructions, is
      one): splitting huge functions would fix it.
      If a translator is wanted for speed, the plan is: port the last
      upstream src/asmjs (asm-scanner, asm-parser, asm-types, asm-js.cc)
      from git history, emit wasm wire bytes, and instantiate them through
      WasmEngine, falling back to JS on validation failure as V8 did. The
      compiled wasm tier it needed now exists.

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
| 2026-10-04 | mjsunit | 7404 | 7602 | 97.4% | baseline pass 2 (out-of-line checks, chunks, code cache, specialized paths), default flags (Sparkplug on): 0 newly failing |
| 2026-10-04 | mjsunit | 8375 | 8902 | 94.1% | WebAssembly: has_webassembly on, so 1300 more tests run (were SKIP); mjsunit/wasm 341/511, regress/wasm 627/782; 0 non-wasm newly failing, +2 (maglev/regress-539121142, regress/regress-447206453); the 327 wasm failures recorded in mjsunit.v8sharp.txt (see "WebAssembly" above) |
| 2026-10-04 | message | 356 | 371 | 96.0% | WebAssembly: 38 wasm message tests run (were SKIP); the 15 left print --trace-wasm* or tiering output |
| 2026-10-04 | test262 | 94881 | 95123 | 99.7% | WebAssembly branch, built-ins and the rest: 0 newly failing, 0 newly passing |
| 2026-10-09 | mjsunit | 8377 | 8902 | 94.1% | WebAssembly compiler on (lazy IL compilation of every wasm function): 0 newly failing; mjsunit/wasm + regress/wasm 970/1293 (was 964 with the interpreter: +regress-347914831, +simd-wasm-interpreter) |
| 2026-10-04 | mjsunit | 7393 | 7587 | 97.4% | baseline pass 2, --always-sparkplug: -2, both failing in the interpreter too with --no-lazy-feedback-allocation: regress-class-initializer-eval, es6/for-of-array-iterator-optimization-maglev-eager-next-call (assertMaglevved) |
| 2026-10-04 | test262 | 94881 | 95123 | 99.75% | baseline pass 2, default flags and --always-sparkplug: 0 newly failing, 0 newly passing (one staging/sm TypedArray test failed before the elements kind check, 57147b31) |
| 2026-10-04 | mjsunit | 7399 | 7602 | 97.3% | baseline pass 2 merged with Maglev on by default (75d8926c), default flags: 0 newly failing (regress-331074427 crashed under memory pressure from a concurrent run; passes alone, with regress-1189077 and regress-3359) |
| 2026-10-04 | mjsunit | 7389 | 7587 | 97.4% | baseline pass 2 merged (75d8926c), --always-sparkplug: the same 2 as above (fail in the interpreter with --no-lazy-feedback-allocation) |

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
Ninth interpreter performance pass: construct, the regexp interpreter, Wide
calls, the GC write barrier (2026-10-03; "before" is b376253d, "after"
b04f4c35; `v8sharp:jitless` against V8 --jitless, regular expressions in
both engines' bytecode interpreters).

Parity publishes, `bench-session.sh`, octane-steady in two sessions of 3
interleaved runs (A: Richards .. NavierStokes plus micro:accessors and
micro:cpu, with a third column: the final build on the runtime's default
write barrier; B: PdfJS .. TypeScript). Every V8Sharp column runs with
`DOTNET_GCWriteBarrier=3` (below) unless stated. Fingerprints (before /
after): A load 1.51 / 1.89, steal 0%, idle 99 / 72%, cpu-cal 1796 / 1722 ms,
mem-bw 32.0 / 31.4 GB/s; B load 2.28 / 5.24, steal 0%, idle 99 / 73%,
cpu-cal 1787 / 1757 ms, mem-bw 33.8 / 27.9 GB/s (another agent's build ran
during the end of B). V8 exited 139 on one run of Richards, DeltaBlue, PdfJS
and CodeLoad.

| benchmark | before | after | after, default barrier | V8 --jitless | after / V8 |
|---|---|---|---|---|---|
| Richards | 472.0 | 476.1 | 429.7 | 801.7 | 59% |
| DeltaBlue | 417.3 | 417.2 | 389.8 | 855.0 | 49% |
| Crypto | 56.4 | 55.0 | 54.5 | 99.2 | 55% |
| RayTrace | 212.9 | 219.7 | 215.6 | 457.9 | 48% |
| EarleyBoyer | 87.4 | 91.4 | 83.7 | 187.8 | 49% |
| RegExp | 209.9 | 234.1 | 224.3 | 494.4 | 47% |
| Splay | 3705 | 3726 | 3531 | 4418 | 84% |
| NavierStokes | 240.8 | 240.3 | 236.4 | 208.9 | 115% |
| PdfJS | 802.9 | 818.9 | | 1921.9 | 43% |
| Mandreel | 83.5 | 83.0 | | 143.7 | 58% |
| Gameboy | 372.3 | 362.9 | | 781.5 | 46% |
| CodeLoad | 3445 | 3456 | | 4083 | 85% |
| Box2D | 737.8 | 735.0 | | 934.0 | 79% |
| zlib | 19.21 | 19.39 | | 40.42 | 48% |
| Typescript | 295.8 | 299.8 | | 627.4 | 48% |
| **geomean (15, no latency scores)** | 299.4 | 303.1 | | 520.5 | **57.5% / 58.2%** |
| geomean of session A (8) | 280.9 | 287.0 | 273.0 | 476.8 | 58.9% / 60.2% (57.3% default barrier) |
| micro:accessors + micro:cpu geomean (26) | 20.51 | 20.90 | 20.55 | 35.79 | 57.3% / 58.4% |

The engine changes are worth +1.2% on the geomean of 15 (RegExp +11.5%,
EarleyBoyer +4.5%, RayTrace +3.2%, the rest within noise); the write
barrier setting +5.1% on session A's eight (Richards +10.8%, EarleyBoyer
+9.2%, DeltaBlue +7.0%, Splay +5.5%). The eighth pass's 55.6% was measured
on the default barrier. Accessors are unchanged: AccProtoGetter 15.95 M/s
(63 ns) against V8's 29.5 M/s (34 ns).

The pass, one commit each (git log b376253d..b04f4c35). Per-step A/B
sessions on bin builds (`octane-quick`, 3 interleaved runs, V8 --jitless in
the session; ab1-ab3 in artifacts/scratch, not committed):
- Construct fast path (`InterpreterInlineCalls.TryConstructFast`): a
  monomorphic ordinary constructor that is its own new.target, with an
  initial map in fast mode, is checked, its receiver allocated
  (FastNewObject, the one call) and entered through `EnterFastCore` (the
  frame entry `TryEnterFast` now shares), with the construct stub's slots
  reserved below the frame; the general entry is `ConstructSlow`. A
  construct frame's return takes the Star lookahead. RayTrace +4%,
  EarleyBoyer +2%.
- RegExp bytecode interpreter: RyuJIT's inlining budget ran out in
  `RawMatch` (11.9 KB of code, a 968-byte frame), so every operand read
  (`I32`, `U16`, `IndexIsInBounds`, the span conversions) was a call, and
  SkipUntilOneOfMasked(3) looked their operands up by name at each execution.
  Operand reads are unaligned loads at fixed offsets, the backtrack stack is
  an array in locals (cached per thread, as V8's RegExpStack on the isolate)
  without try/finally, and the two peephole scans and the case-insensitive
  back references are separate methods (5.7 KB, a 216-byte frame).
  RegExp +12.5%.
- Wide calls and `Construct` (functions with more than 256 feedback slots or
  128 registers) run the single-scale call handlers from the loop
  (`WideCall`), which enter a callee for both scales; they used to run one
  step of `Loop<DoubleScale>` and a nested Run per call (a wide method call
  in a loop: 160 -> 87 ns). TypeScript within noise (its wide calls are 6% of
  its calls); kept for the micro. `BytecodeStats` lists the prefixed
  bytecodes.
- Function.prototype.apply's fast elements test the instance type instead of
  casting to the (unsealed) JSObject class (the cast helper was 0.5% of
  RayTrace).
- V8Sharp.Bench runs V8Sharp with `DOTNET_GCWriteBarrier=3`, the GC's
  non-region ("server") write barrier: an ephemeral range check and a card
  byte, where the default bitwise region barrier looks up the generation of
  both regions. Reference stores into the register stack (an old, pinned
  array) at Stars and call entries make the barrier 6-13% of the OO
  benchmarks. A host setting only the environment can make
  (tools/V8Sharp.Bench/README.md, deviations.md General); d8sharp should be
  run with it for comparable numbers.

Tried and dropped, measured:
- JSString.Length as a type test for SeqString and a virtual call otherwise:
  PdfJS -7%, RegExp -2% (reverted).
- The in-object transition store in the fast SetNamedProperty handler: the
  loop's monomorphic case already calls StoreIC.TryStoreTransition; no
  effect on `new` micros (not committed).

What is left (profiles of the final build): a getter or method call and
return are still ~1.8x V8 (63 ns against 34 ns): the handler call, frame
entry and return are each 40-120 instructions with no single hot spot; the
loop's Smi tests of doubles (cvttsd2si/cvtsi2sd round trips) in Add,
Inc and the comparisons; polymorphic named loads in Gameboy (9% in the fast
handler, the field load and its return through a stack temporary); GC and
allocation (a Context is two objects, a string two, arguments objects
copy their elements); `LoopCold` has a 0x508-byte zeroed frame (TDZ checks
of let/const/class bindings take it; rare in Octane). Not reached: 70% of
V8 --jitless (58.2% measured).

Conformance at b04f4c35: V8Sharp.Tests 1064/1064 (Baseline and Maglev
tests included; 1064/1064 again after merging d20549e1); mjsunit 7602 run, 0
newly failing, 0 newly passing; test262 95123 run, 0 newly failing, 0 newly
passing (shared lock, 2 jobs each, side by side); V8Sharp.RegExp.Tests
103/103 (3 skipped); bytecode generation untouched.
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

Baseline pass 2 (2026-10-04, merged with Maglev on by default; the baseline
tier measured as `v8sharp:sparkplug`, which passes --no-maglev). Changes: call
stubs inline the frame entry (one register-stack compare for overflow and
interrupts), polymorphic FixedArray hits in GetKeyedPropertySlow, RyuJIT
limits recalibrated, offset stores through a ref, unchecked constant/feedback
reads, out-of-line number checks for big functions, compile-time own-field
and typed array specialization, chunked compilation, a code cache by
bytecode. Parity publishes (R2R composite, self-contained), octane-steady,
mean of 2 interleaved runs; "main" is 57d945ce. The host was shared with
other agents' builds and unlocked test runs (load 4-12 at the end of the
second session and the cold one), so the cross-engine ratios are the
evidence, not the absolute scores.

| benchmark | interpreter | baseline (main) | baseline (pass 2) | V8 --jitless | V8 sparkplug |
|---|---|---|---|---|---|
| Richards | 480 | 523 | 684 | 798 | 986 |
| DeltaBlue | 414 | 456 | 549 | 888 | 1076 |
| Crypto | 56.0 | 56.0 | 73.2 | 101 | 136 |
| RayTrace | 234 | 265 | 286 | 462 | 604 |
| EarleyBoyer | 92.2 | 99.4 | 110 | 196 | 246 |
| RegExp | 232 | 299 | 318 | 508 | 960 |
| Splay | 3593 | 3647 | 3774 | 4614 | 4933 |
| NavierStokes | 244 | 351 | 376 | 226 | 266 |
| PdfJS | 814 | 947 | 979 | 1975 | 2599 |
| Mandreel | 84.8 | 74.5 | 85.2 | 146 | 198 |
| Gameboy | 380 | 401 | 462 | 727 | 1023 |
| CodeLoad | 3289 | 3194 | 3355 | 4318 | 4400 |
| Box2D | 707 | 575 | 962 | 938 | 1153 |
| zlib | 19.6 | 19.9 | 23.2 | 42.1 | 1747 |
| Typescript | 306 | 278 | 325 | 639 | 904 |
| geomean | 304 | 320 | 372 | 532 | 865 |

Pass 2 / main baseline: 1.16x; baseline / interpreter: 1.22x (now ahead on
every benchmark); baseline / V8 sparkplug: 0.43 (0.55 without zlib, which V8
runs as asm.js through wasm). Cold (`octane`, the same publishes): geomean
interpreter 1929, main 1931, pass 2 2153, V8 --jitless 3338, V8 sparkplug
5151; CodeLoad (7143 vs 9580), Box2D (1816 vs 2560) and Typescript (6383 vs
7467) are still below the interpreter cold, from RyuJIT compile time.

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

After the ninth interpreter performance pass (b04f4c35, parity publish, two
sessions, 3 runs, `v8sharp:jitless`): **58.2%** of V8 --jitless (303.1 /
520.5; its base, b376253d, measured 57.5% in the same sessions). Both
columns run with the GC's non-region write barrier, which V8Sharp.Bench now
sets (worth +5.1% on the eight classic benchmarks; the 55.6% above is on the
default barrier). See "Ninth interpreter performance pass" above.

## Phase 2: the fast tiers

Order (decided 2026-09-28): the interpreter is finished first — correctness
(test262/mjsunit) and interpreter performance (target: within 2x of V8
--jitless) — before any further work on the IL tiers. The baseline tier is
on by default since 2026-10-03; the optimizing tier (Maglev) since
2026-10-04.

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
      (BaselineCalls, also through Function.prototype.call/apply);
      feedback-specialized own-field and typed array accesses; out-of-line
      checks, chunks and compact code for functions beyond RyuJIT's
      optimization limits; a code cache by bytecode; concurrent
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
  - Functions over RyuJIT's limits are emitted in the out-of-line form, then
    in chunks (one IL method per bytecode range, 2026-10-04); functions over
    100000 bytes of bytecode still stay in the interpreter unless
    %CompileBaseline asks (mjsunit regress-crbug-808192 builds one of 4 MB).
    A chunk exit spills the cached registers and re-enters through the
    dispatch: a hot loop that crosses a chunk boundary would pay it per
    iteration (cuts are placed at the least loop depth to avoid that).
  - Compile cost: functions with the same bytecode share their methods
    (BaselineCodeCache; CodeLoad: 62 of 71 compiles are hits). Typescript and
    PdfJS still pay RyuJIT for each distinct function.
  - In big methods RyuJIT stops inlining (inline budget, 512 locals): what
    the code relies on being inlined must be IL (the bytecode offset store,
    Smi constants are; FieldAt, Map.IsUndetectable, FromNumber are calls).
  - Performance: calls still pay the interpreter frame's setup (register
    file clear, frame record, write barriers: about 20% of DeltaBlue and
    Richards in BaselineCalls.EnterInline, PushFrame and the write barrier,
    after the 2026-10-04 pass); a leaner frame protocol shared with the
    interpreter would help both tiers. Skipping the register clear for
    register-cached callees was measured and gave nothing. Calls between
    Maglev functions no longer do (lazy frames, see "Calls and frames").
  - SignedSmall feedback needs an explicit Smi check per operand (JSValue has
    no tagged Smis): integral, 31-bit, not -0.
  - zlib: V8 runs it as asm.js through wasm (about 70x the baseline score);
    out of scope for the baseline tier.
- [~] Optimizing compiler (Maglev analogue), src/V8Sharp/Maglev/ and
      Deoptimizer/ (architecture.md 9.2). On by default since 2026-10-04 (`--no-maglev` turns it off):
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
  - Performance (2026-10-04): calls between optimized functions enter the
    callee's direct entry (MaglevCode.FastCall, ~25 ns for a method call
    from ~45 ns); what is left per call is the interpreter frame the entry
    builds (fixed slots, frame record, InterpreterState) and the GC write
    barrier of the receiver slot. Load elimination of fields, elements,
    lengths and context slots, loop effects by graph restart (no loop
    peeling), parameter stores sunk to observing nodes, hoisted loop entry
    untagging, keyed load elements kind transitions, nested literal copies,
    instanceof without the IC, apply(arguments) forwarding for megamorphic
    targets, inlining with the --maglev-as-top-tier limits. Open: no
    CSE or range analysis (see "Code quality on warm code" and "Inlined
    allocation and escape analysis" for what came since).
  - Code quality on warm code (2026-10-04, 28765f36..a6367b6c; V8 files:
    maglev-graph-builder.cc, access-info.cc, maglev-ir.cc,
    maglev-code-generator.cc): field representations and field types from
    the descriptor (LoadDoubleField / StoreDoubleField payloads, Smi fields
    untagged unchecked, stable class field types as known maps, with
    FieldRepresentation / FieldType / stable map dependencies); const fields
    of constant objects folded (TryFoldLoadConstantDataField, FieldConst);
    generic calls with up to three arguments pass them as values and enter
    Maglev callees through their direct entry, builtins through their fast
    paths; bodies up to 1200 bytes of IL inlined into the direct entry;
    element, context slot and in-object transition stores inline through the
    slot address; CheckedObjectToIndex inline for numbers; polymorphic load
    continuations (FindContinuationForPolymorphicPropertyLoad: one arm per
    map group up to the call, constant call targets); a constant callee wins
    over call feedback; loop peeling of innermost call-free loops (PeelLoop,
    under 400 bytes, 900 per compilation); frameless direct entries for leaf
    code (no frame, no frame record; deopt builds the frame from the
    scratch buffer, MaglevCalls.DeoptimizeFrameless). Switches for A/B:
    V8SHARP_MAGLEV_NO_FRAMELESS, _NO_CONTINUATIONS, _NO_INLINE_TRANSITIONS,
    V8SHARP_MAGLEV_INLINE_BODY_IL=0, --no-maglev-loop-peeling.
    Effect of each (warm octane-quick, interleaved, 3 runs, switch off vs
    on): frameless entries RayTrace +18%, DeltaBlue +10%, Box2D +8%;
    continuations Richards +14%; peeling NavierStokes +21% (Box2D and
    Crypto within noise either way; peeling loops with calls cost DeltaBlue,
    EarleyBoyer and Box2D, hence call-free only); inline transitions
    Richards +7%; inlined bodies within noise warm. Before the warm
    methodology (compile time included): field representations Box2D +21%,
    RayTrace +4%; value calls Richards +10%, DeltaBlue +11%, Crypto +20%.
    All of it against main 9e48c4b5 (octane-quick, 3 runs): Richards +24%,
    DeltaBlue +31%, RayTrace +39%, Crypto +19%, EarleyBoyer +3%,
    NavierStokes +15%, Box2D +18%.
    octane-steady (warm, compile time excluded; parity publishes, 3
    interleaved runs, two sessions: load 5.9/2.6 and 7.4/6.5, steal 0-1%,
    cpu-cal 1665/1726 and 1805/1713 ms, mem-bw 30.3/30.7 and 33.4/31.4 GB/s;
    v8:jit crashed (exit 139) once on Richards, RayTrace, EarleyBoyer and
    NavierStokes; latency rows omitted):

    | benchmark | main 9e48c4b5 | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 1112 | 1525 | 8903 | 12333 |
    | DeltaBlue | 1364 | 1750 | 10733 | 20648 |
    | Crypto | 306 | 365 | 1344 | 2086 |
    | RayTrace | 328 | 413 | 3807 | 6289 |
    | EarleyBoyer | 158 | 161 | 904 | 1143 |
    | RegExp | 231 | 252 | 1040 | 1112 |
    | Splay | 2582 | 2698 | 10581 | 11293 |
    | NavierStokes | 1463 | 1736 | 2068 | 3008 |
    | PdfJS | 1484 | 1391 | 8575 | 8972 |
    | Mandreel | 607 | 624 | 4215 | 5789 |
    | Gameboy | 1413 | 1897 | 7436 | 6168 |
    | CodeLoad | 3490 | 3863 | 3828 | 4208 |
    | Box2D | 2543 | 3283 | 17417 | 18993 |
    | zlib | 174 | 184 | 1831 | 1830 |
    | Typescript | 447 | 513 | 2448 | 2484 |

    Cold Octane (start-up, compile time included; wall clock, same
    publishes, 3 interleaved runs, load 8.4/3.1, steal 0-1%, cpu-cal
    1770/1706 ms, mem-bw 28.9/29.9 GB/s; V8 crashed (exit 139) once on 11
    benchmark/engine pairs, their means are over two runs):

    | benchmark | main 9e48c4b5 | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 2330 | 3376 | 24142 | 37129 |
    | DeltaBlue | 3158 | 4133 | 30289 | 59449 |
    | Crypto | 4414 | 5978 | 22234 | 34310 |
    | RayTrace | 2702 | 3284 | 34262 | 54175 |
    | EarleyBoyer | 4699 | 4845 | 29856 | 42277 |
    | RegExp | 1215 | 1147 | 5872 | 6777 |
    | Splay | 3374 | 3620 | 5212 | 4050 |
    | SplayLatency | 2358 | 2291 | 3403 | 2785 |
    | NavierStokes | 14434 | 15316 | 21073 | 29434 |
    | PdfJS | 3820 | 3765 | 30334 | 32021 |
    | Mandreel | 3400 | 3383 | 24459 | 35232 |
    | MandreelLatency | 6436 | 6511 | 34780 | 42652 |
    | Gameboy | 7078 | 9322 | 69564 | 73202 |
    | CodeLoad | 8392 | 8956 | 16956 | 17328 |
    | Box2D | 5194 | 5504 | 72326 | 82496 |
    | zlib | 7051 | 7385 | 71382 | 75443 |
    | Typescript | 8834 | 8997 | 57004 | 54903 |
    | geomean | 4416 | 4871 | 24166 | 29196 |

    Warm geomean of the 15 (latency rows out): 881 from 766 at main (+15%),
    22.5% of v8:maglev from 19.5%; cold geomean +10%. PdfJS -6% warm is
    the one decrease (within its run-to-run noise; open). Conformance at 30152ab1:
    V8Sharp.Tests 1101/1101 (bytecode goldens included); test262 0 newly
    failing, default and forced (95123 run each); mjsunit forced 0 newly
    failing, 0 newly passing (7565 run); mjsunit default 1 newly failing,
    regress-1236560 (TIMEOUT: a stack overflow through a typed array store,
    74 s alone and as slow with --no-maglev; it times out on main 9e48c4b5
    too). compiler/constructor-inlining timed out with inline stores in
    code with catch blocks (RyuJIT, see deviations.md); those go through
    the StoreSlot helper now (after the measurements above, which it does
    not affect but for functions with handlers).
    Open, by size of the gap to v8:maglev: zlib (10x; one function of
    6343 nodes, MinOpts above RyuJIT's limits, needs function splitting or
    smaller deopt metadata), RayTrace and Box2D (allocation of short-lived
    vectors: escape analysis and inlined allocation), DeltaBlue and
    Richards (call frames: the frameful entry's frame and frame record for
    every non-leaf call; polymorphic calls through MaglevCalls.Call),
    EarleyBoyer (closures and cons cells: allocation), Splay (tree
    allocation, GC write barriers), and a quarter of the basic blocks are
    map checks of values not known to be receivers (main's measurement).
  - Types, calls and huge functions (2026-10-09, 29c46401..; V8 files:
    maglev-graph-builder.cc, maglev-interpreter-frame-state.cc,
    maglev-ir.cc/h, maglev-code-generator.cc):
    - Phi types (MergeValue's union of the inputs' types; loop phis get
      their post-loop type when the back edge is merged; result phis of
      inlined calls and polymorphic accesses) and CheckType on map loads
      (CheckMaps, LoadMap, TransitionElementsKind skip the receiver check
      when the input is known to be a JSReceiver where the node is built).
      DeltaBlue's IL histogram: map checks of known receivers 135 -> 242
      of 476.
    - Prototype chain validity as a compilation dependency on the IC
      handler's validity cell (V8: DependOnStablePrototypeChains) instead of
      a CheckValidityCell per access: DeltaBlue had 572 of them (8.6 KB of
      IL, 572 blocks); Richards -8% basic blocks.
    - Calls whose feedback is a FeedbackCell (closures of one CreateClosure
      site; BuildCallWithFeedback): CheckJSFunctionFeedbackCell, then the
      shared function's Maglev code entered directly or inlined with the
      closure's context; the inlined frame's closure is spilled for deopts.
      Closure micro (four closures of one site and a sloppy one): 48 ->
      33 ns per call pair directly, 25 ns inlined. V8 has no polymorphic
      call target feedback (its CallIC is monomorphic, a feedback cell, or
      megamorphic), so this and the polymorphic load continuations are
      what V8 does for polymorphic calls.
    - Region splitting (V8Sharp only, deviations.md): code over RyuJIT's
      optimization limits is emitted again as methods of 40% of the limits,
      cut at the least loop depth, with the live values passed through a
      per-activation Transfer struct. zlib's a1 (143 KB of IL, MinOpts) is
      six FullOpts regions; Typescript had 8 MinOpts compiles
      (Parser.parseStatement 67-87 KB, TypeFlow.typeCheckFunction), none
      now. Region tests force splitting of every Maglev test snippet;
      all of Octane runs correctly with code split at 3000 IL bytes.
    - Int32 overflow checks keep the 64-bit result on the IL stack (a
      scratch local shared by every check is one long-lived variable to
      RyuJIT's register allocator); pure builtin calls (no call, throw or
      lazy deopt, no frame arguments) no longer push lazily pushed inlined
      frames and are allowed in frameless entries.
    Per change, warm octane-quick (3 interleaved runs, bin builds with
    main 79d66bad's Bench; load 5.8 -> 1.6, steal 0%, cpu-cal 1766/1807 ms,
    mem-bw 19.5/17.0 GB/s): validity cells DeltaBlue 1177 -> 1678 (+43%),
    Crypto 525 -> 590 (+12%); the rest within noise. Richards and DeltaBlue
    are bimodal from run to run (Richards 1400-1600 or 2200-2900 with the
    same build: concurrent compiles see different feedback and inline
    differently), so a single row change below about 30% is not a result
    for them. Regions on vs off (V8SHARP_MAGLEV_NO_REGIONS=1, same build;
    octane-quick, 3 interleaved runs, load 1.5/2.4, steal 0%, cpu-cal
    1828/1922 ms, mem-bw 17.6/16.9 GB/s): zlib 418 -> 440 (+5%, 2 functions
    split into 10 regions), Mandreel 407 -> 421 (+3%, 2 split), Box2D
    1018 -> 952 (-6%, 2 split), Typescript 25.5 -> 23.4 (-8%, 18 split,
    among them the recursive Parser.parseStatement: every call goes
    through the dispatcher and three or four region calls), PdfJS 908 ->
    624 (no function is split; that row is run-to-run variation, 515-808
    with regions on). Warm, the split code is no faster than MinOpts code
    of the same function. Cold (octane, compile time included, same
    builds, 3 runs, load 2.6/3.4, cpu-cal 2276/1880 ms, mem-bw 17.6/12.7
    GB/s): Typescript 3831 -> 4332 (+13%), zlib 4312 -> 4423 (+3%),
    Mandreel 1329 -> 1353 (+2%), Box2D 1446 -> 1304 (-10%, two small
    splits; 1215-1360 vs 1287-1527, overlapping). Regions stay on: they
    are neutral overall and keep huge code out of MinOpts; the per-call
    cost of the dispatcher for a hot recursive function (parseStatement)
    is open (splitting only code reached once per call, or entering the
    first region directly, would remove it).
    Not done: (2) leaner frameful calls. A frameful direct call costs about
    23 ns more than a frameless one (micro, 34.5 vs 11.5 ns); the cost is
    spread over the frame's slot stores, the frame record, the
    InterpreterState and the context switch (a profile of the entry by
    instruction groups shows no single hot spot; removing the fault block
    measured nothing). Materializing frames lazily as V8 does needs stack
    walks (Error.stack, function.arguments) to find frameless activations,
    which the frame records are; a frame built on demand before the first
    call would only help paths without calls. (5) Register pressure: in
    Crypto's am3 loop RyuJIT keeps nearly every value in a stack slot
    (frames of 600-950 bytes; only two or three registers used in the
    loop). Removing the loop's only call (the interrupt check) changed the
    frame by 2%, so the cold calls are not the cause; the remaining
    suspects are the values the loop's deopt exits read (every check keeps
    the frame state live through the loop) and the size of the method
    (RyuJIT's allocator spills whole intervals). The scratch local of the
    overflow checks was one such interval (removed). The cause was the deopt
    exits' block weights; see "Hot loop code quality" below.
    The branch before main's allocation work (octane-steady, parity
    publishes, 3 runs, against main 53e9d1ba; load 8.8/16.3, idle 0-1%,
    so noisy): geomean of the 15 +2.8% (621 vs 604), DeltaBlue +13%,
    PdfJS +13%, zlib +11%, RegExp +10%, Splay +11%; Gameboy -10%,
    Mandreel -6%, NavierStokes -5% within their run-to-run ranges.
    Final, merged with main 38355201 (inlined allocation and escape
    analysis) at ab104c2a; octane-steady (warm, compile time excluded),
    parity publishes (R2R composite, self-contained), 3 interleaved runs,
    load 4.4/8.2, steal 0-1%, idle 71%/24%, cpu-cal 2228/2105 ms, mem-bw
    17.1/14.7 GB/s; V8 crashed (exit 139) on 5 benchmark/engine runs, their
    means are over the other runs; latency rows out of the geomean:

    | benchmark | main 38355201 | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 1004 | 1285 | 5147 | 6848 |
    | DeltaBlue | 1328 | 1458 | 7984 | 9669 |
    | Crypto | 240 | 210 | 627 | 1494 |
    | RayTrace | 532 | 543 | 2442 | 4118 |
    | EarleyBoyer | 105 | 115 | 574 | 758 |
    | RegExp | 174 | 189 | 759 | 705 |
    | Splay | 2462 | 2364 | 7706 | 8396 |
    | NavierStokes | 1194 | 1525 | 1374 | 2535 |
    | PdfJS | 1000 | 1135 | 5280 | 5871 |
    | Mandreel | 404 | 439 | 2733 | 4663 |
    | Gameboy | 1301 | 1231 | 3907 | 4361 |
    | CodeLoad | 2409 | 2585 | 2477 | 2848 |
    | Box2D | 1970 | 2725 | 10079 | 12379 |
    | zlib | 135 | 132 | 1360 | 1402 |
    | Typescript | 314 | 316 | 1355 | 1519 |
    | geomean | 630 | 679 | 2465 | 3220 |

    Warm geomean +7.9% over main; 27.6% of v8:maglev, 21.1% of v8:jit.
    Crypto (204-231 vs 207-275) and Splay are within their ranges.

    Cold Octane (start-up, compile time included; wall clock, same
    publishes, 3 interleaved runs, load 1.9/4.6, steal 0%, cpu-cal
    2119/2213 ms, mem-bw 17.0/18.2 GB/s; V8 --jit crashed once on Gameboy):

    | benchmark | main 38355201 | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 2385 | 2574 | 14886 | 24926 |
    | DeltaBlue | 2521 | 3214 | 21449 | 45496 |
    | Crypto | 3760 | 4224 | 10814 | 25791 |
    | RayTrace | 2992 | 3429 | 24193 | 36161 |
    | EarleyBoyer | 3125 | 3222 | 21921 | 30385 |
    | RegExp | 782 | 751 | 4070 | 4471 |
    | Splay | 1700 | 1583 | 3234 | 2513 |
    | SplayLatency | 1752 | 1819 | 2501 | 2021 |
    | NavierStokes | 7867 | 8640 | 13006 | 24635 |
    | PdfJS | 787 | 835 | 16595 | 11449 |
    | Mandreel | 1245 | 1063 | 14292 | 22463 |
    | MandreelLatency | 2607 | 2530 | 17426 | 22476 |
    | Gameboy | 2588 | 3095 | 34807 | 34656 |
    | CodeLoad | 5026 | 4520 | 10702 | 10823 |
    | Box2D | 1947 | 1848 | 39428 | 37630 |
    | zlib | 5164 | 5004 | 58785 | 49312 |
    | Typescript | 4450 | 4624 | 34778 | 32555 |
    | geomean | 2551 | 2640 | 16721 | 20621 |

    Cold geomean +3.5% over main (15.8% of v8:maglev, 12.8% of v8:jit).
    Toward 50% of v8:jit warm (a 2.4x geomean gain), the gaps by their
    share of the log-geomean gap: Mandreel and zlib (x10.6 each, 10%
    each), RayTrace x7.6, Crypto x7.1, DeltaBlue and EarleyBoyer x6.6 (8%
    each), Richards x5.3, PdfJS x5.2, Typescript x4.8 (7% each), Box2D
    x4.5, RegExp x3.7, Splay x3.6, Gameboy x3.5, NavierStokes x1.7,
    CodeLoad x1.1. By cause: calls and frames (DeltaBlue, Richards,
    RayTrace's EnterInlinedFrame, EarleyBoyer, Typescript: about a third
    of the gap); code quality of hot loops on typed and untyped arrays
    (zlib, Mandreel, Crypto, Gameboy: RyuJIT keeps the loop values in
    stack slots, every check a deopt exit with its frame state live; about
    a third); allocation and GC (EarleyBoyer, Splay, Box2D, RayTrace);
    RegExp (the irregexp port's matcher).
    Conformance (9159d809 for test262, the merge of main aea50a9d for
    mjsunit): V8Sharp.Tests 1194/1194 (bytecode goldens included);
    test262 0 newly failing, default and forced (95123 run each; one
    flaky CRASH under load, staging/sm/String/replace-math, passed on
    rerun); mjsunit default 0 newly failing (8902 run); mjsunit forced
    0 newly failing (8862 run). The merge with main's allocation work had broken
    four mjsunit tests (es6/unscopables and three more: an arguments
    object in a closure inlined through its feedback cell took a null
    JSFunction constant), fixed in 9159d809.
    regress/regress-crbug-808192 takes 223 s alone with forced Maglev (233 s
    on main) against a 240 s limit, so it times out under load.
  - Inlined allocation and escape analysis (2026-10-09, 66b7b30b..3b512997;
    V8 files: maglev-ir.h InlinedAllocation/VirtualObject,
    maglev-graph-builder.cc BuildInlinedAllocation, CreateJSConstructor,
    TryBuildStoreTaggedFieldToAllocation, TryBuildLoadTaggedFieldFromAllocation,
    BuildVirtualArgumentsObject, TryBuildFastCreateObjectOrArrayLiteral,
    TryBuildInlinedAllocatedContext, FastCreateClosure,
    maglev-post-hoc-optimizations-processors.h RunEscapeAnalysis,
    maglev-code-generator.cc captured objects, translated-state.cc
    materialization): `new` of known constructors, primitive-field object
    literals and `{}` are InlinedAllocations constructed in IL; their fields
    are tracked in VirtualObjects (--maglev-object-tracking, on in V8Sharp)
    through map transitions, also for allocations stored in each other;
    non-escaping ones are elided with their stores and materialized at deopts
    (DeoptPoint.CapturedObjects); inlined functions' arguments objects are
    virtual for apply(thisArg, arguments), with the extra arguments in the
    deopt frames (frames pushed lazily); calls passing a non-escaped
    allocation inline to the hard depth limit; megamorphic named loads of a
    virtual object use its exact map (Class.create constructors); contexts
    and closures by frame-free helpers; repeated validity cell checks and
    overwritten transition map stores dropped (deviations.md, Maglev, items
    on inlined allocations). Tests: tests/V8Sharp.Tests/Maglev/
    MaglevEscapeAnalysisTest.cs (differential snippets, deopt materialization,
    graph facts). --trace-maglev-escape-analysis names the escaping uses.
    Per change (octane-quick, the warm protocol of 79d66bad, 3 interleaved
    runs, parity publishes of main 79d66bad and f1310101 with switches;
    load 10.6/8.5, steal 0-2%, cpu-cal 1771/1655 ms, mem-bw 19.3/18.5 GB/s;
    "alloc" = inlined allocation, inlined arguments, closures/contexts,
    validity cells, without escape analysis and deep inlining; "EA" = plus
    escape analysis; "all" = plus inlining for escape analysis):

    | benchmark | main | alloc | EA | all | v8:maglev |
    |---|---|---|---|---|---|
    | RayTrace | 601 | 742 | 789 | 1094 | 5072 |
    | Box2D | 1105 | 1117 | 1093 | 1200 | 4893 |
    | EarleyBoyer | 234 | 261 | 250 | 257 | 1270 |
    | Splay | 3073 | 2152 | 2542 | 2543 | 7660 |
    | DeltaBlue | 2463 | 2568 | 2475 | 2566 | 13512 |
    | Richards (control) | 2669 | 2528 | 2724 | 2802 | 12842 |

    Earlier (2026-10-04, compile-time-included warm protocol, octane-quick,
    3 runs, load 7.9/19.3): constructed objects 66b7b30b vs 30664d6b
    RayTrace +10%, Box2D +7%, EarleyBoyer +7%, DeltaBlue +7%, Splay +3%,
    Richards +3%; inlined arguments 6c4dd3ad RayTrace +15% more, DeltaBlue
    +6%. Splay's quick score is noisy (GC-bound; local alternating runs of
    main and final are equal within noise). Elided per Octane run: RayTrace
    17 of 61 allocation sites; Box2D, EarleyBoyer, DeltaBlue and Splay
    none (their objects are stored into longer-lived ones, returned from
    functions not inlined, or merged with different field values).
    octane-steady (warm protocol of 79d66bad; parity publishes main 79d66bad
    and final 3b512997, 2 interleaved runs, two sessions: load 3.6/1.7 and
    2.3/2.9, steal 0%, cpu-cal 1794/2153 and 1966/2100 ms, mem-bw 15.5/14.7
    and 15.9/13.4 GB/s; V8 crashed (exit 139) once on RayTrace (maglev),
    EarleyBoyer (jit) and zlib (maglev); latency rows omitted):

    | benchmark | main 79d66bad | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 1124 | 1128 | 4954 | 7159 |
    | DeltaBlue | 1217 | 1131 | 5609 | 11348 |
    | Crypto | 242 | 261 | 627 | 1444 |
    | RayTrace | 258 | 521 | 2503 | 3324 |
    | EarleyBoyer | 101 | 108 | 520 | 764 |
    | RegExp | 176 | 179 | 720 | 742 |
    | Splay | 1961 | 3356 | 7334 | 6349 |
    | NavierStokes | 1213 | 1225 | 1642 | 2508 |
    | PdfJS | 1067 | 1040 | 5117 | 6584 |
    | Mandreel | 439 | 451 | 3025 | 4182 |
    | Gameboy | 1574 | 1332 | 4794 | 5679 |
    | CodeLoad | 2701 | 2519 | 2995 | 2594 |
    | Box2D | 2198 | 2888 | 12851 | 12053 |
    | zlib | 118 | 128 | 1376 | 1384 |
    | Typescript | 293 | 371 | 1863 | 1883 |
    | geomean | 606 | 679 | 2574 | 3237 |

    Warm geomean +12% over main: 26.4% of v8:maglev (from 23.5%), 21.0% of
    v8:jit (from 18.7%). Gameboy -15% and CodeLoad -7% here were not
    reproduced by an octane-quick A/B (3 runs: Gameboy 484/483, CodeLoad
    1175/1370 main/final). Cold Octane (start-up, wall
    clock, same publishes, 2 interleaved runs, load 3.5/5.7, steal 0-1%,
    cpu-cal 1844/1924 ms, mem-bw 14.6/19.7 GB/s; V8 crashed (exit 139) on
    RayTrace and EarleyBoyer (maglev, twice each: one run of RayTrace left)
    and RegExp (jit, once)): main / final / v8:maglev / v8:jit: Richards
    3066/2692/19164/27585, DeltaBlue 3034/3436/25267/53232, Crypto
    4115/4463/11504/26840, RayTrace 2393/4019/27325/37130, EarleyBoyer
    3166/3596/21220/31937, RegExp 1028/960/4745/4778, Splay
    2225/2373/4150/3608, NavierStokes 10316/11786/15380/25744, PdfJS
    1294/1747/21798/19457, Mandreel 1719/1989/18990/24528, Gameboy
    1993/2185/36150/33682, CodeLoad 5463/5252/11703/12508, Box2D
    3398/1906/39234/39338, zlib 4996/5199/61828/61938, Typescript
    4393/4816/36716/36334; geomean (with latencies) 2930/3113/17279/20946
    (+6%). Box2D's runs were 2830/3966 (main) and 2504/1308 (final, a 5.2 s
    run); cold runs of the bin builds alternating main and final gave
    Box2D 8274/5678, 7599/5461, 5125/5861 ms and Richards 535/744, 755/572,
    652/590 ms: within the cold noise.
    Conformance (3b512997 before merging main 53e9d1ba, flock -s, --jobs 2):
    test262 default and forced 0 newly failing (95123 run); mjsunit forced
    1 newly failing, regress-1236560 (TIMEOUT, known); mjsunit default 2
    newly failing, regress-crbug-808192 and unicode-case-overoptimization0
    (TIMEOUTs under load: 130 s and 38 s alone with both main and this
    branch). After merging main 53e9d1ba
    (de7201d2): V8Sharp.Tests 1119/1119 (bytecode goldens included); mjsunit
    default 0 newly failing, 0 newly passing (8902 run, 7 flaky on rerun);
    mjsunit forced 1 newly failing, regress-1236560 (TIMEOUT, known; 8862
    run). An earlier run found maglev/arguments-forwarding (a store into
    an inlined arguments object before the apply), fixed in 3b512997.
    Open, ranked by their share of the geomean gap to v8:jit (log of the
    ratio / 15): zlib (x10.8; asm.js, one huge function), DeltaBlue (x10.0;
    calls, frames), Mandreel (x9.3), EarleyBoyer (x7.1; cons cells stored
    into each other and returned: escape through returns of non-inlined
    calls), RayTrace (x6.4; frames of lazily pushed inlined functions,
    EnterInlinedFrame 4.5% of samples), Richards (x6.3), PdfJS (x6.3),
    Crypto (x5.5), Typescript (x5.1), Gameboy (x4.3), Box2D (x4.2), RegExp
    (x4.1). For allocation: virtual objects are not merged at control-flow
    joins (V8 merges them with phis), no allocation folding, contexts and
    closures are not escape-analysed, and objects returned by calls that are
    not inlined always escape.
  - Calls and frames (2026-10-09, 45787b8a..; V8 files: frames.cc
    MaglevFrame, deoptimizer.cc, builtins-call-gen.cc CallFunction,
    maglev-graph-builder.cc BuildGenericCall/BuildConstruct/
    TryReduceConstructArrayConstructor/ReduceFunctionPrototypeCall):
    - Lazy frames (45787b8a, 06f451fc, a54c8f4d, eb377d28, 491e52b5,
      59b123ec, cab3ea10): a direct entry that calls out keeps its
      function, pc, argc, receiver and arguments in a MaglevActivation
      struct on the .NET stack and pushes a frame record flagged Lazy that
      points at it (one store of each, no register-window writes, no
      write barriers). Stack walks, captureStackTrace, f.arguments and
      f.caller read the activation; deopt materializes the record into a
      full interpreter frame first (MaglevCalls.MaterializeLazyFrame).
      Inlined functions of a lazy entry get lazy records too, pushed only
      at calls that can observe them. Arguments objects, rest parameters
      and apply(this, arguments) read the activation. fib(n) per call
      ~30 ns -> ~17 ns. Long-warm local A/B against
      V8SHARP_MAGLEV_NO_LAZY_FRAMES=1 (best ms, same build): Richards
      3.7/5.8, DeltaBlue 5.9-6.5/7.2, RayTrace 50/55, EarleyBoyer
      199-207/247-249. Entries on Octane after the change: RayTrace 23
      lazy/0 frameful, EarleyBoyer 39/0, DeltaBlue 22/0, Crypto 45/0,
      NavierStokes 12/0, Typescript 122/15 (V8SHARP_MAGLEV_TRACE_ENTRIES
      names the reason for each frameful one).
    - Direct entries from every tier (e9502620, 1f658dc7): baseline,
      interpreter and runtime calls of Maglev code with argc <= the
      code's FastCallArity enter its direct entry instead of the
      frameful one (EarleyBoyer +7%, Typescript +5% octane-quick).
    - Values instead of register windows (25565961, 0185c2a9): generic
      calls with up to six and constructs with up to six arguments pass
      them as values (no AllocateRegisters/window copy in the caller).
    - Reductions on the call path (06cb1b93, 57cf840a): new Array() with
      an AllocationSite allocates without a frame (DeltaBlue's
      OrderedCollection); f.call(r, ...) is a call of f (inlined or
      direct when f is known).
    octane-steady (parity publishes main fde9fe66 and final 7d927f7b, 2
    interleaved runs, two sessions: load 3.0/2.5 and 2.2/2.5, steal 0%,
    cpu-cal 1916/1805 and 1741/1671 ms, mem-bw 14.8/19.3 and 17.0/19.6
    GB/s; V8 crashed (exit 139) on RayTrace, Splay and zlib (maglev) and
    NavierStokes (jit) once each; latency rows omitted):

    | benchmark | main fde9fe66 | final 7d927f7b | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 1199 | 1841 | 5716 | 8669 |
    | DeltaBlue | 1313 | 1922 | 7424 | 14552 |
    | Crypto | 283 | 298 | 663 | 1629 |
    | RayTrace | 627 | 736 | 3117 | 4699 |
    | EarleyBoyer | 134 | 152 | 627 | 864 |
    | RegExp | 221 | 222 | 884 | 899 |
    | Splay | 2833 | 3150 | 8732 | 8768 |
    | NavierStokes | 1542 | 1538 | 1726 | 2793 |
    | PdfJS | 1244 | 1291 | 6200 | 7716 |
    | Mandreel | 532 | 508 | 3392 | 5031 |
    | Gameboy | 1440 | 1709 | 5240 | 5848 |
    | CodeLoad | 2895 | 3005 | 3441 | 3472 |
    | Box2D | 2768 | 2653 | 12422 | 14900 |
    | zlib | 770 | 885 | 1503 | 1549 |
    | Typescript | 343 | 446 | 1921 | 2169 |
    | geomean | 840 | 948 | 2928 | 3897 |

    Warm geomean +13% over main: 32.4% of v8:maglev (from 28.7%), 24.3%
    of v8:jit (from 21.6%). Richards +54%, DeltaBlue +46%, Typescript
    +30%, Gameboy +19%, RayTrace +17%, zlib +15%, EarleyBoyer +13%,
    Splay +11%; Box2D -4% and Mandreel -4% within the session noise.
    micro:frames and micro:calls (3 runs, same session protocol, load
    2.7): main / final / v8:maglev / v8:jit: Fib 1677/2571/9139/9687,
    PolymorphicMethods 879k/983k/2.40M/3.12M, NewArray
    48.8k/98.0k/622k/950k, CallLoop 3242/3375/5987/11452, MethodCall
    3295/3485/6204/12227, ClosureCreate 216/217/553/2103.
    Cold Octane (start-up, wall clock, same publishes, 2 interleaved runs,
    load 2.6/3.3, steal 0%, cpu-cal 1709/1764 ms, mem-bw 20.0/20.4 GB/s;
    V8 crashed (exit 139) on 9 of its 60 runs): main / final / v8:maglev /
    v8:jit: Richards 3227/4879/19031/31670, DeltaBlue
    4098/5997/25882/57866, Crypto 4540/5176/13799/29594, RayTrace
    6238/6933/33340/54250, EarleyBoyer 4108/4536/25166/37033, RegExp
    1144/1132/4904/5330, Splay 3362/3452/6088/4389, NavierStokes
    15018/14936/16267/28525, PdfJS 2808/2620/22812/26078, Mandreel
    2810/2662/21222/30751, Gameboy 6856/5588/47174/52804 (runs
    6448/4728 against 6556/7157), CodeLoad 6894/7062/14686/14016, Box2D
    4734/3744/52480/67170, zlib 36627/36568/71610/69108,
    Typescript 7870/7816/46037/46438; geomean 5133/5340/22237/29145
    (+4%). Box2D cold is lower in both runs (3718/3769 against
    4783/4686); with the final publish alone, lazy frames on/off gave
    4043/4574, 4547/5721, 4404/4499 (open: the lazy entries' bigger
    methods, with a fault block, cost compile time in a 2 s run).
    Conformance (7d927f7b, flock -s, --jobs 2): V8Sharp.Tests 1208/1208;
    mjsunit default 1 newly failing, regress-1236560 (TIMEOUT, known);
    mjsunit forced 4 newly failing: regress-1236560, regress-crbug-808192
    and unicode-case-overoptimization0 (TIMEOUTs, known under load) and
    regress-331074427 (worker OOM-killed by the session's memory cgroup
    while another job held 5 GB; passes alone, as does
    unicode-case-overoptimization0). An earlier default run at 491e52b5
    had regress-1189077 (huge strings) OOM-killed the same way; it passes
    alone. test262 default and forced 0 newly failing (95123 run).
    Open, ranked:
    1. RyuJIT's prolog zero-init of big direct entries: every JSValue
       local and activation is a GC struct and must be zeroed; in
       Richards' HandlerTask.run 26% of the samples are the 864-byte
       zeroing of the lazy entry. Fewer, narrower locals (reuse of
       temporaries across blocks, untagged locals for int/double values)
       is the lever; SkipLocalsInit does not cover GC refs.
    2. Allocation and GC (RayTrace, EarleyBoyer, Splay): see "Inlined
       allocation and escape analysis".
    3. Hot-loop code quality (spills, deopt exits): the other agent's
       pass.
    4. (Measured 2026-10-10, see "Maglev coverage of large programs": the
       measured iterations of Typescript, Gameboy, Box2D and EarleyBoyer
       run 97-99% in Maglev code; what keeps them slow is generic and
       megamorphic code inside Maglev and GC.)
    5. Class constructors and derived constructors have no direct entry
       (frameful construct path; no Octane benchmark spends measurable time
       there); calls with more than six arguments and ForInPrepare now keep
       the lazy entry with a written register window.
  - Maglev coverage of large programs (2026-10-10, c39c1120..3ab91b5d).
    Measured with `V8SHARP_TIER_PROFILE=1` (TierProfiler: 1 ms samples of
    the measured iterations only) and `V8SHARP_MAGLEV_COUNT_GENERIC=2`
    (generic builtin calls of Maglev code by site). Warm tier shares
    (interpreter / baseline / Maglev, octane-steady, before -> after):
    Typescript 0.1/0.1/99.8 -> 0.1/0.1/99.7, PdfJS 11.3/4.1/84.5 ->
    7.8/3.5/88.7, Gameboy 2.6/0.3/97.2 -> 1.6/0.8/97.6, Box2D 2.5/0.1/97.4
    -> 2.6/0.0/97.4, CodeLoad 62.9/37.0/0.1 -> 67.6/32.2/0.1, EarleyBoyer
    1.2/1.5/97.3 -> 2.1/1.3/96.6. The hot code of these programs was
    already in Maglev; what is below Maglev is the harness (bm.run,
    Measure, NotifyResult), PdfJS's teardown and CodeLoad's freshly
    evaluated code (parse and first runs: inherent, as in V8). No Octane
    function hits a Maglev bailout, and there are no deopts in the measured
    iterations (the OSR early exits and feedback deopts of the warm-up
    match V8). What was slow was generic code inside Maglev:
    - Megamorphic keyed loads and stores missed to the runtime on every
      access (Typescript: 84K keyed load and 33K keyed store IC misses per
      iteration, now ~0): KeyedLoadIC.LoadGeneric and
      KeyedStoreIC.StoreGeneric (KeyedLoadICGeneric, KeyedStoreGeneric).
    - Lazy entries with a written register window (calls with more than six
      arguments, ForInPrepare, register-range builtins) instead of frameful
      entries; for-in fast paths (ForInPrepare/ForInNext with the enum
      cache, keyed loads by the enumerated key).
    - instanceof without the generic call (HasInPrototypeChain; EarleyBoyer:
      35M OrdinaryHasInstance calls), `F.prototype` folded (RayTrace's
      Class.create), recursion check of the inliner limited to the current
      unit as V8 (RayTrace: 727K non-inlined constructs), keyed loads of
      string characters (PdfJS), getters and setters on the prototype
      chain (Box2D), parseInt of integers (Box2D), String.fromCharCode
      (PdfJS, 1.3M calls per 4 iterations), typed array arms in polymorphic
      element loads and stores (PdfJS's decrypt, Gameboy's getTypedArray),
      megamorphic keyed stores into holes and array appends without the
      runtime (Crypto's bnpSquareTo).
    Per-change octane-quick (bench-session.sh, interleaved, load ~2, noise
    about 15% per run): A = 8203504e (before), D = cc02e3be (+ keyed
    generic ICs, lazy windows, for-in, instanceof, prototype fold, inliner
    recursion rule, string characters), F = 9d0e005c (+ accessors, parseInt,
    fromCharCode, polymorphic typed arrays, keyed store holes); 3 runs,
    session-20261010-060337: Typescript 32.0 / 35.3 / 35.7 (+12%), PdfJS
    752 / 811 / 814 (+8%), Crypto 739 / 645 / 760, EarleyBoyer 282 / 282 /
    266 (one outlier run of F; medians 293 / 282 / 281), RayTrace 1273 /
    1384 / 1666 (+31%), Box2D 1165 / 1162 / 1211 (+4%), Gameboy 549 / 538
    / 581 (+6%), geomean +7.7%; V8 --jit is 4.4x (Typescript) to 8.8x
    (PdfJS) faster. Crypto, RayTrace and Richards from C = d92cdb5e (before
    instanceof .. string characters), 5 runs: Crypto 676 / 679 / 662,
    RayTrace 908 / 1543 / 1386 (+53% from the inliner's recursion rule and
    the prototype fold), Richards 3396 / 3405 / 3372.
    octane-steady, parity publish of 3ab91b5d, 2 runs (session
    bench-20261010-073856; load 3-5, idle 98%, cpu-cal 1.9 s): geomean
    1929 vs V8 --no-turbofan 5138 and V8 --jit 5052 (V8Sharp at 38% of
    V8's Maglev); per benchmark vs v8:maglev: Richards 1863/6338,
    DeltaBlue 2182/6882, Crypto 277/778, RayTrace 1123/2664, EarleyBoyer
    157/654, RegExp 211/776, Splay 2207/8184, NavierStokes 1529/1730, PdfJS
    1256/6696, Mandreel 510/3344, Gameboy 1552/4887, CodeLoad 2916/3416,
    Box2D 3305/14101, zlib 910/1667, Typescript 496/2071. Conformance after
    merging main 8f390cd2 (9f53f81b; flock -s, --jobs 2): V8Sharp.Tests
    1250/1250; mjsunit default and forced 0 newly failing (forced:
    regress-331074427 crashed under load, passes alone 3/3); test262
    default and forced 0 newly failing (95123 run).
    Remaining generic code in Maglev, ranked by calls per
    measured iteration: Typescript's megamorphic named stores in the AST
    constructors (9.5M SetNamedProperty through the stub cache, as V8) and
    megamorphic calls of its AST walker table (as V8); Gameboy's opcode
    table calls (megamorphic, as V8); EarleyBoyer's strict equality with
    kAny feedback (4.8M, generic in V8 too); PdfJS's StaInArrayLiteral
    (345K, V8 builds it from the literal's element feedback); getters and
    setters in polymorphic accesses and own accessor pairs (V8 builds
    them); class and derived constructors still have no direct entry (no
    Octane time).
    GC pauses are 10-17% of Typescript, PdfJS, Gameboy and EarleyBoyer (the
    allocation work).
  - Hot loop code quality (2026-10-09, 5aca1629..; V8 files:
    maglev-code-generator.cc (deferred code), maglev-ir.cc
    (HandleNoHeapWritesInterrupt, TransitionElementsKind), maglev-graph-builder.cc
    (MarkPossibleSideEffect, TryBuildPropertyCellLoad, BuildLoadTypedArrayLength,
    EnsureWritableFastElements), maglev-truncation.cc). Method: RyuJIT's
    disassembly of the generated methods (DOTNET_JitDisasm='*maglev:am3*',
    DOTNET_JitStdOutFile) and perf with perf maps (DOTNET_PerfMapEnabled=1,
    DOTNET_EnableWriteXorExecute=0; the samples' offsets in a method against
    its disassembly). What forced the spills:
    - Block weights. RyuJIT knows nothing of how often a branch is taken: its
      profile synthesis gives a branch out of a loop 10% and others 48%, so a
      loop with a few checks looked barely hotter than its deopt exits, and
      LSRA kept the loop's values in stack slots (am3's loop: every value in
      memory; with the exit's spills removed, all in registers). Deopt exits
      are now cold: a try region of their own whose spill chains end in a
      throw (likelihood 0), caught by the region's handler, which returns.
    - Calls on the back edge: the interrupt check's call (with the bytecode
      offset store and the push of lazily pushed inlined frames on every
      iteration) made the loop's values live across a call. Loops without
      calls now exit to the interpreter for an interrupt they cannot defer
      (kInterrupt, the code stays valid) and leave code installation pending;
      loops with calls keep the call, with the offset store and the frame
      pushes on its slow path. Leaf code with loops gets frameless entries.
    - Address exposure: CheckedObjectToIndex's slow path took the int scratch
      local by reference, which kept it in memory in the whole method.
    - Graph-level: elements kind transitions cleared everything they could
      not (Crypto's am3 inlined in montReduce transitioned, checked maps and
      reloaded both arrays per element); mutable globals and typed array
      lengths were loaded per access; a Float64 add of int32 values whose
      uses all truncate (am3's `+ c` after its feedback overflowed) was a
      double add and a ToInt32; the COW check and CheckInt32IsSmi of masked
      values ran per element store.
    - Indirections: element accesses loaded FixedArray._data per access
      (RyuJIT cannot hoist it past byref stores), typed array accesses
      followed typed array, buffer and backing store per access; both now
      read locals loaded with the elements / the length.
    Per change (octane-quick, bin builds, 3 interleaved runs, a = the
    branch base caed044a; the host was loaded, load 7-17 at the starts):
    cold exits Crypto/NS/Gameboy/Mandreel within noise (a: 543/1698/462/442,
    cold exits: 494/1829/479/468); + interrupt slow path, address exposure,
    transitions (c4): Crypto 603 -> 797 (+32%), NavierStokes 1669 -> 1881
    (+13%), Mandreel 474 -> 530 (+12%); + interrupt exits, frameless loops,
    global and typed array length load elimination (c8): Mandreel 514 ->
    806 (+57%), Crypto 894 -> 783 (within its range, 713-850); + Float64
    truncation, elements and typed array data locals (c12, against c8):
    NavierStokes 1880 -> 2389 (+27%), Mandreel 766 -> 1152 (+50%), PdfJS
    738 -> 911, Gameboy 523 -> 559; Richards and Box2D within noise.
    Exiting for code installation interrupts too sent Richards'
    Scheduler.schedule to the interpreter after every concurrent compile
    (27 exits in 30 runs; fixed before measuring).
    octane-steady at e0d9478a (warm, compile time excluded), parity
    publishes (R2R composite, self-contained) of caed044a and e0d9478a, 3
    interleaved runs, two sessions: load 2.4/2.3 and 2.3/2.9, steal 0%,
    idle 99%, cpu-cal 1710/1791 and 1711/1834 ms, mem-bw 20.0/20.1 and
    18.5/19.4 GB/s; V8 crashed (exit 139) on 6 benchmark/engine runs,
    their means are over the other runs; latency rows out:

    | benchmark | caed044a | e0d9478a | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 1358 | 1250 | 6382 | 8850 |
    | DeltaBlue | 1543 | 1622 | 7092 | 12161 |
    | Crypto | 274 | 461 | 741 | 1544 |
    | RayTrace | 617 | 609 | 3155 | 4429 |
    | EarleyBoyer | 131 | 133 | 660 | 857 |
    | RegExp | 221 | 221 | 776 | 862 |
    | Splay | 2353 | 2595 | 8752 | 7584 |
    | NavierStokes | 1586 | 1787 | 1698 | 2873 |
    | PdfJS | 1258 | 1039 | 6302 | 6444 |
    | Mandreel | 463 | 1093 | 3345 | 4464 |
    | Gameboy | 1518 | 1680 | 4742 | 5523 |
    | CodeLoad | 3111 | 2900 | 3163 | 3147 |
    | Box2D | 2879 | 3062 | 13362 | 15395 |
    | zlib | 948 | 975 | 1577 | 1659 |
    | Typescript | 404 | 379 | 2126 | 2190 |
    | geomean | 867 | 953 | 2954 | 3698 |

    Warm geomean +10%: 25.8% of v8:jit (from 23.4%), 32.3% of v8:maglev.
    NavierStokes is above v8:maglev. PdfJS -17% here did not reproduce: a
    second session (4 runs) gave 1272 vs 1225 (-4%, within noise), with
    the cold exits off 1332 vs 1265, with the data locals off 1296 vs 1285.
    Final, merged with main 7e2b5fd6 (calls and frames) at 07d0d079;
    octane-steady, parity publishes of 7e2b5fd6 and 07d0d079, 3 interleaved
    runs, two sessions: load 1.8/2.0 and 1.6/3.0, steal 0%, idle 97-98%,
    cpu-cal 1837/1615 and 1676/1757 ms, mem-bw 18.9/18.1 and 19.7/18.7
    GB/s; V8 crashed (exit 139) on 5 benchmark/engine runs, their means are
    over the other runs; latency rows out:

    | benchmark | main 7e2b5fd6 | 07d0d079 | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 1745 | 1578 | 6281 | 8789 |
    | DeltaBlue | 2242 | 2155 | 7841 | 12214 |
    | Crypto | 295 | 447 | 749 | 1652 |
    | RayTrace | 716 | 704 | 2946 | 4946 |
    | EarleyBoyer | 162 | 159 | 667 | 883 |
    | RegExp | 222 | 219 | 815 | 832 |
    | Splay | 2776 | 2714 | 8705 | 7430 |
    | NavierStokes | 1404 | 1681 | 1787 | 2856 |
    | PdfJS | 1161 | 1206 | 6095 | 6114 |
    | Mandreel | 499 | 784 | 2768 | 4569 |
    | Gameboy | 1526 | 1915 | 5183 | 5390 |
    | CodeLoad | 2927 | 2743 | 3231 | 3291 |
    | Box2D | 2712 | 2930 | 11237 | 15942 |
    | zlib | 919 | 907 | 1592 | 1424 |
    | Typescript | 435 | 460 | 1968 | 2039 |
    | geomean | 927 | 1002 | 2911 | 3685 |

    Warm geomean +8.0% over main: 27.2% of v8:jit (from 25.2%), 34.4% of
    v8:maglev. Richards -10% is within its bimodal range (see above).
    Cold Octane (start-up, wall clock, same publishes, 3 interleaved runs,
    load 2.0/3.1, steal 0%, cpu-cal 1760/1870 ms, mem-bw 17.6/18.8 GB/s;
    V8 crashed on 8 benchmark/engine runs): geomean (latencies in) 4038 ->
    4187 (+3.7%; v8:maglev 18743, v8:jit 24375); Mandreel 2475 -> 3588,
    Crypto 3087 -> 4115, Box2D 3398 -> 3855, NavierStokes 12181 -> 13207,
    DeltaBlue 2200 -> 2490; EarleyBoyer 4081 -> 3225 and Splay 2854 -> 2391
    (cold noise; their warm rows are equal).
    Toward 50% of v8:jit warm, by share of the log gap: RayTrace x7.0,
    Mandreel x5.8, DeltaBlue x5.7, Richards x5.6, EarleyBoyer x5.5, Box2D
    x5.4 (9-10% each), PdfJS x5.1, Typescript x4.4, RegExp x3.8, Crypto
    x3.7, Gameboy x2.8, Splay x2.7, NavierStokes x1.7, zlib x1.6, CodeLoad
    x1.2.
    Conformance (532e3832, after merging main 7e2b5fd6; flock -s, --jobs 2,
    with other agents' conformance runs on the host, load up to 26):
    V8Sharp.Tests 1220/1220; test262 default and forced 0 newly failing
    (95123 run each); mjsunit default 1 newly failing, regress-crbug-808192
    (TIMEOUT, known load-sensitive); mjsunit forced 3 newly failing,
    regress-crbug-808192, regress-484904778 and wasm/compare-exchange-stress
    (TIMEOUTs; the last two pass alone, as do regress-crbug-740398 and
    regress-crbug-854299, flaky in the run).
    Open, for hot loops: RyuJIT's prolog zeroing of JSValue locals (every
    local holding an object reference is zeroed, 864 bytes per call in
    Richards' HandlerTask.run: fewer and narrower locals, untagged locals
    where the value is a number, no struct locals); register pressure in
    big methods with inlined loops (Crypto's montReduce/bnpSquareTo: frames
    of 900+ bytes, phi moves through memory); two bounds checks per element
    access (the JS length and the .NET array's, which RyuJIT cannot
    elide); overflow checks as 64-bit compares (add.ovf would throw);
    int32 elements are doubles in JSValue (a conversion per load and
    store); the interrupt check reads StackGuard's flags through the
    isolate (two loads per back edge).
  - Compile pipeline and tier-up (2026-10-04, f93817c5..dbf2af5b):
    concurrent jobs (MaglevConcurrentDispatcher.cs, maglev-concurrent-
    dispatcher.cc) build the graph on two worker threads (the main thread
    keeps PrepareJob and FinalizeJob: Box2D from bin, 2067 ms of main-thread
    compile time to 68 ms); feedback pairs under a seqlock, commit against
    a dependency invalidation log, no heap writes from the worker; deopt
    exits share spill chains with one scratch slot per value (exit IL of
    zlib's biggest function 434 KB -> 33 KB); fallthrough to the next block
    and fused compare branches (Box2D: 11% less IL, 15% fewer blocks); the
    direct call entry compiled on the worker too; no graph or IL limits
    beyond V8's bytecode limits (MinOpts for methods over RyuJIT's limits);
    queued jobs of ticking functions move to the front
    (concurrent_recompilation_front_running); TieringManager with V8's
    tiering_in_progress / osr_tiering_in_progress states, Maglev OSR budget
    and urgency, cached tiering decisions (kEarlyMaglev, kDelayMaglev) and
    NotifyICChanged's profile-guided part; no deopt count limit (V8's;
    Gameboy's executeIteration hit the old limit of 8 in every run). Per-change cold Octane (parity
    publishes, 3 interleaved runs, bench-session.sh):
    - worker graph building + 2 workers (f93817c5 vs 57d945ce; load 6.1 ->
      5.3, my own test run overlapped): Box2D 1159 -> 2030, Gameboy 2543 ->
      4286, PdfJS +2%, Typescript +1%, Richards equal; with the graph built on
      the main thread (same build, V8SHARP_MAGLEV_GRAPH_ON_MAIN_THREAD=1)
      Box2D 1398, Gameboy 2975.
    - spill chains (a74d8ed2 vs f93817c5; started at load 18, ended at 1):
      Richards +20%, PdfJS +13%, zlib +8%, Box2D/Gameboy within noise.
    - no size limits (same build, V8SHARP_MAGLEV_MAX_NODES/MAX_IL lifted):
      zlib 4145 -> 7314 (+76%), Typescript +11%, RegExp -4% (noise).
    - front running (bdfa42d7's parent vs the same build with
      --no-concurrent-recompilation-front-running; idle host): Box2D +20%,
      Gameboy +38%, Typescript +5%, PdfJS -4%, Richards -6%.
    The ReadyToRun composite publish precompiles the compiler: a cold Box2D
    run JITs one V8Sharp.Maglev method at tier 0 (36 are rejitted at tier 1
    in the background, 6 AggressiveOptimization helpers are FullOpts).
    Conformance (dbf2af5b, flock -s, --jobs 2; the same at bdfa42d7): mjsunit default 0 newly
    failing (7602 run); mjsunit forced optimization 1 newly failing,
    regress/regress-1236560 (TIMEOUT in the runner's worker, 0.3 s in the
    shell; also times out with 57d945ce on this host, so not from this
    work); test262 default and forced 0 newly failing (95123 run); no
    worker exceptions (V8SHARP_MAGLEV_REPORT_BACKGROUND_EXCEPTIONS to a
    file).
    Open: RyuJIT is the bottleneck (about 70% of a job; time grows with
    basic blocks, about a quarter of them CheckMaps on values not known to
    be receivers: null, instance type and map compares); jobs still wait
    hundreds of ms for a worker at start-up; methods over RyuJIT's limits
    (Typescript's Scanner.innerScan, 3800 blocks) get MinOpts, where a
    compact form (out-of-line checks, as the baseline tier's chunks and
    out-of-line paths) could keep them FullOpts; the OSR check runs at
    budget interrupts, not every JumpLoop; JumpLoop OSR into code installed
    meanwhile relies on the install zeroing the budget.
    Cold Octane at f0b0a4cf (octane, wall-clock scores, 3 interleaved runs,
    parity publishes, bench-session.sh; load 5.9/4.1, steal 0%, cpu-cal
    1749/1792 ms, mem-bw 29.1/33.7 GB/s; V8 crashed (exit 139) once on a few
    rows, means over the remaining runs); "before" is 57d945ce:

    | benchmark | v8sharp before | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 2377 | 2644 | 24415 | 35455 |
    | DeltaBlue | 3206 | 3350 | 35170 | 70813 |
    | Crypto | 5556 | 5189 | 23623 | 35560 |
    | RayTrace | 2732 | 3001 | 39650 | 67586 |
    | EarleyBoyer | 4716 | 4846 | 32745 | 44673 |
    | RegExp | 1313 | 1294 | 6786 | 7082 |
    | Splay | 4163 | 4345 | 7221 | 7635 |
    | SplayLatency | 2652 | 2631 | 5180 | 5410 |
    | NavierStokes | 13619 | 13304 | 20249 | 29414 |
    | PdfJS | 2898 | 3128 | 31103 | 33282 |
    | Mandreel | 2655 | 3490 | 26176 | 37100 |
    | MandreelLatency | 6628 | 6354 | 36773 | 45323 |
    | Gameboy | 5803 | 7519 | 72898 | 77245 |
    | CodeLoad | 8996 | 8765 | 17482 | 17665 |
    | Box2D | 2294 | 5746 | 70748 | 84257 |
    | zlib | 3820 | 7250 | 73707 | 75072 |
    | Typescript | 6551 | 7781 | 53583 | 56183 |
    | geomean | 3990 | 4614 | 26372 | 32930 |

    Cold geomean +16% (17.5% of V8 --no-turbofan, 14.0% of V8).
    octane-steady at f0b0a4cf (the warm protocol of e9b062be: 2x warm-up,
    background compiles waited for and installed; thread CPU, 2 interleaved
    runs; load 2.5/3.3, steal 0%, cpu-cal 1767/1690 ms, mem-bw 34.2/33.2
    GB/s; V8 crashed (exit 139) on a few rows, means over the remaining
    runs; latency rows omitted):

    | benchmark | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|
    | Richards | 1129 | 8945 | 10506 |
    | DeltaBlue | 1311 | 12040 | 20317 |
    | Crypto | 306 | 1394 | 2123 |
    | RayTrace | 334 | 3871 | 6487 |
    | EarleyBoyer | 160 | 919 | 1169 |
    | RegExp | 238 | 1126 | 1158 |
    | Splay | 2861 | 12212 | 12320 |
    | NavierStokes | 1535 | 2149 | 3063 |
    | PdfJS | 1456 | 9302 | 9537 |
    | Mandreel | 623 | 4311 | 5933 |
    | Gameboy | 1431 | 7809 | 8312 |
    | CodeLoad | 3627 | 4318 | 4376 |
    | Box2D | 2932 | 19333 | 21362 |
    | zlib | 178 | 1823 | 1852 |
    | Typescript | 485 | 2767 | 2845 |
    | geomean | 792 | 4180 | 5044 |

    Warm geomean: 18.9% of V8 --no-turbofan.
  - Maglev on by default (2026-10-04, cea743c4; V8Sharp.Bench `compare`,
    2 interleaved runs, parity publish (R2R composite, self-contained),
    bench-session.sh under the lock; V8 is the 14.7 oracle; "default" is
    V8Sharp's default configuration, now Ignition + baseline IL + Maglev).
    Cold Octane (wall): host load 2.3/1.4, steal 0%, cpu-cal 1726/1783 ms,
    mem-bw 32.2/33.9 GB/s; v8:maglev crashed (exit 139) once each on
    RegExp, Splay, NavierStokes, PdfJS and zlib (means over one run):

    | benchmark | v8sharp:jitless | v8sharp:sparkplug | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|---|
    | Richards | 805 | 897 | 2385 | 24462 | 35486 |
    | DeltaBlue | 740 | 801 | 3050 | 36041 | 69526 |
    | Crypto | 702 | 690 | 5051 | 24680 | 36070 |
    | RayTrace | 1629 | 1822 | 2874 | 41698 | 73074 |
    | EarleyBoyer | 2646 | 2780 | 4827 | 32028 | 42710 |
    | RegExp | 1039 | 1254 | 1284 | 6568 | 7084 |
    | Splay | 2315 | 2403 | 4320 | 7782 | 7385 |
    | SplayLatency | 2687 | 2715 | 2655 | 5629 | 5528 |
    | NavierStokes | 1707 | 2530 | 15098 | 20640 | 30583 |
    | PdfJS | 2834 | 3077 | 3434 | 32782 | 37118 |
    | Mandreel | 558 | 497 | 2881 | 27476 | 37867 |
    | MandreelLatency | 3076 | 3360 | 6738 | 38824 | 45124 |
    | Gameboy | 3487 | 3734 | 6338 | 77165 | 79214 |
    | CodeLoad | 10094 | 9583 | 9456 | 18312 | 18076 |
    | Box2D | 2719 | 2298 | 2365 | 77301 | 83622 |
    | zlib | 881 | 902 | 3837 | 76720 | 76657 |
    | Typescript | 7615 | 6746 | 7221 | 59820 | 61098 |
    | geomean | 1914 | 2002 | 4130 | 27545 | 33571 |

    octane-steady (thread CPU after a warm pass of 1/50 of Octane's
    iterations; two sessions, load 7.4/1.7 and 1.6/5.4, steal 0-1%, cpu-cal
    1789/1738 and 1699/1776 ms, mem-bw 33.5/33.6 and 27.5/32.4 GB/s; V8
    crashed once on Richards (maglev), Crypto (both), RegExp (both) and
    CodeLoad (jit); latency rows omitted):

    | benchmark | v8sharp:jitless | v8sharp (default) | v8:maglev | v8:jit |
    |---|---|---|---|---|
    | Richards | 476 | 1603 | 11435 | 14899 |
    | DeltaBlue | 425 | 1490 | 15142 | 20695 |
    | Crypto | 55 | 426 | 1816 | 2658 |
    | RayTrace | 227 | 420 | 5137 | 7490 |
    | EarleyBoyer | 94 | 206 | 1245 | 1577 |
    | RegExp | 233 | 301 | 1527 | 1577 |
    | Splay | 3588 | 2845 | 16667 | 15746 |
    | NavierStokes | 239 | 2053 | 2955 | 4155 |
    | PdfJS | 811 | 1093 | 8393 | 8074 |
    | Mandreel | 84 | 504 | 4341 | 5832 |
    | Gameboy | 358 | 1280 | 8781 | 7746 |
    | CodeLoad | 3156 | 2896 | 4035 | 4066 |
    | Box2D | 706 | 704 | 15923 | 17381 |
    | zlib | 19 | 89 | 1838 | 1855 |
    | Typescript | 311 | 257 | 2416 | 2260 |

    The default configuration is 2.06x the baseline-only configuration on
    cold Octane (ahead on every benchmark but CodeLoad, -1%, and
    SplayLatency, -2%) and 15% of V8 --no-turbofan. The steady scores of
    the big benchmarks (Box2D, Typescript, CodeLoad, PdfJS) measure tier-up
    more than optimized code: the warm pass is 1/50 of Octane's work, and
    Box2D's score moves 806-1420 between single runs.
  - Conformance with Maglev on by default (2026-10-04, cea743c4): test262
    0 newly failing (95123 run, also with --maglev before the switch);
    mjsunit 0 newly failing after the expectations took the six tests that
    fail with the optimizing tier (code-coverage-block-opt,
    elide-double-hole-check-12, es6/super-ic-opt, regress/regress-2618,
    turbolev/holey-double-load-arith, turboshaft/regress-380487911, with
    reasons) and dropped two maglev/ tests that now pass. Under forced
    optimization at cea743c4: mjsunit 0 newly failing, 0 newly passing
    (7565 run; two large-allocation regress tests crashed once and passed
    on rerun).
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
  - On by default since 2026-10-04 (conformance-clean under forced
    optimization but for the six listed below, and a net win on
    octane-steady and cold Octane; see "Maglev on by default").
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
