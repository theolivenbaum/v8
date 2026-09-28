// The engine's view of the JavaScript stack, standing in for V8's
// StackFrameIterator/JavaScriptStackFrameIterator and FrameSummary
// (src/execution/frames.h). The interpreter owns the frames; it registers an
// IJavaScriptFrames implementation on the isolate so stack traces, message
// locations and Error.captureStackTrace can walk them.
using V8Sharp.Objects;

namespace V8Sharp;

/// <summary>V8's FrameSummary::JavaScriptFrameSummary.</summary>
public readonly struct JavaScriptFrameSummary(JSValue receiver, JSFunction function, int codeOffset, int sourcePosition, bool isConstructor)
{
    public JSValue Receiver { get; } = receiver;
    public JSFunction Function { get; } = function;
    /// <summary>The bytecode offset (V8's code_offset).</summary>
    public int CodeOffset { get; } = codeOffset;
    /// <summary>The source position of <see cref="CodeOffset"/> (AbstractCode::SourcePosition).</summary>
    public int SourcePosition { get; } = sourcePosition;
    public bool IsConstructor { get; } = isConstructor;
}

/// <summary>Access to the live JavaScript frames of an isolate, top-most first.</summary>
public interface IJavaScriptFrames
{
    /// <summary>Gets the summary of the JavaScript frame at depth <paramref name="index"/> (0 is the top-most).</summary>
    bool TryGetFrame(int index, out JavaScriptFrameSummary summary);
}

/// <summary>V8's FrameSkipMode (messages.h).</summary>
public enum FrameSkipMode
{
    SKIP_FIRST,
    SKIP_UNTIL_SEEN,
    SKIP_NONE,
}

/// <summary>
/// The CallPrinter hook (src/ast/prettyprinter.h): renders the expression at a
/// source position of a function for error messages such as
/// "x.foo is not a function". The parser port implements it.
/// </summary>
public interface ICallPrinter
{
    CallPrinterResult Print(Isolate isolate, SharedFunctionInfo shared, int position, bool spreadErrorInArgs);
}

/// <summary>CallPrinter::ErrorHint.</summary>
public enum CallPrinterErrorHint
{
    None,
    NormalIterator,
    AsyncIterator,
    CallAndNormalIterator,
    CallAndAsyncIterator,
}

/// <summary>What CallPrinter found at the position.</summary>
public readonly struct CallPrinterResult
{
    /// <summary>The printed call site; empty if nothing was found.</summary>
    public string CallSite { get; init; }
    public CallPrinterErrorHint Hint { get; init; }
    /// <summary>destructuring_assignment() != nullptr.</summary>
    public bool IsDestructuring { get; init; }
    /// <summary>The destructuring property's name if it is a property name, else null.</summary>
    public string? DestructuringPropertyName { get; init; }
    /// <summary>Position of the destructuring property key, or -1.</summary>
    public int DestructuringPropertyPosition { get; init; }
    /// <summary>Position of the destructured value, or -1.</summary>
    public int DestructuringValuePosition { get; init; }
    /// <summary>Position of the spread argument, or -1.</summary>
    public int SpreadArgPosition { get; init; }
}
