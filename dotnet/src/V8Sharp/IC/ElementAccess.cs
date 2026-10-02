// Port of the fast element access paths of src/ic/accessor-assembler.cc
// (EmitElementLoad, HandleLoadICSmiHandlerLoadNamedCase's element case) and
// src/codegen/code-stub-assembler.cc (EmitElementStore, CheckForCapacityGrow)
// for the fast elements kinds and typed arrays. Everything else (dictionary,
// sealed/frozen, sloppy arguments) misses to the runtime.
using System.Runtime.CompilerServices;

namespace V8Sharp.IC;

public static class ElementAccess
{
    /// <summary>
    /// A keyed load through an element handler. Returns false when the
    /// handler does not apply (the caller misses).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryLoadFastElement(Isolate isolate, JSObject obj, double key, LoadHandler handler, out JSValue result)
    {
        result = default;
        if (handler.HandlerKind == LoadHandler.Kind.kElementWithTransition)
        {
            // Runtime_TransitionElementsKindWithKind, then the element load.
            if (obj is not JSArray || !ElementsKinds.IsFastElementsKind(obj.Map.ElementsKind)) return false;
            JSObject.TransitionElementsKind(isolate, obj, handler.ElementsKind);
            if (obj.Map.ElementsKind != handler.ElementsKind) return false;
        }
        if (!ElementsKinds.IsFastElementsKind(handler.ElementsKind))
        {
            if (ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(handler.ElementsKind) && obj is JSTypedArray typedArray)
            {
                return TryLoadTypedElement(isolate, typedArray, key, handler, out result);
            }
            // Dictionary, nonextensible, sealed and frozen elements take the runtime.
            return false;
        }
        int index = (int)key;
        if (index != key || index < 0) return false;
        // Typed arrays keep their data in the buffer (their elements are the
        // empty byte array): they miss to the runtime.
        if (ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(obj.Map.ElementsKind)) return false;
        FixedArrayBase elements = obj.Elements;
        int length = handler.IsJSArray ? (int)Unsafe.As<JSArray>(obj).Length._num : elements.Length;
        if (index >= length)
        {
            if (!handler.AllowOutOfBounds || !Protectors.IsNoElementsIntact(isolate)) return false;
            result = JSValue.Undefined;
            return true;
        }
        if (elements is FixedArray fixedArray)
        {
            if (!ElementsKinds.IsSmiOrObjectElementsKind(handler.ElementsKind)) return false;
            JSValue value = fixedArray._data[index];
            if (value.IsTheHole)
            {
                if (!handler.AllowHandlingHole || !Protectors.IsNoElementsIntact(isolate)) return false;
                value = JSValue.Undefined;
            }
            result = value;
            return true;
        }
        if (elements is FixedDoubleArray doubleArray)
        {
            if (!ElementsKinds.IsDoubleElementsKind(handler.ElementsKind)) return false;
            double d = doubleArray._data[index];
            if (FixedDoubleArray.IsHoleBits(d))
            {
                if (!handler.AllowHandlingHole || !Protectors.IsNoElementsIntact(isolate)) return false;
                result = JSValue.Undefined;
                return true;
            }
            result = JSValue.FromNumber(d);
            return true;
        }
        return false;
    }

    /// <summary>
    /// EmitElementLoad for the typed array kinds: an in-bounds element, or
    /// undefined out of bounds (including detached) when the handler allows it.
    /// </summary>
    static bool TryLoadTypedElement(Isolate isolate, JSTypedArray array, double key, LoadHandler handler, out JSValue result)
    {
        result = default;
        long index = (long)key;
        if (index != key || index < 0) return false;
        if (TypedArrayElementsOps.TryGetFixedLength(array, out ulong fixedLength))
        {
            if ((ulong)index < fixedLength)
            {
                result = TypedArrayElementsOps.LoadElement(isolate, array, array.Map.ElementsKind, (int)index);
                return true;
            }
            if (!handler.AllowOutOfBounds) return false;
            result = JSValue.Undefined;
            return true;
        }
        ulong length = array.GetLength();
        if ((ulong)index >= length)
        {
            if (!handler.AllowOutOfBounds) return false;
            result = JSValue.Undefined;
            return true;
        }
        result = TypedArrayElementsOps.Load(isolate, array, (ulong)index);
        return true;
    }

