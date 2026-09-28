// Port of src/ic/handler-configuration.{h,cc,-inl.h} and
// src/objects/data-handler.h: the IC handlers stored in feedback vectors and
// the megamorphic stub cache.
//
// V8 encodes simple handlers as Smis (kind + field index + flags) and
// prototype-chain handlers as DataHandler objects (smi handler, validity
// cell, data1..3). V8Sharp has one sealed class per direction with the same
// information as fields: the kind, the field index, the holder, the data
// (constant, getter, PropertyCell, transition map ...) and the prototype
// chain validity cell. Handlers are created on IC misses only; the
// interpreter's fast paths test the kind and read the field directly.
using System.Runtime.CompilerServices;

namespace V8Sharp.IC;

/// <summary>V8's LoadHandler (Smi handlers and LoadHandler DataHandlers).</summary>
public sealed class LoadHandler : HeapObject
{
    /// <summary>LoadHandler::Kind, plus the special code handlers V8 uses builtins for.</summary>
    public enum Kind : byte
    {
        kElement,
        kElementWithTransition,
        kIndexedString,
        kNormal,
        kGlobal,
        kField,
        kConstantFromPrototype,
        kAccessorFromPrototype,
        kNativeDataProperty,
        kApiGetter,
        kInterceptor,
        kSlow,
        kProxy,
        kNonExistent,
        kModuleExport,
        kGeneric,
        // The builtin code handlers of LoadIC::ComputeHandler.
        kStringLength,
        kStringWrapperLength,
        kFunctionPrototype,
        // An own accessor pair (V8 stores the AccessorPair itself as the handler).
        kAccessorPair,
        // JSArray length (V8: kField with kArrayLengthFieldDescriptorIndex).
        kArrayLength,
    }

    public readonly Kind HandlerKind;

    /// <summary>kField: the field's property index in the holder's field storage.</summary>
    public readonly int FieldIndex;

    /// <summary>The holder for prototype-chain handlers, or null when it is the lookup start object.</summary>
    public readonly JSReceiver? Holder;

    /// <summary>
    /// data1: the constant (kConstantFromPrototype), the getter
    /// (kAccessorFromPrototype), the PropertyCell (kGlobal), the AccessorPair
    /// (kAccessorPair), the AccessorInfo (kNativeDataProperty) or the exports
    /// cell (kModuleExport).
    /// </summary>
    public readonly JSValue Data;

    /// <summary>The prototype chain validity cell (null when the handler does not depend on the chain).</summary>
    public readonly Cell? ValidityCell;

    /// <summary>LookupOnLookupStartObjectBits: dictionary-mode start objects need a negative lookup.</summary>
    public readonly bool LookupOnLookupStartObject;

    /// <summary>kElement / kIndexedString: AllowOutOfBoundsBits.</summary>
    public readonly bool AllowOutOfBounds;

    /// <summary>kElement: IsJsArrayBits.</summary>
    public readonly bool IsJSArray;

    /// <summary>kElement: AllowHandlingHole (convert holes to undefined when the chain has no elements).</summary>
    public readonly bool AllowHandlingHole;

    /// <summary>kElement: ElementsKindBits.</summary>
    public readonly ElementsKind ElementsKind;

    /// <summary>
    /// The field index of a kField handler for the lookup start object itself,
    /// else -1: the case the interpreter's GetNamedProperty handles inline.
    /// </summary>
    public readonly int OwnFieldIndex;

    /// <summary>A kConstantFromPrototype handler that needs no lookup on the start object (the inline case for methods).</summary>
    public readonly bool IsPrototypeConstant;

    LoadHandler(Kind kind, int fieldIndex = -1, JSReceiver? holder = null, JSValue data = default, Cell? validityCell = null,
        bool lookupOnLookupStartObject = false, bool allowOutOfBounds = false, bool isJSArray = false,
        bool allowHandlingHole = false, ElementsKind elementsKind = default) : base(InstanceType.CodeType)
    {
        HandlerKind = kind;
        FieldIndex = fieldIndex;
        Holder = holder;
        Data = data;
        ValidityCell = validityCell;
        LookupOnLookupStartObject = lookupOnLookupStartObject;
        AllowOutOfBounds = allowOutOfBounds;
        IsJSArray = isJSArray;
        AllowHandlingHole = allowHandlingHole;
        ElementsKind = elementsKind;
        OwnFieldIndex = kind == Kind.kField && holder is null ? fieldIndex : -1;
        IsPrototypeConstant = kind == Kind.kConstantFromPrototype && !lookupOnLookupStartObject;
    }

