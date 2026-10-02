// Port of src/compiler/bytecode-analysis.{h,cc} and
// src/compiler/bytecode-liveness-map.{h,cc} (in Maglev/, its only user in
// V8Sharp; namespace V8Sharp.Maglev): the loop structure (headers,
// nesting, the registers assigned in each loop) and the register/accumulator
// liveness of a BytecodeArray, which Maglev's graph builder uses to decide
// which registers get loop phis and which values a deopt frame state keeps.
//
// The liveness transfer functions are V8's (UpdateInLiveness /
// UpdateOutLiveness: register operands by operand type, implicit accumulator
// and short-star uses, the Suspend/Resume pass-through, exception handlers
// making the handler's liveness and its context register live). Deviation:
// V8 does one backward pass and then re-processes loop bodies in a queue of
// loop ends; this port iterates the same equations over the bytecode in
// reverse order until nothing changes, which reaches the same fixed point.
using V8Sharp.Codegen;
using V8Sharp.Interpreter;

namespace V8Sharp.Maglev;

/// <summary>BytecodeLivenessState: the live registers and the accumulator at one point.</summary>
public sealed class BytecodeLivenessState
{
    readonly ulong[] _bits;
    readonly int _registerCount;

    public BytecodeLivenessState(int registerCount)
    {
        _registerCount = registerCount;
        _bits = new ulong[(registerCount + 1 + 63) >> 6];
    }

    public BytecodeLivenessState(BytecodeLivenessState other)
    {
        _registerCount = other._registerCount;
        _bits = (ulong[])other._bits.Clone();
    }

    public int RegisterCount => _registerCount;

    public bool RegisterIsLive(int index) => (_bits[index >> 6] & (1UL << (index & 63))) != 0;
    public void MarkRegisterLive(int index) => _bits[index >> 6] |= 1UL << (index & 63);
    public void MarkRegisterDead(int index) => _bits[index >> 6] &= ~(1UL << (index & 63));
    public bool AccumulatorIsLive() => RegisterIsLive(_registerCount);
    public void MarkAccumulatorLive() => MarkRegisterLive(_registerCount);
    public void MarkAccumulatorDead() => MarkRegisterDead(_registerCount);

    public void CopyFrom(BytecodeLivenessState other) => other._bits.AsSpan().CopyTo(_bits);

    public void Union(BytecodeLivenessState other)
    {
        for (int i = 0; i < _bits.Length; i++) _bits[i] |= other._bits[i];
    }

    /// <summary>Union, returning whether anything changed.</summary>
    public bool UnionIsChanged(BytecodeLivenessState other)
    {
        bool changed = false;
        for (int i = 0; i < _bits.Length; i++)
        {
            ulong merged = _bits[i] | other._bits[i];
            if (merged != _bits[i])
            {
                _bits[i] = merged;
                changed = true;
            }
        }
        return changed;
    }

    public bool Equals(BytecodeLivenessState other) => _bits.AsSpan().SequenceEqual(other._bits);

    /// <summary>The number of live registers (without the accumulator).</summary>
    public int LiveValueCount()
    {
        int count = 0;
        for (int i = 0; i < _registerCount; i++) if (RegisterIsLive(i)) count++;
        return count;
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _registerCount; i++) sb.Append(RegisterIsLive(i) ? 'L' : '.');
        sb.Append(AccumulatorIsLive() ? 'L' : '.');
        return sb.ToString();
    }
}

/// <summary>BytecodeLoopAssignments: the parameters and registers assigned inside a loop.</summary>
public sealed class BytecodeLoopAssignments(int parameterCount, int registerCount)
{
    readonly bool[] _bits = new bool[parameterCount + registerCount];

    public int ParameterCount => parameterCount;
    public int LocalCount => registerCount;

    public void Add(Register r)
    {
        if (r.IsParameter)
        {
            int index = r.ToParameterIndex();
            if ((uint)index < (uint)parameterCount) _bits[index] = true;
        }
        else if ((uint)r.Index < (uint)registerCount)
        {
            _bits[parameterCount + r.Index] = true;
        }
    }

