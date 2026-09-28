// Helpers for testing builtins without the interpreter: look up intrinsics on
// the native context and call them through Execution.Call / Execution.New.
namespace V8Sharp.Tests.Builtins;

public abstract class BuiltinsTestBase : TestWithContext
{
    static BuiltinsTestBase()
    {
        // The species getters' builtin belongs to another area; register a
        // stand-in if that area has not landed yet.
        if (!BuiltinRegistry.IsImplemented(Builtin.ReturnReceiver))
        {
            BuiltinRegistry.Register(Builtin.ReturnReceiver, static (Isolate _, in BuiltinArguments args) => args.Receiver);
        }
    }

    protected Isolate isolate => i_isolate;

    protected JSValue S(string s) => factory.NewStringFromUtf16(s);

    protected static JSValue N(double d) => JSValue.FromNumber(d);

    protected JSValue Global(string name) =>
        ObjectOps.GetProperty(isolate, isolate.NativeContext.GlobalObject, factory.InternalizeString(name));

    protected Name Key(string name) => name switch
    {
        "@@match" => ReadOnlyRoots.match_symbol,
        "@@matchAll" => ReadOnlyRoots.match_all_symbol,
        "@@replace" => ReadOnlyRoots.replace_symbol,
        "@@search" => ReadOnlyRoots.search_symbol,
        "@@split" => ReadOnlyRoots.split_symbol,
        "@@iterator" => ReadOnlyRoots.iterator_symbol,
        _ => factory.InternalizeString(name),
    };

    protected JSValue Get(JSValue obj, string name) => ObjectOps.GetProperty(isolate, obj, Key(name));

    protected void Set(JSValue obj, string name, JSValue value) =>
        ObjectOps.SetProperty(isolate, obj, Key(name), value, StoreOrigin.MaybeKeyed, ShouldThrow.ThrowOnError);

    /// <summary>Calls String.prototype[name] (or another prototype's) with the receiver.</summary>
    protected JSValue Invoke(JSValue holder, string name, JSValue receiver, params JSValue[] args) =>
        Execution.Call(isolate, Get(holder, name), receiver, args);

    protected JSValue StringProto => isolate.NativeContext.InitialStringPrototype;
    protected JSValue RegExpProto => isolate.NativeContext.RegExpPrototype;

    /// <summary>"abc".name(args) through String.prototype.</summary>
    protected JSValue StrCall(JSValue receiver, string name, params JSValue[] args) => Invoke(StringProto, name, receiver, args);

    protected JSValue StrCall(string receiver, string name, params JSValue[] args) => StrCall(S(receiver), name, args);

    /// <summary>re.name(args) looked up on the regexp itself.</summary>
    protected JSValue ReCall(JSValue regexp, string name, params JSValue[] args) =>
        Execution.Call(isolate, Get(regexp, name), regexp, args);

    protected JSValue Re(string pattern, string flags = "") =>
        Execution.New(isolate, Global("RegExp"), [S(pattern), S(flags)]);

    protected string Str(JSValue v) => ObjectOps.ToString(isolate, v).Flatten();

    protected double Num(JSValue v) => ObjectOps.ToNumber(isolate, v).Number;

    /// <summary>A readable form of a value: arrays as [a,b], undefined, null, strings quoted.</summary>
    protected string Show(JSValue v)
    {
        if (v.IsUndefined) return "undefined";
        if (v.IsNull) return "null";
        if (v.HeapObjectOrNull is JSString s) return "\"" + s.Flatten() + "\"";
        if (v.HeapObjectOrNull is JSArray array)
        {
            int length = (int)array.Length.Number;
            var parts = new string[length];
            for (int i = 0; i < length; i++) parts[i] = Show(ObjectOps.GetElement(isolate, array, (uint)i));
            return "[" + string.Join(",", parts) + "]";
        }
        return Str(v);
    }

    /// <summary>Asserts that the action throws a JS error of the given constructor name and message.</summary>
    protected void AssertThrows(string errorName, Action action, string? message = null)
    {
        var ex = Assert.Throws<JavaScriptException>(action);
        JSValue name = ObjectOps.GetProperty(isolate, ex.Value, ReadOnlyRoots.name_string);
        Assert.Equal(errorName, Str(name));
        if (message is not null)
        {
            JSValue msg = ObjectOps.GetProperty(isolate, ex.Value, ReadOnlyRoots.message_string);
            Assert.Equal(message, Str(msg));
        }
    }
}
