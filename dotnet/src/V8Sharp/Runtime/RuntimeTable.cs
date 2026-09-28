// The runtime function table: V8's Runtime::FunctionForId(id)->entry, i.e.
// the RUNTIME_FUNCTION implementations bound to their FunctionId
// (src/runtime/runtime.cc). CallRuntime and CallRuntimeForPair dispatch
// through it; inline intrinsics (%_Foo) that are not interpreter intrinsics
// use the entry of Foo, as in V8.
//
// Each area registers its functions in Register<Area>() below; a function
// without an implementation throws when called.
using V8Sharp.Codegen;

namespace V8Sharp.Runtime;

/// <summary>A runtime function implementation: arguments in, result out.</summary>
public delegate JSValue RuntimeFunctionImpl(Isolate isolate, ReadOnlySpan<JSValue> args);

/// <summary>A runtime function that returns a pair (RUNTIME_FUNCTION_RETURN_PAIR).</summary>
public delegate JSValue RuntimeFunctionPairImpl(Isolate isolate, ReadOnlySpan<JSValue> args, out JSValue second);

public static partial class RuntimeTable
{
    static readonly RuntimeFunctionImpl?[] s_table = new RuntimeFunctionImpl?[(int)FunctionId.NumFunctions];
    static readonly RuntimeFunctionPairImpl?[] s_pairTable = new RuntimeFunctionPairImpl?[(int)FunctionId.NumFunctions];

    static RuntimeTable()
    {
        RegisterScopes();
        RegisterObject();
        RegisterClasses();
        RegisterInternal();
        RegisterCompiler();
        RegisterTest();
        RegisterPromiseCollectionsAndWeakRefs();
        RegisterIntrinsics();
        RegisterTypedArray();

        // %_Foo uses Foo's entry when it is not an interpreter intrinsic.
        ReadOnlySpan<RuntimeFunction> all = Runtime.AllFunctions;
        for (int i = 0; i < all.Length; i++)
        {
            RuntimeFunction f = all[i];
            if (f.intrinsic_type != IntrinsicType.INLINE) continue;
            RuntimeFunction? plain = Runtime.FunctionForName(f.name.AsSpan(1));
            if (plain is null) continue;
            s_table[(int)f.function_id] ??= s_table[(int)plain.function_id];
            s_pairTable[(int)f.function_id] ??= s_pairTable[(int)plain.function_id];
        }
    }

    public static void Register(FunctionId id, RuntimeFunctionImpl impl) => s_table[(int)id] = impl;

    public static void RegisterPair(FunctionId id, RuntimeFunctionPairImpl impl) => s_pairTable[(int)id] = impl;

    public static bool IsImplemented(FunctionId id) => s_table[(int)id] is not null || s_pairTable[(int)id] is not null;

    /// <summary>CallRuntime.</summary>
    public static JSValue Call(Isolate isolate, FunctionId id, ReadOnlySpan<JSValue> args)
    {
        RuntimeFunctionImpl? impl = s_table[(int)id];
        if (impl is null) return NotImplemented(id);
        return impl(isolate, args);
    }

    /// <summary>CallRuntimeForPair.</summary>
    public static JSValue CallForPair(Isolate isolate, FunctionId id, ReadOnlySpan<JSValue> args, out JSValue second)
    {
        RuntimeFunctionPairImpl? impl = s_pairTable[(int)id];
        if (impl is null)
        {
            second = default;
            return NotImplemented(id);
        }
        return impl(isolate, args, out second);
    }

    static JSValue NotImplemented(FunctionId id) =>
        throw new NotSupportedException("V8Sharp: runtime function %" + Runtime.FunctionForId(id).name + " is not implemented");

    // ---- Registration ---------------------------------------------------------------------

