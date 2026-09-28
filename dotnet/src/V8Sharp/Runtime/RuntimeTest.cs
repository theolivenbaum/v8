// Port of the test runtime functions of src/runtime/runtime-test.cc (the
// %-natives of --allow-natives-syntax that mjsunit uses), as they behave in
// a --jitless V8: optimization requests are accepted and ignored, the
// status reports lite mode, and the heap-shape queries answer from the
// object model.
using System.Globalization;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Runtime;

public static class RuntimeTest
{
    // OptimizationStatus (src/runtime/runtime.h).
    const int kIsFunction = 1 << 0;
    const int kNeverOptimize = 1 << 1;
    const int kInterpreted = 1 << 6;
    const int kIsExecuting = 1 << 10;
    const int kLiteMode = 1 << 12;
    const int kTopmostFrameIsInterpreted = 1 << 15;
    const int kIsLazy = 1 << 17;

    /// <summary>Runtime_GetOptimizationStatus.</summary>
    public static JSValue GetOptimizationStatus(Isolate isolate, JSValue functionObject)
    {
        // These modes cannot optimize. Unit tests should handle these the same way.
        int status = kLiteMode | kNeverOptimize;
        if (functionObject.IsUndefined) return JSValue.FromInt(status);
        if (functionObject.HeapObjectOrNull is not JSFunction function) return JSValue.FromInt(status);
        status |= kIsFunction;
        if (function.Shared.FunctionData is Interpreter.BytecodeArray || !function.Shared.IsCompiled) status |= kInterpreted;
        if (!function.Shared.IsCompiled) status |= kIsLazy;

        // Additionally, detect activations of this frame on the stack, and report the
        // status of the topmost frame.
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        for (int i = isolate.InterpreterFrameDepth - 1; i >= 0; i--)
        {
            if (ReferenceEquals(frames[i].Function, function) && frames[i].Kind == InterpreterFrameKind.Interpreted)
            {
                status |= kIsExecuting | kTopmostFrameIsInterpreted;
                break;
            }
        }
        return JSValue.FromInt(status);
    }

    /// <summary>
    /// %PrepareFunctionForOptimization / %EnsureFeedbackVectorForFunction:
    /// compiles the function and allocates its feedback vector.
    /// </summary>
    public static JSValue EnsureFeedbackVector(Isolate isolate, JSValue functionObject)
    {
        if (functionObject.HeapObjectOrNull is not JSFunction function) return JSValue.Undefined;
        if (!function.Shared.IsCompiled && !Codegen.Compiler.CompileLazy(isolate, function)) return JSValue.Undefined;
        if (function.Shared.FunctionData is Interpreter.BytecodeArray) JSFunctionFeedback.EnsureFeedbackVector(isolate, function);
        return JSValue.Undefined;
    }

    /// <summary>%ClearFunctionFeedback.</summary>
    public static JSValue ClearFunctionFeedback(Isolate isolate, JSValue functionObject)
    {
        if (functionObject.HeapObjectOrNull is JSFunction function && JSFunctionFeedback.GetFeedbackVector(function) is { } vector)
        {
            vector.ClearSlots(isolate, ClearBehavior.kClearAll);
        }
        return JSValue.Undefined;
    }

    /// <summary>%IsBeingInterpreted: always, V8Sharp has only the interpreter.</summary>
    public static JSValue IsBeingInterpreted(Isolate isolate) => JSValue.True;

    /// <summary>%HasFastProperties.</summary>
    public static JSValue HasFastProperties(Isolate isolate, JSValue obj) =>
        JSValue.FromBoolean(obj.HeapObjectOrNull is JSReceiver r && r.HasFastProperties);

    /// <summary>%HaveSameMap.</summary>
    public static JSValue HaveSameMap(Isolate isolate, JSValue a, JSValue b) =>
        JSValue.FromBoolean(a.HeapObjectOrNull is JSReceiver ra && b.HeapObjectOrNull is JSReceiver rb &&
                            ReferenceEquals(ra.Map, rb.Map));

    /// <summary>%IsSmi.</summary>
    public static JSValue IsSmi(Isolate isolate, JSValue obj) => JSValue.FromBoolean(obj.IsSmi);

    /// <summary>ELEMENTS_KIND_CHECK_RUNTIME_FUNCTION: a predicate on the object's elements kind.</summary>
    public static JSValue HasElementsKind(JSValue obj, Func<ElementsKind, bool> predicate) =>
        JSValue.FromBoolean(obj.HeapObjectOrNull is JSObject o && predicate(o.GetElementsKind()));

    /// <summary>%ToFastProperties.</summary>
    public static JSValue ToFastProperties(Isolate isolate, JSValue obj)
    {
        if (obj.HeapObjectOrNull is JSObject o && o is not JSGlobalObject && !o.HasFastProperties)
        {
            JSObject.MigrateSlowToFast(isolate, o, 0, "RuntimeToFastProperties");
        }
        return obj;
    }

