// Port of src/execution/messages.{h,cc}: MessageLocation, JSMessageObject,
// MessageHandler, MessageFormatter (over the template table) and ErrorUtils
// (error construction, Error.prototype.toString, stack formatting and the
// "x is not a function"-style errors that use CallPrinter).
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's MessageLocation: a script range (or a bytecode offset) an error is reported at.</summary>
public sealed class MessageLocation
{
    public MessageLocation(Script script, int startPos, int endPos, SharedFunctionInfo? shared = null)
    {
        Script = script;
        StartPos = startPos;
        EndPos = endPos;
        BytecodeOffset = -1;
        Shared = shared;
    }

    public MessageLocation(Script script, SharedFunctionInfo shared, int bytecodeOffset)
    {
        Script = script;
        StartPos = -1;
        EndPos = -1;
        BytecodeOffset = bytecodeOffset;
        Shared = shared;
    }

    public Script Script { get; }
    public int StartPos { get; }
    public int EndPos { get; }
    public int BytecodeOffset { get; }
    public SharedFunctionInfo? Shared { get; }
}

/// <summary>V8's JSMessageObject: an error report (template, argument, script range, stack).</summary>
public sealed class JSMessageObject : HeapObject
{
    public JSMessageObject() : base(InstanceType.FixedArrayType) { }

    public MessageTemplate Type;
    public JSValue Argument;
    public Script Script = Factory.EmptyScript;
    public int StartPosition = -1;
    public int EndPosition = -1;
    public SharedFunctionInfo? SharedInfo;
    public int BytecodeOffset = -1;
    /// <summary>The detailed stack trace (inspector), when captured.</summary>
    public FixedArray? StackTrace;
    public int ErrorLevel = 1 << 3; // v8::Isolate::kMessageError

    public const int kNoLineNumberInfo = 0;
    public const int kNoColumnInfo = 0;

    /// <summary>JSMessageObject::GetLineNumber (1-based).</summary>
    public int GetLineNumber()
    {
        if (StartPosition == -1) return kNoLineNumberInfo;
        if (!Script.GetPositionInfo(StartPosition, out Script.PositionInfo info)) return kNoLineNumberInfo;
        return info.Line + 1;
    }

    /// <summary>JSMessageObject::GetColumnNumber. Note: No '+1' in contrast to GetLineNumber.</summary>
    public int GetColumnNumber()
    {
        if (StartPosition == -1) return -1;
        if (!Script.GetPositionInfo(StartPosition, out Script.PositionInfo info)) return -1;
        return info.Column;
    }

    public JSString GetSource() => Script.Source.HeapObjectOrNull as JSString ?? ReadOnlyRoots.empty_string;

    public JSString GetSourceLine(Isolate isolate)
    {
        if (!Script.GetPositionInfo(StartPosition, out Script.PositionInfo info)) return ReadOnlyRoots.empty_string;
        var src = Script.Source.As<JSString>();
        return isolate.Factory.NewSubString(src, info.LineStart, info.LineEnd);
    }
}

/// <summary>V8's MessageHandler.</summary>
public static class MessageHandler
{
    /// <summary>MessageHandler::MakeMessageObject.</summary>
    public static JSMessageObject MakeMessageObject(Isolate isolate, MessageTemplate message, MessageLocation? location,
        JSValue argument, FixedArray? stackTrace = null)
    {
        var result = new JSMessageObject { Type = message, Argument = argument, StackTrace = stackTrace };
        if (location is not null)
        {
            result.StartPosition = location.StartPos;
            result.EndPosition = location.EndPos;
            result.Script = location.Script;
            result.BytecodeOffset = location.BytecodeOffset;
            result.SharedInfo = location.Shared;
        }
        return result;
    }

    /// <summary>MessageHandler::GetMessage: the formatted message text.</summary>
    public static JSString GetMessage(Isolate isolate, JSMessageObject message) =>
        MessageFormatter.Format(isolate, message.Type, [message.Argument]);

    public static string GetLocalizedMessage(Isolate isolate, JSMessageObject message) =>
        GetMessage(isolate, message).ToCString();

    /// <summary>
    /// MessageHandler::ReportMessage for an uncaught exception. Without
    /// embedder message listeners this prints V8's default report.
    /// </summary>
    public static string DefaultMessageReport(Isolate isolate, MessageLocation? loc, JSMessageObject message)
    {
        string str = GetLocalizedMessage(isolate, message);
        if (loc is null) return str;
        string data = loc.Script.Name.HeapObjectOrNull is JSString s ? s.ToCString() : "<unknown>";
        return $"{data}:{loc.StartPos}: {str}";
    }
}

