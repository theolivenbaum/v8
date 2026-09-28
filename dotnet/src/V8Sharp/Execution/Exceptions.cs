// The .NET side of V8's exception propagation (architecture.md section 7).
//
// V8 returns ReadOnlyRoots::exception() and keeps the thrown value in
// Isolate::exception_; C++ callers test for it with MaybeHandle/Maybe. In
// V8Sharp a throw that leaves a frame is a .NET exception carrying the thrown
// JSValue and the message object created at the throw site, so callers do not
// test return values; `try { } catch (JavaScriptException)` is V8's TryCatch.
namespace V8Sharp;

/// <summary>A JavaScript exception in flight (V8: isolate->exception() plus the pending message).</summary>
public sealed class JavaScriptException : Exception
{
    public JavaScriptException(JSValue value, JSMessageObject? message)
        : base("JavaScript exception")
    {
        Value = value;
        MessageObject = message;
    }

    /// <summary>The thrown value.</summary>
    public JSValue Value { get; }

    /// <summary>The message created when the value was thrown (V8's pending message), if any.</summary>
    public JSMessageObject? MessageObject { get; internal set; }

    public override string Message
    {
        get
        {
            try
            {
                Isolate? isolate = Isolate.Current;
                if (isolate is not null && MessageObject is not null)
                {
                    return MessageHandler.GetMessage(isolate, MessageObject).ToString();
                }
                if (isolate is null) return Value.ToString();
                return Objects.ObjectOps.NoSideEffectsToString(isolate, Value).ToString();
            }
            catch (Exception)
            {
                return "JavaScript exception";
            }
        }
    }
}

/// <summary>
/// The uncatchable termination exception (V8's termination_exception), raised
/// by TerminateExecution. JavaScript catch blocks and finally blocks do not see
/// it; only the embedder does.
/// </summary>
public sealed class TerminationException() : Exception("Execution terminated");
