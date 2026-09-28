// Port of src/objects/instance-type.h (the parts with semantic meaning).
//
// V8 encodes string representation bits into the low instance-type values;
// V8Sharp's strings are all two-byte and flat-or-rope, so only the ordering
// invariants that code tests with range checks are kept:
//   strings < other names < other primitives < JSReceivers,
//   JS_PROXY_TYPE is the first receiver, JS_OBJECT_TYPE ranges follow,
//   and the JSFunction subtypes are contiguous.
namespace V8Sharp.Objects;

public enum InstanceType : ushort
{
    // Names.
    SeqStringType,
    ConsStringType,
    SlicedStringType,
    ThinStringType,
    SymbolType,

    // Other primitives.
    HeapNumberType,       // only the JSValue.NumberTag sentinel has this type
    BigIntType,
    OddballType,

    // Internal (non-JS-visible) heap objects.
    MapType,
    FixedArrayType,
    FixedDoubleArrayType,
    ByteArrayType,
    PropertyArrayType,
    DescriptorArrayType,
    TransitionArrayType,
    NameDictionaryType,
    GlobalDictionaryType,
    NumberDictionaryType,
    SimpleNumberDictionaryType,
    OrderedHashMapType,
    OrderedHashSetType,
    OrderedNameDictionaryType,
    SwissNameDictionaryType,
    AccessorPairType,
    AccessorInfoType,
    PropertyCellType,
    CellType,
    ContextType,           // all Context kinds; Context.Kind distinguishes them
    ScopeInfoType,
    SharedFunctionInfoType,
    BytecodeArrayType,
    FeedbackVectorType,
    FeedbackCellType,
    FeedbackMetadataType,
    ClosureFeedbackCellArrayType,
    ScriptType,
    AllocationSiteType,
    ArrayBoilerplateDescriptionType,
    ObjectBoilerplateDescriptionType,
    TemplateObjectDescriptionType,
    ClassBoilerplateType,
    RegExpBoilerplateDescriptionType,
    RegExpDataType,
    RegExpMatchInfoType,
    PromiseReactionType,
    PromiseCapabilityType,
    MicrotaskType,
    AsyncGeneratorRequestType,
    SourceTextModuleType,
    SyntheticModuleType,
    ModuleRequestType,
    SourceTextModuleInfoEntryType,
    CallSiteInfoType,
    ErrorStackDataType,
    InterpreterDataType,
    CodeType,
    ForeignType,
    WeakCellType,
    PrototypeInfoType,
    EnumCacheType,
    ArrayListType,
    WeakFixedArrayType,
    FreeSpaceType,
    FunctionTemplateInfoType,

    // JSReceivers. Keep JSProxyType first: FIRST_JS_RECEIVER_TYPE.
    JSProxyType,
    // FIRST_JS_OBJECT_TYPE: everything from here is a JSObject.
    JSGlobalObjectType,
    JSGlobalProxyType,
    JSSpecialApiObjectType,
    JSPrimitiveWrapperType,
    JSApiObjectType,
    JSObjectType,
    JSArgumentsObjectType,
    JSArrayType,
    JSArrayBufferType,
    JSArrayIteratorType,
    JSAsyncFromSyncIteratorType,
    JSAsyncFunctionObjectType,
    JSAsyncGeneratorObjectType,
    JSGeneratorObjectType,
    JSDataViewType,
    JSRabGsabDataViewType,
    JSTypedArrayType,
    JSDateType,
    JSErrorType,
    JSExternalObjectType,
    JSFinalizationRegistryType,
    JSMapType,
    JSSetType,
    JSMapKeyIteratorType,
    JSMapKeyValueIteratorType,
    JSMapValueIteratorType,
    JSSetKeyValueIteratorType,
    JSSetValueIteratorType,
    JSWeakMapType,
    JSWeakSetType,
    JSWeakRefType,
    JSPromiseType,
    JSRegExpType,
    JSRegExpStringIteratorType,
    JSStringIteratorType,
    JSIteratorPrototypeType,
    // The special prototype types the bootstrapper assigns to intrinsic
    // prototypes so protector checks can recognize them (Genesis).
    JSObjectPrototypeType,
    JSArrayIteratorPrototypeType,
    JSPromisePrototypeType,
    JSRegExpPrototypeType,
    JSStringIteratorPrototypeType,
    JSMapIteratorPrototypeType,
    JSSetIteratorPrototypeType,
    JSSetPrototypeType,
    JSTypedArrayPrototypeType,
    JSIteratorHelperType,
    JSIteratorMapHelperType,
    JSIteratorFilterHelperType,
    JSIteratorTakeHelperType,
    JSIteratorDropHelperType,
    JSIteratorFlatMapHelperType,
    JSIteratorConcatHelperType,
    JSIteratorZipHelperType,
    JSIteratorZipKeyedHelperType,
    JSValidIteratorWrapperType,
    JSDisposableStackBaseType,
    /// <summary>JS_SYNC_DISPOSABLE_STACK_TYPE.</summary>
    JSDisposableStackType,
    JSAsyncDisposableStackType,
    JSSharedArrayType,
    JSSharedStructType,
    JSAtomicsMutexType,
    JSAtomicsConditionType,
    JSModuleNamespaceType,
    JSContextExtensionObjectType,
    JSArgumentsExoticObjectType,
    JSRawJsonType,
    JSShadowRealmType,
    JSWrappedFunctionType,
    JSBoundFunctionType,
    // FIRST_JS_FUNCTION_TYPE: JSFunction and its subtypes.
    JSFunctionType,
    JSFunctionWithoutPrototypeType,
    JSClassConstructorType,
    JSPromiseConstructorType,
    JSArrayConstructorType,
    JSObjectConstructorType,
    JSRegExpConstructorType,
    // LAST_JS_FUNCTION_TYPE / LAST_JS_RECEIVER_TYPE
}

public static class InstanceTypeChecks
{
    public const InstanceType FirstName = InstanceType.SeqStringType;
    public const InstanceType LastString = InstanceType.ThinStringType;
    public const InstanceType LastName = InstanceType.SymbolType;
    public const InstanceType FirstJSReceiver = InstanceType.JSProxyType;
    public const InstanceType FirstJSObject = InstanceType.JSGlobalObjectType;
    public const InstanceType FirstJSFunction = InstanceType.JSFunctionType;
    public const InstanceType LastJSFunction = InstanceType.JSRegExpConstructorType;

    public static bool IsString(InstanceType t) => t <= LastString;
    public static bool IsName(InstanceType t) => t <= LastName;
    public static bool IsJSReceiver(InstanceType t) => t >= FirstJSReceiver;
    public static bool IsJSObject(InstanceType t) => t >= FirstJSObject;
    public static bool IsJSFunction(InstanceType t) => t >= FirstJSFunction;
    public static bool IsJSGeneratorObject(InstanceType t) =>
        t is InstanceType.JSGeneratorObjectType or InstanceType.JSAsyncGeneratorObjectType or InstanceType.JSAsyncFunctionObjectType;
}
