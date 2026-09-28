// The shapes (data layout) of the special JSObject kinds, with the object-level
// operations V8 keeps next to them:
//   src/objects/arguments.h            JSArgumentsObject, SloppyArgumentsElements,
//                                      AliasedArgumentsEntry
//   src/objects/js-array-buffer.{h,cc} JSArrayBuffer, JSArrayBufferView,
//                                      JSTypedArray (+ DefineOwnProperty), JSDataView
//   src/objects/js-date.h              JSDate (the date math is in the Date builtins)
//   src/objects/js-regexp.h            JSRegExp
//   src/objects/js-promise.h, objects.cc JSPromise (+ Fulfill/Reject/Resolve/
//                                      TriggerPromiseReactions), PromiseReaction,
//                                      PromiseCapability
//   src/objects/js-generator.h         JSGeneratorObject and the async variants
//   src/objects/js-collection.h        JSMap, JSSet, JSWeakMap, JSWeakSet, iterators
//   src/objects/js-weak-refs.h         JSWeakRef, JSFinalizationRegistry
//   src/objects/js-iterator-helpers.h  JSIteratorHelper, JSValidIteratorWrapper
//   src/objects/js-disposable-stack.h  JSDisposableStackBase
//   src/objects/module.{h,cc}          JSModuleNamespace (exports as a name -> Cell table)
//   src/objects/js-raw-json.h, js-shadow-realm.h, js-external-object.h
//   src/objects/allocation-site.h      AllocationSite
//   src/builtins/builtins-shadow-realm-gen.cc  CallWrappedFunction, GetWrappedValue
using V8Sharp.Common;
using V8Sharp.Roots;

namespace V8Sharp.Objects;

// ---- Arguments --------------------------------------------------------------------

/// <summary>V8's JSArgumentsObject (sloppy and strict arguments objects).</summary>
public sealed class JSArgumentsObject(Map map) : JSObject(map)
{
    /// <summary>JSSloppyArgumentsObject::kLengthIndex / kCalleeIndex: in-object property indices.</summary>
    public const int kLengthIndex = 0;
    public const int kCalleeIndex = 1;
}

/// <summary>
/// V8's SloppyArgumentsElements: the elements of a mapped (sloppy) arguments
/// object. Entry i aliases context slot mapped_entries[i] while it is not the
/// hole; everything else lives in <see cref="Arguments"/>.
/// </summary>
public sealed class SloppyArgumentsElements(Context context, FixedArrayBase arguments, int length)
    : FixedArrayBase(InstanceType.FixedArrayType)
{
    readonly JSValue[] _mappedEntries = length == 0 ? [] : new JSValue[length];

    public Context Context = context;
    public FixedArrayBase Arguments = arguments;

    /// <summary>The number of mapped entries.</summary>
    public override int Length => _mappedEntries.Length;

    public JSValue MappedEntries(int index) => _mappedEntries[index];

    public void SetMappedEntries(int index, JSValue value) => _mappedEntries[index] = value;

    public void SetMappedEntry(int index, JSValue value) => _mappedEntries[index] = value;
}

/// <summary>V8's AliasedArgumentsEntry: a slow-mode arguments element that aliases a context slot.</summary>
public sealed class AliasedArgumentsEntry(int aliasedContextSlot) : HeapObject(InstanceType.FixedArrayType)
{
    public readonly int AliasedContextSlot = aliasedContextSlot;
}

// ---- ArrayBuffer and views ------------------------------------------------------------

/// <summary>V8's JSArrayBuffer. The backing store is a managed byte array.</summary>
public sealed class JSArrayBuffer(Map map) : JSObject(map)
{
    /// <summary>ArrayBuffer::kMaxByteLength (the port allocates managed arrays).</summary>
    public const ulong kMaxByteLength = int.MaxValue;

    public byte[] BackingStore = [];
    public ulong ByteLength;
    public ulong MaxByteLength;
    public bool IsShared;
    public bool IsResizableByJs;
    public bool IsDetachable = true;
    public bool WasDetached;
    public bool IsImmutable;
    public JSValue DetachKey;

    /// <summary>JSArrayBuffer::Detach.</summary>
    public void Detach()
    {
        if (WasDetached) return;
        BackingStore = [];
        ByteLength = 0;
        WasDetached = true;
    }
}

/// <summary>V8's JSArrayBufferView: the common part of typed arrays and DataViews.</summary>
public abstract class JSArrayBufferView(Map map) : JSObject(map)
{
    public JSArrayBuffer Buffer = null!;
    public ulong ByteOffset;
    public ulong RawByteLength;
    public bool IsLengthTracking;
    public bool IsBackedByRab;

    public bool WasDetached => Buffer.WasDetached;
    public bool IsVariableLength => IsLengthTracking || IsBackedByRab;
}

/// <summary>V8's ExternalArrayType.</summary>
public enum ExternalArrayType
{
    kExternalInt8Array = 1,
    kExternalUint8Array,
    kExternalInt16Array,
    kExternalUint16Array,
    kExternalInt32Array,
    kExternalUint32Array,
    kExternalFloat16Array,
    kExternalFloat32Array,
    kExternalFloat64Array,
    kExternalUint8ClampedArray,
    kExternalBigInt64Array,
    kExternalBigUint64Array,
}

/// <summary>V8's JSTypedArray.</summary>
public sealed class JSTypedArray(Map map) : JSArrayBufferView(map)
{
    /// <summary>JSTypedArray::kMaxByteLength.</summary>
    public const ulong kMaxByteLength = JSArrayBuffer.kMaxByteLength;

    public ulong RawLength;

    public ElementsKind Kind => Map.ElementsKind;

    public int ElementSize => ElementsKinds.ElementsKindToByteSize(Kind);

    public bool IsBigIntArray => ElementsKinds.IsBigIntTypedArrayElementsKind(Kind);

