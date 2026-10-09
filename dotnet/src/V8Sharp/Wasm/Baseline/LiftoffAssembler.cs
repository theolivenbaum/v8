// Port of src/wasm/baseline/liftoff-assembler.{h,cc}: the compile-time value
// stack (LiftoffAssembler::CacheState and VarState) over an IL generator.
//
// Liftoff keeps each wasm stack value as a constant, in a register, or in a
// stack slot, and merges states at control-flow joins by moving values into
// the target's canonical locations. V8Sharp keeps each value as
//   - a constant (Const), materialized when used;
//   - a reference to a wasm local (Local), loaded when used (Liftoff has no
//     such state; it is what Liftoff's register cache achieves for locals);
//   - on the IL evaluation stack (Stack), the register analogue: values
//     computed in straight-line code stay there, so RyuJIT sees expression
//     trees;
//   - in a slot (Slot), an IL local per stack depth and kind: the stack slot,
//     and the canonical location at control-flow joins.
// At every branch, label and exception region the IL evaluation stack is
// empty: values live in slots (or are constants). RyuJIT allocates registers
// for the slots, as Liftoff's register allocator would.
using System.Reflection;
using System.Reflection.Emit;

namespace V8Sharp.Wasm.Baseline;

/// <summary>LiftoffAssembler::VarState.</summary>
internal struct VarState
{
    public enum Loc : byte { Stack, Slot, Const, Local }

    public Loc Location;
    public WasmKind Kind;
    /// <summary>The constant's bits (Const) or the wasm local's index (Local).</summary>
    public long Value;

    public static VarState OnStack(WasmKind kind) => new() { Location = Loc.Stack, Kind = kind };
    public static VarState InSlot(WasmKind kind) => new() { Location = Loc.Slot, Kind = kind };
    public static VarState Constant(WasmKind kind, long bits) => new() { Location = Loc.Const, Kind = kind, Value = bits };
    public static VarState OfLocal(WasmKind kind, int index) => new() { Location = Loc.Local, Kind = kind, Value = index };

    public override readonly string ToString() => $"{Kind}:{Location}{(Location is Loc.Const or Loc.Local ? "(" + Value + ")" : "")}";
}

internal sealed class LiftoffAssembler(ILGenerator il)
{
    public readonly ILGenerator IL = il;
    public readonly List<VarState> Stack = new(64);

    readonly Dictionary<int, LocalBuilder> _slots = [];
    readonly List<LocalBuilder>[] _temps = [[], [], [], [], [], []];
    readonly int[] _tempsInUse = new int[6];

    /// <summary>Wasm locals: IL argument indices (parameters) or IL locals.</summary>
    public int ParamCount;
    public LocalBuilder?[] Locals = [];

    public int Height => Stack.Count;

    public LocalBuilder Slot(int depth, WasmKind kind)
    {
        int key = depth * 8 + (int)kind;
        if (!_slots.TryGetValue(key, out LocalBuilder? local))
        {
            local = IL.DeclareLocal(WasmKinds.ClrType(kind));
            _slots[key] = local;
        }
        return local;
    }

    /// <summary>A scratch local of <paramref name="kind"/>, until <see cref="ReleaseTemps"/>.</summary>
    public LocalBuilder Temp(WasmKind kind)
    {
        List<LocalBuilder> pool = _temps[(int)kind];
        int n = _tempsInUse[(int)kind]++;
        if (n == pool.Count) pool.Add(IL.DeclareLocal(WasmKinds.ClrType(kind)));
        return pool[n];
    }

    public void ReleaseTemps() => Array.Clear(_tempsInUse);

    // ---- Emitting loads ----------------------------------------------------

    public void LoadLocal(int index)
    {
        if (index < ParamCount)
        {
            IL.Emit(OpCodes.Ldarg, (short)(index + 1));
        }
        else
        {
            IL.Emit(OpCodes.Ldloc, Locals[index]!);
        }
    }

    public void StoreLocal(int index)
    {
        if (index < ParamCount)
        {
            IL.Emit(OpCodes.Starg, (short)(index + 1));
        }
        else
        {
            IL.Emit(OpCodes.Stloc, Locals[index]!);
        }
    }

    /// <summary>
    /// Loads a float constant without letting RyuJIT see it (see
    /// <see cref="IsOpaqueConstant"/>).
    /// </summary>
    public Action<WasmKind, long>? LoadNaN;

