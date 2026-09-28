// Port of the fast element access paths of src/ic/accessor-assembler.cc
// (EmitElementLoad, HandleLoadICSmiHandlerLoadNamedCase's element case) and
// src/codegen/code-stub-assembler.cc (EmitElementStore, CheckForCapacityGrow)
// for the fast elements kinds. Everything else (typed arrays, dictionary,
// sealed/frozen, sloppy arguments, transitions) misses to the runtime.
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
    /// A keyed store through an element handler (EmitElementStore for the fast
    /// kinds). Returns false when the store needs the runtime (a value that
    /// does not fit the elements kind, a copy-on-write store, growth beyond
    /// the handler's store mode ...); the caller then misses.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryStoreFastElement(Isolate isolate, JSObject obj, double key, StoreHandler handler, JSValue value)
    {
        int index = (int)key;
        if (index != key || index < 0) return false;
        ElementsKind kind = obj.Map.ElementsKind;
        if (!ElementsKinds.IsFastElementsKind(kind)) return false;
        if (handler.ElementsTransitionMap is { } transitionMap)
        {
            // ElementsTransitionAndStore: move the receiver to the more general
            // map the IC has seen first (this also feeds the transition into an
            // allocation memento's site), then store.
            ElementsKind toKind = transitionMap.ElementsKind;
            if (!ElementsKinds.IsFastElementsKind(toKind) || !ElementsKinds.IsMoreGeneralElementsKindTransition(kind, toKind)) return false;
            if (obj.Elements.IsCowArray) return false;
            JSObject.TransitionElementsKind(isolate, obj, toKind);
            if (!ReferenceEquals(obj.Map, transitionMap)) return false;
            kind = toKind;
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
