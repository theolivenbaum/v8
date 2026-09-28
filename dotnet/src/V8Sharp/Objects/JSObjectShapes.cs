// The shapes (data layout) of the special JSObject kinds, with the object-level
// operations V8 keeps next to them:
//   src/objects/arguments.h            JSArgumentsObject, SloppyArgumentsElements,
//                                      AliasedArgumentsEntry
//   src/objects/js-array-buffer.{h,cc} JSArrayBuffer, JSArrayBufferView,
//                                      JSTypedArray (+ DefineOwnProperty), JSDataView
//   src/objects/js-date.h              JSDate (the date math is in the Date builtins)
//   src/objects/js-regexp.h            JSRegExp
//   src/objects/js-generator.h         JSGeneratorObject and the async variants
//   JSStringIterator, JSRegExpStringIterator
// (JSPromise is in JSPromise.cs, the collections in JSCollection.cs, the weak
// references in JSWeakRefs.cs, the iterator helpers in JSIteratorHelpers.cs and
// the disposable stacks in JSDisposableStack.cs.)
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
public sealed class JSDate(Map map) : JSObject(map)
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

/// <summary>
/// V8's JSRegExp. The fields, RegExpData and the operations of
/// src/objects/js-regexp.{h,cc} are in JSRegExp.cs.
/// </summary>
public sealed partial class JSRegExp(Map map) : JSObject(map)
{
    /// <summary>In-object fields of a JSRegExp: lastIndex.</summary>
    public const int kInObjectFieldCount = 1;
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

// ---- String iterators ----------------------------------------------------------------

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
