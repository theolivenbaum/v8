// Port of src/interpreter/interpreter-intrinsics.h/.cc: the runtime functions
// that InvokeIntrinsic dispatches to inline handlers.
using V8Sharp.Runtime;
namespace V8Sharp.Interpreter;

public static class IntrinsicsHelper
{
    /// <summary>INTRINSICS_LIST, with upper case name.</summary>
    public enum IntrinsicId
    {
        AsyncFunctionAwait,
        AsyncFunctionEnter,
        AsyncFunctionReject,
        AsyncFunctionResolve,
        AsyncGeneratorAwait,
        AsyncGeneratorReject,
        AsyncGeneratorResolve,
        AsyncGeneratorYieldWithAwait,
        CreateJSGeneratorObject,
        GeneratorGetResumeMode,
        GeneratorClose,
        GetImportMetaObject,
        CopyDataProperties,
        CopyDataPropertiesWithExcludedPropertiesOnStack,
        CreateIterResultObject,
        GeneratorYieldResult,
        CreateAsyncFromSyncIterator,
        IdCount,
    }

    // The lower case names and the expected number of arguments (-1 denoting
    // argument count is variable), in INTRINSICS_LIST order.
    static readonly (FunctionId RuntimeId, string LowerCase, int Count)[] s_intrinsics =
    [
        (FunctionId.InlineAsyncFunctionAwait, "async_function_await_caught", 2),
        (FunctionId.InlineAsyncFunctionEnter, "async_function_enter", 2),
        (FunctionId.InlineAsyncFunctionReject, "async_function_reject", 2),
        (FunctionId.InlineAsyncFunctionResolve, "async_function_resolve", 2),
        (FunctionId.InlineAsyncGeneratorAwait, "async_generator_await_caught", 2),
        (FunctionId.InlineAsyncGeneratorReject, "async_generator_reject", 2),
        (FunctionId.InlineAsyncGeneratorResolve, "async_generator_resolve", 3),
        (FunctionId.InlineAsyncGeneratorYieldWithAwait, "async_generator_yield_with_await", 2),
        (FunctionId.InlineCreateJSGeneratorObject, "create_js_generator_object", 2),
        (FunctionId.InlineGeneratorGetResumeMode, "generator_get_resume_mode", 1),
        (FunctionId.InlineGeneratorClose, "generator_close", 1),
        (FunctionId.InlineGetImportMetaObject, "get_import_meta_object", 0),
        (FunctionId.InlineCopyDataProperties, "copy_data_properties", 2),
        (FunctionId.InlineCopyDataPropertiesWithExcludedPropertiesOnStack,
            "copy_data_properties_with_excluded_properties_on_stack", -1),
        (FunctionId.InlineCreateIterResultObject, "create_iter_result_object", 2),
        (FunctionId.InlineGeneratorYieldResult, "generator_yield_result", 2),
        (FunctionId.InlineCreateAsyncFromSyncIterator, "create_async_from_sync_iterator", 1),
    ];

    public static bool IsSupported(FunctionId functionId)
    {
        foreach (var entry in s_intrinsics)
        {
            if (entry.RuntimeId == functionId) return true;
        }
        return false;
    }

    public static IntrinsicId FromRuntimeId(FunctionId functionId)
    {
        for (int i = 0; i < s_intrinsics.Length; i++)
        {
            if (s_intrinsics[i].RuntimeId == functionId) return (IntrinsicId)i;
        }
        throw new UnreachableException();
    }

    public static FunctionId ToRuntimeId(IntrinsicId intrinsicId)
    {
        if ((uint)intrinsicId >= (uint)s_intrinsics.Length) throw new UnreachableException();
        return s_intrinsics[(int)intrinsicId].RuntimeId;
    }

    public static string LowerCaseName(IntrinsicId intrinsicId) => s_intrinsics[(int)intrinsicId].LowerCase;

    public static int ExpectedArgumentCount(IntrinsicId intrinsicId) => s_intrinsics[(int)intrinsicId].Count;
}
