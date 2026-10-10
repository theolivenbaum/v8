// Port of src/maglev/maglev-truncation.{h,cc} (TruncationProcessor) for
// int32 additions and subtractions: an Int32AddWithOverflow /
// Int32SubtractWithOverflow whose every use truncates its result to int32
// (bitwise operations and shifts, or another addition that is truncated
// itself) cannot observe an overflow: the exact sum of int32 values is a safe
// integer, and ToInt32 of it is the wrapping int32 sum. It becomes a
// wrapping Int32Add / Int32Subtract without the overflow deopt.
//
// Deviation: V8's pass works on the ranges of maglev-range-analysis.h (and
// so also truncates multiplications and Float64 operations whose result is a
// safe integer); V8Sharp has no range analysis, so it bounds the magnitude by
// the depth of the truncated chain (each level at most doubles it: 21 levels
// keep the exact result below 2^53) and handles additions, subtractions and
// multiplications of int32 values, and Float64 additions and subtractions
// of int32 values whose uses all truncate. A value used by a deopt frame, a phi or anything else is not truncated.
namespace V8Sharp.Maglev;

internal static class MaglevTruncation
{
    /// <summary>The exact result of a truncated operation must be a safe integer.</summary>
    const double kMaxSafeInteger = 9007199254740991.0;