    static readonly LoadHandler s_slow = new(Kind.kSlow);
    static readonly LoadHandler s_proxy = new(Kind.kProxy);
    static readonly LoadHandler s_normal = new(Kind.kNormal);
    static readonly LoadHandler s_stringLength = new(Kind.kStringLength);
    static readonly LoadHandler s_stringWrapperLength = new(Kind.kStringWrapperLength);
    static readonly LoadHandler s_functionPrototype = new(Kind.kFunctionPrototype);
    static readonly LoadHandler s_arrayLength = new(Kind.kArrayLength);
    static readonly LoadHandler s_nonExistentNoCell = new(Kind.kNonExistent);
    static readonly LoadHandler s_generic = new(Kind.kGeneric);

    public static LoadHandler LoadSlow(Isolate isolate) => s_slow;
    public static LoadHandler LoadProxy(Isolate isolate) => s_proxy;
    public static LoadHandler LoadGeneric(Isolate isolate) => s_generic;
    public static LoadHandler LoadStringLength => s_stringLength;
    public static LoadHandler LoadStringWrapperLength => s_stringWrapperLength;
    public static LoadHandler LoadFunctionPrototype => s_functionPrototype;
    public static LoadHandler LoadArrayLength => s_arrayLength;

    /// <summary>LoadHandler::LoadNormal (own dictionary property).</summary>
    public static LoadHandler LoadNormal(Isolate isolate) => s_normal;

    /// <summary>LoadHandler::LoadField (own fast field).</summary>
    public static LoadHandler LoadField(Isolate isolate, FieldIndex fieldIndex) => new(Kind.kField, fieldIndex.PropertyIndex);

    /// <summary>LoadHandler::LoadNativeDataProperty (own AccessorInfo).</summary>
    public static LoadHandler LoadNativeDataProperty(Isolate isolate, AccessorInfo info) =>
        new(Kind.kNativeDataProperty, data: info);

    /// <summary>An own JS accessor (V8 caches the AccessorPair as the handler).</summary>
    public static LoadHandler LoadAccessorPair(Isolate isolate, AccessorPair pair) => new(Kind.kAccessorPair, data: pair);

    /// <summary>LoadHandler::LoadModuleExport.</summary>
    public static LoadHandler LoadModuleExport(Isolate isolate, Cell cell) => new(Kind.kModuleExport, data: cell);

    /// <summary>
    /// LoadHandler::LoadFromPrototype: a handler for a property found on the
    /// prototype chain (or through a dictionary-mode start object), guarded by
    /// the validity cell of the lookup start object's map.
    /// </summary>
    public static LoadHandler LoadFromPrototype(Isolate isolate, Map lookupStartObjectMap, JSReceiver holder, Kind kind,
        int fieldIndex = -1, JSValue data = default)
    {
        Cell? validityCell = Map.GetOrCreatePrototypeChainValidityCell(lookupStartObjectMap, isolate);
        bool lookupOnStart = lookupStartObjectMap.IsDictionaryMap;
        return new LoadHandler(kind, fieldIndex, holder, data, validityCell, lookupOnStart);
    }

    /// <summary>LoadHandler::LoadNonExistent: undefined while the chain is unchanged.</summary>
    public static LoadHandler LoadNonExistent(Isolate isolate, Map? lookupStartObjectMap = null)
    {
        if (lookupStartObjectMap is null) return s_nonExistentNoCell;
        Cell? validityCell = Map.GetOrCreatePrototypeChainValidityCell(lookupStartObjectMap, isolate);
        return new LoadHandler(Kind.kNonExistent, validityCell: validityCell,
            lookupOnLookupStartObject: lookupStartObjectMap.IsDictionaryMap);
    }

    /// <summary>LoadHandler::LoadElement.</summary>
    public static LoadHandler LoadElement(Isolate isolate, ElementsKind elementsKind, bool isJSArray, bool allowOutOfBounds,
        bool allowHandlingHole) =>
        new(Kind.kElement, allowOutOfBounds: allowOutOfBounds, isJSArray: isJSArray, allowHandlingHole: allowHandlingHole,
            elementsKind: elementsKind);

    /// <summary>LoadHandler::TransitionAndLoadElement: transition a JSArray to <paramref name="kindAfterTransition"/>, then load.</summary>
    public static LoadHandler TransitionAndLoadElement(Isolate isolate, ElementsKind kindAfterTransition, bool allowOutOfBounds,
        bool allowHandlingHole) =>
        new(Kind.kElementWithTransition, allowOutOfBounds: allowOutOfBounds, isJSArray: true, allowHandlingHole: allowHandlingHole,
            elementsKind: kindAfterTransition);

