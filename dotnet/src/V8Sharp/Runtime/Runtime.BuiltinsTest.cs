// The runtime functions of the builtins and object model that mjsunit calls:
// the string, regexp, elements and number queries of src/runtime/runtime-test.cc
// (%ConstructConsString, %RegexpTypeTag, %HasCowElements, %GetHoleNaN ...),
// runtime-strings.cc (%FlattenString, %StringLessThan), runtime-numbers.cc
// (%MaxSmi), runtime-object.cc (%OptimizeObjectForAddingMultipleProperties,
// %NormalizeElements via runtime-test.cc), runtime-debug.cc
// (%IterableForEach), runtime-symbol.cc (%SymbolIsPrivate) and
// runtime-array.cc (%IsArray). Tier queries that only describe V8Sharp's
// single tier (%IsMaglevEnabled ...) answer for the interpreter.
using V8Sharp.Builtins;
using V8Sharp.RegExp;

namespace V8Sharp.Runtime;

public static class RuntimeBuiltinsTest
{
    // ---- strings (runtime-test.cc, runtime-strings.cc) ----------------------------------------

    /// <summary>Runtime_ConstructConsString.</summary>
    public static JSValue ConstructConsString(Isolate isolate, JSString left, JSString right)
    {
        if (left.Length + right.Length < ConsString.kMinLength) throw new InvalidOperationException("CHECK failed: cons length");
        return isolate.Factory.NewConsString(left, right);
    }

    /// <summary>Runtime_ConstructSlicedString: a substring from <paramref name="index"/> to the end.</summary>
    public static JSValue ConstructSlicedString(Isolate isolate, JSString str, int index)
    {
        if ((uint)index >= (uint)str.Length) throw new InvalidOperationException("CHECK failed: slice index");
        return isolate.Factory.NewSubString(str, index, str.Length);
    }

    /// <summary>
    /// Runtime_ConstructInternalizedString / Runtime_ConstructThinString: the
    /// internalized string (V8Sharp has no ThinString; the original string
    /// forwards to its internalized copy).
    /// </summary>
    public static JSValue ConstructInternalizedString(Isolate isolate, JSString str)
    {
        isolate.Factory.InternalizeString(str);
        return str;
    }

    /// <summary>Runtime_FlattenString.</summary>
    public static JSValue FlattenString(Isolate isolate, JSString str) => JSString.Flatten(isolate, str);

    /// <summary>Runtime_StringIsFlat.</summary>
    public static JSValue StringIsFlat(JSString str) => JSValue.FromBoolean(str.IsFlat);

    /// <summary>Runtime_StringLessThan (and the other String::Compare runtime functions).</summary>
    public static JSValue StringCompare(Isolate isolate, JSString x, JSString y, ComparisonResult expected, bool orEqual)
    {
        ComparisonResult result = JSString.Compare(x, y);
        return JSValue.FromBoolean(result == expected || (orEqual && result == ComparisonResult.Equal));
    }

    // ---- regexps (runtime-test.cc) -----------------------------------------------------------

    /// <summary>Runtime_NewRegExpWithBacktrackLimit.</summary>
    public static JSValue NewRegExpWithBacktrackLimit(Isolate isolate, JSString pattern, JSString flagsString, int backtrackLimit)
    {
        if (backtrackLimit < 0) throw new InvalidOperationException("CHECK failed: backtrack limit");
        RegExpFlags flags = JSRegExp.FlagsFromString(isolate, flagsString) ??
            throw new InvalidOperationException("CHECK failed: regexp flags");
        return JSRegExp.New(isolate, pattern, flags, (uint)backtrackLimit);
    }

    // ---- elements and objects --------------------------------------------------------------

    /// <summary>Runtime_NormalizeElements.</summary>
    public static JSValue NormalizeElements(Isolate isolate, JSObject array)
    {
        if (array.HasTypedArrayOrRabGsabTypedArrayElements) throw new InvalidOperationException("CHECK failed: typed array");
        JSObject.NormalizeElements(isolate, array);
        return array;
    }

    /// <summary>Runtime_HasCowElements.</summary>
    public static JSValue HasCowElements(JSArray array) => JSValue.FromBoolean(array.Elements.IsCowArray);

