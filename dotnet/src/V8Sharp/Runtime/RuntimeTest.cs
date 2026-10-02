// Port of the test runtime functions of src/runtime/runtime-test.cc (the
// %-natives of --allow-natives-syntax that mjsunit uses), as they behave in
// a V8 built without Turbofan and Maglev (Sparkplug is the only compiler):
// optimization requests are accepted and ignored, the status reports lite
// mode and the baseline tier, and the heap-shape queries answer from the
// object model.
using System.Globalization;
using V8Sharp.Base.Numbers;

namespace V8Sharp.Runtime;

public static partial class RuntimeTest
{
    // OptimizationStatus (src/runtime/runtime.h).
    const int kIsFunction = 1 << 0;
    const int kNeverOptimize = 1 << 1;
    const int kMaybeDeopted = 1 << 2;
    const int kOptimized = 1 << 3;
    const int kMaglevved = 1 << 4;
    const int kMarkedForDeoptimization = 1 << 13;
    const int kTopmostFrameIsMaglev = 1 << 18;
    const int kOptimizeOnNextCallOptimizesToMaglev = 1 << 19;
    const int kInterpreted = 1 << 6;
    const int kIsExecuting = 1 << 10;
    const int kLiteMode = 1 << 12;
    const int kBaseline = 1 << 14;
    const int kTopmostFrameIsInterpreted = 1 << 15;
    const int kTopmostFrameIsBaseline = 1 << 16;
    const int kIsLazy = 1 << 17;

    /// <summary>Runtime_GetOptimizationStatus.</summary>
    public static JSValue GetOptimizationStatus(Isolate isolate, JSValue functionObject)
    {
        // These modes cannot optimize. Unit tests should handle these the same way.
        // V8Sharp without --maglev answers as a V8 built without optimizing
        // compilers (lite mode); with --maglev as V8 run with --maglev
        // --no-turbofan, where %OptimizeFunctionOnNextCall optimizes to Maglev.
        int status = 0;
        if (!isolate.UseOptimizer) status |= kLiteMode | kNeverOptimize;
        else status |= kOptimizeOnNextCallOptimizesToMaglev;
        if (isolate.Flags.deopt_every_n_times != 0) status |= kMaybeDeopted;
        if (functionObject.IsUndefined) return JSValue.FromInt(status);
        if (functionObject.HeapObjectOrNull is not JSFunction function) return JSValue.FromInt(status);
        status |= kIsFunction;
        if (function.RawFeedbackCell.Value is FeedbackVector { MaglevCode: { } code })
        {
            status |= code.MarkedForDeoptimization ? kMarkedForDeoptimization : kOptimized;
            status |= kMaglevved;
        }
        if (function.Shared.HasBaselineCode) status |= kBaseline;
        // ActiveTierIsIgnition, or not compiled yet (the CompileLazy trampoline).
        if (TieringManager.ActiveTierIsIgnition(function) || !function.Shared.IsCompiled) status |= kInterpreted;
        if (!function.Shared.IsCompiled) status |= kIsLazy;

        // Additionally, detect activations of this frame on the stack, and report the
        // status of the topmost frame.
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        for (int i = isolate.InterpreterFrameDepth - 1; i >= 0; i--)
        {
            if (ReferenceEquals(frames[i].Function, function) && frames[i].Kind == InterpreterFrameKind.Interpreted)
            {
                status |= kIsExecuting | (frames[i].IsMaglev ? kTopmostFrameIsMaglev
                    : frames[i].IsBaseline ? kTopmostFrameIsBaseline : kTopmostFrameIsInterpreted);
                break;
            }
        }
        return JSValue.FromInt(status);
    }

    /// <summary>Runtime_CompileBaseline.</summary>
    public static JSValue CompileBaseline(Isolate isolate, JSValue functionObject)
    {
        if (functionObject.HeapObjectOrNull is not JSFunction function || !function.Shared.IsUserJavaScript())
        {
            throw new InvalidOperationException("V8Sharp: %CompileBaseline needs a user JavaScript function");
        }
        // First compile the bytecode, if we have to.
        if (!function.Shared.IsCompiled && !Codegen.Compiler.CompileLazy(isolate, function))
        {
            throw new InvalidOperationException("V8Sharp: %CompileBaseline could not compile the function");
        }
        if (!Codegen.Compiler.CompileBaseline(isolate, function))
        {
            throw new InvalidOperationException("V8Sharp: %CompileBaseline failed (is --sparkplug off?)");
        }
        return JSValue.Undefined;
    }

