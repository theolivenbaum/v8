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
| `src/codegen`, the architecture backends | none. Machine code comes from RyuJIT: the compiler tiers emit IL (section 9). |
| `src/wasm` (decoder, Liftoff, TurboFan wasm pipeline, DrumBrake) | WACS, vendored as `src/V8Sharp.Wasm` (decoder, validator, store, interpreter; Apache-2.0, see its README), and a port of Liftoff that emits IL (`src/V8Sharp/Wasm/Baseline/`, section 9). The JS API on top (`wasm-js.cc`, the JS-facing parts of `wasm-objects.cc` and `module-instantiate.cc`, the JS-to-wasm and wasm-to-JS wrappers) is ported in `src/V8Sharp/Wasm/`. Differences: deviations.md, "WebAssembly". |
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
    internal readonly long _bits;         // the double payload's bits when _obj is NumberTag
    internal double _num => BitConverter.Int64BitsToDouble(_bits);
}
```

The payload is stored as a `long` so that the value is two integer words
for the JIT: the System V x64 ABI has no callee-saved XMM registers, so a
double that lives across calls (the dispatch loop's accumulator) would sit in
a stack slot, while a long gets a callee-saved general register (the
accumulator is then two registers, as V8's is one).

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
  (`JSObject.InObjectSlot`, span indexing, no `unsafe`). Arguments objects
  derive from the two-slot class. Other JSObject subclasses (arrays,
  functions, ...) keep their in-object fields at the front of the
  PropertyArray. `FieldIndex` encodes V8's (in-object, index)
  split plus `StorageIndex`, the physical location, which is what inline
  caches store with the map (`JSObject.FieldAt`).
- The receiver header is V8's three words: `Map`, `_fields` (V8's
  properties_or_hash: the PropertyArray, or in dictionary mode an array
  whose last element is the dictionary) and `Elements`; the identity hash
  and per-class flag bits share the word with `InstanceType`
  (`HeapObject`). A plain object with two in-object fields is 80 bytes
  (16 of CLR header, 8 + 3 x 8 of header words, 2 x 16 of slots). A new
  object is one allocation; elements start as the shared empty FixedArray.
  Reference stores that leave the reference half of a slot unchanged write
  only the payload (`JSValue.StoreSlot`), skipping the GC write barrier.
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
- **Interpreter frame layout** (`Interpreter/InterpreterFrames.cs`,
  `InterpreterRuntime.k*Offset`; what the baseline and optimizing tiers,
  deoptimization and the stack walker rely on). A frame is a window of
  `Isolate.RegisterStack` around its frame pointer `fp` (an index), laid out
  as V8's x64 `InterpreterFrameConstants`:

  ```
  fp - 10 - i   argument i (parameter register a_i); argc may exceed the
                formal count, the window holds max(argc, formals) arguments
  fp - 9        receiver                      (kReceiverOffset)
  fp - 8, -7    unused (V8's caller fp / return address; the frame record
                links frames instead)
  fp - 6        current context               (kContextOffset)
  fp - 5        closure                       (kClosureOffset)
  fp - 4        argc, a raw int               (kArgcOffset)
  fp - 3        bytecode array                (kBytecodeArrayOffset)
  fp - 2        bytecode offset, a raw int    (kBytecodeOffsetOffset)
  fp - 1        feedback vector or undefined  (kFeedbackVectorOffset)
  fp + 0 ...    register file r0 .. r(RegisterCount - 1)
  ```

  Each frame value is held once, in its fixed slot, as in V8 (no copy in
  `InterpreterState` or the record). A raw int slot has a null object half
  and the int in the payload (`InterpreterRuntime.FramePc`/`FrameArgc`,
  `SetFramePc`), so storing the offset is one 4-byte store with no write
  barrier. A register operand `o` addresses `fp - 7 - o`
  (`kRegisterOperandBase`, `Register::FromOperand`), so `r0` is operand -7,
  the receiver operand 2 and `a_i` operand 3 + i, as V8 encodes them in the
  bytecode. Every entry (EnterFrame, the inline calls, baseline and Maglev
  calls, Maglev's inlined frames, the deoptimizer) writes all six fixed
  slots; `InterpreterRuntime.InitializeFrameSlots` writes bytecode, offset
  and argc.

  The window starts at the stack top before the call (`RegisterStart`) and
  ends at `fp + RegisterCount` (`RegisterStackTop`). Slots above the top are
  undefined except below `RegisterStackDirtyEnd`, where a returned inline
  frame left its values; a new frame clears the part of its register file
  below that mark (V8's register file fill) and compares before storing
  each fixed slot's reference, so a repeated call at the same position pays
  no GC write barriers for them.

  Each frame also has an `InterpreterFrameRecord` in
  `Isolate.InterpreterFrames[0 .. InterpreterFrameDepth)`: `Fp`, one
  `Flags` byte (`Builtin`, `Constructor`, `Baseline`, `Maglev`,
  `InlineCall`), and for frames entered from a caller's dispatch loop without
  a .NET call `ReturnPc` (on the caller's record) and `RegisterStart`. A
  builtin frame has no register window and keeps `BuiltinFunction` and
  `BuiltinReceiver` in its record. The stack walker, `Error.stack`,
  `arguments` materialization and the debugger read the record and the
  frame's slots (`GetFunction`, `GetBytecode`, `GetPc`, `GetArgc`). Missing
  arguments are undefined in the parameter slots (V8's argument adaptation).

  While a frame runs, the dispatch loop (`InterpreterExecution.Loop<TS>`)
  keeps `ip` (a `ref byte` into the bytecode), `fpSlot` (a `ref JSValue` at
  fp) and the accumulator in locals, and reads the function, bytecode,
  constants, feedback vector and context from the fixed slots through
  `fpSlot` (`InterpreterRuntime.Frame*`), as V8's handlers load them from
  the frame. The `ref struct InterpreterState` holds only what is not in
  the frame: the isolate, the spilled accumulator and offset, `Fp`,
  `FrameIndex`, `BaseFrameIndex` and the OSR/return flags; its `Function`,
  `Bytecode`, `FeedbackVector`, `Context` and `Argc` are properties over
  the slots. A JS-to-JS call from the loop pushes the callee's frame and
  record and continues in the same loop (`InterpreterInlineCalls`);
  `Return` pops back to the caller's record (`ReturnPc`). The call protocol
  of such an inline call, in order: the callee's
  `SharedFunctionInfo.InterpreterCallMode` (or `InterpreterConstructMode`
  for `new`; cached, reset by the setters of the fields they depend on)
  says whether it runs in the loop; a sloppy callee gets the global proxy
  for an undefined receiver inline; the entry reserves parameters, fixed
  slots and register file at the stack top, clears the register file below
  `RegisterStackDirtyEnd`, copies the receiver and arguments from the
  caller's registers into the parameter slots (the only copy; V8's
  InterpreterPushArgsThenCall pushes them too), writes the six fixed slots,
  pushes the record (four fields) at `InterpreterFrameDepth` (the caller's
  record is the one below it, which gets `ReturnPc`) and sets
  `InterpreterState`'s `Fp`, `FrameIndex`, `Pc`, accumulator and
  `ResumeFp`/`ResumeIp` (ref fields: the callee's slot at fp and its first
  bytecode, which the loop continues from without recomputing them). There
  are two entries with this effect. `TryEnterFast` is the common case and
  calls nothing (the call handlers, getters and setters use it): the callee's
  call mode is inline, it has a feedback vector and materialized constants,
  the call feedback needs only its count bumped, and the frame fits under
  `Isolate.RegisterStackInterruptLimit`, V8's interrupt stack limit (the
  register stack limit, or 0 while `StackGuard` has an interrupt pending), so
  one compare covers overflow and interrupts as in V8's
  InterpreterEntryTrampoline; it does every scalar store before the
  reference stores. Slots reserved below the frame (`reservedBelow`: the
  argument window of `f.apply(thisArg, arguments)`, `TryApplyFast`) start at
  the old stack top, which becomes the record's `RegisterStart`, and are
  released with the frame. Otherwise it changes nothing and the handler takes
  `EnterInline` (the general entry, which other call sites such as
  `f.call`, the general `f.apply` and `new` use), which handles interrupts,
  overflow, feedback allocation and receiver conversion. `new` has the same
  split: `TryConstructFast` (a monomorphic ordinary constructor that is its
  own new.target, an initial map in fast mode) checks everything, allocates
  the receiver (FastNewObject, the one call) and enters through the shared
  `EnterFastCore` with the construct stub's slots reserved below the frame,
  the `Constructor` flag on the record and new.target in its register;
  `TryPushConstructFrame` is the general construct entry. A return (`ReturnInline`) restores
  `Fp`, `FrameIndex`, `Pc` and the resume refs from the caller's record (the
  context comes from the caller's slot), keeps the loop's accumulator as the
  result (a construct frame's receiver replaces it), and leaves the callee's
  slots and record as they are. A handler that returns a value to the loop
  (GetNamedProperty, the call handlers) returns
  `InterpreterInlineCalls.FrameEntered`, a marker no JavaScript value can be,
  when it entered a frame instead. Calls from builtins and runtime
  code enter a new loop through `InterpreterExecution.EnterFrame` / `Run`,
  which is also where the baseline tier enters (OSR from `JumpLoop` sets
  `InterpreterState.OsrToBaseline`, and `Run` continues the frame in
  baseline code at `Pc`). Baseline code keeps `fpRef` in a local and stores
  the offset into the slot (`BaselineAssembler.StoreBytecodeOffset`);
  Maglev code does the same per frame, inlined frames included. A tier
  that materializes an interpreter frame (deoptimization) writes the fixed
  slots (closure, feedback vector and bytecode of the translated function,
  the offset), the register file and the record, and continues it
  through `Run`.
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
  V8's transitions, because the optimizing tier reads them. Handlers are
  objects (`LoadHandler`/`StoreHandler`); a field handler stored beside its
  map (`FeedbackNexus.EncodeHandler`) also carries its field index (and a
  store's representation) in the payload of the JSValue that holds it,
  which is otherwise unused for an object and not part of its identity, so
  the interpreter's field loads and stores read it as V8 reads a Smi
  handler. Readers of the slots that test the handler object are unaffected.
- `Interpreter` is a `switch` dispatch loop over the bytecode array, with the
  accumulator and the register window as locals. As V8's handlers do
  (`StarDispatchLookahead`, `Bytecodes::IsStarLookahead`), the loads,
  property loads, arithmetic, call and construct handlers and an inline
  return store the accumulator into a following short Star (`Star0` ..
  `Star15`, the last opcodes, one compare) without dispatching it; the long
  `Star` likewise runs a following `Ldar` (a V8Sharp addition). The frequent
  Wide forms of huge functions (register moves, context loads, keyed and
  named loads, keyed and named stores, Smi arithmetic, `JumpLoop`) and
  the frequent constant-pool jumps are decoded by the single-scale loop itself,
  and the Wide calls and `Construct` run the single-scale call handlers
  (`WideCall`; the handlers enter a callee for both scales); the rest
  of the prefixed bytecodes run one step in `Loop<DoubleScale>` /
  `Loop<QuadrupleScale>` (`RunPrefixed`). The loop is compiled once, at full
  optimization, on its first call, so every class whose statics it reads
  must be initialized before then (the `Isolate` constructor runs
  `InterpreterInlineCalls`' class constructor): otherwise RyuJIT emits a
  class-initialization check at each read.

## 9. Tiers (after the interpreter is conformant)

V8: Ignition -> Sparkplug (baseline) -> Maglev (mid-tier, SSA + feedback) ->
Turbofan/Turboshaft. V8Sharp keeps the tiering policy (interrupt budget,
`TieringManager`, OSR at `JumpLoop`) and replaces code generation with IL:

1. **Baseline (Sparkplug analogue).** A single pass from bytecode to IL
   (Reflection.Emit): one IL block per bytecode, same frame layout, the
   ICs' monomorphic hits and number fast paths inline, calls into the same
   runtime/IC helpers otherwise. Removes dispatch and operand decoding; RyuJIT
   does register allocation. Implemented in `src/V8Sharp/Baseline/`
   (section 9.1).
2. **Optimizing (Maglev analogue).** A graph built from bytecode + feedback:
   speculative Smi/double/int32 representations held in unboxed IL locals,
   map checks, inlined property loads at known field indices, inlined
   builtins, `Vector<T>` for eligible loops. Speculation failures
   **deoptimize**: the frame state is materialised back into an interpreter
   frame and execution continues in Ignition, as V8's deoptimizer does.
3. RyuJIT's own tiering (tier-0/tier-1, dynamic PGO) sits below both.
4. **WebAssembly (Liftoff analogue).** `src/V8Sharp/Wasm/Baseline/` is
   liftoff-compiler.cc's single pass over a validated function body, with
   LiftoffAssembler's value stack (each value on the IL evaluation stack, in
   an IL local, a constant or a wasm local; spilled at control-flow joins so
   the IL stack is empty at every branch). Each function becomes one
   DynamicMethod `R f(WasmCode, P...)` with wasm's numbers unboxed, v128 as
   `Vector128<byte>` and references as WACS `Value`s; RyuJIT's optimizing
   compile stands in for TurboFan, so there is one tier. Functions compile
   lazily on first call through a stub in the instance's function table
   (V8's lazy compile table), and compile their direct callees ahead so those
   calls are direct IL calls. Instructions without IL (GC objects, tables,
   atomics, relaxed SIMD) run WACS's instruction object on its operand
   stack, and a function the compiler declines runs in the interpreter, which
   is also the whole tier under `--wasm-jitless`/`--jitless`. Compiled frames
   are recorded in a per-thread array merged with the interpreter's frames
   for stack traces; wasm exceptions are .NET exceptions caught by IL
   exception filters. Deviations: deviations.md, "WebAssembly".
5. **asm.js (V8 14.7's src/asmjs).** `src/V8Sharp/AsmJs/` validates a
   "use asm" module and translates it to a wasm module in one pass
   (AsmJsParser over AsmJsScanner, emitting through
   `Wasm/WasmModuleBuilder.cs`), when the module's function is first
   compiled: Compiler runs AsmJsCompilationJob in place of the bytecode
   generator, and on success the function's SharedFunctionInfo holds
   AsmWasmData and the InstantiateAsmJs builtin. Calling the function
   instantiates the module (with the stdlib, foreign object and heap
   buffer as 14.7 checks them) and returns its exports; asm.js arithmetic
   and heap access use the 0xfa opcodes, which the wasm compiler emits
   inline. A validation or link failure reports V8's message and falls
   back to the bytecode. This tree removed asm.js; V8Sharp keeps 14.7's
   pipeline (deviations.md, "asm.js").

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
`VisitSingleBytecode`), `BaselineCompiler.Inline.cs` (the fast paths emitted
as IL), `BaselineCompiler.Feedback.cs` (which fast paths, from the
feedback), `BaselineCompiler.Registers.cs` (registers in IL locals),
`BaselineAssembler.cs` (baseline-assembler.h over an `ILGenerator`, through
`BaselineILEmitter`, which counts what RyuJIT's limits count),
`BaselineBuiltins*.cs` (what the Visit* methods call), `BaselineCalls.cs`
(calls from baseline code), `BaselineCode.cs` (Code of kind BASELINE),
`BaselineExecution.cs` (entry, OSR, budget interrupts),
`BaselineBatchCompiler.cs` (batches, concurrent compilation), `Baseline.cs`
(CanCompileWithBaseline), `Execution/TieringManager.cs`,
`Codegen/Compiler.Baseline.cs` (CompileSharedWithBaseline, CompileBaseline,
CompileAllWithBaseline).

**Code shape.** A baseline function is one static method
`JSValue (BaselineCode, Isolate, ref InterpreterState)` (the entry delegate
is closed over the BaselineCode) in its own type of a process-wide
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
The accumulator, the current context and the feedback vector live in IL
locals, and the feedback slots, the constant pool and the bytecodes as refs
to their first elements (read at the operands' fixed offsets without bounds
checks); the prologue takes the closure, context and feedback vector from
their frame slots and the constant pool and bytecodes from the code object
(the method's first argument). The context is also written to its frame slot
and to `isolate.Context` where the interpreter does it. In functions without exception handlers that are not
resumable, the registers r0..rN (up to 64) live in IL locals too, and the
frame copy is written only where something reads it (register lists passed
to calls, the back edges' interrupt call) and reloaded where a builtin
writes it (header of BaselineCompiler.Registers.cs); parameters and the
fixed slots always stay in the frame. Register stores to the frame compare
before storing, as the interpreter's do, to skip the GC write barrier when
the value is unchanged. The record's `IsBaseline` flag tells
`%GetOptimizationStatus` the frame's tier.

**Bytecode offset.** Before every bytecode that can throw or call out, the
code stores the bytecode offset (after any prefix, as the interpreter does)
in the frame's offset slot, a 4-byte store through a ref the prologue
computes (`PcRef`). V8 recovers the offset from the return address through
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

**Fast paths.** The hot paths of the interpreter's cases are emitted as IL
(BaselineCompiler.Inline.cs), reading the interpreter's and the ICs' data
structures directly: Smi and number arithmetic, bitwise operations and
comparisons (a comparison followed by a conditional jump branches directly),
ToBoolean, TestUndetectable/TestNull/TestUndefined, context slots, global
loads from a PropertyCell, and the monomorphic hits of the named and keyed
load and store ICs (own fields, prototype constants, array length, in-bounds
FixedArray and FixedDoubleArray elements). Each fast path also records the
feedback the interpreter would (or checks that the feedback would not
change); anything else calls the out-of-line builtin, which is the complete
operation. Which paths are emitted is decided per bytecode from the feedback
at compile time (BaselineCompiler.Feedback.cs): an operation that never ran,
or whose feedback rules the fast path out, gets only the builtin call; for
an operation whose feedback is already Number, the feedback check is left
out. `--always-sparkplug` emits every fast path. Some paths are specialized
to the compile-time feedback, each guarded at run time so that any other
feedback takes the general path: a monomorphic own-field load or store
compares the handler slot's payload (the encoded field index,
`FeedbackNexus.EncodeHandler` / `StoreIC.EncodeFieldStore`) and accesses
the field at its offset; a keyed access on a typed array calls the element
load or store of the feedback map's kind (`LoadTypedElementBits`,
`TryStoreTypedElement`); a polymorphic named access calls
`LoadPolymorphicOwnField` / `TryStorePolymorphicOwnField`. These helpers
and the number paths' feedback checks of the out-of-line form (below) are
`AggressiveInlining | AggressiveOptimization`: inlined where RyuJIT inlines,
fully optimized where it leaves them called.

**Calling helpers.** Every other bytecode calls a static method of
`BaselineBuiltins` that does what the interpreter's case does (the same IC
entry points, runtime functions, `InterpreterOps`, `InterpreterCalls`).
The slow paths of the inline code are `NoInlining` so that RyuJIT does not
grow the baseline method with them; all builtins are
`AggressiveOptimization` (at RyuJIT's tier 0 they were slower than the
interpreter). The out-of-line paths of the IC bytecodes try the hits of
V8's *IC_Baseline builtins first (own fields from the handler payload,
monomorphic or polymorphic; polymorphic fast elements; typed arrays)
before entering the IC.

**Calls.** `BaselineCalls` handles the call bytecodes. A JSFunction callee
whose SharedFunctionInfo has baseline code (and no exception handlers) is
entered directly: `EnterInline<TArgs>`, inlined into each call bytecode's
stub (`CallProperty0` ...), checks the register stack against
`Isolate.RegisterStackInterruptLimit` (overflow and pending interrupts in
one compare), pushes the frame record and the register window, copies the
arguments from IL locals or a register list (`ICallArguments` structs, so
the copy is specialized per form), and calls the callee's entry delegate; the teardown is not in a `finally` (a throw
leaves the frame stack to the catching frame's handler lookup, which
restores it). Interpreted callees enter the interpreter's `Run` on the same
frame protocol. `Function.prototype.call` and `apply` with a JSFunction
target enter the target without a builtin frame. Anything else (bound
functions, proxies, API functions, builtins without a fast path) goes
through `InterpreterCalls`. The .NET stack is checked every eighth call
depth, or every call for functions with more than 1024 bytes of bytecode.

**Size: out-of-line checks, chunks, compact code.** RyuJIT compiles a method
beyond its limits (IL size, instructions, basic blocks, local references)
with MinOpts, which is slower than the interpreter. `BaselineILEmitter`
counts them while the method is emitted (calibrated against RyuJIT's own
decisions: `V8SHARP_BASELINE_IGNORE_LIMITS=1` with `DOTNET_JitDisasmSummary`).
A function that would exceed them is emitted again in the out-of-line form:
the number paths' feedback checks call `BaselineBuiltins.BinaryFeedbackUnchanged`
/ `CompareFeedbackUnchanged` (about a third of the IL; RyuJIT counts a
method's own IL, not its inlinees'), and IC sites whose feedback is still
empty get only the builtin call. If that still exceeds the limits, the
function is compiled as several methods, one per range of its bytecode
(chunks, `BaselineCode.GenerateChunks`; ranges sized from the full form's
counts and cut at the least loop depth). A jump out of a chunk goes to its
exit sequence, which spills the cached registers (skipping unchanged
references), leaves the accumulator and the target offset in the state and
returns `BaselineCompiler.ChunkExitMarker`; `BaselineCode.RunChunks` then
enters the target's chunk, whose prologue dispatches to the offset as for
an OSR entry. A range still over the limits below 256 bytes is compiled
without fast paths and register locals (compact code). Functions with more
than 100000 bytes of bytecode do not tier up by themselves
(`BaselineSupport.TiersUpToBaseline`; `%CompileBaseline` still compiles them).
`V8SHARP_BASELINE_OUT_OF_LINE_CHECKS=1` and `V8SHARP_BASELINE_CHUNK=n` force
the forms for testing.

**Code cache.** Baseline methods read everything not in the bytecode at run
time, so functions with the same bytecode (embedded feedback masked out),
handler table, register file shape, resumable kind and number constants
share the compiled methods (`BaselineCodeCache`): code evaluated again
(Octane CodeLoad) gets its code without IL emission or RyuJIT.

**Tiering.** `TieringManager.OnInterruptTick` is ported: the first budget
interrupt allocates the feedback vector (budget `invocation_count_for_feedback_allocation`
(8) x bytecode length) and, once `--invocation-count-for-sparkplug` (64, a
V8Sharp deviation; V8 enqueues at the first interrupt) invocations' worth of
budget ran out, the function is enqueued to the batch compiler (V8's
defaults: `--sparkplug`, on, `--baseline-batch-compilation`, threshold 4 KB
of estimated code). Later ticks raise the budget, or with `--maglev` tier up
to Maglev (section 9.2). A call to a function whose SharedFunctionInfo has baseline code
enters it (allocating the feedback vector first if needed,
Runtime_InstallBaselineCode). `--always-sparkplug` compiles every function
when its bytecode is finalized. A running interpreter frame switches at its
next `JumpLoop` (OSR to baseline, `InterpreterOnStackReplacement_ToBaseline`):
the dispatch loop returns to `Run`, which continues the same frame in the
baseline code at the loop header. `--jitless` implies `--no-sparkplug`.
The interrupt budget's runtime call on a back edge is in one stub per method
that every loop's budget check jumps to (with the loop's index), so the
loops' code stays small; the stub also enters Maglev OSR code for the loop
when there is some (`BaselineExecution.BudgetInterruptOnJumpLoopOsr`).
Calls between the tiers are direct: baseline code enters a callee's Maglev
code (`BaselineCalls.EnterMaglev`, also for `new` and `f.apply`), and Maglev
code enters a callee's baseline code (`BaselineCalls.CallFromOptimizedCode`).

**Code generation threads.** The bytecode offset is stored only before calls
that can throw or call out, as IL (RyuJIT stops inlining in big methods:
what must be cheap there is emitted as IL, not called). Each thread that
emits code has its own dynamic assemblies (`BaselineCodeSpace`); the
Sparkplug batches have a background thread (`BaselineCompileThread`), and
the Maglev jobs run on the dispatcher's worker threads
(`MaglevConcurrentDispatcher`, section 9.2).

**Concurrent compilation and RyuJIT.** With `--concurrent-sparkplug` (the
default, as in V8 on x64), a batch is compiled by one process-wide background
thread (`BaselineCompileThread`; V8: `ConcurrentBaselineCompiler` on the
platform's workers). The main thread materializes the constant pools and the
names; the background task emits the IL and has RyuJIT compile the method
fully optimized (`AggressiveOptimization`, `RuntimeHelpers.PrepareMethod`),
so no time is spent in tier-0 code; the main thread installs the code at the
next INSTALL_BASELINE_CODE interrupt. This is what makes Sparkplug's
"compile fast" property hold on the main thread: an optimized compile costs
about 1-35 ms per function (median ~6 ms in PdfJS). Without concurrency
(`--no-concurrent-sparkplug`, `--always-sparkplug`, `%CompileBaseline`),
methods of the (non-collectible) dynamic assembly take part in RyuJIT's
tiered compilation: tier 0 at the first call, tier 1 with dynamic PGO when
hot, and OSR for long-running loops in tier-0 code.
`V8SHARP_BASELINE_DYNAMICMETHOD=1` switches back to collectible
DynamicMethods for comparison; `V8SHARP_BASELINE_TIERED=1` makes
concurrently compiled code start at tier 0 too,
`V8SHARP_BASELINE_NO_FEEDBACK_GUIDANCE=1` emits every fast path,
`V8SHARP_BASELINE_NO_REGISTER_CACHE=1` keeps the registers in the frame, and
`V8SHARP_BASELINE_IL_PROFILE=1` prints the IL emitted per bytecode at exit.

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
method with the same locals. Functions with handlers are not inlined;
calls inside try blocks are (a throw in the inlined code continues at the
caller's catch block, whose state merges the caller's frame at the call;
EnterCatchBlock drops the inlined frames).

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
makes deopts of inlined code cheap; the push is lazy, before the first node
of the inlined body that can observe frames (calls, nodes that can throw or
deoptimize lazily); an eager deopt pushes the frames it needs. The current bytecode offset of every
frame is stored in its record before anything that can throw or call.

**Direct calls.** Each code has a second IL method, its direct entry
(`MaglevCode.FastCall`: `JSValue FastCall(MaglevCode, Isolate, JSFunction,
int argc, JSValue receiver, JSValue a0, ...)`, up to six arguments; a
function that reads its actual arguments takes six and keeps argc of them
in the frame). It builds the frame with the function's constants (bytecode,
feedback vector, register count) without clearing the register file, runs
the code, and pops the frame (inline after the call, in a fault block for
exceptions). `CallKnownJSFunction` (a call of a constant JSFunction that is
not inlined) loads the callee's code from its feedback vector at run time
and invokes the entry with the receiver and arguments as values; generic
calls (`MaglevCalls.Call`) and constructs (`ConstructWithReceiver`, argc's
sign bit set, new.target in `Isolate.MaglevNewTarget`) use it too.
Parameters assigned by the code stay in IL locals and reach the frame only
before nodes that can observe it (`InterpreterFrameState.DirtyParameters`).
A code body of at most 1200 bytes of IL without exception handlers is
AggressiveInlining, so RyuJIT compiles it into the direct entry (one .NET
call per JS call). Generic calls with up to three arguments pass them as
values (`MaglevCalls.CallWithValuesN`): a callee with Maglev code is
entered through its direct entry without going through the register stack.
The concurrent compile job prepares (JITs) the direct entry as well.

**Frameless entries.** As V8's optimized frames are not interpreter
frames, code that cannot observe its frame (a leaf: no calls, throws, lazy
deopts or frame reads and writes; eagerly pushed inlined frames excluded;
loops are allowed, their interrupt checks deoptimize) gets a second code generation pass from the same graph
(`MaglevCodeGenerator.TryGenerateFramelessEntry`): a method with the direct
entry's signature that builds no frame and no frame record. Its InitialValues
are the entry's arguments (the closure, its context, the receiver and the
parameters), and its eager deopt exits spill the translation to the deopt
scratch buffer (the receiver and the arguments in its first slots, at the
end of each spill chain) and call `MaglevCalls.DeoptimizeFrameless`, which builds the frame the direct entry
would have built, deoptimizes into it and continues in the interpreter. The
two passes share the code's deopt points. When the graph turns out to
need the frame (the second pass touches it), the frameful direct entry is
used.

**Allocations and escape analysis.** `new` of a known constructor (with
the constructor inlined), object literals of primitive fields and `{}` are
`InlinedAllocation` nodes (MaglevGraphBuilder.Allocation.cs): the IL
constructs the object of the map's in-object slot class
(MaglevCodeGenerator.Allocation.cs). While an allocation has not escaped
(no use but tracked stores into it, deopt frames and the frames of inlined
calls that are never pushed), KnownNodeAspects keeps its `VirtualObject`
(map and in-object fields, a new immutable version per store, including map
transitions); loads of its fields are the stored values and deopt frames
capture the current `VirtualObjectList`. After the graph is built,
`MaglevEscapeAnalysis` elides the allocations nothing lets escape (with
those stored in their fields) and removes their stores; a deopt exit spills
the fields of the elided objects its frames hold, and the Deoptimizer
materializes them first (`DeoptPoint.CapturedObjects`, V8's captured
objects, one object per allocation however many registers hold it). Calls
that pass a non-escaped allocation are inlined deeper, so constructors
that call an initializer (Class.create, Box2D's) keep the object virtual;
an inlined function's arguments object is virtual for apply(thisArg,
arguments). Contexts and closures are allocated by frame-free helpers.

**Load elimination.** KnownNodeAspects keeps loaded fields (by object and
storage index), elements, array, FixedArray and typed array lengths,
global property cell values, and context slots; stores update their own key
and forget aliases of it, an elements kind transition forgets only the maps
of objects that may have a source map (and elements), and nodes that can
run arbitrary code clear everything (MarkPossibleSideEffect). V8Sharp
assumes a loop without calls changes nothing, checks that at the back edge
(`LoopEffects`) and, when it did change something known at the header,
builds the graph again with those effects (`MaglevRestartException`).
Innermost loops without calls under 400 bytes of bytecode (900 per
compilation) are peeled (`PeelLoop`, V8's non-optimistic mode): the first
iteration is built as straight-line code from the merged forward edges,
its back edge enters the loop, and the loop's interior merge states start
again, so the checks and loads of loop-invariant values happen once, in the
peeled iteration.

**Property access.** Loads of fields take the field's representation and
field type from the descriptor of the receiver's (or holder's) map
(ComputeDataFieldAccessInfo): a Double field is `LoadDoubleField` (the
payload, no number check), a Smi field's value is untagged unchecked, and a
HeapObject field with a stable class field type has that map, with field
representation, field type and stable map dependencies; a const field of a
constant object is folded to its value (FieldConst dependency). Double field
stores are `StoreDoubleField` (the payload of the Float64 value, no write
barrier). A polymorphic load of methods followed by a call of the loaded
value (`GetNamedProperty; Star r; ...; CallProperty r`) builds one arm per
map group that loads its constant and replays the bytecodes up to the call,
which then has a constant target (inlined if small, a direct call
otherwise); the arms' frames merge after the call (V8's polymorphic load
continuations). Element, context slot and in-object transition stores are
written inline through the slot's address, numbers as payloads.

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
exits, and OSR code deopting outside its loop); as in V8 the function is
optimized again after any number of deopts (`V8SHARP_MAGLEV_MAX_DEOPTS` sets
a limit). A
deopt of a check made while reducing a builtin call disallows speculation
on the call's feedback (out of bounds first only disallows bounds-check
speculation), as V8's feedback_to_update.

**Dependencies.** Code depends on stable maps (prototype chains, known
maps), property cells (global constants), initial maps (FastNewObject) and
protectors. `DependentCode` keeps a weak table from the object to the code;
the object model calls `DeoptimizeDependencyGroups` where V8 does, which
marks the code (lazy deopt).

**Types and map checks.** As V8's graph builder, a phi's type is the union
of its inputs' known types on their edges (a loop phi's type is unknown in
the loop body and becomes the union after the back edge is merged,
post_loop_type), and the result phis of inlined calls and polymorphic
accesses get the union of the arms' types. A map load (CheckMaps, LoadMap,
TransitionElementsKind) records the known type of its input where it is
built (CheckType): only JSReceivers have a map field in V8Sharp, so the check
V8's kCheckHeapObject stands for is a receiver check (object half non-null,
instance type), omitted when the input is known to be a receiver. Prototype
chain validity is a compilation dependency on the IC handler's validity
cell (`JSObject`'s invalidation of the cell deoptimizes the code), so no
check runs in the optimized code (V8 depends on the stable maps of the chain).

**Calls of closures.** A call whose feedback is a FeedbackCell (the closures
of one CreateClosure site) checks the callee's feedback cell
(CheckJSFunctionFeedbackCell) and calls the shared function's code directly,
or inlines it with the closure's context loaded from the closure; the
inlined frame's closure is the run-time value (spilled for deopts).

**Huge functions.** A graph whose IL exceeds RyuJIT's optimization limits
(which compile such a method with MinOpts) is emitted again in regions
(`MaglevCodeGenerator.Regions.cs`): consecutive blocks cut at the least
loop depth outside inlined bodies, each a method of 40% of the limits as
the first emission measured them. The code's own method dispatches over
the regions; an edge into another region stores the values live into its
target (SSA liveness over the graph) and the target's phis into a Transfer
struct local to the dispatcher and returns the target's entry id, which the
target region's entry stub loads. Regions have their own deopt exits and
share the deopt points.

**Locals.** Values whose live ranges do not overlap share an IL local
(`MaglevCodeGenerator.TryAllocateSharedLocals`, the role of maglev-regalloc):
ranges run from a value's definition to its last use, counting deopt frame
values, phi inputs at the predecessor's end and lazily pushed inlined
frames, and a value live into a loop is live through it. This keeps a
method's locals below RyuJIT's inlining (512) and MinOpts (2000) limits.
There is no IL size limit beyond V8's bytecode size limits: RyuJIT compiles
a method over its optimization limits with MinOpts, which still beats the
lower tiers; methods over 20000 bytes are `AggressiveOptimization` (RyuJIT's
tier 0 of big methods is MinOpts).
Deopt exits spill only non-constant values (constants are literals of the
deopt point). Every spilled value has its own slot of the scratch buffer
(`DeoptFrameData.ScratchSlots`), so the code that stores a value is the same
for every exit: an exit stores what one of the last 16 exits' spill blocks
does not and jumps to it, and the chain ends in `MaglevBuiltins.Deopt0`.
Consecutive exits share most of their live values, so an exit costs a few
stores (zlib's biggest function: 434 KB of exit IL before, 33 KB after).

**Code that RyuJIT compiles well.** RyuJIT's profile synthesis knows
nothing of how often a branch is taken: it gives a branch out of a loop 10%
and any other conditional branch 48%, but a path that ends in a throw 0%.
With deopt exits that returned, a loop with a few checks looked barely
hotter than its exits and its values lived in stack slots. So the exits are
cold for it: they run in a try region of their own, entered through a
switch on the exit, whose spill chains end in `throw new
MaglevDeoptUnwind()`; the region's handler returns the deopt's result. A
small body without loops that its direct entry inlines keeps plain exits
(RyuJIT does not inline methods with exception handlers). For the same
reason a loop's back edge has no call: in a loop without calls the
interrupt check leaves code installation pending and deoptimizes, without
invalidating the code (`kInterrupt`), for the other interrupts; loops with
calls serve interrupts through a call whose slow path stores the bytecode
offset and pushes lazily pushed inlined frames. Locals are never address
exposed (an `out` parameter made the shared scratch local unenregistrable
everywhere). The disassembly of the generated methods is the check:
`DOTNET_JitDisasm='*maglev:name*'` (the methods are `maglev:<function>`,
`...@osr<offset>`, `...:frameless` and `FastCall` of the code's type) and
`DOTNET_JitStdOutFile`; `perf` with `DOTNET_PerfMapEnabled=1` and
`DOTNET_EnableWriteXorExecute=0` names them in profiles.
The IL goes through `MaglevILEmitter`, which counts what RyuJIT's
optimization limits count and uses the short constant encodings.

**Tiering.** `--maglev` (on by default, as in V8) makes
`Isolate.UseOptimizer` true; `TieringManager.OnInterruptTick` requests a
compile once a function's invocation count reaches
`--invocation-count-for-maglev` (400, V8's). With `--concurrent-recompilation`
(the default) the request is a `MaglevCompilationJob`
(MaglevConcurrentDispatcher.cs, maglev-concurrent-dispatcher.cc): PrepareJob
on the main thread (the bailouts that need no graph, the constant pool, the
start of the dependency log), ExecuteJob on one of two worker threads (graph
building, the passes, the IL and RyuJIT's fully optimized compile), and
FinalizeJob at the next INSTALL_MAGLEV_CODE interrupt, which commits the
dependencies and installs the code on the feedback vector, which every
closure of the CreateClosure site shares. The worker reads the heap without
writing it: the (map, handler) pairs of property feedback are read under the
vector's pair write sequence (the main thread's `FeedbackNexus.SetFeedback`
of a pair is a seqlock writer), map updates do not record migration targets,
and functions whose constant pool the interpreter has not materialized are
not inlined. While jobs are open, `DependentCode.DeoptimizeDependencyGroups`
logs every invalidation; the commit discards code that depends on an object
invalidated after its job was prepared (it is compiled again later). The
job's `FeedbackVector.TieringInProgress` stands for V8's optimization
request (V8 starts the job at the function's next call): ticks meanwhile
get the Maglev OSR budget and raise the OSR urgency. A frame stuck
in a loop (interpreted or baseline) OSRs at its next JumpLoop budget
interrupt (`MaglevExecution.TryGetOsrCode`, `RunOsr`); with
`--concurrent-osr` the OSR compile is a concurrent job too and a later back
edge enters the code. OSR code starts at the loop header with the frame's
registers as initial values; back edges of enclosing loops leave it
(kOSREarlyExit).