    /// <summary>JSTypedArray::type.</summary>
    public ExternalArrayType Type
    {
        get
        {
            ElementsKind kind = ElementsKinds.IsRabGsabTypedArrayElementsKind(Kind)
                ? ElementsKinds.GetCorrespondingNonRabGsabElementsKind(Kind)
                : Kind;
            return kind switch
            {
                ElementsKind.INT8_ELEMENTS => ExternalArrayType.kExternalInt8Array,
                ElementsKind.UINT8_ELEMENTS => ExternalArrayType.kExternalUint8Array,
                ElementsKind.INT16_ELEMENTS => ExternalArrayType.kExternalInt16Array,
                ElementsKind.UINT16_ELEMENTS => ExternalArrayType.kExternalUint16Array,
                ElementsKind.INT32_ELEMENTS => ExternalArrayType.kExternalInt32Array,
                ElementsKind.UINT32_ELEMENTS => ExternalArrayType.kExternalUint32Array,
                ElementsKind.FLOAT16_ELEMENTS => ExternalArrayType.kExternalFloat16Array,
                ElementsKind.FLOAT32_ELEMENTS => ExternalArrayType.kExternalFloat32Array,
                ElementsKind.FLOAT64_ELEMENTS => ExternalArrayType.kExternalFloat64Array,
                ElementsKind.UINT8_CLAMPED_ELEMENTS => ExternalArrayType.kExternalUint8ClampedArray,
                ElementsKind.BIGINT64_ELEMENTS => ExternalArrayType.kExternalBigInt64Array,
                _ => ExternalArrayType.kExternalBigUint64Array,
            };
        }
    }

    /// <summary>The bytes of the view in its buffer (V8's DataPtr()).</summary>
    public Span<byte> DataSpan() => Buffer.BackingStore.AsSpan((int)ByteOffset);

    /// <summary>JSTypedArray::GetVariableByteLengthOrOutOfBounds.</summary>
    ulong GetVariableByteLengthOrOutOfBounds(out bool outOfBounds)
    {
        outOfBounds = false;
        ulong ownByteOffset = ByteOffset;
        if (IsLengthTracking)
        {
            ulong ownElementSize = (ulong)ElementSize;
            ulong bufferByteLength = Buffer.ByteLength;
            if (IsBackedByRab && ownByteOffset > bufferByteLength)
            {
                outOfBounds = true;
                return 0;
            }
            // Round down to the nearest multiple of element size.
            ulong available = bufferByteLength - ownByteOffset;
            return available - available % ownElementSize;
        }
        ulong ownByteLength = RawByteLength;
        ulong bufferLength = Buffer.ByteLength;
        if (ownByteLength > bufferLength || ownByteOffset > bufferLength - ownByteLength)
        {
            outOfBounds = true;
            return 0;
        }
        return ownByteLength;
    }

    /// <summary>JSTypedArray::GetLengthOrOutOfBounds.</summary>
    public ulong GetLengthOrOutOfBounds(out bool outOfBounds)
    {
        outOfBounds = false;
        if (WasDetached) return 0;
        if (IsVariableLength) return GetVariableByteLengthOrOutOfBounds(out outOfBounds) / (ulong)ElementSize;
        return RawByteLength / (ulong)ElementSize;
    }

    /// <summary>JSTypedArray::GetLength.</summary>
    public ulong GetLength() => GetLengthOrOutOfBounds(out _);

    /// <summary>JSTypedArray::GetByteLength.</summary>
    public ulong GetByteLength()
    {
        if (WasDetached) return 0;
        if (IsVariableLength) return GetVariableByteLengthOrOutOfBounds(out _);
        return RawByteLength;
    }

    /// <summary>byte_length() as V8's JSArrayBufferView field.</summary>
    public ulong ByteLength => RawByteLength;

    public bool IsOutOfBounds
    {
        get
        {
            GetLengthOrOutOfBounds(out bool outOfBounds);
            return outOfBounds;
        }
    }

    public bool IsDetachedOrOutOfBounds => WasDetached || IsOutOfBounds;

    /// <summary>The class name ("Uint8Array" ...), for JSReceiver::class_name.</summary>
    public static JSString TypedArrayClassName(ElementsKind kind)
    {
        if (ElementsKinds.IsRabGsabTypedArrayElementsKind(kind)) kind = ElementsKinds.GetCorrespondingNonRabGsabElementsKind(kind);
        return kind switch
        {
            ElementsKind.INT8_ELEMENTS => ReadOnlyRoots.Int8Array_string,
            ElementsKind.UINT8_ELEMENTS => ReadOnlyRoots.Uint8Array_string,
            ElementsKind.INT16_ELEMENTS => ReadOnlyRoots.Int16Array_string,
            ElementsKind.UINT16_ELEMENTS => ReadOnlyRoots.Uint16Array_string,
            ElementsKind.INT32_ELEMENTS => ReadOnlyRoots.Int32Array_string,
            ElementsKind.UINT32_ELEMENTS => ReadOnlyRoots.Uint32Array_string,
            ElementsKind.FLOAT16_ELEMENTS => ReadOnlyRoots.Float16Array_string,
            ElementsKind.FLOAT32_ELEMENTS => ReadOnlyRoots.Float32Array_string,
            ElementsKind.FLOAT64_ELEMENTS => ReadOnlyRoots.Float64Array_string,
            ElementsKind.UINT8_CLAMPED_ELEMENTS => ReadOnlyRoots.Uint8ClampedArray_string,
            ElementsKind.BIGINT64_ELEMENTS => ReadOnlyRoots.BigInt64Array_string,
            _ => ReadOnlyRoots.BigUint64Array_string,
        };
    }

    // https://tc39.es/ecma262/#sec-canonicalnumericindexstring
    // Returns true if the lookup_key represents a valid index string.
    static bool CanonicalNumericIndexString(Isolate isolate, in PropertyKey lookupKey, out bool isMinusZero)
    {
        // 1. Assert: Type(argument) is String.
        isMinusZero = false;
        if (lookupKey.IsElement) return true;

        var key = (JSString)lookupKey.Name!;

        // 3. Let n be ! ToNumber(argument).
        double result = JSString.ToNumber(key);
        if (result == 0 && double.IsNegative(result))
        {
            // 2. If argument is "-0", return -0𝔽.
            // We are not performing SaveValue check for -0 because it'll be rejected
            // anyway.
            isMinusZero = true;
        }
        else
        {
            // 4. If SameValue(! ToString(n), argument) is false, return undefined.
            JSString str = isolate.Factory.NumberToString(result);
            // Avoid treating strings like "2E1" and "20" as the same key.
            if (!JSString.Equals(str, key)) return false;
        }
        return true;
    }