    /// <summary>Runtime_ActiveTierIsSparkplug.</summary>
    public static JSValue ActiveTierIsSparkplug(JSValue functionObject) =>
        JSValue.FromBoolean(functionObject.HeapObjectOrNull is JSFunction function && TieringManager.ActiveTierIsBaseline(function));

    /// <summary>
    /// Runtime_BaselineOsr: baseline-compiles the function of the topmost
    /// JavaScript frame, so its next JumpLoop continues in baseline code.
    /// </summary>
    public static JSValue BaselineOsr(Isolate isolate)
    {
        if (!isolate.Flags.sparkplug || !isolate.Flags.use_osr) return JSValue.Undefined;
        InterpreterFrameRecord[] frames = isolate.InterpreterFrames;
        for (int i = isolate.InterpreterFrameDepth - 1; i >= 0; i--)
        {
            if (frames[i].Kind != InterpreterFrameKind.Interpreted) continue;
            Codegen.Compiler.CompileBaseline(isolate, frames[i].Function);
            break;
        }
        return JSValue.Undefined;
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

    /// <summary>
    /// Runtime_IsBeingInterpreted: always true (Turbofan lowers the call to
    /// false, so it never reaches the runtime from optimized code; Maglev does
    /// not lower it).
    /// </summary>
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
        isolate.StdOut.WriteLine("DebugPrint: " + text);
        return value;
    }

    /// <summary>%DebugTraceMinimal: Isolate::PrintMinimalStack(stdout).</summary>
    public static JSValue DebugTraceMinimal(Isolate isolate)
    {
        isolate.StdOut.Write(BuildMinimalStack(isolate, int.MaxValue));
        return JSValue.Undefined;
    }

    /// <summary>
    /// Isolate::BuildMinimalStack with isolate.cc's MinimalStackPrinter: one
    /// line per JavaScript frame, "name in script:line:column", where a
    /// script repeated from the previous line prints as "=", ended by "$".
    /// </summary>
    public static string BuildMinimalStack(Isolate isolate, int maxLength)
    {
        const string kUnknownName = "<none>";
        const string kRepeatMarker = "=";
        const string kEndMarker = "$";
        var @out = new System.Text.StringBuilder();
        string? prevScriptName = null;
        IJavaScriptFrames? frames = isolate.Frames;
        if (frames is not null)
        {
            for (int i = 0; @out.Length < maxLength && frames.TryGetFrame(i, out JavaScriptFrameSummary summary); i++)
            {
                JSString name = summary.Function.Shared.Name();
                @out.Append(name.Length == 0 ? kUnknownName : name.ToString());
                Script? script = summary.Function.Shared.Script;
                if (script is not null && script.GetNameOrSourceURL().HeapObjectOrNull is JSString nameOrUrl)
                {
                    string currentName = nameOrUrl.ToString();
                    if (currentName == prevScriptName)
                    {
                        @out.Append(" in ").Append(kRepeatMarker);
                    }
                    else
                    {
                        @out.Append(" in ").Append(currentName);
                        prevScriptName = currentName;
                    }
                }
                // Source positions are always available: V8Sharp keeps the
                // source position table of every bytecode array.
                if (script is not null)
                {
                    int pos = summary.SourcePosition;
                    @out.Append(':').Append((script.GetLineNumber(pos) + 1).ToString(CultureInfo.InvariantCulture))
                        .Append(':').Append((script.GetColumnNumber(pos) + 1).ToString(CultureInfo.InvariantCulture));
                }
                @out.Append('\n');
            }
        }
        @out.Append(kEndMarker).Append('\n');
        return @out.ToString();
    }

    /// <summary>%Is64Bit.</summary>
    public static JSValue Is64Bit(Isolate isolate) => JSValue.FromBoolean(Environment.Is64BitProcess);

