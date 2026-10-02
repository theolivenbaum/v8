# V8Sharp architecture

V8Sharp is a port of V8 (this repository, `src/`) to safe, managed C# on
.NET 10. This document fixes the cross-cutting design decisions. Every
component port builds on them; change them only with a note in `todo.md`.

The guiding rule: **port V8's architecture, not just its behaviour.** The same
pipeline (scanner, parser, scope analysis, Ignition bytecode, feedback vectors,
inline caches, hidden classes, elements kinds, tiered compilation) with the
same names, so that a reader can hold a C# file next to the `.cc` it came from.
What is replaced is what .NET already provides: the heap and GC, the machine
code backends, and the platform layer.

## 1. Projects

| Project | V8 source | Depends on |
|---|---|---|
| `V8Sharp.Base` | `src/base` (numbers, bits, hashing, vectors), `src/numbers` (string/number conversions), `src/bigint`, `src/strings/unicode*`, `src/strings/char-predicates` | nothing |
| `V8Sharp.Parsing` | `src/parsing`, `src/ast` | Base |
| `V8Sharp.RegExp` | `src/regexp` (irregexp: parser, compiler, bytecode generator, peephole, interpreter) | Base |
| `V8Sharp` | everything else: `src/objects`, `src/execution`, `src/heap/factory`, `src/init`, `src/interpreter`, `src/builtins`, `src/runtime`, `src/ic`, `src/json`, `src/date`, `src/compiler` (the IL tiers) | Base, Parsing, RegExp |
| `V8Sharp.D8` | `src/d8` - the `d8sharp` shell; also a library the test runner uses in-process | V8Sharp |
| `V8Sharp.Oracle` | none: the real V8 through ClearScript, for differential tests | ClearScript (native) |

Inside a project, folders mirror V8's directory names (`Objects/`, `Interpreter/`,
`Builtins/`...), and a file is named after the V8 file it ports
(`objects/lookup.cc` -> `Objects/Lookup.cs`, class `LookupIterator`).

Namespaces: `V8Sharp.<Folder>` (e.g. `V8Sharp.Objects`, `V8Sharp.Interpreter`),
`V8Sharp.Base.Numbers`, `V8Sharp.Parsing`, `V8Sharp.Ast`, `V8Sharp.RegExp`.

## 2. What is not ported, and what replaces it

| V8 | V8Sharp |
|---|---|
| `src/heap` (Orinoco GC, spaces, write barriers, handles) | the .NET GC. Objects are ordinary managed objects. `Handle<T>`/`Tagged<T>` disappear: a C# reference is always valid and GC-safe. `DisallowGarbageCollection` scopes are dropped. |
| `src/snapshot` | none. The bootstrapper builds the native context at start-up. (A later optimisation may cache it.) |
| `src/codegen`, the architecture backends, `src/wasm` | none. Machine code comes from RyuJIT: the compiler tiers emit IL (section 9). |
| CSA / Torque builtins | C# methods. The algorithm, the fast paths and the slow paths follow the `.tq`/`-gen.cc` file; the spec text is the tie-breaker. |
| `src/sandbox`, pointer compression, `src/trap-handler` | not applicable: managed memory is already safe. |
| ICU (`V8_INTL_SUPPORT`) | not ported. V8Sharp matches V8 built with `v8_enable_i18n_support=false`. Identifier predicates use .NET's Unicode tables (equivalent to V8's ICU path); case mapping uses the ported `unibrow` tables. |
| `src/inspector`, `src/debug`, `src/profiler` | deferred. |

## 3. Values: `JSValue`

V8's `Tagged<Object>` is a word that is either a Smi or a pointer to a heap
object. V8Sharp's equivalent is a 16-byte struct:

```csharp
public readonly struct JSValue
{
    internal readonly HeapObject? _obj;   // null => undefined
    internal readonly double _num;        // payload when _obj is NumberTag
}
```

- `_obj == null` is **undefined**, so `default(JSValue)` and a fresh
  `new JSValue[n]` are all-undefined, which is what V8's register file and
  `FixedArray::New` initialisation mean.