    public void AddList(Register r, int count)
    {
        for (int i = 0; i < count; i++) Add(new Register(r.Index + i));
    }

    public void Union(BytecodeLoopAssignments other)
    {
        for (int i = 0; i < _bits.Length; i++) _bits[i] |= other._bits[i];
    }

    public bool ContainsParameter(int index) => _bits[index];
    public bool ContainsLocal(int index) => _bits[parameterCount + index];
}

/// <summary>LoopInfo.</summary>
public sealed class LoopInfo(int parentOffset, int loopStart, int loopEnd, int jumpLoopOffset, int parameterCount, int registerCount)
{
    public int ParentOffset { get; } = parentOffset;
    public int LoopStart { get; } = loopStart;
    public int LoopEnd { get; } = loopEnd;
    public int JumpLoopOffset { get; } = jumpLoopOffset;
    public bool Resumable { get; internal set; }
    public bool Innermost { get; internal set; } = true;
    public BytecodeLoopAssignments Assignments { get; } = new(parameterCount, registerCount);

    public bool Contains(int offset) => offset >= LoopStart && offset < LoopEnd;
}

/// <summary>BytecodeAnalysis.</summary>
public sealed class BytecodeAnalysis
{
    readonly BytecodeArray _bytecode;
    readonly JSValue[] _constants;
    readonly SortedDictionary<int, LoopInfo> _headerToInfo = [];
    readonly Dictionary<int, int> _endToHeader = [];
    readonly List<LoopInfo> _loopInfos = [];
    readonly int[] _offsets;
    readonly int[] _indexOfOffset;
    BytecodeLivenessState?[]? _in;
    BytecodeLivenessState?[]? _out;

    public BytecodeAnalysis(BytecodeArray bytecode, JSValue[] constantPoolValues, int osrBailoutId = -1, bool analyzeLiveness = true)
    {
        _bytecode = bytecode;
        _constants = constantPoolValues;
        var offsets = new List<int>();
        var it = new BytecodeArrayIterator(bytecode);
        for (; !it.Done(); it.Advance()) offsets.Add(it.CurrentOffset());
        _offsets = offsets.ToArray();
        _indexOfOffset = new int[bytecode.Length + 1];
        Array.Fill(_indexOfOffset, -1);
        for (int i = 0; i < _offsets.Length; i++) _indexOfOffset[_offsets[i]] = i;
        OsrBailoutId = osrBailoutId;
        AnalyzeLoops();
        if (analyzeLiveness) AnalyzeLiveness();
    }

    public int OsrBailoutId { get; }
    /// <summary>The loop header of the OSR'd loop (osr_entry_point), or -1.</summary>
    public int OsrEntryPoint { get; private set; } = -1;
    public int BytecodeCount => _offsets.Length;
    public IReadOnlyList<LoopInfo> GetLoopInfos() => _loopInfos;

    public bool IsLoopHeader(int offset) => _headerToInfo.ContainsKey(offset);

    public LoopInfo GetLoopInfoFor(int headerOffset) => _headerToInfo[headerOffset];
    public LoopInfo? TryGetLoopInfoFor(int headerOffset) => _headerToInfo.GetValueOrDefault(headerOffset);

    /// <summary>The header offset of the innermost loop containing <paramref name="offset"/>, or -1.</summary>
    public int GetLoopOffsetFor(int offset)
    {
        int best = -1;
        foreach (LoopInfo info in _loopInfos)
        {
            if (info.Contains(offset) && info.LoopStart > best) best = info.LoopStart;
        }
        return best;
    }

    public BytecodeLivenessState GetInLivenessFor(int offset) => _in![_indexOfOffset[offset]]!;
    public BytecodeLivenessState GetOutLivenessFor(int offset) => _out![_indexOfOffset[offset]]!;

    // ---- Jump targets through the materialized constant pool ----------------------------------

    /// <summary>The absolute jump target of the current jump (constant jumps read the materialized pool).</summary>
    public static int JumpTargetOffset(BytecodeArrayIterator it, JSValue[] constants)
    {
        Bytecode bytecode = it.CurrentBytecode();
        int relative;
        if (Bytecodes.IsJumpImmediate(bytecode))
        {
            relative = (int)it.GetUnsignedImmediateOperand(0);
            if (bytecode == Bytecode.JumpLoop) relative = -relative;
        }
        else
        {
            relative = (int)constants[(int)it.GetConstantPoolIndexOperand(0)].Number;
        }
        return it.GetAbsoluteOffset(relative);
    }

