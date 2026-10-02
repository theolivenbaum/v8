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
  value (architecture.md section 3). So `%IsSmi(%AllocateHeapNumberWithValue(1))`
  is true (mjsunit call-intrinsic-fuzzing fails on it).

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
- Parsing: the character streams read a `char[]`, not the source string on
  the heap (V8's OnHeapStream). A source of 4096 characters or more is copied
  once and the copy kept beside the string (a ConditionalWeakTable in
  `ScannerStream`), so each lazy compile of one of its functions does not copy
  the script again (it did: a large-object allocation per lazy compile, 10% of
  Octane CodeLoad).
- Parsing: stack_limit_ is a budget of 4 x --stack-size bytes of .NET stack
  from where each parser starts (V8: the isolate's C stack limit). The .NET
  parser frames are about four times V8's, so the RangeError comes at about
  V8's nesting depth (2997 nested parentheses vs V8's 2296 at the default
  984 KB, 2208 array literals vs 3100). The stack position is the address
  of a local (`Unsafe.ByteOffset` from the null ref, no unsafe context).
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
- RegExp native tier: code whose IL exceeds 256 KB
  (`RegExpMacroAssemblerIL.kMaxILSize`) is not handed to the JIT; the regexp
  is compiled to bytecode instead and never tiers up. V8 compiles every
  irregexp to native code (no size limit short of kMaxRegisterCount), but
  RyuJIT's compile time grows superlinearly with method size: 200 KB of IL
  take 90 ms, the 2048 named groups of an alternation (655 KB) 1.8 s, and
  the 8192 of mjsunit regress-980891 36 s (2.5 s now, parsing and bytecode
  included).
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

## Baseline compiler (Sparkplug) and tiering

- Temporary: `--sparkplug` is off by default (V8's x64 default is on), so
  V8Sharp runs the interpreter only unless `--sparkplug` or
  `--always-sparkplug` is passed. Baseline code is faster than the
  interpreter once compiled (1.2-1.8x on the long-running Octane
  benchmarks), but compiling costs far more than V8's Sparkplug: RyuJIT
  takes about 3-6 us per byte of IL, about 10 us per byte of bytecode
  (2.5-3 s of background CPU for PdfJS or Box2D), so short programs on a
  machine without idle cores run slower than in the interpreter. Turn it
  back on in FlagList.Generated.cs when the compile cost is down.
- Code generation: IL in a static method of a dynamic assembly per function
  instead of machine code (architecture.md section 9.1). The assembly is not
  collectible (RyuJIT does not tier collectible code), so baseline code is
  never freed, where V8 collects Code objects; no bytecode offset table: the current
  bytecode offset is stored in the frame record before each bytecode that can
  throw or call (`BaselineAssembler.StoreBytecodeOffset`), so the frame walker
  and handler lookup work as for interpreted frames.
- Exception handlers and OSR entries: the code is entered at a bytecode offset
  through a dispatch at the method start (handlers, loop headers, 0), not at a
  machine pc. `BaselineExecution.Run` re-enters it after the handler lookup.
- Baseline code lives on `SharedFunctionInfo.BaselineCode` beside the
  BytecodeArray (V8 replaces function_data with the Code object). A JSFunction
  has no code field: every closure of a SharedFunctionInfo with baseline code
  runs it (V8 updates each closure's code at its next call through
  CompileLazy/InstallBaselineCode, with the same effect).
- OSR from Ignition: V8 checks for baseline code on every JumpLoop and
  tail-calls InterpreterOnStackReplacement_ToBaseline, entering at the
  JumpLoop's pc; V8Sharp does the check after the back edge and enters at the
  loop header (the same bytecode runs next). The max_arguments stack check
  before OSR is not needed (arguments are not pushed on a machine stack).
- `--concurrent-sparkplug` (on, as in V8 on x64): one process-wide
  background thread (`BaselineCompileThread`) compiles every isolate's
  batches, where V8 posts jobs to the platform's worker pool. The task
  generates the IL and has RyuJIT compile it fully optimized
  (`AggressiveOptimization`, `RuntimeHelpers.PrepareMethod`) off the main
  thread; the main thread only materializes the constant pool and the name
  before, and installs the code at the next INSTALL_BASELINE_CODE interrupt.
  The batch queue holds the closures (weakly), not their SharedFunctionInfos.
  Without concurrency (`--no-concurrent-sparkplug`, `--always-sparkplug`,
  `%CompileBaseline`) the IL is generated on the main thread and RyuJIT
  compiles the method at its first call, at tier 0 first. The builtins and
  call paths baseline code calls are `AggressiveOptimization` in both modes:
  at tier 0 they ran slower than the interpreter's own (optimized) loop.
- Calls: a call from baseline code to a function with baseline code (and no
  exception handlers) enters it directly, C# call to C# call, through
  `BaselineCalls.Enter` with the arguments in registers or locals
  (`ICallArguments`), pushing the frame record and the register window
  itself. V8 calls through the Call builtins and the callee's prologue
  builtin. The baseline frame's teardown is not in a `finally`: a throw
  unwinds C# frames to the catching frame, which restores the frame stack
  (as the interpreter's handler lookup does). `Function.prototype.call` and
  `apply` with a JSFunction target enter the target without a builtin
  frame (V8 has a builtin frame for them; it is not observable in stack
  traces, which skip it). The JS stack limit is checked every fourth call
  depth (every call for functions with more than 1024 bytes of bytecode),
  not at every entry; the .NET stack is large enough for the slack.
- Registers: in functions without exception handlers or generator
  resumption, up to 64 interpreter registers live in IL locals; the frame
  copy is written only where something reads it (register lists passed to
  calls and runtime functions, the interrupt budget's runtime call on a back
  edge) and reloaded where a builtin writes it (header of
  BaselineCompiler.Registers.cs). V8's Sparkplug keeps every register in the
  frame.
- Inline fast paths: V8's Sparkplug code calls the same builtins as Ignition
  for every bytecode; V8Sharp emits the hot paths (Smi and number
  arithmetic, comparisons fused with the following conditional jump,
  ToBoolean, monomorphic named and keyed loads and stores, global loads from
  a PropertyCell, context slots) directly as IL, with the out-of-line
  builtin as the slow path, and chooses per bytecode from the feedback at
  compile time which paths to emit (header of BaselineCompiler.Feedback.cs).
  The emitted code records the same feedback as the interpreter.
- Compact code and size limit: a function whose inline code would exceed
  RyuJIT's optimization limits (it would compile it with MinOpts) is
  compiled again with calls to the builtins only. Functions with more than
  5000 bytes of bytecode do not tier up (batch compilation,
  `--always-sparkplug`) and stay interpreted unless compiled explicitly with
  `%CompileBaseline` (`BaselineSupport.TiersUpToBaseline`); V8 tiers up any
  size.
- Without `--maglev` (V8Sharp's default) `Isolate.UseOptimizer` is false, so
  `TieringManager` behaves as in a V8 built without Turbofan and Maglev
  (`%GetOptimizationStatus` reports lite mode and never-optimize, plus the
  baseline bits). The interrupt budget after tier-up is
  `invocation_count_for_turbofan` x bytecode length, as V8 computes it; the
  ticks only raise it.
- `BaselineBuiltins`: V8's baseline code calls the same builtins as Ignition's
  handlers; V8Sharp's builtins are C# methods in Baseline/ that repeat the
  glue of the interpreter's dispatch-loop cases (register windows, feedback
  collection) around the shared helpers, so the interpreter needs no
  refactoring. The slow paths of the inline code
  (BaselineBuiltins.SlowPaths.cs) are `NoInlining`, so RyuJIT keeps them out
  of the baseline methods.
- `%CompileBaseline` on a non-user function, or when Sparkplug is disabled,
  throws an InvalidOperationException (V8: CHECK failure).

## Optimizing compiler (Maglev) and deoptimizer

- Temporary: `--maglev` is off by default (V8's x64 default is on) until the
  tier is conformance-clean under forced optimization and a net win
  (`todo.md`). `Isolate.UseOptimizer` is `--maglev && !--jitless`.
- Code generation: IL in the baseline code space instead of machine code
  (architecture.md section 9.2); values live in IL locals rather than
  registers and stack slots, so there is no register allocator (RyuJIT
  allocates) and no safepoint table. The code is never freed (the assembly
  is not collectible); invalidated code is only unreferenced.
- Frames: the optimized frame is the interpreter frame the call built, and
  inlined functions push real interpreter frames (V8 has one optimized frame
  and materializes the inlined ones at deopt and for stack walks). A deopt
  writes the translation's values into these frames instead of building new
  ones. Deopt exits copy the values into a per-isolate scratch buffer.
- Concurrent compilation (`--concurrent-recompilation`, on as in V8): the
  tiering manager's requests build the graph on the main thread (V8 builds it
  on a worker, with the heap broker's snapshot of the heap); the IL
  generation and a fully optimized RyuJIT compile (`AggressiveOptimization`,
  `RuntimeHelpers.PrepareMethod`) run on the process-wide background compile
  thread the baseline tier uses, and the code is installed at the next
  INSTALL_MAGLEV_CODE interrupt. The dependencies are registered when the
  graph is built: an invalidation before the install marks the code and the
  install drops it (V8 validates them at commit). `%OptimizeFunctionOnNextCall`,
  `%OptimizeMaglevOnNextCall` and OSR compile synchronously (RyuJIT tier 0
  first).
- OSR: the check for OSR code runs at the JumpLoop budget interrupt (V8
  checks the OSR urgency on every back edge); OSR code takes the
  interpreter frame's registers as its initial values at the loop header,
  and runs in the same frame. No OSR from a Wide/ExtraWide JumpLoop.
- Bytecode liveness is computed by an iterative fixed point over all
  bytecodes (V8 does one backward pass plus a loop fix-up pass); the result
  is the same.
- Prototype chain checks of property accesses use the IC handler's validity
  cell (CheckValidityCell) and the stable-map dependency, not V8's
  per-holder map checks from the broker's PropertyAccessInfo.
- Generic nodes call the baseline tier's builtins (`BaselineBuiltins`), which
  collect feedback like the interpreter does; V8's generic Maglev nodes call
  builtins that mostly do not. Calls with consecutive argument registers call
  `MaglevCalls.Call` and do not collect feedback, as V8's.
- Protectors are bools (Protectors.cs), not PropertyCells: code depending on
  one registers on a stand-in Cell per protector, invalidated through
  `Protectors.OnInvalidate`.
- `MaglevCompiler.kMaxDeoptCount` (8) eager deopts stop the tiering manager
  from optimizing a function (V8 counts deopts with `--max-deopt-count` per
  feedback vector only for Turbofan and lets Maglev re-optimize). Explicit
  requests (`%OptimizeFunctionOnNextCall`) still compile, as in V8.
- The tiering manager does not optimize functions whose graph exceeds
  `MaglevCompiler.kMaxTieringGraphNodes` (500 nodes, `V8SHARP_MAGLEV_MAX_NODES`
  overrides it): RyuJIT's cost grows with the IL (big graphs exceed its
  MinOpts limits and are jitted without optimization), and big functions are
  mostly straight-line code that runs a few times (Octane's RegExp runBlocks).
  V8 optimizes them. `%OptimizeFunctionOnNextCall` still compiles such
  functions. Measured with fixed work: RegExp and PdfJS gain, the rest is
  within noise.
- Typed array stores: V8Sharp's keyed store IC gives typed arrays a slow
  handler (and goes megamorphic), so the element store is built from the
  feedback maps alone, and megamorphic keyed stores call
  `KeyedStoreICMegamorphic`, which has the typed array fast path of V8's
  KeyedStoreIC_Megamorphic builtin.
- OSR code is invalidated when an exit outside its loop is taken a second
  time (V8 only invalidates it for exits inside the loop): a function whose
  own compile failed would otherwise re-enter the OSR code and deoptimize at
  the same exit on every call.
- Deopt exits are shared by the checks of one frame state; the failed
  check's reason is passed to the Deoptimizer at run time (V8 has one exit
  per check, with the reason in the deopt data).
- Exception handlers: the code body is one .NET try region; a throwing
  node inside a JS try block stores its index in a local, and the region's
  filtered catch clause runs that node's trampoline (the catch block's
  exception phis from the node's frame) and re-enters the region, whose
  first instruction dispatches to the catch block. V8 returns to a handler
  address. Catch blocks are always built: V8 lazy-deopts instead when the
  handler was never used, but the interpreter does not record handler use.
  Calls inside try blocks and functions with handlers are not inlined (V8
  inlines them and drops the inlined frames on a throw).
- Select diamonds of one node: charCodeAt's out-of-bounds NaN
  (`BuiltinStringPrototypeCharCodeAtOrNaN`) and the keyed name check against
  the name's primitive (`CheckValueEqualsString` with the primitive) are one
  node each where V8 builds a branch and a phi.
- No escape analysis (except the arguments object forwarded to
  Function.prototype.apply), loop peeling, LICM, or typed array/DataView/string
  builder reductions yet; generators and async functions are not optimized
  (the compile bails out).

## Interpreter execution, ICs, runtime, compiler and modules

- Dispatch: one C# loop specialized per operand scale
  (`InterpreterExecution.Loop<TS>`) instead of generated handlers. The loop
  keeps only the handlers whose fast path is a few instructions (and the
  monomorphic IC hits: own field and prototype constant loads, field stores
  and field-adding transitions, fast element loads and stores, global
  PropertyCell loads); the others are NoInlining methods in
  InterpreterHandlers.cs, and the rare bytecodes sit in `LoopCold<TS>`, so
  that RyuJIT keeps the accumulator, the current bytecode and the frame
  pointer in registers (it stops promoting structs and inlining in a method
  with too many locals). The current bytecode is a `ref byte` into the
  bytecode array rather than V8's (array, offset) pair, so the loop needs
  one register for it, and the accumulator's number payload is a long
  (JSValue._bits), so the accumulator lives in two callee-saved general
  registers: on System V x64 a double local would sit in a stack slot. The
  offset is computed from the reference where a handler needs it (SavePc,
  the return offset of a call). The loop is AggressiveOptimization (it
  would otherwise run as OSR code). Wide/ExtraWide run one bytecode in the scaled loop, except
  LdaSmi, which the single-scale loop decodes itself.
- JumpLoop's OSR-to-baseline check (InterpreterAssembler::OnStackReplacement,
  case 3) runs only once the isolate has installed baseline code
  (`Isolate.MayHaveBaselineCode`); V8 compiles the check into every JumpLoop
  of a non-jitless build and out of a jitless one.
- The bytecode offset is stored in the frame record only by the handlers
  that call out (`SavePc`, V8's SaveBytecodeOffset), not before every
  bytecode.
- Frames: the register file, receiver, arguments and fixed slots live on the
  isolate's `RegisterStack` (a `JSValue[]`) in V8's layout, not on the machine
  stack; each frame also has an `InterpreterFrameRecord` the stack walker
  reads. The argument count slot (fp - 4) is not written by inline calls
  (nothing reads it). The register stack is sized to `--stack-size` and it
  and the frame records are allocated on the pinned object heap: on the
  large object heap every gen-0 collection took time proportional to their
  size. A popped inline frame's record keeps its Function and Bytecode (every
  push sets both), so a call of the same function at the same depth skips
  those reference stores; they stay reachable until the record is reused or
  an explicit collection (`gc()`, `Isolate.CollectGarbage`) clears the
  records above the live frames. Popping an inline frame writes nothing to
  its record (every push sets all fields).
- The register stack above its top is undefined except below
  `Isolate.RegisterStackDirtyEnd`: a returning inline frame leaves its
  parameters, fixed slots and registers there, and the next inline call at
  that depth compares before each reference store (same closure, context,
  feedback vector, often the same receiver and argument tags), skipping the
  GC write barriers. Its register file is cleared on entry (V8's trampoline
  fills it with undefined). The stale values stay reachable until they are
  overwritten, the frame entered from C# below them returns
  (`ReleaseRegistersAndDirty`), or an explicit collection clears them.
- `InterpreterState` is a `ref struct`: RyuJIT emits no GC write barrier for
  stores through a byref to a byref-like type (it cannot be on the heap).
- Calls: a call or `new` from bytecode to an ordinary compiled bytecode
  function runs in the caller's dispatch loop (`InterpreterInlineCalls`)
  without a .NET frame; generators, async functions, class and derived
  constructors, builtins and wide-operand calls take the ordinary path through
  `Execution`/`InterpreterExecution.Invoke`.
- Builtin calls: a builtin runs under a frame record of kind Builtin (V8's
  builtin frame, so it shows in stack traces), in its function's context,
  with register-stack slots reserved for recursion. A [[Call]] of a leaf
  builtin (Math.*, the String.prototype searching and slicing methods,
  Array.prototype.indexOf/includes/lastIndexOf/at on packed arrays,
  Number.prototype.toString, String(), Number(), parseInt ...) whose receiver
  and arguments are primitives that convert without side effects cannot call
  JavaScript, throw or depend on the realm, so it skips all three
  (Builtins/BuiltinFramelessCalls.cs). The call handlers first try the
  Torque/CSA fast paths of the hottest builtins directly
  (Builtins/BuiltinFastPaths.cs: Math.floor/ceil/round/trunc/abs/sqrt/max/min/
  pow/atan2 on numbers, charCodeAt/charAt on a String and a Smi index,
  toString() of a number, push/pop/shift), without BuiltinArguments.
- Stack limit: V8's limit is on the machine stack; V8Sharp limits the
  register stack to `--stack-size` KB / 8 slots and reserves 16 slots for the
  construct stub, which puts the RangeError at about the recursion depth V8
  reaches (12593 vs 12456 plain calls, 4844 vs 4790 constructs). The .NET
  stack is checked with `TryEnsureSufficientExecutionStack` on ordinary entries.
  Builtins and the JSON serializer run on the .NET stack, which V8Sharp's
  register-stack limit does not see, so a call to a builtin reserves 12
  register slots (`kBuiltinFrameSlots`, about a builtin exit frame) and each
  level of JSON.stringify's recursion 20 (`kSerializeFrameSlots`): recursion
  through them (toString -> join -> toString, a toJSON that stringifies)
  then overflows with a RangeError at about V8's depth instead of the .NET
  stack guard's.
- Exceptions are .NET exceptions (`JavaScriptException`); a frame's handler is
  found in an exception filter, so frames without a handler do not catch and
  rethrow. `Throw`/`ReThrow` dispatch to a handler in the same frame without a
  .NET exception. Termination is `TerminationException`, never catchable.
- Feedback: the embedded binary/compare feedback bytes of this tree's
  bytecode are updated in place in the bytecode array; call counts are bumped
  in place. No ContextCells (the cell and no-cell context slot bytecodes are
  the same load/store; --jitless V8 does not use them either).
- ICs: handlers are C# objects (`LoadHandler`/`StoreHandler`) instead of Smi
  handlers and code; the megamorphic stub cache holds them. `LoadSuperIC` is
  the generic path. No allocation-site pretenuring feedback.
- CloneObjectIC: FastCloneJSObject copies the source's field array and
  elements into an object of the cached result map (V8 copies the in-object
  words and the PropertyArray; V8Sharp has one field array). null and
  undefined have no map in V8Sharp to key feedback on, so cloning them builds
  the empty object without recording feedback (V8 records the Smi 0 handler
  for their maps).
- Runtime: `%` functions are delegates in `RuntimeTable`; functions only an
  optimizing tier or the debugger uses are not registered (their calls throw
  "runtime function %X is not implemented"). Tier queries (%IsTurbofanEnabled,
  %GetOptimizationStatus ...) and %GetFeedback answer as --jitless V8 does.
- The TestRunner's v8sharp engine runs the microtask checkpoint when the
  outermost script execution returns (d8's kAuto policy); a nested
  `Realm.eval` leaves its microtasks queued.
- Compiler: source positions are collected eagerly (no lazy source
  positions); there is no compilation cache and no preparse data (inner
  functions are reparsed); every lazy function has UncompiledData without
  preparse data. `DefineClass` builds the class sequentially from the class
  boilerplate stand-in: the constructor's map gets the length, name and
  prototype AccessorConstant descriptors appended (as the descriptor template
  of AddDescriptorsByTemplate has them, so the map stays fast and
  UseFastFunctionNameLookup holds), and the members are then added through
  ordinary property definitions (fields rather than V8's constant
  descriptors). The template object cache is per SharedFunctionInfo.
- A JSMessageObject whose location is a (SharedFunctionInfo, bytecode offset)
  pair (a stalled top-level await) gets its source position when it is made,
  where V8 computes it on first use (InitializeSourcePositions).
- Async functions and generators follow builtins-async-*-gen.cc; the debugger
  parts (Runtime_DebugAsyncFunctionSuspended's debug events, async stack
  trace annotations for the inspector) are not ported.
- Modules: the SourceTextModuleInfo parts, regular exports/imports and
  requested modules are typed arrays instead of FixedArrays; the embedder API
  (ResolveModuleCallback, SyntheticModuleEvaluationSteps, the dynamic import
  and import.meta callbacks, the source phase ResolveSourceCallback) are
  delegates; the host's dynamic import callback is the phase-taking
  HostImportModuleWithPhaseDynamicallyCallback only. Module source objects
  exist only for WebAssembly, which is not ported, so every source phase
  import fails with d8's SyntaxError. The STACK_CHECK of linking and
  evaluation also requires 32 register-stack slots (the C++ frames of
  Module::Evaluate in V8), so that a deferred module evaluated at the
  recursion limit fails with the RangeError as in V8
  (modules-import-defer-stack-overflow-on-sync-eval). Not ported:
  WebAssembly modules and the code cache in the d8 loader.
- Parser flags: the fuzzing flags reach the parser, and
  `RuntimeFuzzing.IsEnabledForFuzzing` is runtime.cc's allowlist; the
  FOR_EACH_INTRINSIC_TEST list it needs is copied into the parsing assembly
  (the engine's `FunctionId` table lives in V8Sharp, which the parser cannot
  reference).
- Stack traces: async frames are captured as CallSiteInfos with the source
  position already resolved from the generator's suspend offset (V8 stores the
  bytecode offset and resolves lazily).
- `FastAssign` (Objects/JSReceiver.cs) checks the excluded keys of
  CopyDataProperties before reading a value rather than after, so an excluded
  getter is not called; this matches what V8 does (regress-41488094).
- Test natives: `%ConstructThinString` returns a cons string with the same
  contents (V8Sharp has no thin strings), `%DetachGlobal`-like realm operations are no-ops in the
  TestRunner engine, and `RunModule` checks a rejected top-level promise once
  after a microtask checkpoint, like the oracle engine.

## V8Sharp engine: objects and execution

Heap and object model
- Read-only roots are process-wide static objects (`ReadOnlyRoots`), shared by
  every isolate, instead of a per-isolate read-only space. Accessor infos
  (`Accessors`) are shared the same way.
- In-object properties (Objects/JSObjects.InObject.cs): a CLR object cannot
  be sized per allocation, so ordinary objects (the instance types
  `JSObject.UsesInObjectSlots` lists: JS_OBJECT_TYPE, API objects, errors,
  the special prototype types) are allocated from a chain of classes with
  `[InlineArray]` slot segments (1, 2, 3, 4, 8, 12, 16, 32, 64, 128, 256 slots), the
  smallest covering the map's in-object property count. After in-object slack
  tracking shrinks a map, objects allocated earlier keep their larger class
  (V8 turns the tail into filler). Arguments objects (at most two in-object
  properties, no subclasses) derive from the two-slot class. The other
  JSObject subclasses (arrays, functions, regexps, collections ...) keep the map's
  in-object fields at the start of the PropertyArray `JSValue[]`; the map's
  counts, `FieldIndex` and slack tracking are V8's for every object.
  `FieldIndex.StorageIndex` is the physical location the IC handlers cache.
  Header and instance sizes are approximated from `JSObject.GetHeaderSize`,
  and builtin function instance sizes are recomputed from the header size
  plus in-object count (`Bootstrapper.CreateFunctionForBuiltinWithPrototype`).
- The identity hash lives in the header word (`HeapObject._hashField`, which
  is Name's raw hash field for names), not in `properties_or_hash`: a field
  of the root class fills the padding after InstanceType, where a JSReceiver
  field would add 8 bytes to every object. The two spare bytes of that word
  (`HeapObject._headerFlags`) hold JSString's internalized bit, and a
  FixedArray's copy-on-write bit is its unused hash field, for the same reason.
- `properties_or_hash` is one field, `JSReceiver._fields`, as in V8: the
  PropertyArray in fast mode; in dictionary mode an array whose last element
  is the property dictionary (after the in-object area of the classes without
  slots). A dictionary-mode object therefore has one array more than in V8,
  and every receiver one field less than with a separate dictionary field.
- `JSArray.Length` is a property over a `double` field (`_length`): the
  length is always a Number, and the double is 8 bytes smaller than a
  JSValue and written without a GC write barrier.
- Stores into fields, elements and context slots of a value whose reference
  part is unchanged (a number over a number, the same object) write only the
  payload (`JSValue.StoreSlot`): a CLR reference store pays a GC write barrier,
  V8's a Smi store does not.
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
- Allocation mementos: an array created from an AllocationSite (literal copies,
  empty array literals, `new Array` with construct feedback) keeps the site in
  `JSArray.AllocationMementoSite` for its whole life; V8's memento sits behind
  a young object and is gone once the object is promoted, so V8Sharp feeds
  later elements-kind transitions of old arrays back into the site as well.

Weakness (no GC hooks)
- Transition targets, `FieldType.Class` maps, prototype-user registries, the
  map cache and the normalized map cache hold their entries strongly. Nothing
  is cleared, so maps that V8 would collect stay reachable.
- The number-string caches (`SmiStringCache`, `DoubleStringCache`,
  Objects/NumberStringCache.cs) are not flushed by a full GC
  (Heap::FlushNumberStringCache); their strings stay until overwritten.
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
- Interceptors and API templates beyond `FunctionTemplateInfo` and
  signatures are not ported (no embedder API).
- Access checks (`Isolate::MayAccess`, `ReportFailedAccessCheck`,
  `DetachGlobal`) are ported for global proxies only: with no embedder API
  there is no `AccessCheckInfo`, so an access is allowed exactly when the
  security tokens match and a failed check always throws TypeError kNoAccess
  (V8's behaviour without a FailedAccessCheckCallback). `DetachGlobal` does
  not create V8's `global_proxy_for_api` copy.
- `ElementsAccessor` uses virtual dispatch instead of CRTP; shared-array and
  Atomics entry points are not ported.
- `KeyAccumulator` does not use the prototype-info enum cache.
- Hash tables use local copies of V8's hashers (`Hashing.ComputeSeededHash`
  and friends) until V8Sharp.Base lands.

Bootstrapper
- No snapshot: `Bootstrapper.CreateEnvironment` builds every native context
  from scratch with Genesis, in V8's order.
- Not installed: Intl, Temporal, shared structs and the extensions
  other than gc and externalize-string. The extras binding object has only
  what InstallExtrasBindings puts there (isTraceCategoryEnabled, trace). The RegExpMatchInfo of a native context is created on first use
  (`RegExpMatchInfo.Get`), not by InitializeGlobal.
- The error stack getter and setter are JSFunctions created eagerly per native
  context (`NativeContext.ErrorStackGetterFun`/`ErrorStackSetterFun`), not
  FunctionTemplateInfo roots instantiated lazily. They run in their own realm
  (V8's run in the caller's), so the CallSite objects Error.prepareStackTrace
  receives are made in the error's creation context rather than the current
  one (`Messages.GetStackFrames`).
- The empty function uses the bootstrapping ScopeInfo.
- `V8_FUNCTION_ARGUMENTS_CALLER_ARE_OWN_PROPS` is off, as in V8's default build.

Console (Builtins.Console.cs)
- `console` is installed in Genesis like V8's. The debug::ConsoleDelegate is
  an abstract C# class on `Isolate.ConsoleDelegate`; without one the methods
  do nothing, as in V8. `console.profile`/`profileEnd` reach the delegate but
  there is no CPU profiler behind them. Trace events: `isTraceCategoryEnabled`
  always answers false and `trace` records nothing (no tracing controller).

ValueSerializer (Objects/ValueSerializer.cs)
- The buffer is a managed byte array; the delegate's
  ReallocateBufferMemory/FreeBufferMemory hooks are not ported.
- Strings are written one-byte (Latin-1) when every code unit is at most 0xFF,
  which is where V8 writes a one-byte string's own representation; V8Sharp
  strings are UTF-16 only.
- ReadJSObjectProperties defines the properties one by one (V8 first tries
  to follow the expected map transitions); the resulting maps are the same.
- Not ported: WebAssembly modules and memories, shared structs/arrays and the
  shared-object conveyor, host objects of the API (the delegate hooks exist).
- ArrayBuffers of 2^31 bytes or more cannot be allocated (byte[] backing).

d8 host in the TestRunner (tools/V8Sharp.TestRunner/Shell)
- Worker, `d8.serializer` and the worker globals are implemented by the
  TestRunner's D8Shell over `ValueSerializer` (Serialization.cs in
  V8Sharp.D8 ports d8's SerializationData and delegates). Each worker is an
  isolate on its own 256 MB .NET thread; messages go through a
  SerializationDataQueue and the parent is notified through its task queue,
  as in d8. The Worker constructor and methods are JavaScript in d8-shim.js
  (V8: FunctionTemplates), holding the worker id in a WeakMap instead of an
  internal field.
- `print`, `printErr` and `write` are API functions (`CreateStringArgumentsFunction`),
  so they do not appear in stack traces; API functions are sloppy, as V8's
  FunctionTemplate functions are for CallSite purposes.
- d8's console is `V8Sharp.D8.D8Console` (d8-console.cc) behind the engine's
  own console builtins, in d8sharp and in the TestRunner's v8sharp engine
  (the oracle engine keeps the JavaScript replacement in d8-shim.js). Not
  ported: `console.profile`/`profileEnd` (no CPU profiler) and
  `console.trace` (V8 prints the stack to stderr).
- The runtime's own stdout output (`%DebugPrint`, `%DebugTraceMinimal`,
  `--disable-abortjs`) goes to `Isolate.StdOut`, a TextWriter defaulting to
  `Console.Out`, where V8 writes to the C stdout: the in-process shell
  redirects it into the test's output so it interleaves with `print`.
- `--enable-tracing --trace-config=FILE`: the file is read and parsed as
  JSON, with d8's error reports, but there is no tracing controller, so the
  categories are not used. d8sharp parses it in the main context (d8: a
  fresh context); the TestRunner uses a fresh realm.
- The message listener d8 installs (PrintMessageCallback) is the
  `MicrotaskQueue.UncaughtException` event, which V8Sharp raises for the
  exceptions V8 reports through verbose TryCatches (microtask callbacks,
  FinalizationRegistry cleanup callbacks).

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

- `instanceof` with an ordinary JSFunction (original @@hasInstance, the
  prototype accessor) and a prototype chain without proxies or access checks
  is decided without the builtin frame of Function.prototype[@@hasInstance]
  (`ObjectOps.TryFastInstanceOf`, CodeStubAssembler::InstanceOf's walk);
  nothing on that path can throw.

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
- CallSite methods: the ShadowRealm boundary check reads
  `NativeContext.IsShadowRealm` (V8: the context's shadow_realm_scope_info).
  getScriptHash computes the SHA-256 on each call (V8 caches it on
  the script) and never returns "" for opaque origins (not modelled).
  getThis returns undefined for a receiver that is still the hole.
- Error.isError has no API-wrapper (DOMException) case.
- %GetUndetectable builds the instance of an ObjectTemplate with a call
  handler as V8 does (callable map, API function constructor, called through
  CALL_AS_FUNCTION_DELEGATE), but its prototype is Object.prototype instead of
  the template function's own prototype object.
- ShadowRealm: a ShadowRealm's native context is marked with
  `NativeContext.IsShadowRealm` instead of V8's shadow_realm_scope_info.
- The global parseInt/parseFloat are Number.parseInt/parseFloat (one
  function, as in V8); their builtins (NumberParseInt, NumberParseFloat) are
  registered by the global functions' area.
- `Isolate.CountUsage` is a no-op (no use counters).

## Array, ArrayBuffer, SharedArrayBuffer, TypedArray, DataView, Atomics

- `a.push(x)`, `a.pop()` and `a.shift()` on a fast JSArray run the builtins'
  fast paths from the interpreter's call handlers (`BuiltinsArray.TryFastPush`
  / `TryFastPop` / `TryFastShift`) without CallBuiltin's frame record, as the
  CSA/Torque builtins do their fast paths without calling out. Nothing on
  those paths throws or runs JavaScript, so the missing frame is not
  observable.

- Backing stores are managed `byte[]` arrays (`BackingStore`). A growable
  SharedArrayBuffer allocates its maximum length up front (the array cannot
  move while other threads read it); a resizable ArrayBuffer allocates its
  current length and reallocates when resized past it. Allocations above
  `Array.MaxLength` fail with "Array buffer allocation failed", although
  `kMaxByteLength` is V8's 32GB - 1 (the sandbox build's limit).
- Typed arrays are always off-heap (no on-heap JSTypedArray elements below
  `typed_array_max_size_in_heap`). V8 keeps the data pointer in the typed
  array (external_pointer + base_pointer); V8Sharp caches the backing store's
  array, the byte offset and the length in the view (`JSTypedArray.FastData`)
  for fixed-length views of non-resizable buffers, filled by the element IC
  slow paths, and recomputes them from the buffer everywhere else. Array
  buffers do not keep a list of their views: a view checks
  `buffer.WasDetached` instead of being marked (the element fast paths skip
  the check while the ArrayBufferDetaching protector is intact).
- The typed array constructors keep JS_FUNCTION_TYPE maps (V8 gives them
  JS_*_TYPED_ARRAY_CONSTRUCTOR_TYPE); nothing observable depends on it.
- `v8_enable_undefined_double` is not modelled: double elements never hold
  undefined, so holey double arrays read holes as undefined as without the
  flag. Only elements kinds (%DebugPrint) can tell.
- V8's CSA/Torque fast loops over fast JSArrays (forEach/map/filter/every/
  some/reduce/find*, splice/slice/copyWithin/reverse/lastIndexOf/flat/
  toSpliced/with/toReversed) are not ported one to one; their results are
  unobservable, so the generic continuations run with a fast element probe
  (`TryGetFastElement`), and slice/splice/copyWithin/reverse/includes/indexOf
  keep a direct fast path over the backing store.
- The join Buffer is one growable array of entries instead of a linked list of
  FixedArray chunks; the result string is built flat.
- Array.prototype.toLocaleString and %TypedArray%.prototype.toLocaleString
  follow the !V8_INTL_SUPPORT build: element toLocaleString methods are called
  without the locales and options arguments. The oracle is an ICU build.
- Array.prototype.sort is the PowerSort of this tree (third_party/v8/builtins/
  array-sort.tq). The oracle's V8 14.7 still runs TimSort (no
  kMaxInlineSortLength shortcut, different run merging), so user comparefn
  call orders differ from the oracle's; they match the tree's algorithm.