    /// <summary>LoadHandler::LoadIndexedString.</summary>
    public static LoadHandler LoadIndexedString(Isolate isolate, bool allowOutOfBounds) =>
        new(Kind.kIndexedString, allowOutOfBounds: allowOutOfBounds);

    /// <summary>
    /// Whether the prototype chain the handler relies on is still valid
    /// (the validity cell check of AccessorAssembler::HandleLoadICProtoHandler).
    /// </summary>
    public bool IsValid { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ValidityCell is null || !ValidityCell.Value.IsIdenticalTo(Cell.kPrototypeChainInvalid); }

    public override string ToString() => $"<LoadHandler {HandlerKind}>";
}

/// <summary>V8's StoreHandler (Smi handlers, StoreHandler DataHandlers and transition maps).</summary>
public sealed class StoreHandler : HeapObject
{
    /// <summary>StoreHandler::Kind.</summary>
    public enum Kind : byte
    {
        kField,
        kConstField,
        kAccessorFromPrototype,
        kNativeDataProperty,
        kSharedStructField,
        kApiSetter,
        kGlobalProxy,
        kNormal,
        kInterceptor,
        kSlow,
        kProxy,
        kKindsNumber,
        // A map transition that adds a field (V8 caches the transition map).
        kTransitionToField,
        // A map transition that adds a descriptor-located constant.
        kTransitionToConstant,
        // An own accessor pair's setter.
        kAccessorPair,
        // A store to a global object PropertyCell.
        kGlobalCell,
        // KeyedStoreIC element stores (StoreHandler::StoreElementTransition / element code handlers).
        kElement,
        // StoreIC for the length of an array (the ArrayLength setter).
        kArrayLength,
    }

    public readonly Kind HandlerKind;

    /// <summary>kField / kTransitionToField: the property index of the field.</summary>
    public readonly int FieldIndex;

    /// <summary>kField / kTransitionToField: the field representation the value must fit.</summary>
    public readonly Representation Representation;

    /// <summary>The field type (a Map for FieldType::Class fields), or null for Any.</summary>
    public readonly Map? FieldTypeClass;

    /// <summary>kTransitionToField / kTransitionToConstant: the transition target.</summary>
    public readonly Map? TransitionMap;

    /// <summary>The holder of a setter found on the prototype chain.</summary>
    public readonly JSReceiver? Holder;

    /// <summary>data1: the setter, AccessorPair, AccessorInfo or PropertyCell.</summary>
    public readonly JSValue Data;

    /// <summary>The prototype chain validity cell.</summary>
    public readonly Cell? ValidityCell;

    /// <summary>kElement: the elements kind of the receiver map.</summary>
    public readonly ElementsKind ElementsKind;

    /// <summary>kElement: the map to transition the elements kind to (null when none).</summary>
    public readonly Map? ElementsTransitionMap;

    /// <summary>kElement: KeyedAccessStoreMode (grow / handle COW / ignore OOB).</summary>
    public readonly KeyedAccessStoreMode StoreMode;

    /// <summary>
    /// The field index of a kField handler whose representation is Tagged (any
    /// value fits), else -1: the case the interpreter's SetNamedProperty handles inline.
    /// </summary>
    public readonly int TaggedFieldIndex;

    StoreHandler(Kind kind, int fieldIndex = -1, Representation representation = default, Map? fieldTypeClass = null,
        Map? transitionMap = null, JSReceiver? holder = null, JSValue data = default, Cell? validityCell = null,
        ElementsKind elementsKind = default, Map? elementsTransitionMap = null,
        KeyedAccessStoreMode storeMode = KeyedAccessStoreMode.kInBounds) : base(InstanceType.CodeType)
    {
        HandlerKind = kind;
        FieldIndex = fieldIndex;
        Representation = representation;
        FieldTypeClass = fieldTypeClass;
        TransitionMap = transitionMap;
        Holder = holder;
        Data = data;
        ValidityCell = validityCell;
        ElementsKind = elementsKind;
        ElementsTransitionMap = elementsTransitionMap;
        StoreMode = storeMode;
        TaggedFieldIndex = kind == Kind.kField && representation.IsTagged ? fieldIndex : -1;
    }

    static readonly StoreHandler s_slow = new(Kind.kSlow);
    static readonly StoreHandler s_proxy = new(Kind.kProxy);
    static readonly StoreHandler s_normal = new(Kind.kNormal);
    static readonly StoreHandler s_arrayLength = new(Kind.kArrayLength);

