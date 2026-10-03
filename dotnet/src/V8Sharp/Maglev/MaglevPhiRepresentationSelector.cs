// Port of src/maglev/maglev-phi-representation-selector.{h,cc}: the graph
// builder makes every Phi tagged (its untagged inputs are tagged in the
// predecessors); this pass retypes phis whose inputs are all untagged
// numbers to Int32 or Float64, bypasses the taggings of their inputs, and
// turns the untagging conversions of their uses into identities (or cheaper
// conversions).
//
// As in V8 the decision is optimistic over loop phis (a loop phi is assumed
// untaggable until an input says otherwise, iterated to a fixed point), and
// a phi without an untagging use stays tagged.
//
// Deviation (structural): V8 inserts the tagging conversions untagged phis
// need at their tagged uses; V8Sharp's code generator converts an untagged
// value where a use needs it tagged (an IL conversion at the use), so only
// the untagging uses are rewritten here.
namespace V8Sharp.Maglev;

internal static class MaglevPhiRepresentationSelector
{
    enum Hint : byte
    {
        Int32,
        Float64,
        Tagged,
    }

    public static void Run(Graph graph)
    {
        var phis = new List<Phi>();
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            // Exception phis stay tagged (their inputs are tagged by the exception trampolines).
            foreach (Phi phi in block.Phis) if (!phi.IsExceptionPhi) phis.Add(phi);
        }
        if (phis.Count == 0) return;

