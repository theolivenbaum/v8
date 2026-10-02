// Port of src/maglev/maglev-graph-printer.{h,cc} (--print-maglev-graph), in
// a simpler text form: blocks with predecessors, phis, nodes with inputs,
// representations and deopt info, and the control node.
using System.Text;

namespace V8Sharp.Maglev;

internal static class MaglevGraphPrinter
{
    public static void Print(MaglevCompilationInfo info, TextWriter output)
    {
        var sb = new StringBuilder();
        sb.Append("Graph of ").Append(MaglevCompiler.DebugName(info.Function.Shared));
        if (info.IsOsr) sb.Append(" (OSR at ").Append(info.OsrOffset).Append(')');
        sb.AppendLine();
        sb.AppendLine("  constants:");
        foreach (ValueNode c in info.Graph.Constants)
        {
            sb.Append("    ").Append(Describe(c)).AppendLine();
        }
        foreach (BasicBlock block in info.Graph.Blocks)
        {
            if (block.IsDead) continue;
            sb.Append("  b").Append(block.Id);
            if (block.IsLoopHeader) sb.Append(" (loop)");
            if (block.Offset >= 0) sb.Append(" @").Append(block.Offset);
            sb.Append(" preds:");
            foreach (BasicBlock p in block.Predecessors) sb.Append(" b").Append(p.Id);
            sb.AppendLine();
            foreach (Phi phi in block.Phis)
            {
                sb.Append("    ").Append(Describe(phi)).Append(" [").Append(phi.Owner).Append("] uses=").Append(phi.UseCount).AppendLine();
            }
            foreach (Node node in block.Nodes)
            {
                sb.Append("    ").Append(Describe(node));
                if (node is ValueNode v) sb.Append(" uses=").Append(v.UseCount);
                if (node.EagerDeoptInfo is { } e) sb.Append(" eager-deopt(").Append(e.Reason).Append(" @").Append(((InterpretedDeoptFrame)e.TopFrame).BytecodeOffset).Append(')');
                if (node.LazyDeoptInfo is not null) sb.Append(" lazy-deopt");
                sb.AppendLine();
            }
            if (block.Control is { } c)
            {
                sb.Append("    ").Append(Describe(c));
                if (c.Target is not null) sb.Append(" -> b").Append(c.Target.Id);
                if (c.FalseTarget is not null) sb.Append(" / b").Append(c.FalseTarget.Id);
                if (c.Targets is not null)
                {
                    sb.Append(" ->");
                    foreach (BasicBlock? t in c.Targets) sb.Append(" b").Append(t?.Id);
                }
                sb.AppendLine();
            }
        }
        output.Write(sb.ToString());
    }

    static string Describe(NodeBase node)
    {
        var sb = new StringBuilder();
        sb.Append('n').Append(node.Id).Append(": ").Append(node.Opcode);
        if (node is ValueNode v)
        {
            sb.Append('<').Append(v.Representation.ToString()[1..]).Append('>');
            switch (v.Opcode)
            {
                case Opcode.SmiConstant:
                case Opcode.Int32Constant:
                    sb.Append('(').Append(v.Int0).Append(')');
                    break;
                case Opcode.Float64Constant:
                    sb.Append('(').Append(v.Double0.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(')');
                    break;
                case Opcode.RootConstant:
                    sb.Append('(').Append((RootIndex)v.Int0).Append(')');
                    break;
                case Opcode.Constant:
                    sb.Append('(').Append(v.Value0).Append(')');
                    break;
            }
        }
        if (node.Obj0 is CallBuiltinInfo info) sb.Append(' ').Append(info.Name);
        if (node.Inputs.Length > 0)
        {
            sb.Append(" [");
            for (int i = 0; i < node.Inputs.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('n').Append(node.Inputs[i].Id);
            }
            sb.Append(']');
        }
        return sb.ToString();
    }
}