    /// <summary>The (case value, absolute target) pairs of the current switch, skipping holes.</summary>
    public static List<(int CaseValue, int Target)> JumpTableTargets(BytecodeArrayIterator it, JSValue[] constants)
    {
        int tableStart, tableSize, caseValueBase;
        if (it.CurrentBytecode() == Bytecode.SwitchOnGeneratorState)
        {
            tableStart = (int)it.GetConstantPoolIndexOperand(1);
            tableSize = (int)it.GetUnsignedImmediateOperand(2);
            caseValueBase = 0;
        }
        else
        {
            tableStart = (int)it.GetConstantPoolIndexOperand(0);
            tableSize = (int)it.GetUnsignedImmediateOperand(1);
            caseValueBase = it.GetImmediateOperand(2);
        }
        var result = new List<(int, int)>(tableSize);
        for (int i = 0; i < tableSize; i++)
        {
            JSValue entry = constants[tableStart + i];
            if (!entry.IsNumber) continue;
            result.Add((caseValueBase + i, it.GetAbsoluteOffset((int)entry.Number)));
        }
        return result;
    }

    // ---- Loops ----------------------------------------------------------------------------------

    void AnalyzeLoops()
    {
        int parameterCount = _bytecode.ParameterCount;
        int registerCount = _bytecode.RegisterCount;
        var it = new BytecodeArrayIterator(_bytecode);
        // Collect the loops (header, end) in bytecode order of their JumpLoops.
        var loops = new List<(int Header, int End, int JumpLoop)>();
        for (; !it.Done(); it.Advance())
        {
            if (it.CurrentBytecode() != Bytecode.JumpLoop) continue;
            int jumpLoop = it.CurrentOffset();
            int header = JumpTargetOffset(it, _constants);
            int end = jumpLoop + it.CurrentBytecodeSize();
            loops.Add((header, end, jumpLoop));
            if (jumpLoop == OsrBailoutId) OsrEntryPoint = header;
        }
        // Nesting: the parent is the innermost other loop that contains the header.
        loops.Sort((a, b) => a.Header.CompareTo(b.Header));
        foreach ((int header, int end, int jumpLoop) in loops)
        {
            int parent = -1;
            foreach (LoopInfo candidate in _loopInfos)
            {
                if (candidate.Contains(header) && candidate.LoopStart < header) parent = candidate.LoopStart;
            }
            var info = new LoopInfo(parent, header, end, jumpLoop, parameterCount, registerCount);
            if (parent >= 0) _headerToInfo[parent].Innermost = false;
            _loopInfos.Add(info);
            _headerToInfo[header] = info;
            _endToHeader[end] = header;
        }
        if (_loopInfos.Count == 0) return;

        // Assignments: every register written by a bytecode inside the loop
        // (inner loops' assignments are included by the range).
        it = new BytecodeArrayIterator(_bytecode);
        for (; !it.Done(); it.Advance())
        {
            int offset = it.CurrentOffset();
            Bytecode bytecode = it.CurrentBytecode();
            foreach (LoopInfo info in _loopInfos)
            {
                if (!info.Contains(offset)) continue;
                UpdateAssignments(bytecode, info.Assignments, it);
                if (bytecode == Bytecode.ResumeGenerator) info.Resumable = true;
            }
        }
    }

