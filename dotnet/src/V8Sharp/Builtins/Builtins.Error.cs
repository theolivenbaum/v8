// Port of the Error builtins: src/builtins/builtins-error.cc (Error,
// Error.captureStackTrace, Error.prototype.toString, Error.isError),
// aggregate-error.tq with Runtime_ConstructAggregateErrorHelper and
// suppressed-error.tq with Runtime_ConstructSuppressedError
// (src/runtime/runtime-promise.cc). The error machinery itself (construction,
// stack capture and formatting, prepareStackTrace) is ErrorUtils in
// Execution/Messages.cs, ported from src/execution/messages.cc. The CallSite
// methods are in Builtins.Error.CallSite.cs.
namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterError()
    {
        Register(Builtin.ErrorConstructor, BuiltinsError.ErrorConstructor);
        Register(Builtin.ErrorCaptureStackTrace, BuiltinsError.ErrorCaptureStackTrace);
        Register(Builtin.ErrorPrototypeToString, BuiltinsError.ErrorPrototypeToString);
        Register(Builtin.ErrorIsError, BuiltinsError.ErrorIsError);
        Register(Builtin.AggregateErrorConstructor, BuiltinsError.AggregateErrorConstructor);
        Register(Builtin.SuppressedErrorConstructor, BuiltinsError.SuppressedErrorConstructor);
        RegisterCallSite();
    }
}

/// <summary>The Error builtins.</summary>
public static class BuiltinsError
{
    /// <summary>ES6 section 19.5.1.1 Error ( message ).</summary>
    public static JSValue ErrorConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSValue options = args.AtOrUndefined(2);
        return ErrorUtils.Construct(isolate, args.Target, args.NewTarget, args.AtOrUndefined(1), options);
    }

    /// <summary>Error.captureStackTrace ( object [ , constructor ] ).</summary>
    public static JSValue ErrorCaptureStackTrace(Isolate isolate, in BuiltinArguments args)
    {
        JSValue objectObj = args.AtOrUndefined(1);

        isolate.CountUsage("kErrorCaptureStackTrace");

        if (objectObj.HeapObjectOrNull is not JSObject obj)
        {
            return isolate.ThrowTypeError(MessageTemplate.InvalidArgument, objectObj);
        }

        JSValue caller = args.AtOrUndefined(2);
        FrameSkipMode mode = caller.HeapObjectOrNull is JSFunction ? FrameSkipMode.SKIP_UNTIL_SEEN : FrameSkipMode.SKIP_FIRST;

        // Collect the stack trace and install the stack accessors.
        ErrorUtils.CaptureStackTrace(isolate, obj, mode, caller);
        return JSValue.Undefined;
    }

    /// <summary>ES6 section 19.5.3.4 Error.prototype.toString ( ).</summary>
    public static JSValue ErrorPrototypeToString(Isolate isolate, in BuiltinArguments args) =>
        ErrorUtils.ToString(isolate, args.Receiver);

    /// <summary>https://tc39.es/proposal-is-error/</summary>
    public static JSValue ErrorIsError(Isolate isolate, in BuiltinArguments args)
    {
        JSValue obj = args.AtOrUndefined(1);

        isolate.CountUsage("kErrorIsError");

        // 1. If argument is not an Object, return false.
        // 2. If argument has an [[ErrorData]] internal slot, return true.
        // 3. Return false.
        // (V8 also answers true for DOMExceptions, which are API wrapper
        // objects; V8Sharp has no API wrappers.)
        return JSValue.FromBoolean(obj.HeapObjectOrNull is JSReceiver receiver && receiver.Map.InstanceType == InstanceType.JSErrorType);
    }

    /// <summary>AggregateError ( errors, message [ , options ] ) (aggregate-error.tq).</summary>
    public static JSValue AggregateErrorConstructor(Isolate isolate, in BuiltinArguments args)
    {
        // 1. If NewTarget is undefined, let newTarget be the active function
        // object, else let newTarget be NewTarget.
        // 2. Let O be ? OrdinaryCreateFromConstructor(newTarget,
        // "%AggregateError.prototype%", « [[ErrorData]], [[AggregateErrors]] »).
        // 3. If _message_ is not _undefined_, then
        //   a. Let msg be ? ToString(_message_).
        //   b. Let msgDesc be the PropertyDescriptor { [[Value]]: _msg_,
        //   [[Writable]]: *true*, [[Enumerable]]: *false*, [[Configurable]]: *true*
        //   c. Perform ! DefinePropertyOrThrow(_O_, *"message"*, _msgDesc_).
        JSValue message = args.AtOrUndefined(2);
        JSValue options = args.AtOrUndefined(3);
        // Runtime_ConstructAggregateErrorHelper.
        JSObject obj = ErrorUtils.Construct(isolate, args.Target, args.NewTarget, message, options);

        // 4. Let errorsList be ? IterableToList(errors).
        JSValue errors = args.AtOrUndefined(1);
        JSArray errorsList = IteratorHelpers.IterableToListWithSymbolLookup(isolate, errors);

        // 5. Perform ! DefinePropertyOrThrow(_O_, `"errors"`, Property Descriptor {
        // [[Configurable]]: *true*, [[Enumerable]]: *false*, [[Writable]]: *true*,
        // [[Value]]: ! CreateArrayFromList(_errorsList_) }).
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, obj, ReadOnlyRoots.errors_string, errorsList, PropertyAttributes.DONT_ENUM);

        // 6. Return O.
        return obj;
    }

    /// <summary>SuppressedError ( error, suppressed, message ) (suppressed-error.tq).</summary>
    public static JSValue SuppressedErrorConstructor(Isolate isolate, in BuiltinArguments args)
    {
        JSValue error = args.AtOrUndefined(1);
        JSValue suppressed = args.AtOrUndefined(2);
        JSValue message = args.AtOrUndefined(3);

        // 1. If NewTarget is undefined, let newTarget be the active function object;
        // else let newTarget be NewTarget.
        // 2. Let O be ? OrdinaryCreateFromConstructor(newTarget,
        // "%SuppressedError.prototype%", « [[ErrorData]] »).
        // 3. If message is not undefined, then
        //    a. Let messageString be ? ToString(message).
        //    b. Perform CreateNonEnumerableDataPropertyOrThrow(O, "message",
        //    messageString).
        // Runtime_ConstructSuppressedError.
        JSObject obj = ErrorUtils.Construct(isolate, args.Target, args.NewTarget, message, JSValue.Undefined);

        // 4. Perform CreateNonEnumerableDataPropertyOrThrow(O, "error", error).
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, obj, ReadOnlyRoots.error_string, error, PropertyAttributes.DONT_ENUM);

        // 5. Perform CreateNonEnumerableDataPropertyOrThrow(O, "suppressed",
        // suppressed).
        JSObject.SetOwnPropertyIgnoreAttributes(isolate, obj, ReadOnlyRoots.suppressed_string, suppressed,
            PropertyAttributes.DONT_ENUM);

        // 6. Return O.
        return obj;
    }
}
