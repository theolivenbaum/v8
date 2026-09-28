// The arguments-object bytecodes (CreateMappedArguments,
// CreateUnmappedArguments, CreateRestParameter): the builtins
// FastNewSloppyArguments / FastNewStrictArguments / FastNewRestArguments of
// src/builtins/builtins-call-gen.cc and their runtime fallbacks
// Runtime_NewSloppyArguments / NewStrictArguments / NewRestParameter
// (src/runtime/runtime-scopes.cc), reading the actual arguments from the
// interpreter frame.
namespace V8Sharp.Interpreter;

public static class InterpreterArguments
{
    /// <summary>The arguments object without the elements (Factory::NewArgumentsObject).</summary>
    static JSObject NewArgumentsObject(Isolate isolate, JSFunction callee, int length, bool strict)
    {
        NativeContext nc = isolate.NativeContext;
        JSObject result = isolate.Factory.NewJSObjectFromMap(strict ? nc.StrictArgumentsMap : nc.SloppyArgumentsMap);
        // The FastNewSloppyArguments / FastNewStrictArguments builtins store
        // length and callee into their in-object fields (the maps' first
        // descriptors, JSSloppyArgumentsObject::kLengthIndex / kCalleeIndex)
        // rather than through Object::SetProperty as the runtime does.
        Debug.Assert(result.Map.InstanceDescriptors.GetKey(new InternalIndex(JSArgumentsObject.kLengthIndex)) == ReadOnlyRoots.length_string);
        // Arguments objects keep their in-object fields in the PropertyArray (JSObjects.InObject.cs).
        JSValue[] fields = result._fields;
        fields[JSArgumentsObject.kLengthIndex] = JSValue.FromInt(length);
        if (!strict) fields[JSArgumentsObject.kCalleeIndex] = callee;
        return result;
    }

    /// <summary>CreateMappedArguments: NewSloppyArguments (runtime-scopes.cc).</summary>
    public static JSObject NewSloppyArguments(Isolate isolate, JSFunction callee, Context context, int fp, int argumentCount)
    {
        JSValue[] stack = isolate.RegisterStack;
        JSObject result = NewArgumentsObject(isolate, callee, argumentCount, strict: false);

        // Allocate the elements if needed.
        int parameterCount = callee.Shared.InternalFormalParameterCountWithoutReceiver;
        if (argumentCount > 0)
        {
            if (parameterCount > 0)
            {
                int mappedCount = Math.Min(argumentCount, parameterCount);

                // Store the context and the arguments array at the beginning of the
                // parameter map.
                var arguments = new FixedArray(argumentCount);
                var parameterMap = new SloppyArgumentsElements(context, arguments, mappedCount);

                result.Map = isolate.NativeContext.FastAliasedArgumentsMap;
                result.Elements = parameterMap;

                // Loop over the actual parameters backwards.
                for (int index = argumentCount - 1; index >= mappedCount; index--)
                {
                    // These go directly in the arguments array and have no
                    // corresponding slot in the parameter map.
                    arguments.Set(index, InterpreterRuntime.ArgumentSlot(stack, fp, index));
                }

                ScopeInfo scopeInfo = callee.Shared.ScopeInfo;

                // First mark all mappable slots as unmapped and copy the values into the
                // arguments object.
                for (int i = 0; i < mappedCount; i++)
                {
                    arguments.Set(i, InterpreterRuntime.ArgumentSlot(stack, fp, i));
                    parameterMap.SetMappedEntries(i, JSValue.TheHole);
                }

                // Walk all context slots to find context allocated parameters. Mark each
                // found parameter as mapped.
                for (int i = 0; i < scopeInfo.ContextLocalCount; i++)
                {
                    if (!scopeInfo.ContextLocalIsParameter(i)) continue;
                    int parameter = (int)scopeInfo.ContextLocalParameterNumber(i);
                    if (parameter >= mappedCount) continue;
                    arguments.SetTheHole(parameter);
                    parameterMap.SetMappedEntries(parameter, JSValue.FromInt(scopeInfo.ContextHeaderLength() + i));
                }
            }
            else
            {
                // If there is no aliasing, the arguments object elements are not
                // special in any way.
                var elements = new FixedArray(argumentCount);
                for (int i = 0; i < argumentCount; i++) elements.Set(i, InterpreterRuntime.ArgumentSlot(stack, fp, i));
                result.Elements = elements;
            }
        }
        return result;
    }

    /// <summary>CreateUnmappedArguments: Runtime_NewStrictArguments.</summary>
    public static JSObject NewStrictArguments(Isolate isolate, JSFunction callee, int fp, int argumentCount)
    {
        JSObject result = NewArgumentsObject(isolate, callee, argumentCount, strict: true);
        if (argumentCount > 0)
        {
            JSValue[] stack = isolate.RegisterStack;
            var array = new FixedArray(argumentCount);
            for (int i = 0; i < argumentCount; i++) array.Set(i, InterpreterRuntime.ArgumentSlot(stack, fp, i));
            result.Elements = array;
        }
        return result;
    }

    /// <summary>CreateRestParameter: Runtime_NewRestParameter.</summary>
    public static JSArray NewRestParameter(Isolate isolate, JSFunction callee, int fp, int argumentCount)
    {
        int startIndex = callee.Shared.InternalFormalParameterCountWithoutReceiver;
        int numElements = Math.Max(0, argumentCount - startIndex);
        JSArray result = isolate.Factory.NewJSArray(ElementsKind.PACKED_ELEMENTS, numElements, numElements);
        if (numElements == 0) return result;
        var elements = (FixedArray)result.Elements;
        JSValue[] stack = isolate.RegisterStack;
        for (int i = 0; i < numElements; i++) elements.Set(i, InterpreterRuntime.ArgumentSlot(stack, fp, i + startIndex));
        return result;
    }
}
