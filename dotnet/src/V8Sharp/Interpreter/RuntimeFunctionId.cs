// The parts of src/runtime/runtime.h the interpreter reads (Runtime::FunctionId
// and Runtime::FunctionForId(id)->name/nargs/result_size). The table itself
// is V8Sharp.Parsing's port (V8Sharp.Runtime.Runtime), shared with the parser.
using V8Sharp.Runtime;

namespace V8Sharp.Interpreter;

public static class RuntimeFunctions
{
    public const int kNumFunctions = Runtime.Runtime.kNumFunctions;

    /// <summary>Runtime::FunctionForId(id)-&gt;name: the C++ name, with a leading '_' for inline intrinsics.</summary>
    public static string Name(FunctionId id) => Runtime.Runtime.FunctionForId(id).name;
    public static int NumberOfArguments(FunctionId id) => Runtime.Runtime.FunctionForId(id).nargs;
    public static int ResultSize(FunctionId id) => Runtime.Runtime.FunctionForId(id).result_size;
    public static bool IsInline(FunctionId id) => Runtime.Runtime.FunctionForId(id).intrinsic_type == IntrinsicType.INLINE;

    /// <summary>Looks a function up by its Runtime::Function name (e.g. "DefineClass", "_CreateJSGeneratorObject").</summary>
    public static bool TryFromName(string name, out FunctionId id)
    {
        RuntimeFunction? f = Runtime.Runtime.FunctionForName(name);
        id = f?.function_id ?? default;
        return f is not null;
    }
}