    /// <summary>JSTypedArray::DefineOwnProperty (ES #sec-typedarray-defineownproperty).</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSTypedArray o, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        Debug.Assert(key.IsName || key.IsNumber);
        // 1. If Type(P) is String, then
        var lookupKey = new PropertyKey(isolate, key);
        if (lookupKey.IsElement || key.IsSmi || key.IsString)
        {
            // 1a. Let numericIndex be ! CanonicalNumericIndexString(P)
            // 1b. If numericIndex is not undefined, then
            bool isMinusZero = false;
            if (key.IsSmi ||  // Smi keys are definitely canonical
                CanonicalNumericIndexString(isolate, lookupKey, out isMinusZero))
            {
                // 1b i. If IsValidIntegerIndex(O, numericIndex) is false, return false.

                // IsValidIntegerIndex:
                ulong index = lookupKey.Index;
                ulong length = o.GetLengthOrOutOfBounds(out bool outOfBounds);
                if (o.WasDetached || outOfBounds || index >= length)
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.InvalidTypedArrayIndex);
                }
                if (o.Buffer.IsImmutable)
                {
                    // 10.4.5.3 [[DefineOwnProperty]] ( P, Desc )
                    // step 1.b.ii. If IsImmutableBuffer(O.[[ViewedArrayBuffer]]) is true...
                    // We need to validate that the new descriptor is compatible with the
                    // existing immutable property (which is non-configurable,
                    // non-writable).
                    var it0 = new LookupIterator(isolate, o, index, LookupIterator.Configuration.OWN);
                    JSValue currentValue = ObjectOps.GetProperty(ref it0);

                    if (PropertyDescriptor.IsAccessorDescriptor(in desc) || (desc.HasConfigurable && desc.Configurable) ||
                        (desc.HasEnumerable && !desc.Enumerable) || (desc.HasWritable && desc.Writable) ||
                        (desc.HasValue && !ObjectOps.SameValue(desc.Value, currentValue)))
                    {
                        return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                            MessageTemplate.RedefineDisallowed, key);
                    }
                    return true;
                }
                if (!lookupKey.IsElement || isMinusZero)
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.InvalidTypedArrayIndex);
                }

                // 1b ii. If Desc has a [[Configurable]] field and if
                //     Desc.[[Configurable]] is false, return false.
                // 1b iii. If Desc has an [[Enumerable]] field and if Desc.[[Enumerable]]
                //     is false, return false.
                // 1b iv. If IsAccessorDescriptor(Desc) is true, return false.
                // 1b v. If Desc has a [[Writable]] field and if Desc.[[Writable]] is
                //     false, return false.

                if (PropertyDescriptor.IsAccessorDescriptor(in desc))
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.RedefineDisallowed, key);
                }

                if ((desc.HasConfigurable && !desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) ||
                    (desc.HasWritable && !desc.Writable))
                {
                    return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                        MessageTemplate.RedefineDisallowed, key);
                }

                // 1b vi. If Desc has a [[Value]] field, perform
                // ? IntegerIndexedElementSet(O, numericIndex, Desc.[[Value]]).
                if (desc.HasValue)
                {
                    if (!desc.HasConfigurable) desc.SetConfigurable(true);
                    if (!desc.HasEnumerable) desc.SetEnumerable(true);
                    if (!desc.HasWritable) desc.SetWritable(true);
                    JSValue value = desc.Value;
                    var it = new LookupIterator(isolate, o, index, LookupIterator.Configuration.OWN);
                    JSObject.DefineOwnPropertyIgnoreAttributes(ref it, value, desc.ToAttributes());
                }
                // 1b vii. Return true.
                return true;
            }
        }
        // 4. Return ! OrdinaryDefineOwnProperty(O, P, Desc).
        return JSReceiver.OrdinaryDefineOwnProperty(isolate, o, lookupKey, ref desc, shouldThrow);
    }
}

/// <summary>V8's JSDataView / JSRabGsabDataView.</summary>
public sealed class JSDataView(Map map) : JSArrayBufferView(map)
{
}

// ---- Date and RegExp ----------------------------------------------------------------------

/// <summary>V8's JSDate: the time value and the cached local-time fields.</summary>
public sealed partial class JSDate(Map map) : JSObject(map)
{
    /// <summary>JSDate::FieldIndex.</summary>
    public enum FieldIndex
    {
        kYear,
        kMonth,
        kDay,
        kWeekday,
        kHour,
        kMinute,
        kSecond,
        kFirstUncachedField,
        kMillisecond = kFirstUncachedField,
        kDays,
        kTimeInDay,
        kFirstUTCField,
        kYearUTC = kFirstUTCField,
        kMonthUTC,
        kDayUTC,
        kWeekdayUTC,
        kHourUTC,
        kMinuteUTC,
        kSecondUTC,
        kMillisecondUTC,
        kDaysUTC,
        kTimeInDayUTC,
        kTimezoneOffset,
    }

    /// <summary>The time value (NaN for an invalid date).</summary>
    public double Value = double.NaN;

    public JSValue Year;
    public JSValue Month;
    public JSValue Day;
    public JSValue Weekday;
    public JSValue Hour;
    public JSValue Min;
    public JSValue Sec;

    /// <summary>The DateCache stamp the cached fields belong to (NaN: not cached).</summary>
    public JSValue CacheStamp = JSValue.NaN;

    /// <summary>JSDate::SetNanValue.</summary>
    public void SetNanValue()
    {
        Value = double.NaN;
        JSValue nan = JSValue.NaN;
        CacheStamp = nan;
        Year = nan;
        Month = nan;
        Day = nan;
        Hour = nan;
        Min = nan;
        Sec = nan;
        Weekday = nan;
    }
}

/// <summary>V8's JSRegExp. The compiled data (RegExpData) belongs to V8Sharp.RegExp.</summary>
public sealed class JSRegExp(Map map) : JSObject(map)
{
    /// <summary>In-object fields of a JSRegExp: lastIndex.</summary>
    public const int kInObjectFieldCount = 1;