/// <summary>
/// The engine side of V8's MessageFormatter (messages.cc): formats a template
/// with JSValue arguments, each converted with NoSideEffectsToString. The
/// template table itself is V8Sharp.Common.MessageFormatter.
/// </summary>
public static class MessageFormatter
{
    public static string TemplateString(MessageTemplate index) => Common.MessageFormatter.TemplateString(index);

    /// <summary>MessageFormatter::Format.</summary>
    public static JSString Format(Isolate isolate, MessageTemplate index, ReadOnlySpan<JSValue> args)
    {
        const int kMaxArgs = 3;
        var argStrings = new string[Math.Min(args.Length, kMaxArgs)];
        for (int i = 0; i < argStrings.Length; ++i)
        {
            argStrings[i] = ObjectOps.NoSideEffectsToString(isolate, args[i]).ToCString();
        }
        string result;
        try
        {
            result = Common.MessageFormatter.Format(index, argStrings);
        }
        catch (JavaScriptException)
        {
            return isolate.Factory.InternalizeString("<error>");
        }
        return isolate.Factory.NewStringFromUtf16(result);
    }
}

/// <summary>V8's ErrorStackData: the raw call sites and, once computed, the formatted stack.</summary>
public sealed class ErrorStackData(FixedArray? callSiteInfos, JSValue formattedStack) : HeapObject(InstanceType.ErrorStackDataType)
{
    public FixedArray? CallSiteInfos = callSiteInfos;
    public JSValue FormattedStack = formattedStack;
    public bool HasFormattedStack => !FormattedStack.IsTheHole && !FormattedStack.IsUndefined;
}

/// <summary>V8's ErrorUtils.</summary>
public static class ErrorUtils
{
    public enum StackTraceCollection { Enabled, Disabled }

    public enum ToStringMessageSource { PreferOriginalMessage, CurrentMessageProperty }

    /// <summary>ErrorUtils::Construct for the Error constructors: skips frames up to new.target.</summary>
    public static JSObject Construct(Isolate isolate, JSFunction target, JSValue newTarget, JSValue message, JSValue options)
    {
        FrameSkipMode mode = FrameSkipMode.SKIP_FIRST;
        JSValue caller = JSValue.Undefined;

        // When we're passed a JSFunction as new target, we can skip frames until that
        // specific function is seen instead of unconditionally skipping the first
        // frame.
        if (newTarget.HeapObjectOrNull is JSFunction)
        {
            mode = FrameSkipMode.SKIP_UNTIL_SEEN;
            caller = newTarget;
        }
        return Construct(isolate, target, newTarget, message, options, mode, caller, StackTraceCollection.Enabled);
    }

    /// <summary>ErrorUtils::Construct (ES #sec-error-message).</summary>
    public static JSObject Construct(Isolate isolate, JSFunction target, JSValue newTarget, JSValue message,
        JSValue options, FrameSkipMode mode, JSValue caller, StackTraceCollection stackTraceCollection)
    {
        // 1. If NewTarget is undefined, let newTarget be the active function object,
        // else let newTarget be NewTarget.
        JSReceiver newTargetRecv = newTarget.HeapObjectOrNull as JSReceiver ?? target;

        // 2. Let O be ? OrdinaryCreateFromConstructor(newTarget, "%ErrorPrototype%",
        //    « [[ErrorData]] »).
        JSObject err = JSObject.New(isolate, target, newTargetRecv, null);

        // 3. If message is not undefined, then
        //  a. Let msg be ? ToString(message).
        //  b. Let msgDesc be the PropertyDescriptor{[[Value]]: msg, [[Writable]]:
        //     true, [[Enumerable]]: false, [[Configurable]]: true}.
        //  c. Perform ! DefinePropertyOrThrow(O, "message", msgDesc).
        // 4. Return O.
        if (!message.IsUndefined)
        {
            JSString msgString = ObjectOps.ToString(isolate, message);
            JSObject.SetOwnPropertyIgnoreAttributes(isolate, err, ReadOnlyRoots.message_string, msgString, PropertyAttributes.DONT_ENUM);
            if (isolate.Flags.use_original_message_for_stack_trace)
            {
                JSObject.SetOwnPropertyIgnoreAttributes(isolate, err, ReadOnlyRoots.error_message_symbol, msgString, PropertyAttributes.DONT_ENUM);
            }
        }

        if (!options.IsUndefined)
        {
            // If Type(options) is Object and ? HasProperty(options, "cause") then
            //   a. Let cause be ? Get(options, "cause").
            //   b. Perform ! CreateNonEnumerableDataPropertyOrThrow(O, "cause", cause).
            Name causeString = ReadOnlyRoots.cause_string;
            if (options.HeapObjectOrNull is JSReceiver jsOptions)
            {
                if (JSReceiver.HasProperty(isolate, jsOptions, causeString))
                {
                    JSValue cause = JSReceiver.GetProperty(isolate, jsOptions, causeString);
                    JSObject.SetOwnPropertyIgnoreAttributes(isolate, err, causeString, cause, PropertyAttributes.DONT_ENUM);
                }
            }
        }

        if (stackTraceCollection == StackTraceCollection.Enabled)
        {
            isolate.CaptureAndSetErrorStack(err, mode, caller);
        }
        return err;
    }

