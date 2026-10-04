// A cache of compiled baseline methods by bytecode (V8Sharp addition).
//
// Deviation: V8 compiles each SharedFunctionInfo's baseline code itself
// (Sparkplug compiles about as fast as it copies bytecode, so there is
// nothing to share). V8Sharp's code costs RyuJIT milliseconds per function,
// and programs that evaluate the same source again (Octane CodeLoad: each
// iteration evaluates fresh copies of the same libraries) would compile the
// same IL over and over. Baseline code reads everything that is not in the
// bytecode itself at run time (the constant pool and bytecodes from its code
// object, the feedback vector and context from the frame), so a method
// compiled for one function runs any function with the same bytecode: the
// cache key is the bytecode with its embedded feedback masked out, the
// handler table, the register file shape, the resumable kind and the number
// constants (jump tables). What the compile-time feedback chose (which fast
// paths, which field offsets) only decides the speed of the reused code:
// every path checks its assumption at run time.
using System.Reflection;
using V8Sharp.Interpreter;

namespace V8Sharp.Baseline;

internal static class BaselineCodeCache
{
    /// <summary>The compiled methods of one function: one, or one per chunk (with the chunks' first offsets).</summary>
    internal sealed record Entry(MethodInfo[] Methods, int[]? ChunkStarts, int ILSize);

    sealed class Key : IEquatable<Key>
    {
        readonly byte[] _bytes;
        readonly int _hash;

        public Key(byte[] bytes)
        {
            _bytes = bytes;
            var hash = new HashCode();
            hash.AddBytes(bytes);
            _hash = hash.ToHashCode();
        }

        public bool Equals(Key? other) => other is not null && _hash == other._hash && _bytes.AsSpan().SequenceEqual(other._bytes);
        public override bool Equals(object? obj) => obj is Key other && Equals(other);
        public override int GetHashCode() => _hash;
    }

    static readonly Dictionary<Key, Entry> s_entries = [];
    static readonly Lock s_lock = new();

    /// <summary>V8SHARP_BASELINE_NO_CODE_CACHE=1: every function compiles its own code.</summary>
    static readonly bool s_disabled = Environment.GetEnvironmentVariable("V8SHARP_BASELINE_NO_CODE_CACHE") == "1";

    /// <summary>The key of <paramref name="code"/>'s bytecode, or null when the cache is not used.</summary>
    internal static object? KeyFor(BaselineCode code)
    {
        if (s_disabled) return null;
        BytecodeArray bytecode = code.Bytecode;
        var bytes = new List<byte>(bytecode.Length + bytecode.HandlerTable.Length * 4 + 64);
        bytes.AddRange(bytecode.Bytecodes);
        // The embedded feedback changes as the function runs: masked out.
        for (var it = new BytecodeArrayIterator(bytecode); !it.Done(); it.Advance())
        {
            Bytecode b = it.CurrentBytecode();
            ReadOnlySpan<OperandType> types = Bytecodes.GetOperandTypes(b);
            for (int i = 0; i < types.Length; i++)
            {
                if (types[i] != OperandType.EmbeddedFeedback) continue;
                int cursor = it.CurrentOffset() + it.CurrentBytecodeSize() - it.CurrentBytecodeSizeWithoutPrefix();
                bytes[cursor + it.CurrentOperandOffset(i)] = 0;
            }
        }
        AddInt(bytes, bytecode.Length);
        AddInt(bytes, bytecode.HandlerTable.Length);
        bytes.AddRange(bytecode.HandlerTable);
        AddInt(bytes, bytecode.RegisterCount);
        AddInt(bytes, bytecode.ParameterCount);
        AddInt(bytes, bytecode.IncomingNewTargetOrGeneratorRegister.Index);
        AddInt(bytes, Globals.IsResumableFunction(code.SharedFunctionInfo.Kind) ? 1 : 0);
        JSValue[] constants = code.Constants!;
        for (int i = 0; i < constants.Length; i++)
        {
            if (!constants[i].IsNumber) continue;
            AddInt(bytes, i);
            long bits = BitConverter.DoubleToInt64Bits(constants[i]._num);
            AddInt(bytes, (int)bits);
            AddInt(bytes, (int)(bits >> 32));
        }
        return new Key(bytes.ToArray());
    }

    static void AddInt(List<byte> bytes, int value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)(value >> 16));
        bytes.Add((byte)(value >> 24));
    }

    internal static Entry? Get(object? key)
    {
        if (key is not Key k) return null;
        lock (s_lock) return s_entries.GetValueOrDefault(k);
    }

    internal static void Add(object? key, Entry entry)
    {
        if (key is not Key k) return;
        lock (s_lock) s_entries.TryAdd(k, entry);
    }
}