    /// <summary>JSRegExp::Flag.</summary>
    [Flags]
    public enum Flags
    {
        kNone = 0,
        kHasIndices = 1 << 0,
        kGlobal = 1 << 1,
        kIgnoreCase = 1 << 2,
        kLinear = 1 << 3,
        kMultiline = 1 << 4,
        kDotAll = 1 << 5,
        kUnicode = 1 << 6,
        kUnicodeSets = 1 << 7,
        kSticky = 1 << 8,
    }

    /// <summary>JSRegExp::kLastIndexFieldIndex: lastIndex is the first in-object property.</summary>
    public const int kLastIndexFieldIndex = 0;

    /// <summary>The RegExpData (TODO(merge): V8Sharp.RegExp's type).</summary>
    public object? Data;
    public JSValue Source;
    public Flags FlagsValue;
}

// ---- Promises ---------------------------------------------------------------------------

/// <summary>v8::Promise::PromiseState.</summary>
public enum PromiseState { kPending, kFulfilled, kRejected }

/// <summary>V8's PromiseReaction: a pending then() reaction, linked in reverse order.</summary>
public sealed class PromiseReaction(JSValue next, JSValue rejectHandler, JSValue fulfillHandler, JSValue promiseOrCapability)
    : HeapObject(InstanceType.PromiseReactionType)
{
    /// <summary>PromiseReaction::Type.</summary>
    public enum Type { kFulfill, kReject }

    /// <summary>The next reaction, or 0 (V8's Smi zero) at the end.</summary>
    public JSValue Next = next;
    public JSValue RejectHandler = rejectHandler;
    public JSValue FulfillHandler = fulfillHandler;
    public JSValue PromiseOrCapability = promiseOrCapability;
}

/// <summary>V8's PromiseCapability.</summary>
public sealed class PromiseCapability(JSValue promise, JSValue resolve, JSValue reject)
    : HeapObject(InstanceType.PromiseCapabilityType)
{
    public JSValue Promise = promise;
    public JSValue Resolve = resolve;
    public JSValue Reject = reject;
}

/// <summary>V8's JSPromise.</summary>
public sealed class JSPromise(Map map) : JSObject(map)
{
    /// <summary>The reactions (a PromiseReaction list, 0 when empty) while pending, the result once settled.</summary>
    public JSValue ReactionsOrResult = JSValue.Zero;
    public PromiseState Status = PromiseState.kPending;
    public bool HasHandler;
    public bool IsSilent;
    public int AsyncTaskId;

    /// <summary>JSPromise::result (only valid once settled).</summary>
    public JSValue Result
    {
        get
        {
            Debug.Assert(Status != PromiseState.kPending);
            return ReactionsOrResult;
        }
    }

    /// <summary>JSPromise::reactions (only valid while pending).</summary>
    public JSValue Reactions
    {
        get
        {
            Debug.Assert(Status == PromiseState.kPending);
            return ReactionsOrResult;
        }
    }

    /// <summary>JSPromise::Status.</summary>
    public static string StatusName(PromiseState status) => status switch
    {
        PromiseState.kFulfilled => "fulfilled",
        PromiseState.kPending => "pending",
        _ => "rejected",
    };

    /// <summary>JSPromise::Fulfill (ES #sec-fulfillpromise).</summary>
    public static JSValue Fulfill(Isolate isolate, JSPromise promise, JSValue value)
    {
        // 1. Assert: The value of promise.[[PromiseState]] is "pending".
        if (promise.Status != PromiseState.kPending) throw new InvalidOperationException("promise is not pending");

        // 2. Let reactions be promise.[[PromiseFulfillReactions]].
        JSValue reactions = promise.Reactions;

        // 3. Set promise.[[PromiseResult]] to value.
        // 4. Set promise.[[PromiseFulfillReactions]] to undefined.
        // 5. Set promise.[[PromiseRejectReactions]] to undefined.
        promise.ReactionsOrResult = value;

        // 6. Set promise.[[PromiseState]] to "fulfilled".
        promise.Status = PromiseState.kFulfilled;

        // 7. Return TriggerPromiseReactions(reactions, value).
        return TriggerPromiseReactions(isolate, reactions, value, PromiseReaction.Type.kFulfill);
    }

    /// <summary>JSPromise::Reject (ES #sec-rejectpromise).</summary>
    public static JSValue Reject(Isolate isolate, JSPromise promise, JSValue reason, bool debugEvent = true)
    {
        // 1. Assert: The value of promise.[[PromiseState]] is "pending".
        if (promise.Status != PromiseState.kPending) throw new InvalidOperationException("promise is not pending");

        // 2. Let reactions be promise.[[PromiseRejectReactions]].
        JSValue reactions = promise.Reactions;

        // 3. Set promise.[[PromiseResult]] to reason.
        // 4. Set promise.[[PromiseFulfillReactions]] to undefined.
        // 5. Set promise.[[PromiseRejectReactions]] to undefined.
        promise.ReactionsOrResult = reason;

        // 6. Set promise.[[PromiseState]] to "rejected".
        promise.Status = PromiseState.kRejected;

        // 7. If promise.[[PromiseIsHandled]] is false, perform
        //    HostPromiseRejectionTracker(promise, "reject").
        if (!promise.HasHandler) isolate.ReportPromiseReject(promise, reason, PromiseRejectEvent.kPromiseRejectWithNoHandler);

        // 8. Return TriggerPromiseReactions(reactions, reason).
        return TriggerPromiseReactions(isolate, reactions, reason, PromiseReaction.Type.kReject);
    }

