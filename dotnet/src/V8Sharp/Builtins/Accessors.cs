// Port of src/builtins/accessors.{h,cc}: the AccessorInfos behind the
// engine's "special data properties" (Array length, function length, name and
// prototype, String length, bound/wrapped function length and name, module
// namespace entries) and the Error stack getter/setter callbacks.
//
// V8 allocates one AccessorInfo per accessor per isolate (isolate roots such as
// function_length_accessor). The getters and setters here are stateless, so
// V8Sharp shares one AccessorInfo per accessor across isolates.
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

public static class Accessors
{
    // ---------------------------------------------------------------------
    // The AccessorInfo instances (ACCESSOR_INFO_LIST_GENERATOR).

    public static readonly AccessorInfo ArgumentsIteratorAccessor =
        MakeAccessor(ReadOnlyRoots.iterator_symbol, ArgumentsIteratorGetter, null);

    public static readonly AccessorInfo ArrayLengthAccessor =
        MakeAccessor(ReadOnlyRoots.length_string, ArrayLengthGetter, ArrayLengthSetter);

    public static readonly AccessorInfo BoundFunctionLengthAccessor =
        MakeAccessor(ReadOnlyRoots.length_string, BoundFunctionLengthGetter, ReconfigureToDataProperty);

    public static readonly AccessorInfo BoundFunctionNameAccessor =
        MakeAccessor(ReadOnlyRoots.name_string, BoundFunctionNameGetter, ReconfigureToDataProperty);

    public static readonly AccessorInfo FunctionArgumentsAccessor =
        MakeAccessor(ReadOnlyRoots.arguments_string, FunctionArgumentsGetter, null);

    public static readonly AccessorInfo FunctionCallerAccessor =
        MakeAccessor(ReadOnlyRoots.caller_string, FunctionCallerGetter, null);

    public static readonly AccessorInfo FunctionNameAccessor =
        MakeAccessor(ReadOnlyRoots.name_string, FunctionNameGetter, ReconfigureToDataProperty);

    public static readonly AccessorInfo FunctionLengthAccessor =
        MakeAccessor(ReadOnlyRoots.length_string, FunctionLengthGetter, ReconfigureToDataProperty);

    public static readonly AccessorInfo FunctionPrototypeAccessor =
        MakeAccessor(ReadOnlyRoots.prototype_string, FunctionPrototypeGetter, FunctionPrototypeSetter);

    public static readonly AccessorInfo ModuleNamespaceEntryAccessor =
        MakeAccessor(ReadOnlyRoots.empty_string, ModuleNamespaceEntryGetter, ModuleNamespaceEntrySetter);

    public static readonly AccessorInfo StringLengthAccessor =
        MakeAccessor(ReadOnlyRoots.length_string, StringLengthGetter, null);

    public static readonly AccessorInfo ValueUnavailableAccessor =
        MakeAccessor(ReadOnlyRoots.empty_string, ValueUnavailableGetter, ReconfigureToDataProperty);

    public static readonly AccessorInfo WrappedFunctionLengthAccessor =
        MakeAccessor(ReadOnlyRoots.length_string, WrappedFunctionLengthGetter, ReconfigureToDataProperty);

    public static readonly AccessorInfo WrappedFunctionNameAccessor =
        MakeAccessor(ReadOnlyRoots.name_string, WrappedFunctionNameGetter, ReconfigureToDataProperty);

    /// <summary>
    /// Accessors::MakeAccessor. Every accessor in ACCESSOR_INFO_LIST_GENERATOR
    /// declares its getter kHasNoSideEffect (setup-heap-internal.cc applies it).
    /// </summary>
    static AccessorInfo MakeAccessor(Name name, AccessorNameGetter getter, AccessorNameSetter? setter) =>
        new(name, getter, setter ?? ReconfigureToDataProperty) { HasNoSideEffect = true };

    // ---------------------------------------------------------------------

