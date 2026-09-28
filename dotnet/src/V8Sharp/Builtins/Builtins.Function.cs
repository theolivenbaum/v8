// Port of the Function builtins: src/builtins/builtins-function.cc
// (CreateDynamicFunction and the Function/GeneratorFunction/AsyncFunction/
// AsyncGeneratorFunction constructors, Function.prototype.bind and toString,
// the legacy arguments/caller accessors), function.tq
// (Function.prototype[@@hasInstance], FastFunctionPrototypeBind), and the
// semantics of the ASM builtins Function.prototype.apply/call
// (builtins-x64.cc Generate_FunctionPrototypeApply/Call) with
// CallWithArrayLike/ConstructWithArrayLike (builtins-call-gen.cc
// CallOrConstructWithArrayLike) and Runtime_ThrowTargetNonFunction
// (runtime-internal.cc). Also the trivial builtins of builtins-internal.cc
// behind %FunctionPrototype% and the poison-pill/unsupported throwers.
using System.Buffers;
using V8Sharp.Parsing;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterFunction()
    {
        Register(Builtin.FunctionConstructor, BuiltinsFunction.FunctionConstructor);
        Register(Builtin.GeneratorFunctionConstructor, BuiltinsFunction.GeneratorFunctionConstructor);
        Register(Builtin.AsyncFunctionConstructor, BuiltinsFunction.AsyncFunctionConstructor);
        Register(Builtin.AsyncGeneratorFunctionConstructor, BuiltinsFunction.AsyncGeneratorFunctionConstructor);
        Register(Builtin.FunctionPrototypeApply, BuiltinsFunction.FunctionPrototypeApply);
        Register(Builtin.FunctionPrototypeCall, BuiltinsFunction.FunctionPrototypeCall);
        Register(Builtin.FunctionPrototypeBind, BuiltinsFunction.FunctionPrototypeBind);
        Register(Builtin.FastFunctionPrototypeBind, BuiltinsFunction.FastFunctionPrototypeBind);
        Register(Builtin.FunctionPrototypeToString, BuiltinsFunction.FunctionPrototypeToString);
        Register(Builtin.FunctionPrototypeHasInstance, BuiltinsFunction.FunctionPrototypeHasInstance);
        Register(Builtin.FunctionPrototypeLegacyArgumentsGetter, BuiltinsFunction.FunctionPrototypeLegacyArgumentsGetter);
        Register(Builtin.FunctionPrototypeLegacyArgumentsSetter, BuiltinsFunction.FunctionPrototypeLegacyArgumentsSetter);
        Register(Builtin.FunctionPrototypeLegacyCallerGetter, BuiltinsFunction.FunctionPrototypeLegacyCallerGetter);
        Register(Builtin.FunctionPrototypeLegacyCallerSetter, BuiltinsFunction.FunctionPrototypeLegacyCallerSetter);
        Register(Builtin.EmptyFunction, BuiltinsFunction.EmptyFunction);
        Register(Builtin.EmptyFunction1, BuiltinsFunction.EmptyFunction);
        Register(Builtin.IllegalInvocationThrower, BuiltinsFunction.IllegalInvocationThrower);
        Register(Builtin.UnsupportedThrower, BuiltinsFunction.UnsupportedThrower);
        Register(Builtin.StrictPoisonPillThrower, BuiltinsFunction.StrictPoisonPillThrower);
    }
}

/// <summary>The Function builtins.</summary>
public static class BuiltinsFunction
{
    // ---------------------------------------------------------------------
    // builtins-function.cc: CreateDynamicFunction

    /// <summary>ES6 section 19.2.1.1 Function ( p1, p2, ... , pn, body ).</summary>
    public static JSValue FunctionConstructor(Isolate isolate, in BuiltinArguments args) =>
        CreateDynamicFunction(isolate, in args, "function");

