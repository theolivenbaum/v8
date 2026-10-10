// Port of the call path of src/builtins/builtins-api.cc (HandleApiCallHelper)
// for API functions whose FunctionTemplateInfo is a C# callback. Instance
// templates, signatures and access checks are not ported (no embedder API).
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static class BuiltinsApi
{
    /// <summary>Registers the API call builtins in <see cref="BuiltinRegistry"/>.</summary>
    public static void Register()
    {
        BuiltinRegistry.Register(Builtin.HandleApiCallOrConstruct, HandleApiCallOrConstruct);
        BuiltinRegistry.Register(Builtin.HandleApiCallAsFunctionDelegate, HandleApiCallAsFunctionDelegate);
    }

    /// <summary>
    /// Builtins::HandleApiCallAsFunctionDelegate
    /// (HandleApiCallAsFunctionOrConstructorDelegate, not a construct call):
    /// a call of a callable non-function API object, which is the receiver.
    /// The callback comes from the instance call handler of the API function
    /// that constructed the object.
    /// </summary>
    public static JSValue HandleApiCallAsFunctionDelegate(Isolate isolate, in BuiltinArguments args)
    {
        var obj = (JSObject)args.Receiver.Object;
        var constructor = (JSFunction)obj.Map.GetConstructor()!;
        FunctionTemplateInfo templ = constructor.Shared.GetApiFunctionData().InstanceCallHandler!;
        var callArgs = new BuiltinArguments(args.Target, JSValue.Undefined, obj, args.Arguments);
        JSValue result = templ.Callback(isolate, in callArgs);
        return result;
    }

    /// <summary>
    /// Builtins::HandleApiCallOrConstruct: calls the API callback of the
    /// target's FunctionTemplateInfo. A construct call gets a fresh ordinary
    /// object as receiver (V8 instantiates the instance template).
    /// </summary>
    public static JSValue HandleApiCallOrConstruct(Isolate isolate, in BuiltinArguments args)
    {
        FunctionTemplateInfo funData = args.Target.Shared.GetApiFunctionData();
        if (args.IsConstructCall)
        {
            var newTarget = (JSReceiver)args.NewTarget.Object;
            JSObject jsReceiver = JSObject.New(isolate, isolate.NativeContext.ObjectFunction, newTarget, null);
            var constructArgs = new BuiltinArguments(args.Target, args.NewTarget, jsReceiver, args.Arguments);
            JSValue rawResult = funData.Callback(isolate, in constructArgs);
            return rawResult.IsJSReceiver ? rawResult : jsReceiver;
        }
        return funData.Callback(isolate, in args);
    }
}