    /// <summary>
    /// Accessors::IsJSObjectFieldAccessor: true for properties that are
    /// accessors to object fields (JSArray length).
    /// </summary>
    public static bool IsJSObjectFieldAccessor(Isolate isolate, Map map, Name name, out InternalIndex fakeDescriptorIndex)
    {
        fakeDescriptorIndex = InternalIndex.NotFound;
        // There are not descriptors in a dictionary mode map.
        if (map.IsDictionaryMap) return false;
        if (map.InstanceType == InstanceType.JSArrayType)
        {
            fakeDescriptorIndex = new InternalIndex(JSArray.kLengthDescriptorIndex);
            return name is JSString s && JSString.Equals(s, ReadOnlyRoots.length_string);
        }
        return false;
    }

    /// <summary>Accessors::ReplaceAccessorWithDataProperty.</summary>
    public static JSValue ReplaceAccessorWithDataProperty(Isolate isolate, JSObject holder, Name name, JSValue value)
    {
        var key = new PropertyKey(isolate, name);
        var it = new LookupIterator(isolate, holder, key, holder, LookupIterator.Configuration.OWN_SKIP_INTERCEPTOR);
        // Skip any access checks we might hit. This accessor should never hit in a
        // situation where the caller does not have access.
        while (it.State == LookupIterator.StateKind.ACCESS_CHECK)
        {
            it.Next();
        }
        if (it.State != LookupIterator.StateKind.ACCESSOR)
        {
            throw new InvalidOperationException("ReplaceAccessorWithDataProperty: not an accessor");
        }
        it.ReconfigureDataProperty(value, it.PropertyAttributes());
        return value;
    }

    /// <summary>Accessors::ReconfigureToDataProperty.</summary>
    public static bool ReconfigureToDataProperty(Isolate isolate, JSValue receiver, JSObject holder, Name name,
        JSValue value, ShouldThrow? shouldThrow)
    {
        ReplaceAccessorWithDataProperty(isolate, holder, name, value);
        return true;
    }

    /// <summary>UseFastFunctionNameLookup (js-function.cc).</summary>
    public static bool UseFastFunctionNameLookup(Isolate isolate, Map map)
    {
        if (map.NumberOfOwnDescriptors < JSFunction.kMinDescriptorsForFastBindAndWrap) return false;
        Debug.Assert(!map.IsDictionaryMap);
        DescriptorArray descriptors = map.InstanceDescriptors;
        var kNameIndex = new InternalIndex(JSFunctionOrBoundFunctionOrWrappedFunction.kNameDescriptorIndex);
        if (!ReferenceEquals(descriptors.GetKey(kNameIndex), ReadOnlyRoots.name_string)) return false;
        return descriptors.GetStrongValue(kNameIndex).HeapObjectOrNull is AccessorInfo;
    }

    // ---------------------------------------------------------------------
    // Getters and setters.

    static JSValue ArgumentsIteratorGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        isolate.NativeContext.ArrayValuesIterator;

    static JSValue ArrayLengthGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        ((JSArray)holder).Length;

    static bool ArrayLengthSetter(Isolate isolate, JSValue receiver, JSObject holder, Name name, JSValue value,
        ShouldThrow? shouldThrow)
    {
        Debug.Assert(ReferenceEquals(name, ReadOnlyRoots.length_string));
        var array = (JSArray)holder;
        bool wasReadonly = JSArray.HasReadOnlyLength(array);

        if (!JSArray.AnythingToArrayLength(isolate, value, out uint length)) return false;

        if (!wasReadonly && JSArray.HasReadOnlyLength(array))
        {
            // AnythingToArrayLength() may have called setter re-entrantly and modified
            // its property descriptor. Don't perform this check if "length" was
            // previously readonly, as this may have been called during
            // DefineOwnPropertyIgnoreAttributes().
            if (length == ObjectOps.NumberValue(array.Length)) return true;
            if (ObjectOps.GetShouldThrow(isolate, shouldThrow) == ShouldThrow.ThrowOnError)
            {
                isolate.ThrowTypeError(MessageTemplate.StrictReadOnlyProperty, name, ObjectOps.TypeOf(isolate, array), array);
            }
            return false;
        }

        JSArray.SetLength(isolate, array, length);

        ObjectOps.ToArrayLength(array.Length, out uint actualNewLen);
        // Fail if there were non-deletable elements.
        if (actualNewLen != length)
        {
            if (ObjectOps.GetShouldThrow(isolate, shouldThrow) == ShouldThrow.ThrowOnError)
            {
                isolate.ThrowTypeError(MessageTemplate.StrictCannotDeleteProperty,
                    isolate.Factory.NewNumberFromUint(actualNewLen - 1), array);
            }
            return false;
        }
        return true;
    }

    static JSValue ModuleNamespaceEntryGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        ((JSModuleNamespace)holder).GetExport(isolate, (JSString)name);

    static bool ModuleNamespaceEntrySetter(Isolate isolate, JSValue receiver, JSObject holder, Name name, JSValue value,
        ShouldThrow? shouldThrow)
    {
        if (ObjectOps.GetShouldThrow(isolate, shouldThrow) == ShouldThrow.ThrowOnError)
        {
            isolate.ThrowTypeError(MessageTemplate.StrictReadOnlyProperty, name, ObjectOps.TypeOf(isolate, holder), holder);
        }
        return false;
    }

    static JSValue StringLengthGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSValue.FromInt(((JSString)((JSPrimitiveWrapper)holder).Value.Object).Length);

    static JSValue FunctionPrototypeGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSFunction.GetFunctionPrototype(isolate, (JSFunction)holder);

    static bool FunctionPrototypeSetter(Isolate isolate, JSValue receiver, JSObject holder, Name name, JSValue value,
        ShouldThrow? shouldThrow)
    {
        JSFunction.SetPrototype(isolate, (JSFunction)holder, value);
        return true;
    }

    static JSValue FunctionLengthGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSValue.FromNumber(((JSFunction)holder).Length);

    static JSValue FunctionNameGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSFunction.GetName(isolate, (JSFunction)holder);

