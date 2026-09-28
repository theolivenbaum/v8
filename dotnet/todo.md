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
- [x] Golden bytecode test harness (`bytecode_expectations/*.golden`): parser,
      printer, assembler; waits for the BytecodeGenerator to compile snippets

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
- [ ] objects: strings (table, flattening, hashing, compare), symbols,
      Map/descriptors/transitions, PropertyDetails, FieldIndex, JSObject
      fast/dictionary properties, elements kinds and accessors, LookupIterator,
      property descriptors, keys (KeyAccumulator), ordered hash tables
- [ ] Isolate, Factory, roots, StringTable, MessageTemplate, error creation,
      stack traces (CallSiteInfo, Error.captureStackTrace, prepareStackTrace)
- [ ] conversions and operators (Object::ToNumber, ToPrimitive, Equals,
      StrictEquals, Compare, arithmetic helpers)
- [ ] bootstrapper/Genesis: native context, intrinsics in V8's install order
- [x] interpreter: bytecodes, operands, array builder/writer, register
      optimizer, constant array builder, handler tables, control-flow builders,
      decoder, iterators, source-position table (292 tests; 100 golden files
      round-trip). Open: embedded operation hints, feedback-kind checks.
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

## Known deviations

(Every intentional behaviour difference from V8, with the reason.)

- Oracle version: V8 14.7 with ICU vs. this tree 15.6 without ICU. Tests whose
  expectations differ for that reason are listed in the runner's
  `oracle-deviations` file.
- No Smi/HeapNumber distinction in `JSValue`; `IsSmi` is computed from the
  value (architecture.md section 3).
- `Register` default value is r0, not V8's invalid register; use
  `Register.InvalidValue()`.
- Bytecode verifier (sandbox) not ported; `Disassemble` prints offsets, not
  addresses.
- TODO(merge) from the Ignition port: `InterpreterCommon.cs` duplicates Token,
  LanguageMode and other shared enums; `RuntimeFunctionId` and
  `NativeContextFields` move to the runtime/objects code; the constant pool is
  `object[]` until heap constants exist.
