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
- [ ] Performance: the correctly rounded Math functions (ieee754) take
      0.25-0.7 us per call on their double-double fast path, 20-40x the
      platform libm; a table-driven kernel like llvm-libc's would close most
      of that.

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
- [ ] native backends: an IL-emitting RegExpMacroAssembler (tier-up)
- [ ] TODO(merge): switch Unicode/*.cs to the shared unibrow port in
      V8Sharp.Base once it lands

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
- RegExp: V8 uses ICU for case closure (/ui, /vi), Unicode property escapes
  and \p{...} of strings. V8Sharp.RegExp emulates the needed ICU calls
  (UnicodeSet closeOver with simple case folding, property lookups by exact
  alias, empty sets rejected like ICU) over tables generated from UCD 17.0
  and emoji 17.0 (`Unicode/UnicodeTables.g.cs`), instead of "no ICU".
- RegExp: a subject counts as one-byte when all its code units are <= 0xFF
  (V8 decides by string representation); callers that know the
  representation pass it to `CompiledRegExp.Exec`. Results are identical;
  only which bytecode runs differs.
- RegExp: no interrupt/stack-guard polling in the bytecode interpreter or the
  NFA interpreter (no isolate), so they never return RETRY; the backtrack
  stack is limited to V8's 64 MB (EXCEPTION on overflow).
- RegExp: `AddNonBmpSurrogatePairs` emits its grouped alternatives in
  insertion order where V8 iterates a ZoneUnorderedMap; the alternatives match
  disjoint code points, so only code order differs.
- RegExp differential tests: the oracle (V8 14.7) predates the lookbehind
  alternative-sorting fix (regress-regexp-lookbehind-sort-alternatives.js)
  and Unicode/Emoji 17 (new scripts Beria_Erfe, Sidetic, Tai_Yo, Tolong_Siki,
  U+0295 now Ll, Extended_Pictographic changes); those mismatches are
  classified as known in the test.
- Number to string: V8 finds the shortest digits with dragonbox
  (third_party/dragonbox, not checked out); `DoubleToCString` uses the port
  of `DoubleToAscii(SHORTEST)` (Grisu3 with the bignum fallback), which
  yields the same digits. Checked against the oracle.
- String to number: V8 parses decimal literals with fast_float
  (third_party/fast_float, not checked out); `StringToDouble` parses the same
  grammar itself and converts with the port of `base::Strtod` (correctly
  rounded, as fast_float is). Checked against the oracle.
- `ConversionFlag` is a `[Flags]` enum with separate hex, octal, binary,
  implicit-octal and trailing-junk bits (the API V8Sharp's callers asked
  for); V8's has three values, `NO_CONVERSION_FLAG`,
  `ALLOW_NON_DECIMAL_PREFIX` (= `AllowHex | AllowOctal | AllowBinary` here)
  and `ALLOW_TRAILING_JUNK`. `AllowImplicitOctal` keeps the legacy "0777"
  handling that V8 now only offers through `ImplicitOctalStringToDouble`.
- Math functions: `base::ieee754` takes acos, asin, atan, atan2, cos, sin,
  tan, exp, expm1, log, log1p, log2, log10, cbrt and `legacy::pow` from
  llvm-libc, whose sources are not in this checkout. llvm-libc's double
  functions are correctly rounded, so V8Sharp computes the correctly rounded
  result its own way (double-double with Ziv's rounding test, BigInteger
  fallback; `Ieee754.CorrectlyRounded.cs`). The results match; checked
  against 130-digit references. The oracle (14.7) still used fdlibm and
  differs by up to one ulp.
- `tanh` and `math::pow` (with `--use-std-math-pow`, the default) are the
  platform's `tanh`/`pow` in V8 too; V8Sharp calls `Math.Tanh`/`Math.Pow`,
  which are the same C library functions.
- `acosh`/`atanh` return a signaling NaN in V8 for out-of-range arguments;
  V8Sharp returns the ordinary NaN (JavaScript cannot tell them apart).
- V8's fatal `CHECK`s in Base (e.g. `RandomNumberGenerator::NextSample`,
  BigInt size limits) throw exceptions instead of aborting the process.
- `RandomNumberGenerator()` without an entropy source seeds from
  `System.Security.Cryptography.RandomNumberGenerator` rather than reading
  /dev/urandom directly (the same OS source).
- String hashing: `V8_ENABLE_SEEDED_ARRAY_INDEX_HASH` (off by default in
  V8) is not ported.
- `CharPredicates`: V8 answers non-Latin-1 identifier and white-space
  questions from ICU; V8Sharp uses .NET's Unicode data plus the
  Other_ID_Start/Other_ID_Continue and Pattern_* lists. Checked over every
  code point against the oracle.
