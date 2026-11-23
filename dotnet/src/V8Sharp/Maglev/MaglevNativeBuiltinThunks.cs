// V8Sharp: the managed side's calls of the CallBuiltin nodes of native Maglev
// code (MaglevNativeCode.Execute). V8's Maglev calls the builtin from its
// machine code; native code exits instead and the managed side calls it. A
// MethodInvoker call boxes every argument into an object[]; a thunk per
// builtin method (IL, compiled once per process and shared by every graph)
// takes the arguments as JSValues and the per-node constants as objects,
// converts them to the parameter types as MaglevNativeCode.Convert does, and
// returns the result as a JSValue.
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using V8Sharp.Objects;

namespace V8Sharp.Maglev;

/// <summary>A builtin's call: the JSValue arguments (by parameter), the constant ones as objects.</summary>
internal delegate JSValue NativeBuiltinThunk(Isolate isolate, JSValue[] values, object?[] objects);

internal static class NativeBuiltinThunks
{
    static readonly ConcurrentDictionary<(MethodInfo Method, string Shape), NativeBuiltinThunk?> s_thunks = new();

    /// <summary>
    /// The thunk of <paramref name="method"/> whose parameter k comes from the
    /// isolate ('I'), values[k] ('V'), objects[k] ('O') or a byte of the
    /// array objects[k] at index values[k] by reference ('B'); null when a
    /// parameter or the result has a type the thunk does not convert.
    /// </summary>
    public static NativeBuiltinThunk? For(MethodInfo method, string shape) => s_thunks.GetOrAdd((method, shape), static key => Create(key.Method, key.Shape));

    static NativeBuiltinThunk? Create(MethodInfo method, string shape)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (parameters.Length != shape.Length || !method.IsStatic) return null;
        var dm = new DynamicMethod("NativeBuiltinThunk_" + method.Name, typeof(JSValue), [typeof(Isolate), typeof(JSValue[]), typeof(object?[])],
            typeof(NativeBuiltinThunks).Module, skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        for (int k = 0; k < parameters.Length; k++)
        {
            Type t = parameters[k].ParameterType;
            if (shape[k] == 'B')
            {
                // ref bytes[index]: the array in objects[k], the index in values[k].
                if (t != typeof(byte).MakeByRefType()) return null;
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldc_I4, k);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(OpCodes.Castclass, typeof(byte[]));
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldc_I4, k);
                il.Emit(OpCodes.Ldelem, typeof(JSValue));
                il.Emit(OpCodes.Call, Helper(nameof(ToInt32)));
                il.Emit(OpCodes.Ldelema, typeof(byte));
                continue;
            }
            if (t.IsByRef) return null;
            switch (shape[k])
            {
                case 'I':
                    if (t != typeof(Isolate)) return null;
                    il.Emit(OpCodes.Ldarg_0);
                    break;
                case 'V':
                    il.Emit(OpCodes.Ldarg_1);
                    il.Emit(OpCodes.Ldc_I4, k);
                    il.Emit(OpCodes.Ldelem, typeof(JSValue));
                    if (t == typeof(JSValue)) break;
                    if (t == typeof(int)) il.Emit(OpCodes.Call, Helper(nameof(ToInt32)));
                    else if (t == typeof(uint)) il.Emit(OpCodes.Call, Helper(nameof(ToUInt32)));
                    else if (t == typeof(double)) il.Emit(OpCodes.Call, Helper(nameof(ToDouble)));
                    else if (!t.IsValueType)
                    {
                        il.Emit(OpCodes.Call, Helper(nameof(ToObject)));
                        il.Emit(OpCodes.Castclass, t);
                    }
                    else return null;
                    break;
                case 'O':
                    il.Emit(OpCodes.Ldarg_2);
                    il.Emit(OpCodes.Ldc_I4, k);
                    il.Emit(OpCodes.Ldelem_Ref);
                    il.Emit(t.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, t);
                    break;
                default:
                    return null;
            }
        }
        il.Emit(OpCodes.Call, method);
        Type r = method.ReturnType;
        if (r == typeof(void))
        {
            LocalBuilder none = il.DeclareLocal(typeof(JSValue));
            il.Emit(OpCodes.Ldloca, none);
            il.Emit(OpCodes.Initobj, typeof(JSValue));
            il.Emit(OpCodes.Ldloc, none);
        }
        else if (r == typeof(int)) il.Emit(OpCodes.Call, Helper(nameof(FromInt32)));
        else if (r == typeof(uint)) il.Emit(OpCodes.Call, Helper(nameof(FromUInt32)));
        else if (r == typeof(double)) il.Emit(OpCodes.Call, Helper(nameof(FromDouble)));
        else if (r == typeof(bool)) il.Emit(OpCodes.Call, Helper(nameof(FromBool)));
        else if (typeof(HeapObject).IsAssignableFrom(r)) il.Emit(OpCodes.Call, Helper(nameof(FromObject)));
        else if (r != typeof(JSValue)) return null;
        il.Emit(OpCodes.Ret);
        return dm.CreateDelegate<NativeBuiltinThunk>();
    }

    static MethodInfo Helper(string name) => typeof(NativeBuiltinThunks).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;

    // MaglevNativeCode.Convert's conversions, and the results' (Execute's).
    public static int ToInt32(JSValue v) => (int)v.Number;
    public static uint ToUInt32(JSValue v) => (uint)(int)v.Number;
    public static double ToDouble(JSValue v) => v.Number;
    public static object? ToObject(JSValue v) => v.HeapObjectOrNull;
    public static JSValue FromInt32(int i) => JSValue.FromInt(i);
    public static JSValue FromUInt32(uint u) => JSValue.FromNumber(u);
    public static JSValue FromDouble(double d) => JSValue.FromNumber(d);
    public static JSValue FromBool(bool b) => JSValue.FromBoolean(b);
    public static JSValue FromObject(HeapObject? o) => JSValue.FromObject(o);
}
