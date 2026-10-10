// Port of the global object's functions: src/builtins/builtins-global.cc
// (decodeURI, decodeURIComponent, encodeURI, encodeURIComponent, escape,
// unescape, eval), builtins-global-gen.cc (isFinite, isNaN) and, because the
// global parseInt/parseFloat are the Number.parseInt/parseFloat functions,
// number.tq NumberParseFloat / ParseInt / NumberParseInt with
// runtime-numbers.cc Runtime_StringParseInt / Runtime_StringParseFloat.
// The URI coding itself (src/strings/uri.cc) is in Builtins.Global.Uri.cs.
using V8Sharp.Base.Numbers;
using V8Sharp.Parsing;

namespace V8Sharp.Builtins;

public static partial class BuiltinRegistry
{
    static partial void RegisterGlobal()
    {
        Register(Builtin.GlobalDecodeURI, BuiltinsGlobal.GlobalDecodeURI);
        Register(Builtin.GlobalDecodeURIComponent, BuiltinsGlobal.GlobalDecodeURIComponent);
        Register(Builtin.GlobalEncodeURI, BuiltinsGlobal.GlobalEncodeURI);
        Register(Builtin.GlobalEncodeURIComponent, BuiltinsGlobal.GlobalEncodeURIComponent);
        Register(Builtin.GlobalEscape, BuiltinsGlobal.GlobalEscape);
        Register(Builtin.GlobalUnescape, BuiltinsGlobal.GlobalUnescape);
        Register(Builtin.GlobalEval, BuiltinsGlobal.GlobalEval);
        Register(Builtin.GlobalIsFinite, BuiltinsGlobal.GlobalIsFinite);
        Register(Builtin.GlobalIsNaN, BuiltinsGlobal.GlobalIsNaN);
        // The global parseInt/parseFloat are Number.parseInt/parseFloat
        // (bootstrapper.cc installs one function under both names).
        Register(Builtin.NumberParseFloat, BuiltinsGlobal.NumberParseFloat);
        Register(Builtin.NumberParseInt, BuiltinsGlobal.NumberParseInt);
    }
}

/// <summary>The global object's function builtins.</summary>
public static class BuiltinsGlobal
{
    /// <summary>ES6 section 18.2.6.2 decodeURI (encodedURI).</summary>
    public static JSValue GlobalDecodeURI(Isolate isolate, in BuiltinArguments args) =>
        Uri.DecodeUri(isolate, ObjectOps.ToString(isolate, args.AtOrUndefined(1)));

    /// <summary>ES6 section 18.2.6.3 decodeURIComponent (encodedURIComponent).</summary>
    public static JSValue GlobalDecodeURIComponent(Isolate isolate, in BuiltinArguments args) =>
        Uri.DecodeUriComponent(isolate, ObjectOps.ToString(isolate, args.AtOrUndefined(1)));

    /// <summary>ES6 section 18.2.6.4 encodeURI (uri).</summary>
    public static JSValue GlobalEncodeURI(Isolate isolate, in BuiltinArguments args) =>
        Uri.EncodeUri(isolate, ObjectOps.ToString(isolate, args.AtOrUndefined(1)));

    /// <summary>ES6 section 18.2.6.5 encodeURIComponent (uriComponent).</summary>
    public static JSValue GlobalEncodeURIComponent(Isolate isolate, in BuiltinArguments args) =>
        Uri.EncodeUriComponent(isolate, ObjectOps.ToString(isolate, args.AtOrUndefined(1)));

    /// <summary>ES6 section B.2.1.1 escape (string).</summary>
    public static JSValue GlobalEscape(Isolate isolate, in BuiltinArguments args) =>
        Uri.Escape(isolate, ObjectOps.ToString(isolate, args.AtOrUndefined(1)));

    /// <summary>ES6 section B.2.1.2 unescape (string).</summary>
    public static JSValue GlobalUnescape(Isolate isolate, in BuiltinArguments args) =>
        Uri.Unescape(isolate, ObjectOps.ToString(isolate, args.AtOrUndefined(1)));

