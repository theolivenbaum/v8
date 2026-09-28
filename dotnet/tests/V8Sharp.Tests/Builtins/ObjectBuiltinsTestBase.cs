// Helpers for the tests of the Object, Function, Reflect, Proxy, global,
// Error, Boolean and Symbol builtins. Until the interpreter runs scripts,
// the builtins are called through Execution.Call/New on the intrinsics of a
// fresh native context, with C# callbacks (API functions) standing in for
// JavaScript functions.
using System.Globalization;
using System.Text;

namespace V8Sharp.Tests.Builtins;

public abstract class CoreBuiltinsTest : TestWithContext
{
    protected NativeContext NC => i_isolate.NativeContext;

    protected JSValue Str(string s) => factory.NewStringFromUtf16(s);

    protected static JSValue Num(double d) => JSValue.FromNumber(d);

    protected JSValue Global => NC.GlobalProxyObject;

    /// <summary>Reads a dotted path from the global object, e.g. "Object.prototype.toString".</summary>
    protected JSValue G(string path)
    {
        JSValue current = NC.GlobalObject;
        foreach (string part in path.Split('.'))
        {
            current = ObjectOps.GetProperty(i_isolate, current, factory.InternalizeString(part));
        }
        return current;
    }

    protected JSValue Get(JSValue obj, string name) => Get(obj, Str(name));

    protected JSValue Get(JSValue obj, JSValue key)
    {
        var it = new LookupIterator(i_isolate, obj, PropertyKey.FromKey(i_isolate, key));
        return ObjectOps.GetProperty(ref it);
    }

    protected void Set(JSValue obj, string name, JSValue value) => Set(obj, Str(name), value);

    protected void Set(JSValue obj, JSValue key, JSValue value)
    {
        var it = new LookupIterator(i_isolate, obj, PropertyKey.FromKey(i_isolate, key));
        ObjectOps.SetProperty(ref it, value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);
    }

    /// <summary>Calls the function at <paramref name="path"/> with an undefined receiver... or the given one.</summary>
    protected JSValue Call(string path, params JSValue[] args) => Execution.Call(i_isolate, G(path), JSValue.Undefined, args);

    protected JSValue CallOn(string path, JSValue receiver, params JSValue[] args) =>
        Execution.Call(i_isolate, G(path), receiver, args);

    protected JSValue CallMethod(JSValue receiver, string name, params JSValue[] args) =>
        Execution.Call(i_isolate, Get(receiver, name), receiver, args);

    protected JSValue New(string path, params JSValue[] args) => Execution.New(i_isolate, G(path), args);

    /// <summary>A fresh ordinary object.</summary>
    protected JSObject Obj() => factory.NewJSObject(NC.ObjectFunction);

    /// <summary>An ordinary object with the given data properties, in order.</summary>
    protected JSObject Obj(params (string Key, JSValue Value)[] properties)
    {
        JSObject o = Obj();
        foreach ((string key, JSValue value) in properties) Set(o, key, value);
        return o;
    }

    /// <summary>A packed JSArray of the given values.</summary>
    protected JSArray Arr(params JSValue[] values) => factory.NewJSArrayWithElements(new FixedArray((JSValue[])values.Clone()));

    /// <summary>An API function running <paramref name="body"/> (a C# stand-in for a JavaScript function).</summary>
    protected JSFunction Fn(Func<JSValue, JSValue[], JSValue> body, string name = "", int length = 0)
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

    /// <summary>
    /// An iterable over <paramref name="values"/> built from API functions, so
    /// that the generic iteration paths run without the Array iterator.
    /// </summary>
    protected JSObject Iterable(params JSValue[] values)
    {
        JSObject iterable = Obj();
        JSFunction iteratorMethod = Fn((_, _) =>
        {
            int i = 0;
            JSObject iterator = Obj();
            Set(iterator, "next", Fn((_, _) =>
            {
                JSObject result = Obj();
                Set(result, "done", JSValue.FromBoolean(i >= values.Length));
                Set(result, "value", i < values.Length ? values[i] : JSValue.Undefined);
                i++;
                return result;
            }));
            return iterator;
        });
        ObjectOps.SetProperty(i_isolate, iterable, ReadOnlyRoots.iterator_symbol, iteratorMethod, StoreOrigin.MaybeKeyed,
            ShouldThrow.ThrowOnError);
        return iterable;
    }

    /// <summary>String(value) as JavaScript prints it, arrays joined with ",".</summary>
    protected string S(JSValue value)
    {
        if (value.HeapObjectOrNull is JSArray array)
        {
            var sb = new StringBuilder();
            uint length = (uint)array.Length.Number;
            for (uint i = 0; i < length; i++)
            {
                if (i > 0) sb.Append(',');
                JSValue element = JSReceiver.GetElement(i_isolate, array, i);
                if (!element.IsNullOrUndefined) sb.Append(S(element));
            }
            return sb.ToString();
        }
        if (value.IsSymbol) return BuiltinsSymbol.SymbolDescriptiveString(i_isolate, value.As<Symbol>()).ToString();
        return ObjectOps.ToString(i_isolate, value).ToString();
    }

    /// <summary>The keys of an array of property keys, "a,b,Symbol(c)".</summary>
    protected string Keys(JSValue array) => S(array);

    /// <summary>Runs <paramref name="action"/> and returns "TypeError: message" for the JavaScript exception it throws.</summary>
    protected string Throws(Action action)
    {
        try
        {
            action();
        }
        catch (JavaScriptException e)
        {
            return ErrorUtils.ToString(i_isolate, e.Value).ToString();
        }
        throw new Xunit.Sdk.XunitException("expected a JavaScript exception");
    }

    protected static string N(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