    /// <summary>Runtime_OptimizeObjectForAddingMultipleProperties.</summary>
    public static JSValue OptimizeObjectForAddingMultipleProperties(Isolate isolate, JSObject obj, int properties)
    {
        // Conservative upper limit to prevent fuzz tests from going OOM.
        if (properties > 100000) return isolate.ThrowIllegalOperation();
        if (obj.HasFastProperties && obj is not JSGlobalProxy)
        {
            JSObject.NormalizeProperties(isolate, obj, PropertyNormalizationMode.KEEP_INOBJECT_PROPERTIES, properties,
                "OptimizeForAdding");
        }
        return obj;
    }

    /// <summary>Runtime_IterableForEach (runtime-debug.cc): calls <paramref name="callback"/> with each value.</summary>
    public static JSValue RunIterableForEach(Isolate isolate, JSValue iterable, JSValue callback)
    {
        var visitor = new CallbackVisitor(isolate, callback);
        IterableForEach.Run(isolate, iterable, ref visitor, allowJSExecution: true, out _, null);
        return JSValue.Undefined;
    }

    struct CallbackVisitor(Isolate isolate, JSValue callback) : IterableForEach.IVisitor
    {
        public bool VisitInt(int value) => VisitGeneric(JSValue.FromInt(value));
        public bool VisitDouble(double value) => VisitGeneric(JSValue.FromNumber(value));

        public bool VisitGeneric(JSValue value)
        {
            Execution.Call(isolate, callback, JSValue.Undefined, [value]);
            return true;
        }
    }

    // ---- numbers (runtime-test.cc, runtime-numbers.cc) --------------------------------------

    /// <summary>Runtime_ConstructDouble: the double with bits (hi, lo).</summary>
    public static JSValue ConstructDouble(JSValue hi, JSValue lo)
    {
        ulong bits = ((ulong)(uint)hi.Number << 32) | (uint)lo.Number;
        return JSValue.FromNumber(BitConverter.UInt64BitsToDouble(bits));
    }
}

