// Copyright 2018 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/common/message-template.h (generated from MESSAGE_TEMPLATES)
// and MessageFormatter::TemplateString/TryFormat from src/execution/messages.cc.
// Regenerate rather than edit by hand when syncing V8.

namespace V8Sharp.Common;

public enum MessageTemplate
{
    None,
    ConflictingPrivateName,
    CyclicProto,
    Debugger,
    DebuggerLoading,
    DefaultOptionsMissing,
    DeletePrivateField,
    PlaceholderOnly,
    UncaughtException,
    Unsupported,
    WrongServiceType,
    WrongValueType,
    IcuError,
    TargetNonFunction,
    ArgumentsDisallowedInInitializerAndStaticBlock,
    ArgumentIsNonObject,
    ArgumentIsNonString,
    ArrayBufferDetachKeyDoesntMatch,
    ArrayBufferTooShort,
    ArrayBufferSpeciesThis,
    AwaitNotInAsyncContext,
    AwaitNotInDebugEvaluate,
    AtomicsMutexNotOwnedByCurrentThread,
    AtomicsOperationNotAllowed,
    BadRoundingType,
    BadSortComparisonFunction,
    BigIntFromNumber,
    BigIntFromObject,
    BigIntMixedTypes,
    BigIntSerializeJSON,
    BigIntShr,
    BigIntToNumber,
    CalledNonCallable,
    CalledOnNonObject,
    CalledOnNullOrUndefined,
    CallShadowRealmEvaluateThrew,
    CallSiteExpectsFunction,
    CallSiteMethod,
    CallSiteMethodCrossedShadowRealmBoundary,
    CallWrappedFunctionThrew,
    CannotBeShared,
    CannotConvertToPrimitive,
    CannotPreventExt,
    CannotFreeze,
    CannotFreezeArrayBufferView,
    CannotSeal,
    CannotSealArrayBufferView,
    CannotWrap,
    CircularStructure,
    ConstructAbstractClass,
    ConstAssign,
    ConstructorClassField,
    ConstructorNonCallable,
    AnonymousConstructorNonCallable,
    ConstructorNotFunction,
    ConstructorNotReceiver,
    CurrencyCode,
    CyclicModuleDependency,
    DataViewNotArrayBuffer,
    DateType,
    DebuggerFrame,
    DebuggerType,
    DeclarationMissingInitializer,
    DefineDisallowed,
    DefineDisallowedFixedLayout,
    TypedArrayDetachedErrorOperation,
    TypedArrayOOBErrorOperation,
    TypedArrayValidateErrorOperation,
    TypedArrayValidateWriteErrorOperation,
    TypedArrayImmutableBufferErrorOperation,
    DoNotUse,
    DuplicateTemplateProperty,
    ExtendsValueNotConstructor,
    FirstArgumentNotRegExp,
    FunctionBind,
    GeneratorRunning,
    IllegalInvocation,
    ImmutablePrototypeSet,
    ImportAttributesDuplicateKey,
    ImportCallNotNewExpression,
    ImportOutsideModule,
    ImportMetaOutsideModule,
    ImportMissingSpecifier,
    ImportShadowRealmRejected,
    IncompatibleMethodReceiver,
    InstanceofNonobjectProto,
    InvalidArgument,
    InvalidArgumentForTemporal,
    InvalidOption,
    InvalidInOperatorUse,
    InvalidRawJsonValue,
    InvalidRegExpExecResult,
    InvalidUnit,
    IsNotNumber,
    IterableYieldedNonString,
    IteratorReduceNoInitial,
    IteratorResultNotAnObject,
    InvalidIteratorZipMode,
    IteratorZipStrictMismatch,
    SpreadIteratorSymbolNonCallable,
    FirstArgumentIteratorSymbolNonCallable,
    FirstArgumentAsyncIteratorSymbolNonCallable,
    IteratorValueNotAnObject,
    KeysMethodInvalid,
    LanguageID,
    LocaleNotEmpty,
    LocaleBadParameters,
    ListFormatBadParameters,
    MapperFunctionNonCallable,
    MethodInvokedOnWrongType,
    NoAccess,
    NonCallableInInstanceOfCheck,
    NonCoercible,
    NonCoercibleWithProperty,
    NonExtensibleProto,
    NonObjectAttributesOption,
    NonObjectInInstanceOfCheck,
    NonObjectPrivateNameAccess,
    NonObjectPropertyLoad,
    NonObjectPropertyLoadWithProperty,
    NonObjectPropertyStore,
    NonObjectPropertyStoreWithProperty,
    NonObjectImportArgument,
    NonStringImportAttributeValue,
    NoSetterInCallback,
    NotAnIterator,
    NotReadyForSyncExec,
    PromiseNewTargetUndefined,
    NotConstructor,
    NotDateObject,
    NotGeneric,
    NotCallable,
    NotCallableOrIterable,
    NotCallableOrAsyncIterable,
    NotFiniteNumber,
    NotIterable,
    NotIterableNoSymbolLoad,
    NotAsyncIterable,
    NotPropertyName,
    NotTypedArray,
    NotSuperConstructor,
    NotSuperConstructorAnonymousClass,
    NotIntegerTypedArray,
    NotInt32OrBigInt64TypedArray,
    NotSharedTypedArray,
    ObjectFixedLayout,
    ObjectGetterExpectingFunction,
    ObjectGetterCallable,
    ObjectNotExtensible,
    ObjectSetterExpectingFunction,
    ObjectSetterCallable,
    OrdinaryFunctionCalledAsConstructor,
    PromiseCyclic,
    PromiseExecutorAlreadyInvoked,
    PromiseNonCallable,
    PropertyDescObject,
    PropertyNotFunction,
    ProtoObjectOrNull,
    PrototypeParentNotAnObject,
    ProxyConstructNonObject,
    ProxyDefinePropertyNonConfigurable,
    ProxyDefinePropertyNonConfigurableWritable,
    ProxyDefinePropertyNonExtensible,
    ProxyDefinePropertyIncompatible,
    ProxyDeletePropertyNonConfigurable,
    ProxyDeletePropertyNonExtensible,
    ProxyGetNonConfigurableData,
    ProxyGetNonConfigurableAccessor,
    ProxyGetOwnPropertyDescriptorIncompatible,
    ProxyGetOwnPropertyDescriptorInvalid,
    ProxyGetOwnPropertyDescriptorNonConfigurable,
    ProxyGetOwnPropertyDescriptorNonConfigurableWritable,
    ProxyGetOwnPropertyDescriptorNonExtensible,
    ProxyGetOwnPropertyDescriptorUndefined,
    ProxyGetPrototypeOfInvalid,
    ProxyGetPrototypeOfNonExtensible,
    ProxyHasNonConfigurable,
    ProxyHasNonExtensible,
    ProxyIsExtensibleInconsistent,
    ProxyNonObject,
    ProxyOwnKeysMissing,
    ProxyOwnKeysNonExtensible,
    ProxyOwnKeysDuplicateEntries,
    ProxyPreventExtensionsExtensible,
    ProxyPrivate,
    ProxyRevoked,
    ProxySetFrozenData,
    ProxySetFrozenAccessor,
    ProxySetPrototypeOfNonExtensible,
    ProxyTrapReturnedFalsish,
    ProxyTrapReturnedFalsishFor,
    RedefineDisallowed,
    RedefineExternalArray,
    ReduceNoInitial,
    RegExpFlags,
    RegExpNonObject,
    RegExpNonRegExp,
    RegExpGlobalInvokedOnNonGlobal,
    RelativeDateTimeFormatterBadParameters,
    ResolverNotAFunction,
    ReturnMethodNotCallable,
    SizeIsNaN,
    ShadowRealmErrorStackNonString,
    ShadowRealmErrorStackThrows,
    SharedArrayBufferTooShort,
    SharedArrayBufferSpeciesThis,
    SharedStructTypeRegistryMismatch,
    StaticPrototype,
    StrictCannotCreateProperty,
    StrictCannotDeleteProperty,
    StrictCannotSetProperty,
    StrictPoisonPill,
    StrictReadOnlyProperty,
    StringMatchAllNullOrUndefinedFlags,
    SymbolIteratorInvalid,
    SymbolAsyncIteratorInvalid,
    SymbolKeyFor,
    SymbolToNumber,
    SymbolToString,
    Temporal,
    TemporalWithArg,
    ThrowMethodMissing,
    TopLevelAwaitStalled,
    UndefinedOrNullToObject,
    AwaitUsingAssign,
    UsingAssign,
    ValueAndAccessor,
    VarRedeclaration,
    VarNotAllowedInEvalScope,
    WrongArgs,
    NotDefined,
    SuperAlreadyCalled,
    AccessedUninitializedVariable,
    UnsupportedSuper,
    AccessedUnavailableVariable,
    DisposableStackIsDisposed,
    NotAnAsyncDisposableStack,
    BigIntDivZero,
    BigIntTooBig,
    CantSetOptionXWhenYIsUsed,
    DateRange,
    ExpectedLocation,
    InvalidArrayBufferLength,
    InvalidArrayBufferMaxLength,
    InvalidArrayBufferResizeLength,
    ArrayBufferAllocationFailed,
    Invalid,
    InvalidArrayLength,
    InvalidAtomicAccessIndex,
    InvalidCalendar,
    InvalidCodePoint,
    InvalidCountValue,
    InvalidDataViewAccessorOffset,
    InvalidDataViewLength,
    InvalidOffset,
    InvalidHint,
    InvalidIndex,
    InvalidLanguageTag,
    InvalidWeakMapKey,
    InvalidWeakSetValue,
    InvalidShadowRealmEvaluateSourceText,
    InvalidStringLength,
    InvalidTimeValue,
    InvalidTimeZone,
    InvalidTypedArrayAlignment,
    InvalidTypedArrayIndex,
    InvalidTypedArrayLength,
    LetInLexicalBinding,
    LocaleMatcher,
    MaximumFractionDigitsNotEqualMinimumFractionDigits,
    NormalizationForm,
    OutOfMemory,
    ZeroDigitNumericSeparator,
    NumberFormatRange,
    TrailingNumericSeparator,
    ContinuousNumericSeparator,
    PropertyValueOutOfRange,
    StackOverflow,
    ToPrecisionFormatRange,
    ToRadixFormatRange,
    SharedArraySizeOutOfRange,
    StructFieldCountOutOfRange,
    TypedArraySetOffsetOutOfBounds,
    TypedArraySetSourceTooLarge,
    TypedArrayTooLargeToSort,
    IterableTooLargeToSum,
    ValueOutOfRange,
    CollectionGrowFailed,
    MustBePositive,
    ArgumentIsNotUndefinedOrInteger,
    AmbiguousExport,
    BadGetterArity,
    BadSetterArity,
    Base64ExtraBits,
    Base64InputRemainder,
    BigIntInvalidString,
    ConstructorIsAccessor,
    ConstructorIsGenerator,
    ConstructorIsAsync,
    ConstructorIsPrivate,
    DerivedConstructorReturnedNonObject,
    DuplicateConstructor,
    DuplicateExport,
    DuplicateProto,
    ForInOfLoopInitializer,
    ForOfLet,
    ForOfAsync,
    ForInOfLoopMultiBindings,
    GeneratorInSingleStatementContext,
    AsyncFunctionInSingleStatementContext,
    IllegalBreak,
    ModuleExportNameWithoutFromClause,
    NoIterationStatement,
    IllegalContinue,
    IllegalLanguageModeDirective,
    IllegalReturn,
    IntrinsicWithSpread,
    InvalidBase64Character,
    InvalidRestBindingPattern,
    InvalidPropertyBindingPattern,
    InvalidRestAssignmentPattern,
    InvalidEscapedReservedWord,
    InvalidEscapedMetaProperty,
    InvalidLhsInAssignment,
    InvalidCoverInitializedName,
    InvalidDestructuringTarget,
    InvalidLhsInFor,
    InvalidLhsInPostfixOp,
    InvalidLhsInPrefixOp,
    InvalidHexString,
    InvalidModuleExportName,
    InvalidRegExpFlags,
    InvalidOrUnexpectedToken,
    InvalidPrivateBrandInstance,
    InvalidPrivateBrandStatic,
    InvalidPrivateBrandReinitialization,
    InvalidPrivateFieldReinitialization,
    InvalidPrivateFieldResolution,
    InvalidPrivateMemberRead,
    InvalidPrivateMemberWrite,
    InvalidPrivateMethodWrite,
    InvalidPrivateGetterAccess,
    InvalidPrivateSetterAccess,
    InvalidSizeValue,
    InvalidUnusedPrivateStaticMethodAccessedByDebugger,
    InvalidUsingInForInLoop,
    JsonParseUnexpectedEOS,
    JsonParseUnexpectedTokenNumber,
    JsonParseUnexpectedTokenString,
    JsonParseUnterminatedString,
    JsonParseExpectedPropNameOrRBrace,
    JsonParseExpectedCommaOrRBrack,
    JsonParseExpectedCommaOrRBrace,
    JsonParseExpectedDoubleQuotedPropertyName,
    JsonParseExponentPartMissingNumber,
    JsonParseExpectedColonAfterPropertyName,
    JsonParseUnterminatedFractionalNumber,
    JsonParseUnexpectedNonWhiteSpaceCharacter,
    JsonParseBadEscapedCharacter,
    JsonParseBadControlCharacter,
    JsonParseBadUnicodeEscape,
    JsonParseNoNumberAfterMinusSign,
    JsonParseShortString,
    JsonParseUnexpectedTokenShortString,
    JsonParseUnexpectedTokenSurroundStringWithContext,
    JsonParseUnexpectedTokenEndStringWithContext,
    JsonParseUnexpectedTokenStartStringWithContext,
    LabelRedeclaration,
    LabelledFunctionDeclaration,
    MalformedArrowFunParamList,
    MalformedRegExp,
    MalformedRegExpFlags,
    ModuleExportUndefined,
    MissingFunctionName,
    MismatchedCalendars,
    HtmlCommentInModule,
    MultipleDefaultsInSwitch,
    NewlineAfterThrow,
    NoCatchOrFinally,
    ParamAfterRest,
    FlattenPastSafeLength,
    PushPastSafeLength,
    ElementAfterRest,
    BadSetterRestParameter,
    ParamDupe,
    ArgStringTerminatesParametersEarly,
    UnexpectedEndOfArgString,
    RestDefaultInitializer,
    RuntimeWrongNumArgs,
    SuperNotCalled,
    SingleFunctionLiteral,
    SloppyFunction,
    SpeciesNotConstructor,
    StrictDelete,
    StrictEvalArguments,
    StrictFunction,
    StrictOctalLiteral,
    StrictDecimalWithLeadingZero,
    StrictOctalEscape,
    Strict8Or9Escape,
    StrictWith,
    TemplateOctalLiteral,
    Template8Or9Escape,
    ThisFormalParameter,
    AwaitBindingIdentifier,
    AwaitExpressionFormalParameter,
    TooManyArguments,
    TooManyParameters,
    TooManyProperties,
    TooManySpreads,
    TooManyVariables,
    TooManyEvals,
    TooManyElementsInPromiseCombinator,
    TypedArrayTooShort,
    UnexpectedEOS,
    UnexpectedPrivateField,
    UnexpectedReserved,
    UnexpectedStrictReserved,
    UnexpectedSuper,
    UnexpectedNewTarget,
    UnexpectedTemplateString,
    UnexpectedToken,
    UnexpectedTokenUnaryExponentiation,
    UnexpectedTokenIdentifier,
    UnexpectedTokenNumber,
    UnexpectedTokenString,
    UnexpectedTokenRegExp,
    UnexpectedLexicalDeclaration,
    UnknownLabel,
    UnresolvableExport,
    UnterminatedArgList,
    UnterminatedRegExp,
    UnterminatedTemplate,
    UnterminatedTemplateExpr,
    FoundNonCallableHasInstance,
    InvalidHexEscapeSequence,
    InvalidUnicodeEscapeSequence,
    UndefinedUnicodeCodePoint,
    YieldInParameter,
    CodeGenFromStrings,
    NoSideEffectDebugEvaluate,
    URIMalformed,
    WasmTrapUnreachable,
    WasmTrapMemOutOfBounds,
    WasmTrapUnalignedAccess,
    WasmTrapDivByZero,
    WasmTrapDivUnrepresentable,
    WasmTrapRemByZero,
    WasmTrapFloatUnrepresentable,
    WasmTrapTableOutOfBounds,
    WasmTrapNullFunc,
    WasmTrapFuncSigMismatch,
    WasmTrapMultiReturnLengthMismatch,
    WasmTrapJSTypeError,
    WasmTrapDataSegmentOutOfBounds,
    WasmTrapElementSegmentOutOfBounds,
    WasmTrapRethrowNull,
    WasmTrapNullDereference,
    WasmTrapIllegalCast,
    WasmTrapArrayOutOfBounds,
    WasmTrapArrayTooLarge,
    WasmTrapStringInvalidUtf8,
    WasmTrapStringInvalidWtf8,
    WasmTrapStringOffsetOutOfBounds,
    WasmTrapResume,
    WasmTrapSuspend,
    WasmTrapSwitch,
    WasmSuspendError,
    WasmFXSuspendError,
    WasmTrapStringIsolatedSurrogate,
    WasmSuspendJSFrames,
    WasmExceptionError,
    WasmObjectsAreOpaque,
    DataCloneError,
    DataCloneErrorOutOfMemory,
    DataCloneErrorDetachedArrayBuffer,
    DataCloneErrorNonDetachableArrayBuffer,
    DataCloneErrorSharedArrayBufferTransferred,
    DataCloneDeserializationError,
    DataCloneDeserializationVersionError,
    TraceEventCategoryError,
    TraceEventNameError,
    TraceEventNameLengthError,
    TraceEventPhaseError,
    TraceEventIDError,
    InvalidWeakRefsUnregisterToken,
    WeakRefsCleanupMustBeCallable,
    InvalidWeakRefsRegisterTarget,
    WeakRefsRegisterTargetAndHoldingsMustNotBeSame,
    InvalidWeakRefsWeakRefConstructorTarget,
    OptionalChainingNoNew,
    OptionalChainingNoSuper,
    OptionalChainingNoTemplate,
    AllPromisesRejected,
    CannotDeepFreezeObject,
    CannotDeepFreezeValue,
    SuppressedErrorDuringDisposal,
    ExpectAnObjectWithUsing,
    MessageCount,
}