    /// <summary>
    /// A keyed store through an element handler (EmitElementStore for the fast
    /// kinds). Returns false when the store needs the runtime (a value that
    /// does not fit the elements kind, a copy-on-write store, growth beyond
    /// the handler's store mode ...); the caller then misses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryStoreFastElement(Isolate isolate, JSObject obj, double key, StoreHandler handler, JSValue value)
    {
        if (!handler.IsValid) return false;
        int index = (int)key;
        if (index != key || index < 0) return false;
        if (handler.ElementsTransitionMap is Map transition)
        {
            // ElementsTransitionAndStore: the pessimistic transition to the most
            // general kind among the polymorphic receiver maps, then the store.
            // JSObject.TransitionElementsKind also feeds the transition back to
            // the allocation site (V8's CSA bails out to the runtime for that).
            if (!ElementsKinds.IsFastElementsKind(obj.Map.ElementsKind) ||
                !ElementsKinds.IsFastElementsKind(transition.ElementsKind) || obj.Elements.IsCowArray)
            {
                return false;
            }
            JSObject.TransitionElementsKind(isolate, obj, transition.ElementsKind);
        }
        ElementsKind kind = obj.Map.ElementsKind;
        if (!ElementsKinds.IsFastElementsKind(kind))
        {
            return ElementsKinds.IsTypedArrayOrRabGsabTypedArrayElementsKind(handler.ElementsKind) && obj is JSTypedArray typedArray &&
                   TryStoreTypedElement(typedArray, key, handler, value);
        }

        // The value must fit the elements kind without a transition.
        if (ElementsKinds.IsSmiElementsKind(kind))
        {
            if (!value.IsSmi) return false;
        }
        else if (ElementsKinds.IsDoubleElementsKind(kind))
        {
            if (!value.IsNumber) return false;
        }

        FixedArrayBase elements = obj.Elements;
        if (elements.IsCowArray) return false;
        JSArray? array = obj as JSArray;
        int length = array is not null ? (int)array.Length._num : elements.Length;
        if (index >= length)
        {
            // CheckForCapacityGrow: only an append at the end of a JSArray.
            if (array is null || handler.StoreMode != KeyedAccessStoreMode.kGrowAndHandleCOW || index != length) return false;
            // The appended index is a hole on the receiver: the prototype chain decides.
            if (!Protectors.IsNoElementsIntact(isolate)) return false;
            if (index >= elements.Length)
            {
                if (!ElementsAccessor.ForKind(kind).GrowCapacity(isolate, obj, (uint)index)) return false;
                if (obj.Map.ElementsKind != kind) return false;
                elements = obj.Elements;
            }
            if (!WriteElement(elements, kind, index, value)) return false;
            array.Length = JSValue.FromInt(index + 1);
            return true;
        }
        // Storing into a hole must look at the prototype chain (a setter or a
        // read-only element there) unless the NoElements protector is intact.
        if (ElementsKinds.IsHoleyElementsKind(kind) && !Protectors.IsNoElementsIntact(isolate) && IsHoleAt(elements, index))
        {
            return false;
        }
        return WriteElement(elements, kind, index, value);
    }