public static partial class RuntimeTable
{
    static void RegisterBuiltinsTest()
    {
        // Strings.
        Register(FunctionId.ConstructConsString,
            static (i, a) => RuntimeBuiltinsTest.ConstructConsString(i, a[0].As<JSString>(), a[1].As<JSString>()));
        Register(FunctionId.ConstructSlicedString,
            static (i, a) => RuntimeBuiltinsTest.ConstructSlicedString(i, a[0].As<JSString>(), (int)a[1].Number));
        Register(FunctionId.ConstructInternalizedString,
            static (i, a) => RuntimeBuiltinsTest.ConstructInternalizedString(i, a[0].As<JSString>()));
        Register(FunctionId.ConstructThinString, static (i, a) => RuntimeBuiltinsTest.ConstructInternalizedString(i, a[0].As<JSString>()));
        Register(FunctionId.FlattenString, static (i, a) => RuntimeBuiltinsTest.FlattenString(i, a[0].As<JSString>()));
        Register(FunctionId.StringIsFlat, static (i, a) => RuntimeBuiltinsTest.StringIsFlat(a[0].As<JSString>()));
        Register(FunctionId.StringLessThan, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(i, a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.LessThan, false));
        Register(FunctionId.StringLessThanOrEqual, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(i, a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.LessThan, true));
        Register(FunctionId.StringGreaterThan, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(i, a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.GreaterThan, false));
        Register(FunctionId.StringGreaterThanOrEqual, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(i, a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.GreaterThan, true));
        Register(FunctionId.StringEqual,
            static (i, a) => JSValue.FromBoolean(JSString.Equals(a[0].As<JSString>(), a[1].As<JSString>())));

        // RegExps (typed methods in Runtime.Regexp.cs).
        Register(FunctionId.RegexpHasBytecode,
            static (i, a) => JSValue.FromBoolean(RuntimeRegExp.RegexpHasBytecode(a[0].As<JSRegExp>(), a[1].IsTrue)));
        Register(FunctionId.RegexpHasNativeCode,
            static (i, a) => JSValue.FromBoolean(RuntimeRegExp.RegexpHasNativeCode(a[0].As<JSRegExp>(), a[1].IsTrue)));
        Register(FunctionId.RegexpTypeTag, static (i, a) => RuntimeRegExp.RegexpTypeTag(i, a[0].As<JSRegExp>()));
        Register(FunctionId.RegexpIsUnmodified,
            static (i, a) => JSValue.FromBoolean(RuntimeRegExp.RegexpIsUnmodified(i, a[0].As<JSRegExp>())));
        Register(FunctionId.NewRegExpWithBacktrackLimit, static (i, a) =>
            RuntimeBuiltinsTest.NewRegExpWithBacktrackLimit(i, a[0].As<JSString>(), a[1].As<JSString>(), (int)a[2].Number));

        // Elements, objects, symbols.
        Register(FunctionId.NormalizeElements, static (i, a) => RuntimeBuiltinsTest.NormalizeElements(i, a[0].As<JSObject>()));
        Register(FunctionId.HasCowElements, static (i, a) => RuntimeBuiltinsTest.HasCowElements(a[0].As<JSArray>()));
        Register(FunctionId.OptimizeObjectForAddingMultipleProperties,
            static (i, a) => RuntimeBuiltinsTest.OptimizeObjectForAddingMultipleProperties(i, a[0].As<JSObject>(), (int)a[1].Number));
        Register(FunctionId.IterableForEach, static (i, a) => RuntimeBuiltinsTest.RunIterableForEach(i, a[0], a[1]));
        Register(FunctionId.IsArray, static (i, a) => JSValue.FromBoolean(a[0].HeapObjectOrNull is JSArray));
        Register(FunctionId.SymbolIsPrivate, static (i, a) => JSValue.FromBoolean(a[0].As<Symbol>().IsPrivate));
        Register(FunctionId.CreateAsyncFromSyncIterator,
            static (i, a) => AsyncFromSyncIteratorBuiltins.CreateAsyncFromSyncIterator(i, a[0]));
        Register(FunctionId.ReferenceEqual, static (i, a) => JSValue.FromBoolean(a[0].IsIdenticalTo(a[1])));
        Register(FunctionId.CompleteInobjectSlackTracking, static (i, a) => JSValue.Undefined);
        Register(FunctionId.PretenureAllocationSite, static (i, a) => JSValue.Undefined);
        Register(FunctionId.SetForceSlowPath, static (i, a) => JSValue.Undefined);

        // Numbers.
        Register(FunctionId.MaxSmi, static (i, a) => JSValue.FromInt(JSValue.SmiMaxValue));
        Register(FunctionId.GetHoleNaN, static (i, a) => JSValue.FromNumber(FixedDoubleArray.HoleNaN));
        Register(FunctionId.GetHoleNaNUpper, static (i, a) => JSValue.FromNumber(unchecked((uint)((ulong)EngineGlobals.kHoleNanInt64 >> 32))));
        Register(FunctionId.GetHoleNaNLower, static (i, a) => JSValue.FromNumber(unchecked((uint)EngineGlobals.kHoleNanInt64)));
        Register(FunctionId.ConstructDouble, static (i, a) => RuntimeBuiltinsTest.ConstructDouble(a[0], a[1]));
        Register(FunctionId.AllocateHeapNumber, static (i, a) => JSValue.FromNumber(0));
        Register(FunctionId.AllocateHeapNumberWithValue, static (i, a) => JSValue.FromNumber(a[0].Number));

        // Build and tier configuration.
        Register(FunctionId.IsUndefinedDoubleEnabled, static (i, a) => JSValue.False);
        Register(FunctionId.ICsAreEnabled, static (i, a) => JSValue.FromBoolean(i.Flags.use_ic));
        Register(FunctionId.IsMaglevEnabled, static (i, a) => JSValue.False);
        Register(FunctionId.IsSparkplugEnabled, static (i, a) => JSValue.False);
        Register(FunctionId.CurrentFrameIsTurbofan, static (i, a) => JSValue.False);
        Register(FunctionId.RunningInSimulator, static (i, a) => JSValue.False);
        Register(FunctionId.InLargeObjectSpace, static (i, a) => JSValue.False);
        Register(FunctionId.CollectGarbage, static (i, a) =>
        {
            i.CollectGarbage();
            return JSValue.Undefined;
        });
        Register(FunctionId.MajorGCForCompilerTesting, static (i, a) =>
        {
            i.CollectGarbage();
            return JSValue.Undefined;
        });
    }
}
