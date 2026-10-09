// The IL emitter of the Maglev code generator (the role of maglev-assembler.h
// over an ILGenerator): it counts what RyuJIT's optimization limits count
// (compSetOptimizationLevel in RyuJIT's compiler.cpp: IL bytes, IL
// instructions, basic blocks, local variable references; beyond any of them
// a method is compiled with MinOpts) and picks the short encodings of
// integer constants, which keeps big methods under the IL byte limit.
using System.Reflection;
using System.Reflection.Emit;

namespace V8Sharp.Maglev;

internal sealed class MaglevILEmitter(ILGenerator il)
{
    /// <summary>IL instructions emitted (RyuJIT's opts.instrCount).</summary>
    public int Instructions;

    /// <summary>Local variable references (opts.lvRefCount).</summary>
    public int LocalReferences;

    /// <summary>Branches, branch targets, returns and throws: an upper bound of the basic block count (fgBBcount).</summary>
    public int BlockBoundaries;

    /// <summary>Locals declared (lvaCount, without the arguments and RyuJIT's temporaries).</summary>
    public int Locals;

    public int ILOffset => il.ILOffset;

    public LocalBuilder DeclareLocal(Type type)
    {
        Locals++;
        return il.DeclareLocal(type);
    }

    public Label DefineLabel() => il.DefineLabel();

    public void MarkLabel(Label label)
    {
        BlockBoundaries++;
        il.MarkLabel(label);
    }

    /// <summary>Arguments are locals to RyuJIT: their loads count as local references.</summary>
    static bool IsArgumentAccess(OpCode op) =>
        op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3 ||
        op == OpCodes.Ldarg_S || op == OpCodes.Ldarg || op == OpCodes.Ldarga_S || op == OpCodes.Ldarga ||
        op == OpCodes.Starg_S || op == OpCodes.Starg;

    public void Emit(OpCode op)
    {
        Instructions++;
        if (IsArgumentAccess(op)) LocalReferences++;
        if (op.FlowControl is FlowControl.Return or FlowControl.Throw) BlockBoundaries++;
        il.Emit(op);
    }

    /// <summary>A one-byte operand (ldarg.s, ldloc.s ...).</summary>
    public void Emit(OpCode op, byte value)
    {
        Instructions++;
        if (IsArgumentAccess(op)) LocalReferences++;
        il.Emit(op, value);
    }

    /// <summary>A two-byte operand (ldarg, ldloc ...).</summary>
    public void Emit(OpCode op, short value)
    {
        Instructions++;
        if (IsArgumentAccess(op)) LocalReferences++;
        il.Emit(op, value);
    }

    public void Emit(OpCode op, int value)
    {
        Instructions++;
        if (op == OpCodes.Ldc_I4)
        {
            EmitLdcI4(value);
            return;
        }
        il.Emit(op, value);
    }

    /// <summary>ldc.i4 in its shortest form (ldc.i4.m1 .. ldc.i4.8, ldc.i4.s).</summary>
    void EmitLdcI4(int value)
    {
        switch (value)
        {
            case -1: il.Emit(OpCodes.Ldc_I4_M1); return;
            case 0: il.Emit(OpCodes.Ldc_I4_0); return;
            case 1: il.Emit(OpCodes.Ldc_I4_1); return;
            case 2: il.Emit(OpCodes.Ldc_I4_2); return;
            case 3: il.Emit(OpCodes.Ldc_I4_3); return;
            case 4: il.Emit(OpCodes.Ldc_I4_4); return;
            case 5: il.Emit(OpCodes.Ldc_I4_5); return;
            case 6: il.Emit(OpCodes.Ldc_I4_6); return;
            case 7: il.Emit(OpCodes.Ldc_I4_7); return;
            case 8: il.Emit(OpCodes.Ldc_I4_8); return;
        }
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue) il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
        else il.Emit(OpCodes.Ldc_I4, value);
    }

    public void Emit(OpCode op, long value)
    {
        Instructions++;
        il.Emit(op, value);
    }

    public void Emit(OpCode op, double value)
    {
        Instructions++;
        il.Emit(op, value);
    }

    public void Emit(OpCode op, Label label)
    {
        Instructions++;
        BlockBoundaries++;
        il.Emit(op, label);
    }

    public void Emit(OpCode op, Label[] labels)
    {
        Instructions++;
        BlockBoundaries += labels.Length + 1;
        il.Emit(op, labels);
    }

    public void Emit(OpCode op, FieldInfo field)
    {
        Instructions++;
        il.Emit(op, field);
    }

    public void Emit(OpCode op, MethodInfo method)
    {
        Instructions++;
        il.Emit(op, method);
    }

    public void Emit(OpCode op, ConstructorInfo constructor)
    {
        Instructions++;
        il.Emit(op, constructor);
    }

    public void Emit(OpCode op, Type type)
    {
        Instructions++;
        il.Emit(op, type);
    }

    public void Emit(OpCode op, LocalBuilder local)
    {
        Instructions++;
        LocalReferences++;
        il.Emit(op, local);
    }

    public Label BeginExceptionBlock()
    {
        BlockBoundaries++;
        return il.BeginExceptionBlock();
    }

    public void BeginExceptFilterBlock()
    {
        BlockBoundaries++;
        il.BeginExceptFilterBlock();
    }

    public void BeginCatchBlock(Type? exceptionType)
    {
        BlockBoundaries++;
        il.BeginCatchBlock(exceptionType);
    }

    public void EndExceptionBlock()
    {
        BlockBoundaries++;
        il.EndExceptionBlock();
    }
}