public static class MessageFormatter
{
    private static readonly string[] s_templates =
    [
        "", // None
        "Operation is ambiguous because there are more than one private name'%' on the object", // ConflictingPrivateName
        "Cyclic __proto__ value", // CyclicProto
        "Debugger: %", // Debugger
        "Error loading debugger", // DebuggerLoading
        "Internal % error. Default options are missing.", // DefaultOptionsMissing
        "Private fields can not be deleted", // DeletePrivateField
        "%", // PlaceholderOnly
        "Uncaught %", // UncaughtException
        "Not supported", // Unsupported
        "Internal error, wrong service type: %", // WrongServiceType
        "Internal error. Wrong value type.", // WrongValueType
        "Internal error. Icu error.", // IcuError
        "% was called on %, which is % and not a function", // TargetNonFunction
        "'arguments' is not allowed in class field initializer or static initialization block", // ArgumentsDisallowedInInitializerAndStaticBlock
        "% argument must be an object", // ArgumentIsNonObject
        "% argument must be a string", // ArgumentIsNonString
        "Provided key doesn't match [[ArrayBufferDetachKey]]", // ArrayBufferDetachKeyDoesntMatch
        "Derived ArrayBuffer constructor created a buffer which was too small", // ArrayBufferTooShort
        "ArrayBuffer subclass returned this from species constructor", // ArrayBufferSpeciesThis
        "await is only valid in async functions and the top level bodies of modules", // AwaitNotInAsyncContext
        "await can not be used when evaluating code while paused in the debugger", // AwaitNotInDebugEvaluate
        "Atomics.Mutex is not owned by the current agent", // AtomicsMutexNotOwnedByCurrentThread
        "% cannot be called in this context", // AtomicsOperationNotAllowed
        "RoundingType is not fractionDigits", // BadRoundingType
        "The comparison function must be either a function or undefined: %", // BadSortComparisonFunction
        "The number % cannot be converted to a BigInt because it is not an integer", // BigIntFromNumber
        "Cannot convert % to a BigInt", // BigIntFromObject
        "Cannot mix BigInt and other types, use explicit conversions", // BigIntMixedTypes
        "Do not know how to serialize a BigInt", // BigIntSerializeJSON
        "BigInts have no unsigned right shift, use >> instead", // BigIntShr
        "Cannot convert a BigInt value to a number", // BigIntToNumber
        "% is not a function", // CalledNonCallable
        "% called on non-object", // CalledOnNonObject
        "% called on null or undefined", // CalledOnNullOrUndefined
        "ShadowRealm evaluate threw (%)", // CallShadowRealmEvaluateThrew
        "CallSite expects wasm object as first or function as second argument, got <%, %>", // CallSiteExpectsFunction
        "CallSite method % expects CallSite as receiver", // CallSiteMethod
        "CallSite method % cannot access objects across ShadowRealm boundary", // CallSiteMethodCrossedShadowRealmBoundary
        "WrappedFunction threw (%)", // CallWrappedFunctionThrew
        "% cannot be shared", // CannotBeShared
        "Cannot convert object to primitive value", // CannotConvertToPrimitive
        "Cannot prevent extensions", // CannotPreventExt
        "Cannot freeze", // CannotFreeze
        "Cannot freeze array buffer views with elements", // CannotFreezeArrayBufferView
        "Cannot seal", // CannotSeal
        "Cannot seal array buffer views with elements", // CannotSealArrayBufferView
        "Cannot wrap target callable (%)", // CannotWrap
        "Converting circular structure to JSON%", // CircularStructure
        "Abstract class % not directly constructable", // ConstructAbstractClass
        "Assignment to constant variable.", // ConstAssign
        "Classes may not have a field named 'constructor'", // ConstructorClassField
        "Class constructor % cannot be invoked without 'new'", // ConstructorNonCallable
        "Class constructors cannot be invoked without 'new'", // AnonymousConstructorNonCallable
        "Constructor % requires 'new'", // ConstructorNotFunction
        "The .constructor property is not an object", // ConstructorNotReceiver
        "Currency code is required with currency style.", // CurrencyCode
        "Detected cycle while resolving name '%' in '%'", // CyclicModuleDependency
        "First argument to DataView constructor must be an ArrayBuffer", // DataViewNotArrayBuffer
        "this is not a Date object.", // DateType
        "Debugger: Invalid frame index.", // DebuggerFrame
        "Debugger: Parameters have wrong types.", // DebuggerType
        "Missing initializer in % declaration", // DeclarationMissingInitializer
        "Cannot define property %, object is not extensible", // DefineDisallowed
        "Cannot define property %, object has fixed layout", // DefineDisallowedFixedLayout
        "Cannot perform % on a detached ArrayBuffer", // TypedArrayDetachedErrorOperation
        "Cannot perform % out-of-bounds of the ArrayBuffer", // TypedArrayOOBErrorOperation
        "Cannot perform % on a detached or out-of-bounds ArrayBuffer", // TypedArrayValidateErrorOperation
        "Cannot perform % on a detached or out-of-bounds or immutable ArrayBufferCannot perform % on a detached or out-of-bounds ArrayBuffer", // TypedArrayValidateWriteErrorOperation
        "Cannot perform % on an immutable ArrayBuffer", // TypedArrayImmutableBufferErrorOperation
        "Do not use %; %", // DoNotUse
        "Object template has duplicate property '%'", // DuplicateTemplateProperty
        "Class extends value % is not a constructor or null", // ExtendsValueNotConstructor
        "First argument to % must not be a regular expression", // FirstArgumentNotRegExp
        "Bind must be called on a function", // FunctionBind
        "Generator is already running", // GeneratorRunning
        "Illegal invocation", // IllegalInvocation
        "Immutable prototype object '%' cannot have their prototype set", // ImmutablePrototypeSet
        "Import attribute has duplicate key '%'", // ImportAttributesDuplicateKey
        "Cannot use new with import", // ImportCallNotNewExpression
        "Cannot use import statement outside a module", // ImportOutsideModule
        "Cannot use 'import.meta' outside a module", // ImportMetaOutsideModule
        "import() requires a specifier", // ImportMissingSpecifier
        "Cannot import in ShadowRealm (%)", // ImportShadowRealmRejected
        "Method % called on incompatible receiver %", // IncompatibleMethodReceiver
        "Function has non-object prototype '%' in instanceof check", // InstanceofNonobjectProto
        "invalid_argument", // InvalidArgument
        "Invalid argument for Temporal %", // InvalidArgumentForTemporal
        "invalid option %", // InvalidOption
        "Cannot use 'in' operator to search for '%' in %", // InvalidInOperatorUse
        "Invalid value for JSON.rawJSON", // InvalidRawJsonValue
        "RegExp exec method returned something other than an Object or null", // InvalidRegExpExecResult
        "Invalid unit argument for %() '%'", // InvalidUnit
        "Type of '%' must be 'number', found '%'", // IsNotNumber
        "Iterable yielded % which is not a string", // IterableYieldedNonString
        "Reduce of a done iterator with no initial value", // IteratorReduceNoInitial
        "Iterator result % is not an object", // IteratorResultNotAnObject
        "% mode must be 'shortest', 'longest', or 'strict'", // InvalidIteratorZipMode
        "Iterators passed to % in 'strict' mode must have the same length", // IteratorZipStrictMismatch
        "Spread syntax requires ...iterable[Symbol.iterator] to be a function", // SpreadIteratorSymbolNonCallable
        "% requires that the property of the first argument, items[Symbol.iterator], when exists, be a function", // FirstArgumentIteratorSymbolNonCallable
        "% requires that the property of the first argument, items[Symbol.asyncIterator], when exists, be a function", // FirstArgumentAsyncIteratorSymbolNonCallable
        "Iterator value % is not an entry object", // IteratorValueNotAnObject
        "Result of the keys method is not an object", // KeysMethodInvalid
        "Language ID should be string or object.", // LanguageID
        "First argument to Intl.Locale constructor can't be empty or missing", // LocaleNotEmpty
        "Incorrect locale information provided", // LocaleBadParameters
        "Incorrect ListFormat information provided", // ListFormatBadParameters
        "flatMap mapper function is not callable", // MapperFunctionNonCallable
        "Method invoked on an object that is not %.", // MethodInvokedOnWrongType
        "no access", // NoAccess
        "Right-hand side of 'instanceof' is not callable", // NonCallableInInstanceOfCheck
        "Cannot destructure '%' as it is %.", // NonCoercible
        "Cannot destructure property '%' of '%' as it is %.", // NonCoercibleWithProperty
        "% is not extensible", // NonExtensibleProto
        "The 'with' option must be an object", // NonObjectAttributesOption
        "Right-hand side of 'instanceof' is not an object", // NonObjectInInstanceOfCheck
        "Cannot access private name % from %", // NonObjectPrivateNameAccess
        "Cannot read properties of %", // NonObjectPropertyLoad
        "Cannot read properties of % (reading '%')", // NonObjectPropertyLoadWithProperty
        "Cannot set properties of %", // NonObjectPropertyStore
        "Cannot set properties of % (setting '%')", // NonObjectPropertyStoreWithProperty
        "The second argument to import() must be an object", // NonObjectImportArgument
        "Import attribute value must be a string", // NonStringImportAttributeValue
        "Cannot set property % of % which has only a getter", // NoSetterInCallback
        "% is not an iterator", // NotAnIterator
        "Deferred module is not ready for sync execution", // NotReadyForSyncExec
        "Promise constructor cannot be invoked without 'new'", // PromiseNewTargetUndefined
        "% is not a constructor", // NotConstructor
        "this is not a Date object.", // NotDateObject
        "% requires that 'this' be a %", // NotGeneric
        "% is not a function", // NotCallable
        "% is not a function or its return value is not iterable", // NotCallableOrIterable
        "% is not a function or its return value is not async iterable", // NotCallableOrAsyncIterable
        "Value need to be finite number for %()", // NotFiniteNumber
        "% is not iterable", // NotIterable
        "% is not iterable (cannot read property %)", // NotIterableNoSymbolLoad
        "% is not async iterable", // NotAsyncIterable
        "% is not a valid property name", // NotPropertyName
        "this is not a typed array.", // NotTypedArray
        "Super constructor % of % is not a constructor", // NotSuperConstructor
        "Super constructor % of anonymous class is not a constructor", // NotSuperConstructorAnonymousClass
        "% is not an integer typed array.", // NotIntegerTypedArray
        "% is not an int32 or BigInt64 typed array.", // NotInt32OrBigInt64TypedArray
        "% is not a shared typed array.", // NotSharedTypedArray
        "Cannot add property %, object has fixed layout", // ObjectFixedLayout
        "Object.prototype.__defineGetter__: Expecting function", // ObjectGetterExpectingFunction
        "Getter must be a function: %", // ObjectGetterCallable
        "Cannot add property %, object is not extensible", // ObjectNotExtensible
        "Object.prototype.__defineSetter__: Expecting function", // ObjectSetterExpectingFunction
        "Setter must be a function: %", // ObjectSetterCallable
        "Function object that's not a constructor was created with new", // OrdinaryFunctionCalledAsConstructor
        "Chaining cycle detected for promise %", // PromiseCyclic
        "Promise executor has already been invoked with non-undefined arguments", // PromiseExecutorAlreadyInvoked
        "Promise resolve or reject function is not callable", // PromiseNonCallable
        "Property description must be an object: %", // PropertyDescObject
        "'%' returned for property '%' of object '%' is not a function", // PropertyNotFunction
        "Object prototype may only be an Object or null: %", // ProtoObjectOrNull
        "Class extends value does not have valid prototype property %", // PrototypeParentNotAnObject
        "'construct' on proxy: trap returned non-object ('%')", // ProxyConstructNonObject
        "'defineProperty' on proxy: trap returned truish for defining non-configurable property '%' which is either non-existent or configurable in the proxy target", // ProxyDefinePropertyNonConfigurable
        "'defineProperty' on proxy: trap returned truish for defining non-configurable property '%' which cannot be non-writable, unless there exists a corresponding non-configurable, non-writable own property of the target object.", // ProxyDefinePropertyNonConfigurableWritable
        "'defineProperty' on proxy: trap returned truish for adding property '%'  to the non-extensible proxy target", // ProxyDefinePropertyNonExtensible
        "'defineProperty' on proxy: trap returned truish for adding property '%'  that is incompatible with the existing property in the proxy target", // ProxyDefinePropertyIncompatible
        "'deleteProperty' on proxy: trap returned truish for property '%' which is non-configurable in the proxy target", // ProxyDeletePropertyNonConfigurable
        "'deleteProperty' on proxy: trap returned truish for property '%' but the proxy target is non-extensible", // ProxyDeletePropertyNonExtensible
        "'get' on proxy: property '%' is a read-only and non-configurable data property on the proxy target but the proxy did not return its actual value (expected '%' but got '%')", // ProxyGetNonConfigurableData
        "'get' on proxy: property '%' is a non-configurable accessor property on the proxy target and does not have a getter function, but the trap did not return 'undefined' (got '%')", // ProxyGetNonConfigurableAccessor
        "'getOwnPropertyDescriptor' on proxy: trap returned descriptor for property '%' that is incompatible with the existing property in the proxy target", // ProxyGetOwnPropertyDescriptorIncompatible
        "'getOwnPropertyDescriptor' on proxy: trap returned neither object nor undefined for property '%'", // ProxyGetOwnPropertyDescriptorInvalid
        "'getOwnPropertyDescriptor' on proxy: trap reported non-configurability for property '%' which is either non-existent or configurable in the proxy target", // ProxyGetOwnPropertyDescriptorNonConfigurable
        "'getOwnPropertyDescriptor' on proxy: trap reported non-configurable and non-writable for property '%' which is non-configurable, writable in the proxy target", // ProxyGetOwnPropertyDescriptorNonConfigurableWritable
        "'getOwnPropertyDescriptor' on proxy: trap returned undefined for property '%' which exists in the non-extensible proxy target", // ProxyGetOwnPropertyDescriptorNonExtensible
        "'getOwnPropertyDescriptor' on proxy: trap returned undefined for property '%' which is non-configurable in the proxy target", // ProxyGetOwnPropertyDescriptorUndefined
        "'getPrototypeOf' on proxy: trap returned neither object nor null", // ProxyGetPrototypeOfInvalid
        "'getPrototypeOf' on proxy: proxy target is non-extensible but the trap did not return its actual prototype", // ProxyGetPrototypeOfNonExtensible
        "'has' on proxy: trap returned falsish for property '%' which exists in the proxy target as non-configurable", // ProxyHasNonConfigurable
        "'has' on proxy: trap returned falsish for property '%' but the proxy target is not extensible", // ProxyHasNonExtensible
        "'isExtensible' on proxy: trap result does not reflect extensibility of proxy target (which is '%')", // ProxyIsExtensibleInconsistent
        "Cannot create proxy with a non-object as target or handler", // ProxyNonObject
        "'ownKeys' on proxy: trap result did not include '%'", // ProxyOwnKeysMissing
        "'ownKeys' on proxy: trap returned extra keys but proxy target is non-extensible", // ProxyOwnKeysNonExtensible
        "'ownKeys' on proxy: trap returned duplicate entries", // ProxyOwnKeysDuplicateEntries
        "'preventExtensions' on proxy: trap returned truish but the proxy target is extensible", // ProxyPreventExtensionsExtensible
        "Cannot pass private property name to proxy trap", // ProxyPrivate
        "Cannot perform '%' on a proxy that has been revoked", // ProxyRevoked
        "'set' on proxy: trap returned truish for property '%' which exists in the proxy target as a non-configurable and non-writable data property with a different value", // ProxySetFrozenData
        "'set' on proxy: trap returned truish for property '%' which exists in the proxy target as a non-configurable and non-writable accessor property without a setter", // ProxySetFrozenAccessor
        "'setPrototypeOf' on proxy: trap returned truish for setting a new prototype on the non-extensible proxy target", // ProxySetPrototypeOfNonExtensible
        "'%' on proxy: trap returned falsish", // ProxyTrapReturnedFalsish
        "'%' on proxy: trap returned falsish for property '%'", // ProxyTrapReturnedFalsishFor
        "Cannot redefine property: %", // RedefineDisallowed
        "Cannot redefine a property of an object with external array elements", // RedefineExternalArray
        "Reduce of empty array with no initial value", // ReduceNoInitial
        "Cannot supply flags when constructing one RegExp from another", // RegExpFlags
        "% getter called on non-object %", // RegExpNonObject
        "% getter called on non-RegExp object", // RegExpNonRegExp
        "% called with a non-global RegExp argument", // RegExpGlobalInvokedOnNonGlobal
        "Incorrect RelativeDateTimeFormatter provided", // RelativeDateTimeFormatterBadParameters
        "Promise resolver % is not a function", // ResolverNotAFunction
        "The iterator's 'return' method is not callable", // ReturnMethodNotCallable
        "The .size property is NaN", // SizeIsNaN
        "Error stack is not a string in ShadowRealm (%)", // ShadowRealmErrorStackNonString
        "Error stack getter threw in ShadowRealm (%)", // ShadowRealmErrorStackThrows
        "Derived SharedArrayBuffer constructor created a buffer which was too small", // SharedArrayBufferTooShort
        "SharedArrayBuffer subclass returned this from species constructor", // SharedArrayBufferSpeciesThis
        "SharedStructType registered as '%' does not match", // SharedStructTypeRegistryMismatch
        "Classes may not have a static property named 'prototype'", // StaticPrototype
        "Cannot create property '%' on % '%'", // StrictCannotCreateProperty
        "Cannot delete property '%' of %", // StrictCannotDeleteProperty
        "Cannot assign to property '%' of %", // StrictCannotSetProperty
        "'caller', 'callee', and 'arguments' properties may not be accessed on strict mode functions or the arguments objects for calls to them", // StrictPoisonPill
        "Cannot assign to read only property '%' of % '%'", // StrictReadOnlyProperty
        "The .flags property of the argument to String.prototype.matchAll cannot be null or undefined", // StringMatchAllNullOrUndefinedFlags
        "Result of the Symbol.iterator method is not an object", // SymbolIteratorInvalid
        "Result of the Symbol.asyncIterator method is not an object", // SymbolAsyncIteratorInvalid
        "% is not a symbol", // SymbolKeyFor
        "Cannot convert a Symbol value to a number", // SymbolToNumber
        "Cannot convert a Symbol value to a string", // SymbolToString
        "Temporal error: %", // Temporal
        "Temporal error: % %.", // TemporalWithArg
        "The iterator does not provide a 'throw' method.", // ThrowMethodMissing
        "Top-level await promise never resolved", // TopLevelAwaitStalled
        "Cannot convert undefined or null to object", // UndefinedOrNullToObject
        "Assignment to await using variable.", // AwaitUsingAssign
        "Assignment to using variable.", // UsingAssign
        "Invalid property descriptor. Cannot both specify accessors and a value or writable attribute, %", // ValueAndAccessor
        "Identifier '%' has already been declared", // VarRedeclaration
        "Identifier '%' cannot be declared with 'var' in current evaluation scope, consider trying 'let' instead", // VarNotAllowedInEvalScope
        "%: Arguments list has wrong type", // WrongArgs
        "% is not defined", // NotDefined
        "Super constructor may only be called once", // SuperAlreadyCalled
        "Cannot access '%' before initialization", // AccessedUninitializedVariable
        "Unsupported reference to 'super'", // UnsupportedSuper
        "Cannot access '%' from debugger", // AccessedUnavailableVariable
        "Cannot call % on an already-disposed DisposableStack", // DisposableStackIsDisposed
        "Receiver is not an AsyncDisposableStack", // NotAnAsyncDisposableStack
        "Division by zero", // BigIntDivZero
        "Maximum BigInt size exceeded", // BigIntTooBig
        "Can't set option % when % is used", // CantSetOptionXWhenYIsUsed
        "Provided date is not in valid range.", // DateRange
        "Expected letters optionally connected with underscores or hyphens for a location, got %", // ExpectedLocation
        "Invalid array buffer length", // InvalidArrayBufferLength
        "Invalid array buffer max length", // InvalidArrayBufferMaxLength
        "%: Invalid length parameter", // InvalidArrayBufferResizeLength
        "Array buffer allocation failed", // ArrayBufferAllocationFailed
        "Invalid % : %", // Invalid
        "Invalid array length", // InvalidArrayLength
        "Invalid atomic access index", // InvalidAtomicAccessIndex
        "Invalid calendar specified: %", // InvalidCalendar
        "Invalid code point %", // InvalidCodePoint
        "Invalid count value: %", // InvalidCountValue
        "Offset is outside the bounds of the DataView", // InvalidDataViewAccessorOffset
        "Invalid DataView length %", // InvalidDataViewLength
        "Start offset % is outside the bounds of the buffer", // InvalidOffset
        "Invalid hint: %", // InvalidHint
        "Invalid value: not (convertible to) a safe integer", // InvalidIndex
        "Invalid language tag: %", // InvalidLanguageTag
        "Invalid value used as weak map key", // InvalidWeakMapKey
        "Invalid value used in weak set", // InvalidWeakSetValue
        "Invalid value used as source text", // InvalidShadowRealmEvaluateSourceText
        "Invalid string length", // InvalidStringLength
        "Invalid time value", // InvalidTimeValue
        "Invalid time zone specified: %", // InvalidTimeZone
        "% of % should be a multiple of %", // InvalidTypedArrayAlignment
        "Invalid typed array index", // InvalidTypedArrayIndex
        "Invalid typed array length: %", // InvalidTypedArrayLength
        "let is disallowed as a lexically bound name", // LetInLexicalBinding
        "Illegal value for localeMatcher:%", // LocaleMatcher
        "maximumFractionDigits not equal to minimumFractionDigits", // MaximumFractionDigitsNotEqualMinimumFractionDigits
        "The normalization form should be one of %.", // NormalizationForm
        "%: Out of memory", // OutOfMemory
        "Numeric separator can not be used after leading 0.", // ZeroDigitNumericSeparator
        "% argument must be between 0 and 100", // NumberFormatRange
        "Numeric separators are not allowed at the end of numeric literals", // TrailingNumericSeparator
        "Only one underscore is allowed as numeric separator", // ContinuousNumericSeparator
        "% value is out of range.", // PropertyValueOutOfRange
        "Maximum call stack size exceeded", // StackOverflow
        "toPrecision() argument must be between 1 and 100", // ToPrecisionFormatRange
        "toString() radix argument must be between 2 and 36", // ToRadixFormatRange
        "SharedArray length out of range", // SharedArraySizeOutOfRange
        "Struct field count out of range (maximum of 999 allowed)", // StructFieldCountOutOfRange
        "offset is out of bounds", // TypedArraySetOffsetOutOfBounds
        "Source is too large", // TypedArraySetSourceTooLarge
        "Custom comparefn not supported for huge TypedArrays", // TypedArrayTooLargeToSort
        "Math.sumPrecise not supported for huge iterables", // IterableTooLargeToSum
        "Value % out of range for % options property %", // ValueOutOfRange
        "% maximum size exceeded", // CollectionGrowFailed
        "% must be positive", // MustBePositive
        "% argument must be undefined or an integer", // ArgumentIsNotUndefinedOrInteger
        "The requested module '%' contains conflicting star exports for name '%'", // AmbiguousExport
        "Getter must not have any formal parameters.", // BadGetterArity
        "Setter must have exactly one formal parameter.", // BadSetterArity
        "The base64 input terminates with non-zero padding bits.", // Base64ExtraBits
        "The base64 input terminates with a single character, excluding padding (=).", // Base64InputRemainder
        "Invalid BigInt string", // BigIntInvalidString
        "Class constructor may not be an accessor", // ConstructorIsAccessor
        "Class constructor may not be a generator", // ConstructorIsGenerator
        "Class constructor may not be an async method", // ConstructorIsAsync
        "Class constructor may not be a private method", // ConstructorIsPrivate
        "Derived constructors may only return object or undefined", // DerivedConstructorReturnedNonObject
        "A class may only have one constructor", // DuplicateConstructor
        "Duplicate export of '%'", // DuplicateExport
        "Duplicate __proto__ fields are not allowed in object literals", // DuplicateProto
        "% loop variable declaration may not have an initializer.", // ForInOfLoopInitializer
        "The left-hand side of a for-of loop may not start with 'let'.", // ForOfLet
        "The left-hand side of a for-of loop may not be 'async'.", // ForOfAsync
        "Invalid left-hand side in % loop: Must have a single binding.", // ForInOfLoopMultiBindings
        "Generators can only be declared at the top level or inside a block.", // GeneratorInSingleStatementContext
        "Async functions can only be declared at the top level or inside a block.", // AsyncFunctionInSingleStatementContext
        "Illegal break statement", // IllegalBreak
        "String literal module export names must be followed by a 'from' clause", // ModuleExportNameWithoutFromClause
        "Illegal continue statement: no surrounding iteration statement", // NoIterationStatement
        "Illegal continue statement: '%' does not denote an iteration statement", // IllegalContinue
        "Illegal '%' directive in function with non-simple parameter list", // IllegalLanguageModeDirective
        "Illegal return statement", // IllegalReturn
        "Intrinsic calls do not support spread arguments", // IntrinsicWithSpread
        "Found a character that cannot be part of a valid base64 string.", // InvalidBase64Character
        "`...` must be followed by an identifier in declaration contexts", // InvalidRestBindingPattern
        "Illegal property in declaration context", // InvalidPropertyBindingPattern
        "`...` must be followed by an assignable reference in assignment contexts", // InvalidRestAssignmentPattern
        "Keyword must not contain escaped characters", // InvalidEscapedReservedWord
        "'%' must not contain escaped characters", // InvalidEscapedMetaProperty
        "Invalid left-hand side in assignment", // InvalidLhsInAssignment
        "Invalid shorthand property initializer", // InvalidCoverInitializedName
        "Invalid destructuring assignment target", // InvalidDestructuringTarget
        "Invalid left-hand side in for-loop", // InvalidLhsInFor
        "Invalid left-hand side expression in postfix operation", // InvalidLhsInPostfixOp
        "Invalid left-hand side expression in prefix operation", // InvalidLhsInPrefixOp
        "Input string must contain hex characters in even length", // InvalidHexString
        "Invalid module export name: contains unpaired surrogate", // InvalidModuleExportName
        "Invalid flags supplied to RegExp constructor '%'", // InvalidRegExpFlags
        "Invalid or unexpected token", // InvalidOrUnexpectedToken
        "Receiver must be an instance of class %", // InvalidPrivateBrandInstance
        "Receiver must be class %", // InvalidPrivateBrandStatic
        "Cannot initialize private methods of class % twice on the same object", // InvalidPrivateBrandReinitialization
        "Cannot initialize % twice on the same object", // InvalidPrivateFieldReinitialization
        "Private field '%' must be declared in an enclosing class", // InvalidPrivateFieldResolution
        "Cannot read private member % from an object whose class did not declare it", // InvalidPrivateMemberRead
        "Cannot write private member % to an object whose class did not declare it", // InvalidPrivateMemberWrite
        "Private method '%' is not writable", // InvalidPrivateMethodWrite
        "'%' was defined without a getter", // InvalidPrivateGetterAccess
        "'%' was defined without a setter", // InvalidPrivateSetterAccess
        "'%' is an invalid size", // InvalidSizeValue
        "Unused static private method '%' cannot be accessed at debug time", // InvalidUnusedPrivateStaticMethodAccessedByDebugger
        "Invalid 'using' in for-in loop", // InvalidUsingInForInLoop
        "Unexpected end of JSON input", // JsonParseUnexpectedEOS
        "Unexpected number in JSON at position % (line % column %)", // JsonParseUnexpectedTokenNumber
        "Unexpected string in JSON at position % (line % column %)", // JsonParseUnexpectedTokenString
        "Unterminated string in JSON at position % (line % column %)", // JsonParseUnterminatedString
        "Expected property name or '}' in JSON at position % (line % column %)", // JsonParseExpectedPropNameOrRBrace
        "Expected ',' or ']' after array element in JSON at position % (line % column %)", // JsonParseExpectedCommaOrRBrack
        "Expected ',' or '}' after property value in JSON at position % (line % column %)", // JsonParseExpectedCommaOrRBrace
        "Expected double-quoted property name in JSON at position % (line % column %)", // JsonParseExpectedDoubleQuotedPropertyName
        "Exponent part is missing a number in JSON at position % (line % column %)", // JsonParseExponentPartMissingNumber
        "Expected ':' after property name in JSON at position % (line % column %)", // JsonParseExpectedColonAfterPropertyName
        "Unterminated fractional number in JSON at position % (line % column %)", // JsonParseUnterminatedFractionalNumber
        "Unexpected non-whitespace character after JSON at position % (line % column %)", // JsonParseUnexpectedNonWhiteSpaceCharacter
        "Bad escaped character in JSON at position % (line % column %)", // JsonParseBadEscapedCharacter
        "Bad control character in string literal in JSON at position % (line % column %)", // JsonParseBadControlCharacter
        "Bad Unicode escape in JSON at position % (line % column %)", // JsonParseBadUnicodeEscape
        "No number after minus sign in JSON at position % (line % column %)", // JsonParseNoNumberAfterMinusSign
        "\"%\" is not valid JSON", // JsonParseShortString
        "Unexpected token '%', \"%\" is not valid JSON", // JsonParseUnexpectedTokenShortString
        "Unexpected token '%', ...\"%\"... is not valid JSON", // JsonParseUnexpectedTokenSurroundStringWithContext
        "Unexpected token '%', ...\"%\" is not valid JSON", // JsonParseUnexpectedTokenEndStringWithContext
        "Unexpected token '%', \"%\"... is not valid JSON", // JsonParseUnexpectedTokenStartStringWithContext
        "Label '%' has already been declared", // LabelRedeclaration
        "Labelled function declaration not allowed as the body of a control flow structure", // LabelledFunctionDeclaration
        "Malformed arrow function parameter list", // MalformedArrowFunParamList
        "Invalid regular expression: /%/%: %", // MalformedRegExp
        "Invalid regular expression flags", // MalformedRegExpFlags
        "Export '%' is not defined in module", // ModuleExportUndefined
        "Function statements require a function name", // MissingFunctionName
        "Mismatched calendars.", // MismatchedCalendars
        "HTML comments are not allowed in modules", // HtmlCommentInModule
        "More than one default clause in switch statement", // MultipleDefaultsInSwitch
        "Illegal newline after throw", // NewlineAfterThrow
        "Missing catch or finally after try", // NoCatchOrFinally
        "Rest parameter must be last formal parameter", // ParamAfterRest
        "Flattening % elements on an array-like of length % is disallowed, as the total surpasses 2**53-1", // FlattenPastSafeLength
        "Pushing % elements on an array-like of length % is disallowed, as the total surpasses 2**53-1", // PushPastSafeLength
        "Rest element must be last element", // ElementAfterRest
        "Setter function argument must not be a rest parameter", // BadSetterRestParameter
        "Duplicate parameter name not allowed in this context", // ParamDupe
        "Arg string terminates parameters early", // ArgStringTerminatesParametersEarly
        "Unexpected end of arg string", // UnexpectedEndOfArgString
        "Rest parameter may not have a default initializer", // RestDefaultInitializer
        "Runtime function given wrong number of arguments", // RuntimeWrongNumArgs
        "Must call super constructor in derived class before accessing 'this' or returning from derived constructor", // SuperNotCalled
        "Single function literal required", // SingleFunctionLiteral
        "In non-strict mode code, functions can only be declared at top level, inside a block, or as the body of an if statement.", // SloppyFunction
        "object.constructor[Symbol.species] is not a constructor", // SpeciesNotConstructor
        "Delete of an unqualified identifier in strict mode.", // StrictDelete
        "Unexpected eval or arguments in strict mode", // StrictEvalArguments
        "In strict mode code, functions can only be declared at top level or inside a block.", // StrictFunction
        "Octal literals are not allowed in strict mode.", // StrictOctalLiteral
        "Decimals with leading zeros are not allowed in strict mode.", // StrictDecimalWithLeadingZero
        "Octal escape sequences are not allowed in strict mode.", // StrictOctalEscape
        "\\8 and \\9 are not allowed in strict mode.", // Strict8Or9Escape
        "Strict mode code may not include a with statement", // StrictWith
        "Octal escape sequences are not allowed in template strings.", // TemplateOctalLiteral
        "\\8 and \\9 are not allowed in template strings.", // Template8Or9Escape
        "'this' is not a valid formal parameter name", // ThisFormalParameter
        "'await' is not a valid identifier name in an async function", // AwaitBindingIdentifier
        "Illegal await-expression in formal parameters of async function", // AwaitExpressionFormalParameter
        "Too many arguments in function call (only 65525 allowed)", // TooManyArguments
        "Too many parameters in function definition (only 65534 allowed)", // TooManyParameters
        "Too many properties to enumerate", // TooManyProperties
        "Literal containing too many nested spreads (up to 65534 allowed)", // TooManySpreads
        "Too many variables declared (only 4194303 allowed)", // TooManyVariables
        "Too many eval calls in script", // TooManyEvals
        "Too many elements passed to Promise.%", // TooManyElementsInPromiseCombinator
        "Derived TypedArray constructor created an array which was too small", // TypedArrayTooShort
        "Unexpected end of input", // UnexpectedEOS
        "Unexpected private field", // UnexpectedPrivateField
        "Unexpected reserved word", // UnexpectedReserved
        "Unexpected strict mode reserved word", // UnexpectedStrictReserved
        "'super' keyword unexpected here", // UnexpectedSuper
        "new.target expression is not allowed here", // UnexpectedNewTarget
        "Unexpected template string", // UnexpectedTemplateString
        "Unexpected token '%'", // UnexpectedToken
        "Unary operator used immediately before exponentiation expression. Parenthesis must be used to disambiguate operator precedence", // UnexpectedTokenUnaryExponentiation
        "Unexpected identifier '%'", // UnexpectedTokenIdentifier
        "Unexpected number", // UnexpectedTokenNumber
        "Unexpected string", // UnexpectedTokenString
        "Unexpected regular expression", // UnexpectedTokenRegExp
        "Lexical declaration cannot appear in a single-statement context", // UnexpectedLexicalDeclaration
        "Undefined label '%'", // UnknownLabel
        "The requested module '%' does not provide an export named '%'", // UnresolvableExport
        "missing ) after argument list", // UnterminatedArgList
        "Invalid regular expression: missing /", // UnterminatedRegExp
        "Unterminated template literal", // UnterminatedTemplate
        "Missing } in template expression", // UnterminatedTemplateExpr
        "Found non-callable @@hasInstance", // FoundNonCallableHasInstance
        "Invalid hexadecimal escape sequence", // InvalidHexEscapeSequence
        "Invalid Unicode escape sequence", // InvalidUnicodeEscapeSequence
        "Undefined Unicode code-point", // UndefinedUnicodeCodePoint
        "Yield expression not allowed in formal parameter", // YieldInParameter
        "%", // CodeGenFromStrings
        "Possible side-effect in debug-evaluate", // NoSideEffectDebugEvaluate
        "URI malformed", // URIMalformed
        "unreachable", // WasmTrapUnreachable
        "memory access out of bounds", // WasmTrapMemOutOfBounds
        "operation does not support unaligned accesses", // WasmTrapUnalignedAccess
        "divide by zero", // WasmTrapDivByZero
        "divide result unrepresentable", // WasmTrapDivUnrepresentable
        "remainder by zero", // WasmTrapRemByZero
        "float unrepresentable in integer range", // WasmTrapFloatUnrepresentable
        "table index is out of bounds", // WasmTrapTableOutOfBounds
        "null function", // WasmTrapNullFunc
        "function signature mismatch", // WasmTrapFuncSigMismatch
        "multi-return length mismatch", // WasmTrapMultiReturnLengthMismatch
        "type incompatibility when transforming from/to JS", // WasmTrapJSTypeError
        "data segment out of bounds", // WasmTrapDataSegmentOutOfBounds
        "element segment out of bounds", // WasmTrapElementSegmentOutOfBounds
        "rethrowing null value", // WasmTrapRethrowNull
        "dereferencing a null pointer", // WasmTrapNullDereference
        "illegal cast", // WasmTrapIllegalCast
        "array element access out of bounds", // WasmTrapArrayOutOfBounds
        "requested new array is too large", // WasmTrapArrayTooLarge
        "invalid UTF-8 string", // WasmTrapStringInvalidUtf8
        "invalid WTF-8 string", // WasmTrapStringInvalidWtf8
        "string offset out of bounds", // WasmTrapStringOffsetOutOfBounds
        "WasmFX: resuming an invalid continuation", // WasmTrapResume
        "WasmFX: unhandled suspend", // WasmTrapSuspend
        "WasmFX: switching from central stack", // WasmTrapSwitch
        "trying to suspend without WebAssembly.promising", // WasmSuspendError
        "WasmFX: unhandled suspend", // WasmFXSuspendError
        "Failed to encode string as UTF-8: contains unpaired surrogate", // WasmTrapStringIsolatedSurrogate
        "trying to suspend JS frames", // WasmSuspendJSFrames
        "wasm exception", // WasmExceptionError
        "WebAssembly objects are opaque", // WasmObjectsAreOpaque
        "% could not be cloned.", // DataCloneError
        "Data cannot be cloned, out of memory.", // DataCloneErrorOutOfMemory
        "An ArrayBuffer is detached and could not be cloned.", // DataCloneErrorDetachedArrayBuffer
        "ArrayBuffer is not detachable and could not be cloned.", // DataCloneErrorNonDetachableArrayBuffer
        "A SharedArrayBuffer could not be cloned. SharedArrayBuffer must not be transferred.", // DataCloneErrorSharedArrayBufferTransferred
        "Unable to deserialize cloned data.", // DataCloneDeserializationError
        "Unable to deserialize cloned data due to invalid or unsupported version.", // DataCloneDeserializationVersionError
        "Trace event category must be a string.", // TraceEventCategoryError
        "Trace event name must be a string.", // TraceEventNameError
        "Trace event name must not be an empty string.", // TraceEventNameLengthError
        "Trace event phase must be a number.", // TraceEventPhaseError
        "Trace event id must be a number.", // TraceEventIDError
        "Invalid unregisterToken ('%')", // InvalidWeakRefsUnregisterToken
        "FinalizationRegistry: cleanup must be callable", // WeakRefsCleanupMustBeCallable
        "FinalizationRegistry.prototype.register: invalid target", // InvalidWeakRefsRegisterTarget
        "FinalizationRegistry.prototype.register: target and holdings must not be same", // WeakRefsRegisterTargetAndHoldingsMustNotBeSame
        "WeakRef: invalid target", // InvalidWeakRefsWeakRefConstructorTarget
        "Invalid optional chain from new expression", // OptionalChainingNoNew
        "Invalid optional chain from super property", // OptionalChainingNoSuper
        "Invalid tagged template on optional chain", // OptionalChainingNoTemplate
        "All promises were rejected", // AllPromisesRejected
        "Cannot DeepFreeze object of type %", // CannotDeepFreezeObject
        "Cannot DeepFreeze non-const value %", // CannotDeepFreezeValue
        "An error was suppressed during disposal", // SuppressedErrorDuringDisposal
        "An object is expected with `using` declarations", // ExpectAnObjectWithUsing
    ];

