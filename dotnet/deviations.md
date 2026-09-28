# Deviations from V8

Every place where V8Sharp knowingly differs from V8 (this tree, see
`UPSTREAM.md`): what V8 does, what V8Sharp does instead, and why. The code
at each deviation carries a short comment pointing here. An entry is either
an observable difference, or a structural one (a different algorithm or data
source producing the same results) that a reader diffing C# against C++
would otherwise mistake for an unfinished or wrong port.

Replacing V8's heap/GC, handles, snapshot, machine-code backends and ICU is
by design (`docs/architecture.md` section 2) and is not repeated per site.

Status: some items below are provisional (marked "provisional"): accepted
for now, to be revisited when the reason goes away.

## General

- Oracle version: V8 14.7 with ICU vs. this tree 15.6 without ICU. Tests whose
  outcome on the oracle differs from the `.status` files (version skew, ICU,
  d8 features the ClearScript host lacks) are listed in
  `tools/V8Sharp.TestRunner/expectations/<suite>.oracle.txt`; the README there
  classifies them.
- No Smi/HeapNumber distinction in `JSValue`; `IsSmi` is computed from the
  value (architecture.md section 3).

## V8Sharp.Base (numbers, math, unicode, hashing)

- Provisional. Third-party sources: dragonbox, fast_float and llvm-libc are not in this
  checkout (third_party/ holds only their BUILD.gn/README.v8), and fetching
  them was not permitted in the porting session. The three items below are
  therefore implementations of the same published algorithms rather than
  transcriptions; each is held bit for bit to an exact reference in the
  tests. Revisit them once the sources can be read.
- Number to string: V8 finds the shortest digits with dragonbox's
  `to_decimal`; `ShortestDecimal` implements Schubfach, the algorithm
  dragonbox derives from, which returns the same digits (shortest, closest,
  ties to even, trailing zeros removed). Subnormals with a significand below
  1000 take `DoubleToAscii(SHORTEST)`. `SignificandToChars` and the rest of
  `DoubleToStringView` are ported from conversions.cc. Checked against
  `DoubleToAscii` on 1.4 million doubles and against the oracle.
- String to number: V8 parses decimal literals with fast_float; `FastFloat`
  implements its Clinger fast path and the Eisel-Lemire algorithm (up to 19
  digits, and more when the truncation cannot matter), and falls back to
  the port of `base::Strtod` where fast_float uses its big-integer
  comparison and in the rare cases the implementation cannot bound (within
  two units of a rounding boundary, subnormal or near-overflow results).
  All paths are correctly rounded; checked against `Strtod` on 420000
  strings including exact midpoints, and against the oracle.
- `ConversionFlag` is a `[Flags]` enum with separate hex, octal, binary,
  implicit-octal and trailing-junk bits (the API V8Sharp's callers asked
  for); V8's has three values, `NO_CONVERSION_FLAG`,
  `ALLOW_NON_DECIMAL_PREFIX` (= `AllowHex | AllowOctal | AllowBinary` here)
  and `ALLOW_TRAILING_JUNK`. `AllowImplicitOctal` keeps the legacy "0777"
  handling that V8 now only offers through `ImplicitOctalStringToDouble`.
- Math functions: `base::ieee754` takes acos, asin, atan, atan2, cos, sin,
  tan, exp, expm1, log, log1p, log2, log10, cbrt and `legacy::pow` from
  llvm-libc. llvm-libc's double functions are correctly rounded, so V8Sharp
  computes the correctly rounded result with its own table-driven kernels in
  llvm-libc's style (`Ieee754.Kernels.cs`: table reduction, short
  polynomial, Ziv's rounding test), falling back to a double-double and then
  a BigInteger evaluation (`Ieee754.FastPath.cs`,
  `Ieee754.CorrectlyRounded.cs`) when the rounding is undecided. The results
  match: the kernels agree with the BigInteger reference on 12 million
  arguments, which agrees with 130-digit references. The oracle (14.7)
  still used fdlibm and differs by up to one ulp.
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

## V8Sharp.Parsing

- Parsing: regexp literal syntax is validated only when the engine injects
  an `IRegExpSyntaxValidator` (`ParseInfo.set_regexp_syntax_validator`);
  the engine must wire V8Sharp.RegExp in (390 test262 early-error tests).
- Parsing: `VariableMap` iterates in insertion order (V8: hash order); only
  visible in which duplicate name some messages mention, and debug printing.
- Parsing: no UTF-8 / Windows-1252 / streaming character streams; one managed
  `PreparseData` class for V8's zone and heap forms (release byte format);
  flags passed per parse (`ParsingFlags`) instead of global.
