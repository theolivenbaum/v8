// Port of the parts of src/execution/isolate.{h,cc} the object model and the
// runtime need: roots, factory, string table, flags, the current context,
// throwing (Throw/ReThrow/ThrowAt/StackOverflow/TerminateExecution), message
// creation, error stack capture, the stack guard, the microtask queue and the
// interpreter's register stack (architecture.md sections 6 and 7).
//
// Types of src/execution live in the root namespace V8Sharp, because a class
// named Execution cannot live in a namespace V8Sharp.Execution.
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>Runs a function's bytecode: the entry point the interpreter registers (V8's InterpreterEntryTrampoline).</summary>
public delegate JSValue InterpreterEntryDelegate(Isolate isolate, JSFunction function, JSValue receiver,
    ReadOnlySpan<JSValue> arguments, JSValue newTarget);

/// <summary>Compiles a lazily compiled function (V8's Compiler::Compile); returns false if compilation threw.</summary>
public delegate bool CompileLazyDelegate(Isolate isolate, JSFunction function);

/// <summary>
/// V8's Isolate: one independent instance of the engine, with its own heap
/// state, contexts, flags and execution stack. Use it from one thread at a
/// time; <see cref="Enter"/> makes it <see cref="Current"/> on that thread.
/// </summary>
public sealed partial class Isolate
{
    [ThreadStatic] static Isolate? t_current;

    /// <summary>The isolate entered on this thread (V8's Isolate::Current()).</summary>
    public static Isolate? Current => t_current;

    /// <summary>The current isolate's flags, or the process defaults when none is entered.</summary>
    public static FlagList CurrentFlags => t_current?.Flags ?? FlagList.Default;

    /// <summary>The flags of this isolate (V8's v8_flags).</summary>
    public readonly FlagList Flags;

    public readonly Factory Factory;
    public readonly StringTable StringTable;
    public readonly StackGuard StackGuard;
    public readonly MicrotaskQueue DefaultMicrotaskQueue;
    public readonly Protectors Protectors = new();

    /// <summary>Bootstrapper::IsActive: true while Genesis builds a native context.</summary>
    public bool BootstrapperActive;

    /// <summary>The current context (V8's isolate->context()).</summary>
    public Context? Context;

    /// <summary>The current native context (V8's isolate->native_context()).</summary>
    public NativeContext NativeContext => Context?.NativeContext ?? throw new InvalidOperationException("No context entered");

    /// <summary>The native context the isolate was created with (the first realm).</summary>
    public NativeContext? InitialNativeContext;

    /// <summary>Runs bytecode. Registered by the interpreter.</summary>
    public InterpreterEntryDelegate? InterpreterEntry;

    /// <summary>Compiles lazy functions. Registered by the compiler pipeline.</summary>
    public CompileLazyDelegate? CompileLazyHook;

    /// <summary>The live JavaScript frames. Registered by the interpreter.</summary>
    public IJavaScriptFrames? Frames;

    /// <summary>The CallPrinter for error messages. Registered by the parser port.</summary>
    public ICallPrinter? CallPrinter;

    /// <summary>
    /// Isolate::SetPrepareStackTraceCallback (the embedder's
    /// Error.prepareStackTrace replacement); null when not set.
    /// </summary>
    public Func<Isolate, NativeContext, JSObject, JSArray, JSValue>? PrepareStackTraceCallback;

    /// <summary>Isolate::formatting_stack_trace.</summary>
    public bool FormattingStackTrace;

    /// <summary>
    /// Isolate::SetHostCreateShadowRealmContextCallback: creates the native
    /// context of a new ShadowRealm (d8 creates a fresh context); null when not set.
    /// </summary>
    public Func<Isolate, NativeContext, NativeContext>? HostCreateShadowRealmContextCallback;

