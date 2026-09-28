// Port of src/regexp/experimental/experimental-interpreter.h and
// experimental-interpreter.cc.
//
// V8 checks for interrupts (stack guard) every 64 input characters and may
// return RETRY when the subject changes representation. There is no isolate
// here: interrupts are not polled and RETRY is never produced.

namespace V8Sharp.RegExp.Experimental;

public static class ExperimentalRegExpInterpreter
{
    /// <summary>
    /// --experimental-regexp-engine-capture-group-opt-max-memory-usage in MB
    /// (default 1024).
    /// </summary>
    public static ulong s_captureGroupOptMaxMemoryUsage = 1024;

    // Executes a bytecode program in breadth-first NFA mode, without
    // backtracking, and writes resulting captures.  "Breadth-first" means that
    // threads are executed in lockstep with respect to their input position,
    // i.e. the threads share a common input index.  This is similar to
    // breadth-first simulation of a non-deterministic finite automaton (nfa),
    // hence the name of the class.
    //
    // Returns the number of matches found, or a negative result code.
    public static int FindMatches(Instruction[] bytecode, int registerCountPerMatch, ReadOnlySpan<char> input,
        int startIndex, Span<int> outputRegisters)
    {
        var interpreter = new NfaInterpreter(bytecode, registerCountPerMatch, input.ToArray(), startIndex);
        return interpreter.FindMatches(outputRegisters);
    }

    const int kUndefinedRegisterValue = -1;
    const int kUndefinedMatchIndexValue = -1;
    const ulong kUndefinedClockValue = ulong.MaxValue;

    static bool IsLineTerminator(int c) => c == '\n' || c == '\r' || c == 0x2028 || c == 0x2029;