- Parsing: AstPrinter/ScopePrinter are always compiled (DEBUG-only in V8).
- Parsing: decorators (`@`) not scanned; V8's status lists those tests as FAIL.

## V8Sharp.RegExp

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

## Interpreter (Ignition)

- `Register` default value is r0, not V8's invalid register; use
  `Register.InvalidValue()`.
- Bytecode verifier (sandbox) not ported; `Disassemble` prints offsets, not
  addresses.

## V8Sharp engine: objects and execution

Heap and object model
- Read-only roots are process-wide static objects (`ReadOnlyRoots`), shared by
  every isolate, instead of a per-isolate read-only space. Accessor infos
  (`Accessors`) are shared the same way.
- Objects keep their fields in one `JSValue[]` rather than V8's in-object slots
  plus a PropertyArray. `FieldIndex` still encodes V8's (in-object, offset)
  split so `LoadByFieldIndex` and the descriptor encodings match. Header and
  instance sizes are approximated from `JSObject.GetHeaderSize`, and builtin
  function instance sizes are recomputed from the header size plus in-object
  count (`Bootstrapper.CreateFunctionForBuiltinWithPrototype`).
- The identity hash lives in a dedicated field on JSReceiver, not in
  `properties_or_hash`.
- No Smi/HeapNumber distinction: numbers are unboxed. A non-Smi number does not
  fit `Representation.HeapObject` (`ObjectOps.FitsRepresentation`), where V8's
  HeapNumber does; storing one generalizes the field to Tagged instead.
- Strings are always UTF-16 (`SeqString`); there is no one-byte storage, no
  external strings and no ThinString (a string remembers its internalized copy
  in `InternalizedForward`). `StringCharacterStream` walks .NET strings.
- The special prototype instance types (`JS_OBJECT_PROTOTYPE_TYPE`,
  `JS_PROMISE_PROTOTYPE_TYPE`, ...) are set on the map only;
  `HeapObject.InstanceType` stays the generic type.
- The string table is a `Dictionary` keyed by content, not V8's open-addressed
  table with forwarding indices.
- No allocation mementos or allocation sites feedback on literals.

Weakness (no GC hooks)
- Transition targets, `FieldType.Class` maps, prototype-user registries, the
  map cache and the normalized map cache hold their entries strongly. Nothing
  is cleared, so maps that V8 would collect stay reachable.
- WeakMap/WeakSet use `ConditionalWeakTable`; WeakRef uses a CLR `WeakReference`.
  FinalizationRegistry cleanup scheduling is not ported.

Execution
- JavaScript exceptions are .NET exceptions (`JavaScriptException`); `Isolate.Throw*`
  never return. There is no pending-exception slot to check.
- Flags live in a per-isolate `FlagList`, not a process-global `v8_flags`, so
  tests can run isolates with different flags in one process.
- Protectors are plain booleans on the isolate, not PropertyCells.
- CallSiteInfo is captured eagerly as objects rather than a raw frame array
  (`Isolate.CaptureSimpleStackTrace`).
- The `Builtin` enum includes the Torque builtins; implementations are
  registered by id in `BuiltinRegistry`. Calling an unregistered builtin throws
  `NotImplementedException`.
- Interceptors, access checks, API templates beyond `FunctionTemplateInfo` and
  signatures are not ported (no embedder API).
- `ElementsAccessor` uses virtual dispatch instead of CRTP; shared-array and
  Atomics entry points are not ported.
- `KeyAccumulator` does not use the prototype-info enum cache.
- Hash tables use local copies of V8's hashers (`Hashing.ComputeSeededHash`
  and friends) until V8Sharp.Base lands.
- `BigInt` operations go through `System.Numerics.BigInteger`
  (`BigIntOps.cs`) until the port of `src/bigint` lands.

Bootstrapper
- No snapshot: `Bootstrapper.CreateEnvironment` builds every native context
  from scratch with Genesis, in V8's order.
- Not installed yet: Intl, Temporal, ArrayBuffer/SharedArrayBuffer/Atomics,
  typed arrays, DataView, DisposableStack, shared structs, extras and
  extensions, RegExpMatchInfo.
- The error stack getter and setter are JSFunctions created eagerly per native
  context (`NativeContext.ErrorStackGetterFun`/`ErrorStackSetterFun`), not
  FunctionTemplateInfo roots instantiated lazily.
- The empty function uses the bootstrapping ScopeInfo.
- `V8_FUNCTION_ARGUMENTS_CALLER_ARE_OWN_PROPS` is off, as in V8's default build.
