// Checks that the intrinsics of the Object, Function, Reflect, Proxy, global,
// Error, Boolean and Symbol areas have V8's shape: own keys in V8's order,
// attributes, and the name and length of their functions. The description is
// built with the builtins under test (Reflect.ownKeys,
// Object.getOwnPropertyDescriptor) and compared with the oracle's.
using System.Text;
using V8Sharp.Oracle;

namespace V8Sharp.Tests.Builtins;

public class ObjectIntrinsicsShapeTest : CoreBuiltinsTest
{
    static readonly string[] s_paths =
    [
        "Object", "Object.prototype", "Function", "Function.prototype", "Reflect", "Proxy", "Boolean", "Boolean.prototype",
        "Symbol", "Symbol.prototype", "Error", "Error.prototype", "TypeError", "TypeError.prototype", "RangeError",
        "RangeError.prototype", "SyntaxError.prototype", "ReferenceError.prototype", "EvalError.prototype", "URIError.prototype",
        "AggregateError", "AggregateError.prototype", "SuppressedError", "SuppressedError.prototype",
    ];

    static readonly string[] s_globals =
    [
        "decodeURI", "decodeURIComponent", "encodeURI", "encodeURIComponent", "escape", "unescape", "eval", "isFinite", "isNaN",
        "parseInt", "parseFloat", "Object", "Function", "Boolean", "Symbol", "Error", "Proxy", "Reflect", "globalThis",
    ];

    const string kOracleDescribe = """
        function __key(k) { return typeof k === 'symbol' ? k.toString() : k; }
        function __fn(v) { return typeof v === 'function' ? '(' + v.name + '/' + v.length + ')' : ''; }
        function __describe(o, keys) {
          var out = [];
          keys = keys || Reflect.ownKeys(o);
          for (var k of keys) {
            var d = Object.getOwnPropertyDescriptor(o, k);
            if (!d) { out.push(__key(k) + ':absent'); continue; }
            var a = (d.enumerable ? 'e' : '-') + (d.configurable ? 'c' : '-');
            if ('value' in d) out.push(__key(k) + ':' + (d.writable ? 'w' : '-') + a + __fn(d.value));
            else out.push(__key(k) + ':' + a + ' get' + __fn(d.get) + ' set' + __fn(d.set));
          }
          return out.join('\n');
        }
        globalThis.__describe = __describe;
        """;

    string Key(JSValue k) => S(k);

    string Fn(JSValue v)
    {
        if (!ObjectOps.IsCallable(v)) return "";
        return "(" + S(Get(v, "name")) + "/" + S(Get(v, "length")) + ")";
    }

    string Describe(JSValue o, string[]? names)
    {
        var sb = new StringBuilder();
        JSValue[] keys;
        if (names is null)
        {
            JSValue ownKeys = Call("Reflect.ownKeys", o);
            uint length = (uint)((JSArray)ownKeys.Object).Length.Number;
            keys = new JSValue[length];
            for (uint i = 0; i < length; i++) keys[i] = JSReceiver.GetElement(i_isolate, (JSArray)ownKeys.Object, i);
        }
        else
        {
            keys = Array.ConvertAll(names, n => Str(n));
        }
        foreach (JSValue k in keys)
        {
            if (sb.Length > 0) sb.Append('\n');
            JSValue d = Call("Object.getOwnPropertyDescriptor", o, k);
            if (d.IsUndefined)
            {
                sb.Append(Key(k)).Append(":absent");
                continue;
            }
            string a = (Get(d, "enumerable").IsTrue ? "e" : "-") + (Get(d, "configurable").IsTrue ? "c" : "-");
            if (Call("Object.hasOwn", d, Str("value")).IsTrue)
            {
                sb.Append(Key(k)).Append(':').Append(Get(d, "writable").IsTrue ? 'w' : '-').Append(a).Append(Fn(Get(d, "value")));
            }
            else
            {
                sb.Append(Key(k)).Append(':').Append(a).Append(" get").Append(Fn(Get(d, "get"))).Append(" set").Append(Fn(Get(d, "set")));
            }
        }
        return sb.ToString();
    }

    [Fact]
    public void IntrinsicsMatchOracle()
    {
        using var v8 = new ReferenceV8();
        v8.Run(kOracleDescribe);
        Assert.Contains("assign:w-c(assign/2)\ngetOwnPropertyDescriptor:w-c(getOwnPropertyDescriptor/2)", v8.Eval("__describe(Object)"));
        var failures = new List<string>();
        foreach (string path in s_paths)
        {
            string expected = v8.Eval($"__describe({path})");
            string actual = Describe(G(path), null);
            if (expected != actual) failures.Add($"--- {path}\n oracle:\n{expected}\n v8sharp:\n{actual}");
        }
        string globalNames = string.Join(",", s_globals.Select(n => "'" + n + "'"));
        string expectedGlobals = v8.Eval($"__describe(globalThis, [{globalNames}])");
        string actualGlobals = Describe(NC.GlobalObject, s_globals);
        if (expectedGlobals != actualGlobals) failures.Add($"--- globalThis\n oracle:\n{expectedGlobals}\n v8sharp:\n{actualGlobals}");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
