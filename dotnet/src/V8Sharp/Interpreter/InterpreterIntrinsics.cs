// Port of src/interpreter/interpreter-intrinsics.h/.cc: the runtime functions
// that InvokeIntrinsic dispatches to inline handlers.
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
    static readonly (RuntimeFunctionId RuntimeId, string LowerCase, int Count)[] s_intrinsics =
    [
        (RuntimeFunctionId.InlineAsyncFunctionAwait, "async_function_await_caught", 2),
        (RuntimeFunctionId.InlineAsyncFunctionEnter, "async_function_enter", 2),
        (RuntimeFunctionId.InlineAsyncFunctionReject, "async_function_reject", 2),
        (RuntimeFunctionId.InlineAsyncFunctionResolve, "async_function_resolve", 2),
        (RuntimeFunctionId.InlineAsyncGeneratorAwait, "async_generator_await_caught", 2),
        (RuntimeFunctionId.InlineAsyncGeneratorReject, "async_generator_reject", 2),
        (RuntimeFunctionId.InlineAsyncGeneratorResolve, "async_generator_resolve", 3),
        (RuntimeFunctionId.InlineAsyncGeneratorYieldWithAwait, "async_generator_yield_with_await", 2),
        (RuntimeFunctionId.InlineCreateJSGeneratorObject, "create_js_generator_object", 2),
        (RuntimeFunctionId.InlineGeneratorGetResumeMode, "generator_get_resume_mode", 1),
        (RuntimeFunctionId.InlineGeneratorClose, "generator_close", 1),
        (RuntimeFunctionId.InlineGetImportMetaObject, "get_import_meta_object", 0),
        (RuntimeFunctionId.InlineCopyDataProperties, "copy_data_properties", 2),
        (RuntimeFunctionId.InlineCopyDataPropertiesWithExcludedPropertiesOnStack,
            "copy_data_properties_with_excluded_properties_on_stack", -1),
        (RuntimeFunctionId.InlineCreateIterResultObject, "create_iter_result_object", 2),
        (RuntimeFunctionId.InlineGeneratorYieldResult, "generator_yield_result", 2),
        (RuntimeFunctionId.InlineCreateAsyncFromSyncIterator, "create_async_from_sync_iterator", 1),
    ];

    public static bool IsSupported(RuntimeFunctionId functionId)
    {
        foreach (var entry in s_intrinsics)
        {
            if (entry.RuntimeId == functionId) return true;
        }
        return false;
    }

    public static IntrinsicId FromRuntimeId(RuntimeFunctionId functionId)
    {
        for (int i = 0; i < s_intrinsics.Length; i++)
        {
            if (s_intrinsics[i].RuntimeId == functionId) return (IntrinsicId)i;
        }
        throw new UnreachableException();
    }

    public static RuntimeFunctionId ToRuntimeId(IntrinsicId intrinsicId)
    {
        if ((uint)intrinsicId >= (uint)s_intrinsics.Length) throw new UnreachableException();
        return s_intrinsics[(int)intrinsicId].RuntimeId;
    }

    public static string LowerCaseName(IntrinsicId intrinsicId) => s_intrinsics[(int)intrinsicId].LowerCase;

    public static int ExpectedArgumentCount(IntrinsicId intrinsicId) => s_intrinsics[(int)intrinsicId].Count;
}
