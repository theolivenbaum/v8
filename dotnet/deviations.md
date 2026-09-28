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
- RegExp native tier: RegExpMacroAssemblerIL has no CheckPreemption at
  backtracks and no JS stack guard check in the prologue (no isolate), like
  the interpreter above. Positions are absolute char indices where x64 keeps
  negative byte offsets from the subject end; backtrack targets are label ids
  dispatched by an IL `switch` where x64 pushes code offsets and jumps
  indirectly; registers past 1024 live in a per-execution int[] instead of
  the frame.
- RegExp native tier: the SIMD scans (SkipUntilChar/CharOrChar/CharAnd/
  BitInTable, under x64's *UseSimd predicates) call a helper over
  `IndexOfAny(SearchValues<char>)` instead of emitting SSE code; it stops
  exactly where the scalar loop would, so V8's scalar tail never runs.
  SkipUntilOneOfMasked(3) use the portable lowering. Back references
  (including the LATIN1 case-insensitive one x64 inlines) and range arrays
  call C# helpers.
- RegExp tiering: the V8 flags --regexp-interpret-all, --regexp-tier-up and
  --regexp-tier-up-ticks are static fields (RegExpEngine.s_regexp*) that
  RegExpEngine.Compile snapshots into the regexp (RegExpTierPolicy, also a
  Compile parameter), where V8 reads the global flags at each exec. This lets
  tests run tiers side by side; with unchanged flags the behaviour is V8's.
  `CompiledRegExp.Exec` fills a multi-match register span only for global
  regexps (V8 never passes one otherwise; native code would stop after one
  match where the interpreter keeps going).
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
- Bytecode generator: RAII helper scopes (`ControlScope` and subclasses,
  `RegisterAllocationScope`, `ContextScope`, `HoleCheckElisionScope`, ...)
  are `IDisposable` classes or ref structs used with `using`; the
  `ExpressionResultScope` kinds (effect, value, test) are one pooled class
  recycled through a per-generator free list instead of stack objects. The
  `BuildTryCatch`/`BuildTryFinally` lambdas are C# delegates. Same bytecode.
- Bytecode generator: heap objects are data descriptions behind
  `IBytecodeGeneratorHeap` (`SharedFunctionInfoDescription`,
  `ObjectBoilerplateDescriptionData`, `ArrayBoilerplateDescriptionData`,
  `TemplateObjectDescriptionData`, `ClassBoilerplateDescription`,
  `FixedArrayDescription`, `CoverageInfoDescription`); V8 allocates them in
  `AllocateDeferredConstants`/`FinalizeBytecode`. Provisional until the object
  model implements the interface. `ClassBoilerplate::New` is not ported: the
  class boilerplate is the class literal itself.
- Bytecode generator: `AddToEagerLiteralsIfEager` ignores
  `should_parallel_compile()`: V8 posts those literals to the lazy compile
  dispatcher, which V8Sharp does not have. The parser only marks literals for
  parallel compile under flags V8Sharp leaves off.
- Golden bytecode tests (`GoldenBytecodeCompiler`, `GoldenFunctionResolver`):
  generate-bytecode-expectations runs the script and fetches the global test
  function (or the callee); the port has no interpreter yet, so the harness
  compiles the whole script eagerly and finds the function with a small
  static evaluator (declarations, assignments, `new C().m`, `C.prototype.m`,
  `C.m`, calls returning functions, `arguments.callee`, the `.result`
  completion). A direct eval of a string literal is compiled in the harness
  against a managed ScopeInfo, eagerly, where V8 uses `--lazy-eval`; the
  bytecode is the same for the golden snippets.
- Constant briefs in `Disassemble` (`<ScopeInfo>`, `<ClassBoilerplate>`, long
  `<BigInt ...>`) do not print what V8's heap printer prints (scope type,
  truncated digits), because there is no heap object behind them.
- Oracle comparison (`OracleBytecodeGeneratorTest`): V8 14.7 differs from this
  tree in feedback slots (14.7 has slots where this tree embeds feedback),
  the TDZ hole bytecodes (renamed `*TdzHole` here), `typeof x == "literal"`
  (TypeOf + compare in 14.7, TestTypeOf here), the `yield` result intrinsic
  (`_GeneratorYieldResult` here), cross-closure TDZ check elision (here only),
  an unused `.result` register 14.7 reserves in scripts with lexical
  declarations, and the Smi range of the ClearScript build (32-bit Smis). The
  test normalizes the first two and lists the rest per function.

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

Bootstrapper
- No snapshot: `Bootstrapper.CreateEnvironment` builds every native context
  from scratch with Genesis, in V8's order.
- Not installed yet: Intl, Temporal, ArrayBuffer/SharedArrayBuffer/Atomics,
  typed arrays, DataView, DisposableStack, shared structs, extras and
  extensions. The RegExpMatchInfo of a native context is created on first use
  (`RegExpMatchInfo.Get`), not by InitializeGlobal.