    /// <summary>JSPromise::Resolve (ES #sec-promise-resolve-functions).</summary>
    public static JSValue Resolve(Isolate isolate, JSPromise promise, JSValue resolutionObj)
    {
        // 7. If SameValue(resolution, promise) is true, then
        if (ReferenceEquals(resolutionObj.HeapObjectOrNull, promise))
        {
            // a. Let selfResolutionError be a newly created TypeError object.
            JSObject selfResolutionError = isolate.Factory.NewTypeError(MessageTemplate.PromiseCyclic, resolutionObj);
            // b. Return RejectPromise(promise, selfResolutionError).
            return Reject(isolate, promise, selfResolutionError);
        }

        // 8. If Type(resolution) is not Object, then
        if (resolutionObj.HeapObjectOrNull is not JSReceiver resolutionRecv)
        {
            // a. Return FulfillPromise(promise, resolution).
            return Fulfill(isolate, promise, resolutionObj);
        }

        // 9. Let then be Get(resolution, "then").
        JSValue thenAction;
        try
        {
            // Make sure a lookup of "then" on any JSPromise whose [[Prototype]] is the
            // initial %PromisePrototype% yields the initial method. In addition this
            // protector also guards the negative lookup of "then" on the intrinsic
            // %ObjectPrototype%, meaning that such lookups are guaranteed to yield
            // undefined without triggering any side-effects.
            if (resolutionRecv is JSPromise && resolutionRecv.Map.Prototype is JSReceiver proto &&
                proto.GetCreationContext() is { } protoContext &&
                ReferenceEquals(proto, protoContext.PromisePrototype) &&
                Protectors.IsPromiseThenLookupChainIntact(isolate))
            {
                thenAction = protoContext.PromiseThen;
            }
            else
            {
                thenAction = JSReceiver.GetProperty(isolate, resolutionRecv, ReadOnlyRoots.then_string);
            }
        }
        catch (JavaScriptException e)
        {
            // 10. If then is an abrupt completion, then
            // a. Return RejectPromise(promise, then.[[Value]]).
            return Reject(isolate, promise, e.Value, false);
        }

        // 11. Let thenAction be then.[[Value]].
        // 12. If IsCallable(thenAction) is false, then
        if (!ObjectOps.IsCallable(thenAction))
        {
            // a. Return FulfillPromise(promise, resolution).
            return Fulfill(isolate, promise, resolutionRecv);
        }

        // 13. Let job be NewPromiseResolveThenableJob(promise, resolution,
        //                                             thenAction).
        NativeContext thenContext = JSReceiver.GetContextForMicrotask(isolate, (JSReceiver)thenAction.Object) ?? isolate.NativeContext;

        var task = new PromiseResolveThenableJobTask(thenContext, promise, resolutionRecv, (JSReceiver)thenAction.Object);
        MicrotaskQueue? microtaskQueue = thenContext.MicrotaskQueue;
        microtaskQueue?.EnqueueMicrotask(task);

        // 15. Return undefined.
        return JSValue.Undefined;
    }

    static NativeContext? HandlerContext(Isolate isolate, JSValue handler)
    {
        if (handler.HeapObjectOrNull is not JSReceiver receiver) return null;
        if (receiver is JSGeneratorObject generator) return generator.Context.NativeContext;
        return JSReceiver.GetContextForMicrotask(isolate, receiver);
    }

    /// <summary>JSPromise::TriggerPromiseReactions: morphs the reactions into jobs on the microtask queue.</summary>
    public static JSValue TriggerPromiseReactions(Isolate isolate, JSValue reactions, JSValue argument, PromiseReaction.Type type)
    {
        // We need to reverse the {reactions} here, since we record them
        // on the JSPromise in the reverse order.
        {
            JSValue current = reactions;
            JSValue reversed = JSValue.Zero;
            while (current.HeapObjectOrNull is PromiseReaction reaction)
            {
                JSValue next = reaction.Next;
                reaction.Next = reversed;
                reversed = current;
                current = next;
            }
            reactions = reversed;
        }

        // Morph the {reactions} into PromiseReactionJobTasks
        // and push them onto the microtask queue.
        while (reactions.HeapObjectOrNull is PromiseReaction reaction)
        {
            reactions = reaction.Next;

            // According to HTML, we use the context of the appropriate handler as the
            // context of the microtask. See step 3 of HTML's EnqueueJob:
            // https://html.spec.whatwg.org/C/#enqueuejob(queuename,-job,-arguments)
            JSValue primaryHandler, secondaryHandler;
            if (type == PromiseReaction.Type.kFulfill)
            {
                primaryHandler = reaction.FulfillHandler;
                secondaryHandler = reaction.RejectHandler;
            }
            else
            {
                primaryHandler = reaction.RejectHandler;
                secondaryHandler = reaction.FulfillHandler;
            }

            NativeContext handlerContext = HandlerContext(isolate, primaryHandler) ??
                                           HandlerContext(isolate, secondaryHandler) ??
                                           isolate.NativeContext;

            // V8 reuses the PromiseReaction object as the task by changing its map;
            // V8Sharp allocates the job task.
            var task = new PromiseReactionJobTask(type == PromiseReaction.Type.kReject, argument, handlerContext, primaryHandler,
                reaction.PromiseOrCapability);

            MicrotaskQueue? microtaskQueue = handlerContext.MicrotaskQueue;
            microtaskQueue?.EnqueueMicrotask(task);
        }

        return JSValue.Undefined;
    }
}

/// <summary>v8::PromiseRejectEvent.</summary>
public enum PromiseRejectEvent
{
    kPromiseRejectWithNoHandler = 0,
    kPromiseHandlerAddedAfterReject = 1,
    kPromiseRejectAfterResolved = 2,
    kPromiseResolveAfterResolved = 3,
}

// ---- Generators ------------------------------------------------------------------------

/// <summary>V8's JSGeneratorObject.</summary>
public class JSGeneratorObject(Map map) : JSObject(map)
{
    /// <summary>JSGeneratorObject::ResumeMode.</summary>
    public enum ResumeMode { kNext, kReturn, kThrow, kRethrow }

    public const int kGeneratorExecuting = -2;
    public const int kGeneratorClosed = -1;

    public JSFunction Function = null!;
    public Context Context = null!;
    public JSValue Receiver;
    public JSValue InputOrDebugPos;
    public ResumeMode Mode;
    /// <summary>The bytecode offset to resume at, or kGeneratorExecuting / kGeneratorClosed.</summary>
    public int ContinuationValue;
    /// <summary>The saved registers and parameters (V8's parameters_and_registers).</summary>
    public FixedArray ParametersAndRegisters = FixedArray.Empty;

    public bool IsClosed => ContinuationValue == kGeneratorClosed;
    public bool IsExecuting => ContinuationValue == kGeneratorExecuting;
    public bool IsSuspended => ContinuationValue >= 0;
}

/// <summary>V8's JSAsyncFunctionObject.</summary>
public sealed class JSAsyncFunctionObject(Map map) : JSGeneratorObject(map)
{
    public JSPromise Promise = null!;
}

