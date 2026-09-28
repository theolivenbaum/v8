// The generator machinery of the interpreter: the register file export and
// import of SuspendGenerator / ResumeGenerator
// (InterpreterAssembler::ExportParametersAndRegisterFile /
// ImportRegisterFile, src/interpreter/interpreter-assembler.cc),
// Runtime_CreateJSGeneratorObject (src/runtime/runtime-generator.cc), the
// ResumeGeneratorTrampoline (src/builtins/x64/builtins-x64.cc) and
// GeneratorBuiltinsAssembler::InnerResume
// (src/builtins/builtins-generator-gen.cc).
namespace V8Sharp.Interpreter;

public static class InterpreterGenerators
{
    /// <summary>SuspendGenerator: stores the formal parameters and the registers in the generator.</summary>
    public static void ExportParametersAndRegisterFile(Isolate isolate, JSGeneratorObject generator, int fp, int firstRegister,
        int registerCount)
    {
        JSValue[] stack = isolate.RegisterStack;
        FixedArray array = generator.ParametersAndRegisters;
        int parameterCount = generator.Function.Shared.InternalFormalParameterCountWithoutReceiver;
        JSValue[] data = array.Data;
        // Store the formal parameters (without receiver) followed by the
        // registers into the generator's internal parameters_and_registers field.
        for (int i = 0; i < parameterCount; i++) data[i] = InterpreterRuntime.ArgumentSlot(stack, fp, i);
        // The mapping of register to array index must match that used in
        // BytecodeGraphBuilder::VisitResumeGenerator.
        Array.Copy(stack, fp + firstRegister, data, parameterCount, registerCount);
    }

    /// <summary>ResumeGenerator: restores the registers and clears the saved copies.</summary>
    public static void ImportRegisterFile(Isolate isolate, JSGeneratorObject generator, int fp, int firstRegister,
        int registerCount)
    {
        JSValue[] stack = isolate.RegisterStack;
        JSValue[] data = generator.ParametersAndRegisters.Data;
        int parameterCount = generator.Function.Shared.InternalFormalParameterCountWithoutReceiver;
        Array.Copy(data, parameterCount, stack, fp + firstRegister, registerCount);
        // Erase the array contents to not keep them alive artificially
        // (V8 writes the stale register sentinel).
        Array.Clear(data, parameterCount, registerCount);
    }

    /// <summary>Runtime_CreateJSGeneratorObject (%_CreateJSGeneratorObject).</summary>
    public static JSGeneratorObject CreateJSGeneratorObject(Isolate isolate, JSFunction function, JSValue receiver)
    {
        var bytecode = (BytecodeArray)function.Shared.FunctionData!;
        int length = bytecode.ParameterCountWithoutReceiver + bytecode.RegisterCount;
        var parametersAndRegisters = new FixedArray(length);

        JSFunction.EnsureHasInitialMap(isolate, function);
        var generator = (JSGeneratorObject)isolate.Factory.NewJSObjectFromMap(function.InitialMap);
        if (generator is JSAsyncGeneratorObject asyncGeneratorObject) asyncGeneratorObject.Queue = JSValue.Undefined;
        generator.Function = function;
        generator.Context = isolate.Context!;
        generator.Receiver = receiver;
        generator.ParametersAndRegisters = parametersAndRegisters;
        generator.Mode = JSGeneratorObject.ResumeMode.kNext;
        generator.ContinuationValue = JSGeneratorObject.kGeneratorExecuting;
        if (generator is JSAsyncGeneratorObject asyncGenerator) asyncGenerator.IsAwaiting = false;
        return generator;
    }

    /// <summary>
    /// ResumeGeneratorTrampoline: stores the input, pushes the saved formal
    /// parameters as arguments and re-enters the bytecode with the generator
    /// in the incoming new.target/generator register.
    /// </summary>
    public static JSValue ResumeGeneratorTrampoline(Isolate isolate, JSValue value, JSGeneratorObject generator)
    {
        // Store input value into generator object.
        generator.InputOrDebugPos = value;
        JSFunction function = generator.Function;
        int parameterCount = function.Shared.InternalFormalParameterCountWithoutReceiver;
        ReadOnlySpan<JSValue> parameters = generator.ParametersAndRegisters.Data.AsSpan(0, parameterCount);
        return InterpreterExecution.Invoke(isolate, function, generator.Receiver, parameters, generator, isConstruct: false);
    }

    /// <summary>GeneratorBuiltinsAssembler::InnerResume.</summary>
    public static JSValue InnerResume(Isolate isolate, JSGeneratorObject receiver, JSValue value,
        JSGeneratorObject.ResumeMode resumeMode)
    {
        // Check if the {receiver} is running or already closed.
        int receiverContinuation = receiver.ContinuationValue;
        if (receiverContinuation == JSGeneratorObject.kGeneratorClosed)
        {
            // The {receiver} is closed already.
            switch (resumeMode)
            {
                case JSGeneratorObject.ResumeMode.kNext:
                    return InterpreterRuntime.NewJSIteratorResult(isolate, JSValue.Undefined, true);
                case JSGeneratorObject.ResumeMode.kReturn:
                    return InterpreterRuntime.NewJSIteratorResult(isolate, value, true);
                case JSGeneratorObject.ResumeMode.kThrow:
                    return isolate.Throw(value);
                default:
                    throw new UnreachableException();
            }
        }
        if (receiverContinuation < JSGeneratorObject.kGeneratorClosed)
        {
            return isolate.ThrowTypeError(MessageTemplate.GeneratorRunning);
        }

        // Remember the {resume_mode} for the {receiver}.
        receiver.Mode = resumeMode;

        // Resume the {receiver} using our trampoline.
        // Close the generator if there was an exception.
        JSValue result;
        try
        {
            result = ResumeGeneratorTrampoline(isolate, value, receiver);
        }
        catch (JavaScriptException)
        {
            receiver.ContinuationValue = JSGeneratorObject.kGeneratorClosed;
            throw;
        }

        // If the generator is not suspended (i.e., its state is 'executing'),
        // close it and wrap the return value in IteratorResult.
        if (receiver.ContinuationValue == JSGeneratorObject.kGeneratorExecuting)
        {
            // Close the generator.
            receiver.ContinuationValue = JSGeneratorObject.kGeneratorClosed;
            // Return the wrapped result.
            return InterpreterRuntime.NewJSIteratorResult(isolate, result, true);
        }
        return result;
    }
}