    static JSString GetStringPropertyOrDefault(Isolate isolate, JSReceiver recv, JSString key, JSString defaultStr)
    {
        JSValue obj = JSReceiver.GetProperty(isolate, recv, key);
        return obj.IsUndefined ? defaultStr : ObjectOps.ToString(isolate, obj);
    }

    /// <summary>ES6 section 19.5.3.4 Error.prototype.toString ( ).</summary>
    public static JSString ToString(Isolate isolate, JSValue receiver,
        ToStringMessageSource messageSource = ToStringMessageSource.CurrentMessageProperty)
    {
        // 1. Let O be the this value.
        // 2. If Type(O) is not Object, throw a TypeError exception.
        if (receiver.HeapObjectOrNull is not JSReceiver recv)
        {
            isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromAsciiChecked("Error.prototype.toString"), receiver);
            return null!;
        }
        // 3. Let name be ? Get(O, "name").
        // 4. If name is undefined, let name be "Error"; otherwise let name be
        // ? ToString(name).
        JSString name = GetStringPropertyOrDefault(isolate, recv, ReadOnlyRoots.name_string, ReadOnlyRoots.Error_string);

        // 5. Let msg be ? Get(O, "message").
        // 6. If msg is undefined, let msg be the empty String; otherwise let msg be
        // ? ToString(msg).
        JSString? msg = null;
        JSString msgDefault = ReadOnlyRoots.empty_string;
        if (messageSource == ToStringMessageSource.PreferOriginalMessage)
        {
            // V8-specific extension for Error.stack: Use the original message with
            // which the Error constructor was called. This keeps Error.stack consistent
            // w.r.t. "message" property changes regardless of the time when Error.stack
            // is accessed the first time.
            //
            // If |recv| was not constructed with %Error%, use the "message" property.
            var it = new LookupIterator(isolate, recv, ReadOnlyRoots.error_message_symbol, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
            JSValue result = JSReceiver.GetDataProperty(ref it);
            if (it.IsFound && result.IsUndefined) msg = msgDefault;
            else if (it.IsFound) msg = ObjectOps.ToString(isolate, result);
        }

        msg ??= GetStringPropertyOrDefault(isolate, recv, ReadOnlyRoots.message_string, msgDefault);

        // 7. If name is the empty String, return msg.
        // 8. If msg is the empty String, return name.
        if (name.Length == 0) return msg;
        if (msg.Length == 0) return name;

        // 9. Return the result of concatenating name, the code unit 0x003A (COLON),
        // the code unit 0x0020 (SPACE), and msg.
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendString(name);
        builder.AppendCStringLiteral(": ");
        builder.AppendString(msg);
        return builder.Finish();
    }

    /// <summary>ErrorUtils::MakeGenericError: an error of <paramref name="constructor"/> with a template message.</summary>
    public static JSObject MakeGenericError(Isolate isolate, JSFunction constructor, MessageTemplate index,
        ReadOnlySpan<JSValue> args, FrameSkipMode mode)
    {
        JSString msg = MessageFormatter.Format(isolate, index, args);
        // The call below can't fail because constructor is a builtin.
        return Construct(isolate, constructor, constructor, msg, JSValue.Undefined, mode, JSValue.Undefined, StackTraceCollection.Enabled);
    }

