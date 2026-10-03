// Port of the JavaScript-visible builtins of src/builtins/builtins-async-function-gen.cc
// (AsyncFunctionAwaitResolveClosure, AsyncFunctionAwaitRejectClosure) and
// src/builtins/builtins-async-generator-gen.cc (AsyncGeneratorPrototypeNext,
// Return and Throw, and the await/yield/return closures) and
// src/builtins/builtins-async-module.cc (CallAsyncModuleFulfilled and
// CallAsyncModuleRejected). The algorithms are the interpreter's
// (InterpreterAsync) and the module system's (SourceTextModule).
using V8Sharp.Interpreter;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterAsync()
    {
        Register(Builtin.AsyncFunctionAwaitResolveClosure, BuiltinsAsync.AsyncFunctionAwaitResolveClosure);
        Register(Builtin.AsyncFunctionAwaitRejectClosure, BuiltinsAsync.AsyncFunctionAwaitRejectClosure);
        Register(Builtin.AsyncGeneratorPrototypeNext, BuiltinsAsync.AsyncGeneratorPrototypeNext);
        Register(Builtin.AsyncGeneratorPrototypeReturn, BuiltinsAsync.AsyncGeneratorPrototypeReturn);
        Register(Builtin.AsyncGeneratorPrototypeThrow, BuiltinsAsync.AsyncGeneratorPrototypeThrow);
        Register(Builtin.AsyncGeneratorAwaitResolveClosure, BuiltinsAsync.AsyncGeneratorAwaitResolveClosure);
        Register(Builtin.AsyncGeneratorAwaitRejectClosure, BuiltinsAsync.AsyncGeneratorAwaitRejectClosure);
        Register(Builtin.AsyncGeneratorYieldWithAwaitResolveClosure, BuiltinsAsync.AsyncGeneratorYieldWithAwaitResolveClosure);
        Register(Builtin.AsyncGeneratorReturnResolveClosure, BuiltinsAsync.AsyncGeneratorReturnResolveClosure);
        Register(Builtin.AsyncGeneratorReturnClosedResolveClosure, BuiltinsAsync.AsyncGeneratorReturnClosedResolveClosure);
        Register(Builtin.AsyncGeneratorReturnClosedRejectClosure, BuiltinsAsync.AsyncGeneratorReturnClosedRejectClosure);
        Register(Builtin.CallAsyncModuleFulfilled, BuiltinsAsync.CallAsyncModuleFulfilled);
        Register(Builtin.CallAsyncModuleRejected, BuiltinsAsync.CallAsyncModuleRejected);
        Register(Builtin.AbstractModuleSourceToStringTag, BuiltinsAsync.AbstractModuleSourceToStringTag);
    }
}

/// <summary>The async function and async generator builtins.</summary>
public static class BuiltinsAsync
{
    // ---- builtins-async-function-gen.cc -------------------------------------------------------

    public static JSValue AsyncFunctionAwaitResolveClosure(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncFunctionAwaitResumeClosure(isolate,
            InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncFunctionObject>(args), args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kNext);

    public static JSValue AsyncFunctionAwaitRejectClosure(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncFunctionAwaitResumeClosure(isolate,
            InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncFunctionObject>(args), args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kThrow);

    // ---- builtins-async-generator-gen.cc ------------------------------------------------------

    /// <summary>https://tc39.github.io/proposal-async-iteration/#sec-asyncgenerator-prototype-next.</summary>
    public static JSValue AsyncGeneratorPrototypeNext(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorEnqueue(isolate, args.Receiver, args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kNext, "[AsyncGenerator].prototype.next");

    /// <summary>https://tc39.github.io/proposal-async-iteration/#sec-asyncgenerator-prototype-return.</summary>
    public static JSValue AsyncGeneratorPrototypeReturn(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorEnqueue(isolate, args.Receiver, args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kReturn, "[AsyncGenerator].prototype.return");

    /// <summary>https://tc39.github.io/proposal-async-iteration/#sec-asyncgenerator-prototype-throw.</summary>
    public static JSValue AsyncGeneratorPrototypeThrow(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorEnqueue(isolate, args.Receiver, args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kThrow, "[AsyncGenerator].prototype.throw");

    public static JSValue AsyncGeneratorAwaitResolveClosure(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorAwaitResume(isolate,
            InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncGeneratorObject>(args), args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kNext);

    public static JSValue AsyncGeneratorAwaitRejectClosure(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorAwaitResume(isolate,
            InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncGeneratorObject>(args), args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kRethrow);

    public static JSValue AsyncGeneratorYieldWithAwaitResolveClosure(Isolate isolate, in BuiltinArguments args)
    {
        JSAsyncGeneratorObject generator = InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncGeneratorObject>(args);
        generator.IsAwaiting = false;
        InterpreterAsync.AsyncGeneratorResolve(isolate, generator, args.AtOrUndefined(1), false);
        return InterpreterAsync.AsyncGeneratorResumeNext(isolate, generator);
    }

    /// <summary>
    /// AsyncGeneratorReturnResolveClosure: on resolve of the awaited value of a
    /// "return" request, resume the suspended generator with a return completion.
    /// </summary>
    public static JSValue AsyncGeneratorReturnResolveClosure(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorAwaitResume(isolate,
            InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncGeneratorObject>(args), args.AtOrUndefined(1),
            JSGeneratorObject.ResumeMode.kReturn);

    public static JSValue AsyncGeneratorReturnClosedResolveClosure(Isolate isolate, in BuiltinArguments args)
    {
        JSAsyncGeneratorObject generator = InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncGeneratorObject>(args);
        generator.IsAwaiting = false;
        // Return ! AsyncGeneratorResolve(_F_.[[Generator]], _value_, *true*).
        InterpreterAsync.AsyncGeneratorResolve(isolate, generator, args.AtOrUndefined(1), true);
        return InterpreterAsync.AsyncGeneratorResumeNext(isolate, generator);
    }

    public static JSValue AsyncGeneratorReturnClosedRejectClosure(Isolate isolate, in BuiltinArguments args) =>
        InterpreterAsync.AsyncGeneratorReturnClosedReject(isolate,
            InterpreterAsync.GeneratorOfAwaitClosure<JSAsyncGeneratorObject>(args), args.AtOrUndefined(1));

    // ---- builtins-async-module.cc -------------------------------------------------------------

    static SourceTextModule ModuleOfClosure(in BuiltinArguments args) =>
        args.Target.Context[SourceTextModule.kExecuteAsyncModuleContextModuleSlot].As<SourceTextModule>();

    public static JSValue CallAsyncModuleFulfilled(Isolate isolate, in BuiltinArguments args)
    {
        SourceTextModule.AsyncModuleExecutionFulfilled(isolate, ModuleOfClosure(args));
        return JSValue.Undefined;
    }

    public static JSValue CallAsyncModuleRejected(Isolate isolate, in BuiltinArguments args)
    {
        SourceTextModule.AsyncModuleExecutionRejected(isolate, ModuleOfClosure(args), args.AtOrUndefined(1));
        return JSValue.Undefined;
    }

    // ---- builtins-abstract-module-source.cc ---------------------------------------------------

    /// <summary>get %AbstractModuleSource%.prototype[@@toStringTag].</summary>
    public static JSValue AbstractModuleSourceToStringTag(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Let O be the this value.
        // 2. If O is not an Object, return undefined.
        // 3. Let sourceNameResult be Completion(HostGetModuleSourceName(O)).
        // 4. If sourceNameResult is an abrupt completion, return undefined.
        // V8Sharp has no WebAssembly, so no object has a module source name.
        return JSValue.Undefined;
    }
}