    /// <summary>
    /// Float constants RyuJIT must not see: NaNs (RyuJIT keeps float constants
    /// as doubles, which quiets a signaling NaN, and folds NaN arithmetic to
    /// the default NaN, where wasm and V8 keep the payload) and the identity
    /// elements +-0 and +-1 (RyuJIT folds x - 0, x + -0, x * 1 and x / 1 to x,
    /// which leaves a signaling NaN signaling where wasm quiets it).
    /// </summary>
    public static bool IsOpaqueConstant(WasmKind kind, long bits)
    {
        if (kind == WasmKind.F32)
        {
            float f = BitConverter.Int32BitsToSingle((int)bits);
            return float.IsNaN(f) || f == 0 || f == 1 || f == -1;
        }
        if (kind == WasmKind.F64)
        {
            double d = BitConverter.Int64BitsToDouble(bits);
            return double.IsNaN(d) || d == 0 || d == 1 || d == -1;
        }
        return false;
    }

    public void LoadConst(WasmKind kind, long bits)
    {
        if (LoadNaN != null && IsOpaqueConstant(kind, bits))
        {
            LoadNaN(kind, bits);
            return;
        }
        switch (kind)
        {
            case WasmKind.I32:
                IL.Emit(OpCodes.Ldc_I4, (int)bits);
                break;
            case WasmKind.I64:
                IL.Emit(OpCodes.Ldc_I8, bits);
                break;
            case WasmKind.F32:
                // ldc.r4 keeps a NaN's payload (the bits are the immediate).
                IL.Emit(OpCodes.Ldc_I4, (int)bits);
                IL.Emit(OpCodes.Call, s_int32BitsToSingle);
                break;
            case WasmKind.F64:
                IL.Emit(OpCodes.Ldc_I8, bits);
                IL.Emit(OpCodes.Call, s_int64BitsToDouble);
                break;
            default:
                throw new InvalidOperationException("constant of kind " + kind);
        }
    }

    static readonly MethodInfo s_int32BitsToSingle = typeof(BitConverter).GetMethod(nameof(BitConverter.Int32BitsToSingle))!;
    static readonly MethodInfo s_int64BitsToDouble = typeof(BitConverter).GetMethod(nameof(BitConverter.Int64BitsToDouble))!;

    /// <summary>Loads the value of a stack entry that is not on the IL stack.</summary>
    void Load(int depth, VarState v)
    {
        switch (v.Location)
        {
            case VarState.Loc.Slot:
                IL.Emit(OpCodes.Ldloc, Slot(depth, v.Kind));
                break;
            case VarState.Loc.Const:
                LoadConst(v.Kind, v.Value);
                break;
            case VarState.Loc.Local:
                LoadLocal((int)v.Value);
                break;
            default:
                throw new InvalidOperationException("value is on the IL stack");
        }
    }

    // ---- Pushing -------------------------------------------------------------

    public void PushStack(WasmKind kind) => Stack.Add(VarState.OnStack(kind));
    public void PushSlot(WasmKind kind) => Stack.Add(VarState.InSlot(kind));
    public void PushConst(WasmKind kind, long bits) => Stack.Add(VarState.Constant(kind, bits));
    public void PushLocal(WasmKind kind, int index) => Stack.Add(VarState.OfLocal(kind, index));

    public WasmKind PeekKind(int fromTop = 0) => Stack[^(fromTop + 1)].Kind;

    // ---- Spilling --------------------------------------------------------------

    /// <summary>Moves the IL-stack entries at indices &gt;= <paramref name="from"/> to their slots.</summary>
    public void SpillStackEntries(int from)
    {
        for (int i = Stack.Count - 1; i >= from; i--)
        {
            VarState v = Stack[i];
            if (v.Location != VarState.Loc.Stack) continue;
            IL.Emit(OpCodes.Stloc, Slot(i, v.Kind));
            v.Location = VarState.Loc.Slot;
            Stack[i] = v;
        }
    }

    /// <summary>Moves an entry that is not on the IL stack into its slot.</summary>
    void Materialize(int i)
    {
        VarState v = Stack[i];
        if (v.Location is VarState.Loc.Slot or VarState.Loc.Stack) return;
        Load(i, v);
        IL.Emit(OpCodes.Stloc, Slot(i, v.Kind));
        v.Location = VarState.Loc.Slot;
        Stack[i] = v;
    }

    /// <summary>
    /// Empties the IL stack and puts every reference to a local in a slot
    /// (LiftoffAssembler::SpillAllRegisters, at control-flow boundaries), and
    /// the constants at or above <paramref name="constantsFrom"/> too.
    /// </summary>
    public void SpillAll(int constantsFrom = int.MaxValue)
    {
        SpillStackEntries(0);
        for (int i = 0; i < Stack.Count; i++)
        {
            VarState v = Stack[i];
            if (v.Location == VarState.Loc.Local || (v.Location == VarState.Loc.Const && i >= constantsFrom)) Materialize(i);
        }
    }