    /// <summary>Isolate::RunHostCreateShadowRealmContextCallback.</summary>
    public NativeContext RunHostCreateShadowRealmContextCallback()
    {
        if (HostCreateShadowRealmContextCallback is null)
        {
            Throw(Factory.NewError(NativeContext.ErrorFunction, MessageTemplate.Unsupported, []));
        }
        NativeContext shadowRealmContext = HostCreateShadowRealmContextCallback!(this, NativeContext);
        // shadow_realm_context->set_scope_info(shadow_realm_scope_info).
        shadowRealmContext.IsShadowRealm = true;
        return shadowRealmContext;
    }

    /// <summary>Isolate::allow_atomics_wait (v8::Isolate::SetAllowAtomicsWait; d8's --no-can-block).</summary>
    public bool AllowAtomicsWait = true;

    /// <summary>Isolate::error_message_param (used by DataView builtins' stack names).</summary>
    public int ErrorMessageParam;

    int _nextScriptId;
    int _nextUniqueSfiId;
    bool _inStackOverflow;

    /// <summary>The Symbol.for registry (V8's public_symbol_table).</summary>
    public readonly Dictionary<string, Symbol> PublicSymbolTable = new(StringComparer.Ordinal);
    /// <summary>The api_symbol_table / api_private_symbol_table (Symbol::ForApi).</summary>
    public readonly Dictionary<string, Symbol> ApiSymbolTable = new(StringComparer.Ordinal);
    public readonly Dictionary<string, Symbol> ApiPrivateSymbolTable = new(StringComparer.Ordinal);

    /// <summary>Creates an isolate without a context. <see cref="Bootstrapper"/> creates its native context.</summary>
    public Isolate(FlagList? flags = null)
    {
        Flags = flags ?? FlagList.Default.Clone();
        StringTable = new StringTable(this);
        Factory = new Factory(this);
        StackGuard = new StackGuard(this);
        DefaultMicrotaskQueue = new MicrotaskQueue(this);
        RegisterStack = new JSValue[kRegisterStackSize];
        RegisterStackLimit = (int)Math.Min(kRegisterStackSize, Math.Max(1L, (long)Flags.stack_size) * 1024 / 8);
        InitializeInterpreter();
    }

    /// <summary>Creates an isolate and its initial native context (V8's Isolate::New + Context::New).</summary>
    public static Isolate New(FlagList? flags = null)
    {
        var isolate = new Isolate(flags);
        using (isolate.Enter())
        {
            NativeContext context = Bootstrapper.CreateEnvironment(isolate);
            isolate.InitialNativeContext = context;
            isolate.Context = context;
        }
        return isolate;
    }

    /// <summary>Makes this isolate current on the calling thread until the scope is disposed (V8's Isolate::Scope).</summary>
    public IsolateScope Enter()
    {
        Isolate? previous = t_current;
        t_current = this;
        return new IsolateScope(previous);
    }

    /// <summary>Restores the previously entered isolate.</summary>
    public readonly struct IsolateScope(Isolate? previous) : IDisposable
    {
        public void Dispose() => t_current = previous;
    }

    /// <summary>Switches the current context for the duration of a scope (V8's SaveContext / Context::Scope).</summary>
    public SaveContext EnterContext(Context context)
    {
        var save = new SaveContext(this, Context);
        Context = context;
        return save;
    }

    public readonly struct SaveContext(Isolate isolate, Context? saved) : IDisposable
    {
        public void Dispose() => isolate.Context = saved;
    }

    public int GetNextScriptId() => Interlocked.Increment(ref _nextScriptId);
    public int GetNextUniqueSharedFunctionInfoId() => Interlocked.Increment(ref _nextUniqueSfiId);

    // --- register stack (architecture.md section 7) -------------------------

    /// <summary>Slots in the interpreter's register stack.</summary>
    public const int kRegisterStackSize = 1 << 20;

    /// <summary>The interpreter's register stack: parameters and register files of all interpreter frames.</summary>
    public readonly JSValue[] RegisterStack;

    /// <summary>The first free slot of <see cref="RegisterStack"/>.</summary>
    public int RegisterStackTop;