    /// <summary>ES6 section 18.2.1 eval (x): indirect eval.</summary>
    public static JSValue GlobalEval(Isolate isolate, in BuiltinArguments args)
    {
        JSValue x = args.AtOrUndefined(1);
        JSFunction target = args.Target;
        JSGlobalProxy targetGlobalProxy = target.GlobalProxy;
        // Builtins::AllowDynamicFunction is always true in V8Sharp (one
        // embedder, no differing security tokens).

        // Run embedder pre-checks before executing eval. If the argument is a
        // non-String (or other object the embedder doesn't know to handle), then
        // return it directly.
        NativeContext nativeContext = target.NativeContext;
        JSString? source = ValidateDynamicCompilationSource(isolate, nativeContext, x, out bool unhandledObject);
        if (unhandledObject) return x;

        if (source is null)
        {
            // Compiler::GetFunctionFromValidatedString with an empty source.
            return isolate.Throw(isolate.Factory.NewEvalError(MessageTemplate.CodeGenFromStrings,
                ErrorMessageForCodeGenerationFromStrings(isolate, nativeContext)));
        }

        IDynamicFunctionCompiler compiler = isolate.DynamicFunctionCompiler ??
            throw new InvalidOperationException("V8Sharp: no compiler registered (Isolate.DynamicFunctionCompiler)");
        JSFunction function = compiler.GetFunctionFromValidatedString(isolate, nativeContext, source,
            ParseRestriction.NO_PARSE_RESTRICTION, Globals.kNoSourcePosition);
        return Execution.Call(isolate, function, targetGlobalProxy, []);
    }

    /// <summary>
    /// Compiler::ValidateDynamicCompilationSource without embedder callbacks:
    /// the source string when code generation from strings is allowed;
    /// <paramref name="unhandledObject"/> is set for non-string arguments,
    /// which eval returns unchanged.
    /// </summary>
    public static JSString? ValidateDynamicCompilationSource(Isolate isolate, NativeContext context, JSValue originalSource,
        out bool unhandledObject)
    {
        // Check if the context unconditionally allows code gen from strings.
        // allow_code_gen_from_strings can be many things, so we'll always check
        // against the 'false' literal, so that e.g. undefined and 'true' are treated
        // the same.
        if (!context.AllowCodeGenFromStrings.IsFalse && originalSource.HeapObjectOrNull is JSString s)
        {
            unhandledObject = false;
            return s;
        }
        // If unconditional codegen was disabled, and no callback defined, we block
        // strings and allow all other objects.
        unhandledObject = !originalSource.IsString;
        return null;
    }

    /// <summary>NativeContext::ErrorMessageForCodeGenerationFromStrings.</summary>
    public static JSValue ErrorMessageForCodeGenerationFromStrings(Isolate isolate, NativeContext context)
    {
        JSValue message = context.ErrorMessageForCodeGenFromStrings;
        if (!message.IsUndefined) return message;
        return isolate.Factory.NewStringFromAsciiChecked("Code generation from strings disallowed for this context");
    }

    /// <summary>ES6 #sec-isfinite-number (TF_BUILTIN GlobalIsFinite).</summary>
    public static JSValue GlobalIsFinite(Isolate isolate, in BuiltinArguments args)
    {
        JSValue num = args.AtOrUndefined(1);
        // Need to convert {num} to a Number first.
        if (!num.IsNumber) num = ObjectOps.ToNumber(isolate, num);
        return JSValue.FromBoolean(double.IsFinite(num.Number));
    }

    /// <summary>ES6 #sec-isnan-number (TF_BUILTIN GlobalIsNaN).</summary>
    public static JSValue GlobalIsNaN(Isolate isolate, in BuiltinArguments args)
    {
        JSValue num = args.AtOrUndefined(1);
        if (!num.IsNumber) num = ObjectOps.ToNumber(isolate, num);
        return JSValue.FromBoolean(double.IsNaN(num.Number));
    }