    /// <summary>Puts the references to local <paramref name="index"/> in slots (before the local changes).</summary>
    public void SpillLocal(int index)
    {
        for (int i = 0; i < Stack.Count; i++)
        {
            VarState v = Stack[i];
            if (v.Location == VarState.Loc.Local && v.Value == index) Materialize(i);
        }
    }

    // ---- Popping operands ---------------------------------------------------

    /// <summary>
    /// Puts the top <paramref name="count"/> entries on the IL stack, in order,
    /// and pops them.
    /// </summary>
    public void PopToStack(int count)
    {
        int first = Stack.Count - count;
        // Entries already on the IL stack must form a prefix of the operands:
        // the IL stack can only be pushed in order.
        int k = first;
        while (k < Stack.Count && Stack[k].Location == VarState.Loc.Stack) k++;
        bool ordered = true;
        for (int i = k; i < Stack.Count; i++)
        {
            if (Stack[i].Location == VarState.Loc.Stack)
            {
                ordered = false;
                break;
            }
        }
        if (!ordered)
        {
            SpillStackEntries(k);
        }
        for (int i = k; i < Stack.Count; i++) Load(i, Stack[i]);
        Stack.RemoveRange(first, count);
    }

    /// <summary>
    /// As <see cref="PopToStack"/>, with <paramref name="prefix"/> emitted
    /// below the operands (a call's target).
    /// </summary>
    public void PopToStackWithPrefix(int count, Action prefix)
    {
        int first = Stack.Count - count;
        SpillStackEntries(first);
        prefix();
        for (int i = first; i < Stack.Count; i++) Load(i, Stack[i]);
        Stack.RemoveRange(first, count);
    }

    /// <summary>Puts the top <paramref name="count"/> entries in slots or constants (not the IL stack, not locals).</summary>
    public void Settle(int count)
    {
        int first = Stack.Count - count;
        SpillStackEntries(first);
        for (int i = first; i < Stack.Count; i++)
        {
            if (Stack[i].Location == VarState.Loc.Local) Materialize(i);
        }
    }

    /// <summary>Emits the load of a settled entry (<see cref="Settle"/>) at <paramref name="index"/>.</summary>
    public void LoadSettled(int index) => Load(index, Stack[index]);

    public void Drop(int count = 1)
    {
        for (int n = 0; n < count; n++)
        {
            int i = Stack.Count - 1;
            if (Stack[i].Location == VarState.Loc.Stack)
            {
                // Earlier IL-stack entries are below it: only the top one can be popped.
                SpillStackEntries(i + 1);
                IL.Emit(OpCodes.Pop);
            }
            Stack.RemoveAt(i);
        }
    }

    /// <summary>
    /// Moves the top <paramref name="count"/> values into the slots at
    /// <paramref name="targetBase"/> (a branch to a block with that many
    /// values): the merge of LiftoffAssembler::MergeStackWith. The IL stack
    /// must be empty. The entries stay as they are.
    /// </summary>
    public void Transfer(int count, int targetBase)
    {
        int first = Stack.Count - count;
        var pending = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            VarState v = Stack[first + i];
            if (v.Location == VarState.Loc.Slot && first + i == targetBase + i) continue;
            pending.Add(i);
        }
        foreach (int i in pending) Load(first + i, Stack[first + i]);
        for (int p = pending.Count - 1; p >= 0; p--)
        {
            int i = pending[p];
            IL.Emit(OpCodes.Stloc, Slot(targetBase + i, Stack[first + i].Kind));
        }
    }

    /// <summary>Whether a branch with <paramref name="count"/> values to <paramref name="targetBase"/> needs moves.</summary>
    public bool NeedsTransfer(int count, int targetBase)
    {
        int first = Stack.Count - count;
        for (int i = 0; i < count; i++)
        {
            VarState v = Stack[first + i];
            if (v.Location != VarState.Loc.Slot || first + i != targetBase + i) return true;
        }
        return false;
    }

    /// <summary>Resets the stack to <paramref name="height"/> entries followed by slots of <paramref name="kinds"/>.</summary>
    public void ResetTo(int height, WasmKind[] kinds)
    {
        Stack.RemoveRange(height, Stack.Count - height);
        foreach (WasmKind k in kinds) Stack.Add(VarState.InSlot(k));
    }

    public List<VarState> Snapshot() => [.. Stack];

    public void Restore(List<VarState> state)
    {
        Stack.Clear();
        Stack.AddRange(state);
    }
}