/// <summary>V8's JSAsyncGeneratorObject.</summary>
public sealed class JSAsyncGeneratorObject(Map map) : JSGeneratorObject(map)
{
    /// <summary>The AsyncGeneratorRequest queue (undefined when empty).</summary>
    public JSValue Queue;
    public bool IsAwaiting;
}

// ---- Collections and iterators ---------------------------------------------------------

/// <summary>V8's JSCollection (JSMap / JSSet): the table is an OrderedHashMap / OrderedHashSet.</summary>
public abstract class JSCollection(Map map) : JSObject(map)
{
    public HeapObject Table = null!;
}

/// <summary>V8's JSMap.</summary>
public sealed class JSMap(Map map) : JSCollection(map)
{
    /// <summary>JSMap::Initialize.</summary>
    public static void Initialize(JSMap map, Isolate isolate) => map.Table = OrderedHashMap.Allocate(OrderedHashMap.kInitialCapacity);

    /// <summary>JSMap::Clear.</summary>
    public static void Clear(Isolate isolate, JSMap map)
    {
        var table = (OrderedHashMap)map.Table;
        map.Table = OrderedHashMap.Clear(isolate, table);
    }
}

/// <summary>V8's JSSet.</summary>
public sealed class JSSet(Map map) : JSCollection(map)
{
    /// <summary>JSSet::Initialize.</summary>
    public static void Initialize(JSSet set, Isolate isolate) => set.Table = OrderedHashSet.Allocate(OrderedHashSet.kInitialCapacity);

    /// <summary>JSSet::Clear.</summary>
    public static void Clear(Isolate isolate, JSSet set)
    {
        var table = (OrderedHashSet)set.Table;
        set.Table = OrderedHashSet.Clear(isolate, table);
    }
}

/// <summary>V8's JSMapIterator (keys / values / entries).</summary>
public sealed class JSMapIterator(Map map) : JSObject(map)
{
    public HeapObject Table = null!;
    public int Index;
}

/// <summary>V8's JSSetIterator (values / entries).</summary>
public sealed class JSSetIterator(Map map) : JSObject(map)
{
    public HeapObject Table = null!;
    public int Index;
}

/// <summary>
/// V8's JSWeakCollection (JSWeakMap / JSWeakSet). Deviation: V8 keeps an
/// EphemeronHashTable whose entries die with their keys; V8Sharp uses a
/// ConditionalWeakTable keyed by the CLR object, which has the same ephemeron
/// semantics under the CLR GC.
/// </summary>
public abstract class JSWeakCollection(Map map) : JSObject(map)
{
    public readonly System.Runtime.CompilerServices.ConditionalWeakTable<HeapObject, StrongBox> Table = new();

    /// <summary>A mutable box for the value (ConditionalWeakTable values are reference types).</summary>
    public sealed class StrongBox(JSValue value)
    {
        public JSValue Value = value;
    }

    /// <summary>JSWeakCollection::Set.</summary>
    public static void Set(JSWeakCollection weakCollection, JSValue key, JSValue value)
    {
        HeapObject k = key.Object;
        if (weakCollection.Table.TryGetValue(k, out StrongBox? box))
        {
            box.Value = value;
        }
        else
        {
            weakCollection.Table.Add(k, new StrongBox(value));
        }
    }

    /// <summary>JSWeakCollection::Delete.</summary>
    public static bool Delete(JSWeakCollection weakCollection, JSValue key) =>
        key.HeapObjectOrNull is { } k && weakCollection.Table.Remove(k);

    public static bool Has(JSWeakCollection weakCollection, JSValue key) =>
        key.HeapObjectOrNull is { } k && weakCollection.Table.TryGetValue(k, out _);

    public static JSValue Get(JSWeakCollection weakCollection, JSValue key) =>
        key.HeapObjectOrNull is { } k && weakCollection.Table.TryGetValue(k, out StrongBox? box) ? box.Value : JSValue.Undefined;
}

public sealed class JSWeakMap(Map map) : JSWeakCollection(map);

public sealed class JSWeakSet(Map map) : JSWeakCollection(map);

/// <summary>V8's JSWeakRef (deviation: holds the target through a CLR WeakReference).</summary>
public sealed class JSWeakRef(Map map) : JSObject(map)
{
    public WeakReference<HeapObject>? Target;
}

/// <summary>V8's JSFinalizationRegistry (the cleanup scheduling is not ported).</summary>
public sealed class JSFinalizationRegistry(Map map) : JSObject(map)
{
    public NativeContext NativeContext = null!;
    public JSValue Cleanup;
    public readonly List<(WeakReference<HeapObject> Target, JSValue HeldValue, JSValue UnregisterToken)> Cells = [];
}

/// <summary>V8's JSStringIterator.</summary>
public sealed class JSStringIterator(Map map) : JSObject(map)
{
    public JSString String = ReadOnlyRoots.empty_string;
    public int Index;
}

/// <summary>V8's JSRegExpStringIterator (String.prototype.matchAll).</summary>
public sealed class JSRegExpStringIterator(Map map) : JSObject(map)
{
    public JSValue IteratingRegExp;
    public JSString IteratedString = ReadOnlyRoots.empty_string;
    public bool Done;
    public bool Global;
    public bool Unicode;
}

/// <summary>V8's JSAsyncFromSyncIterator.</summary>
public sealed class JSAsyncFromSyncIterator(Map map) : JSObject(map)
{
    public JSReceiver SyncIterator = null!;
    public JSValue Next;
}

/// <summary>V8's JSIteratorHelper and its subclasses (map, filter, take, drop, flatMap, concat).</summary>
public sealed class JSIteratorHelper(Map map) : JSObject(map)
{
    /// <summary>The underlying iterator record (object and next method).</summary>
    public JSValue UnderlyingObject;
    public JSValue UnderlyingNext;
    public JSValue Mapper;
    public JSValue Predicate;
    public double Counter;
    public double Remaining;
    public JSValue InnerIterator;
    public JSValue InnerNext;
    public bool Executing;
    public bool Exhausted;
}