    /// <summary>%StringMaxLength.</summary>
    public static JSValue StringMaxLength(Isolate isolate) => JSValue.FromInt(JSString.kMaxLength);

    /// <summary>%GetUndetectable: an undetectable callable object (d8's Object with the undetectable map bit).</summary>
    public static JSValue GetUndetectable(Isolate isolate)
    {
        // v8::ObjectTemplate with MarkAsUndetectable and SetCallAsFunctionHandler(ReturnNull):
        // the instance's map is callable and its constructor is the template's API function.
        var templ = new FunctionTemplateInfo(static (Isolate _, in BuiltinArguments _) => JSValue.Undefined)
        {
            InstanceCallHandler = new FunctionTemplateInfo(static (Isolate _, in BuiltinArguments _) => JSValue.Null),
        };
        SharedFunctionInfo info = isolate.Factory.NewSharedFunctionInfo(ReadOnlyRoots.empty_string, templ,
            Builtin.HandleApiCallOrConstruct, 0, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        NativeContext nc = isolate.NativeContext;
        JSFunction constructor = isolate.Factory.NewFunction(info, nc, nc.StrictFunctionWithoutPrototypeMap);
        Map map = Map.Copy(isolate, nc.ObjectFunction.InitialMap, "Undetectable");
        map.IsUndetectable = true;
        map.IsCallable = true;
        map.SetConstructor(constructor);
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

    /// <summary>
    /// CHECK_UNLESS_FUZZING (runtime-test.cc): true when the check failed under
    /// --fuzzing (the caller returns undefined); a failed check without
    /// --fuzzing is V8's CHECK failure, a fatal error.
    /// </summary>
    internal static bool FailedUnlessFuzzing(Isolate isolate, bool condition)
    {
        if (condition) return false;
        if (isolate.Flags.fuzzing) return true;
        throw new InvalidOperationException("V8Sharp: runtime-test CHECK failed");
    }

    /// <summary>%ConstructConsString.</summary>
    public static JSValue ConstructConsString(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (FailedUnlessFuzzing(isolate, args.Length == 2)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, args[0].IsString)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, args[1].IsString)) return JSValue.Undefined;
        JSString left = args[0].As<JSString>(), right = args[1].As<JSString>();
        if (FailedUnlessFuzzing(isolate, left.Length + right.Length >= ConsString.kMinLength)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, left.Length + right.Length <= JSString.kMaxLength)) return JSValue.Undefined;
        return isolate.Factory.NewConsString(left, right);
    }

    /// <summary>%ConstructSlicedString.</summary>
    public static JSValue ConstructSlicedString(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (FailedUnlessFuzzing(isolate, args.Length == 2)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, args[0].IsString)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, args[1].IsSmi)) return JSValue.Undefined;
        JSString s = args[0].As<JSString>();
        uint index = unchecked((uint)(int)args[1].Number);
        if (FailedUnlessFuzzing(isolate, index < (uint)s.Length)) return JSValue.Undefined;
        JSString sliced = isolate.Factory.NewSubString(s, (int)index, s.Length);
        if (FailedUnlessFuzzing(isolate, sliced is SlicedString)) return JSValue.Undefined;
        return sliced;
    }

    /// <summary>%ConstructInternalizedString.</summary>
    public static JSValue ConstructInternalizedString(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (FailedUnlessFuzzing(isolate, args.Length == 1)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, args[0].IsString)) return JSValue.Undefined;
        return isolate.Factory.InternalizeString(args[0].As<JSString>());
    }

    /// <summary>
    /// %ConstructThinString. Deviation: V8Sharp has no ThinStrings (an
    /// internalized copy never forwards the original), so the string is
    /// returned as a cons string with the same contents.
    /// </summary>
    public static JSValue ConstructThinString(Isolate isolate, ReadOnlySpan<JSValue> args)
    {
        if (FailedUnlessFuzzing(isolate, args.Length == 1)) return JSValue.Undefined;
        if (FailedUnlessFuzzing(isolate, args[0].IsString)) return JSValue.Undefined;
        JSString s = args[0].As<JSString>();
        if (s is ConsString) return s;
        if (FailedUnlessFuzzing(isolate, s.Length >= ConsString.kMinLength)) return JSValue.Undefined;
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