    // --- call-site rendering (RenderCallSite and friends) -----------------------

    static bool ComputeLocation(Isolate isolate, out MessageLocation location) => isolate.ComputeLocation(out location);

    static JSString BuildDefaultCallSite(Isolate isolate, JSValue obj)
    {
        var builder = new IncrementalStringBuilder(isolate);
        builder.AppendString(ObjectOps.TypeOf(isolate, obj));
        if (obj.HeapObjectOrNull is JSString s)
        {
            builder.AppendCStringLiteral(" \"");
            // This threshold must be sufficiently far below String::kMaxLength that
            // the {builder}'s result can never exceed that limit.
            const int kMaxPrintedStringLength = 100;
            builder.AppendStringCapped(s, kMaxPrintedStringLength);
            builder.AppendCStringLiteral("\"");
        }
        else if (obj.IsNull) builder.AppendCStringLiteral(" null");
        else if (obj.IsTrue) builder.AppendCStringLiteral(" true");
        else if (obj.IsFalse) builder.AppendCStringLiteral(" false");
        else if (obj.IsNumber)
        {
            builder.AppendCharacter(' ');
            builder.AppendString(isolate.Factory.NumberToString(obj));
        }
        return builder.Finish();
    }

    static JSString RenderCallSite(Isolate isolate, JSValue obj, out MessageLocation? location, out CallPrinterErrorHint hint)
    {
        hint = CallPrinterErrorHint.None;
        location = null;
        if (ComputeLocation(isolate, out MessageLocation computed))
        {
            location = computed;
            if (isolate.CallPrinter is ICallPrinter printer && computed.Shared is not null)
            {
                CallPrinterResult printed = printer.Print(isolate, computed.Shared, computed.StartPos, false);
                hint = printed.Hint;
                if (!string.IsNullOrEmpty(printed.CallSite)) return isolate.Factory.NewStringFromUtf16(printed.CallSite);
            }
        }
        return BuildDefaultCallSite(isolate, obj);
    }

    static MessageTemplate UpdateErrorTemplate(CallPrinterErrorHint hint, MessageTemplate defaultId) => hint switch
    {
        CallPrinterErrorHint.NormalIterator => MessageTemplate.NotIterable,
        CallPrinterErrorHint.CallAndNormalIterator => MessageTemplate.NotCallableOrIterable,
        CallPrinterErrorHint.AsyncIterator => MessageTemplate.NotAsyncIterable,
        CallPrinterErrorHint.CallAndAsyncIterator => MessageTemplate.NotCallableOrAsyncIterable,
        _ => defaultId,
    };

    /// <summary>ErrorUtils::NewIteratorError: "x is not iterable".</summary>
    public static JSObject NewIteratorError(Isolate isolate, JSValue source)
    {
        JSString callsite = RenderCallSite(isolate, source, out _, out CallPrinterErrorHint hint);
        MessageTemplate id = MessageTemplate.NotIterableNoSymbolLoad;
        if (hint == CallPrinterErrorHint.None)
        {
            return isolate.Factory.NewTypeError(id, callsite, ReadOnlyRoots.iterator_symbol);
        }
        id = UpdateErrorTemplate(hint, id);
        return isolate.Factory.NewTypeError(id, callsite);
    }

    /// <summary>ErrorUtils::ThrowSpreadArgError.</summary>
    public static JSValue ThrowSpreadArgError(Isolate isolate, MessageTemplate id, JSValue obj)
    {
        MessageLocation? location = null;
        JSString? callsite = null;
        if (ComputeLocation(isolate, out MessageLocation computed))
        {
            location = computed;
            if (isolate.CallPrinter is ICallPrinter printer && computed.Shared is not null)
            {
                CallPrinterResult printed = printer.Print(isolate, computed.Shared, computed.StartPos, true);
                callsite = !string.IsNullOrEmpty(printed.CallSite)
                    ? isolate.Factory.NewStringFromUtf16(printed.CallSite)
                    : BuildDefaultCallSite(isolate, obj);
                if (printed.SpreadArgPosition >= 0)
                {
                    // Change the message location to point at the property name.
                    int pos = printed.SpreadArgPosition;
                    location = new MessageLocation(computed.Script, pos, pos + 1, computed.Shared);
                }
            }
            else
            {
                callsite = BuildDefaultCallSite(isolate, obj);
            }
        }
        callsite ??= BuildDefaultCallSite(isolate, obj);
        JSObject error = isolate.Factory.NewTypeError(id, callsite, obj);
        if (location is not null) return isolate.ThrowAt(error, location);
        return isolate.Throw(error);
    }