    static void RegisterScopes()
    {
        Register(FunctionId.DeclareGlobals, static (i, a) => RuntimeScopes.DeclareGlobals(i, a[0], a[1]));
        Register(FunctionId.DeclareEvalVar, static (i, a) => RuntimeScopes.DeclareEvalHelper(i, a[0].As<JSString>(), JSValue.Undefined));
        Register(FunctionId.DeclareEvalFunction, static (i, a) => RuntimeScopes.DeclareEvalHelper(i, a[0].As<JSString>(), a[1]));
        Register(FunctionId.DeleteLookupSlot, static (i, a) => RuntimeScopes.DeleteLookupSlot(i, i.Context!, a[0].As<JSString>()));
        Register(FunctionId.LoadLookupSlot,
            static (i, a) => RuntimeScopes.LoadLookupSlot(i, i.Context!, a[0].As<JSString>(), ShouldThrow.ThrowOnError));
        Register(FunctionId.LoadLookupSlotInsideTypeof,
            static (i, a) => RuntimeScopes.LoadLookupSlot(i, i.Context!, a[0].As<JSString>(), ShouldThrow.DontThrow));
        RegisterPair(FunctionId.LoadLookupSlotForCall,
            static (Isolate i, ReadOnlySpan<JSValue> a, out JSValue receiver) =>
                RuntimeScopes.LoadLookupSlot(i, i.Context!, a[0].As<JSString>(), ShouldThrow.ThrowOnError, out receiver));
        Register(FunctionId.StoreLookupSlot_Sloppy,
            static (i, a) => RuntimeScopes.StoreLookupSlot(i, i.Context!, a[0].As<JSString>(), a[1], LanguageMode.Sloppy, false));
        Register(FunctionId.StoreLookupSlot_Strict,
            static (i, a) => RuntimeScopes.StoreLookupSlot(i, i.Context!, a[0].As<JSString>(), a[1], LanguageMode.Strict, false));
        Register(FunctionId.StoreLookupSlot_SloppyHoisting,
            static (i, a) => RuntimeScopes.StoreLookupSlot(i, i.Context!, a[0].As<JSString>(), a[1], LanguageMode.Sloppy, true));
        Register(FunctionId.StoreGlobalNoHoleCheckForReplLetOrConst,
            static (i, a) => RuntimeScopes.StoreGlobalNoHoleCheckForReplLetOrConst(i, a[0].As<JSString>(), a[1]));
        Register(FunctionId.NewFunctionContext,
            static (i, a) => RuntimeScopes.NewFunctionContext(i, i.Context!, a[0].As<ScopeInfo>(), false));
        Register(FunctionId.PushWithContext,
            static (i, a) => RuntimeScopes.PushWithContext(i, i.Context!, a[0], a[1].As<ScopeInfo>()));
        Register(FunctionId.PushCatchContext,
            static (i, a) => i.Factory.NewCatchContext(i.Context!, a[1].As<ScopeInfo>(), a[0]));
        Register(FunctionId.PushBlockContext, static (i, a) => i.Factory.NewBlockContext(i.Context!, a[0].As<ScopeInfo>()));
        Register(FunctionId.NewClosure,
            static (i, a) => RuntimeClosures.NewClosure(i, a[0].As<SharedFunctionInfo>(), i.Context!, a[1].As<FeedbackCell>()));
        Register(FunctionId.NewClosure_Tenured,
            static (i, a) => RuntimeClosures.NewClosure(i, a[0].As<SharedFunctionInfo>(), i.Context!, a[1].As<FeedbackCell>()));
        Register(FunctionId.ThrowConstAssignError, static (i, a) => RuntimeScopes.ThrowConstAssignError(i));
        Register(FunctionId.ThrowUsingAssignError, static (i, a) => RuntimeInternal.ThrowUsingAssignError(i));
        Register(FunctionId.ThrowAwaitUsingAssignError, static (i, a) => RuntimeInternal.ThrowAwaitUsingAssignError(i));
    }