    /// <summary>%DebugPrint: a short description of the value on stdout (V8 prints the full object in debug builds).</summary>
    public static JSValue DebugPrint(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        JSValue value = args.Length > 0 ? args[0] : JSValue.Undefined;
        string text;
        if (value.IsSmi)
        {
            text = "Smi: 0x" + ((int)value.Number).ToString("x", CultureInfo.InvariantCulture) + " (" +
                   ((int)value.Number).ToString(CultureInfo.InvariantCulture) + ")";
        }
        else
        {
            text = ObjectOps.NoSideEffectsToString(isolate, value).ToString();
        }
        Console.Out.WriteLine("DebugPrint: " + text);
        return value;
    }

    /// <summary>%Is64Bit.</summary>
    public static JSValue Is64Bit(Isolate isolate) => JSValue.FromBoolean(Environment.Is64BitProcess);

    /// <summary>%StringMaxLength.</summary>
    public static JSValue StringMaxLength(Isolate isolate) => JSValue.FromInt(JSString.kMaxLength);

    /// <summary>%GetUndetectable: an undetectable callable object (d8's Object with the undetectable map bit).</summary>
    public static JSValue GetUndetectable(Isolate isolate)
    {
        Map map = Map.Copy(isolate, isolate.NativeContext.ObjectFunction.InitialMap, "Undetectable");
        map.IsUndetectable = true;
        return isolate.Factory.NewJSObjectFromMap(map);
    }

    /// <summary>%Equal (Object::Equals).</summary>
    public static JSValue Equal(Isolate isolate, JSValue a, JSValue b) => JSValue.FromBoolean(ObjectOps.Equals(isolate, a, b));

    /// <summary>%NotEqual.</summary>
    public static JSValue NotEqual(Isolate isolate, JSValue a, JSValue b) => JSValue.FromBoolean(!ObjectOps.Equals(isolate, a, b));

    /// <summary>%IsInternalizedString.</summary>
    public static JSValue IsInternalizedString(Isolate isolate, JSValue obj) =>
        JSValue.FromBoolean(obj.HeapObjectOrNull is JSString s && s.IsInternalized);

    /// <summary>%InternalizeString.</summary>
    public static JSValue InternalizeString(Isolate isolate, JSValue obj) =>
        isolate.Factory.InternalizeString(obj.As<JSString>());

    /// <summary>%GetFunctionForCurrentFrame.</summary>
    public static JSValue GetFunctionForCurrentFrame(Isolate isolate)
    {
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        for (int i = isolate.InterpreterFrameDepth - 1; i >= 0; i--)
        {
            if (frames[i].Kind == InterpreterFrameKind.Interpreted) return frames[i].Function!;
        }
        return JSValue.Undefined;
    }

    /// <summary>%CreatePrivateSymbol.</summary>
    public static JSValue CreatePrivateSymbol(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        Symbol symbol = isolate.Factory.NewPrivateSymbol();
        if (args.Length > 0 && args[0].IsString) symbol = new Symbol(args[0]) { PrivateSymbolKind = PrivateSymbolKind.Internal };
        return symbol;
    }

    /// <summary>%HasOwnConstDataProperty: V8Sharp does not track dictionary constness; answers for fast properties.</summary>
    public static JSValue HasOwnConstDataProperty(Isolate isolate, JSValue obj, JSValue property)
    {
        if (obj.HeapObjectOrNull is not JSObject o) return JSValue.Undefined;
        PropertyKey key = PropertyKey.FromKey(isolate, property);
        var it = new LookupIterator(isolate, o, key, LookupIterator.Configuration.OWN);
        if (it.State != LookupIterator.StateKind.DATA) return JSValue.Undefined;
        return JSValue.FromBoolean(!it.IsDictionaryHolder && it.Constness == PropertyConstness.Const);
    }

    /// <summary>The natives that are no-ops under --jitless (optimization, deopt, GC hints).</summary>
    public static JSValue ReturnUndefined(Isolate isolate, ReadOnlySpan<JSValue> args) => JSValue.Undefined;

    public static JSValue ReturnTrue(Isolate isolate, ReadOnlySpan<JSValue> args) => JSValue.True;

    public static JSValue ReturnFalse(Isolate isolate, ReadOnlySpan<JSValue> args) => JSValue.False;

    // ---- Strings (runtime-test.cc / runtime-strings.cc) -----------------------------------

    /// <summary>%ConstructConsString.</summary>
    public static JSValue ConstructConsString(Isolate isolate, JSValue left, JSValue right) =>
        isolate.Factory.NewConsString(left.As<JSString>(), right.As<JSString>());

    /// <summary>%ConstructSlicedString.</summary>
    public static JSValue ConstructSlicedString(Isolate isolate, JSValue str, JSValue index)
    {
        JSString s = str.As<JSString>();
        return isolate.Factory.NewSubString(s, (int)index.Number, s.Length);
    }

    /// <summary>%ConstructInternalizedString.</summary>
    public static JSValue ConstructInternalizedString(Isolate isolate, JSValue str) =>
        isolate.Factory.InternalizeString(str.As<JSString>());