/// <summary>V8's JSValidIteratorWrapper (Iterator.from).</summary>
public sealed class JSValidIteratorWrapper(Map map) : JSObject(map)
{
    public JSValue UnderlyingObject;
    public JSValue UnderlyingNext;
}

/// <summary>V8's JSDisposableStackBase (DisposableStack / AsyncDisposableStack).</summary>
public sealed class JSDisposableStackBase(Map map) : JSObject(map)
{
    /// <summary>DisposableStackState.</summary>
    public enum DisposableStackState { kDisposed, kPending }

    public FixedArray Stack = FixedArray.Empty;
    public int Length;
    public DisposableStackState State = DisposableStackState.kPending;
    public JSValue Error = JSValue.TheHole;
    public JSValue ErrorMessage = JSValue.TheHole;
    public bool NeedsAwait;
    public bool HasAwaited;
    public bool SuppressedErrorCreated;
}

// ---- Modules and the rest ------------------------------------------------------------------

/// <summary>
/// V8's JSModuleNamespace. The module record belongs to the module system;
/// the namespace keeps the export table V8 reads through module()->exports():
/// name -> Cell whose value is the export binding (TheHole while in TDZ).
/// </summary>
public sealed class JSModuleNamespace(Map map) : JSObject(map)
{
    /// <summary>The module's exports (V8: module()->exports()); TODO(merge): the Module type of the module system.</summary>
    public ObjectHashTable Exports = ObjectHashTable.New(0);

    /// <summary>The module (TODO(merge): V8Sharp's Module when the module system is ported).</summary>
    public object? Module;

    /// <summary>JSModuleNamespace::HasExport.</summary>
    public bool HasExport(Isolate isolate, JSString name) => !Exports.Lookup(isolate, name).IsTheHole;

    /// <summary>JSModuleNamespace::GetExport.</summary>
    public JSValue GetExport(Isolate isolate, JSString name)
    {
        JSValue obj = Exports.Lookup(isolate, name);
        if (obj.IsTheHole) return JSValue.Undefined;

        JSValue value = ((Cell)obj.Object).Value;
        if (value.IsTheHole)
        {
            // According to https://tc39.es/ecma262/#sec-InnerModuleLinking
            // step 10 and
            // https://tc39.es/ecma262/#sec-source-text-module-record-initialize-environment
            // step 8-25, variables must be declared in Link. And according to
            // https://tc39.es/ecma262/#sec-module-namespace-exotic-objects-get-p-receiver,
            // here accessing uninitialized variable error should be thrown.
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.AccessedUninitializedVariable, name));
        }
        return value;
    }

    /// <summary>JSModuleNamespace::GetPropertyAttributes.</summary>
    public static new PropertyAttributes GetPropertyAttributes(ref LookupIterator it)
    {
        JSModuleNamespace obj = it.GetHolder<JSModuleNamespace>();
        var name = (JSString)it.GetName();
        Isolate isolate = it.Isolate;

        JSValue lookup = obj.Exports.Lookup(isolate, name);
        if (lookup.IsTheHole) return PropertyAttributes.ABSENT;

        JSValue value = ((Cell)lookup.Object).Value;
        if (value.IsTheHole)
        {
            isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.NotDefined, name));
        }
        return it.PropertyAttributes();
    }

    /// <summary>JSModuleNamespace::DefineOwnProperty (ES #sec-module-namespace-exotic-objects-defineownproperty-p-desc).</summary>
    public static bool DefineOwnProperty(Isolate isolate, JSModuleNamespace obj, JSValue key, ref PropertyDescriptor desc,
        ShouldThrow? shouldThrow)
    {
        // 1. If Type(P) is Symbol, return OrdinaryDefineOwnProperty(O, P, Desc).
        if (key.IsSymbol) return JSReceiver.OrdinaryDefineOwnProperty(isolate, obj, key, ref desc, shouldThrow);

        // 2. Let current be ? O.[[GetOwnProperty]](P).
        var lookupKey = new PropertyKey(isolate, key);
        var it = new LookupIterator(isolate, obj, lookupKey, LookupIterator.Configuration.OWN);
        var current = new PropertyDescriptor();
        bool hasOwn = JSReceiver.GetOwnPropertyDescriptor(ref it, ref current);

        // 3. If current is undefined, return false.
        // 4. If Desc.[[Configurable]] is present and has value true, return false.
        // 5. If Desc.[[Enumerable]] is present and has value false, return false.
        // 6. If ! IsAccessorDescriptor(Desc) is true, return false.
        // 7. If Desc.[[Writable]] is present and has value false, return false.
        // 8. If Desc.[[Value]] is present, return
        //    SameValue(Desc.[[Value]], current.[[Value]]).
        if (!hasOwn || (desc.HasConfigurable && desc.Configurable) || (desc.HasEnumerable && !desc.Enumerable) ||
            PropertyDescriptor.IsAccessorDescriptor(in desc) || (desc.HasWritable && !desc.Writable) ||
            (desc.HasValue && !ObjectOps.SameValue(desc.Value, current.Value)))
        {
            return ObjectOps.ReturnFailure(isolate, ObjectOps.GetShouldThrow(isolate, shouldThrow),
                MessageTemplate.RedefineDisallowed, key);
        }

        return true;
    }
}

/// <summary>V8's JSRawJson (JSON.rawJSON).</summary>
public sealed class JSRawJson(Map map) : JSObject(map)
{
    public const int kRawJsonInitialIndex = 0;
}

/// <summary>V8's JSShadowRealm.</summary>
public sealed class JSShadowRealm(Map map) : JSObject(map)
{
    public NativeContext NativeContext = null!;
}

/// <summary>V8's JSExternalObject (v8::External).</summary>
public sealed class JSExternalObject(Map map) : JSObject(map)
{
    public object? Value;
}

/// <summary>
/// V8's AllocationSite: allocation feedback for array and object literals
/// (elements-kind transitions and pretenuring). V8Sharp has no mementos or
/// pretenuring; the site records the transition feedback only.
/// </summary>
public sealed class AllocationSite() : HeapObject(InstanceType.AllocationSiteType)
{
    /// <summary>AllocationSite::PretenureDecision.</summary>
    public enum PretenureDecision { kUndecided = 0, kDontTenure = 1, kMaybeTenure = 2, kTenure = 3, kZombie = 4 }