    static void RegisterObject()
    {
        Register(FunctionId.GetProperty, static (i, a) => RuntimeObject.GetObjectProperty(i, a[0], a[1], a.Length > 2 ? a[2] : a[0], out _));
        Register(FunctionId.SetKeyedProperty,
            static (i, a) => RuntimeObject.SetObjectProperty(i, a[0], a[1], a[2], StoreOrigin.MaybeKeyed));
        Register(FunctionId.SetNamedProperty,
            static (i, a) => RuntimeObject.SetObjectProperty(i, a[0], a[1], a[2], StoreOrigin.Named));
        Register(FunctionId.DefineObjectOwnProperty,
            static (i, a) => RuntimeObject.DefineObjectOwnProperty(i, a[0], a[1], a[2], StoreOrigin.Named));
        Register(FunctionId.DeleteProperty,
            static (i, a) => RuntimeObject.DeleteProperty(i, a[0], a[1], (LanguageMode)(int)a[2].Number));
        Register(FunctionId.HasProperty, static (i, a) => RuntimeObject.HasProperty(i, a[1], a[0]));
        Register(FunctionId.SetFunctionName, static (i, a) => RuntimeObject.SetFunctionName(i, a[0], a[1]));
        Register(FunctionId.InternalSetPrototype, static (i, a) => RuntimeObject.InternalSetPrototype(i, a[0], a[1]));
        Register(FunctionId.DefineAccessorPropertyUnchecked,
            static (i, a) => RuntimeObject.DefineAccessorPropertyUnchecked(i, a[0], a[1], a[2], a[3], a[4]));
        Register(FunctionId.DefineGetterPropertyUnchecked,
            static (i, a) => RuntimeObject.DefineAccessorComponentUnchecked(i, a[0], a[1], a[2], a[3], isGetter: true));
        Register(FunctionId.DefineSetterPropertyUnchecked,
            static (i, a) => RuntimeObject.DefineAccessorComponentUnchecked(i, a[0], a[1], a[2], a[3], isGetter: false));
        Register(FunctionId.DefineKeyedOwnPropertyInLiteral,
            static (i, a) => RuntimeObject.DefineKeyedOwnPropertyInLiteral(i, a[0], a[1], a[2],
                (Interpreter.DefineKeyedOwnPropertyInLiteralFlags)(int)a[3].Number, a[4].HeapObjectOrNull as FeedbackVector,
                (int)a[5].Number));
        Register(FunctionId.AddPrivateBrand, static (i, a) => RuntimeObject.AddPrivateBrand(i, a[0], a[1], a[2], a[3]));
        Register(FunctionId.GetPrivateMember, static (i, a) => RuntimeObject.GetPrivateMember(i, a[0], a[1]));
        Register(FunctionId.SetPrivateMember, static (i, a) => RuntimeObject.SetPrivateMember(i, a[0], a[1], a[2]));
        Register(FunctionId.LoadPrivateGetter, static (i, a) => RuntimeObject.LoadPrivateAccessorComponent(a[0], getter: true));
        Register(FunctionId.LoadPrivateSetter, static (i, a) => RuntimeObject.LoadPrivateAccessorComponent(a[0], getter: false));
        Register(FunctionId.CreatePrivateAccessors, static (i, a) => RuntimeObject.CreatePrivateAccessors(i, a[0], a[1]));
        Register(FunctionId.CopyDataProperties, static (i, a) => RuntimeObject.CopyDataProperties(i, a[0], a[1], useSet: false));
        Register(FunctionId.SetDataProperties, static (i, a) => RuntimeObject.CopyDataProperties(i, a[0], a[1], useSet: true));
        Register(FunctionId.CopyDataPropertiesWithExcludedPropertiesOnStack,
            static (i, a) => RuntimeObject.CopyDataPropertiesWithExcludedProperties(i, a[0], a[1..]));
        Register(FunctionId.CreateIterResultObject, static (i, a) => RuntimeObject.CreateIterResultObject(i, a[0], a[1]));
        Register(FunctionId.ToObject, static (i, a) => ObjectOps.ToObject(i, a[0]));
        Register(FunctionId.ToNumber, static (i, a) => ObjectOps.ToNumber(i, a[0]));
        Register(FunctionId.ToNumeric, static (i, a) => ObjectOps.ToNumeric(i, a[0]));
        Register(FunctionId.ToLength, static (i, a) => ObjectOps.ToLength(i, a[0]));
        Register(FunctionId.ToString, static (i, a) => ObjectOps.ToString(i, a[0]));
        Register(FunctionId.ToName, static (i, a) => ObjectOps.ToName(i, a[0]));
        Register(FunctionId.HasFastPackedElements,
            static (i, a) => JSValue.FromBoolean(a[0].HeapObjectOrNull is JSObject o &&
                                                 ElementsKinds.IsFastPackedElementsKind(o.GetElementsKind())));
        Register(FunctionId.IsJSReceiver, static (i, a) => JSValue.FromBoolean(a[0].IsJSReceiver));
        Register(FunctionId.HasInPrototypeChain,
            static (i, a) => JSValue.FromBoolean(a[0].HeapObjectOrNull is JSReceiver r && JSReceiver.HasInPrototypeChain(i, r, a[1])));
        Register(FunctionId.ToFastProperties, static (i, a) => RuntimeTest.ToFastProperties(i, a[0]));
        Register(FunctionId.ObjectKeys, static (i, a) => i.Factory.NewJSArrayWithElements(
            KeyAccumulator.GetKeys(i, ObjectOps.ToObject(i, a[0]), KeyCollectionMode.OwnOnly, PropertyFilter.ENUMERABLE_STRINGS,
                GetKeysConversion.ConvertToString)));
    }

