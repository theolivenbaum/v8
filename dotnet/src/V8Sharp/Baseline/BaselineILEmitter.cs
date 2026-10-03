// The ILGenerator the baseline compiler writes through, with the counts that
// decide how RyuJIT will treat the method.
//
// RyuJIT compiles a method with minimal optimization (MinOpts: no register
// allocation of locals, no inlining) when its IL is too large or too complex
// (compSetOptimizationLevel's DEFAULT_MIN_OPTS_* limits: 60000 IL bytes, 20000
// instructions, 2000 basic blocks, 8000 local references). Baseline code
// compiled like that is slower than the interpreter, whose dispatch loop is
// fully optimized; the compiler watches these counts and emits a more compact
// form of a function that would exceed them (BaselineCompiler.Build).
using System.Reflection;
using System.Reflection.Emit;

namespace V8Sharp.Baseline;

internal sealed class BaselineILEmitter(ILGenerator il)
{
    /// <summary>IL instructions emitted (RyuJIT's opts.instrCount).</summary>
    public int Instructions;

    /// <summary>Local variable references (opts.lvRefCount).</summary>
    public int LocalReferences;

    /// <summary>Branches and branch targets: an upper bound of the basic block count (fgBBcount).</summary>
    public int BlockBoundaries;

    public int ILOffset => il.ILOffset;

    public LocalBuilder DeclareLocal(Type type) => il.DeclareLocal(type);

    public Label DefineLabel() => il.DefineLabel();

    public void MarkLabel(Label label)
    {
        BlockBoundaries++;
        il.MarkLabel(label);
    }

    public void Emit(OpCode op)
    {
        Instructions++;
        if (op.FlowControl is FlowControl.Return or FlowControl.Throw) BlockBoundaries++;
        il.Emit(op);
    }

    public void Emit(OpCode op, int value)
    {
        Instructions++;
        il.Emit(op, value);
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

    public void Emit(OpCode op, LocalBuilder local)
    {
        Instructions++;
        LocalReferences++;
        il.Emit(op, local);
    }

    public void Emit(OpCode op, Type type)
    {
        Instructions++;
        il.Emit(op, type);
    }
}
