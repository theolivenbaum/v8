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
- [ ] TestRunner: `.status` files, `// Flags:`, mjsunit harness, test262
      frontmatter and includes, in-process isolates with watchdog, both engines
- [ ] Baseline results of the oracle on mjsunit / test262 (expected-pass lists)
- [ ] d8sharp shell: `print`, `load`, `read`, `quit`, `version`, `-e`, `--flags`,
      `%` natives, `d8.*` test helpers used by mjsunit
- [ ] Golden bytecode test harness (`bytecode_expectations/*.golden`)

## Phase 1: the unoptimized pipeline

### V8Sharp.Base
- [ ] `src/base/numbers`: diy-fp, cached-powers, fast-dtoa, fixed-dtoa,
      bignum, bignum-dtoa, dtoa, strtod (+ unittests)
- [ ] `src/numbers/conversions`: StringToDouble/Int/BigInt, DoubleToCString,
      DoubleToFixed/Exponential/Precision/Radix, DoubleToInt32 ... (+ unittests)
- [ ] `src/bigint`: digit arithmetic, mul (schoolbook/karatsuba/toom/fft),
      div (schoolbook/burnikel/barrett), tostring, fromstring, bitwise (+ unittests)
- [ ] `src/strings/unicode*` (unibrow case mapping, utf8/utf16),
      `char-predicates` (ID_Start/ID_Continue via .NET Unicode data)
- [ ] hashing (`src/strings/string-hasher`, `src/base/hashing`), `bits`, `ieee754`

### V8Sharp.Parsing
- [ ] tokens, keywords, scanner, character streams, literal buffer
- [ ] AstValueFactory, AST nodes, AstNodeFactory
- [ ] scopes, variables, ScopeInfo-independent analysis
- [ ] parser-base, parser, preparser, expression scopes, func-name inferrer,
      rewriter, pending compilation errors
- [ ] tests: scanner, parsing, preparser, ast-value, scanner-streams

### V8Sharp.RegExp
- [ ] regexp parser, AST, flags
- [ ] compiler (tonode, compiler), bytecode generator, peephole
- [ ] bytecode interpreter; experimental (linear) engine
- [ ] case folding / unicode sets without ICU
- [ ] tests: regexp-unittest

### V8Sharp (engine)
- [x] objects: strings (table, flattening, hashing, compare, ConsStringIterator,
      StringCharacterStream), symbols, Map/descriptors/transitions, MapUpdater,
      PropertyDetails, FieldIndex, JSObject fast/dictionary properties, elements
      kinds and accessors, LookupIterator, property descriptors, keys
      (KeyAccumulator), hash tables, ordered hash tables.
      Tests: object-unittest, test-field-type-tracking, dictionary-unittest,
      hashcode-unittest, test-orderedhashtable (OrderedHashMap/Set),
      test-strings (non-JS), elements-kind-unittest, test-transitions.
      Missing: interceptors and access checks, shared/Atomics elements,
      SmallOrderedHashTable, the JS-running tests of those files
      (StoreToConstantField_*, HoleyHeapNumber, the JS parts of test-strings)
- [~] Isolate, Factory, roots, StringTable, MessageTemplate, error creation,
      stack traces (CallSiteInfo, Error.captureStackTrace, prepareStackTrace).
      Done: Isolate, Factory, roots, StringTable, error creation, CallSiteInfo,
      protectors. Missing: the Error builtins (captureStackTrace,
      prepareStackTrace callers) belong to the builtins port
- [x] conversions and operators (Object::ToNumber, ToPrimitive, Equals,
      StrictEquals, Compare, arithmetic helpers)