- A **number** has `_obj == JSValue.NumberTag` (a sentinel `HeapObject`) and the
  double in `_num`. There is no separate Smi representation in the value:
  V8's Smi/HeapNumber split is a heap-layout detail, not a semantic one. Where
  V8 behaviour observably depends on "is a Smi" (feedback kinds such as
  `SignedSmall`, `%IsSmi`, elements kinds `PACKED_SMI_ELEMENTS`), the test is
  `JSValue.IsSmi` = "integral, in the 31-bit Smi range, and not -0".
  (V8 uses 31-bit Smis with pointer compression, which is the default config.)
- Anything else is a reference to a `HeapObject`: `Oddball` (null, true,
  false, the_hole, ...), `JSString`, `Symbol`, `BigInt`, `JSReceiver`s, and
  internal objects (`Context`, `SharedFunctionInfo`, `FeedbackVector`,
  `ScopeInfo`...).

Numbers never allocate. Comparisons against oddballs are reference compares.
`JSValue` is passed by value; hot helpers take `in JSValue`.

## 4. Heap objects

```
HeapObject                 abstract; readonly InstanceType field (switchable)
 ├─ Oddball                undefined is not an Oddball instance (null ref); null,
 │                         true, false, the_hole, uninitialized, exception,
 │                         optimized_out, stale_register, termination_exception
 ├─ Name                   hash field (with cached array index, like V8)
 │   ├─ JSString           abstract: length, IsInternalized, Flatten()
 │   │   ├─ SeqString      a flat string over System.String (UTF-16, same as V8's two-byte;
 │   │   │                 V8's one-byte strings are a storage optimisation we do not need)
 │   │   ├─ ConsString     rope from `+`; flattened lazily in place (becomes a
 │   │   │                 "thin" wrapper of the flat result, as V8's flattening does)
 │   │   └─ SlicedString   substring view (optional, V8's SlicedString)
 │   └─ Symbol             description, flags (private, private_name, well_known ...)
 ├─ BigInt                 sign + digits (V8Sharp.Base.BigInt does the arithmetic)
 ├─ FixedArrayBase         FixedArray (JSValue[]), FixedDoubleArray (double[] + hole NaN),
 │                         NumberDictionary, NameDictionary, OrderedHashMap/Set ...
 ├─ Map                    hidden class (section 5)
 ├─ Context, ScopeInfo, SharedFunctionInfo, BytecodeArray, FeedbackVector,
 │  FeedbackCell, ClosureFeedbackCellArray, AccessorPair, PropertyCell ...
 └─ JSReceiver             Map map
     ├─ JSProxy
     └─ JSObject           in-object slots (JSObjectInObjectN), PropertyArray, Elements
         ├─ JSFunction     SharedFunctionInfo, Context, FeedbackCell, code (tier entry)
         ├─ JSArray, JSPrimitiveWrapper, JSDate, JSRegExp, JSError-like (plain JSObject),
         ├─ JSArrayBuffer, JSTypedArray, JSDataView, JSMap/Set/WeakMap/WeakSet,
         ├─ JSPromise, JSGeneratorObject (+Async variants), JSBoundFunction,
         ├─ JSGlobalObject, JSGlobalProxy, JSArgumentsObject ...
```

Class names are V8's. Where V8 uses Torque/C++ field accessors, C# uses
fields or properties with the same names in PascalCase.

Strings used as **property keys are always internalized** (via the isolate's
`StringTable`), so named-property lookup compares references, as in V8.
Integer-index keys are elements, never named properties (V8's
`PropertyKey`/`LookupIterator` rule).

## 5. Hidden classes and properties

Ported from `src/objects/map.*`, `descriptor-array.*`, `transitions.*`,
`property-details.h`, `field-index.h`, `lookup.*`, `js-objects.cc`:

- Every `JSReceiver` has a `Map`: instance type, prototype, bit fields
  (callable, constructor, extensible, prototype map, dictionary map,
  undetectable, has named interceptor...), `ElementsKind`, a
  `DescriptorArray` (keys in insertion order + `PropertyDetails`), and
  transitions to child maps.