    // ---------------------------------------------------------------------
    // number.tq: NumberParseFloat, ParseInt, NumberParseInt

    /// <summary>https://tc39.es/ecma262/#sec-number.parsefloat</summary>
    public static JSValue NumberParseFloat(Isolate isolate, in BuiltinArguments args)
    {
        JSValue value = args.AtOrUndefined(1);
        if (value.IsNumber)
        {
            // The input is already a Number. Take care of -0.
            // The sense of comparison is important for the NaN case.
            return value.Number == 0 ? JSValue.Zero : value;
        }
        JSString s = value.HeapObjectOrNull as JSString ?? ObjectOps.ToString(isolate, value);
        // Check if the string is a cached array index.
        if (TryGetCachedArrayIndex(s, out uint arrayIndex)) return JSValue.FromNumber(arrayIndex);
        // Fall back to the runtime to convert string to a number (Runtime_StringParseFloat).
        double result = Conversions.StringToDouble(s.Flatten(), ConversionFlag.AllowTrailingJunk, double.NaN);
        return JSValue.FromNumber(result);
    }

    /// <summary>https://tc39.es/ecma262/#sec-number.parseint</summary>
    public static JSValue NumberParseInt(Isolate isolate, in BuiltinArguments args) =>
        ParseInt(isolate, args.AtOrUndefined(1), args.AtOrUndefined(2));

    /// <summary>The ParseInt builtin (number.tq).</summary>
    public static JSValue ParseInt(Isolate isolate, JSValue input, JSValue radix)
    {
        // Check if radix should be 10 (i.e. undefined, 0 or 10).
        if (radix.IsUndefined || (radix.IsNumber && (radix.Number == 10 || radix.Number == 0) && radix.IsSmi))
        {
            if (input.IsNumber)
            {
                double asFloat64 = input.Number;
                // Check if the input value is in Signed32 range.
                int asInt32 = Conversions.DoubleToInt32(asFloat64);
                // The sense of comparison is important for the NaN case.
                if (asFloat64 == asInt32)
                {
                    return JSValue.FromInt(asInt32);
                }
                // Check if the absolute value of input is in the [1,1<<31[ range. Call
                // the runtime for the range [0,1[ because the result could be -0.
                const double kMaxAbsValue = 2147483648.0;
                double absInput = Math.Abs(asFloat64);
                if (absInput < kMaxAbsValue && absInput >= 1.0) return JSValue.FromInt(asInt32);
            }
            else if (input.HeapObjectOrNull is JSString s && TryGetCachedArrayIndex(s, out uint arrayIndex))
            {
                // Check if the string is a cached array index.
                return JSValue.FromNumber(arrayIndex);
            }
        }
        return StringParseInt(isolate, input, radix);
    }

    /// <summary>Runtime_StringParseInt.</summary>
    static JSValue StringParseInt(Isolate isolate, JSValue str, JSValue radix)
    {
        // Convert {string} to a String first, and flatten it.
        JSString subject = ObjectOps.ToString(isolate, str);

        // Convert {radix} to Int32.
        if (!radix.IsNumber) radix = ObjectOps.ToNumber(isolate, radix);
        int radix32 = Conversions.DoubleToInt32(radix.Number);
        if (radix32 != 0 && (radix32 < 2 || radix32 > 36)) return JSValue.NaN;

        double result = Conversions.StringToInt(subject.Flatten(), radix32);
        return JSValue.FromNumber(result);
    }

    /// <summary>IsIntegerIndex(hash) with a cached array index (the hash field fast path of number.tq).</summary>
    static bool TryGetCachedArrayIndex(JSString s, out uint index)
    {
        index = 0;
        return Name.ContainsCachedArrayIndex(s.RawHashField) && s.AsArrayIndex(out index);
    }
}