    /// <summary>
    /// The register stack's limit in slots, V8's stack limit (--stack-size, in KB)
    /// counted in 8-byte stack slots: the interpreter frames' parameters, fixed
    /// slots and registers live here as they do on V8's machine stack, so
    /// recursion overflows at about V8's depth. The .NET stack check stays as
    /// the backstop for native recursion.
    /// </summary>
    public int RegisterStackLimit;

    /// <summary>Reserves <paramref name="count"/> register slots; throws V8's stack overflow RangeError when full.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AllocateRegisters(int count)
    {
        int start = RegisterStackTop;
        int end = start + count;
        if ((uint)end > (uint)RegisterStackLimit) StackOverflow();
        RegisterStackTop = end;
        return start;
    }

    /// <summary>Releases register slots down to <paramref name="start"/>, clearing them for the GC.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReleaseRegisters(int start)
    {
        RegisterStack.AsSpan(start, RegisterStackTop - start).Clear();
        RegisterStackTop = start;
    }

    // --- lazy compilation ------------------------------------------------------

    /// <summary>Compiler::Compile for a lazy function: true when the function is (now) compiled.</summary>
    public bool CompileLazy(JSFunction function)
    {
        if (function.Shared.IsCompiled) return true;
        CompileLazyDelegate? hook = CompileLazyHook;
        if (hook is null) return false;
        try
        {
            return hook(this, function);
        }
        catch (JavaScriptException)
        {
            // Compiler::CLEAR_EXCEPTION.
            return false;
        }
    }

    // --- exceptions ------------------------------------------------------------

    /// <summary>
    /// Isolate::Throw: creates the message object for <paramref name="exception"/>
    /// (computing the location from the top JavaScript frame when none is given)
    /// and throws it as a <see cref="JavaScriptException"/>. Never returns; the
    /// return type lets callers write <c>return isolate.Throw(...)</c>.
    /// </summary>
    [DoesNotReturn]
    public JSValue Throw(JSValue exception, MessageLocation? location = null)
    {
        JSMessageObject? message = null;
        MessageLocation? computedLocation = location;
        // If no location was specified we try to use a computed one instead.
        if (computedLocation is null && ComputeLocation(out MessageLocation computed))
        {
            computedLocation = computed;
        }
        if (!BootstrapperActive)
        {
            message = CreateMessageOrAbort(exception, computedLocation);
        }
        throw new JavaScriptException(exception, message);
    }

