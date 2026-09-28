// Port of the protector-update and bookkeeping parts of
// src/execution/isolate.{h,cc}: UpdateNoElementsProtectorOnSetElement and
// friends, IsArrayOrObjectOrStringPrototype, IsInCreationContext,
// ReportPromiseReject, elements_deletion_counter.
namespace V8Sharp;

public sealed partial class Isolate
{
    public enum KnownPrototype
    {
        None,
        Object,
        Array,
        String,
    }

    /// <summary>
    /// Isolate::elements_deletion_counter: counts deletions from fast holey
    /// elements so ElementsAccessor::Delete can decide when to normalize.
    /// </summary>
    public ulong ElementsDeletionCounter;

    /// <summary>The embedder's PromiseRejectCallback (v8::Isolate::SetPromiseRejectCallback).</summary>
    public Action<JSPromise, JSValue, PromiseRejectEvent>? PromiseRejectCallback;

    /// <summary>Isolate::IsArrayOrObjectOrStringPrototype.</summary>
    public KnownPrototype IsArrayOrObjectOrStringPrototype(JSObject obj)
    {
        NativeContext? nativeContext = obj.GetCreationContext();
        if (nativeContext is null) return KnownPrototype.None;
        if (ReferenceEquals(nativeContext.Slots[(int)Context.Field.INITIAL_OBJECT_PROTOTYPE_INDEX].HeapObjectOrNull, obj))
        {
            return KnownPrototype.Object;
        }
        if (ReferenceEquals(nativeContext.Slots[(int)Context.Field.INITIAL_ARRAY_PROTOTYPE_INDEX].HeapObjectOrNull, obj))
        {
            return KnownPrototype.Array;
        }
        if (ReferenceEquals(nativeContext.Slots[(int)Context.Field.INITIAL_STRING_PROTOTYPE_INDEX].HeapObjectOrNull, obj))
        {
            return KnownPrototype.String;
        }
        return KnownPrototype.None;
    }

    /// <summary>Isolate::IsInCreationContext.</summary>
    public bool IsInCreationContext(JSObject obj, Context.Field index)
    {
        NativeContext? nativeContext = obj.GetCreationContext();
        return nativeContext is not null && ReferenceEquals(nativeContext.Slots[(int)index].HeapObjectOrNull, obj);
    }

    bool IsTypedArrayConstructor(JSObject obj) =>
        IsInCreationContext(obj, Context.Field.UINT8_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.INT8_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.UINT16_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.INT16_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.UINT32_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.INT32_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.BIGUINT64_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.BIGINT64_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.UINT8_CLAMPED_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.FLOAT32_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.FLOAT64_ARRAY_FUN_INDEX) ||
        IsInCreationContext(obj, Context.Field.FLOAT16_ARRAY_FUN_INDEX);

    /// <summary>Isolate::UpdateNoElementsProtectorOnSetElement.</summary>
    public void UpdateNoElementsProtectorOnSetElement(JSObject obj)
    {
        if (!obj.Map.IsPrototypeMap) return;
        if (!Protectors.IsNoElementsIntact(this)) return;
        KnownPrototype objType = IsArrayOrObjectOrStringPrototype(obj);
        if (objType == KnownPrototype.None) return;
        if (objType == KnownPrototype.Object)
        {
            CountUsage(UseCounterFeature.kObjectPrototypeHasElements);
        }
        else if (objType == KnownPrototype.Array)
        {
            CountUsage(UseCounterFeature.kArrayPrototypeHasElements);
        }
        Protectors.InvalidateNoElements(this);
    }

    public void UpdateNoElementsProtectorOnSetLength(JSObject obj) => UpdateNoElementsProtectorOnSetElement(obj);

    public void UpdateNoElementsProtectorOnSetPrototype(JSObject obj) => UpdateNoElementsProtectorOnSetElement(obj);

    public void UpdateNoElementsProtectorOnNormalizeElements(JSObject obj) => UpdateNoElementsProtectorOnSetElement(obj);

    /// <summary>Isolate::UpdateProtectorsOnSetPrototype.</summary>
    public void UpdateProtectorsOnSetPrototype(JSObject obj, JSValue newPrototype)
    {
        UpdateNoElementsProtectorOnSetPrototype(obj);
        UpdateTypedArraySpeciesLookupChainProtectorOnSetPrototype(obj);
        UpdateNumberStringNotRegexpLikeProtectorOnSetPrototype(obj);
        UpdateStringWrapperToPrimitiveProtectorOnSetPrototype(obj, newPrototype);
    }

    public void UpdateTypedArraySpeciesLookupChainProtectorOnSetPrototype(JSObject obj)
    {
        // Setting the __proto__ of TypedArray constructor could change TypedArray's
        // @@species. So we need to invalidate the @@species protector.
        if (IsTypedArrayConstructor(obj) && Protectors.IsTypedArraySpeciesLookupChainIntact(this))
        {
            Protectors.InvalidateTypedArraySpeciesLookupChain(this);
        }
    }

    public void UpdateNumberStringNotRegexpLikeProtectorOnSetPrototype(JSObject obj)
    {
        if (!Protectors.IsNumberStringNotRegexpLikeIntact(this)) return;
        // We need to protect the prototype chain of `Number.prototype` and
        // `String.prototype`. We detect `Number.prototype` and `String.prototype`
        // by checking for a prototype that is a JSPrimitiveWrapper. This is a
        // safe approximation. Using JSPrimitiveWrapper as prototype should be
        // sufficiently rare.
        if (obj.Map.IsPrototypeMap && obj is JSPrimitiveWrapper)
        {
            Protectors.InvalidateNumberStringNotRegexpLike(this);
        }
    }

    public void UpdateStringWrapperToPrimitiveProtectorOnSetPrototype(JSObject obj, JSValue newPrototype)
    {
        if (!Protectors.IsStringWrapperToPrimitiveIntact(this)) return;
        // We can have a custom @@toPrimitive on a string wrapper also if we subclass
        // String and the subclass (or one of its subclasses) defines its own
        // @@toPrimitive. Thus we invalidate the protector whenever we detect
        // subclassing String - it should be reasonably rare.
        if (ObjectOps.IsStringWrapper(obj) || ObjectOps.IsStringWrapper(newPrototype))
        {
            Protectors.InvalidateStringWrapperToPrimitive(this);
        }
    }

    /// <summary>Isolate::ReportPromiseReject.</summary>
    public void ReportPromiseReject(JSPromise promise, JSValue value, PromiseRejectEvent rejectEvent)
    {
        PromiseRejectCallback?.Invoke(promise, value, rejectEvent);
    }

    /// <summary>
    /// Isolate::CountUsage. V8 forwards to the embedder's use counter
    /// callback; V8Sharp has no embedder counters yet.
    /// </summary>
    public void CountUsage(UseCounterFeature feature) { }
}