    static void RegisterClasses()
    {
        Register(FunctionId.DefineClass, static (i, a) => RuntimeClasses.DefineClass(i, a));
        Register(FunctionId.LoadFromSuper, static (i, a) => RuntimeClasses.LoadFromSuper(i, a[0], a[1], a[2]));
        Register(FunctionId.LoadKeyedFromSuper, static (i, a) => RuntimeClasses.LoadFromSuper(i, a[0], a[1], a[2]));
        Register(FunctionId.StoreToSuper, static (i, a) => RuntimeClasses.StoreToSuper(i, a[0], a[1], a[2], a[3], StoreOrigin.Named));
        Register(FunctionId.StoreKeyedToSuper,
            static (i, a) => RuntimeClasses.StoreToSuper(i, a[0], a[1], a[2], a[3], StoreOrigin.MaybeKeyed));
        Register(FunctionId.ThrowUnsupportedSuperError, static (i, a) => RuntimeClasses.ThrowUnsupportedSuperError(i));
        Register(FunctionId.ThrowConstructorNonCallableError,
            static (i, a) => RuntimeClasses.ThrowConstructorNonCallableError(i, a[0].As<JSFunction>()));
        Register(FunctionId.ThrowStaticPrototypeError, static (i, a) => RuntimeClasses.ThrowStaticPrototypeError(i));
        Register(FunctionId.ThrowSuperAlreadyCalledError, static (i, a) => RuntimeClasses.ThrowSuperAlreadyCalledError(i));
        Register(FunctionId.ThrowSuperNotCalled, static (i, a) => RuntimeClasses.ThrowSuperNotCalled(i));
        Register(FunctionId.ThrowNotSuperConstructor,
            static (i, a) => RuntimeClasses.ThrowNotSuperConstructor(i, a[0], a[1].As<JSFunction>()));
    }

