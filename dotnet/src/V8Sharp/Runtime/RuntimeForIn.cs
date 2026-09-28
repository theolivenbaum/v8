// Port of src/runtime/runtime-forin.cc (Runtime_ForInEnumerate,
// Runtime_ForInHasProperty) and of the for-in helpers the bytecode handlers
// call: CodeStubAssembler::ForInPrepare, ForInNextSlow (internal.tq) and the
// ForInFilter builtin (builtins-internal-gen.cc).
namespace V8Sharp.Runtime;

public static class RuntimeForIn
{
    /// <summary>Enumerate (runtime-forin.cc): the receiver's map when the enum cache applies, else the keys.</summary>
    public static JSValue ForInEnumerate(Isolate isolate, JSReceiver receiver)
    {
        JSObject.MakePrototypesFast(receiver, WhereToStart.StartAtReceiver, isolate);
        var accumulator = new FastKeyAccumulator(isolate, receiver, KeyCollectionMode.IncludePrototypes,
            PropertyFilter.ENUMERABLE_STRINGS, isForIn: true);
        // Test if we have an enum cache for {receiver}.
        if (!accumulator.IsReceiverSimpleEnum)
        {
            FixedArray keys = accumulator.GetKeys(accumulator.MayHaveElementsFlag
                ? GetKeysConversion.ConvertToString
                : GetKeysConversion.NoNumbers);
            // Test again, since cache may have been built by GetKeys() calls above.
            if (!accumulator.IsReceiverSimpleEnum) return keys;
        }
        return receiver.Map;
    }

    /// <summary>UpdateFeedback for a for-in slot (the combined ForInFeedback).</summary>
    static void UpdateFeedback(FeedbackVector? vector, int slot, ForInFeedback feedback)
    {
        if (vector is null) return;
        int existing = vector.Slots[slot].IsNumber ? (int)vector.Slots[slot]._num : 0;
        int combined = existing | (int)feedback;
        if (combined != existing) vector.Slots[slot] = JSValue.FromInt(combined);
    }

    /// <summary>CodeStubAssembler::ForInPrepare.</summary>
    public static void ForInPrepare(Isolate isolate, HeapObject enumerator, FeedbackVector? vector, int slot,
        out JSValue cacheArray, out int cacheLength)
    {
        if (enumerator is Map map)
        {
            // Load the enumeration length and cache from the {enumerator}.
            int enumLength = map.EnumLength;
            EnumCache enumCache = map.InstanceDescriptors.EnumCache;

            // Check if we have enum indices available.
            ForInFeedback feedback = enumLength <= enumCache.Indices.Length
                ? ForInFeedback.kEnumCacheKeysAndIndices
                : ForInFeedback.kEnumCacheKeys;
            UpdateFeedback(vector, slot, feedback);

            cacheArray = enumCache.Keys;
            cacheLength = enumLength;
            return;
        }

        // The {enumerator} is a FixedArray with all the keys to iterate.
        var arrayEnumerator = (FixedArray)enumerator;

        // Record the fact that we hit the for-in slow-path.
        UpdateFeedback(vector, slot, ForInFeedback.kAny);

        cacheArray = arrayEnumerator;
        cacheLength = arrayEnumerator.Length;
    }

    /// <summary>ForInNextSlow (internal.tq): records kAny and filters the key.</summary>
    public static JSValue ForInNextSlow(Isolate isolate, FeedbackVector? vector, int slot, JSValue receiver, JSValue key,
        JSValue cacheType)
    {
        UpdateFeedback(vector, slot, ForInFeedback.kAny);
        return ForInFilter(isolate, key, receiver);
    }

    /// <summary>ForInFilter: the key if the receiver still has it as an enumerable property, else undefined.</summary>
    public static JSValue ForInFilter(Isolate isolate, JSValue key, JSValue obj) =>
        HasEnumerableProperty(isolate, obj.As<JSReceiver>(), key).IsUndefined ? JSValue.Undefined : key;

    /// <summary>HasEnumerableProperty (runtime-forin.cc): the name when found and enumerable, else undefined.</summary>
    public static JSValue HasEnumerableProperty(Isolate isolate, JSReceiver receiver, JSValue key)
    {
        PropertyKey lookupKey = PropertyKey.FromKey(isolate, key);
        var it = new LookupIterator(isolate, receiver, lookupKey);
        for (;; it.Next())
        {
            switch (it.State)
            {
                case LookupIterator.StateKind.TRANSITION:
                    throw new UnreachableException();
                case LookupIterator.StateKind.JSPROXY:
                {
                    // For proxies we have to invoke the [[GetOwnProperty]] trap.
                    PropertyAttributes result = JSProxy.GetPropertyAttributes(ref it);
                    if (result == PropertyAttributes.ABSENT)
                    {
                        // Continue lookup on the proxy's prototype.
                        JSProxy proxy = it.GetHolder<JSProxy>();
                        JSReceiver? prototype = JSProxy.GetPrototype(isolate, proxy);
                        if (prototype is null) return JSValue.Undefined;
                        // We already have a stack-check in JSProxy::GetPrototype.
                        return HasEnumerableProperty(isolate, prototype, key);
                    }
                    if ((result & PropertyAttributes.DONT_ENUM) != 0) return JSValue.Undefined;
                    return it.GetName();
                }
                case LookupIterator.StateKind.MODULE_NAMESPACE:
                    continue;
                case LookupIterator.StateKind.STRING_LOOKUP_START_OBJECT:
                    throw new UnreachableException();
                case LookupIterator.StateKind.WASM_OBJECT:
                    continue;  // Continue to the prototype, if present.
                case LookupIterator.StateKind.INTERCEPTOR:
                    continue;
                case LookupIterator.StateKind.ACCESS_CHECK:
                    continue;
                case LookupIterator.StateKind.TYPED_ARRAY_INDEX_NOT_FOUND:
                    // TypedArray out-of-bounds access.
                    return JSValue.Undefined;
                case LookupIterator.StateKind.ACCESSOR:
                    return it.GetName();
                case LookupIterator.StateKind.DATA:
                    return it.GetName();
                case LookupIterator.StateKind.NOT_FOUND:
                    return JSValue.Undefined;
            }
        }
    }
}
