// Port of src/maglev/maglev-basic-block.h, maglev-graph.h,
// maglev-compilation-info.{h,cc} and maglev-compilation-unit.{h,cc}.
//
// V8's compilation info owns the zone, the JSHeapBroker and the
// CompilationDependencies; V8Sharp reads the live heap directly (no broker or
// serialization: the compile runs on the main thread), so the info holds the
// dependencies to install with the code and the code generator's state.
using System.Reflection.Emit;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>BasicBlock.</summary>
public sealed class BasicBlock(int id)
{
    public int Id = id;
    public readonly List<Phi> Phis = [];
    public readonly List<Node> Nodes = [];
    public ControlNode? Control;
    public readonly List<BasicBlock> Predecessors = [];
    /// <summary>The merge state this block starts with (merge points only).</summary>
    public MergePointInterpreterFrameState? State;
    public bool IsLoopHeader;
    /// <summary>The bytecode offset the block starts at (-1 for blocks not at a bytecode, e.g. edge splits).</summary>
    public int Offset = -1;
    /// <summary>Code generation: the IL label of the block.</summary>
    internal Label Label;
    internal bool LabelDefined;
    /// <summary>Code generation: whether the block is emitted (reachable).</summary>
    public bool IsDead;

    public int PredecessorIndexOf(BasicBlock predecessor)
    {
        int i = Predecessors.IndexOf(predecessor);
        if (i < 0) throw new InvalidOperationException("not a predecessor");
        return i;
    }

    public IEnumerable<BasicBlock> Successors()
    {
        ControlNode? c = Control;
        if (c is null) yield break;
        if (c.Targets is not null)
        {
            foreach (BasicBlock? t in c.Targets) if (t is not null) yield return t;
        }
        if (c.Target is not null) yield return c.Target;
        if (c.FalseTarget is not null) yield return c.FalseTarget;
    }

    public override string ToString() => $"Block b{Id}";
}

/// <summary>Graph.</summary>
public sealed class Graph
{
    public readonly List<BasicBlock> Blocks = [];
    readonly Dictionary<int, ValueNode> _smiConstants = [];
    readonly Dictionary<int, ValueNode> _int32Constants = [];
    readonly Dictionary<long, ValueNode> _float64Constants = [];
    readonly ValueNode?[] _rootConstants = new ValueNode?[5];
    readonly Dictionary<object, ValueNode> _heapConstants = new(ReferenceEqualityComparer.Instance);
    /// <summary>Constants are emitted in the first block (V8 keeps them out of blocks; the code generator materialises them).</summary>
    public readonly List<ValueNode> Constants = [];
    int _nextNodeId = 1;
    int _nextBlockId;

    public int NewNodeId() => _nextNodeId++;
    public BasicBlock NewBlock() => new(_nextBlockId++);
    public int NodeCount => _nextNodeId;

    ValueNode AddConstant(ValueNode node)
    {
        node.Id = NewNodeId();
        Constants.Add(node);
        return node;
    }

    public ValueNode GetSmiConstant(int value)
    {
        if (_smiConstants.TryGetValue(value, out ValueNode? node)) return node;
        node = AddConstant(new ValueNode(Opcode.SmiConstant, ValueRepresentation.kTagged)
        {
            Int0 = value,
            Type = NodeTypes.ForConstant(JSValue.FromInt(value)),
        });
        return _smiConstants[value] = node;
    }

    public ValueNode GetInt32Constant(int value)
    {
        if (_int32Constants.TryGetValue(value, out ValueNode? node)) return node;
        node = AddConstant(new ValueNode(Opcode.Int32Constant, ValueRepresentation.kInt32)
        {
            Int0 = value,
            Type = NodeTypes.ForConstant(JSValue.FromInt(value)),
        });
        return _int32Constants[value] = node;
    }

    public ValueNode GetFloat64Constant(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        if (_float64Constants.TryGetValue(bits, out ValueNode? node)) return node;
        node = AddConstant(new ValueNode(Opcode.Float64Constant, ValueRepresentation.kFloat64)
        {
            Double0 = value,
            Type = NodeType.kNumber,
        });
        return _float64Constants[bits] = node;
    }

    public ValueNode GetRootConstant(RootIndex index)
    {
        ref ValueNode? slot = ref _rootConstants[(int)index];
        return slot ??= AddConstant(new ValueNode(Opcode.RootConstant, ValueRepresentation.kTagged)
        {
            Int0 = (int)index,
            Type = NodeTypes.ForConstant(ValueNode.RootValue(index)),
        });
    }

    public ValueNode GetBooleanConstant(bool value) => GetRootConstant(value ? RootIndex.kTrueValue : RootIndex.kFalseValue);