    static void RegisterInternal()
    {
        Register(FunctionId.NewTypeError, static (i, a) => RuntimeInternal.NewTypeError(i, a));
        Register(FunctionId.NewReferenceError, static (i, a) => RuntimeInternal.NewReferenceError(i, a));
        Register(FunctionId.NewError, static (i, a) => RuntimeInternal.NewPlainError(i, a));
        Register(FunctionId.ThrowTypeError, static (i, a) => RuntimeInternal.ThrowTypeError(i, a));
        Register(FunctionId.ThrowRangeError, static (i, a) => RuntimeInternal.ThrowRangeError(i, a));
        Register(FunctionId.ThrowReferenceError, static (i, a) => RuntimeInternal.ThrowReferenceError(i, a[0]));
        Register(FunctionId.ThrowIteratorResultNotAnObject, static (i, a) => RuntimeInternal.ThrowIteratorResultNotAnObject(i, a[0]));
        Register(FunctionId.ThrowThrowMethodMissing, static (i, a) => RuntimeInternal.ThrowThrowMethodMissing(i));
        Register(FunctionId.ThrowSymbolIteratorInvalid, static (i, a) => RuntimeInternal.ThrowSymbolIteratorInvalid(i));
        Register(FunctionId.ThrowSymbolAsyncIteratorInvalid, static (i, a) => RuntimeInternal.ThrowSymbolAsyncIteratorInvalid(i));
        Register(FunctionId.ThrowPatternAssignmentNonCoercible,
            static (i, a) => RuntimeInternal.ThrowPatternAssignmentNonCoercible(i, a[0]));
        Register(FunctionId.ThrowConstructorReturnedNonObject, static (i, a) => RuntimeInternal.ThrowConstructorReturnedNonObject(i));
        Register(FunctionId.ThrowCalledNonCallable, static (i, a) => RuntimeInternal.ThrowCalledNonCallable(i, a[0]));
        Register(FunctionId.ThrowConstructedNonConstructable,
            static (i, a) => RuntimeInternal.ThrowConstructedNonConstructable(i, a[0]));
        Register(FunctionId.ThrowIteratorError, static (i, a) => RuntimeInternal.ThrowIteratorError(i, a[0]));
        Register(FunctionId.ThrowSpreadArgError, static (i, a) => RuntimeInternal.ThrowSpreadArgError(i, a[0], a[1]));
        Register(FunctionId.Throw, static (i, a) => i.Throw(a[0]));
        Register(FunctionId.ReThrow, static (i, a) => i.ReThrow(a[0]));
        Register(FunctionId.CreatePrivateNameSymbol, static (i, a) => RuntimeInternal.CreatePrivateNameSymbol(i, a[0]));
        Register(FunctionId.CreatePrivateBrandSymbol, static (i, a) => RuntimeInternal.CreatePrivateBrandSymbol(i, a[0]));
        Register(FunctionId.AbortJS, static (i, a) => RuntimeInternal.AbortJS(i, a[0]));
        Register(FunctionId.TraceEnter, static (i, a) => JSValue.Undefined);
        Register(FunctionId.TraceExit, static (i, a) => a[0]);
        Register(FunctionId.StackGuard, static (i, a) =>
        {
            i.StackGuard.StackCheck(i);
            return JSValue.Undefined;
        });
        Register(FunctionId.TerminateExecution, static (i, a) => i.TerminateExecution());
        Register(FunctionId.PerformMicrotaskCheckpoint, static (i, a) =>
        {
            Execution.PerformMicrotaskCheckpoint(i);
            return JSValue.Undefined;
        });
        Register(FunctionId.CreateListFromArrayLike, static (i, a) =>
            ObjectOps.CreateListFromArrayLike(i, a[0], ElementTypes.All));
    }

    static void RegisterCompiler()
    {
        Register(FunctionId.ResolvePossiblyDirectEval, static (i, a) => Compiler.ResolvePossiblyDirectEval(i, a));
    }