    static void UpdateAssignments(Bytecode bytecode, BytecodeLoopAssignments assignments, BytecodeArrayIterator it)
    {
        ReadOnlySpan<OperandType> operandTypes = Bytecodes.GetOperandTypes(bytecode);
        for (int i = 0; i < operandTypes.Length; ++i)
        {
            switch (operandTypes[i])
            {
                case OperandType.RegInOut:
                case OperandType.RegOut:
                    assignments.Add(it.GetRegisterOperand(i));
                    break;
                case OperandType.RegOutList:
                {
                    Register r = it.GetRegisterOperand(i++);
                    int count = (int)it.GetRegisterCountOperand(i);
                    assignments.AddList(r, count);
                    break;
                }
                case OperandType.RegOutPair:
                    assignments.AddList(it.GetRegisterOperand(i), 2);
                    break;
                case OperandType.RegOutTriple:
                    assignments.AddList(it.GetRegisterOperand(i), 3);
                    break;
            }
        }
        if (Bytecodes.WritesImplicitRegister(bytecode)) assignments.Add(Register.FromShortStar(bytecode));
    }

    // ---- Liveness ---------------------------------------------------------------------------------

    void AnalyzeLiveness()
    {
        int n = _offsets.Length;
        int registerCount = _bytecode.RegisterCount;
        _in = new BytecodeLivenessState[n];
        _out = new BytecodeLivenessState[n];
        for (int i = 0; i < n; i++)
        {
            _in[i] = new BytecodeLivenessState(registerCount);
            _out[i] = new BytecodeLivenessState(registerCount);
        }
        bool hasHandlers = _bytecode.HandlerTable.Length != 0;
        var table = new HandlerTable(_bytecode.HandlerTable);

        // Decode every bytecode once: successors and the in-transfer.
        var successors = new List<int>[n];
        var iterators = new BytecodeArrayIterator(_bytecode);
        var handlerData = new (int Handler, int ContextRegister)[n];
        for (int i = 0; i < n; i++, iterators.Advance())
        {
            Bytecode bytecode = iterators.CurrentBytecode();
            var succ = new List<int>(2);
            handlerData[i] = (-1, -1);
            bool special = bytecode is Bytecode.SuspendGenerator or Bytecode.ResumeGenerator;
            if (special || bytecode == Bytecode.SwitchOnGeneratorState)
            {
                if (i + 1 < n) succ.Add(i + 1);
            }
            else
            {
                if (i + 1 < n && !Bytecodes.IsUnconditionalJump(bytecode) && !Bytecodes.Returns(bytecode) &&
                    !Bytecodes.UnconditionallyThrows(bytecode))
                {
                    succ.Add(i + 1);
                }
                if (Bytecodes.IsJump(bytecode))
                {
                    succ.Add(_indexOfOffset[JumpTargetOffset(iterators, _constants)]);
                }
                else if (Bytecodes.IsSwitch(bytecode))
                {
                    foreach ((int _, int target) in JumpTableTargets(iterators, _constants)) succ.Add(_indexOfOffset[target]);
                }
                if (hasHandlers && !Bytecodes.IsWithoutExternalSideEffects(bytecode))
                {
                    int handlerIndex = table.LookupHandlerIndexForRange(iterators.CurrentOffset());
                    if (handlerIndex >= 0)
                    {
                        handlerData[i] = (_indexOfOffset[table.GetRangeHandler((uint)handlerIndex)],
                            table.GetRangeData((uint)handlerIndex));
                    }
                }
            }
            successors[i] = succ;
        }

        var scratch = new BytecodeLivenessState(registerCount);
        bool changed = true;
        while (changed)
        {
            changed = false;
            var it = new BytecodeArrayIterator(_bytecode);
            // Walk backwards: collect the iterator positions once.
            for (int i = n - 1; i >= 0; i--)
            {
                it.SetOffset(_offsets[i]);
                Bytecode bytecode = it.CurrentBytecode();
                BytecodeLivenessState outState = _out[i]!;
                scratch.CopyFrom(outState);
                foreach (int s in successors[i]) scratch.Union(_in[s]!);
                if (bytecode == Bytecode.SwitchOnGeneratorState)
                {
                    scratch.MarkRegisterLive(it.GetRegisterOperand(0).Index);
                }
                (int handler, int contextRegister) = handlerData[i];
                if (handler >= 0)
                {
                    bool wasAccumulatorLive = scratch.AccumulatorIsLive();
                    scratch.Union(_in[handler]!);
                    if (contextRegister >= 0 && contextRegister < registerCount) scratch.MarkRegisterLive(contextRegister);
                    if (!wasAccumulatorLive) scratch.MarkAccumulatorDead();
                }
                if (!scratch.Equals(outState))
                {
                    outState.CopyFrom(scratch);
                    changed = true;
                }
                scratch.CopyFrom(outState);
                UpdateInLiveness(bytecode, scratch, it);
                if (!scratch.Equals(_in[i]!))
                {
                    _in[i]!.CopyFrom(scratch);
                    changed = true;
                }
            }
        }
    }