    /// <summary>ES6 section 25.2.1.1 GeneratorFunction (p1, p2, ... , pn, body).</summary>
    public static JSValue GeneratorFunctionConstructor(Isolate isolate, in BuiltinArguments args) =>
        CreateDynamicFunction(isolate, in args, "function*");

    /// <summary>AsyncFunction ( p1, p2, ... , pn, body ).</summary>
    public static JSValue AsyncFunctionConstructor(Isolate isolate, in BuiltinArguments args) =>
        // V8 computes the eval position eagerly here (Script::GetEvalPosition),
        // because it may not be determinable after the function is resumed.
        // V8Sharp's Script computes it from the stored eval origin at any time.
        CreateDynamicFunction(isolate, in args, "async function");

    /// <summary>https://tc39.es/proposal-async-iteration/#sec-asyncgeneratorfunction-constructor</summary>
    public static JSValue AsyncGeneratorFunctionConstructor(Isolate isolate, in BuiltinArguments args) =>
        CreateDynamicFunction(isolate, in args, "async function*");

    /// <summary>ES6 section 19.2.1.1.1 CreateDynamicFunction.</summary>
    static JSValue CreateDynamicFunction(Isolate isolate, in BuiltinArguments args, string token)
    {
        // Compute number of arguments, ignoring the receiver.
        int argc = args.ArgcWithoutReceiver;
        JSFunction target = args.Target;
        JSGlobalProxy targetGlobalProxy = target.GlobalProxy;

        // Builtins::AllowDynamicFunction: V8Sharp has one embedder and no
        // security tokens that could differ, so dynamic functions are always
        // allowed.

        // Use an embedder-provided source string when available; otherwise, fall
        // back to ToString(). (There is no modify-code-gen callback in V8Sharp.)
        JSString[] parameterStrings = argc > 1 ? new JSString[argc - 1] : [];

        // A call with no arguments has nothing trustworthy to base "code-like"
        // on, so it starts false instead of true. (No object is code-like
        // without an embedder IsCodeLike callback.)
        bool isCodeLike = false;
        JSString bodyString = ReadOnlyRoots.empty_string;
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        for (int i = 1; i <= argc; ++i)
        {
            JSString argumentString = ObjectOps.ToString(isolate, arguments[i - 1]);
            if (i < argc) parameterStrings[i - 1] = argumentString;
            else bodyString = argumentString;
        }

        // Build the source string.
        JSString source;
        int parametersEndPos;
        {
            var builder = new IncrementalStringBuilder(isolate);
            builder.AppendCharacter('(');
            builder.AppendCString(token);
            builder.AppendCStringLiteral(" anonymous(");
            for (int i = 0; i < parameterStrings.Length; ++i)
            {
                if (i > 0) builder.AppendCharacter(',');
                builder.AppendString(parameterStrings[i]);
            }
            builder.AppendCharacter('\n');
            parametersEndPos = builder.Length;
            builder.AppendCStringLiteral(") {\n");
            builder.AppendString(bodyString);
            builder.AppendCStringLiteral("\n})");
            source = builder.Finish();
        }

        // Compile the string in the constructor and not a helper so that errors to
        // come from here.
        IDynamicFunctionCompiler compiler = isolate.DynamicFunctionCompiler ??
            throw new InvalidOperationException("V8Sharp: no compiler registered (Isolate.DynamicFunctionCompiler)");
        JSFunction function = compiler.GetFunctionFromString(isolate, target.NativeContext, source, parametersEndPos, isCodeLike);
        JSValue result = Execution.Call(isolate, function, targetGlobalProxy, []);
        function = result.As<JSFunction>();
        function.Shared.NameShouldPrintAsAnonymous = true;

        // If new.target is equal to target then the function created
        // is already correctly setup and nothing else should be done
        // here. But if new.target is not equal to target then we are
        // have a Function builtin subclassing case and therefore the
        // function has wrong initial map. To fix that we create a new
        // function object with correct initial map.
        JSValue uncheckedNewTarget = args.NewTarget;
        if (!uncheckedNewTarget.IsUndefined && !ReferenceEquals(uncheckedNewTarget.HeapObjectOrNull, target))
        {
            var newTarget = uncheckedNewTarget.As<JSReceiver>();
            Map initialMap = JSFunction.GetDerivedMap(isolate, target, newTarget);
            SharedFunctionInfo sharedInfo = function.Shared;
            Map map = AsLanguageMode(isolate, initialMap, sharedInfo);
            function = isolate.Factory.NewFunction(sharedInfo, function.Context, map);
        }
        return function;
    }

