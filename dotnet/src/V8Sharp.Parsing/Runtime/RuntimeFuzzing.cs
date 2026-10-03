// Port of Runtime::IsEnabledForFuzzing (src/runtime/runtime.cc): which
// runtime functions natives syntax may call under --fuzzing. The test list is
// FOR_EACH_INTRINSIC_TEST of src/runtime/runtime.h (no WebAssembly).

namespace V8Sharp.Runtime;

public static class RuntimeFuzzing
{
    public static bool IsEnabledForFuzzing(FunctionId id, V8Sharp.Parsing.ParsingFlags flags)
    {
        // For differential fuzzing, only a handful of functions are allowed,
        // everything else is disabled.
        if (flags.allow_natives_for_differential_fuzzing)
        {
            switch (id)
            {
            case FunctionId.ArrayBufferDetach:
            case FunctionId.DeoptimizeFunction:
            case FunctionId.DeoptimizeNow:
            case FunctionId.DisableOptimizationFinalization:
            case FunctionId.EnableCodeLoggingForTesting:
            case FunctionId.FinalizeOptimization:
            case FunctionId.GetUndetectable:
            case FunctionId.NeverOptimizeFunction:
            case FunctionId.OptimizeFunctionOnNextCall:
            case FunctionId.OptimizeMaglevOnNextCall:
            case FunctionId.OptimizeOsr:
            case FunctionId.PrepareFunctionForOptimization:
            case FunctionId.PretenureAllocationSite:
            case FunctionId.SetAllocationTimeout:
            case FunctionId.SetDispatchTableGCInterval:
            case FunctionId.SetForceSlowPath:
            case FunctionId.SimulateNewspaceFull:
            case FunctionId.WaitForBackgroundOptimization:
            case FunctionId.SetBatterySaverMode:
            case FunctionId.SetPriorityBestEffort:
            case FunctionId.SetPriorityUserVisible:
            case FunctionId.SetPriorityUserBlocking:
            case FunctionId.IsEfficiencyModeEnabled:
            case FunctionId.BaselineOsr:
            case FunctionId.CompileBaseline:
                    return true;
                default:
                    return false;
            }
        }

        // Runtime functions disabled for all/most types of fuzzing.
        switch (id)
        {
            case FunctionId.Abort:
            case FunctionId.AbortCSADcheck:
            case FunctionId.AbortJS:
            case FunctionId.SystemBreak:
            case FunctionId.BenchMaglev:
            case FunctionId.BenchTurbofan:
            case FunctionId.DebugPrintPtr:
            case FunctionId.DisassembleFunction:
            case FunctionId.GetFunctionForCurrentFrame:
            case FunctionId.GetCallable:
            case FunctionId.GetAbstractModuleSource:
            case FunctionId.AssertNotPeeled:
            case FunctionId.AssertPeeled:
            case FunctionId.TurbofanStaticAssert:
            case FunctionId.AssertEscapeAnalysisElided:
            case FunctionId.ClearFunctionFeedback:
            case FunctionId.StringIsFlat:
            case FunctionId.GetInitializerFunction:
            case FunctionId.ArrayBufferDetachForceWasm:
            case FunctionId.ConstructDouble:
            case FunctionId.SerializeDeserializeNow:
            case FunctionId.CompleteInobjectSlackTracking:
            case FunctionId.ForceFlush:
                return false;

            case FunctionId.LeakHole:
                return flags.hole_fuzzing;

            case FunctionId.GetBytecode:
                return flags.sandbox_testing || flags.sandbox_fuzzing;

            case FunctionId.InstallBytecode:
                return (flags.sandbox_testing || flags.sandbox_fuzzing) && flags.verify_bytecode_full;

            case FunctionId.IsSmi:
                return true;  // Enabled when not performing differential fuzzing.
        }

        // The default case: test functions are exposed, everything else is not.
        switch (id)
        {
            case FunctionId.ArrayBufferDetach:
            case FunctionId.Abort:
            case FunctionId.AbortCSADcheck:
            case FunctionId.AbortJS:
            case FunctionId.ActiveTierIsIgnition:
            case FunctionId.ActiveTierIsSparkplug:
            case FunctionId.ActiveTierIsMaglev:
            case FunctionId.ActiveTierIsTurbofan:
            case FunctionId.AllocateHeapNumberWithValue:
            case FunctionId.ArrayBufferDetachForceWasm:
            case FunctionId.ArrayIteratorProtector:
            case FunctionId.ArraySpeciesProtector:
            case FunctionId.AssertNotPeeled:
            case FunctionId.AssertPeeled:
            case FunctionId.BaselineOsr:
            case FunctionId.BenchMaglev:
            case FunctionId.BenchTurbofan:
            case FunctionId.BlockAt:
            case FunctionId.VerifyGetJSBuiltinState:
            case FunctionId.ClearFunctionFeedback:
            case FunctionId.ClearMegamorphicStubCache:
            case FunctionId.CompleteInobjectSlackTracking:
            case FunctionId.ConstructConsString:
            case FunctionId.ConstructDouble:
            case FunctionId.ConstructInternalizedString:
            case FunctionId.ConstructSlicedString:
            case FunctionId.ConstructThinString:
            case FunctionId.CurrentFrameIsTurbofan:
            case FunctionId.DebugPrint:
            case FunctionId.DebugPrintCppHeapPointerTable:
            case FunctionId.DebugPrintCppHeapPointerTableFilterTag:
            case FunctionId.DebugPrintExternalPointerTable:
            case FunctionId.DebugPrintExternalPointerTableFilterTag:
            case FunctionId.DebugPrintGeneric:
            case FunctionId.DebugPrintFloat:
            case FunctionId.DebugPrintPtr:
            case FunctionId.DebugPrintWord:
            case FunctionId.DebugTrace:
            case FunctionId.DebugTraceMinimal:
            case FunctionId.DeoptimizeFunction:
            case FunctionId.DisableOptimizationFinalization:
            case FunctionId.DisallowCodegenFromStrings:
            case FunctionId.DisassembleFunction:
            case FunctionId.EnableCodeLoggingForTesting:
            case FunctionId.EnsureFeedbackVectorForFunction:
            case FunctionId.FinalizeOptimization:
            case FunctionId.ForceFlush:
            case FunctionId.MajorGCForCompilerTesting:
            case FunctionId.GetAbstractModuleSource:
            case FunctionId.GetBytecode:
            case FunctionId.ExhaustInterruptBudget:
            case FunctionId.GetCallable:
            case FunctionId.GetFeedback:
            case FunctionId.GetFunctionForCurrentFrame:
            case FunctionId.GetInitializerFunction:
            case FunctionId.GetOptimizationStatus:
            case FunctionId.GetUndetectable:
            case FunctionId.GetWeakCollectionSize:
            case FunctionId.GlobalPrint:
            case FunctionId.HasCowElements:
            case FunctionId.HasDictionaryElements:
            case FunctionId.HasDoubleElements:
            case FunctionId.HasElementsInALargeObjectSpace:
            case FunctionId.HasFastElements:
            case FunctionId.HasFastProperties:
            case FunctionId.HasFixedBigInt64Elements:
            case FunctionId.HasFixedBigUint64Elements:
            case FunctionId.HasFixedFloat16Elements:
            case FunctionId.HasFixedFloat32Elements:
            case FunctionId.HasFixedFloat64Elements:
            case FunctionId.HasFixedInt16Elements:
            case FunctionId.HasFixedInt32Elements:
            case FunctionId.HasFixedInt8Elements:
            case FunctionId.HasFixedUint16Elements:
            case FunctionId.HasFixedUint32Elements:
            case FunctionId.HasFixedUint8ClampedElements:
            case FunctionId.HasFixedUint8Elements:
            case FunctionId.HasHoleyElements:
            case FunctionId.HasObjectElements:
            case FunctionId.HasPackedElements:
            case FunctionId.HasSloppyArgumentsElements:
            case FunctionId.HasSmiElements:
            case FunctionId.HasSmiOrObjectElements:
            case FunctionId.HaveSameMap:
            case FunctionId.HeapObjectVerify:
            case FunctionId.ICsAreEnabled:
            case FunctionId.InLargeObjectSpace:
            case FunctionId.InstallBytecode:
            case FunctionId.InYoungGeneration:
            case FunctionId.Is64Bit:
            case FunctionId.IsAtomicsWaitAllowed:
            case FunctionId.IsBeingInterpreted:
            case FunctionId.IsConcatSpreadableProtector:
            case FunctionId.IsConcurrentRecompilationSupported:
            case FunctionId.IsDictPropertyConstTrackingEnabled:
            case FunctionId.IsEfficiencyModeEnabled:
            case FunctionId.IsInPlaceInternalizableString:
            case FunctionId.IsInternalizedString:
            case FunctionId.StringToCString:
            case FunctionId.StringUtf8Value:
            case FunctionId.IsUndefinedDoubleEnabled:
            case FunctionId.IsMaglevEnabled:
            case FunctionId.IsSameHeapObject:
            case FunctionId.IsSharedString:
            case FunctionId.IsInWritableSharedSpace:
            case FunctionId.IsSparkplugEnabled:
            case FunctionId.IsTurbofanEnabled:
            case FunctionId.IsWasmTieringPredictable:
            case FunctionId.MapIteratorProtector:
            case FunctionId.NeverOptimizeFunction:
            case FunctionId.NewRegExpWithBacktrackLimit:
            case FunctionId.NoElementsProtector:
            case FunctionId.NotifyContextDisposed:
            case FunctionId.SetPriorityBestEffort:
            case FunctionId.SetPriorityUserVisible:
            case FunctionId.SetPriorityUserBlocking:
            case FunctionId.OptimizeMaglevOnNextCall:
            case FunctionId.OptimizeFunctionOnNextCall:
            case FunctionId.OptimizeOsr:
            case FunctionId.PrepareFunctionForOptimization:
            case FunctionId.PretenureAllocationSite:
            case FunctionId.PrintWithNameForAssert:
            case FunctionId.PromiseSpeciesProtector:
            case FunctionId.RegExpSpeciesProtector:
            case FunctionId.RegexpHasBytecode:
            case FunctionId.RegexpHasNativeCode:
            case FunctionId.RegexpIsUnmodified:
            case FunctionId.RegexpQuickCheckRejects:
            case FunctionId.RegexpTypeTag:
            case FunctionId.Resume:
            case FunctionId.RunningInSimulator:
            case FunctionId.RuntimeEvaluateREPL:
            case FunctionId.ScheduleGCInStackCheck:
            case FunctionId.SerializeDeserializeNow:
            case FunctionId.SetAllocationTimeout:
            case FunctionId.SetDispatchTableGCInterval:
            case FunctionId.SetBatterySaverMode:
            case FunctionId.SetForceSlowPath:
            case FunctionId.SetIteratorProtector:
            case FunctionId.SharedGC:
            case FunctionId.ShareObject:
            case FunctionId.SimulateNewspaceFull:
            case FunctionId.StringIsFlat:
            case FunctionId.StringIteratorProtector:
            case FunctionId.StringWrapperToPrimitiveProtector:
            case FunctionId.SystemBreak:
            case FunctionId.TakeHeapSnapshot:
            case FunctionId.TraceEnter:
            case FunctionId.TraceExit:
            case FunctionId.TurbofanStaticAssert:
            case FunctionId.AssertEscapeAnalysisElided:
            case FunctionId.TypedArraySpeciesProtector:
            case FunctionId.WaitForBackgroundOptimization:
            case FunctionId.WaitUntilBlocked:
            case FunctionId.InlineDeoptimizeNow:
            case FunctionId.LeakHole:
            case FunctionId.GetHoleNaNLower:
            case FunctionId.GetHoleNaNUpper:
            case FunctionId.GetHoleNaN:
            case FunctionId.GetUndefinedNaN:
                return true;
            default:
                return false;
        }
    }
}
