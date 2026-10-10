// Port of BuiltinArguments (src/builtins/builtins-utils.h) and the builtin
// table (src/builtins/builtins.{h,cc}) for the C# builtins.
//
// A builtin is a static C# method `static JSValue Name(Isolate isolate, in
// BuiltinArguments args)` (architecture.md section 7). BuiltinRegistry maps
// V8's Builtin ids to those methods; the SharedFunctionInfo of a builtin
// function holds its id, and Execution.Call dispatches through the table.
using System.Runtime.CompilerServices;
using V8Sharp.Objects;

namespace V8Sharp.Builtins;

/// <summary>
/// V8's BuiltinArguments: the target, new.target, receiver and arguments of a
/// builtin call. As in V8, index 0 is the receiver and <see cref="Length"/>
/// counts it.
/// </summary>
public readonly ref struct BuiltinArguments
{
    readonly ReadOnlySpan<JSValue> _arguments;

    public BuiltinArguments(JSFunction target, JSValue newTarget, JSValue receiver, ReadOnlySpan<JSValue> arguments)
    {
        Target = target;
        NewTarget = newTarget;
        Receiver = receiver;
        _arguments = arguments;
    }

    /// <summary>The called function (V8's target()).</summary>
    public JSFunction Target { get; }

    /// <summary>new.target: undefined for [[Call]], the constructor for [[Construct]].</summary>
    public JSValue NewTarget { get; }

    /// <summary>The receiver (V8's receiver(), index 0).</summary>
    public JSValue Receiver { get; }

    /// <summary>The arguments without the receiver.</summary>
    public ReadOnlySpan<JSValue> Arguments => _arguments;

    /// <summary>The number of arguments including the receiver (V8's length()).</summary>
    public int Length => _arguments.Length + 1;

    /// <summary>The number of arguments without the receiver (V8's argc_without_receiver()).</summary>
    public int ArgcWithoutReceiver => _arguments.Length;

    /// <summary>V8's args[index] / args.at(index): index 0 is the receiver.</summary>
    public JSValue this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => index == 0 ? Receiver : _arguments[index - 1];
    }

    /// <summary>V8's atOrUndefined: index 0 is the receiver; missing arguments are undefined.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public JSValue AtOrUndefined(int index)
    {
        if (index == 0) return Receiver;
        int i = index - 1;
        return (uint)i < (uint)_arguments.Length ? _arguments[i] : JSValue.Undefined;
    }

    /// <summary>True when the builtin was invoked with [[Construct]].</summary>
    public bool IsConstructCall => !NewTarget.IsUndefined;
}

/// <summary>The signature of a C# builtin.</summary>
public delegate JSValue BuiltinFunction(Isolate isolate, in BuiltinArguments args);

/// <summary>
/// The builtins table: Builtin id to C# implementation. Builtins without an
/// implementation throw a clear internal error when called.
/// </summary>
public static partial class BuiltinRegistry
{
    static readonly BuiltinFunction?[] s_table = new BuiltinFunction?[kBuiltinCount];

    /// <summary>Registers the implementation of a builtin.</summary>
    public static void Register(Builtin builtin, BuiltinFunction function) => s_table[(int)builtin] = function;

    /// <summary>True if the builtin has a C# implementation.</summary>
    public static bool IsImplemented(Builtin builtin) =>
        builtin != Builtin.NoBuiltinId && s_table[(int)builtin] is not null;

    /// <summary>The kind of the builtin (CPP, TFJ, ...) as declared in V8.</summary>
    public static BuiltinKind KindOf(Builtin builtin) => s_kinds[(int)builtin];

    /// <summary>Builtins::name.</summary>
    public static string Name(Builtin builtin) => builtin.ToString();

    /// <summary>Invokes a builtin (V8: the builtin's code object via the CEntry/adaptor).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static JSValue Invoke(Isolate isolate, Builtin builtin, JSFunction target, JSValue newTarget, JSValue receiver,
        ReadOnlySpan<JSValue> arguments)
    {
        BuiltinFunction? function = (uint)builtin < (uint)s_table.Length ? s_table[(int)builtin] : null;
        if (function is null) return ThrowUnimplemented(builtin);
        var args = new BuiltinArguments(target, newTarget, receiver, arguments);
        return function(isolate, in args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static JSValue ThrowUnimplemented(Builtin builtin) =>
        throw new NotImplementedException($"V8Sharp: builtin {builtin} is not implemented");
}