    public static StoreHandler StoreSlow(Isolate isolate) => s_slow;
    public static StoreHandler StoreProxy(Isolate isolate) => s_proxy;
    public static StoreHandler StoreNormal(Isolate isolate) => s_normal;
    public static StoreHandler StoreArrayLength => s_arrayLength;

    /// <summary>StoreHandler::StoreField.</summary>
    public static StoreHandler StoreField(Isolate isolate, FieldIndex fieldIndex, Representation representation,
        HeapObject fieldType, bool isConst) =>
        new(isConst ? Kind.kConstField : Kind.kField, fieldIndex.PropertyIndex, representation,
            V8Sharp.Objects.FieldType.IsClass(fieldType) ? V8Sharp.Objects.FieldType.AsClass(fieldType) : null);

    /// <summary>StoreHandler::StoreTransition for a transition that adds a field.</summary>
    public static StoreHandler StoreTransitionToField(Isolate isolate, Map receiverMap, Map transitionMap, FieldIndex fieldIndex,
        Representation representation, HeapObject fieldType)
    {
        Cell? validityCell = Map.GetOrCreatePrototypeChainValidityCell(receiverMap, isolate);
        return new StoreHandler(Kind.kTransitionToField, fieldIndex.PropertyIndex, representation,
            V8Sharp.Objects.FieldType.IsClass(fieldType) ? V8Sharp.Objects.FieldType.AsClass(fieldType) : null,
            transitionMap, validityCell: validityCell);
    }

    /// <summary>StoreHandler::StoreTransition for a transition to a constant (descriptor-located) property.</summary>
    public static StoreHandler StoreTransitionToConstant(Isolate isolate, Map receiverMap, Map transitionMap)
    {
        Cell? validityCell = Map.GetOrCreatePrototypeChainValidityCell(receiverMap, isolate);
        return new StoreHandler(Kind.kTransitionToConstant, transitionMap: transitionMap, validityCell: validityCell);
    }

    /// <summary>StoreHandler::StoreThroughPrototype for a JS setter on the prototype chain.</summary>
    public static StoreHandler StoreAccessorFromPrototype(Isolate isolate, Map receiverMap, JSReceiver holder, JSValue setter)
    {
        Cell? validityCell = Map.GetOrCreatePrototypeChainValidityCell(receiverMap, isolate);
        return new StoreHandler(Kind.kAccessorFromPrototype, holder: holder, data: setter, validityCell: validityCell);
    }

    /// <summary>An own accessor pair's setter.</summary>
    public static StoreHandler StoreAccessorPair(Isolate isolate, AccessorPair pair) => new(Kind.kAccessorPair, data: pair);

    /// <summary>StoreHandler::StoreNativeDataProperty; a null holder means the receiver itself.</summary>
    public static StoreHandler StoreNativeDataProperty(Isolate isolate, JSReceiver? holder, AccessorInfo info, Cell? validityCell) =>
        new(Kind.kNativeDataProperty, holder: holder, data: info, validityCell: validityCell);

    /// <summary>StoreHandler::StoreGlobal (a PropertyCell of the global object).</summary>
    public static StoreHandler StoreGlobal(Isolate isolate, PropertyCell cell) => new(Kind.kGlobalCell, data: cell);

    /// <summary>StoreHandler for element stores (StoreElementTransition / the element store builtins).</summary>
    public static StoreHandler StoreElement(Isolate isolate, ElementsKind elementsKind, Map? transitionMap,
        KeyedAccessStoreMode storeMode, Cell? validityCell = null) =>
        new(Kind.kElement, elementsKind: elementsKind, elementsTransitionMap: transitionMap, storeMode: storeMode,
            validityCell: validityCell);

    public bool IsValid { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ValidityCell is null || !ValidityCell.Value.IsIdenticalTo(Cell.kPrototypeChainInvalid); }

    public override string ToString() => $"<StoreHandler {HandlerKind}>";
}

/// <summary>V8's KeyedAccessStoreMode (src/common/globals.h).</summary>
public enum KeyedAccessStoreMode : byte
{
    kInBounds,
    kGrowAndHandleCOW,
    kIgnoreTypedArrayOOB,
    kHandleCOW,
}

/// <summary>V8's KeyedAccessLoadMode (src/common/globals.h).</summary>
[Flags]
public enum KeyedAccessLoadMode : byte
{
    kInBounds = 0b00,
    kHandleOOB = 0b01,
    kHandleHoles = 0b10,
    kHandleOOBAndHoles = 0b11,
}