    /// <summary>
    /// %ConstructThinString. Deviation: V8Sharp has no ThinStrings (an
    /// internalized copy never forwards the original), so the string is
    /// returned as a cons string with the same contents.
    /// </summary>
    public static JSValue ConstructThinString(Isolate isolate, JSValue str)
    {
        JSString s = str.As<JSString>();
        if (s is ConsString) return s;
        return isolate.Factory.NewConsString(ReadOnlyRoots.empty_string, s);
    }

    /// <summary>%FlattenString.</summary>
    public static JSValue FlattenString(Isolate isolate, JSValue str) => JSString.Flatten(isolate, str.As<JSString>());

    /// <summary>%StringIsFlat.</summary>
    public static JSValue StringIsFlat(JSValue str) => JSValue.FromBoolean(str.As<JSString>() is not ConsString);

    /// <summary>%StringLessThan.</summary>
    public static JSValue StringLessThan(Isolate isolate, JSValue x, JSValue y) =>
        JSValue.FromBoolean(ObjectOps.Compare(isolate, x, y) == ComparisonResult.LessThan);

    // ---- Numbers (runtime-numbers.cc / runtime-test.cc) -------------------------------------

    /// <summary>%MaxSmi.</summary>
    public static JSValue MaxSmi() => JSValue.FromInt(JSValue.SmiMaxValue);

    /// <summary>%GetHoleNaN: a number with the hole NaN's bits.</summary>
    public static JSValue GetHoleNaN() => JSValue.FromNumber(FixedDoubleArray.HoleNaN);

    /// <summary>%GetHoleNaNUpper / %GetHoleNaNLower.</summary>
    public static JSValue GetHoleNaNUpper() => JSValue.FromNumber(unchecked((uint)((ulong)EngineGlobals.kHoleNanInt64 >> 32)));

    public static JSValue GetHoleNaNLower() => JSValue.FromNumber(unchecked((uint)(ulong)EngineGlobals.kHoleNanInt64));

    /// <summary>%ConstructDouble(hi, lo).</summary>
    public static JSValue ConstructDouble(JSValue hi, JSValue lo)
    {
        ulong bits = ((ulong)Conversions.NumberToUint32(hi.Number) << 32) | Conversions.NumberToUint32(lo.Number);
        return JSValue.FromNumber(BitConverter.UInt64BitsToDouble(bits));
    }

    /// <summary>%DoubleToStringWithRadix.</summary>
    public static JSValue DoubleToStringWithRadix(Isolate isolate, JSValue number, JSValue radix) =>
        isolate.Factory.NewStringFromUtf16(Conversions.DoubleToRadixCString(number.Number, Conversions.NumberToInt32(radix.Number)));

    /// <summary>%StringParseInt.</summary>
    public static JSValue StringParseInt(Isolate isolate, JSValue str, JSValue radix)
    {
        JSString subject = JSString.Flatten(isolate, ObjectOps.ToString(isolate, str));
        if (!radix.IsNumber) radix = ObjectOps.ToNumber(isolate, radix);
        int radix32 = Conversions.DoubleToInt32(radix.Number);
        if (radix32 != 0 && (radix32 < 2 || radix32 > 36)) return JSValue.FromNumber(double.NaN);
        return JSValue.FromNumber(Conversions.StringToInt(subject.FlatSpan(), radix32));
    }

    // ---- Objects -------------------------------------------------------------------------

    /// <summary>%IsArray.</summary>
    public static JSValue IsArray(JSValue obj) => JSValue.FromBoolean(obj.HeapObjectOrNull is JSArray);

    /// <summary>%IsSameHeapObject.</summary>
    public static JSValue IsSameHeapObject(JSValue a, JSValue b) =>
        JSValue.FromBoolean(a.IsHeapObject && ReferenceEquals(a.HeapObjectOrNull, b.HeapObjectOrNull));

    /// <summary>%SymbolIsPrivate.</summary>
    public static JSValue SymbolIsPrivate(JSValue symbol) => JSValue.FromBoolean(symbol.As<Symbol>().IsAnyPrivate);

    /// <summary>%Typeof.</summary>
    public static JSValue Typeof(Isolate isolate, JSValue value) => ObjectOps.TypeOf(isolate, value);

    /// <summary>%EnqueueMicrotask.</summary>
    public static JSValue EnqueueMicrotask(Isolate isolate, JSValue function)
    {
        var f = function.As<JSFunction>();
        f.NativeContext.MicrotaskQueue?.EnqueueMicrotask(new CallableTask(f, f.NativeContext));
        return JSValue.Undefined;
    }

    /// <summary>%NewRegExpWithBacktrackLimit.</summary>
    public static JSValue NewRegExpWithBacktrackLimit(Isolate isolate, JSValue pattern, JSValue flags, JSValue limit)
    {
        RegExp.RegExpFlags? parsed = JSRegExp.FlagsFromString(isolate, flags.As<JSString>());
        if (parsed is null) return isolate.ThrowTypeError(MessageTemplate.InvalidRegExpFlags, flags);
        return JSRegExp.New(isolate, pattern.As<JSString>(), parsed.Value, (uint)(int)limit.Number);
    }

    /// <summary>%CollectGarbage and friends: a full .NET collection.</summary>
    public static JSValue CollectGarbage()
    {
        GC.Collect();
        return JSValue.Undefined;
    }
}
