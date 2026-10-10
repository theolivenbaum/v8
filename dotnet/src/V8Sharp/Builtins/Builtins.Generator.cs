// Port of src/builtins/builtins-generator-gen.cc: Generator.prototype.next,
// return and throw (GeneratorPrototypeResume). The resume itself is the
// interpreter's (InterpreterGenerators.InnerResume).
using V8Sharp.Interpreter;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterGenerator()
    {
        Register(Builtin.GeneratorPrototypeNext, BuiltinsGenerator.GeneratorPrototypeNext);
        Register(Builtin.GeneratorPrototypeReturn, BuiltinsGenerator.GeneratorPrototypeReturn);
        Register(Builtin.GeneratorPrototypeThrow, BuiltinsGenerator.GeneratorPrototypeThrow);
    }
}

/// <summary>The Generator.prototype builtins.</summary>
public static class BuiltinsGenerator
{
    /// <summary>GeneratorBuiltinsAssembler::GeneratorPrototypeResume.</summary>
    static JSValue GeneratorPrototypeResume(Isolate isolate, JSValue receiver, JSValue value,
        JSGeneratorObject.ResumeMode resumeMode, string methodName)
    {
        // Check if the {receiver} is actually a JSGeneratorObject.
        if (receiver.HeapObjectOrNull is not JSGeneratorObject generator ||
            generator.Map.InstanceType != InstanceType.JSGeneratorObjectType)
        {
            return isolate.ThrowTypeError(MessageTemplate.IncompatibleMethodReceiver,
                isolate.Factory.NewStringFromUtf16(methodName), receiver);
        }
        return InterpreterGenerators.InnerResume(isolate, generator, value, resumeMode);
    }

    /// <summary>https://tc39.es/ecma262/#sec-generator.prototype.next.</summary>
    public static JSValue GeneratorPrototypeNext(Isolate isolate, in BuiltinArguments args) =>
        GeneratorPrototypeResume(isolate, args.Receiver, args.AtOrUndefined(1), JSGeneratorObject.ResumeMode.kNext,
            "[Generator].prototype.next");

    /// <summary>https://tc39.es/ecma262/#sec-generator.prototype.return.</summary>
    public static JSValue GeneratorPrototypeReturn(Isolate isolate, in BuiltinArguments args) =>
        GeneratorPrototypeResume(isolate, args.Receiver, args.AtOrUndefined(1), JSGeneratorObject.ResumeMode.kReturn,
            "[Generator].prototype.return");

    /// <summary>https://tc39.es/ecma262/#sec-generator.prototype.throw.</summary>
    public static JSValue GeneratorPrototypeThrow(Isolate isolate, in BuiltinArguments args) =>
        GeneratorPrototypeResume(isolate, args.Receiver, args.AtOrUndefined(1), JSGeneratorObject.ResumeMode.kThrow,
            "[Generator].prototype.throw");
}
