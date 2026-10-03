// The runtime functions of the builtins and object model that mjsunit calls
// and RegisterTest does not cover: the String::Compare family of
// runtime-strings.cc (%StringLessThan ... %StringEqual), %NormalizeElements
// (runtime-test.cc) and %IterableForEach (runtime-debug.cc).
using V8Sharp.Builtins;

namespace V8Sharp.Runtime;

public static class RuntimeBuiltinsTest
{
    /// <summary>Runtime_StringLessThan and the other String::Compare runtime functions.</summary>
    public static JSValue StringCompare(JSString x, JSString y, ComparisonResult expected, bool orEqual)
    {
        ComparisonResult result = JSString.Compare(x, y);
        return JSValue.FromBoolean(result == expected || (orEqual && result == ComparisonResult.Equal));
    }

    /// <summary>Runtime_NormalizeElements.</summary>
    public static JSValue NormalizeElements(Isolate isolate, JSObject array)
    {
        if (array.HasTypedArrayOrRabGsabTypedArrayElements) throw new InvalidOperationException("CHECK failed: typed array");
        JSObject.NormalizeElements(isolate, array);
        return array;
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
}

public static partial class RuntimeTable
{
    static void RegisterBuiltinsTest()
    {
        Register(FunctionId.StringLessThanOrEqual, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.LessThan, true));
        Register(FunctionId.StringGreaterThan, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.GreaterThan, false));
        Register(FunctionId.StringGreaterThanOrEqual, static (i, a) =>
            RuntimeBuiltinsTest.StringCompare(a[0].As<JSString>(), a[1].As<JSString>(), ComparisonResult.GreaterThan, true));
        Register(FunctionId.StringEqual,
            static (i, a) => JSValue.FromBoolean(JSString.Equals(a[0].As<JSString>(), a[1].As<JSString>())));
        Register(FunctionId.NormalizeElements, static (i, a) => RuntimeBuiltinsTest.NormalizeElements(i, a[0].As<JSObject>()));
        Register(FunctionId.IterableForEach, static (i, a) => RuntimeBuiltinsTest.RunIterableForEach(i, a[0], a[1]));
    }
}
