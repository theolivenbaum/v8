// Port of src/interpreter/interpreter-intrinsics-generator.cc: the
// InvokeIntrinsic bytecode's dispatch over the interpreter intrinsics
// (INTRINSICS_LIST). Most intrinsics call a builtin; those are the C#
// implementations here and in InterpreterAsync.
using V8Sharp.Runtime;

namespace V8Sharp.Interpreter;

public static class InterpreterIntrinsicsDispatch
{
    /// <summary>InvokeIntrinsic: IntrinsicsGenerator::InvokeIntrinsic.</summary>
    public static JSValue Invoke(Isolate isolate, IntrinsicsHelper.IntrinsicId id, ReadOnlySpan<JSValue> args)
    {
        switch (id)
        {
            case IntrinsicsHelper.IntrinsicId.CopyDataProperties:
                // Builtin::kCopyDataProperties.
                return RuntimeObject.CopyDataProperties(isolate, args[0], args[1], useSet: false);
            case IntrinsicsHelper.IntrinsicId.CopyDataPropertiesWithExcludedPropertiesOnStack:
                return RuntimeObject.CopyDataPropertiesWithExcludedProperties(isolate, args[0], args[1..]);
            case IntrinsicsHelper.IntrinsicId.CreateIterResultObject:
                return InterpreterRuntime.NewJSIteratorResult(isolate, args[0], ObjectOps.BooleanValue(args[1]));
            case IntrinsicsHelper.IntrinsicId.GeneratorYieldResult:
                // V8 skips the allocation when an optimized caller set yielded_value to
                // the hole; interpreted callers always get the iterator result.
                return InterpreterRuntime.NewJSIteratorResult(isolate, args[0], false);
            case IntrinsicsHelper.IntrinsicId.CreateJSGeneratorObject:
                return InterpreterGenerators.CreateJSGeneratorObject(isolate, args[0].As<JSFunction>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.GeneratorGetResumeMode:
                return JSValue.FromInt((int)args[0].As<JSGeneratorObject>().Mode);
            case IntrinsicsHelper.IntrinsicId.GeneratorClose:
                args[0].As<JSGeneratorObject>().ContinuationValue = JSGeneratorObject.kGeneratorClosed;
                return JSValue.Undefined;
            case IntrinsicsHelper.IntrinsicId.GetImportMetaObject:
                throw new NotSupportedException("V8Sharp: import.meta requires ES modules, which are not supported yet");
            case IntrinsicsHelper.IntrinsicId.CreateAsyncFromSyncIterator:
                return InterpreterAsync.CreateAsyncFromSyncIterator(isolate, args[0]);
            case IntrinsicsHelper.IntrinsicId.AsyncFunctionEnter:
                return InterpreterAsync.AsyncFunctionEnter(isolate, args[0].As<JSFunction>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.AsyncFunctionAwait:
                return InterpreterAsync.AsyncFunctionAwait(isolate, args[0].As<JSAsyncFunctionObject>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.AsyncFunctionResolve:
                return InterpreterAsync.AsyncFunctionResolve(isolate, args[0].As<JSAsyncFunctionObject>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.AsyncFunctionReject:
                return InterpreterAsync.AsyncFunctionReject(isolate, args[0].As<JSAsyncFunctionObject>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.AsyncGeneratorAwait:
                return InterpreterAsync.AsyncGeneratorAwait(isolate, args[0].As<JSAsyncGeneratorObject>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.AsyncGeneratorResolve:
                return InterpreterAsync.AsyncGeneratorResolve(isolate, args[0].As<JSAsyncGeneratorObject>(), args[1],
                    ObjectOps.BooleanValue(args[2]));
            case IntrinsicsHelper.IntrinsicId.AsyncGeneratorReject:
                return InterpreterAsync.AsyncGeneratorReject(isolate, args[0].As<JSAsyncGeneratorObject>(), args[1]);
            case IntrinsicsHelper.IntrinsicId.AsyncGeneratorYieldWithAwait:
                return InterpreterAsync.AsyncGeneratorYieldWithAwait(isolate, args[0].As<JSAsyncGeneratorObject>(), args[1]);
            default:
                throw new UnreachableException();
        }
    }
}