- [~] bootstrapper/Genesis: native context, intrinsics in V8's install order.
      Done: Object, Function, Array, Number, Boolean, String, Symbol, Date,
      Promise, RegExp, Errors, JSON, Math, Map/Set/WeakMap/WeakSet/WeakRef/
      FinalizationRegistry, BigInt, Iterator and helpers, Proxy, Reflect,
      bound/wrapped functions, arguments maps, API functions
      (FunctionTemplateInfo, HandleApiCallOrConstruct). Missing: Intl, Temporal,
      ArrayBuffer/SharedArrayBuffer/Atomics, typed arrays, DataView,
      DisposableStack, shared structs, RegExpMatchInfo, extras, extensions,
      the TemplateLiteral map (interpreter port)
- [ ] interpreter: bytecodes, operands, array builder/writer, register
      optimizer, constant array builder, handler tables, control-flow builders
- [ ] bytecode generator (matches golden files)
- [ ] feedback vectors and ICs (load/store/keyed/global/call/binary op/compare)
- [ ] interpreter dispatch loop, generators, async functions
- [ ] runtime functions (`%` intrinsics used by bytecode and by mjsunit)
- [ ] builtins: Object, Function, Array, String, Number, Boolean, Symbol,
      Math, JSON, Error, RegExp, Date, Map/Set/WeakMap/WeakSet/WeakRef/
      FinalizationRegistry, Promise, generators/iterators, Proxy/Reflect,
      ArrayBuffer/TypedArray/DataView/Atomics, BigInt, globalThis functions,
      Iterator helpers, DisposableStack
- [ ] modules (import/export, dynamic import, top-level await)
- [ ] eval / new Function / with

## Phase 2: the fast tiers

- [ ] TieringManager: interrupt budget, OSR triggers (port of tiering-manager.cc)
- [ ] Baseline compiler: bytecode -> IL (Sparkplug analogue)
- [ ] Optimizing compiler: SSA graph from bytecode + feedback, speculative
      representations, inlining, deoptimizer (Maglev analogue)
- [ ] SIMD fast paths: elements accessors, string search, typed arrays
- [ ] Benchmarks: test/js-perf-test, JetStream-like, against the oracle

## Merge cleanups

Stand-ins in the engine that go away when the component they wait for merges
(each marked `TODO(merge)` at the site):

- `src/V8Sharp/Numbers/Conversions.Temp.cs`: delete, use V8Sharp.Base.Numbers.
- `src/V8Sharp/Common/MessageTemplate.Temp.cs`: delete, use V8Sharp.Parsing's
  MessageTemplate.
- `src/V8Sharp/Common/ParsingGlobals.Temp.cs`: delete, use V8Sharp.Parsing's
  Common/Globals.cs.
- `src/V8Sharp/Objects/BigIntOps.cs`: replace the System.Numerics bridge with
  V8Sharp.Base.BigInts.
- `src/V8Sharp/Objects/HashTable.cs` (`Hashing`) and
  `src/V8Sharp/Strings/StringHasher.cs`: use V8Sharp.Base's hashing
  (rapidhash with the isolate's hash seed).
- `src/V8Sharp/Objects/String.cs` (`CharPredicates`): use V8Sharp.Base's
  char-predicates.
- `src/V8Sharp/Objects/ScopeInfo.cs`: implement V8Sharp.Parsing's IScopeInfo.
- `src/V8Sharp/Objects/JSFunction.cs`: type the bytecode as
  V8Sharp.Interpreter.BytecodeArray.
- `src/V8Sharp/Objects/JSObjectShapes.cs`: JSRegExp data as V8Sharp.RegExp's
  type; module namespace as the module system's Module.

## Known deviations

(Moved to `deviations.md`; the engine's are under "V8Sharp engine: objects and
execution".)


(Every intentional behaviour difference from V8, with the reason.)

- Oracle version: V8 14.7 with ICU vs. this tree 15.6 without ICU. Tests whose
  expectations differ for that reason are listed in the runner's
  `oracle-deviations` file.
- No Smi/HeapNumber distinction in `JSValue`; `IsSmi` is computed from the
  value (architecture.md section 3).
