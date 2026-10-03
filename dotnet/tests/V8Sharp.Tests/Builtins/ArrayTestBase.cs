// Helpers for the Array/TypedArray/ArrayBuffer/DataView/Atomics tests, run from
// C# before the interpreter runs scripts: global lookups, calls and constructs through Execution, C# callbacks as
// API functions, and checks on thrown errors.
using System.Globalization;
using V8Sharp.Builtins;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp.Tests.Builtins;

public abstract class ArrayTestBase : TestWithContext
{
    protected NativeContext NC => i_isolate.NativeContext;

    protected JSValue Undefined => JSValue.Undefined;

    protected static JSValue N(double d) => JSValue.FromNumber(d);

    protected JSValue S(string s) => factory.NewStringFromUtf16(s);

    protected JSValue Global(string name) => ObjectOps.GetProperty(i_isolate, NC.GlobalObject, factory.InternalizeString(name));

    protected JSValue Get(JSValue obj, string name) =>
        ObjectOps.GetPropertyOrElement(i_isolate, obj, new PropertyKey(i_isolate, factory.InternalizeString(name)));

    protected JSValue Get(JSValue obj, uint index) => ObjectOps.GetElement(i_isolate, obj, index);

    protected JSValue Get(JSValue obj, JSValue key) =>
        ObjectOps.GetPropertyOrElement(i_isolate, obj, new PropertyKey(i_isolate, ObjectOps.ToPropertyKey(i_isolate, key)));

    protected void Set(JSValue obj, string name, JSValue value) =>
        ObjectOps.SetProperty(i_isolate, obj, factory.InternalizeString(name), value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);

    protected void Set(JSValue obj, uint index, JSValue value) =>
        ObjectOps.SetElement(i_isolate, obj, index, value, ShouldThrow.ThrowOnError);

    protected JSValue Call(JSValue function, JSValue receiver, params JSValue[] args) =>
        Execution.Call(i_isolate, function, receiver, args);

    /// <summary>obj[name](...args).</summary>
    protected JSValue Invoke(JSValue obj, string name, params JSValue[] args) => Call(Get(obj, name), obj, args);

    /// <summary>Global[path](...args) for "Array.from" style paths.</summary>
    protected JSValue CallPath(string path, params JSValue[] args)
    {
        string[] parts = path.Split('.');
        JSValue holder = Global(parts[0]);
        JSValue current = holder;
        for (int i = 1; i < parts.Length; i++)
        {
            holder = current;
            current = Get(current, parts[i]);
        }
        return Call(current, holder, args);
    }

    protected JSValue New(string constructor, params JSValue[] args) => Execution.New(i_isolate, Global(constructor), args);

    protected JSValue New(JSValue constructor, params JSValue[] args) => Execution.New(i_isolate, constructor, args);

    /// <summary>A JS function running a C# callback (an API function).</summary>
    protected JSFunction Fn(Func<JSValue, JSValue[], JSValue> body, int length = 0, string name = "")
    {
        var data = new FunctionTemplateInfo((Isolate isolate, in BuiltinArguments args) =>
            body(args.Receiver, args.Arguments.ToArray())) { Length = length };
        SharedFunctionInfo info = factory.NewSharedFunctionInfo(factory.InternalizeString(name), data,
            Builtin.HandleApiCallOrConstruct, length, false);
        info.BuiltinId = Builtin.HandleApiCallOrConstruct;
        info.LanguageMode = LanguageMode.Strict;
        info.Native = true;
        info.UpdateFunctionMapIndex();
        return factory.NewFunction(info, NC, NC.StrictFunctionWithoutPrototypeMap);
    }

    /// <summary>A packed JS array of the values.</summary>
    protected JSArray Arr(params JSValue[] values)
    {
        JSValue array = New("Array");
        for (uint i = 0; i < values.Length; i++) Set(array, i, values[i]);
        return array.As<JSArray>();
    }

    protected JSArray Arr(params double[] values)
    {
        var v = new JSValue[values.Length];
        for (int i = 0; i < v.Length; i++) v[i] = N(values[i]);
        return Arr(v);
    }

    protected JSObject Obj() => factory.NewJSObject(NC.ObjectFunction);

    protected string Str(JSValue value) => ObjectOps.ToString(i_isolate, value).ToString()!;

    protected double Num(JSValue value) => ObjectOps.ToNumber(i_isolate, value).Number;

    /// <summary>Array elements as a comma separated string (via ToString of each element).</summary>
    protected string Elements(JSValue array)
    {
        double length = Num(Get(array, "length"));
        var parts = new string[(int)length];
        for (uint i = 0; i < length; i++)
        {
            JSValue v = Get(array, i);
            parts[i] = v.IsUndefined ? "undefined" : v.IsNumber ? NumberToJSString(v.Number) : Str(v);
        }
        return string.Join(",", parts);
    }

    protected static string NumberToJSString(double d)
    {
        if (d == 0 && double.IsNegative(d)) return "-0";
        return V8Sharp.Base.Numbers.Conversions.DoubleToCString(d);
    }

    /// <summary>Runs the action and returns "ErrorName: message" of the thrown JS error.</summary>
    protected string Throws(Action action)
    {
        try
        {
            action();
        }
        catch (JavaScriptException e)
        {
            JSValue value = e.Value;
            if (value.IsJSReceiver)
            {
                string name = Str(Get(value, "name"));
                string message = Str(Get(value, "message"));
                return name + ": " + message;
            }
            return "thrown: " + Str(value);
        }
        throw new Xunit.Sdk.XunitException("expected a JavaScript exception");
    }

    protected static string F(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