- Fast-mode properties are laid out as in V8: the map's
  `GetInObjectProperties()` fields live in the object, the rest in a
  PropertyArray (`JSValue[]`) that grows by `kFieldsAdded`. In-object counts
  come from the instance size (`JSFunction::CalculateExpectedNofProperties`
  plus slack for constructors, the literal's property count for object
  literals) and in-object slack tracking (`Map::InobjectSlackTrackingStep`,
  `MapUpdater::CompleteInobjectSlackTracking`) shrinks them after seven
  constructions. A CLR object has a fixed size, so ordinary objects are
  allocated from a small chain of classes (`JSObjectInObject1` ...
  `JSObjectInObject256`) whose `[InlineArray]` segments are the slots, the
  smallest that holds the map's in-object count; a larger class derives from
  the smaller ones, so slot i is the same field in every object that has it
  (`JSObject.InObjectSlot`, span indexing, no `unsafe`). Other JSObject
  subclasses (arrays, functions, ...) keep their in-object fields at the
  front of the PropertyArray. `FieldIndex` encodes V8's (in-object, index)
  split plus `StorageIndex`, the physical location, which is what inline
  caches store with the map (`JSObject.FieldAt`).
- Adding a property follows/creates a transition; deleting (except the last
  added) or too many properties normalises to dictionary mode
  (`NameDictionary`, which keeps enumeration order), with V8's thresholds
  (`kMaxNumberOfDescriptors`, `kFastPropertiesSoftLimit`...).
- Field representation tracking (`Smi`/`Double`/`HeapObject`/`Tagged`) and
  field-type generalisation are ported, because optimized code depends on them.
- `LookupIterator` is the single path for property access semantics (own and
  prototype-chain lookup, accessors, interceptors, proxies, integer-indexed
  exotics, typed arrays).
- Elements: `ElementsKind` and the elements-kind lattice
  (PACKED_SMI -> PACKED_DOUBLE -> PACKED; HOLEY_*; DICTIONARY; typed-array
  kinds; arguments kinds; string wrapper). Backing stores: `FixedArray`
  (`JSValue[]`), `FixedDoubleArray` (`double[]`, holes as V8's hole NaN bit
  pattern), `NumberDictionary`. `ElementsAccessor` is ported per kind, which
  is where SIMD (`Vector<T>`/`Vector256<T>`) pays off: fills, copies, `indexOf`
  on double/Smi arrays, typed-array operations.

## 6. Isolate, contexts, roots

- `Isolate` owns the roots (`ReadOnlyRoots`: oddballs, common internalized
  strings, well-known symbols, root maps), the `Factory`, the `StringTable`,
  the microtask queue, the pending-message/exception state, the stack guard,
  the interpreter's register stack, and flags (`FlagList`, ported from
  `src/flags/flag-definitions.h`, same names: `--allow-natives-syntax`,
  `--jitless`, `--no-lazy-feedback-allocation`...).
- A `NativeContext` holds the realm's intrinsics (V8's
  `NATIVE_CONTEXT_FIELDS`). `Bootstrapper`/`Genesis` (`src/init/bootstrapper.cc`)
  builds it, installing builtins in V8's order so property enumeration order of
  globals and prototypes matches V8.
- Function contexts are `Context` objects (a `JSValue[]` of slots with
  `previous`, `scope_info`, `extension`), exactly V8's scheme; scope analysis
  decides stack vs context allocation.

## 7. Calls, frames, exceptions

- **Calling convention.** `Execution.Call(isolate, callable, receiver, ReadOnlySpan<JSValue> args)`
  and `Execution.New(...)`. Builtins are C# methods of shape
  `static JSValue Name(Isolate isolate, in BuiltinArguments args)`, where
  `BuiltinArguments` is a `ref struct` (target, new_target, receiver,
  `ReadOnlySpan<JSValue>` arguments) - allocation-free, like V8's
  `BuiltinArguments`.