    /// <summary>
    /// Map::AsLanguageMode: the derived function map for the function's
    /// language mode. V8 caches the strict variant as a special transition
    /// (strict_function_transition_symbol) of the sloppy initial map; V8Sharp
    /// builds it each time (only reachable through subclassed Function
    /// constructors with a strict body).
    /// </summary>
    static Map AsLanguageMode(Isolate isolate, Map initialMap, SharedFunctionInfo sharedInfo)
    {
        // Initial map for sloppy mode function is stored in the function
        // constructor. Initial maps for strict mode are cached as special transitions
        // using |strict_function_transition_symbol| as a key.
        if (sharedInfo.LanguageMode == LanguageMode.Sloppy) return initialMap;

        var functionMap = isolate.NativeContext.Slots[sharedInfo.FunctionMapIndex].As<Map>();
        // Create new map taking descriptors from the |function_map| and all
        // the other details from the |initial_map|.
        Map map = Map.CopyInitialMap(isolate, functionMap, initialMap.InstanceSize, initialMap.GetInObjectProperties(),
            initialMap.UnusedPropertyFields());
        map.SetConstructor(initialMap.GetConstructor());
        map.Prototype = initialMap.Prototype;
        map.ConstructionCounter = initialMap.ConstructionCounter;
        return map;
    }

    // ---------------------------------------------------------------------
    // Function.prototype.apply / call (builtins-x64.cc)

    /// <summary>ES6 section 19.2.3.1 Function.prototype.apply ( thisArg, argArray ).</summary>
    public static JSValue FunctionPrototypeApply(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Load receiver into rdi, argArray into rbx (if present), remove all
        // arguments from the stack (including the receiver), and push thisArg (if
        // present) instead.
        JSValue receiver = args.Receiver;
        JSValue thisArg = args.AtOrUndefined(1);
        JSValue argArray = args.AtOrUndefined(2);

        // 2. We don't need to check explicitly for callable receiver here,
        // since that's the first thing the Call/CallWithArrayLike builtins
        // will do.

        // 3. Tail call with no arguments if argArray is null or undefined.
        if (argArray.IsNullOrUndefined)
        {
            // 4b. The argArray is either null or undefined, so we tail call without any
            // arguments to the receiver.
            if (!ObjectOps.IsCallable(receiver)) return ThrowTargetNonFunction(isolate, receiver, ReadOnlyRoots.Function_prototype_apply_string);
            return Execution.Call(isolate, receiver, thisArg, []);
        }

        // 4a. Apply the receiver to the given argArray.
        return CallWithArrayLike(isolate, receiver, thisArg, argArray);
    }

    /// <summary>ES6 section 19.2.3.3 Function.prototype.call ( thisArg, ...args ).</summary>
    public static JSValue FunctionPrototypeCall(Isolate isolate, in BuiltinArguments args)
    {
        // 1. Get the callable to call (passed as receiver) from the stack.
        JSValue target = args.Receiver;
        if (!ObjectOps.IsCallable(target)) return ThrowTargetNonFunction(isolate, target, ReadOnlyRoots.Function_prototype_call_string);
        // 3. Make sure we have at least one argument.
        // 4. The original first argument is the new receiver.
        ReadOnlySpan<JSValue> arguments = args.Arguments;
        if (arguments.Length == 0) return Execution.Call(isolate, target, JSValue.Undefined, []);
        // 5. Call the callable.
        return Execution.Call(isolate, target, arguments[0], arguments[1..]);
    }