- Float16 conversions use a port of V8's DoubleToFloat16 bit manipulation
  (`TypedArrayScalars.DoubleToFloat16`), not `System.Half`, so double rounding
  through float cannot occur; Float16 to double uses `System.Half`, which is
  exact.
- NaN: `JSValue.NaN` (the value of the NaN globals) has the sign bit set
  (C#'s `double.NaN`), while V8's NaN constant is 0x7FF8000000000000; storing
  the constant into a Float64Array or with DataView.setFloat64 writes
  different bytes. Computed NaNs (0/0) have the same bits in both.
- `%TypedArray%.prototype.map`/`set`/Atomics report write failures with
  kTypedArrayValidateErrorOperation unless --js-immutable-arraybuffer is on
  (`JSTypedArray.ValidateErrorMessage`): the generated message table has the
  flag-dependent text of kTypedArrayValidateWriteErrorOperation only for the
  flag-on case.
- Uint8Array base64: fromBase64 follows the proposal's FromBase64 (V8's
  simdutf fast path agrees for complete input). setFromBase64 into a buffer
  that fills up models simdutf::base64_to_binary_safe: it keeps parsing chunk
  by chunk past the point where the proposal stops (`TailDecode`), so later
  bad characters and invalid final chunks are still reported. simdutf's
  trailing-garbage lookahead (test262 trailing-garbage, skipped in
  test262.status) is not modelled. simdutf is not in the checkout; the model
  is fitted to the oracle.