        // Optimistic: every phi starts as Int32 and is generalized by its inputs.
        var hints = new Dictionary<Phi, Hint>(ReferenceEqualityComparer.Instance);
        foreach (Phi phi in phis) hints[phi] = Hint.Int32;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Phi phi in phis)
            {
                Hint hint = Hint.Int32;
                foreach (ValueNode input in phi.Inputs)
                {
                    Hint inputHint = InputHint(input, hints);
                    if (inputHint > hint) hint = inputHint;
                    if (hint == Hint.Tagged) break;
                }
                if (hint != hints[phi])
                {
                    hints[phi] = hint;
                    changed = true;
                }
            }
        }

        // A phi without untagging uses stays tagged (its uses want it tagged).
        var uses = CollectUses(graph);
        foreach (Phi phi in phis)
        {
            if (hints[phi] == Hint.Tagged) continue;
            if (!HasUntaggingUse(phi, uses, hints, new HashSet<Phi>(ReferenceEqualityComparer.Instance)))
            {
                hints[phi] = Hint.Tagged;
            }
        }
        // Keeping a phi tagged can force a phi feeding it... no: a tagged phi accepts untagged inputs.
        // But an untagged phi with a phi input that became tagged must become tagged too.
        changed = true;
        while (changed)
        {
            changed = false;
            foreach (Phi phi in phis)
            {
                if (hints[phi] == Hint.Tagged) continue;
                foreach (ValueNode input in phi.Inputs)
                {
                    if (input is Phi p && hints[p] == Hint.Tagged || InputHint(input, hints) > hints[phi])
                    {
                        hints[phi] = Hint.Tagged;
                        changed = true;
                        break;
                    }
                }
            }
        }

        foreach (Phi phi in phis)
        {
            Hint hint = hints[phi];
            if (hint == Hint.Tagged) continue;
            phi.Representation = hint == Hint.Int32 ? ValueRepresentation.kInt32 : ValueRepresentation.kFloat64;
            phi.Type = NodeType.kNumber;
            for (int i = 0; i < phi.Inputs.Length; i++)
            {
                ValueNode input = phi.Inputs[i];
                if (input.Opcode is Opcode.Int32ToNumber or Opcode.Float64ToTagged or Opcode.Uint32ToNumber)
                {
                    phi.Inputs[i] = input.Inputs[0];
                }
                else if (input.IsConstant && input.Representation == ValueRepresentation.kTagged)
                {
                    phi.Inputs[i] = hint == Hint.Int32 && input.TryGetInt32Constant(out int c)
                        ? graph.GetInt32Constant(c)
                        : graph.GetFloat64Constant(input.ConstantValue().Number);
                }
            }
            phi.InputList.Clear();
            phi.InputList.AddRange(phi.Inputs);
        }

        // The untagging conversions of retyped phis.
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            for (int n = 0; n < block.Nodes.Count; n++)
            {
                Node node = block.Nodes[n];
                if (node.Inputs.Length == 0 || node.Inputs[0] is not Phi phi || phi.Representation == ValueRepresentation.kTagged) continue;
                bool isInt32 = phi.Representation == ValueRepresentation.kInt32;
                switch (node.Opcode)
                {
                    case Opcode.CheckedSmiUntag:
                    case Opcode.CheckedNumberToInt32:
                        if (isInt32) MakeIdentity(node);
                        else Retype(node, Opcode.CheckedFloat64ToInt32);
                        break;
                    case Opcode.UnsafeSmiUntag:
                    case Opcode.TruncateCheckedNumberOrOddballToInt32:
                        if (isInt32) MakeIdentity(node);
                        else Retype(node, Opcode.TruncateFloat64ToInt32, keepDeopt: false);
                        break;
                    case Opcode.CheckedNumberOrOddballToFloat64:
                    case Opcode.UnsafeNumberToFloat64:
                        if (!isInt32) MakeIdentity(node);
                        else Retype(node, Opcode.ChangeInt32ToFloat64, keepDeopt: false);
                        break;
                    case Opcode.CheckNumber:
                        // A number phi is a number.
                        node.Opcode = Opcode.Identity;
                        node.Properties = OpProperties.kNone;
                        node.EagerDeoptInfo = null;
                        block.Nodes.RemoveAt(n);
                        n--;
                        break;
                    case Opcode.CheckSmi when isInt32:
                        node.Opcode = Opcode.CheckInt32IsSmi;
                        break;
                }
            }
        }
    }

    static void MakeIdentity(Node node)
    {
        node.Opcode = Opcode.Identity;
        node.Properties = OpProperties.kNone;
        node.EagerDeoptInfo = null;
    }

    static void Retype(Node node, Opcode opcode, bool keepDeopt = true)
    {
        node.Opcode = opcode;
        if (!keepDeopt)
        {
            node.Properties = OpProperties.kNone;
            node.EagerDeoptInfo = null;
        }
    }

    static Hint InputHint(ValueNode input, Dictionary<Phi, Hint> hints)
    {
        if (input is Phi p) return hints.TryGetValue(p, out Hint h) ? h : Hint.Tagged;
        switch (input.Representation)
        {
            case ValueRepresentation.kInt32:
                return Hint.Int32;
            case ValueRepresentation.kUint32:
            case ValueRepresentation.kFloat64:
                return Hint.Float64;
            case ValueRepresentation.kHoleyFloat64:
                return Hint.Tagged;
        }
        switch (input.Opcode)
        {
            case Opcode.Int32ToNumber:
                return Hint.Int32;
            case Opcode.Float64ToTagged:
            case Opcode.Uint32ToNumber:
                return Hint.Float64;
            case Opcode.SmiConstant:
            case Opcode.Int32Constant:
                return Hint.Int32;
            case Opcode.Float64Constant:
                return Hint.Float64;
            case Opcode.Constant when input.Value0.IsNumber:
                return input.TryGetInt32Constant(out _) ? Hint.Int32 : Hint.Float64;
        }
        return Hint.Tagged;
    }

    static Dictionary<ValueNode, List<Node>> CollectUses(Graph graph)
    {
        var uses = new Dictionary<ValueNode, List<Node>>(ReferenceEqualityComparer.Instance);
        void Add(ValueNode v, Node user)
        {
            if (!uses.TryGetValue(v, out List<Node>? list)) uses[v] = list = [];
            list.Add(user);
        }
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis)
            {
                if (phi.IsExceptionPhi) continue;
                foreach (ValueNode input in phi.Inputs) Add(input, phi);
            }
            foreach (Node node in block.Nodes) foreach (ValueNode input in node.Inputs) Add(input, node);
        }
        return uses;
    }

    static bool HasUntaggingUse(Phi phi, Dictionary<ValueNode, List<Node>> uses, Dictionary<Phi, Hint> hints, HashSet<Phi> visited)
    {
        if (!visited.Add(phi)) return false;
        if (!uses.TryGetValue(phi, out List<Node>? users)) return false;
        foreach (Node user in users)
        {
            switch (user.Opcode)
            {
                case Opcode.CheckedSmiUntag:
                case Opcode.CheckedNumberToInt32:
                case Opcode.UnsafeSmiUntag:
                case Opcode.TruncateCheckedNumberOrOddballToInt32:
                case Opcode.CheckedNumberOrOddballToFloat64:
                case Opcode.UnsafeNumberToFloat64:
                    return true;
                case Opcode.Phi when user is Phi p && hints[p] != Hint.Tagged && HasUntaggingUse(p, uses, hints, visited):
                    return true;
            }
        }
        return false;
    }
}