    static void RegisterTest()
    {
        Register(FunctionId.GetOptimizationStatus, static (i, a) => RuntimeTest.GetOptimizationStatus(i, a.Length > 0 ? a[0] : default));
        Register(FunctionId.PrepareFunctionForOptimization, static (i, a) => RuntimeTest.EnsureFeedbackVector(i, a[0]));
        Register(FunctionId.EnsureFeedbackVectorForFunction, static (i, a) => RuntimeTest.EnsureFeedbackVector(i, a[0]));
        Register(FunctionId.OptimizeFunctionOnNextCall, RuntimeTest.ReturnUndefined);
        Register(FunctionId.OptimizeMaglevOnNextCall, RuntimeTest.ReturnUndefined);
        Register(FunctionId.OptimizeOsr, RuntimeTest.ReturnUndefined);
        Register(FunctionId.NeverOptimizeFunction, RuntimeTest.ReturnUndefined);
        Register(FunctionId.DeoptimizeFunction, RuntimeTest.ReturnUndefined);
        Register(FunctionId.DeoptimizeNow, RuntimeTest.ReturnUndefined);
        Register(FunctionId.CompileBaseline, RuntimeTest.ReturnUndefined);
        Register(FunctionId.WaitForBackgroundOptimization, RuntimeTest.ReturnUndefined);
        Register(FunctionId.FinalizeOptimization, RuntimeTest.ReturnUndefined);
        Register(FunctionId.SetAllocationTimeout, RuntimeTest.ReturnUndefined);
        Register(FunctionId.NotifyContextDisposed, RuntimeTest.ReturnUndefined);
        Register(FunctionId.SimulateNewspaceFull, RuntimeTest.ReturnUndefined);
        Register(FunctionId.TurbofanStaticAssert, RuntimeTest.ReturnUndefined);
        Register(FunctionId.AssertPeeled, RuntimeTest.ReturnUndefined);
        Register(FunctionId.AssertEscapeAnalysisElided, RuntimeTest.ReturnUndefined);
        Register(FunctionId.HeapObjectVerify, RuntimeTest.ReturnTrue);
        Register(FunctionId.IsBeingInterpreted, RuntimeTest.ReturnTrue);
        Register(FunctionId.IsTurbofanEnabled, RuntimeTest.ReturnFalse);
        Register(FunctionId.IsConcurrentRecompilationSupported, RuntimeTest.ReturnFalse);
        Register(FunctionId.IsDictPropertyConstTrackingEnabled, RuntimeTest.ReturnFalse);
        Register(FunctionId.ClearFunctionFeedback, static (i, a) => RuntimeTest.ClearFunctionFeedback(i, a[0]));
        Register(FunctionId.HasFastProperties, static (i, a) => RuntimeTest.HasFastProperties(i, a[0]));
        Register(FunctionId.HaveSameMap, static (i, a) => RuntimeTest.HaveSameMap(i, a[0], a[1]));
        Register(FunctionId.IsSmi, static (i, a) => RuntimeTest.IsSmi(i, a[0]));
        Register(FunctionId.HasSmiElements, static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsSmiElementsKind));
        Register(FunctionId.HasObjectElements, static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsObjectElementsKind));
        Register(FunctionId.HasSmiOrObjectElements,
            static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsSmiOrObjectElementsKind));
        Register(FunctionId.HasDoubleElements, static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsDoubleElementsKind));
        Register(FunctionId.HasHoleyElements, static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsHoleyElementsKind));
        Register(FunctionId.HasDictionaryElements,
            static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsDictionaryElementsKind));
        Register(FunctionId.HasPackedElements, static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsFastPackedElementsKind));
        Register(FunctionId.HasSloppyArgumentsElements,
            static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsSloppyArgumentsElementsKind));
        Register(FunctionId.HasFastElements, static (i, a) => RuntimeTest.HasElementsKind(a[0], ElementsKinds.IsFastElementsKind));
        Register(FunctionId.DebugPrint, static (i, a) => RuntimeTest.DebugPrint(i, a));
        Register(FunctionId.Is64Bit, static (i, a) => RuntimeTest.Is64Bit(i));
        Register(FunctionId.StringMaxLength, static (i, a) => RuntimeTest.StringMaxLength(i));
        Register(FunctionId.GetUndetectable, static (i, a) => RuntimeTest.GetUndetectable(i));
        Register(FunctionId.Equal, static (i, a) => RuntimeTest.Equal(i, a[0], a[1]));
        Register(FunctionId.NotEqual, static (i, a) => RuntimeTest.NotEqual(i, a[0], a[1]));
        Register(FunctionId.IsInternalizedString, static (i, a) => RuntimeTest.IsInternalizedString(i, a[0]));
        Register(FunctionId.InternalizeString, static (i, a) => RuntimeTest.InternalizeString(i, a[0]));
        Register(FunctionId.GetFunctionForCurrentFrame, static (i, a) => RuntimeTest.GetFunctionForCurrentFrame(i));
        Register(FunctionId.CreatePrivateSymbol, static (i, a) => RuntimeTest.CreatePrivateSymbol(i, a));
        Register(FunctionId.HasOwnConstDataProperty, static (i, a) => RuntimeTest.HasOwnConstDataProperty(i, a[0], a[1]));
        Register(FunctionId.Call, static (i, a) => Execution.Call(i, a[0], a[1], a[2..]));
    }

    static void RegisterIntrinsics()
    {
        Register(FunctionId.CreateJSGeneratorObject,
            static (i, a) => Interpreter.InterpreterGenerators.CreateJSGeneratorObject(i, a[0].As<JSFunction>(), a[1]));
        Register(FunctionId.GeneratorGetFunction, static (i, a) => a[0].As<JSGeneratorObject>().Function);
    }
}