- Atomics: element operations use `Interlocked`/`Volatile` on the managed
  array (8- and 16-bit read-modify-write as compare-exchange loops).
  `Isolate.AllowAtomicsWait` (d8's --no-can-block, %SetAllowAtomicsWait) is
  V8's allow_atomics_wait. FutexEmulation keeps one managed wait list for sync
  and async waiters; a woken async waiter's promise is resolved by a task per
  waiter (V8 batches the waiters of an isolate into one
  ResolveAsyncWaiterPromisesTask; the order is the same), and timeouts are
  delayed tasks on the isolate's foreground runner
  (`Isolate.PostNonNestableDelayedTask`, which `RunPendingTasks` waits for
  when nothing else is pending, as d8's message loop does;
  `RunPendingTasks(maxWaitMs)` bounds that wait for an embedder whose message
  loop must also poll other queues, like the TestRunner's Worker host). Due
  times use the Stopwatch clock, so a timeout never fires before an
  embedder's high-resolution monotonic clock has seen it elapse. V8Sharp has
  no Isolate::Deinit; the embedder calls `Isolate.Deinit()` when it is done
  with an isolate, which runs FutexEmulation::IsolateDeinit and drops the
  isolate's pending tasks (CancelableTaskManager::CancelAndWait).
- Array.fromAsync keeps its resume state in a synthetic function context
  like array-from-async.tq, but the state machine loop is a C# switch over
  the labels; each await point is PromiseResolve + PerformPromiseThenImpl
  with the root-function closures, as in V8.

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
- JSON.parse builds objects from the object literal map for their named
  property count (as JSDataObjectBuilder does) with CreateDataProperty in
  source order (elements first), instead of JSDataObjectBuilder's direct
  field writes with the previous array element's map as feedback, and
  without the recursive ParseJsonValueRecursive
  / numeric-array fast path (one iterative parser for all inputs). Keys,
  order, values, duplicate handling and elements kinds of arrays are the
  same; only backing-store choices (e.g. dictionary elements) can differ.
- JSON.parse numbers with at most 15 significant digits and a decimal
  exponent within [-22, 22] take Clinger's fast path in the parser before
  StringToDouble (fast_float's first step, same results; checked against
  StringToDouble on 20000 random numbers). Parse stacks are pooled.
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

## Collections, weak references, Promise, Iterator, DisposableStack

- **Weak references.** V8's GC clears dead WeakRef and WeakCell targets in
  its atomic pause (MarkCompactCollector::ClearJSWeakRefs). V8Sharp holds the
  targets through CLR `WeakReference<HeapObject>`s and notices the clearing
  afterwards: at the end of each microtask checkpoint
  (`Isolate.ClearKeptObjects`), if `GC.CollectionCount(0)` changed since the
  last check, the active cells of every tracked FinalizationRegistry are
  walked, cells whose target died move to the cleared list, and the dirty
  registries are queued. `Isolate.CollectGarbage()` (d8's `gc()`,
  `RequestGarbageCollectionForTesting`) runs a blocking
  `GC.Collect`/`WaitForPendingFinalizers`/`GC.Collect` and then the same
  processing, so tests observe clearing deterministically. The cleanup task
  (FinalizationRegistryCleanupTask) is posted to a per-isolate foreground
  task queue (`Isolate.PostNonNestableTask`, `ForegroundTaskPosted`); the
  embedder runs it with `Isolate.RunPendingTasks()`, which performs a
  microtask checkpoint after each task as d8's message loop does. Registries
  are tracked weakly once they get their first cell; a registry that dies
  never runs its cleanup (as in V8). The unregister-token map is a
  `Dictionary<int, WeakCell>` keyed by the token's identity hash, chained
  through KeyListPrev/Next like V8's SimpleNumberDictionary.
- **WeakMap / WeakSet** use a `ConditionalWeakTable` keyed by the key object
  instead of an EphemeronHashTable; the CLR table has ephemeron semantics.
  Nothing observable differs (weak collections are not enumerable).
- **PromiseReaction** is one class that also plays PromiseReactionJobTask:
  V8 morphs the reaction's map in place (MorphAndEnqueuePromiseReaction),
  V8Sharp changes its `State` field; no allocation either way.
- **Promise rejection** always goes through JSPromise::Reject (the runtime
  path V8 takes for unhandled rejections and hooks); the CSA fast path only
  skips steps that have no effect in that case. There is no pending message
  to move to the promise (MoveMessageToPromise): the message travels with the
  JavaScriptException. Debug events and async stack tagging are not ported.
- **Promise constructor**: V8 checks Builtins::AllowDynamicFunction for the
  executor's context through the embedder's code-generation callback;
  V8Sharp has no such callback, so the check is omitted.
- **Collection constructors**: V8's GotoIfInitialAddFunctionModified checks
  the prototype map and the constness of the add function's descriptor;
  V8Sharp checks the prototype map and the current property value, which is
  the same condition without field constness tracking.
- **Generators and async functions**: the promise jobs resume generators
  through static hooks (`PromiseBuiltins.ResumeGeneratorTrampoline`,
  `AsyncGeneratorResumeNext`, `AsyncGeneratorResolve`) that the interpreter
  sets, instead of calling the ResumeGeneratorTrampoline builtin by id.
- **AsyncFromSyncIterator**: V8 CSA_CHECKs the receiver type (a crash on
  failure); V8Sharp throws InvalidOperationException, which is equally
  unreachable from script.
- **IteratorHelpers**: the pre-port iteration helpers in
  Builtins.Object.cs moved to `IteratorBuiltins` (Builtins.Iterator.cs); a
  forwarding `IteratorHelpers` class remains until every caller is switched
  (TODO(merge)).

## Temporal

- **The engine behind the binding.** V8 implements Temporal as a binding
  layer (`js-temporal-objects.cc`, `builtins-temporal.cc`) over the Rust
  crate temporal_rs (`third_party/rust/temporal_capi`), which is not in this
  checkout. The binding is ported; everything V8 hands to temporal_rs
  ("Rest of the steps handled in Rust") is implemented in C# from the
  Temporal specification's abstract operations in `src/V8Sharp/Temporal/`
  (`temporal_rs::Foo` becomes `V8Sharp.Temporal.Foo`). A JSTemporal* object
  holds the engine value directly instead of a CppGCManaged pointer, and an
  engine error is a `TemporalError` exception that the builtins' wrapper
  turns into the JS error ExtractRustResult would create. Where test262 and
  an older spec text disagree, test262 wins (PlainYearMonth.prototype.add
  rejects units below months, the rounding window of
  proposal-temporal#3168, month-day strings ignore the year).
- **Engine error messages.** The kTemporal messages of the binding are V8's
  text; the messages of errors raised inside the engine are V8Sharp's own
  (temporal_rs's texts are not available). The error types match.
- **Calendars.** Only `iso8601` is available. V8 builds temporal_rs with
  ICU4X's calendar data even without `V8_INTL_SUPPORT`, so it also accepts
  `gregory`, `japanese`, `hebrew` ...; V8Sharp has no calendar data (no ICU,
  architecture.md section 2) and throws RangeError for them. test262's
  built-ins/Temporal uses only iso8601; the other calendars are tested in
  intl402, which does not run without i18n.
- **Time zone data.** V8 reads the IANA database from ICU's zoneinfo64.res
  (compiled into the binary for no-ICU builds, js-temporal-zoneinfo64.cc)
  through temporal_rs's provider. V8Sharp uses .NET's `TimeZoneInfo` (the
  host's /usr/share/zoneinfo on Linux); the available identifiers are
  TimeZoneInfo's plus the zoneinfo directory's names. Consequences: the
  data version is the host's; offsets are TimeZoneInfo's (local mean time
  offsets such as New York's -4:56:02 come out rounded to whole minutes, and
  so do the transitions out of them); instants after
  9999 reuse the rules of the same point in the 400-year Gregorian cycle and
  instants before year 1 the offset of year 1; transitions
  (getTimeZoneTransition, GetStartOfDay in a gap) are found by scanning the
  offsets day by day from 1800 to 450 years ahead and bisecting, so
  transitions less than a day apart can be merged; link names are not
  resolved to their primary identifier for TimeZoneEquals, except the
  aliases of UTC. UTC and offset time zones do not use the provider and are
  exact.
- **System time zone and clock.** As in V8 without `V8_INTL_SUPPORT`,
  `Temporal.Now.timeZoneId()` is "UTC". The embedder's
  `temporal_get_epoch_nanoseconds_callback` (v8::Isolate API) is not ported;
  SystemUTCEpochNanoseconds reads `DateTime.UtcNow` (100 ns resolution).
- **toLocaleString** is toString with default options, as in V8 without
  `V8_INTL_SUPPORT`.
- **Builtin registration.** The Temporal builtins are registered by
  reflection over `TemporalBuiltins` (method name = Builtin id) instead of
  233 explicit Register lines.

