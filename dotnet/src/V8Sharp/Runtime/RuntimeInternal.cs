// Port of the runtime functions of src/runtime/runtime-internal.cc and
// src/runtime/runtime-symbol.cc that bytecode calls: the error constructors
// and throwers (NewTypeError, ThrowIteratorResultNotAnObject ...), Abort,
// the private symbol factories and GetTemplateObject.
using V8Sharp.Codegen;

namespace V8Sharp.Runtime;

public static class RuntimeInternal
{
    /// <summary>NewError (runtime-internal.cc): an error of the constructor with up to three message arguments.</summary>
    static JSObject NewError(Isolate isolate, ReadOnlySpan<JSValue> args, JSFunction constructor)
    {
        var messageId = (MessageTemplate)(int)args[0].Number;
        const int kMaxMessageArgs = 3;
        int count = Math.Min(kMaxMessageArgs, args.Length - 1);
        return isolate.Factory.NewError(constructor, messageId, args.Slice(1, count));
    }

    /// <summary>Runtime_NewTypeError.</summary>
    public static JSValue NewTypeError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        NewError(isolate, args, isolate.NativeContext.TypeErrorFunction);

    /// <summary>Runtime_NewReferenceError.</summary>
    public static JSValue NewReferenceError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        NewError(isolate, args, isolate.NativeContext.ReferenceErrorFunction);

    /// <summary>Runtime_NewSyntaxError.</summary>
    public static JSValue NewSyntaxError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        NewError(isolate, args, isolate.NativeContext.SyntaxErrorFunction);

    /// <summary>Runtime_NewError.</summary>
    public static JSValue NewPlainError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        NewError(isolate, args, isolate.NativeContext.ErrorFunction);

    /// <summary>Runtime_ThrowTypeError / ThrowRangeError / ThrowReferenceError ...</summary>
    public static JSValue ThrowTypeError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        isolate.Throw(NewError(isolate, args, isolate.NativeContext.TypeErrorFunction));

    public static JSValue ThrowRangeError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        isolate.Throw(NewError(isolate, args, isolate.NativeContext.RangeErrorFunction));

    public static JSValue ThrowReferenceErrorWithTemplate(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        isolate.Throw(NewError(isolate, args, isolate.NativeContext.ReferenceErrorFunction));

    public static JSValue ThrowSyntaxError(Isolate isolate, ReadOnlySpan<JSValue> args) =>
        isolate.Throw(NewError(isolate, args, isolate.NativeContext.SyntaxErrorFunction));

    /// <summary>Runtime_ThrowReferenceError: "x is not defined".</summary>
    public static JSValue ThrowReferenceError(Isolate isolate, JSValue name) =>
        isolate.ThrowReferenceError(MessageTemplate.NotDefined, name);

    /// <summary>Runtime_ThrowIteratorResultNotAnObject.</summary>
    public static JSValue ThrowIteratorResultNotAnObject(Isolate isolate, JSValue value) =>
        isolate.ThrowTypeError(MessageTemplate.IteratorResultNotAnObject, value);

    /// <summary>Runtime_ThrowThrowMethodMissing.</summary>
    public static JSValue ThrowThrowMethodMissing(Isolate isolate) => isolate.ThrowTypeError(MessageTemplate.ThrowMethodMissing);

    /// <summary>Runtime_ThrowSymbolIteratorInvalid.</summary>
    public static JSValue ThrowSymbolIteratorInvalid(Isolate isolate) =>
        isolate.ThrowTypeError(MessageTemplate.SymbolIteratorInvalid);

    /// <summary>Runtime_ThrowSymbolAsyncIteratorInvalid.</summary>
    public static JSValue ThrowSymbolAsyncIteratorInvalid(Isolate isolate) =>
        isolate.ThrowTypeError(MessageTemplate.SymbolAsyncIteratorInvalid);

    /// <summary>Runtime_ThrowPatternAssignmentNonCoercible.</summary>
    public static JSValue ThrowPatternAssignmentNonCoercible(Isolate isolate, JSValue obj) =>
        ErrorUtils.ThrowLoadFromNullOrUndefined(isolate, obj, null);

    /// <summary>Runtime_ThrowConstructorReturnedNonObject.</summary>
    public static JSValue ThrowConstructorReturnedNonObject(Isolate isolate) =>
        isolate.ThrowTypeError(MessageTemplate.DerivedConstructorReturnedNonObject);

    /// <summary>Runtime_ThrowUsingAssignError.</summary>
    public static JSValue ThrowUsingAssignError(Isolate isolate) => isolate.ThrowTypeError(MessageTemplate.UsingAssign);

    /// <summary>Runtime_ThrowAwaitUsingAssignError.</summary>
    public static JSValue ThrowAwaitUsingAssignError(Isolate isolate) => isolate.ThrowTypeError(MessageTemplate.AwaitUsingAssign);

    /// <summary>Runtime_ThrowCalledNonCallable.</summary>
    public static JSValue ThrowCalledNonCallable(Isolate isolate, JSValue obj) =>
        isolate.Throw(ErrorUtils.NewCalledNonCallableError(isolate, obj));

    /// <summary>Runtime_ThrowConstructedNonConstructable.</summary>
    public static JSValue ThrowConstructedNonConstructable(Isolate isolate, JSValue obj) =>
        isolate.Throw(ErrorUtils.NewConstructedNonConstructable(isolate, obj));

    /// <summary>Runtime_ThrowIteratorError.</summary>
    public static JSValue ThrowIteratorError(Isolate isolate, JSValue obj) => isolate.Throw(ErrorUtils.NewIteratorError(isolate, obj));

    /// <summary>Runtime_ThrowSpreadArgError.</summary>
    public static JSValue ThrowSpreadArgError(Isolate isolate, JSValue messageId, JSValue obj) =>
        ErrorUtils.ThrowSpreadArgError(isolate, (MessageTemplate)(int)messageId.Number, obj);

    /// <summary>Runtime_CreatePrivateNameSymbol.</summary>
    public static JSValue CreatePrivateNameSymbol(Isolate isolate, JSValue name) =>
        isolate.Factory.NewPrivateNameSymbol(name.As<JSString>());

    /// <summary>Runtime_CreatePrivateBrandSymbol.</summary>
    public static JSValue CreatePrivateBrandSymbol(Isolate isolate, JSValue name) =>
        new Symbol(name.As<JSString>()) { PrivateSymbolKind = PrivateSymbolKind.Brand };

    /// <summary>
    /// The Abort bytecode (Runtime_AbortJS / the abort builtin): V8 prints the
    /// reason and aborts the process; V8Sharp throws a fatal .NET exception.
    /// </summary>
    public static void Abort(Isolate isolate, int reason) =>
        throw new InvalidOperationException("abort: " + AbortReasons.GetAbortReason((AbortReason)reason));

    /// <summary>Runtime_AbortJS: prints the message; V8 aborts unless --disable-abortjs.</summary>
    public static JSValue AbortJS(Isolate isolate, JSValue message)
    {
        if (isolate.Flags.disable_abortjs)
        {
            isolate.StdOut.WriteLine("[disabled] abort: " + message.As<JSString>().ToString());
            return JSValue.Undefined;
        }
        throw new InvalidOperationException("abort: " + message.As<JSString>().ToString());
    }
}
