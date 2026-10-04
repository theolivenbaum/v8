// The stack slot limit of src/maglev/maglev-compiler.cc (kMaxStackSlots: a
// Maglev frame is at most 4 KB of stack slots, so that its prologue cannot
// step over a guard page; a graph that needs more bails out).
//
// Deviation (structural): V8 counts the stack slots the register allocator
// assigned. V8Sharp has no register allocator (values live in IL locals and
// RyuJIT allocates them), so the count is the most values live at once in
// the graph: what V8's allocator would have to keep, less the few that fit
// in registers. Parameters and the frame's fixed slots (InitialValue) are
// not counted: they are in the caller's part of the frame, as in V8.
using System.Numerics;

namespace V8Sharp.Maglev;

internal static class MaglevStackSlots
{
    /// <summary>kMaxStackSlots = 4 KB / kSystemPointerSize.</summary>
    public const int kMaxStackSlots = 4 * 1024 / 8;

    /// <summary>The most values live at once (a liveness analysis over the graph).</summary>
    public static int MaxLiveValues(Graph graph)
    {
        var index = new Dictionary<ValueNode, int>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis) if (phi.UseCount > 0) index[phi] = index.Count;
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode { IsConstant: false, UseCount: > 0 } v && v.Opcode != Opcode.InitialValue) index[v] = index.Count;
            }
        }
        if (index.Count <= kMaxStackSlots) return index.Count;
        int words = (index.Count + 63) / 64;
        var blockIndex = new Dictionary<BasicBlock, int>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in graph.Blocks) blockIndex[block] = blockIndex.Count;
        var liveIn = new ulong[graph.Blocks.Count][];
        for (int i = 0; i < liveIn.Length; i++) liveIn[i] = new ulong[words];
        var live = new ulong[words];
        int max = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int b = graph.Blocks.Count - 1; b >= 0; b--)
            {
                BasicBlock block = graph.Blocks[b];
                if (block.IsDead) continue;
                Array.Clear(live);
                int count = 0;
                void Use(ValueNode v)
                {
                    if (!index.TryGetValue(v, out int i)) return;
                    ulong bit = 1UL << (i & 63);
                    if ((live[i >> 6] & bit) != 0) return;
                    live[i >> 6] |= bit;
                    count++;
                }
                void Def(ValueNode v)
                {
                    if (!index.TryGetValue(v, out int i)) return;
                    ulong bit = 1UL << (i & 63);
                    if ((live[i >> 6] & bit) == 0) return;
                    live[i >> 6] &= ~bit;
                    count--;
                }
                void UseFrame(DeoptFrame? frame) => MaglevEscapeAnalysis.ForEachDeoptValue(frame, Use);
                void UseSuccessor(BasicBlock successor, BasicBlock from, int throwIndex)
                {
                    ulong[] successorLive = liveIn[blockIndex[successor]];
                    for (int w = 0; w < words; w++)
                    {
                        ulong added = successorLive[w] & ~live[w];
                        if (added == 0) continue;
                        live[w] |= added;
                        count += BitOperations.PopCount(added);
                    }
                    int predecessor = throwIndex >= 0 ? throwIndex : successor.Predecessors.IndexOf(from);
                    foreach (Phi phi in successor.Phis)
                    {
                        if (phi.Inputs.Length == 0 || predecessor < 0 || predecessor >= phi.Inputs.Length) continue;
                        Use(phi.Inputs[predecessor]);
                    }
                }
                foreach (BasicBlock successor in block.Successors()) UseSuccessor(successor, block, -1);
                ControlNode control = block.Control!;
                foreach (ValueNode input in control.Inputs) Use(input);
                UseFrame(control.EagerDeoptInfo?.TopFrame);
                if (count > max) max = count;
                for (int n = block.Nodes.Count - 1; n >= 0; n--)
                {
                    Node node = block.Nodes[n];
                    if (node is ValueNode v) Def(v);
                    foreach (ValueNode input in node.Inputs) Use(input);
                    UseFrame(node.EagerDeoptInfo?.TopFrame);
                    UseFrame(node.LazyDeoptInfo?.TopFrame);
                    if (node.ExceptionHandler is { CatchState.Block: { IsDead: false } catchBlock } handler)
                    {
                        UseSuccessor(catchBlock, block, handler.ThrowIndex);
                    }
                    if (count > max) max = count;
                }
                foreach (Phi phi in block.Phis) Def(phi);
                ulong[] blockLive = liveIn[b];
                for (int w = 0; w < words; w++)
                {
                    if (blockLive[w] == live[w]) continue;
                    blockLive[w] = live[w];
                    changed = true;
                }
            }
        }
        return max;
    }
}