    public static void Run(Graph graph)
    {
        TruncateFloat64OfInt32(graph);
        // Candidates and their uses.
        var candidates = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode { Opcode: Opcode.Int32AddWithOverflow or Opcode.Int32SubtractWithOverflow or Opcode.Int32MultiplyWithOverflow } v) candidates.Add(v);
            }
        }
        if (candidates.Count == 0) return;
        var truncatingUsers = new Dictionary<ValueNode, List<ValueNode>>(ReferenceEqualityComparer.Instance);
        var excluded = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        void UseFrame(DeoptFrame? frame)
        {
            for (DeoptFrame? f = frame; f is not null; f = f.Parent)
            {
                foreach ((Interpreter.Register _, ValueNode value) in ((InterpretedDeoptFrame)f).Values)
                {
                    if (candidates.Contains(value)) excluded.Add(value);
                }
            }
        }
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis)
            {
                foreach (ValueNode input in phi.Inputs) if (candidates.Contains(input)) excluded.Add(input);
            }
            foreach (Node node in block.Nodes)
            {
                bool truncates = IsTruncatingUse(node.Opcode);
                foreach (ValueNode input in node.Inputs)
                {
                    if (!candidates.Contains(input) || truncates) continue;
                    if (node is ValueNode user && candidates.Contains(user))
                    {
                        // Truncating only if the user addition is truncated itself.
                        if (!truncatingUsers.TryGetValue(input, out List<ValueNode>? users)) truncatingUsers[input] = users = [];
                        users.Add(user);
                    }
                    else
                    {
                        excluded.Add(input);
                    }
                }
                UseFrame(node.EagerDeoptInfo?.TopFrame);
                UseFrame(node.LazyDeoptInfo?.TopFrame);
            }
            ControlNode control = block.Control!;
            foreach (ValueNode input in control.Inputs) if (candidates.Contains(input)) excluded.Add(input);
            UseFrame(control.EagerDeoptInfo?.TopFrame);
        }
        // Greatest fixpoint: a candidate stays truncatable while all its users
        // truncate (an addition user must be truncatable itself).
        var truncatable = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (ValueNode c in candidates) if (!excluded.Contains(c)) truncatable.Add(c);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (ValueNode c in truncatable.ToArray())
            {
                if (!truncatingUsers.TryGetValue(c, out List<ValueNode>? users)) continue;
                foreach (ValueNode user in users)
                {
                    if (truncatable.Contains(user)) continue;
                    truncatable.Remove(c);
                    changed = true;
                    break;
                }
            }
        }
        if (truncatable.Count == 0) return;
        // The magnitude of the exact result (a bound from the inputs' ranges).
        var magnitude = new Dictionary<ValueNode, double>(ReferenceEqualityComparer.Instance);
        double Magnitude(ValueNode v)
        {
            if (!truncatable.Contains(v)) return InputMagnitude(v);
            if (magnitude.TryGetValue(v, out double m)) return m;
            double a = Magnitude(v.Inputs[0]), b = Magnitude(v.Inputs[1]);
            m = v.Opcode == Opcode.Int32MultiplyWithOverflow ? a * b : a + b;
            magnitude[v] = m;
            return m;
        }
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node is not ValueNode v || !truncatable.Contains(v) || Magnitude(v) > kMaxSafeInteger) continue;
                v.Opcode = v.Opcode switch
                {
                    Opcode.Int32AddWithOverflow => Opcode.Int32Add,
                    Opcode.Int32SubtractWithOverflow => Opcode.Int32Subtract,
                    _ => Opcode.Int32Multiply,
                };
                v.Properties &= ~OpProperties.kEagerDeopt;
                v.EagerDeoptInfo = null;
            }
        }
    }

    /// <summary>
    /// A Float64Add or Float64Subtract of int32 values (ChangeInt32ToFloat64,
    /// int32 constants) whose every use is TruncateFloat64ToInt32: the exact
    /// result is below 2^33, so its ToInt32 is the wrapping int32 operation,
    /// and each truncation becomes an Int32Add / Int32Subtract of the int32
    /// inputs (V8's truncation pass reaches the same through the ranges of
    /// its range analysis). Additions whose int32 feedback once overflowed
    /// (Crypto's am3: <c>xl*l + ... + c</c>, then <c>l&amp;0xfffffff</c> and
    /// <c>l&gt;&gt;28</c>) stay int32 instead of a double add and a ToInt32.
    /// </summary>
    static void TruncateFloat64OfInt32(Graph graph)
    {
        var candidates = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode { Opcode: Opcode.Float64Add or Opcode.Float64Subtract } v &&
                    Int32Source(v.Inputs[0], graph) is not null && Int32Source(v.Inputs[1], graph) is not null)
                {
                    candidates.Add(v);
                }
            }
        }
        if (candidates.Count == 0) return;
        var excluded = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);
        void Exclude(ValueNode v)
        {
            if (candidates.Contains(v)) excluded.Add(v);
        }
        void UseFrame(DeoptFrame? frame)
        {
            for (DeoptFrame? f = frame; f is not null; f = f.Parent)
            {
                foreach ((Interpreter.Register _, ValueNode value) in ((InterpretedDeoptFrame)f).Values) Exclude(value);
            }
        }
        var truncations = new List<ValueNode>();
        foreach (BasicBlock block in graph.Blocks)
        {
            if (block.IsDead) continue;
            foreach (Phi phi in block.Phis) foreach (ValueNode input in phi.Inputs) Exclude(input);
            foreach (Node node in block.Nodes)
            {
                if (node is ValueNode { Opcode: Opcode.TruncateFloat64ToInt32 } t && candidates.Contains(t.Inputs[0]))
                {
                    truncations.Add(t);
                }
                else
                {
                    foreach (ValueNode input in node.Inputs) Exclude(input);
                }
                UseFrame(node.EagerDeoptInfo?.TopFrame);
                UseFrame(node.LazyDeoptInfo?.TopFrame);
            }
            ControlNode control = block.Control!;
            foreach (ValueNode input in control.Inputs) Exclude(input);
            UseFrame(control.EagerDeoptInfo?.TopFrame);
        }
        foreach (ValueNode t in truncations)
        {
            ValueNode f = t.Inputs[0];
            if (excluded.Contains(f)) continue;
            t.Opcode = f.Opcode == Opcode.Float64Add ? Opcode.Int32Add : Opcode.Int32Subtract;
            t.Inputs = [Int32Source(f.Inputs[0], graph)!, Int32Source(f.Inputs[1], graph)!];
        }
    }

    /// <summary>The int32 value a Float64 input is exactly (a ChangeInt32ToFloat64's input, an int32 constant), or null.</summary>
    static ValueNode? Int32Source(ValueNode v, Graph graph)
    {
        if (v.Opcode == Opcode.ChangeInt32ToFloat64 && v.Inputs[0].Representation == ValueRepresentation.kInt32) return v.Inputs[0];
        if (v.IsConstant && v.TryGetInt32Constant(out int c)) return graph.GetInt32Constant(c);
        return null;
    }

    /// <summary>
    /// The largest magnitude of an int32 value that is not truncated (the
    /// static ranges of maglev-range.h for constants, masks and shifts).
    /// </summary>
    static double InputMagnitude(ValueNode v)
    {
        if (v.IsConstant && v.TryGetInt32Constant(out int c)) return Math.Abs((double)c);
        switch (v.Opcode)
        {
            case Opcode.Int32BitwiseAnd:
            {
                double bound = 2147483648.0;
                foreach (ValueNode input in v.Inputs)
                {
                    if (input.IsConstant && input.TryGetInt32Constant(out int mask) && mask >= 0) bound = Math.Min(bound, mask);
                }
                return bound;
            }
            case Opcode.Int32ShiftRight:
            case Opcode.Int32ShiftRightLogical:
                if (v.Inputs[1].IsConstant && v.Inputs[1].TryGetInt32Constant(out int shift) && (shift & 31) != 0)
                {
                    return Math.Pow(2, (v.Opcode == Opcode.Int32ShiftRight ? 31 : 32) - (shift & 31));
                }
                return v.Opcode == Opcode.Int32ShiftRight ? 2147483648.0 : 4294967295.0;
            default:
                return 2147483648.0;
        }
    }

    /// <summary>Uses that only see the value truncated to int32 (ToInt32 semantics).</summary>
    static bool IsTruncatingUse(Opcode opcode) => opcode is Opcode.Int32BitwiseAnd or Opcode.Int32BitwiseOr or Opcode.Int32BitwiseXor or
        Opcode.Int32ShiftLeft or Opcode.Int32ShiftRight or Opcode.Int32ShiftRightLogical or Opcode.Int32BitwiseNot;
}