    /// <summary>UpdateInLiveness: kill the outputs, then mark the inputs live.</summary>
    static void UpdateInLiveness(Bytecode bytecode, BytecodeLivenessState inLiveness, BytecodeArrayIterator it)
    {
        int registerCount = inLiveness.RegisterCount;
        if (bytecode == Bytecode.SuspendGenerator)
        {
            // The generator object has to be live; Suspend also reads and returns the accumulator.
            MarkLive(inLiveness, it.GetRegisterOperand(0), registerCount);
            inLiveness.MarkAccumulatorLive();
            return;
        }
        if (bytecode == Bytecode.ResumeGenerator)
        {
            MarkLive(inLiveness, it.GetRegisterOperand(0), registerCount);
            return;
        }

        ImplicitRegisterUse use = Bytecodes.GetImplicitRegisterUse(bytecode);
        if ((use & ImplicitRegisterUse.WriteAccumulator) != 0) inLiveness.MarkAccumulatorDead();
        ReadOnlySpan<OperandType> operandTypes = Bytecodes.GetOperandTypes(bytecode);
        for (int i = 0; i < operandTypes.Length; i++)
        {
            switch (operandTypes[i])
            {
                case OperandType.RegOut:
                case OperandType.RegInOut:
                    MarkDead(inLiveness, it.GetRegisterOperand(i), registerCount);
                    break;
                case OperandType.RegOutList:
                {
                    Register r = it.GetRegisterOperand(i);
                    int count = (int)it.GetRegisterCountOperand(i + 1);
                    for (int j = 0; j < count; j++) MarkDead(inLiveness, new Register(r.Index + j), registerCount);
                    break;
                }
                case OperandType.RegOutPair:
                {
                    Register r = it.GetRegisterOperand(i);
                    MarkDead(inLiveness, r, registerCount);
                    MarkDead(inLiveness, new Register(r.Index + 1), registerCount);
                    break;
                }
                case OperandType.RegOutTriple:
                {
                    Register r = it.GetRegisterOperand(i);
                    for (int j = 0; j < 3; j++) MarkDead(inLiveness, new Register(r.Index + j), registerCount);
                    break;
                }
            }
        }
        if ((use & ImplicitRegisterUse.WriteShortStar) != 0) MarkDead(inLiveness, Register.FromShortStar(bytecode), registerCount);
        if ((use & ImplicitRegisterUse.ReadAccumulator) != 0) inLiveness.MarkAccumulatorLive();
        for (int i = 0; i < operandTypes.Length; i++)
        {
            switch (operandTypes[i])
            {
                case OperandType.Reg:
                case OperandType.RegInOut:
                    MarkLive(inLiveness, it.GetRegisterOperand(i), registerCount);
                    break;
                case OperandType.RegPair:
                {
                    Register r = it.GetRegisterOperand(i);
                    MarkLive(inLiveness, r, registerCount);
                    MarkLive(inLiveness, new Register(r.Index + 1), registerCount);
                    break;
                }
                case OperandType.RegList:
                {
                    Register r = it.GetRegisterOperand(i);
                    int count = (int)it.GetRegisterCountOperand(i + 1);
                    for (int j = 0; j < count; j++) MarkLive(inLiveness, new Register(r.Index + j), registerCount);
                    break;
                }
            }
        }
    }

    static void MarkLive(BytecodeLivenessState state, Register r, int registerCount)
    {
        if (!r.IsParameter && r.Index < registerCount) state.MarkRegisterLive(r.Index);
    }

    static void MarkDead(BytecodeLivenessState state, Register r, int registerCount)
    {
        if (!r.IsParameter && r.Index < registerCount) state.MarkRegisterDead(r.Index);
    }
}