    /// <summary>Isolate::ThrowAt: records the location on the error object, then throws it.</summary>
    [DoesNotReturn]
    public JSValue ThrowAt(JSObject exception, MessageLocation location)
    {
        ObjectOps.SetProperty(this, exception, ReadOnlyRoots.error_start_pos_symbol, JSValue.FromInt(location.StartPos),
            StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        ObjectOps.SetProperty(this, exception, ReadOnlyRoots.error_end_pos_symbol, JSValue.FromInt(location.EndPos),
            StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        ObjectOps.SetProperty(this, exception, ReadOnlyRoots.error_script_symbol, location.Script,
            StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        return Throw(exception, location);
    }

    /// <summary>Isolate::ReThrow: rethrows with an existing message (or none).</summary>
    [DoesNotReturn]
    public JSValue ReThrow(JSValue exception, JSMessageObject? message = null) =>
        throw new JavaScriptException(exception, message);

    /// <summary>THROW_NEW_ERROR with a TypeError from a message template.</summary>
    [DoesNotReturn]
    public JSValue ThrowTypeError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        Throw(Factory.NewTypeError(template, args));

    /// <summary>THROW_NEW_ERROR with a RangeError from a message template.</summary>
    [DoesNotReturn]
    public JSValue ThrowRangeError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        Throw(Factory.NewRangeError(template, args));

    /// <summary>THROW_NEW_ERROR with a ReferenceError from a message template.</summary>
    [DoesNotReturn]
    public JSValue ThrowReferenceError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        Throw(Factory.NewReferenceError(template, args));

    /// <summary>THROW_NEW_ERROR with a SyntaxError from a message template.</summary>
    [DoesNotReturn]
    public JSValue ThrowSyntaxError(MessageTemplate template, params ReadOnlySpan<JSValue> args) =>
        Throw(Factory.NewSyntaxError(template, args));

    /// <summary>Isolate::ThrowIllegalOperation.</summary>
    [DoesNotReturn]
    public JSValue ThrowIllegalOperation() => Throw(ReadOnlyRoots.illegal_access_string);

    /// <summary>
    /// Isolate::StackOverflow: throws "RangeError: Maximum call stack size exceeded".
    /// </summary>
    [DoesNotReturn]
    public JSValue StackOverflow()
    {
        if (_inStackOverflow)
        {
            // Creating the RangeError overflowed again: throw the message string.
            throw new JavaScriptException(ReadOnlyRoots.empty_string, null);
        }
        _inStackOverflow = true;
        JSObject exception;
        try
        {
            JSFunction fun = NativeContext.RangeErrorFunction;
            JSValue msg = Factory.NewStringFromAsciiChecked(MessageFormatter.TemplateString(MessageTemplate.StackOverflow));
            exception = ErrorUtils.Construct(this, fun, fun, msg, JSValue.Undefined, FrameSkipMode.SKIP_NONE,
                JSValue.Undefined, ErrorUtils.StackTraceCollection.Enabled);
            JSObject.AddProperty(this, exception, ReadOnlyRoots.wasm_uncatchable_symbol, JSValue.True, PropertyAttributes.NONE);
        }
        finally
        {
            _inStackOverflow = false;
        }
        return Throw(exception);
    }

    /// <summary>
    /// Isolate::TerminateExecution: raises the uncatchable termination exception.
    /// Other threads use <see cref="StackGuard.RequestTerminateExecution"/>.
    /// </summary>
    [DoesNotReturn]
    public JSValue TerminateExecution() => throw new TerminationException();

    /// <summary>Requests termination from any thread (v8::Isolate::TerminateExecution).</summary>
    public void RequestTerminateExecution() => StackGuard.RequestTerminateExecution();

    /// <summary>v8::Isolate::CancelTerminateExecution.</summary>
    public void CancelTerminateExecution() => StackGuard.ClearTerminateExecution();

    /// <summary>Isolate::is_catchable_by_javascript.</summary>
    public static bool IsCatchableByJavaScript(Exception exception) => exception is JavaScriptException;

    // --- messages ----------------------------------------------------------------

    /// <summary>Isolate::ComputeLocation: the location of the top-most JavaScript frame.</summary>
    public bool ComputeLocation(out MessageLocation target)
    {
        target = default!;
        IJavaScriptFrames? frames = Frames;
        if (frames is null || !frames.TryGetFrame(0, out JavaScriptFrameSummary summary)) return false;
        SharedFunctionInfo shared = summary.Function.Shared;
        if (shared.Script is Script script && !script.Source.IsUndefined)
        {
            int pos = summary.SourcePosition;
            target = new MessageLocation(script, pos, pos + 1, shared);
            return true;
        }
        return false;
    }

    /// <summary>Isolate::ComputeLocationFromException: the location recorded by ThrowAt.</summary>
    public bool ComputeLocationFromException(out MessageLocation target, JSValue exception)
    {
        target = default!;
        if (exception.HeapObjectOrNull is not JSObject exceptionObject) return false;

        JSValue startPos = JSReceiver.GetDataProperty(this, exceptionObject, ReadOnlyRoots.error_start_pos_symbol);
        if (!startPos.IsSmi) return false;
        int startPosValue = (int)startPos.Number;

        JSValue endPos = JSReceiver.GetDataProperty(this, exceptionObject, ReadOnlyRoots.error_end_pos_symbol);
        if (!endPos.IsSmi) return false;
        int endPosValue = (int)endPos.Number;

        JSValue scriptValue = JSReceiver.GetDataProperty(this, exceptionObject, ReadOnlyRoots.error_script_symbol);
        if (scriptValue.HeapObjectOrNull is not Script script) return false;

        target = new MessageLocation(script, startPosValue, endPosValue);
        return true;
    }

    /// <summary>Isolate::ComputeLocationFromSimpleStackTrace: the first frame with a location.</summary>
    public bool ComputeLocationFromSimpleStackTrace(out MessageLocation target, JSValue exception)
    {
        target = default!;
        if (exception.HeapObjectOrNull is not JSReceiver receiver) return false;
        FixedArray callSiteInfos = GetSimpleStackTrace(receiver);
        for (int i = 0; i < callSiteInfos.Length; ++i)
        {
            var callSiteInfo = callSiteInfos[i].As<CallSiteInfo>();
            if (CallSiteInfo.ComputeLocation(this, callSiteInfo, out target)) return true;
        }
        return false;
    }

    /// <summary>Isolate::CreateMessage.</summary>
    public JSMessageObject CreateMessage(JSValue exception, MessageLocation? location)
    {
        MessageLocation? computedLocation = location;
        if (computedLocation is null &&
            (ComputeLocationFromException(out MessageLocation fromException, exception) ||
             ComputeLocationFromSimpleStackTrace(out fromException, exception) ||
             ComputeLocation(out fromException)))
        {
            computedLocation = fromException;
        }
        return MessageHandler.MakeMessageObject(this, MessageTemplate.UncaughtException, computedLocation, exception);
    }

    /// <summary>Isolate::CreateMessageOrAbort (--abort-on-uncaught-exception is not supported).</summary>
    public JSMessageObject CreateMessageOrAbort(JSValue exception, MessageLocation? location) =>
        CreateMessage(exception, location);

    // --- error stacks ----------------------------------------------------------

    /// <summary>Isolate::GetStackTraceLimit: Error.stackTraceLimit, if it is a number.</summary>
    public static bool GetStackTraceLimit(Isolate isolate, out int result)
    {
        result = 0;
        JSFunction error = isolate.NativeContext.ErrorFunction;
        JSValue stackTraceLimit = JSReceiver.GetDataProperty(isolate, error, ReadOnlyRoots.stackTraceLimit_string);
        if (!stackTraceLimit.IsNumber) return false;
        // Ensure that limit is not negative.
        result = Math.Max(ObjectOps.FastD2IChecked(stackTraceLimit.Number), 0);
        return true;
    }

    /// <summary>
    /// Isolate::CaptureAndSetErrorStack: captures the simple stack trace of the
    /// current frames and stores it in the error's private error_stack_symbol.
    /// </summary>
    public JSObject CaptureAndSetErrorStack(JSObject errorObject, FrameSkipMode mode, JSValue caller)
    {
        JSValue errorStack = JSValue.Undefined;
        // Capture the "simple stack trace" for the error.stack property,
        // which can be disabled by setting Error.stackTraceLimit to a non
        // number value or simply deleting the property.
        if (GetStackTraceLimit(this, out int stackTraceLimit))
        {
            errorStack = CaptureSimpleStackTrace(this, stackTraceLimit, mode, caller);
        }
        ObjectOps.SetProperty(this, errorObject, ReadOnlyRoots.error_stack_symbol, errorStack,
            StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        return errorObject;
    }

    /// <summary>Isolate::GetSimpleStackTrace: the CallSiteInfos of an error's captured stack.</summary>
    public FixedArray GetSimpleStackTrace(JSReceiver maybeErrorObject)
    {
        ErrorUtils.StackPropertyLookupResult lookup = ErrorUtils.GetErrorStackProperty(this, maybeErrorObject);
        if (lookup.ErrorStack.HeapObjectOrNull is FixedArray rawData) return rawData;
        if (lookup.ErrorStack.HeapObjectOrNull is ErrorStackData data && data.CallSiteInfos is not null) return data.CallSiteInfos;
        return FixedArray.Empty;
    }

    /// <summary>
    /// CaptureSimpleStackTrace (isolate.cc) with V8's CallSiteBuilder: walks
    /// the JavaScript frames, filters invisible ones and records CallSiteInfos.
    /// Deviation: V8 stores raw (receiver, function, code, offset, flags)
    /// tuples and builds CallSiteInfos lazily for deferred baseline frames;
    /// V8Sharp stores the CallSiteInfo objects directly.
    /// </summary>
    public static FixedArray CaptureSimpleStackTrace(Isolate isolate, int limit, FrameSkipMode mode, JSValue caller)
    {
        var builder = new CallSiteBuilder(isolate, mode, limit, caller);
        IJavaScriptFrames? frames = isolate.Frames;
        if (frames is not null)
        {
            for (int i = 0; !builder.Full && frames.TryGetFrame(i, out JavaScriptFrameSummary summary); i++)
            {
                builder.AppendJavaScriptFrame(summary);
            }
        }
        return builder.Build();
    }

    /// <summary>CallSiteBuilder (isolate.cc).</summary>
    struct CallSiteBuilder(Isolate isolate, FrameSkipMode mode, int limit, JSValue caller)
    {
        readonly List<JSValue> _elements = new(Math.Min(64, Math.Max(limit, 0)));
        bool _skipNextFrame = mode != FrameSkipMode.SKIP_NONE;
        bool _encounteredStrictFunction;

        public readonly bool Full => _elements.Count >= limit;

        public void AppendJavaScriptFrame(in JavaScriptFrameSummary summary)
        {
            // Filter out internal frames that we do not want to show.
            if (!IsVisibleInStackTrace(summary.Function)) return;

            int flags = 0;
            JSFunction function = summary.Function;
            if (IsStrictFrame(function)) flags |= CallSiteInfo.kIsStrict;
            if (summary.IsConstructor) flags |= CallSiteInfo.kIsConstructor;
            // The source position is resolved by the frame provider.
            flags |= CallSiteInfo.kIsSourcePositionComputed;
            _elements.Add(new CallSiteInfo(summary.Receiver, function, summary.SourcePosition, flags));
        }

        public readonly FixedArray Build() => _elements.Count == 0 ? FixedArray.Empty : new FixedArray(_elements.ToArray());

        // Poison stack frames below the first strict mode frame.
        // The stack trace API should not expose receivers and function
        // objects on frames deeper than the top-most one with a strict mode
        // function.
        bool IsStrictFrame(JSFunction function)
        {
            if (!_encounteredStrictFunction)
            {
                _encounteredStrictFunction = function.Shared.LanguageMode == LanguageMode.Strict;
            }
            return _encounteredStrictFunction;
        }

        // Determines whether the given stack frame should be displayed in a stack
        // trace.
        bool IsVisibleInStackTrace(JSFunction function) =>
            ShouldIncludeFrame(function) && IsNotHidden(function);

        // This mechanism excludes a number of uninteresting frames from the stack
        // trace. This can be be the first frame (which will be a builtin-exit frame
        // for the error constructor builtin) or every frame until encountering a
        // user-specified function.
        bool ShouldIncludeFrame(JSFunction function)
        {
            switch (mode)
            {
                case FrameSkipMode.SKIP_NONE:
                    return true;
                case FrameSkipMode.SKIP_FIRST:
                    if (!_skipNextFrame) return true;
                    _skipNextFrame = false;
                    return false;
                case FrameSkipMode.SKIP_UNTIL_SEEN:
                    if (_skipNextFrame && ReferenceEquals(function, caller.HeapObjectOrNull))
                    {
                        _skipNextFrame = false;
                        return false;
                    }
                    return !_skipNextFrame;
                default:
                    throw new InvalidOperationException("unreachable");
            }
        }

        readonly bool IsNotHidden(JSFunction function)
        {
            // Functions defined not in user scripts are not visible unless directly
            // exposed, in which case the native flag is set.
            // The --builtins-in-stack-traces command line flag allows including
            // internal call sites in the stack trace for debugging purposes.
            if (!isolate.Flags.builtins_in_stack_traces && !function.Shared.IsUserJavaScript())
            {
                return function.Shared.Native || function.Shared.IsApiFunction;
            }
            return true;
        }
    }

    /// <summary>v8::Isolate::UseCounterFeature is not reported; kept for call-site parity.</summary>
    public void CountUsage(string feature) { }
}
