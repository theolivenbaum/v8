// Port of test/unittests/interpreter/interpreter-tester.h and .cc.
//
// The callables of V8's tester are replaced by Call / CallWithReceiver; a
// function is compiled from the source or from "(function f(a, ...){})" with
// its bytecode (and feedback metadata) overwritten, as in V8.
using V8Sharp.Codegen;
using V8Sharp.Interpreter;
using V8Sharp.Objects;

namespace V8Sharp.Tests.Interpreter;

public sealed class InterpreterTester
{
    public const string kFunctionName = "f";

    readonly Isolate _isolate;
    readonly string? _source;
    readonly BytecodeArray? _bytecode;
    readonly FeedbackMetadata? _feedbackMetadata;
    JSFunction? _function;

    public InterpreterTester(Isolate isolate, string? source, BytecodeArray? bytecode, FeedbackMetadata? feedbackMetadata)
    {
        _isolate = isolate;
        _source = source;
        _bytecode = bytecode;
        _feedbackMetadata = feedbackMetadata;
    }

    public InterpreterTester(Isolate isolate, BytecodeArray bytecode, FeedbackMetadata? feedbackMetadata = null)
        : this(isolate, null, bytecode, feedbackMetadata)
    {
    }

    public InterpreterTester(Isolate isolate, string source) : this(isolate, source, null, null)
    {
    }

    public bool HasFeedbackMetadata => _feedbackMetadata is not null;

    /// <summary>The function under test, created on first use with <paramref name="argCount"/> parameters.</summary>
    public JSFunction GetBytecodeFunction(int argCount = 0)
    {
        if (_function is not null) return _function;
        JSFunction function;
        if (_source is not null)
        {
            CompileRun(_isolate, _source);
            JSValue f = ObjectOps.GetProperty(_isolate, _isolate.NativeContext.GlobalProxyObject,
                _isolate.Factory.InternalizeString(kFunctionName));
            function = f.As<JSFunction>();
        }
        else
        {
            var source = new System.Text.StringBuilder("(function " + kFunctionName + "(");
            for (int i = 0; i < argCount; i++) source.Append(i == 0 ? "a" : ", a");
            source.Append("){})");
            function = CompileRun(_isolate, source.ToString()).As<JSFunction>();
        }
        if (!function.Shared.IsCompiled) Compiler.CompileLazy(_isolate, function);

        if (_bytecode is not null)
        {
            function.Shared.FunctionData = _bytecode;
        }
        if (_feedbackMetadata is not null)
        {
            function.RawFeedbackCell = FeedbackCell.ManyClosuresCell;
            function.Shared.FeedbackMetadata = _feedbackMetadata;
            JSFunctionFeedback.EnsureFeedbackVector(_isolate, function);
        }
        _function = function;
        return function;
    }

    public FeedbackVector Vector => JSFunctionFeedback.GetFeedbackVector(_function!)!;

    /// <summary>The callable with an undefined receiver.</summary>
    public JSValue Call(params JSValue[] args) =>
        Execution.Call(_isolate, GetBytecodeFunction(args.Length), JSValue.Undefined, args);

    /// <summary>The callable with a receiver.</summary>
    public JSValue CallWithReceiver(JSValue receiver, params JSValue[] args) =>
        Execution.Call(_isolate, GetBytecodeFunction(args.Length), receiver, args);

    /// <summary>Calls with no arguments and returns the message of the exception it must throw.</summary>
    public JSMessageObject CheckThrowsReturnMessage()
    {
        try
        {
            Call();
        }
        catch (JavaScriptException e)
        {
            Assert.NotNull(e.MessageObject);
            return e.MessageObject!;
        }
        Assert.Fail("expected an exception");
        return null!;
    }

    /// <summary>Calls with no arguments and returns the exception it must throw.</summary>
    public JSValue CheckThrows(params JSValue[] args)
    {
        try
        {
            Call(args);
        }
        catch (JavaScriptException e)
        {
            return e.Value;
        }
        Assert.Fail("expected an exception");
        return default;
    }

    public static JSValue CompileRun(Isolate isolate, string source) => Compiler.CompileAndRun(isolate, source);

    public static JSValue NewObject(Isolate isolate, string script) => CompileRun(isolate, script);

    public static JSString GetName(Isolate isolate, string name) => isolate.Factory.InternalizeString(name);

    public static string SourceForBody(string body) => "function " + kFunctionName + "() {\n" + body + "\n}";

    public static string function_name() => kFunctionName;

    public static RegisterList NewRegisterList(int firstRegIndex, int registerCount) =>
        BytecodeUtils.NewRegisterList(firstRegIndex, registerCount);

    public BinaryOperationFeedback.Type GetBinaryEmbeddedFeedback(int bytecodeOffset, int feedbackValueOffset) =>
        BinaryOperationFeedback.DecodeTypeIndex((BinaryOperationFeedback.TypeIndex)_bytecode!.Get(bytecodeOffset + feedbackValueOffset));

    public CompareOperationFeedback.Type GetCompareEmbeddedFeedback(int bytecodeOffset, int feedbackValueOffset) =>
        CompareOperationFeedback.DecodeTypeIndex((CompareOperationFeedback.TypeIndex)_bytecode!.Get(bytecodeOffset + feedbackValueOffset));
}