    /// <summary>
    /// CallOrConstructBuiltinsAssembler::CallOrConstructWithArrayLike without
    /// new.target (the CallWithArrayLike builtin): [[Call]] of
    /// <paramref name="target"/> with the elements of <paramref name="argumentsList"/>.
    /// </summary>
    public static JSValue CallWithArrayLike(Isolate isolate, JSValue target, JSValue thisArg, JSValue argumentsList)
    {
        // Check that {target} is Callable.
        if (!ObjectOps.IsCallable(target)) return ThrowTargetNonFunction(isolate, target, ReadOnlyRoots.Function_prototype_apply_string);
        return CallOrConstructWithArrayLike(isolate, target, thisArg, null, argumentsList);
    }

    /// <summary>
    /// CallOrConstructBuiltinsAssembler::CallOrConstructWithArrayLike with
    /// new.target (the ConstructWithArrayLike builtin).
    /// </summary>
    public static JSValue ConstructWithArrayLike(Isolate isolate, JSValue target, JSValue newTarget, JSValue argumentsList)
    {
        // Check that {target} is a Constructor.
        if (!ObjectOps.IsConstructor(target)) return isolate.ThrowTypeError(MessageTemplate.NotConstructor, target);
        // Check that {new_target} is a Constructor.
        if (!ObjectOps.IsConstructor(newTarget)) return isolate.ThrowTypeError(MessageTemplate.NotConstructor, newTarget);
        return CallOrConstructWithArrayLike(isolate, target, JSValue.Undefined, newTarget, argumentsList);
    }

    static JSValue CallOrConstructWithArrayLike(Isolate isolate, JSValue target, JSValue thisArg, JSValue? newTarget,
        JSValue argumentsList)
    {
        // Fast paths: the elements of an unmodified arguments object or of a
        // fast JSArray are passed without building a list. They are copied to a
        // pooled buffer first, as V8 pushes them onto the stack: the callee may
        // modify the array while its arguments are live.
        if (TryGetFastElements(isolate, argumentsList, out FixedArrayBase? elements, out int length))
        {
            if (length == 0) return Invoke(isolate, target, thisArg, newTarget, []);
            JSValue[] buffer = ArrayPool<JSValue>.Shared.Rent(length);
            try
            {
                Span<JSValue> span = buffer.AsSpan(0, length);
                if (elements is FixedArray fixedArray)
                {
                    fixedArray.Data.AsSpan(0, length).CopyTo(span);
                    // Holes (holey arrays with intact protectors) read as undefined.
                    for (int i = 0; i < span.Length; i++)
                    {
                        if (span[i].IsTheHole) span[i] = JSValue.Undefined;
                    }
                }
                else
                {
                    // CallOrConstructDoubleVarargs: box the doubles.
                    var doubles = (FixedDoubleArray)elements!;
                    for (int i = 0; i < span.Length; i++)
                    {
                        span[i] = doubles.IsTheHole(i) ? JSValue.Undefined : JSValue.FromNumber(doubles.GetScalar(i));
                    }
                }
                return Invoke(isolate, target, thisArg, newTarget, span);
            }
            finally
            {
                ArrayPool<JSValue>.Shared.Return(buffer, clearArray: true);
            }
        }

        // Ask the runtime to create the list (actually a FixedArray).
        FixedArray list = ObjectOps.CreateListFromArrayLike(isolate, argumentsList, ElementTypes.All);
        return Invoke(isolate, target, thisArg, newTarget, list.Data.AsSpan(0, list.Length));
    }

    static JSValue Invoke(Isolate isolate, JSValue target, JSValue thisArg, JSValue? newTarget, ReadOnlySpan<JSValue> arguments) =>
        newTarget is { } nt
            ? Execution.New(isolate, target, nt, arguments)
            : Execution.Call(isolate, target, thisArg, arguments);