    public static MessageTemplate MessageTemplateFromInt(int messageId)
    {
        System.Diagnostics.Debug.Assert((uint)messageId < (uint)MessageTemplate.MessageCount);
        return (MessageTemplate)messageId;
    }

    public static string TemplateString(MessageTemplate index)
    {
        if ((uint)index >= (uint)MessageTemplate.MessageCount) throw new ArgumentOutOfRangeException(nameof(index));
        return s_templates[(int)index];
    }

    // TODO(14386): Get this list empty.
    private static bool IsTemplateWithMismatchedArguments(MessageTemplate index)
    {
        switch (index)
        {
            case MessageTemplate.ConstAssign:
            case MessageTemplate.ConstructorNotReceiver:
            case MessageTemplate.DataCloneErrorDetachedArrayBuffer:
            case MessageTemplate.DataCloneErrorOutOfMemory:
            case MessageTemplate.IncompatibleMethodReceiver:
            case MessageTemplate.InvalidArgument:
            case MessageTemplate.InvalidArrayLength:
            case MessageTemplate.InvalidAtomicAccessIndex:
            case MessageTemplate.InvalidDataViewLength:
            case MessageTemplate.InvalidIndex:
            case MessageTemplate.InvalidLhsInAssignment:
            case MessageTemplate.InvalidLhsInFor:
            case MessageTemplate.InvalidLhsInPostfixOp:
            case MessageTemplate.InvalidLhsInPrefixOp:
            case MessageTemplate.InvalidPrivateBrandReinitialization:
            case MessageTemplate.InvalidPrivateFieldReinitialization:
            case MessageTemplate.InvalidPrivateMemberWrite:
            case MessageTemplate.InvalidRegExpExecResult:
            case MessageTemplate.InvalidTimeValue:
            case MessageTemplate.InvalidWeakMapKey:
            case MessageTemplate.InvalidWeakSetValue:
            case MessageTemplate.IteratorReduceNoInitial:
            case MessageTemplate.JsonParseShortString:
            case MessageTemplate.JsonParseUnexpectedEOS:
            case MessageTemplate.JsonParseUnexpectedTokenEndStringWithContext:
            case MessageTemplate.JsonParseUnexpectedTokenShortString:
            case MessageTemplate.JsonParseUnexpectedTokenStartStringWithContext:
            case MessageTemplate.JsonParseUnexpectedTokenSurroundStringWithContext:
            case MessageTemplate.MustBePositive:
            case MessageTemplate.NotIterable:
            case MessageTemplate.NotTypedArray:
            case MessageTemplate.ProxyNonObject:
            case MessageTemplate.ProxyPrivate:
            case MessageTemplate.ProxyRevoked:
            case MessageTemplate.ProxyTrapReturnedFalsishFor:
            case MessageTemplate.ReduceNoInitial:
            case MessageTemplate.SpreadIteratorSymbolNonCallable:
            case MessageTemplate.SymbolIteratorInvalid:
            case MessageTemplate.TopLevelAwaitStalled:
            case MessageTemplate.UndefinedOrNullToObject:
            case MessageTemplate.UnexpectedStrictReserved:
            case MessageTemplate.UnexpectedTokenIdentifier:
            case MessageTemplate.WeakRefsCleanupMustBeCallable:
                return true;
            default:
                return false;
        }
    }

    // MessageFormatter::TryFormat: each '%' takes the next argument, '%%' is a
    // literal '%'. Missing arguments print "undefined" for the templates V8
    // lists as mismatched, and are a fatal error otherwise.
    public static string Format(MessageTemplate index, ReadOnlySpan<string> args)
    {
        string template = TemplateString(index);
        var builder = new System.Text.StringBuilder(template.Length + 16);
        int next = 0;
        for (int c = 0; c < template.Length; c++)
        {
            char ch = template[c];
            if (ch == '%')
            {
                if (c + 1 < template.Length && template[c + 1] == '%')
                {
                    c++;
                    builder.Append('%');
                }
                else if (next >= args.Length)
                {
                    if (!IsTemplateWithMismatchedArguments(index))
                        throw new InvalidOperationException($"Missing argument to template (got {args.Length}): {template}");
                    builder.Append("undefined");
                }
                else
                {
                    builder.Append(args[next++]);
                }
            }
            else
            {
                builder.Append(ch);
            }
        }
        if (next < args.Length && !IsTemplateWithMismatchedArguments(index))
            throw new InvalidOperationException($"Too many arguments to template (expected {next}, got {args.Length}): {template}");
        return builder.ToString();
    }
}