    /// <summary>ErrorUtils::NewCalledNonCallableError: "x is not a function".</summary>
    public static JSObject NewCalledNonCallableError(Isolate isolate, JSValue source)
    {
        JSString callsite = RenderCallSite(isolate, source, out _, out CallPrinterErrorHint hint);
        MessageTemplate id = UpdateErrorTemplate(hint, MessageTemplate.CalledNonCallable);
        return isolate.Factory.NewTypeError(id, callsite);
    }

    /// <summary>ErrorUtils::NewConstructedNonConstructable: "x is not a constructor".</summary>
    public static JSObject NewConstructedNonConstructable(Isolate isolate, JSValue source)
    {
        JSString callsite = RenderCallSite(isolate, source, out _, out _);
        return isolate.Factory.NewTypeError(MessageTemplate.NotConstructor, callsite);
    }

    /// <summary>
    /// ErrorUtils::ThrowLoadFromNullOrUndefined: "Cannot read properties of
    /// undefined (reading 'x')" and the destructuring variants.
    /// </summary>
    public static JSValue ThrowLoadFromNullOrUndefined(Isolate isolate, JSValue obj, JSValue? key)
    {
        JSString? maybePropertyName = null;

        // Try to extract the property name from the given key, if any.
        if (key is JSValue keyValue)
        {
            maybePropertyName = keyValue.HeapObjectOrNull is JSString keyString
                ? keyString
                : ObjectOps.NoSideEffectsToMaybeString(isolate, keyValue);
        }

        JSString? callsite = null;

        // Inline the RenderCallSite logic here so that we can additionally access the
        // destructuring property.
        bool locationComputed = false;
        bool isDestructuring = false;
        MessageLocation? location = null;
        if (ComputeLocation(isolate, out MessageLocation computed))
        {
            locationComputed = true;
            location = computed;
            if (isolate.CallPrinter is ICallPrinter printer && computed.Shared is not null)
            {
                CallPrinterResult printed = printer.Print(isolate, computed.Shared, computed.StartPos, false);
                int pos = -1;
                isDestructuring = printed.IsDestructuring;
                if (isDestructuring)
                {
                    // If we don't have one yet, try to extract the property name from the
                    // destructuring property in the AST.
                    if (maybePropertyName is null && printed.DestructuringPropertyName is not null)
                    {
                        maybePropertyName = isolate.Factory.InternalizeString(printed.DestructuringPropertyName);
                        // Change the message location to point at the property name.
                        pos = printed.DestructuringPropertyPosition;
                    }
                    if (maybePropertyName is null)
                    {
                        // Change the message location to point at the destructured value.
                        pos = printed.DestructuringValuePosition;
                    }
                    // If we updated the pos to a valid pos, rewrite the location.
                    if (pos != -1)
                    {
                        location = new MessageLocation(computed.Script, pos, pos + 1, computed.Shared);
                    }
                }
                if (!string.IsNullOrEmpty(printed.CallSite)) callsite = isolate.Factory.NewStringFromUtf16(printed.CallSite);
            }
        }

        callsite ??= BuildDefaultCallSite(isolate, obj);

        JSObject error;
        if (isDestructuring)
        {
            error = maybePropertyName is not null
                ? isolate.Factory.NewTypeError(MessageTemplate.NonCoercibleWithProperty, maybePropertyName, callsite, obj)
                : isolate.Factory.NewTypeError(MessageTemplate.NonCoercible, callsite, obj);
        }
        else
        {
            if (key is null || maybePropertyName is null)
            {
                error = isolate.Factory.NewTypeError(MessageTemplate.NonObjectPropertyLoad, obj);
            }
            else if (ReferenceEquals(key.Value.HeapObjectOrNull, ReadOnlyRoots.iterator_symbol))
            {
                error = NewIteratorError(isolate, obj);
            }
            else
            {
                error = isolate.Factory.NewTypeError(MessageTemplate.NonObjectPropertyLoadWithProperty, obj, maybePropertyName);
            }
        }

        if (locationComputed) return isolate.ThrowAt(error, location!);
        return isolate.Throw(error);
    }

    // --- error.stack --------------------------------------------------------------