    /// <summary>
    /// The element fast paths of CallOrConstructWithArrayLike: an arguments
    /// object with its initial map and length, or a fast JSArray (holey ones
    /// only with the initial Array.prototype and the no-elements protector).
    /// </summary>
    static bool TryGetFastElements(Isolate isolate, JSValue argumentsList, out FixedArrayBase? elements, out int length)
    {
        elements = null;
        length = 0;
        if (argumentsList.HeapObjectOrNull is not JSObject obj) return false;
        Map map = obj.Map;
        NativeContext nativeContext = isolate.NativeContext;

        // Check if {arguments_list} is an (unmodified) arguments object.
        if (ReferenceEquals(map, nativeContext.SloppyArgumentsMap) || ReferenceEquals(map, nativeContext.StrictArgumentsMap))
        {
            // Try to extract the elements from a JSArgumentsObject with standard map.
            JSValue lengthValue = obj.RawFields[JSArgumentsObject.kLengthIndex];
            if (obj.Elements is not FixedArray argumentsElements || !lengthValue.IsSmi ||
                (int)lengthValue.Number != argumentsElements.Length)
            {
                return false;
            }
            elements = argumentsElements;
            length = argumentsElements.Length;
            return true;
        }

        // Check if {arguments_list} is a fast JSArray.
        if (obj is not JSArray array) return false;
        ElementsKind kind = map.ElementsKind;
        if (kind > ElementsKind.LAST_ANY_NONEXTENSIBLE_ELEMENTS_KIND) return false;
        if (ElementsKinds.IsHoleyElementsKindForRead(kind))
        {
            // For holey JSArrays we need to check that the array prototype chain
            // protector is intact and our prototype is the Array.prototype actually.
            if (!ReferenceEquals(map.Prototype, nativeContext.InitialArrayPrototype)) return false;
            if (!Protectors.IsNoElementsIntact(isolate)) return false;
        }
        elements = array.Elements;
        length = (int)array.Length.Number;
        return true;
    }

    /// <summary>
    /// Runtime_ThrowTargetNonFunction: "Function.prototype.apply was called on
    /// x, which is a y and not a function".
    /// </summary>
    static JSValue ThrowTargetNonFunction(Isolate isolate, JSValue obj, JSString target)
    {
        JSString type = ObjectOps.TypeOf(isolate, obj);
        JSString msg;
        if (obj.IsNull)
        {
            // "which is null"
            msg = ReadOnlyRoots.null_string;
        }
        else if (obj.IsUndefined)
        {
            // "which is undefined"
            msg = ReadOnlyRoots.undefined_string;
        }
        else if (type.Equals(ReadOnlyRoots.object_string))
        {
            // "which is an object"
            msg = isolate.Factory.NewStringFromAsciiChecked("an object");
        }
        else
        {
            // "which is a typeof arg"
            msg = isolate.Factory.NewConsString(isolate.Factory.NewStringFromAsciiChecked("a "), type);
        }
        return isolate.ThrowTypeError(MessageTemplate.TargetNonFunction, target, obj, msg);
    }

    // ---------------------------------------------------------------------
    // Function.prototype.bind