- The error stack getter and setter are JSFunctions created eagerly per native
  context (`NativeContext.ErrorStackGetterFun`/`ErrorStackSetterFun`), not
  FunctionTemplateInfo roots instantiated lazily.
- The empty function uses the bootstrapping ScopeInfo.
- `V8_FUNCTION_ARGUMENTS_CALLER_ARE_OWN_PROPS` is off, as in V8's default build.

## String and RegExp builtins

- Strings are UTF-16 only, so the one-byte fast paths of the builtins (and
  String::IsOneByteRepresentationUnderneath for choosing irregexp's LATIN1
  code) decide by content: `JSRegExp.IsOneByteSubject` scans the subject
  (vectorized) and remembers the answer for the last two subjects per thread.
- Results are built flat where V8 builds cons strings (StringRepeat, padStart/
  padEnd, replaceAll, the global replace paths append into an
  IncrementalStringBuilder instead of a ReplacementStringBuilder parts array);
  the strings are equal, only the representation differs.
- `StringSearch` (string-search.h) uses the vectorized
  `MemoryExtensions.IndexOf`/`LastIndexOf` instead of V8's linear/BMH/BM
  strategies; the positions found are the same.
- The regexp compilation cache is a per-isolate dictionary by (source, flags),
  cleared at 4096 entries, instead of V8's generational CompilationCache table.
- The results caches (regexp::ResultsCache, ResultsCache_MatchGlobalAtom) and
  the case-mapping caches are per isolate / per thread and are never cleared
  by GC. Cached arrays are shared copy-on-write, as in V8.
- RegExpMatchInfo::ReserveCaptures grows the match info in place instead of
  allocating a larger one and storing it on the native context.
- PrototypeCheckAssembler: the constness check (map identity plus const
  descriptors) additionally compares the property values with the native
  context's originals, so a store that bypassed constness tracking cannot
  keep a modified prototype on the fast path. The identity fallback reads
  the value through the descriptor's field index.
- The experimental-engine flags of V8Sharp.RegExp are process-wide statics;
  JSRegExp.Compile copies the isolate's flags into them before compiling. The
  tiering flags (--regexp-tier-up, --regexp-interpret-all, --jitless) are
  snapshotted per compiled regexp.
- The global exec loop (GlobalExecRunner, RegExpExecInternal_Batched) asks the
  engine for a batch of matches in every tier; V8's interpreter does one match
  per call. V8Sharp.RegExp fills the batch in all tiers, so results are the
  same.
- localeCompare, normalize, toLocaleUpperCase/LowerCase follow V8's
  !V8_INTL_SUPPORT paths (code-unit order, form validation only, unibrow
  case mapping of code units). The oracle has ICU; its results differ there.

## Builtins: Object, Function, Reflect, Proxy, global, Error, Boolean, Symbol

- Object.assign: the CSA fast path that clones the source's layout into a
  fresh empty target through the object_assign side-step transition is not
  ported; JSReceiver::SetOrCopyDataProperties with its FastAssign descriptor
  walk (V8's runtime fast path) handles every source.
- Object.values/entries: V8's CSA FastGetOwnValuesOrEntries and the runtime
  fast path are one path here (JSReceiver::GetOwnValuesOrEntries with
  try_fast_path for fast-mode JSObjects).
- Object.fromEntries: the fast path checks every [key, value] pair of the
  fast array before creating properties; V8 creates them as it goes and on a
  bail-out restarts on the slow path with a fresh object. The result is the
  same (the pairs are read without side effects).
- Object.groupBy: the groups are a Dictionary keyed by the internalized
  property key plus an insertion-ordered list (V8: an OrderedHashMap of
  ArrayLists); the fast array path reads elements with GetElement instead of
  the FastJSArrayForRead witness.
- The iteration helpers these builtins need (GetIterator, IteratorStep,
  IteratorCloseOnException, IterableToListWithSymbolLookup) are a local
  `IteratorHelpers` class until the iterator builtins are ported.
- CreateDynamicFunction and GlobalEval: no embedder callbacks
  (ModifyCodeGenerationFromStrings, IsCodeLike), and Builtins::
  AllowDynamicFunction is always true (one embedder, no differing security
  tokens). Compilation goes through `Isolate.DynamicFunctionCompiler`
  (Compiler::GetFunctionFromString / GetFunctionFromValidatedString).
  Map::AsLanguageMode builds the strict derived function map each time
  instead of caching it as a strict_function_transition_symbol transition.
- Function.prototype.apply / Reflect.apply / Reflect.construct: the fast
  elements of an arguments object or fast JSArray are copied into a pooled
  buffer (V8 pushes them on the machine stack).
- The proxy trap builtins (ProxyGetProperty, ProxySetProperty, ...,
  CallProxy, ConstructProxy) are not registered: every caller reaches the
  trap logic through JSProxy. The proxy_revoke_shared_fun root is created on
  first use per isolate.
- Uri (src/strings/uri.cc): one UTF-16 buffer instead of V8's one-byte and
  two-byte buffers; the strings produced are the same.
- CallSite methods: no ShadowRealm boundary checks (ShadowRealm is not
  ported). getScriptHash computes the SHA-256 on each call (V8 caches it on
  the script) and never returns "" for opaque origins (not modelled).
  getThis returns undefined for a receiver that is still the hole.
- Error.isError has no API-wrapper (DOMException) case.
- The global parseInt/parseFloat are Number.parseInt/parseFloat (one
  function, as in V8); their builtins (NumberParseInt, NumberParseFloat) are
  registered by the global functions' area.
- `Isolate.CountUsage` is a no-op (no use counters).

## Builtins: Number, Math, BigInt, JSON, Date

Number and Math
- Math.random: V8's Genesis calls MathRandom::InitializeContext; here the
  state object of the native context (MATH_RANDOM_STATE_INDEX) is created on
  first use. The isolate's random number generator (seeded from
  --random-seed or the OS) lives in a table keyed by the isolate.
