// Port of the parts of src/objects/templates.h that the engine itself uses:
// FunctionTemplateInfo as the function_data of an API function. V8Sharp has
// no public embedder API yet; the callback is a builtin-shaped C# delegate.
namespace V8Sharp.Objects;

/// <summary>
/// V8's FunctionTemplateInfo, reduced to what an API function needs at call
/// time: its callback and length. SharedFunctionInfo.FunctionData holds it and
/// the function runs through Builtin.HandleApiCallOrConstruct.
/// </summary>
public sealed class FunctionTemplateInfo(BuiltinFunction callback) : HeapObject(InstanceType.FunctionTemplateInfoType)
{
    public readonly BuiltinFunction Callback = callback;

    public int Length;

    /// <summary>FunctionTemplateInfo::accept_any_receiver.</summary>
    public bool AcceptAnyReceiver = true;

    /// <summary>
    /// FunctionTemplateInfo::GetInstanceCallHandler: the call handler of the
    /// instances (ObjectTemplate::SetCallAsFunctionHandler), which makes them
    /// callable through the call_as_function delegate.
    /// </summary>
    public FunctionTemplateInfo? InstanceCallHandler;
}
