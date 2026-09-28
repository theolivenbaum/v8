// Helpers for testing builtins without the interpreter: look up intrinsics on
// the global object and invoke them through Execution.Call / Execution.New.
using System.Globalization;
using V8Sharp.Base.Numbers;
using V8Sharp.Builtins;

namespace V8Sharp.Tests.Builtins;

public abstract class BuiltinsTestBase : TestWithContext
{
    protected BuiltinsTestBase()
    {
        BuiltinRegistry.RegisterAll();
    }

    protected JSGlobalObject Global => i_isolate.NativeContext.GlobalObject;

    /// <summary>A property of an object, by a dotted path ("Math.max", "Number.prototype.toFixed").</summary>
    protected JSValue Get(JSValue obj, string path)
    {
        foreach (string part in path.Split('.'))
        {
            obj = ObjectOps.GetProperty(i_isolate, obj, factory.InternalizeString(part));
        }
        return obj;
    }

    /// <summary>A global, by a dotted path.</summary>
    protected JSValue G(string path) => Get(Global, path);

    protected JSValue Call(string path, JSValue receiver, params JSValue[] args) =>
        Execution.Call(i_isolate, G(path), receiver, args);

    protected JSValue CallStatic(string path, params JSValue[] args)
    {
        int dot = path.LastIndexOf('.');
        JSValue holder = dot < 0 ? Global : G(path[..dot]);
        return Execution.Call(i_isolate, Get(holder, path[(dot + 1)..]), holder, args);
    }

    protected JSValue New(string path, params JSValue[] args) => Execution.New(i_isolate, G(path), args);

    protected static JSValue Num(double d) => JSValue.FromNumber(d);

    protected JSValue Str(string s) => factory.NewStringFromUtf16(s);

    /// <summary>ToString of a value, as a .NET string.</summary>
    protected string S(JSValue v) => ObjectOps.ToString(i_isolate, v).ToCString();

    /// <summary>The message of a thrown error ("TypeError: ...").</summary>
    protected string Throws(Func<JSValue> action)
    {
        try
        {
            action();
        }
        catch (JavaScriptException e)
        {
            // Error.prototype.toString belongs to another builtins area; read
            // name and message directly.
            if (e.Value.IsJSReceiver)
            {
                return S(Get(e.Value, "name")) + ": " + S(Get(e.Value, "message"));
            }
            return S(e.Value);
        }
        throw new Xunit.Sdk.XunitException("expected a JavaScript exception");
    }

    protected static string D(double d) => Conversions.DoubleToCString(d);

    protected static string Inv(double d) => d.ToString("R", CultureInfo.InvariantCulture);
}