    /// <summary>ES6 section 19.2.3.2 Function.prototype.bind (function.tq FastFunctionPrototypeBind).</summary>
    public static JSValue FastFunctionPrototypeBind(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        int argc = args.ArgcWithoutReceiver;
        const int kCodeMaxArguments = (1 << 16) - 2;

        if (receiver.HeapObjectOrNull is JSFunctionOrBoundFunctionOrWrappedFunction fn && argc < kCodeMaxArguments)
        {
            Map fnMap = fn.Map;
            // Disallow binding of slow-mode functions. We need to figure out
            // whether the length and name property are in the original state.
            // Check whether the length and name properties are still present as
            // AccessorInfo objects. If so, their value can be recomputed even if
            // the actual value on the object changes.
            if (!fnMap.IsDictionaryMap && fnMap.NumberOfOwnDescriptors >= JSFunction.kMinDescriptorsForFastBindAndWrap &&
                IsAccessorInfoAt(fnMap.InstanceDescriptors, JSFunctionOrBoundFunctionOrWrappedFunction.kLengthDescriptorIndex,
                    ReadOnlyRoots.length_string) &&
                IsAccessorInfoAt(fnMap.InstanceDescriptors, JSFunctionOrBoundFunctionOrWrappedFunction.kNameDescriptorIndex,
                    ReadOnlyRoots.name_string))
            {
                // Choose the right bound function map based on whether the target is
                // constructable.
                NativeContext nativeContext = args.Target.NativeContext;
                Map boundFunctionMap = fnMap.IsConstructor
                    ? nativeContext.BoundFunctionWithConstructorMap
                    : nativeContext.BoundFunctionWithoutConstructorMap;

                // Verify that prototype matches that of the target bound function.
                if (ReferenceEquals(fnMap.Prototype, boundFunctionMap.Prototype))
                {
                    // Allocate the arguments array.
                    FixedArray argumentsArray = argc <= 1 ? FixedArray.Empty : new FixedArray(args.Arguments[1..].ToArray());
                    JSValue boundReceiver = args.AtOrUndefined(1);
                    return new JSBoundFunction(boundFunctionMap, fn, boundReceiver, argumentsArray);
                }
            }
        }
        return DoFunctionBind(isolate, in args, ProtoSource.UseTargetPrototype);
    }

    static bool IsAccessorInfoAt(DescriptorArray descriptors, int index, Name name) =>
        ReferenceEquals(descriptors.GetKey(index), name) &&
        descriptors.GetStrongValue(new InternalIndex(index)).HeapObjectOrNull is AccessorInfo;

    /// <summary>ES6 section 19.2.3.2 Function.prototype.bind ( thisArg, ...args ).</summary>
    public static JSValue FunctionPrototypeBind(Isolate isolate, in BuiltinArguments args) =>
        DoFunctionBind(isolate, in args, ProtoSource.UseTargetPrototype);

    enum ProtoSource { NormalFunction, UseTargetPrototype }

    static JSValue DoFunctionBind(Isolate isolate, in BuiltinArguments args, ProtoSource protoSource)
    {
        if (!ObjectOps.IsCallable(args.Receiver)) return isolate.ThrowTypeError(MessageTemplate.FunctionBind);

        // Allocate the bound function with the given {this_arg} and {args}.
        var target = args.Receiver.As<JSReceiver>();
        JSValue thisArg = args.AtOrUndefined(1);
        ReadOnlySpan<JSValue> argv = args.ArgcWithoutReceiver > 1 ? args.Arguments[1..] : [];

        JSReceiver? proto = protoSource == ProtoSource.UseTargetPrototype
            // Determine the prototype of the {target_function}.
            ? JSReceiver.GetPrototype(isolate, target)
            : isolate.NativeContext.FunctionPrototype;

        JSBoundFunction function = isolate.Factory.NewJSBoundFunction(target, thisArg, argv, proto);
        JSFunctionOrBoundFunctionOrWrappedFunction.CopyNameAndLength(isolate, function, target, ReadOnlyRoots.bound__string,
            argv.Length);
        return function;
    }

    // ---------------------------------------------------------------------
    // toString, @@hasInstance

    /// <summary>https://tc39.es/ecma262/#sec-function.prototype.tostring</summary>
    public static JSValue FunctionPrototypeToString(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        switch (receiver.HeapObjectOrNull)
        {
            case JSBoundFunction bound:
                return JSBoundFunction.ToString(isolate, bound);
            case JSFunction function:
                return JSFunction.ToString(isolate, function);
            // With the revised toString behavior, all callable objects are valid
            // receivers for this method.
            case JSReceiver r when r.Map.IsCallable:
                return ReadOnlyRoots.function_native_code_string;
        }
        return isolate.ThrowTypeError(MessageTemplate.NotGeneric,
            isolate.Factory.NewStringFromAsciiChecked("Function.prototype.toString"), ReadOnlyRoots.Function_string);
    }