    /// <summary>GetConstant: a constant tagged value.</summary>
    public ValueNode GetConstant(JSValue value)
    {
        if (value.IsUndefined) return GetRootConstant(RootIndex.kUndefinedValue);
        if (value.IsNumber)
        {
            if (value.IsSmi) return GetSmiConstant((int)value.Number);
            return GetFloat64TaggedConstant(value.Number);
        }
        HeapObject o = value.Object;
        if (ReferenceEquals(o, Oddball.Null)) return GetRootConstant(RootIndex.kNullValue);
        if (ReferenceEquals(o, Oddball.True)) return GetRootConstant(RootIndex.kTrueValue);
        if (ReferenceEquals(o, Oddball.False)) return GetRootConstant(RootIndex.kFalseValue);
        if (ReferenceEquals(o, Oddball.TheHole)) return GetRootConstant(RootIndex.kTheHoleValue);
        if (_heapConstants.TryGetValue(o, out ValueNode? node)) return node;
        node = AddConstant(new ValueNode(Opcode.Constant, ValueRepresentation.kTagged)
        {
            Value0 = value,
            Type = NodeTypes.ForConstant(value),
        });
        return _heapConstants[o] = node;
    }

    readonly Dictionary<long, ValueNode> _taggedNumberConstants = [];

    ValueNode GetFloat64TaggedConstant(double d)
    {
        long bits = BitConverter.DoubleToInt64Bits(d);
        if (_taggedNumberConstants.TryGetValue(bits, out ValueNode? node)) return node;
        node = AddConstant(new ValueNode(Opcode.Constant, ValueRepresentation.kTagged)
        {
            Value0 = JSValue.FromNumber(d),
            Type = NodeType.kHeapNumber,
        });
        return _taggedNumberConstants[bits] = node;
    }
}

/// <summary>MaglevCompilationUnit: one function of the compilation (the top-level one or an inlined one).</summary>
public sealed class MaglevCompilationUnit
{
    public MaglevCompilationUnit(MaglevCompilationInfo info, JSFunction? function, SharedFunctionInfo shared, FeedbackVector feedback,
        MaglevCompilationUnit? caller, int inliningDepth)
    {
        Info = info;
        Function = function;
        SharedFunctionInfo = shared;
        Bytecode = (BytecodeArray)shared.FunctionData!;
        Feedback = feedback;
        Caller = caller;
        InliningDepth = inliningDepth;
        ConstantPool = Bytecode.ConstantPoolValues ?? InterpreterRuntime.MaterializeConstantPool(info.Isolate, Bytecode);
    }

    public MaglevCompilationInfo Info { get; }
    /// <summary>The closure, when known (always for the top-level unit and for inlined known targets).</summary>
    public JSFunction? Function { get; }
    public SharedFunctionInfo SharedFunctionInfo { get; }
    public BytecodeArray Bytecode { get; }
    public FeedbackVector Feedback { get; }
    public JSValue[] ConstantPool { get; }
    public MaglevCompilationUnit? Caller { get; }
    public int InliningDepth { get; }
    public bool IsInline => Caller is not null;

    /// <summary>The closure's value node (the frame's function_closure register).</summary>
    public ValueNode? Closure { get; set; }

    public int ParameterCount => Bytecode.ParameterCount;
    public int RegisterCount => Bytecode.RegisterCount;

    BytecodeAnalysis? _analysis;
    public BytecodeAnalysis BytecodeAnalysis =>
        _analysis ??= new BytecodeAnalysis(Bytecode, ConstantPool, IsInline ? -1 : Info.OsrOffset);

    // Code generation state of the unit's frame (inlined frames).
    internal LocalBuilder? FpLocal;
    internal LocalBuilder? FpRefLocal;
    internal LocalBuilder? FrameRecordLocal;
    internal LocalBuilder? RegisterStartLocal;

    public override string ToString() => SharedFunctionInfo.Name().ToString();
}

/// <summary>A dependency of the code (CompilationDependency): invalidating it deoptimizes the code.</summary>
public readonly record struct CompilationDependency(HeapObject Object, Objects.DependentCode.DependencyGroups Groups);

/// <summary>MaglevCompilationInfo.</summary>
public sealed class MaglevCompilationInfo
{
    public MaglevCompilationInfo(Isolate isolate, JSFunction function, int osrOffset)
    {
        Isolate = isolate;
        Function = function;
        OsrOffset = osrOffset;
        Toplevel = new MaglevCompilationUnit(this, function, function.Shared,
            (FeedbackVector)function.RawFeedbackCell.Value!, null, 0);
    }

    public Isolate Isolate { get; }
    public JSFunction Function { get; }
    /// <summary>The JumpLoop offset of an OSR compilation (BytecodeOffset), or -1.</summary>
    public int OsrOffset { get; }
    public bool IsOsr => OsrOffset >= 0;
    public MaglevCompilationUnit Toplevel { get; }
    public Graph Graph { get; } = new();

    /// <summary>The dependencies to register with the code (CompilationDependencies::Commit).</summary>
    public readonly List<CompilationDependency> Dependencies = [];

    /// <summary>The bytecode size inlined so far (max_maglev_inlined_bytecode_size_cumulative).</summary>
    public int InlinedBytecodeSize;
    /// <summary>The deepest inlining depth reached.</summary>
    public int MaxInliningDepth;

    public bool IsTracing => Isolate.Flags.trace_maglev_graph_building;

    public void AddDependency(HeapObject obj, Objects.DependentCode.DependencyGroups groups) =>
        Dependencies.Add(new CompilationDependency(obj, groups));

    /// <summary>The reason a compilation gave up, for --trace-opt.</summary>
    public string? BailoutReason;
}