- **Interpreter frames.** The isolate owns one `JSValue[]` register stack
  (V8's machine stack, in effect). A frame is a window of it: parameters
  (receiver + args, V8's `a0..an`), then the register file. Argument lists
  passed by `CallProperty r1, r2-r4` are windows into the caller's registers,
  so calls copy nothing but the callee's own parameter slots. Stack overflow
  is detected against both the register stack and the native stack
  (`RuntimeHelpers.TryEnsureSufficientExecutionStack`) and raised as V8's
  `RangeError: Maximum call stack size exceeded`. Hosts run the engine on a
  thread with a large stack (d8sharp: 256 MB).
- **Exceptions.** A JS throw that is caught in the same interpreter frame is
  dispatched through the frame's handler table without .NET exceptions. A
  throw that leaves a frame is a .NET `JavaScriptException` carrying the
  thrown `JSValue` (and the pending message for the uncaught-exception
  report). Runtime and builtin code throws with `isolate.Throw(...)`, which
  creates the exception with V8's message template (`MessageTemplate`,
  ported from `src/common/message-template.h`, so error messages match V8's
  text byte for byte).
- **Termination** (`TerminateExecution`, the watchdog) is an interrupt flag
  checked by the interpreter at back-edges and function entry (V8's
  `StackCheck`/interrupt budget) and raised as an uncatchable exception.
- **Generators/async** follow Ignition: `SuspendGenerator` copies the live
  registers into the generator object and returns; `ResumeGenerator`
  restores them. No threads, no C# iterators.

## 8. Ignition

`src/interpreter` is ported closely, because its output is testable against
V8 exactly:

- `Bytecode` enum with V8's numbering, operand types and sizes, `Wide` and
  `ExtraWide` prefixes, accumulator use, implicit registers.
- `BytecodeArrayBuilder`, `BytecodeArrayWriter`, `BytecodeRegisterOptimizer`,
  `ConstantArrayBuilder`, `HandlerTableBuilder`, control-flow builders, and
  `BytecodeGenerator` produce the **same bytecode as V8** for the same source.
  This is checked against `test/unittests/interpreter/bytecode_expectations/*.golden`
  (this revision's V8) and against the oracle's `--print-bytecode` output.
- `FeedbackVector` / `FeedbackMetadata` with V8's slot kinds; ICs
  (`src/ic`) record monomorphic/polymorphic/megamorphic state per slot with
  V8's transitions, because the optimizing tier reads them.
- `Interpreter` is a `switch` dispatch loop over the bytecode array, with the
  accumulator and the register window as locals.

## 9. Tiers (after the interpreter is conformant)

V8: Ignition -> Sparkplug (baseline) -> Maglev (mid-tier, SSA + feedback) ->
Turbofan/Turboshaft. V8Sharp keeps the tiering policy (interrupt budget,
`TieringManager`, OSR at `JumpLoop`) and replaces code generation with IL:

1. **Baseline (Sparkplug analogue).** A single pass from bytecode to IL via
   `DynamicMethod`: one IL block per bytecode, same frame layout, calls into
   the same runtime/IC helpers. Removes dispatch and operand decoding; RyuJIT
   does register allocation. Implemented in `src/V8Sharp/Baseline/`
   (section 9.1).
2. **Optimizing (Maglev analogue).** A graph built from bytecode + feedback:
   speculative Smi/double/int32 representations held in unboxed IL locals,
   map checks, inlined property loads at known field indices, inlined
   builtins, `Vector<T>` for eligible loops. Speculation failures
   **deoptimize**: the frame state is materialised back into an interpreter
   frame and execution continues in Ignition, as V8's deoptimizer does.
3. RyuJIT's own tiering (tier-0/tier-1, dynamic PGO) sits below both.

## 10. Testing

- **Unit tests** port V8's `test/unittests` and `test/cctest` tests, with the
  V8 file name kept (`test/unittests/numbers/conversions-unittest.cc` ->
  `tests/V8Sharp.Base.Tests/Numbers/ConversionsUnitTest.cs`), each `TEST`/`TEST_F`
  a `[Fact]` of the same name.
- **Golden bytecode**: `bytecode_expectations/*.golden` are parsed and replayed.
- **Conformance**: `tools/V8Sharp.TestRunner` runs `test/mjsunit`,
  `test/message`, `test/test262` (data at `test/test262/data`, V8's pinned
  commit), `test/webkit`, `test/mozilla` with the `.status` files and
  `// Flags:` lines, against either engine: V8Sharp in-process, or the oracle.
- **Differential (parity)**: `tests/V8Sharp.Parity.Tests` runs the same script
  in V8Sharp and in the oracle and compares transcripts.
- The oracle is V8 14.7 (ClearScript 7.5.1.1), slightly older than this
  source tree (15.6), and built with ICU. Differences caused by version or
  ICU are recorded in `todo.md`; for this tree's behaviour the golden files
  and the `.status` files are the authority.

### 9.1 The baseline tier (src/baseline -> `Baseline/`)

Files: `BaselineCompiler.cs` (baseline-compiler.cc: PreVisit, Prologue,
`VisitSingleBytecode`), `BaselineAssembler.cs` (baseline-assembler.h over an
`ILGenerator`), `BaselineBuiltins.cs` (what the Visit* methods call),
`BaselineCode.cs` (Code of kind BASELINE), `BaselineExecution.cs` (entry,
OSR, budget interrupts), `BaselineBatchCompiler.cs`, `Baseline.cs`
(CanCompileWithBaseline), `Execution/TieringManager.cs`,
`Codegen/Compiler.Baseline.cs` (CompileSharedWithBaseline, CompileBaseline,
CompileAllWithBaseline).

**Code shape.** A baseline function is one static method
`JSValue (Isolate, ref InterpreterState)` in its own type of a process-wide
Reflection.Emit assembly (`BaselineCodeSpace`; the assembly carries
`IgnoresAccessChecksTo("V8Sharp")` so the code may reach engine internals). Each bytecode becomes an IL
block; operands are decoded at compile time and pushed as constants.
Jump targets are IL labels, `SwitchOnSmiNoFeedback` and
`SwitchOnGeneratorState` are IL `switch` tables. The IL evaluation stack is
empty between bytecodes.

**Frame.** The frame *is* the interpreter frame (V8's invariant that
baseline frames look like interpreter frames): `InterpreterExecution.EnterFrame`
builds it on the register stack and pushes the frame record, then runs the
SharedFunctionInfo's baseline code instead of the dispatch loop. Registers are
accessed as `ref JSValue` at `fpRef + index` (a managed pointer local), so
generators, `arguments`, stack traces and the frame walker see the same slots.
The accumulator, the current context, the feedback vector, the constant pool
and the bytecode array live in IL locals; the context is also written to its
frame slot and to `isolate.Context` where the interpreter does it. The
record's `IsBaseline` flag tells `%GetOptimizationStatus` the frame's tier.

**Bytecode offset.** Before every bytecode that can throw or call out, the
code stores the bytecode offset (after any prefix, as the interpreter does)
in the frame record. V8 recovers the offset from the return address through
the bytecode offset table; V8Sharp's frame walker, handler lookup and
source positions read the record, as for interpreted frames.

**Entry points and exceptions.** The method starts with a dispatch on
`state.Pc`: offset 0 (a call), every exception handler, and every loop header
(`JumpLoop` target). An exception thrown out of a helper leaves the method;
`BaselineExecution.Run` (like the interpreter's `Run`) looks up the frame's
handler table at the recorded offset, sets up the handler's context and the
exception, and calls the method again, which jumps to the handler. `Throw`
and `ReThrow` with a handler in the same frame branch back to the dispatch
without a .NET exception. IL `try` regions are not used: they cannot be
entered by a branch, and the handler table already has the ranges.

**Calling helpers.** Every non-trivial bytecode calls a static method of
`BaselineBuiltins` that does what the interpreter's case does (the same IC
entry points, runtime functions, `InterpreterOps`, `InterpreterCalls`).
RyuJIT inlines the small ones into the method (verified with
`DOTNET_JitDisasm`), including the ICs' monomorphic fast paths. The binary
and unary operations have Smi fast paths in IL-inlined form: when the
embedded feedback is already SignedSmall and operands and result are Smis,
only the result is computed; anything else takes the interpreter's helper,
which records feedback.

**Tiering.** `TieringManager.OnInterruptTick` is ported: the first budget
interrupt allocates the feedback vector and enqueues the function to the
batch compiler (V8's defaults: `--sparkplug`, `--baseline-batch-compilation`,
threshold 4 KB of estimated code, budget `invocation_count_for_feedback_allocation`
(8) x bytecode length). Later ticks raise the budget (there is no optimizing
tier: `use_optimizer()` is false, as in a V8 built without Turbofan and
Maglev). A call to a function whose SharedFunctionInfo has baseline code
enters it (allocating the feedback vector first if needed,
Runtime_InstallBaselineCode). `--always-sparkplug` compiles every function
when its bytecode is finalized. A running interpreter frame switches at its
next `JumpLoop` (OSR to baseline, `InterpreterOnStackReplacement_ToBaseline`):
the dispatch loop returns to `Run`, which continues the same frame in the
baseline code at the loop header. `--jitless` implies `--no-sparkplug`.
(Temporarily `--sparkplug` defaults to off in V8Sharp; see deviations.md.)

**RyuJIT.** Methods of a (non-collectible) dynamic assembly take part in
RyuJIT's tiered compilation: a baseline method is first jitted quickly at
tier 0, hot ones are rejitted at tier 1 with dynamic PGO, and long-running
loops in tier-0 code switch to optimized code through RyuJIT's OSR. This is
what makes Sparkplug's "compile fast" property hold: a `DynamicMethod` is
always compiled with full optimization, which with the inlined IC and
arithmetic fast paths cost ~7 ms per function (eval-heavy code became 30x
slower than the interpreter). `V8SHARP_BASELINE_DYNAMICMETHOD=1` switches back
to collectible DynamicMethods for comparison. The generated IL stays small
by calling helpers, because very large methods fall back to MinOpts.

### 9.2 The optimizing tier (src/maglev -> `Maglev/`, src/deoptimizer -> `Deoptimizer/`)

Files: `MaglevGraphBuilder*.cs` (maglev-graph-builder.cc: frame state,
merge points and loop phis, speculation from feedback, property access,
calls and inlining), `MaglevIR.cs` (maglev-ir.h: opcodes, value
representations, node types, deopt info), `MaglevGraph.cs` (graph, basic
blocks, compilation unit and info, dependencies),
`MaglevInterpreterFrameState.cs` (KnownNodeAspects, merge states),
`BytecodeAnalysis.cs` (src/compiler/bytecode-analysis: loops, liveness),
`MaglevPhiRepresentationSelector.cs`, `MaglevCodeGenerator.cs` (the IL
emitter), `MaglevCompiler.cs` (the pipeline, code installation, disabling),
`MaglevBuiltins.cs` (helpers the IL calls), `MaglevCalls.cs` (calls into
Maglev code), `MaglevExecution.cs` (entry, OSR, continuation after a deopt),
`Deoptimizer/Deoptimizer.cs`, `Objects/DependentCode.cs`,
`Runtime/RuntimeTest.Maglev.cs` (the natives), `Execution/TieringManager.cs`.

**Pipeline.** As V8: the graph builder walks the bytecode once with an
abstract interpreter frame (a ValueNode per register), creating merge states
(phis) at jump targets and loop headers from the bytecode analysis
(liveness and loop assignments), speculating from the feedback vector and
the embedded feedback (Smi/Int32 and Float64 arithmetic with overflow
checks, map checks with known-map tracking, field loads/stores from IC
handlers, element accesses on fast elements, global property cells, known
call targets, builtins such as Math.*), and inlining small functions. The
phi representation selector untags phis to Int32/Float64. Unsupported
bytecodes (generators, async functions, ...) abort the compilation and
disable optimization of the function, as V8's bailouts do.

**Exceptions.** As V8's graph builder, a node that can throw inside a try
block gets an `ExceptionHandlerInfo`: the catch block's merge state merges
the frame at the node (`MergeThrow`), and values that differ between the
throwing nodes become exception phis whose inputs are those values, in throw
order. In the IL the whole body is one .NET try region; a throwing node
stores its index in a local before it runs (and -1 after), the region's
filtered catch clause runs that node's trampoline (the exception phis from
the node's values, the exception into the accumulator phi, the pending
message) and leaves to the start of the region, whose first instruction
dispatches to the catch block. The catch block then runs in the same
method with the same locals. Functions with handlers, and calls inside try
blocks, are not inlined.

**Code.** The graph becomes one static method `JSValue Code(MaglevCode,
Isolate, ref InterpreterState)` in the dynamic assembly baseline code uses
(`BaselineCodeSpace`), so RyuJIT tiers it like baseline code. Each value
node gets an IL local of its representation (`JSValue`, `int`, `uint`,
`double`): untagged values never touch the heap. Phis are moves on the
edges. Checks branch to deopt exits; small operations are inline IL, others
call `MaglevBuiltins` or the `BaselineBuiltins` the baseline tier uses
(generic nodes: the same IC entry points and runtime functions). Constants
are static fields of the method's type.

**Frames.** The optimized frame *is* the interpreter frame
`InterpreterExecution.EnterFrame` (or `MaglevCalls.EnterFrame`) built:
registers are not kept in it while the code runs (values live in IL
locals), only the fixed slots (receiver, arguments, context, closure,
feedback vector) and the frame record, so stack traces, `arguments` and
the frame walker work unchanged. Inlined functions get real frames too
(`EnterInlinedFrame` pushes an interpreter frame record and its register
area; `LeaveInlinedFrame` pops it), which keeps stack traces exact and
makes deopts of inlined code cheap. The current bytecode offset of every
frame is stored in its record before anything that can throw or call.

**Deoptimization.** A deopt exit stores the values of the frame state of
its checkpoint (V8's translation: every live register, the context and the
accumulator of each frame, outermost first) into `isolate.MaglevDeoptScratch`
and calls `Deoptimizer.Deoptimize`, which writes them into the frames'
registers, makes inlined frames interpreter inline frames (their Return
resumes the caller in the same dispatch loop, `InterpreterInlineCalls`),
points the `InterpreterState` at the innermost frame and sets
`MaglevDeoptPending`; the IL then returns and the caller of the code
(`MaglevExecution.Run`, `MaglevCalls.EnterFrame`, `RunOsr`) continues in
`InterpreterExecution.Run` (InterpreterEnterAtBytecode). Eager deopts
continue at the bytecode whose check failed; lazy deopts (after a call,
when the code was invalidated meanwhile: the IL tests
`MaglevCode.MarkedForDeoptimization` after every call) continue after the
call with its result. An eager deopt invalidates the code (except OSR early
exits, and OSR code deopting outside its loop); after `kMaxDeoptCount`
invalidations the tiering manager does not optimize the function again. A
deopt of a check made while reducing a builtin call disallows speculation
on the call's feedback (out of bounds first only disallows bounds-check
speculation), as V8's feedback_to_update.

**Dependencies.** Code depends on stable maps (prototype chains, known
maps), property cells (global constants), initial maps (FastNewObject) and
protectors. `DependentCode` keeps a weak table from the object to the code;
the object model calls `DeoptimizeDependencyGroups` where V8 does, which
marks the code (lazy deopt).

**Tiering.** `--maglev` (off by default in V8Sharp) makes
`Isolate.UseOptimizer` true; `TieringManager.OnInterruptTick` compiles a
function synchronously once its invocation count reaches
`--invocation-count-for-maglev` and installs the code on the feedback
vector, which every closure of the CreateClosure site shares. A frame stuck
in a loop OSRs at its next JumpLoop budget interrupt
(`MaglevExecution.TryGetOsrCode`, `RunOsr`): OSR code starts at the loop
header with the interpreter's registers as initial values; back edges of
enclosing loops leave it (kOSREarlyExit).
