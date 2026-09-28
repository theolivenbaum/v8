// The async function and async generator intrinsics of the interpreter
// (builtins-async-function-gen.cc, builtins-async-generator-gen.cc,
// builtins-async-gen.cc Await) and CreateAsyncFromSyncIterator.
//
// They need the promise machinery (NewJSPromise, PerformPromiseThen,
// ResolvePromise, RejectPromise), which the promise builtins port provides.
using V8Sharp.Runtime;

namespace V8Sharp.Interpreter;

public static partial class InterpreterAsync
{
    static JSValue NotSupported(string what) =>
        throw new NotSupportedException("V8Sharp: " + what + " needs the promise builtins, which are not ported yet");

    public static JSValue CreateAsyncFromSyncIterator(Isolate isolate, JSValue syncIterator) =>
        NotSupported("CreateAsyncFromSyncIterator");

    public static JSValue AsyncFunctionEnter(Isolate isolate, JSFunction closure, JSValue receiver) =>
        NotSupported("async functions");

    public static JSValue AsyncFunctionAwait(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject, JSValue value) =>
        NotSupported("await");

    public static JSValue AsyncFunctionResolve(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject, JSValue value) =>
        NotSupported("async functions");

    public static JSValue AsyncFunctionReject(Isolate isolate, JSAsyncFunctionObject asyncFunctionObject, JSValue reason) =>
        NotSupported("async functions");

    public static JSValue AsyncGeneratorAwait(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value) =>
        NotSupported("async generators");

    public static JSValue AsyncGeneratorResolve(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value, bool done) =>
        NotSupported("async generators");

    public static JSValue AsyncGeneratorReject(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value) =>
        NotSupported("async generators");

    public static JSValue AsyncGeneratorYieldWithAwait(Isolate isolate, JSAsyncGeneratorObject generator, JSValue value) =>
        NotSupported("async generators");
}
