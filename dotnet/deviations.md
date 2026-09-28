# V8Sharp: deviations from V8

Every intentional difference from V8, grouped by component, with the reason.
Replacing V8's GC, handles, snapshot and machine-code backends is by design
(architecture.md section 2) and is not listed. Each entry is also commented at
the site.

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
