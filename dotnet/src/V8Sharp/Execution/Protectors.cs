// Port of src/execution/protectors.{h,cc}: isolate-wide flags that fast paths
// test ("has anyone touched Array.prototype[Symbol.iterator]?"). Once
// invalidated a protector stays invalid. V8 keeps each in a PropertyCell so
// generated code can embed it; V8Sharp keeps a bool per protector.
namespace V8Sharp;

/// <summary>V8's Protectors (DECLARED_PROTECTORS_ON_ISOLATE).</summary>
public sealed class Protectors
{
    /// <summary>Called on every invalidation with the protector's name (--trace-protector-invalidation, dependent code).</summary>
    public static event Action<Isolate, string>? OnInvalidate;

    bool _arrayBufferDetaching = true;
    bool _arrayBufferMutable = true;
    bool _arrayIteratorLookupChain = true;
    bool _arraySpeciesLookupChain = true;
    bool _isConcatSpreadableLookupChain = true;
    bool _noDateTimeConfigurationChange = true;
    bool _noElements = true;
    bool _megaDOM = true;
    bool _noProfiling = true;
    bool _noUndetectableObjects = true;
    bool _mapIteratorLookupChain = true;
    bool _numberStringNotRegexpLike = true;
    bool _regExpSpeciesLookupChain = true;
    bool _promiseHook = true;
    bool _promiseThenLookupChain = true;
    bool _promiseResolveLookupChain = true;
    bool _promiseSpeciesLookupChain = true;
    bool _setIteratorLookupChain = true;
    bool _stringIteratorLookupChain = true;
    bool _stringLengthOverflowLookupChain = true;
    bool _stringWrapperToPrimitive = true;
    bool _typedArraySpeciesLookupChain = true;

    static void Invalidate(Isolate isolate, ref bool cell, string name)
    {
        if (!cell) return;
        if (isolate.Flags.trace_protector_invalidation) Console.WriteLine($"Invalidating protector cell {name}");
        cell = false;
        OnInvalidate?.Invoke(isolate, name);
    }

    public static bool IsArrayBufferDetachingIntact(Isolate isolate) => isolate.Protectors._arrayBufferDetaching;
    public static void InvalidateArrayBufferDetaching(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._arrayBufferDetaching, "ArrayBufferDetaching");
    public static bool IsArrayBufferMutableIntact(Isolate isolate) => isolate.Protectors._arrayBufferMutable;
    public static void InvalidateArrayBufferMutable(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._arrayBufferMutable, "ArrayBufferMutable");
    public static bool IsArrayIteratorLookupChainIntact(Isolate isolate) => isolate.Protectors._arrayIteratorLookupChain;
    public static void InvalidateArrayIteratorLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._arrayIteratorLookupChain, "ArrayIteratorLookupChain");
    public static bool IsArraySpeciesLookupChainIntact(Isolate isolate) => isolate.Protectors._arraySpeciesLookupChain;
    public static void InvalidateArraySpeciesLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._arraySpeciesLookupChain, "ArraySpeciesLookupChain");
    public static bool IsIsConcatSpreadableLookupChainIntact(Isolate isolate) => isolate.Protectors._isConcatSpreadableLookupChain;
    public static void InvalidateIsConcatSpreadableLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._isConcatSpreadableLookupChain, "IsConcatSpreadableLookupChain");
    public static bool IsNoDateTimeConfigurationChangeIntact(Isolate isolate) => isolate.Protectors._noDateTimeConfigurationChange;
    public static void InvalidateNoDateTimeConfigurationChange(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._noDateTimeConfigurationChange, "NoDateTimeConfigurationChange");
    public static bool IsNoElementsIntact(Isolate isolate) => isolate.Protectors._noElements;
    public static void InvalidateNoElements(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._noElements, "NoElements");
    public static bool IsMegaDOMIntact(Isolate isolate) => isolate.Protectors._megaDOM;
    public static void InvalidateMegaDOM(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._megaDOM, "MegaDOM");
    public static bool IsNoProfilingIntact(Isolate isolate) => isolate.Protectors._noProfiling;
    public static void InvalidateNoProfiling(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._noProfiling, "NoProfiling");
    public static bool IsNoUndetectableObjectsIntact(Isolate isolate) => isolate.Protectors._noUndetectableObjects;
    public static void InvalidateNoUndetectableObjects(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._noUndetectableObjects, "NoUndetectableObjects");
    public static bool IsMapIteratorLookupChainIntact(Isolate isolate) => isolate.Protectors._mapIteratorLookupChain;
    public static void InvalidateMapIteratorLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._mapIteratorLookupChain, "MapIteratorLookupChain");
    public static bool IsNumberStringNotRegexpLikeIntact(Isolate isolate) => isolate.Protectors._numberStringNotRegexpLike;
    public static void InvalidateNumberStringNotRegexpLike(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._numberStringNotRegexpLike, "NumberStringNotRegexpLike");
    public static bool IsRegExpSpeciesLookupChainIntact(Isolate isolate) => isolate.Protectors._regExpSpeciesLookupChain;
    public static void InvalidateRegExpSpeciesLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._regExpSpeciesLookupChain, "RegExpSpeciesLookupChain");
    public static bool IsPromiseHookIntact(Isolate isolate) => isolate.Protectors._promiseHook;
    public static void InvalidatePromiseHook(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._promiseHook, "PromiseHook");
    public static bool IsPromiseThenLookupChainIntact(Isolate isolate) => isolate.Protectors._promiseThenLookupChain;
    public static void InvalidatePromiseThenLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._promiseThenLookupChain, "PromiseThenLookupChain");
    public static bool IsPromiseResolveLookupChainIntact(Isolate isolate) => isolate.Protectors._promiseResolveLookupChain;
    public static void InvalidatePromiseResolveLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._promiseResolveLookupChain, "PromiseResolveLookupChain");
    public static bool IsPromiseSpeciesLookupChainIntact(Isolate isolate) => isolate.Protectors._promiseSpeciesLookupChain;
    public static void InvalidatePromiseSpeciesLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._promiseSpeciesLookupChain, "PromiseSpeciesLookupChain");
    public static bool IsSetIteratorLookupChainIntact(Isolate isolate) => isolate.Protectors._setIteratorLookupChain;
    public static void InvalidateSetIteratorLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._setIteratorLookupChain, "SetIteratorLookupChain");
    public static bool IsStringIteratorLookupChainIntact(Isolate isolate) => isolate.Protectors._stringIteratorLookupChain;
    public static void InvalidateStringIteratorLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._stringIteratorLookupChain, "StringIteratorLookupChain");
    public static bool IsStringLengthOverflowLookupChainIntact(Isolate isolate) => isolate.Protectors._stringLengthOverflowLookupChain;
    public static void InvalidateStringLengthOverflowLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._stringLengthOverflowLookupChain, "StringLengthOverflowLookupChain");
    public static bool IsStringWrapperToPrimitiveIntact(Isolate isolate) => isolate.Protectors._stringWrapperToPrimitive;
    public static void InvalidateStringWrapperToPrimitive(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._stringWrapperToPrimitive, "StringWrapperToPrimitive");
    public static bool IsTypedArraySpeciesLookupChainIntact(Isolate isolate) => isolate.Protectors._typedArraySpeciesLookupChain;
    public static void InvalidateTypedArraySpeciesLookupChain(Isolate isolate) => Invalidate(isolate, ref isolate.Protectors._typedArraySpeciesLookupChain, "TypedArraySpeciesLookupChain");
}