    /// <summary>The boilerplate object, or null.</summary>
    public JSObject? Boilerplate;

    /// <summary>The transition info: the elements kind for array sites.</summary>
    public ElementsKind ElementsKind = ElementsKind.PACKED_SMI_ELEMENTS;
    public bool DoNotInlineCall;
    public AllocationSite? NestedSite;
    public PretenureDecision Decision;

    /// <summary>AllocationSite::ShouldTrack: only more-general transitions of array sites are tracked.</summary>
    public static bool ShouldTrack(ElementsKind from, ElementsKind to) => ElementsKinds.IsMoreGeneralElementsKindTransition(from, to);

    /// <summary>AllocationSite::DigestTransitionFeedback.</summary>
    public static bool DigestTransitionFeedback(Isolate isolate, AllocationSite site, ElementsKind toKind)
    {
        ElementsKind kind = site.ElementsKind;
        if (ElementsKinds.IsHoleyElementsKind(kind)) toKind = ElementsKinds.GetHoleyElementsKind(toKind);
        if (ElementsKinds.IsMoreGeneralElementsKindTransition(kind, toKind))
        {
            site.ElementsKind = toKind;
            return true;
        }
        return false;
    }
}

/// <summary>ShadowRealm wrapped functions (builtins-shadow-realm-gen.cc, js-function.cc).</summary>
public sealed partial class JSWrappedFunction
{
    /// <summary>JSWrappedFunction::Create (ES #sec-wrappedfunctioncreate).</summary>
    public static JSWrappedFunction Create(Isolate isolate, NativeContext creationContext, JSReceiver value)
    {
        // The value must be a callable according to the specification.
        Debug.Assert(ObjectOps.IsCallable(value));
        // The intermediate wrapped functions are not user-visible. And calling a
        // wrapped function won't cause a side effect in the creation realm.
        // Unwrap here to avoid nested unwrapping at the call site.
        if (value is JSWrappedFunction targetWrapped) value = targetWrapped.WrappedTargetFunction;

        // 1. Let internalSlotsList be the internal slots listed in Table 2, plus
        // [[Prototype]] and [[Extensible]].
        // 2. Let wrapped be ! MakeBasicObject(internalSlotsList).
        // 3. Set wrapped.[[Prototype]] to
        // callerRealm.[[Intrinsics]].[[%Function.prototype%]].
        // 4. Set wrapped.[[Call]] as described in 2.1.
        // 5. Set wrapped.[[WrappedTargetFunction]] to Target.
        // 6. Set wrapped.[[Realm]] to callerRealm.
        var wrapped = new JSWrappedFunction(creationContext.WrappedFunctionMap, value, creationContext);

        // 7. Let result be CopyNameAndLength(wrapped, Target, "wrapped").
        try
        {
            CopyNameAndLength(isolate, wrapped, value, null, 0);
        }
        catch (JavaScriptException e)
        {
            // 8. If result is an Abrupt Completion, throw a TypeError exception.
            // The TypeError thrown is created with creation Realm's TypeError
            // constructor instead of the executing Realm's.
            JSString str = ObjectOps.NoSideEffectsToString(isolate, e.Value);
            isolate.Throw(isolate.Factory.NewError(creationContext.TypeErrorFunction, MessageTemplate.CannotWrap, [str]));
        }

        // 9. Return wrapped.
        return wrapped;
    }

    /// <summary>ShadowRealmGetWrappedValue (https://tc39.es/proposal-shadowrealm/#sec-getwrappedvalue).</summary>
    public static JSValue GetWrappedValue(Isolate isolate, NativeContext creationContext, JSValue value)
    {
        // 2. Return value.
        if (value.HeapObjectOrNull is not JSReceiver receiver) return value;

        // 1a. If IsCallable(value) is false, throw a TypeError exception.
        if (!receiver.Map.IsCallable) isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.NotCallable, value));
        // 1b. Return ? WrappedFunctionCreate(callerRealm, value).
        return Create(isolate, creationContext, receiver);
    }

    /// <summary>The CallWrappedFunction builtin ([[Call]] of a wrapped function exotic object).</summary>
    public static JSValue Call(Isolate isolate, JSWrappedFunction function, JSValue receiver, ReadOnlySpan<JSValue> args)
    {
        isolate.StackGuard.StackCheck(isolate);

        // 1. Let target be F.[[WrappedTargetFunction]].
        JSReceiver target = function.WrappedTargetFunction;
        // 4. Let callerRealm be ? GetFunctionRealm(F).
        NativeContext callerContext = function.Context;
        // 3. Let targetRealm be ? GetFunctionRealm(target).
        NativeContext targetContext = JSReceiver.GetFunctionRealm(isolate, target);
        // 5. NOTE: Any exception objects produced after this point are associated
        // with callerRealm.

        // 8. Let wrappedThisArgument to ? GetWrappedValue(targetRealm, thisArgument).
        JSValue wrappedReceiver = GetWrappedValue(isolate, targetContext, receiver);
        // 6. Let wrappedArgs be a new empty List.
        var wrappedArgs = new JSValue[args.Length];
        // 7. For each element arg of argumentsList, do
        for (int i = 0; i < args.Length; i++)
        {
            // 7a. Let wrappedValue be ? GetWrappedValue(targetRealm, arg).
            // 7b. Append wrappedValue to wrappedArgs.
            wrappedArgs[i] = GetWrappedValue(isolate, targetContext, args[i]);
        }

        JSValue result;
        try
        {
            // 9. Let result be the Completion Record of Call(target,
            // wrappedThisArgument, wrappedArgs).
            result = Execution.Call(isolate, target, wrappedReceiver, wrappedArgs);
        }
        catch (JavaScriptException e)
        {
            // 11. Else,
            // 11a. Throw a TypeError exception.
            JSString str = ObjectOps.NoSideEffectsToString(isolate, e.Value);
            isolate.Throw(isolate.Factory.NewTypeError(MessageTemplate.CallWrappedFunctionThrew, str));
            return default;
        }

        // 10. If result.[[Type]] is normal or result.[[Type]] is return, then
        // 10a. Return ? GetWrappedValue(callerRealm, result.[[Value]]).
        return GetWrappedValue(isolate, callerContext, result);
    }
}