    /// <summary>ES6 section 19.2.3.6 Function.prototype[@@hasInstance].</summary>
    public static JSValue FunctionPrototypeHasInstance(Isolate isolate, in BuiltinArguments args) =>
        JSValue.FromBoolean(ObjectOps.OrdinaryHasInstance(isolate, args.Receiver, args.AtOrUndefined(1)));

    // ---------------------------------------------------------------------
    // Legacy .arguments / .caller (!V8_FUNCTION_ARGUMENTS_CALLER_ARE_OWN_PROPS)

    static bool IsSloppyNormalJSFunction(JSValue receiver) =>
        receiver.HeapObjectOrNull is JSFunction function &&
        function.Shared.Kind == FunctionKind.NormalFunction && function.Shared.LanguageMode == LanguageMode.Sloppy;

    public static JSValue FunctionPrototypeLegacyArgumentsGetter(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (!IsSloppyNormalJSFunction(receiver)) return isolate.ThrowTypeError(MessageTemplate.StrictPoisonPill);
        // Only count if we hit the non-throwing, compat behavior.
        isolate.CountUsage("kFunctionPrototypeArguments");
        return Accessors.GetLegacyFunctionArguments(isolate, receiver.As<JSFunction>());
    }

    public static JSValue FunctionPrototypeLegacyArgumentsSetter(Isolate isolate, in BuiltinArguments args)
    {
        if (!IsSloppyNormalJSFunction(args.Receiver)) return isolate.ThrowTypeError(MessageTemplate.StrictPoisonPill);
        isolate.CountUsage("kFunctionPrototypeArguments");
        return JSValue.Undefined;
    }

    public static JSValue FunctionPrototypeLegacyCallerGetter(Isolate isolate, in BuiltinArguments args)
    {
        JSValue receiver = args.Receiver;
        if (!IsSloppyNormalJSFunction(receiver)) return isolate.ThrowTypeError(MessageTemplate.StrictPoisonPill);
        isolate.CountUsage("kFunctionPrototypeCaller");
        return Accessors.GetLegacyFunctionCaller(isolate, receiver.As<JSFunction>());
    }

    public static JSValue FunctionPrototypeLegacyCallerSetter(Isolate isolate, in BuiltinArguments args)
    {
        if (!IsSloppyNormalJSFunction(args.Receiver)) return isolate.ThrowTypeError(MessageTemplate.StrictPoisonPill);
        isolate.CountUsage("kFunctionPrototypeCaller");
        return JSValue.Undefined;
    }

    // ---------------------------------------------------------------------
    // builtins-internal.cc

    /// <summary>EmptyFunction / EmptyFunction1: %FunctionPrototype% and other no-op functions.</summary>
    public static JSValue EmptyFunction(Isolate isolate, in BuiltinArguments args) => JSValue.Undefined;

    public static JSValue IllegalInvocationThrower(Isolate isolate, in BuiltinArguments args) =>
        isolate.ThrowTypeError(MessageTemplate.IllegalInvocation);

    /// <summary>UnsupportedThrower: the CallSite constructor and other unsupported entry points.</summary>
    public static JSValue UnsupportedThrower(Isolate isolate, in BuiltinArguments args) =>
        isolate.Throw(isolate.Factory.NewError(MessageTemplate.Unsupported));

    /// <summary>StrictPoisonPillThrower: %ThrowTypeError%.</summary>
    public static JSValue StrictPoisonPillThrower(Isolate isolate, in BuiltinArguments args) =>
        isolate.ThrowTypeError(MessageTemplate.StrictPoisonPill);
}