    /// <summary>
    /// The in-bounds case of TryStoreFastElement that needs no transition, no
    /// growth and no prototype chain check: an element of a fast kind that the
    /// value fits, stored over a non-hole value (so holey kinds need no
    /// NoElements protector). False when the full path must decide.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryStoreInBounds(JSObject obj, double key, JSValue value)
    {
        if (!JSValue.TryGetIndex(key, out int index)) return false;
        FixedArrayBase elements = obj.Elements;
        ElementsKind kind = obj.Map.ElementsKind;
        if (obj.InstanceType == InstanceType.JSArrayType && index >= Unsafe.As<JSArray>(obj).Length._num) return false;
        if (elements is FixedArray fixedArray)
        {
            JSValue[] data = fixedArray._data;
            if ((uint)index >= (uint)data.Length || fixedArray.IsCowArray) return false;
            if (ElementsKinds.IsSmiElementsKind(kind))
            {
                if (!value.IsSmi) return false;
            }
            else if (!ElementsKinds.IsObjectElementsKind(kind))
            {
                return false;
            }
            ref JSValue slot = ref data[index];
            if (ReferenceEquals(slot._obj, Oddball.TheHole)) return false;
            slot = value;
            return true;
        }
        if (elements is FixedDoubleArray doubleArray)
        {
            double[] data = doubleArray._data;
            if ((uint)index >= (uint)data.Length || !ElementsKinds.IsDoubleElementsKind(kind) ||
                !ReferenceEquals(value._obj, NumberTag.Instance) || FixedDoubleArray.IsHoleBits(data[index]))
            {
                return false;
            }
            doubleArray.Set(index, value._num);
            return true;
        }
        return false;
    }

    /// <summary>
    /// EmitElementStoreTypedArray for a value that needs no conversion (a
    /// Number, or a BigInt for the BigInt kinds; anything else takes the
    /// runtime, which converts it). Out of bounds, including detached, the
    /// store is skipped when the handler's store mode ignores out-of-bounds
    /// stores and misses otherwise.
    /// </summary>
    static bool TryStoreTypedElement(JSTypedArray array, double key, StoreHandler handler, JSValue value)
    {
        if (ElementsKinds.IsBigIntTypedArrayElementsKind(handler.ElementsKind) ? value._obj is not BigInt : !value.IsNumber)
        {
            return false;
        }
        long index = (long)key;
        if (index != key) return false;
        if (value.IsNumber && TypedArrayElementsOps.TryGetFixedLength(array, out ulong fixedLength) && (ulong)index < fixedLength &&
            !array.Buffer.IsImmutable)
        {
            TypedArrayElementsOps.StoreElement(array, array.Map.ElementsKind, (int)index, value._num);
            return true;
        }
        bool ignoresOob = handler.StoreMode == KeyedAccessStoreMode.kIgnoreTypedArrayOOB;
        if (ignoresOob)
        {
            // An immutable buffer must throw in the runtime.
            if (array.Buffer.IsImmutable) return false;
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kRead, out ulong readLength)) return true;
            // Skip the store beyond the length or to a negative index.
            if ((ulong)index >= readLength) return true;
        }
        else
        {
            if (!TypedArrayElementsOps.TryGetLengthAndValidate(array, TypedArrayAccessMode.kWrite, out ulong length)) return false;
            if ((ulong)index >= length) return false;
        }
        TypedArrayElementsOps.StoreNumeric(array, (ulong)index, value);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsHoleAt(FixedArrayBase elements, int index) => elements switch
    {
        FixedArray fixedArray => fixedArray.IsTheHole(index),
        FixedDoubleArray doubleArray => doubleArray.IsTheHole(index),
        _ => false,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool WriteElement(FixedArrayBase elements, ElementsKind kind, int index, JSValue value)
    {
        if (elements is FixedArray fixedArray)
        {
            if (!ElementsKinds.IsSmiOrObjectElementsKind(kind)) return false;
            fixedArray._data[index] = value;
            return true;
        }
        if (elements is FixedDoubleArray doubleArray)
        {
            if (!ElementsKinds.IsDoubleElementsKind(kind)) return false;
            doubleArray.Set(index, value._num);
            return true;
        }
        return false;
    }
}