    static bool IsRegExpWord(int c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    static bool SatisfiesAssertion(RegExpAssertion.Type type, ReadOnlySpan<char> context, int position)
    {
        Debug.Assert(position <= context.Length);
        Debug.Assert(position >= 0);

        switch (type)
        {
            case RegExpAssertion.Type.START_OF_INPUT:
                return position == 0;
            case RegExpAssertion.Type.END_OF_INPUT:
                return position == context.Length;
            case RegExpAssertion.Type.END_OF_BUFFER:
                // \Z requires /u, which the experimental engine doesn't accept,
                // so this assertion type never reaches the interpreter.
                throw new InvalidOperationException("UNREACHABLE");
            case RegExpAssertion.Type.START_OF_LINE:
                if (position == 0) return true;
                return IsLineTerminator(context[position - 1]);
            case RegExpAssertion.Type.END_OF_LINE:
                if (position == context.Length) return true;
                return IsLineTerminator(context[position]);
            case RegExpAssertion.Type.BOUNDARY:
                if (context.Length == 0) return false;
                if (position == 0) return IsRegExpWord(context[position]);
                if (position == context.Length) return IsRegExpWord(context[position - 1]);
                return IsRegExpWord(context[position - 1]) != IsRegExpWord(context[position]);
            case RegExpAssertion.Type.NON_BOUNDARY:
                return !SatisfiesAssertion(RegExpAssertion.Type.BOUNDARY, context, position);
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    sealed class FilterGroups
    {
        public static int[] Filter(int pc, int[] registers, ulong[] quantifiersClocks, ulong[] captureClocks,
            ulong[]? lookaroundClocks, int[] filteredRegisters, Instruction[] bytecode)
        {
            // Capture groups that were not traversed in the last iteration of a
            // quantifier need to be discarded. In order to determine which groups need
            // to be discarded, the interpreter maintains a clock, an internal count of
            // bytecode instructions executed. Whenever it reaches a quantifier or
            // a capture group, it records the current clock. After a match is found,
            // the interpreter filters out capture groups that were defined in any other
            // iteration than the last. To do so, it compares the last clock value of
            // the group with the last clock value of its parent quantifier/group,
            // keeping only groups that were defined after the parent quantifier/group
            // last iteration.
            return new FilterGroups(pc, bytecode).Run(registers, quantifiersClocks, captureClocks, lookaroundClocks,
                filteredRegisters);
        }

        int _pc;
        // The last clock encountered (either from a quantifier or a capture group).
        // Any groups whose clock is less then max_clock_ needs to be discarded.
        ulong _maxClock;
        // Stores pc_ and max_clock_ when the interpreter enters a node.
        readonly Stack<int> _pcStack = new();
        readonly Stack<ulong> _maxClockStack = new();
        readonly Instruction[] _bytecode;

        FilterGroups(int pc, Instruction[] bytecode)
        {
            _pc = pc;
            _bytecode = bytecode;
        }

        // Goes back to the parent node, restoring pc_ and max_clock_. If already at
        // the root of the tree, completes the filtering process.
        void Up()
        {
            if (_pcStack.Count > 0)
            {
                _pc = _pcStack.Pop();
                _maxClock = _maxClockStack.Pop();
            }
        }

        // Increments pc_. When at the end of a node, goes back to the parent node.
        void IncrementPC()
        {
            if (IsAtNodeEnd())
            {
                Up();
            }
            else
            {
                _pc++;
            }
        }

        bool IsAtNodeEnd() =>
            _pc + 1 == _bytecode.Length || _bytecode[_pc + 1].opcode != Instruction.Opcode.FILTER_CHILD;

        int[] Run(int[] registers, ulong[] quantifiersClocks, ulong[] captureClocks, ulong[]? lookaroundClocks,
            int[] filteredRegisters)
        {
            _pcStack.Push(_pc);
            _maxClockStack.Push(_maxClock);

            while (_pcStack.Count != 0)
            {
                Instruction instr = _bytecode[_pc];
                switch (instr.opcode)
                {
                    case Instruction.Opcode.FILTER_CHILD:
                        // We only need to come back for the next instructions if we are at
                        // the end of the node.
                        if (!IsAtNodeEnd())
                        {
                            _pcStack.Push(_pc + 1);
                            _maxClockStack.Push(_maxClock);
                        }

                        // Enter the child's node.
                        _pc = instr.Pc;
                        break;

                    case Instruction.Opcode.FILTER_GROUP:
                    {
                        int groupId = instr.GroupId;

                        // Checks whether the captured group should be saved or discarded.
                        int registerId = 2 * groupId;
                        if (captureClocks[registerId] >= _maxClock && captureClocks[registerId] != kUndefinedClockValue)
                        {
                            filteredRegisters[registerId] = registers[registerId];
                            filteredRegisters[registerId + 1] = registers[registerId + 1];
                            IncrementPC();
                        }
                        else
                        {
                            // If the node should be discarded, all its children should be too.
                            // By going back to the parent, we don't visit the children, and
                            // therefore don't copy their registers.
                            Up();
                        }
                        break;
                    }

                    case Instruction.Opcode.FILTER_QUANTIFIER:
                    {
                        int quantifierId = instr.QuantifierId;

                        // Checks whether the quantifier should be saved or discarded.
                        if (quantifiersClocks[quantifierId] >= _maxClock)
                        {
                            _maxClock = quantifiersClocks[quantifierId];
                            IncrementPC();
                        }
                        else
                        {
                            Up();
                        }
                        break;
                    }

                    case Instruction.Opcode.FILTER_LOOKAROUND:
                        // Checks whether the lookaround should be saved or discarded.
                        if (lookaroundClocks is null || lookaroundClocks[instr.LookaroundId] >= _maxClock)
                        {
                            IncrementPC();
                        }
                        else
                        {
                            Up();
                        }
                        break;

                    default:
                        throw new InvalidOperationException("UNREACHABLE");
                }
            }

            return filteredRegisters;
        }
    }

    // A recycling allocator for fixed-size arrays (RecyclingZoneAllocator).
    sealed class ArrayPool<T>(int size)
    {
        readonly Stack<T[]> _free = new();

        public T[] Allocate() => _free.Count != 0 ? _free.Pop() : new T[size];

        public void Deallocate(T[]? array)
        {
            if (array is not null) _free.Push(array);
        }
    }

    // The state of a "thread" executing experimental regexp bytecode.  (Not to
    // be confused with an OS thread.)
    struct InterpreterThread
    {
        public enum ConsumedCharacter { DidConsume, DidNotConsume }

        // This thread's program counter, i.e. the index within `bytecode_` of the
        // next instruction to be executed.
        public int Pc;
        // The array of registers, which is always size
        // `register_count_per_match_`.
        public int[] RegisterArray;
        // An array containing the input index when the thread did match
        // a lookaround.
        public int[]? LookaroundMatchIndexArray;
        // Arrays containing the clock when the register/quantifier/lookaround was
        // last saved.
        public ulong[]? QuantifierClockArray;
        public ulong[]? CapturesClockArray;
        public ulong[]? LookaroundClockArray;
        // Describe whether the thread consumed a character since it last entered a
        // quantifier. Since quantifier iterations that match the empty string are
        // not allowed, we need to distinguish threads that are allowed to exit a
        // quantifier iteration from those that are not.
        public ConsumedCharacter ConsumedSinceLastQuantifier;
    }

    struct LookaroundInfo
    {
        public int MatchPc;
        public int CapturePc;
        public RegExpLookaround.Type Type;
    }

    // Stores the last input index at which a thread was activated for a given pc.
    // Two values are stored, depending on the value
    // consumed_since_last_quantifier of the thread.
    struct LastInputIndex
    {
        public int HavingConsumedCharacter;
        public int NotHavingConsumedCharacter;

        public static LastInputIndex Initial => new() { HavingConsumedCharacter = -1, NotHavingConsumedCharacter = -1 };
    }

    // See the description of NfaInterpreter in V8's
    // experimental-interpreter.cc: threads are kept in a priority-ordered list;
    // if a thread ACCEPTs, all threads with lower priority are discarded, and the
    // search continues with the threads with higher priority. Lookarounds are
    // handled either by running captureless lookbehinds as threads in parallel
    // with the main expression, or (with capture group opt) by precomputing a
    // lookaround table over the whole input.
    sealed class NfaInterpreter
    {
        readonly Instruction[] _bytecode;
        // Number of registers used per thread.
        readonly int _registerCountPerMatch;
        // Number of quantifiers in the regexp.
        readonly int _quantifierCount;
        readonly char[] _input;
        int _inputIndex;
        // Global clock counting the total of executed instructions.
        ulong _clock;
        // pc_last_input_index_[k] records the values of input_index_ the last
        // time a thread t such that t.pc == k was activated for both values of
        // consumed_since_last_quantifier.
        readonly LastInputIndex[] _pcLastInputIndex;
        // Active threads can potentially (but not necessarily) continue without
        // input.  Sorted from low to high priority.
        readonly List<InterpreterThread> _activeThreads = [];
        // The pc of a blocked thread points to an instruction that consumes a
        // character. Sorted from high to low priority (so the opposite of
        // `active_threads_`).
        readonly List<InterpreterThread> _blockedThreads = [];

        readonly ArrayPool<int> _registerArrayAllocator;
        readonly ArrayPool<int>? _lookaroundMatchIndexArrayAllocator;
        readonly ArrayPool<ulong>? _lookaroundClockArrayAllocator;
        readonly ArrayPool<ulong>? _quantifierArrayAllocator;
        readonly ArrayPool<ulong>? _captureClockArrayAllocator;

        InterpreterThread? _bestMatchThread;

        // Stores the match pc, capture pc and direction of each lookaround,
        // mapped by lookaround id. It also serves as a priority list: the
        // compilation ensures that a lookaround's bytecode appears before the
        // bytecode of all the lookarounds it contains.
        readonly List<LookaroundInfo> _lookarounds = [];

        // Truth table for the lookarounds. lookaround_table_[l][r] indicates
        // whether the lookaround of index l did complete a match on the position
        // r. Only used when `only_captureless_lookbehinds_` is false.
        readonly List<bool[]>? _lookaroundTable;

        // Truth table for the lookbehinds. lookbehind_table_[k] indicates whether
        // the lookbehind of index k did complete a match on the current position.
        // Only used when `only_captureless_lookbehinds_` is true.
        readonly bool[]? _lookbehindTable;

        // This indicates whether the regexp only contains captureless lookbehinds.
        readonly bool _onlyCapturelessLookbehinds = true;

        // Whether we are traversing the input from end to start.
        bool _reverse;

        // When computing the `lookaround_table_`, the id of the lookaround
        // currently being ran.
        int _currentLookaround = -1;

        // PC of the first FILTER_* instruction.
        readonly int? _filterGroupsPc;

        readonly ulong _memoryConsumptionPerThread;

        static bool CaptureGroupOpt => ExperimentalCompiler.s_experimentalRegExpEngineCaptureGroupOpt;

        public NfaInterpreter(Instruction[] bytecode, int registerCountPerMatch, char[] input, int inputIndex)
        {
            _bytecode = bytecode;
            _registerCountPerMatch = registerCountPerMatch;
            _input = input;
            _inputIndex = inputIndex;
            // V8 sizes this by the bytecode's byte length (an over-allocation).
            _pcLastInputIndex = new LastInputIndex[bytecode.Length];
            _registerArrayAllocator = new ArrayPool<int>(registerCountPerMatch);

            Debug.Assert(bytecode.Length != 0);
            Debug.Assert(_inputIndex >= 0);
            Debug.Assert(_inputIndex <= _input.Length);

            // Iterate over the bytecode to find the PC of the filtering
            // instructions and lookarounds, and the number of quantifiers.
            LookaroundInfo? lookaround = null;
            bool inLookaround = false;
            int lookaroundIndex = 0;
            for (int i = 0; i < _bytecode.Length - 1; ++i)
            {
                Instruction inst = _bytecode[i];

                if (inst.opcode == Instruction.Opcode.START_LOOKAROUND)
                {
                    Debug.Assert(lookaround is null);
                    inLookaround = true;

                    // Stores the partial information for a lookaround. The rest will be
                    // determined upon reaching a `WRITE_LOOKAROUND_TABLE` instruction.
                    lookaroundIndex = inst.Lookaround.Index;
                    lookaround = new LookaroundInfo { MatchPc = i, CapturePc = -1, Type = inst.Lookaround.Type };

                    if (inst.Lookaround.Type == RegExpLookaround.Type.LOOKAHEAD) _onlyCapturelessLookbehinds = false;
                }

                if (inst.opcode == Instruction.Opcode.SET_REGISTER_TO_CP && inLookaround)
                {
                    _onlyCapturelessLookbehinds = false;
                }

                if (inst.opcode == Instruction.Opcode.WRITE_LOOKAROUND_TABLE)
                {
                    Debug.Assert(lookaround is not null);

                    // Fills the current lookaround data.
                    LookaroundInfo info = lookaround!.Value;
                    info.CapturePc = i + 1;

                    // Since the lookarounds are not in order in the `lookarounds_` array,
                    // we first fill it until it has the correct size.
                    while (_lookarounds.Count <= lookaroundIndex)
                    {
                        _lookarounds.Add(new LookaroundInfo
                            { MatchPc = -1, CapturePc = -1, Type = RegExpLookaround.Type.LOOKBEHIND });
                    }
                    _lookarounds[lookaroundIndex] = info;
                    lookaround = null;
                }

                if (inst.opcode == Instruction.Opcode.END_LOOKAROUND) inLookaround = false;

                // The first `FILTER_*` instruction encountered is the start of the
                // `FILTER_*` section.
                if (_filterGroupsPc is null && Instruction.IsFilter(inst))
                {
                    Debug.Assert(CaptureGroupOpt);
                    _filterGroupsPc = i;
                }

                if (inst.opcode == Instruction.Opcode.SET_QUANTIFIER_TO_CLOCK)
                {
                    Debug.Assert(CaptureGroupOpt);
                    _quantifierCount = Math.Max(_quantifierCount, inst.QuantifierId + 1);
                }
            }

            // Iniitializes the lookaround truth table and required allocators.
            if (_onlyCapturelessLookbehinds)
            {
                _lookbehindTable = new bool[_lookarounds.Count];
            }
            else
            {
                Debug.Assert(CaptureGroupOpt);

                _lookaroundTable = new List<bool[]>(_lookarounds.Count);
                for (int i = _lookarounds.Count - 1; i >= 0; --i) _lookaroundTable.Add(new bool[_input.Length + 1]);

                _lookaroundClockArrayAllocator = new ArrayPool<ulong>(_lookaroundTable.Count);
                _lookaroundMatchIndexArrayAllocator = new ArrayPool<int>(_lookaroundTable.Count);
            }

            // Precomputes the memory consumption of a single thread, to be used by
            // `CheckMemoryConsumption()`.
            if (CaptureGroupOpt)
            {
                _quantifierArrayAllocator = new ArrayPool<ulong>(_quantifierCount);
                _captureClockArrayAllocator = new ArrayPool<ulong>(_registerCountPerMatch);

                // sizeof(InterpreterThread) in V8 is 48 bytes on 64-bit targets.
                const int kSizeOfInterpreterThread = 48;
                _memoryConsumptionPerThread =
                    (ulong)(_registerCountPerMatch * sizeof(int) +  // RegisterArray
                            _quantifierCount * sizeof(ulong) +  // QuantifierClockArray
                            _registerCountPerMatch * sizeof(ulong) +  // CaptureClockArray
                            _lookarounds.Count * sizeof(ulong) +  // LookaroundClockArray
                            _lookarounds.Count * sizeof(int) +  // LookaroundMatchIndexArray
                            kSizeOfInterpreterThread);
            }

            Array.Fill(_pcLastInputIndex, LastInputIndex.Initial);
        }

        // Finds matches and writes their concatenated capture registers to
        // `output_registers`.  The search continues until all remaining matches
        // have been found or there is no space left in `output_registers`.  Returns
        // the number of matches found.
        public int FindMatches(Span<int> outputRegisters)
        {
            int maxMatchNum = outputRegisters.Length / _registerCountPerMatch;

            if (!_onlyCapturelessLookbehinds)
            {
                int errCode = FillLookaroundTable();
                if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;
            }

            int matchNum = 0;
            int outputOffset = 0;
            while (matchNum != maxMatchNum)
            {
                int errCode = FindNextMatch();
                if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;

                if (!FoundMatch()) break;

                errCode = GetFilteredRegisters(_bestMatchThread!.Value, out int[] registers);
                if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;

                registers.AsSpan(0, _registerCountPerMatch).CopyTo(outputRegisters.Slice(outputOffset));
                outputOffset += _registerCountPerMatch;

                ++matchNum;

                int matchBegin = registers[0];
                int matchEnd = registers[1];
                Debug.Assert(matchBegin <= matchEnd);
                int matchLength = matchEnd - matchBegin;
                if (matchLength != 0)
                {
                    SetInputIndex(matchEnd);
                }
                else if (matchEnd == _input.Length)
                {
                    // Zero-length match, input exhausted.
                    SetInputIndex(matchEnd);
                    break;
                }
                else
                {
                    // Zero-length match, more input.  We don't want to report more matches
                    // here endlessly, so we advance by 1.
                    SetInputIndex(matchEnd + 1);

                    // TODO(mbid,v8:10765): If we're in unicode mode, we have to advance to
                    // the next codepoint, not to the next code unit. See also
                    // `Utils::AdvanceStringIndex`.
                }
            }

            return matchNum;
        }

        void DestroyAllThreads()
        {
            // Clean up left-over data from last iteration.
            foreach (InterpreterThread t in _blockedThreads) DestroyThread(t);
            _blockedThreads.Clear();

            foreach (InterpreterThread t in _activeThreads) DestroyThread(t);
            _activeThreads.Clear();
        }

        int FillLookaroundTable()
        {
            Debug.Assert(CaptureGroupOpt);
            Debug.Assert(!_onlyCapturelessLookbehinds);

            if (_lookarounds.Count == 0) return RegExpResult.kInternalRegExpSuccess;

            Array.Fill(_pcLastInputIndex, LastInputIndex.Initial);

            int oldInputIndex = _inputIndex;

            for (int i = _lookarounds.Count - 1; i >= 0; --i)
            {
                // Clean up left-over data from last iteration.
                DestroyAllThreads();

                _currentLookaround = i;
                _reverse = _lookarounds[i].Type == RegExpLookaround.Type.LOOKAHEAD;
                _inputIndex = _reverse ? _input.Length : 0;

                _activeThreads.Add(NewEmptyThread(_lookarounds[i].MatchPc));

                int errCode = RunActiveThreadsToEnd();
                if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;
            }

            _reverse = false;
            _currentLookaround = -1;
            _inputIndex = oldInputIndex;

            return RegExpResult.kInternalRegExpSuccess;
        }

        // Update the capture groups for matched lookarounds.
        int FillLookaroundCaptures(ref InterpreterThread mainThread)
        {
            Debug.Assert(_bestMatchThread is not null);
            Debug.Assert(CaptureGroupOpt);
            Debug.Assert(!_onlyCapturelessLookbehinds);

            if (_lookarounds.Count == 0) return RegExpResult.kInternalRegExpSuccess;

            // We need to capture the lookarounds from parents to childrens, since we
            // need the index on which the lookaround was matched, and those indexes are
            // computed when the parent expression is captured.
            for (int i = 0; i < _lookarounds.Count; ++i)
            {
                if (mainThread.LookaroundMatchIndexArray![i] == kUndefinedMatchIndexValue) continue;

                LookaroundInfo lookaround = _lookarounds[i];

                Array.Fill(_pcLastInputIndex, LastInputIndex.Initial);

                // Clean up left-over data from last iteration.
                DestroyAllThreads();

                _bestMatchThread = null;

                _reverse = lookaround.Type == RegExpLookaround.Type.LOOKBEHIND;
                _inputIndex = mainThread.LookaroundMatchIndexArray[i];

                // We reuse the same thread as initial thread, to avoid having to merge
                // the new `best_match_thread_` with the previous results.
                mainThread.Pc = lookaround.CapturePc;
                mainThread.ConsumedSinceLastQuantifier = InterpreterThread.ConsumedCharacter.DidConsume;
                _activeThreads.Add(mainThread);

                int errCode = RunActiveThreadsToEnd();
                if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;

                // The lookaround has already been matched once on this position during
                // the match research.
                Debug.Assert(_bestMatchThread is not null);
                mainThread = _bestMatchThread!.Value;
            }

            return RegExpResult.kInternalRegExpSuccess;
        }

        int RunActiveThreadsToEnd()
        {
            // Run the initial thread, potentially forking new threads, until every
            // thread is blocked without further input.
            {
                int errorCode = RunActiveThreads();
                if (errorCode != RegExpResult.kInternalRegExpSuccess) return errorCode;
            }

            // We stop if one of the following conditions hold:
            // - We have exhausted the entire input.
            // - We have found a match at some point, and there are no remaining
            //   threads with higher priority than the thread that produced the match.
            //   Threads with low priority have been aborted earlier, and the remaining
            //   threads are blocked here, so the latter simply means that
            //   `blocked_threads_` is empty.
            while ((_reverse
                       ? 0 < _inputIndex && _inputIndex <= _input.Length
                       : 0 <= _inputIndex && _inputIndex < _input.Length) &&
                   !(FoundMatch() && _blockedThreads.Count == 0))
            {
                Debug.Assert(_activeThreads.Count == 0);

                if (_lookbehindTable is not null) Array.Clear(_lookbehindTable);

                if (_reverse) --_inputIndex;

                char inputChar = _input[_inputIndex];

                if (!_reverse) ++_inputIndex;

                // V8 handles interrupts here every 64 input characters (see the
                // file comment).

                // We unblock all blocked_threads_ by feeding them the input char.
                FlushBlockedThreads(inputChar);

                // Run all threads until they block or accept.
                {
                    int errorCode = RunActiveThreads();
                    if (errorCode != RegExpResult.kInternalRegExpSuccess) return errorCode;
                }
            }

            return RegExpResult.kInternalRegExpSuccess;
        }

        // Change the current input index for future calls to `FindNextMatch`.
        void SetInputIndex(int newInputIndex)
        {
            Debug.Assert(newInputIndex >= 0);
            Debug.Assert(newInputIndex <= _input.Length);

            _inputIndex = newInputIndex;
        }

        // Find the next match and return the corresponding capture registers and
        // write its capture registers to `best_match_thread_`.  The search starts
        // at the current `input_index_`.
        int FindNextMatch()
        {
            Debug.Assert(_activeThreads.Count == 0);
            Array.Fill(_pcLastInputIndex, LastInputIndex.Initial);

            // Clean up left-over data from a previous call to FindNextMatch.
            DestroyAllThreads();

            if (_bestMatchThread is not null)
            {
                DestroyThread(_bestMatchThread.Value);
                _bestMatchThread = null;
            }

            _activeThreads.Add(NewEmptyThread(0));

            if (_onlyCapturelessLookbehinds)
            {
                for (int i = 0; i < _lookarounds.Count; ++i) _activeThreads.Add(NewEmptyThread(_lookarounds[i].MatchPc));
            }

            return RunActiveThreadsToEnd();
        }

        // Run an active thread `t` until it executes a CONSUME_RANGE or ACCEPT
        // or RANGE_COUNT instruction, or its PC value was already processed.
        // - If processing of `t` can't continue because of CONSUME_RANGE or
        //   RANGE_COUNT, it is pushed on `blocked_threads_`.
        // - If `t` executes ACCEPT, set `best_match` according to `t.match_begin` and
        //   the current input index. All remaining `active_threads_` are discarded.
        int RunActiveThread(InterpreterThread t)
        {
            while (true)
            {
                if ((uint)t.Pc >= (uint)_bytecode.Length) throw new InvalidOperationException("SBXCHECK failed");

                ++_clock;

                if (IsPcProcessed(t.Pc, t.ConsumedSinceLastQuantifier))
                {
                    DestroyThread(t);
                    return RegExpResult.kInternalRegExpSuccess;
                }
                MarkPcProcessed(t.Pc, t.ConsumedSinceLastQuantifier);

                Instruction inst = _bytecode[t.Pc];

                switch (inst.opcode)
                {
                    case Instruction.Opcode.CONSUME_RANGE:
                    case Instruction.Opcode.RANGE_COUNT:
                        _blockedThreads.Add(t);
                        return RegExpResult.kInternalRegExpSuccess;
                    case Instruction.Opcode.ASSERTION:
                        if (!SatisfiesAssertion(inst.AssertionType, _input, _inputIndex))
                        {
                            DestroyThread(t);
                            return RegExpResult.kInternalRegExpSuccess;
                        }
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.FORK:
                    {
                        InterpreterThread fork = NewUninitializedThread(inst.Pc);
                        fork.ConsumedSinceLastQuantifier = t.ConsumedSinceLastQuantifier;

                        t.RegisterArray.AsSpan(0, _registerCountPerMatch).CopyTo(fork.RegisterArray);

                        if (CaptureGroupOpt)
                        {
                            t.QuantifierClockArray!.AsSpan(0, _quantifierCount).CopyTo(fork.QuantifierClockArray);
                            t.CapturesClockArray!.AsSpan(0, _registerCountPerMatch).CopyTo(fork.CapturesClockArray);

                            if (!_onlyCapturelessLookbehinds)
                            {
                                t.LookaroundMatchIndexArray!.AsSpan(0, _lookaroundTable!.Count)
                                    .CopyTo(fork.LookaroundMatchIndexArray);
                                t.LookaroundClockArray!.AsSpan(0, _lookaroundTable.Count)
                                    .CopyTo(fork.LookaroundClockArray);
                            }
                        }

                        _activeThreads.Add(fork);

                        if (CaptureGroupOpt)
                        {
                            int errCode = CheckMemoryConsumption();
                            if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;
                        }

                        ++t.Pc;
                        break;
                    }
                    case Instruction.Opcode.JMP:
                        t.Pc = inst.Pc;
                        break;
                    case Instruction.Opcode.ACCEPT:
                    case Instruction.Opcode.END_LOOKAROUND:
                        if (_bestMatchThread is not null) DestroyThread(_bestMatchThread.Value);
                        _bestMatchThread = t;

                        foreach (InterpreterThread s in _activeThreads) DestroyThread(s);
                        _activeThreads.Clear();
                        return RegExpResult.kInternalRegExpSuccess;
                    case Instruction.Opcode.SET_QUANTIFIER_TO_CLOCK:
                        t.QuantifierClockArray![inst.QuantifierId] = _clock;
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.CLEAR_REGISTER:
                        if ((uint)inst.RegisterIndex >= (uint)_registerCountPerMatch)
                        {
                            throw new InvalidOperationException("SBXCHECK failed");
                        }
                        t.RegisterArray[inst.RegisterIndex] = kUndefinedRegisterValue;
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.SET_REGISTER_TO_CP:
                        if ((uint)inst.RegisterIndex >= (uint)_registerCountPerMatch)
                        {
                            throw new InvalidOperationException("SBXCHECK failed");
                        }
                        t.RegisterArray[inst.RegisterIndex] = _inputIndex;
                        if (CaptureGroupOpt) t.CapturesClockArray![inst.RegisterIndex] = _clock;
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.FILTER_QUANTIFIER:
                    case Instruction.Opcode.FILTER_GROUP:
                    case Instruction.Opcode.FILTER_LOOKAROUND:
                    case Instruction.Opcode.FILTER_CHILD:
                        throw new InvalidOperationException("UNREACHABLE");
                    case Instruction.Opcode.BEGIN_LOOP:
                        t.ConsumedSinceLastQuantifier = InterpreterThread.ConsumedCharacter.DidNotConsume;
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.END_LOOP:
                        // If the thread did not consume any character during a whole
                        // quantifier iteration,then it must be destroyed, since quantifier
                        // repetitions are not allowed to match the empty string.
                        if (t.ConsumedSinceLastQuantifier == InterpreterThread.ConsumedCharacter.DidNotConsume)
                        {
                            DestroyThread(t);
                            return RegExpResult.kInternalRegExpSuccess;
                        }
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.START_LOOKAROUND:
                        ++t.Pc;
                        break;
                    case Instruction.Opcode.WRITE_LOOKAROUND_TABLE:
                        // Reaching this instruction means that the current lookaround thread
                        // has found a match and needs to be destroyed. Since the lookaround
                        // is verified at this position, we update the `lookaround_table_`.
                        if (!_onlyCapturelessLookbehinds)
                        {
                            _lookaroundTable![_currentLookaround][_inputIndex] = true;
                        }
                        else
                        {
                            _lookbehindTable![inst.LookaroundId] = true;
                        }

                        DestroyThread(t);
                        return RegExpResult.kInternalRegExpSuccess;
                    case Instruction.Opcode.READ_LOOKAROUND_TABLE:
                        // Destroy the thread if the corresponding lookaround did or did not
                        // complete a match at the current position (depending on whether or
                        // not the lookaround is positive). The lookaround priority list
                        // ensures that all the relevant lookarounds has already been run.
                        if (!_onlyCapturelessLookbehinds)
                        {
                            int index = inst.Lookaround.Index;
                            if (_lookaroundTable![index][_inputIndex] != inst.Lookaround.IsPositive)
                            {
                                DestroyThread(t);
                                return RegExpResult.kInternalRegExpSuccess;
                            }

                            // Store the match informations for positive lookarounds.
                            if (inst.Lookaround.IsPositive)
                            {
                                t.LookaroundClockArray![index] = _clock;
                                t.LookaroundMatchIndexArray![index] = _inputIndex;
                            }

                            ++t.Pc;
                            break;
                        }
                        else
                        {
                            int lookbehindIndex = inst.Lookaround.Index;
                            if (_lookbehindTable![lookbehindIndex] != inst.Lookaround.IsPositive)
                            {
                                DestroyThread(t);
                                return RegExpResult.kInternalRegExpSuccess;
                            }

                            ++t.Pc;
                            break;
                        }
                }
            }
        }

        // Run each active thread until it can't continue without further input.
        // `active_threads_` is empty afterwards.  `blocked_threads_` are sorted from
        // low to high priority.
        int RunActiveThreads()
        {
            while (_activeThreads.Count != 0)
            {
                InterpreterThread t = _activeThreads[^1];
                _activeThreads.RemoveAt(_activeThreads.Count - 1);
                int errCode = RunActiveThread(t);
                if (errCode != RegExpResult.kInternalRegExpSuccess) return errCode;
            }

            return RegExpResult.kInternalRegExpSuccess;
        }

        // Unblock all blocked_threads_ by feeding them an `input_char`.  Should only
        // be called with `input_index_` pointing to the character *after*
        // `input_char` so that `pc_last_input_index_` is updated correctly.
        void FlushBlockedThreads(char inputChar)
        {
            // The threads in blocked_threads_ are sorted from high to low priority,
            // but active_threads_ needs to be sorted from low to high priority, so we
            // need to activate blocked threads in reverse order.
            for (int i = _blockedThreads.Count - 1; i >= 0; --i)
            {
                InterpreterThread t = _blockedThreads[i];
                // Number of ranges to check.
                int ranges = 1;
                // Consume success.
                bool hasMatched = false;

                if (_bytecode[t.Pc].opcode == Instruction.Opcode.RANGE_COUNT)
                {
                    ranges = _bytecode[t.Pc].NumRanges;
                    ++t.Pc;
                }

                // pc of the instruction after all ranges.
                int nextPc = t.Pc + ranges;

                // Checking all ranges.
                for (int pc = t.Pc; pc < nextPc; ++pc)
                {
                    Instruction inst = _bytecode[pc];
                    Debug.Assert(inst.opcode == Instruction.Opcode.CONSUME_RANGE);
                    if (inputChar >= inst.ConsumeRangeMin && inputChar <= inst.ConsumeRangeMax)
                    {
                        // The current char matches the current range.
                        t.Pc = nextPc;
                        t.ConsumedSinceLastQuantifier = InterpreterThread.ConsumedCharacter.DidConsume;
                        hasMatched = true;
                        break;
                    }
                }
                if (hasMatched)
                {
                    _activeThreads.Add(t);
                }
                else
                {
                    DestroyThread(t);
                }
            }
            _blockedThreads.Clear();
        }

        bool FoundMatch() => _bestMatchThread is not null;

        // Checks that the approximative memory usage does not go past a fixed
        // threshold. Returns the appropriate error code.
        int CheckMemoryConsumption()
        {
            Debug.Assert(CaptureGroupOpt);

            // Computes an approximation of the total current memory usage of the
            // interpreter. It is based only on the threads' consumption, since the rest
            // is negligible in comparison.
            ulong approx = (ulong)(_blockedThreads.Count + _activeThreads.Count) * _memoryConsumptionPerThread;

            bool enoughMemory = approx < s_captureGroupOptMaxMemoryUsage * 1024 * 1024;
            if (enoughMemory) return RegExpResult.kInternalRegExpSuccess;

            // V8 throws a stack overflow (RangeError) in this case.
            return RegExpResult.kInternalRegExpException;
        }

        int[] NewRegisterArray(int fillValue)
        {
            int[] array = _registerArrayAllocator.Allocate();
            Array.Fill(array, fillValue);
            return array;
        }

        // Creates an `InterpreterThread` at the given pc and allocates its arrays.
        // The register array is initialized to `kUndefinedRegisterValue`. The clocks'
        // arrays are set to `nullptr` if irrelevant, or initialized to 0.
        InterpreterThread NewEmptyThread(int pc)
        {
            var t = new InterpreterThread
            {
                Pc = pc,
                RegisterArray = NewRegisterArray(kUndefinedRegisterValue),
                ConsumedSinceLastQuantifier = InterpreterThread.ConsumedCharacter.DidConsume,
            };
            if (CaptureGroupOpt)
            {
                if (!_onlyCapturelessLookbehinds)
                {
                    t.LookaroundMatchIndexArray = _lookaroundMatchIndexArrayAllocator!.Allocate();
                    Array.Fill(t.LookaroundMatchIndexArray, kUndefinedMatchIndexValue);
                    t.LookaroundClockArray = _lookaroundClockArrayAllocator!.Allocate();
                    Array.Clear(t.LookaroundClockArray);
                }
                t.QuantifierClockArray = _quantifierArrayAllocator!.Allocate();
                Array.Clear(t.QuantifierClockArray);
                t.CapturesClockArray = _captureClockArrayAllocator!.Allocate();
                Array.Clear(t.CapturesClockArray);
            }
            return t;
        }

        // Creates an `InterpreterThread` at the given pc and allocates its arrays.
        // The clocks' arrays are set to `nullptr` if irrelevant. All arrays are left
        // uninitialized.
        InterpreterThread NewUninitializedThread(int pc)
        {
            var t = new InterpreterThread
            {
                Pc = pc,
                RegisterArray = _registerArrayAllocator.Allocate(),
                ConsumedSinceLastQuantifier = InterpreterThread.ConsumedCharacter.DidConsume,
            };
            if (CaptureGroupOpt)
            {
                if (!_onlyCapturelessLookbehinds)
                {
                    t.LookaroundMatchIndexArray = _lookaroundMatchIndexArrayAllocator!.Allocate();
                    t.LookaroundClockArray = _lookaroundClockArrayAllocator!.Allocate();
                }
                t.QuantifierClockArray = _quantifierArrayAllocator!.Allocate();
                t.CapturesClockArray = _captureClockArrayAllocator!.Allocate();
            }
            return t;
        }

        int GetFilteredRegisters(InterpreterThread mainThread, out int[] filteredRegisters)
        {
            if (!_onlyCapturelessLookbehinds)
            {
                int errCode = FillLookaroundCaptures(ref mainThread);
                if (errCode != RegExpResult.kInternalRegExpSuccess)
                {
                    filteredRegisters = [];
                    return errCode;
                }
            }

            int[] registers = mainThread.RegisterArray;

            if (_filterGroupsPc is not null)
            {
                filteredRegisters = NewRegisterArray(kUndefinedRegisterValue);

                filteredRegisters[0] = registers[0];
                filteredRegisters[1] = registers[1];

                filteredRegisters = FilterGroups.Filter(_filterGroupsPc.Value, mainThread.RegisterArray,
                    mainThread.QuantifierClockArray!, mainThread.CapturesClockArray!,
                    _onlyCapturelessLookbehinds ? null : mainThread.LookaroundClockArray, filteredRegisters,
                    _bytecode);
            }
            else
            {
                filteredRegisters = registers;
            }

            return RegExpResult.kInternalRegExpSuccess;
        }

        void DestroyThread(InterpreterThread t)
        {
            _registerArrayAllocator.Deallocate(t.RegisterArray);

            if (CaptureGroupOpt)
            {
                _quantifierArrayAllocator!.Deallocate(t.QuantifierClockArray);
                _captureClockArrayAllocator!.Deallocate(t.CapturesClockArray);

                if (!_onlyCapturelessLookbehinds)
                {
                    _lookaroundClockArrayAllocator!.Deallocate(t.LookaroundClockArray);
                    _lookaroundMatchIndexArrayAllocator!.Deallocate(t.LookaroundMatchIndexArray);
                }
            }
        }

        // It is redundant to have two threads t, t0 execute at the same PC and
        // consumed_since_last_quantifier values, because one of t, t0 matches iff the
        // other does.  We can thus discard the one with lower priority.  We check
        // whether a thread executed at some PC value by recording for every possible
        // value of PC what the value of input_index_ was the last time a thread
        // executed at PC. If a thread tries to continue execution at a PC value that
        // we have seen before at the current input index, we abort it. (We execute
        // threads with higher priority first, so the second thread is guaranteed to
        // have lower priority.)
        bool IsPcProcessed(int pc, InterpreterThread.ConsumedCharacter consumedSinceLastQuantifier) =>
            consumedSinceLastQuantifier == InterpreterThread.ConsumedCharacter.DidConsume
                ? _pcLastInputIndex[pc].HavingConsumedCharacter == _inputIndex
                : _pcLastInputIndex[pc].NotHavingConsumedCharacter == _inputIndex;

        // Mark a pc as having been processed since the last increment of
        // `input_index_`.
        void MarkPcProcessed(int pc, InterpreterThread.ConsumedCharacter consumedSinceLastQuantifier)
        {
            if (consumedSinceLastQuantifier == InterpreterThread.ConsumedCharacter.DidConsume)
            {
                _pcLastInputIndex[pc].HavingConsumedCharacter = _inputIndex;
            }
            else
            {
                _pcLastInputIndex[pc].NotHavingConsumedCharacter = _inputIndex;
            }
        }
    }
}