- Math.sumPrecise: IterableForEach (builtins-iterator-inl.h) is ported
  without the typed-array fast path (no typed arrays in the object model)
  and the JSSetIterator medium fast path; both only skip the iteration
  protocol when its lookup chain is intact, so the generic path gives the
  same result. Visitors are a struct interface instead of lambdas.
- The Float64* machine operations are V8Sharp.Base's Ieee754 kernels
  (correctly rounded, like the llvm-libc functions this tree uses); the
  oracle (14.7, fdlibm) can differ by one ulp (see V8Sharp.Base).

BigInt
- MutableBigInt is the BigInt under construction (digits array plus a
  length that Canonicalize trims) instead of a separate heap type; a
  canonical 0n instance is shared (V8 allocates one per result; BigInt
  identity is not observable).
- MutableBigInt_AbsoluteModAndCanonicalize's cached-divisor fast path
  (heap->cached_bigint_divisor) is not used: the modulus always takes
  ModuloSmall/ModuloLarge (same result).
- The isolate's bigint::Processor is kept in a table keyed by the isolate;
  it polls TerminateExecution like V8's.

JSON
- Strings are UTF-16: the parser is V8's two-byte instantiation, and the
  string scan uses SearchValues (V8: Highway on one-byte strings).
- JSON.parse builds objects with CreateDataProperty in source order
  (elements first) instead of JSDataObjectBuilder with the previous array
  element's map as feedback, and without the recursive ParseJsonValueRecursive
  / numeric-array fast path (one iterative parser for all inputs). Keys,
  order, values, duplicate handling and elements kinds of arrays are the
  same; only backing-store choices (e.g. dictionary elements) can differ.
- JSON.parse internalizes only property keys; V8 also internalizes short
  one-byte values within a heuristic budget. Not observable.
- JSON.parse always passes the context argument to the reviver; V8 skips
  collecting source text when the reviver can only access fewer than three
  formal parameters (unobservable by such a reviver).
- JSON.stringify: FastJsonStringifier (a side-effect-free serializer that
  restarts in JsonStringifier when it gives up) is not ported; the output is
  the same. JsonStringifier always keeps the cycle-detection stack (V8
  starts without it and restarts with one when needed) and has no
  SimplePropertyKeyCache. Output goes into one pooled UTF-16 buffer.
- A proxy in the prototype chain counts as "may have interesting
  properties" (the toJSON lookup is always done for it).

Date
- The time zone without ICU: V8 asks localtime_r (tm_gmtoff, tm_isdst,
  tm_zone); V8Sharp asks TimeZoneInfo.Local, which honours TZ and reads the
  same tz database on Linux. DaylightSavingsOffset is one hour when the
  instant is in DST and the local offset is the current standard offset, as
  in V8's non-ICU build (so historical offset changes, 30-minute DST and LMT
  are not reproduced, exactly like non-ICU V8). The zone name printed by
  toString is TimeZoneInfo's StandardName/DaylightName, which are the tz
  abbreviations tm_zone reports ("EST", "BST", "IST"), except that UTC zones
  print "UTC"/"GMT" (.NET says "Coordinated Universal Time") and historical
  abbreviations (LMT, EWT) are not available.
- The oracle has ICU: its toString prints long names ("Coordinated Universal
  Time", "Eastern Standard Time") and it applies historical offsets, so the
  differential tests strip the name and compare local-time results only for
  1971..2037 outside UTC. Europe/Moscow 2011-2014 (+4) is a known difference
  (non-ICU V8 uses the current +3, as V8's own comment in
  DateCache::GetLocalOffsetFromOS says).
- DateParser is the two-byte instantiation; the kLegacyDateParser use count
  is also reported through an out parameter of DateParser.Parse (for the
  port of DateParseLegacyUseCounter; Isolate.CountUsage is a no-op).
- The isolate's DateCache, date cache stamp and DateTimeConfigurationChange-
  Notification are a partial Isolate in Date/TimezoneCache.cs.