    static JSValue FunctionArgumentsGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name)
    {
        isolate.CountUsage(UseCounterFeature.kFunctionPrototypeArguments);
        return GetLegacyFunctionArguments(isolate, (JSFunction)holder);
    }

    static JSValue FunctionCallerGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name)
    {
        isolate.CountUsage(UseCounterFeature.kFunctionPrototypeCaller);
        return GetLegacyFunctionCaller(isolate, (JSFunction)holder);
    }

    static JSValue BoundFunctionLengthGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSValue.FromNumber(JSBoundFunction.GetLength(isolate, (JSBoundFunction)holder));

    static JSValue BoundFunctionNameGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSBoundFunction.GetName(isolate, (JSBoundFunction)holder);

    static JSValue WrappedFunctionLengthGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSValue.FromNumber(JSWrappedFunction.GetLength(isolate, (JSWrappedFunction)holder));

    static JSValue WrappedFunctionNameGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        JSWrappedFunction.GetName(isolate, (JSWrappedFunction)holder);

    static JSValue ValueUnavailableGetter(Isolate isolate, JSValue receiver, JSObject holder, Name name) =>
        isolate.Throw(isolate.Factory.NewReferenceError(MessageTemplate.AccessedUnavailableVariable, name));

    // ---------------------------------------------------------------------
    // Legacy function.arguments / function.caller.

    /// <summary>Accessors::GetLegacyFunctionArguments.</summary>
    public static JSValue GetLegacyFunctionArguments(Isolate isolate, JSFunction function)
    {
        if (function.Shared.Native) return JSValue.Null;
        IJavaScriptFrames? frames = isolate.Frames;
        if (frames is null) return JSValue.Null;
        // Find the top invocation of the function by traversing frames.
        for (int i = 0; frames.TryGetFrame(i, out JavaScriptFrameSummary summary); i++)
        {
            if (!ReferenceEquals(summary.Function, function)) continue;
            if (!frames.TryGetArguments(i, out JSValue[] parameters)) return JSValue.Null;
            return GetFrameArguments(isolate, function, parameters);
        }
        return JSValue.Null;
    }

    static JSObject GetFrameArguments(Isolate isolate, JSFunction function, JSValue[] parameters)
    {
        // Construct an arguments object mirror for the right frame and the underlying
        // function.
        int length = parameters.Length;
        JSObject arguments = isolate.Factory.NewArgumentsObject(function, length);
        FixedArray array = isolate.Factory.NewFixedArray(length);
        // Copy the parameters to the arguments object.
        for (int i = 0; i < length; i++)
        {
            JSValue value = parameters[i];
            // Generators currently use holes as dummy arguments when resuming. We
            // must not leak those.
            if (value.IsTheHole) value = JSValue.Undefined;
            array[i] = value;
        }
        arguments.Elements = array;
        return arguments;
    }

    /// <summary>Accessors::GetLegacyFunctionCaller.</summary>
    public static JSValue GetLegacyFunctionCaller(Isolate isolate, JSFunction function)
    {
        // We don't support caller access with correctness fuzzing.
        if (!isolate.Flags.correctness_fuzzer_suppressions && FindCaller(isolate, function) is { } caller)
        {
            return caller;
        }
        return JSValue.Null;
    }

    static JSFunction? FindCaller(Isolate isolate, JSFunction function)
    {
        if (function.Shared.Native) return null;
        IJavaScriptFrames? frames = isolate.Frames;
        if (frames is null) return null;
        // Find the function from the frames. Return null in case no frame
        // corresponding to the given function was found.
        int index = 0;
        while (true)
        {
            if (!frames.TryGetFrame(index++, out JavaScriptFrameSummary summary)) return null;
            if (ReferenceEquals(summary.Function, function)) break;
        }
        // Find previously called non-toplevel function that is also a user-land
        // JavaScript function (or the entry point into native JavaScript builtins
        // in case such a builtin was the caller).
        JSFunction caller;
        while (true)
        {
            if (!frames.TryGetFrame(index++, out JavaScriptFrameSummary summary)) return null;
            caller = summary.Function;
            SharedFunctionInfo shared = caller.Shared;
            if (!shared.IsToplevel && (shared.Native || shared.IsUserJavaScript())) break;
        }
        // Censor if the caller is not a sloppy mode function.
        // Change from ES5, which used to throw, see:
        // https://bugs.ecmascript.org/show_bug.cgi?id=310
        if (caller.Shared.LanguageMode != LanguageMode.Sloppy) return null;
        return caller;
    }

    // ---------------------------------------------------------------------
    // Accessors::ErrorStack (ACCESSOR_CALLBACK_LIST_GENERATOR).

    /// <summary>Accessors::ErrorStackGetter: the builtin behind error.stack's getter.</summary>
    public static JSValue ErrorStackGetter(Isolate isolate, in BuiltinArguments args)
    {
        JSValue formattedStack = JSValue.Undefined;
        if (args.Receiver.HeapObjectOrNull is JSObject maybeErrorObject)
        {
            formattedStack = ErrorUtils.GetFormattedStack(isolate, maybeErrorObject);
        }
        return formattedStack;
    }

    /// <summary>Accessors::ErrorStackSetter: the builtin behind error.stack's setter.</summary>
    public static JSValue ErrorStackSetter(Isolate isolate, in BuiltinArguments args)
    {
        if (args.Receiver.HeapObjectOrNull is JSObject maybeErrorObject)
        {
            ErrorUtils.SetFormattedStack(isolate, maybeErrorObject, args.AtOrUndefined(1));
        }
        return JSValue.Undefined;
    }
}