    public readonly record struct StackPropertyLookupResult(JSObject? ErrorStackSymbolHolder, JSValue ErrorStack);

    /// <summary>ErrorUtils::GetErrorStackProperty.</summary>
    public static StackPropertyLookupResult GetErrorStackProperty(Isolate isolate, JSReceiver maybeErrorObject)
    {
        var it = new LookupIterator(isolate, maybeErrorObject, ReadOnlyRoots.error_stack_symbol,
            LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        JSValue result = JSReceiver.GetDataProperty(ref it);
        if (!it.IsFound) return new StackPropertyLookupResult(null, JSValue.Undefined);
        return new StackPropertyLookupResult(it.GetHolder<JSObject>(), result);
    }

    /// <summary>ErrorUtils::HasErrorStackSymbolOwnProperty.</summary>
    public static bool HasErrorStackSymbolOwnProperty(Isolate isolate, JSObject obj) =>
        JSReceiver.HasOwnProperty(isolate, obj, ReadOnlyRoots.error_stack_symbol);

    /// <summary>ErrorUtils::GetFormattedStack: formats (once) and returns error.stack.</summary>
    public static JSValue GetFormattedStack(Isolate isolate, JSObject maybeErrorObject)
    {
        StackPropertyLookupResult lookup = GetErrorStackProperty(isolate, maybeErrorObject);

        if (lookup.ErrorStack.HeapObjectOrNull is ErrorStackData errorStackData)
        {
            if (errorStackData.HasFormattedStack) return errorStackData.FormattedStack;
            JSValue formatted = FormatStackTrace(isolate, lookup.ErrorStackSymbolHolder!, errorStackData.CallSiteInfos ?? FixedArray.Empty);
            errorStackData.FormattedStack = formatted;
            return formatted;
        }

        if (lookup.ErrorStack.HeapObjectOrNull is FixedArray callSiteInfos)
        {
            JSObject errorObject = lookup.ErrorStackSymbolHolder!;
            JSValue formattedStack = FormatStackTrace(isolate, errorObject, callSiteInfos);
            ObjectOps.SetProperty(isolate, errorObject, ReadOnlyRoots.error_stack_symbol, formattedStack,
                StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
            return formattedStack;
        }

        return lookup.ErrorStack;
    }

    /// <summary>ErrorUtils::SetFormattedStack.</summary>
    public static void SetFormattedStack(Isolate isolate, JSObject maybeErrorObject, JSValue formattedStack)
    {
        StackPropertyLookupResult lookup = GetErrorStackProperty(isolate, maybeErrorObject);
        // Do nothing in case |maybe_error_object| is not an Error, i.e. its
        // prototype doesn't contain objects with |error_stack_symbol| property.
        if (lookup.ErrorStackSymbolHolder is not JSObject errorObject) return;
        if (lookup.ErrorStack.HeapObjectOrNull is ErrorStackData data)
        {
            data.FormattedStack = formattedStack;
        }
        else
        {
            ObjectOps.SetProperty(isolate, errorObject, ReadOnlyRoots.error_stack_symbol, formattedStack,
                StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
        }
    }

    /// <summary>ErrorUtils::CaptureStackTrace (Error.captureStackTrace).</summary>
    public static JSValue CaptureStackTrace(Isolate isolate, JSObject obj, FrameSkipMode mode, JSValue caller)
    {
        Name name = ReadOnlyRoots.stack_string;

        // Explicitly check for frozen objects to simplify things since we need to
        // add both "stack" and "error_stack_symbol" properties in one go.
        if (!JSObject.IsExtensible(isolate, obj))
        {
            return isolate.ThrowTypeError(MessageTemplate.DefineDisallowed, name);
        }

        // Add the stack accessors.
        var desc = new PropertyDescriptor();
        desc.SetEnumerable(false);
        desc.SetConfigurable(true);
        desc.SetGet(isolate.NativeContext.ErrorStackGetterFunTemplate());
        desc.SetSet(isolate.NativeContext.ErrorStackSetterFunTemplate());
        JSReceiver.DefineOwnProperty(isolate, obj, name, ref desc, ShouldThrow.ThrowOnError);

        // Collect the stack trace and store it in |object|'s private
        // "error_stack_symbol" property.
        isolate.CaptureAndSetErrorStack(obj, mode, caller);
        return JSValue.Undefined;
    }

    /// <summary>
    /// ErrorUtils::FormatStackTrace: Error.prepareStackTrace (or the embedder
    /// callback) if present, else "ErrorString\n    at frame..." as V8 prints it.
    /// </summary>
    public static JSValue FormatStackTrace(Isolate isolate, JSObject error, FixedArray callSiteInfos)
    {
        bool inRecursion = isolate.FormattingStackTrace;
        bool hasOverflowed = StackGuard.HasOverflowed();
        NativeContext? errorContext = error.GetCreationContext();
        if (!inRecursion && !hasOverflowed && errorContext is not null)
        {
            if (isolate.PrepareStackTraceCallback is { } callback)
            {
                isolate.FormattingStackTrace = true;
                try
                {
                    JSArray sites = GetStackFrames(isolate, callSiteInfos);
                    return callback(isolate, errorContext, error, sites);
                }
                finally
                {
                    isolate.FormattingStackTrace = false;
                }
            }
            JSFunction globalError = errorContext.ErrorFunction;

            // If there's a user-specified "prepareStackTrace" function, call it on
            // the frames and use its result.
            JSValue prepareStackTrace = JSReceiver.GetProperty(isolate, globalError, isolate.Factory.InternalizeString("prepareStackTrace"));
            if (prepareStackTrace.HeapObjectOrNull is JSFunction)
            {
                isolate.FormattingStackTrace = true;
                try
                {
                    JSArray sites = GetStackFrames(isolate, callSiteInfos);
                    JSValue receiverArg = error is JSGlobalObject global ? JSValue.FromObject(global.GlobalProxy) : error;
                    return Execution.Call(isolate, prepareStackTrace, globalError, [receiverArg, sites]);
                }
                finally
                {
                    isolate.FormattingStackTrace = false;
                }
            }
        }

        // Otherwise, run our internal formatting logic.
        var builder = new IncrementalStringBuilder(isolate);
        AppendErrorString(isolate, error, ref builder);

        for (int i = 0; i < callSiteInfos.Length; ++i)
        {
            builder.AppendCStringLiteral("\n    at ");
            var frame = callSiteInfos[i].As<CallSiteInfo>();
            try
            {
                CallSiteInfo.SerializeCallSiteInfo(isolate, frame, ref builder);
            }
            catch (JavaScriptException e)
            {
                // CallSite.toString threw. Parts of the current frame might have been
                // stringified already regardless. Still, try to append a string
                // representation of the thrown exception.
                try
                {
                    JSString exceptionString = ToString(isolate, e.Value);
                    builder.AppendCStringLiteral("<error: ");
                    builder.AppendString(exceptionString);
                    builder.AppendCStringLiteral("<error>");
                }
                catch (JavaScriptException)
                {
                    // Formatting the thrown exception threw again, give up.
                    builder.AppendCStringLiteral("<error>");
                }
            }
        }
        return builder.Finish();
    }

    static void AppendErrorString(Isolate isolate, JSValue error, ref IncrementalStringBuilder builder)
    {
        try
        {
            builder.AppendString(ToString(isolate, error, ToStringMessageSource.PreferOriginalMessage));
        }
        catch (JavaScriptException e)
        {
            // Error.toString threw. Try to return a string representation of the thrown
            // exception instead.
            try
            {
                JSString errStr = ToString(isolate, e.Value, ToStringMessageSource.PreferOriginalMessage);
                // Formatted thrown exception successfully, append it.
                builder.AppendCStringLiteral("<error: ");
                builder.AppendString(errStr);
                builder.AppendCharacter('>');
            }
            catch (JavaScriptException)
            {
                // Formatting the thrown exception threw again, give up.
                builder.AppendCStringLiteral("<error>");
            }
        }
    }

    // Convert the CallSiteInfos into a JSArray of JSCallSite objects.
    static JSArray GetStackFrames(Isolate isolate, FixedArray callSiteInfos)
    {
        int frameCount = callSiteInfos.Length;
        JSFunction constructor = isolate.NativeContext.CallSiteFunction;
        var sites = new FixedArray(frameCount);
        for (int i = 0; i < frameCount; ++i)
        {
            JSObject site = JSObject.New(isolate, constructor, constructor, null);
            JSObject.SetOwnPropertyIgnoreAttributes(isolate, site, ReadOnlyRoots.call_site_info_symbol, callSiteInfos[i], PropertyAttributes.DONT_ENUM);
            sites[i] = site;
        }
        return isolate.Factory.NewJSArrayWithElements(sites);
    }
}
