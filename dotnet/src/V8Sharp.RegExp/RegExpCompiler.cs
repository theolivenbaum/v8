// Port of src/regexp/regexp-compiler.cc.
//
// The Irregexp regular expression engine is structured in three steps.
//   1) The parser generates an abstract syntax tree.  See RegExpAst.cs.
//   2) From the AST a node network is created.  The nodes are all
//      subclasses of RegExpNode.  The nodes represent states when
//      executing a regular expression.  Several optimizations are
//      performed on the node network.
//   3) From the nodes we generate byte codes that can actually execute the
//      regular expression (perform the search).
// See the long comment at the top of src/regexp/regexp-compiler.cc for the
// code generation model (choice/action/matching/end nodes, the Trace and
// execution state virtualization); it applies unchanged.
//
// Differences from V8:
// - The subject is always two-byte (one_byte() is false in practice); the
//   one-byte paths are ported but only reachable when a caller compiles for a
//   Latin-1 subject.
// - ComputeQuickCheckFilters fills RegExpData fields that only the one-byte
//   JSRegExp exec builtin reads; it is not ported (it returns immediately for
//   two-byte compiles anyway).

using static V8Sharp.RegExp.CompilerConstants;
using V8Sharp.RegExp.Unicode;

namespace V8Sharp.RegExp;

internal readonly ref struct RecursionCheck
{
    readonly RegExpCompiler _compiler;
    public RecursionCheck(RegExpCompiler compiler)
    {
        _compiler = compiler;
        compiler.IncrementRecursionDepth();
    }
    public void Dispose() => _compiler.DecrementRecursionDepth();
}

/// <summary>
/// A (dynamically-sized) set of unsigned integers that behaves especially well
/// on small integers (&lt; kFirstLimit).
/// </summary>
internal sealed class DynamicBitSet
{
    const int kFirstLimit = 32;
    uint _first;
    List<int>? _remaining;

    public bool Get(int value)
    {
        if (value < kFirstLimit) return (_first & (1u << value)) != 0;
        return _remaining is not null && _remaining.Contains(value);
    }

    public bool IsEmpty => _first == 0 && (_remaining is null || _remaining.Count == 0);

    public void Set(int value)
    {
        if (value < kFirstLimit)
        {
            _first |= 1u << value;
        }
        else
        {
            _remaining ??= new List<int>(1);
            if (_remaining.Count == 0 || !_remaining.Contains(value)) _remaining.Add(value);
        }
    }
}

public sealed class CompilationResult
{
    public RegExpError Error { get; }
    public object? Code { get; }
    public int NumRegisters { get; }

    public CompilationResult(RegExpError error) => Error = error;
    public CompilationResult(object code, int registers)
    {
        Code = code;
        NumRegisters = registers;
    }

    public static CompilationResult RegExpTooBig() => new(RegExpError.TooLarge);
    public bool Succeeded => Error == RegExpError.None;
}

public sealed partial class RegExpCompiler
{
    public CompilationResult Assemble(RegExpMacroAssembler macroAssembler, RegExpNode start, int captureCount,
        string pattern)
    {
        _macroAssembler = macroAssembler;

        CompilationResult ReportError()
        {
            _macroAssembler.AbortedCodeGeneration();
            return CompilationResult.RegExpTooBig();
        }

        var workList = new List<RegExpNode>();
        _workList = workList;
        var fail = new Label();
        _macroAssembler.SetFailLabel(fail);
        if (!_macroAssembler.ProloguePushesFailLabel)
        {
            // The fail label sits at the bottom of the backtrack stack: exhausting all
            // real backtracks pops it and falls through to Fail below. We push it here,
            // before the body is emitted. Assemblers that can elide the backtrack stack
            // defer the push to their prologue instead.
            _macroAssembler.PushBacktrack(fail);
        }
        var newTrace = new Trace();
        if (start.Emit(this, newTrace).IsError)
        {
            _workList = null;
            return ReportError();
        }
        _macroAssembler.BindJumpTarget(fail);
        _macroAssembler.Fail();
        while (workList.Count != 0)
        {
            RegExpNode node = workList[^1];
            workList.RemoveAt(workList.Count - 1);
            node.OnWorkList = false;
            if (!node.Label.IsBound)
            {
                if (node.Emit(this, newTrace).IsError)
                {
                    _workList = null;
                    return ReportError();
                }
            }
        }
        if (IsRegExpTooBig)
        {
            _workList = null;
            return ReportError();
        }

        object code = _macroAssembler.GetCode(pattern, _flags);
        _workList = null;
        return new CompilationResult(code, _nextRegister);
    }
}

public sealed partial class Trace
{
    public bool MentionsReg(int reg)
    {
        for (Trace? trace = this; trace is not null; trace = trace._next)
        {
            if (trace.HasAction && trace._action!.Mentions(reg)) return true;
        }
        return false;
    }

    // Returns true if a deferred position store exists to the specified
    // register and stores the offset in the out-parameter.  Otherwise
    // returns false.
    public bool GetStoredPosition(int reg, out int cpOffset)
    {
        cpOffset = 0;
        for (Trace? trace = this; trace is not null; trace = trace._next)
        {
            if (trace.HasAction && trace._action!.Mentions(reg))
            {
                if (trace._action.Type is ActionNode.ActionType.STORE_POSITION or ActionNode.ActionType.RESTORE_POSITION)
                {
                    cpOffset = trace._next!.CpOffset;
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    int FindAffectedRegisters(DynamicBitSet affectedRegisters)
    {
        int maxRegister = RegExpCompiler.kNoRegister;
        for (Trace? trace = this; trace is not null; trace = trace._next)
        {
            ActionNode? action = trace._action;
            if (action is not null)
            {
                int to = action.RegisterTo;
                for (int i = action.RegisterFrom; i <= to; i++) affectedRegisters.Set(i);
                if (to > maxRegister) maxRegister = to;
            }
        }
        return maxRegister;
    }

    static void RestoreAffectedRegisters(RegExpMacroAssembler assembler, int maxRegister,
        DynamicBitSet registersToPop, DynamicBitSet registersToClear)
    {
        for (int reg = maxRegister; reg >= 0; reg--)
        {
            if (registersToPop.Get(reg))
            {
                assembler.PopRegister(reg);
            }
            else if (registersToClear.Get(reg))
            {
                int clearTo = reg;
                while (reg > 0 && registersToClear.Get(reg - 1)) reg--;
                assembler.ClearRegisters(reg, clearTo);
            }
        }
    }

    // Scans back through the deferred actions to find, for a given register, what
    // needs to be done to effectuate the deferred actions.  Also tells us what
    // needs to be undone on backtrack.
    static void ScanDeferredActions(Trace top, int reg, ref RegisterFlushInfo info)
    {
        // The chronologically first deferred action in the trace
        // is used to infer the action needed to restore a register
        // to its previous state (or not, if it's safe to ignore it).

        // This is a little tricky because we are scanning the actions in reverse
        // historical order (newest first).
        for (Trace? trace = top; trace is not null; trace = trace._next)
        {
            ActionNode? action = trace._action;
            if (action is null) continue;
            if (action.Mentions(reg))
            {
                switch (action.Type)
                {
                    case ActionNode.ActionType.SET_REGISTER_FOR_LOOP:
                        if (!info.Absolute)
                        {
                            info.Value += action.Value;
                            info.Absolute = true;
                        }
                        // SET_REGISTER_FOR_LOOP is only used for newly introduced loop
                        // counters. They can have a significant previous value if they
                        // occur in a loop. TODO(lrn): Propagate this information, so
                        // we can set undo_action to IGNORE if we know there is no value to
                        // restore.
                        info.UndoAction = DeferredActionUndoType.RESTORE;
                        Debug.Assert(info.StorePosition == kNoStore);
                        Debug.Assert(!info.Clear);
                        break;
                    case ActionNode.ActionType.INCREMENT_REGISTER:
                        if (!info.Absolute) info.Value++;
                        Debug.Assert(info.StorePosition == kNoStore);
                        Debug.Assert(!info.Clear);
                        info.UndoAction = DeferredActionUndoType.RESTORE;
                        break;
                    case ActionNode.ActionType.STORE_POSITION:
                    case ActionNode.ActionType.RESTORE_POSITION:
                        if (!info.Clear && info.StorePosition == kNoStore)
                        {
                            info.StorePosition = trace._next!.CpOffset;
                        }

                        // For captures we know that stores and clears alternate.
                        // Other register, are never cleared, and if the occur
                        // inside a loop, they might be assigned more than once.
                        if (reg <= 1)
                        {
                            // Registers zero and one, aka "capture zero", is
                            // always set correctly if we succeed. There is no
                            // need to undo a setting on backtrack, because we
                            // will set it again or fail.
                            info.UndoAction = DeferredActionUndoType.IGNORE;
                        }
                        else
                        {
                            info.UndoAction = action.Type == ActionNode.ActionType.STORE_POSITION
                                ? DeferredActionUndoType.CLEAR
                                : DeferredActionUndoType.RESTORE;
                        }
                        Debug.Assert(!info.Absolute);
                        Debug.Assert(info.Value == 0);
                        break;
                    case ActionNode.ActionType.CLEAR_CAPTURES:
                        // Since we're scanning in reverse order, if we've already
                        // set the position we have to ignore historically earlier
                        // clearing operations.
                        if (info.StorePosition == kNoStore) info.Clear = true;
                        info.UndoAction = DeferredActionUndoType.RESTORE;
                        Debug.Assert(!info.Absolute);
                        Debug.Assert(info.Value == 0);
                        break;
                    default:
                        throw new InvalidOperationException("UNREACHABLE");
                }
            }
        }
    }

    void PerformDeferredActions(RegExpMacroAssembler assembler, int maxRegister, DynamicBitSet affectedRegisters,
        DynamicBitSet registersToPop, DynamicBitSet registersToClear)
    {
        // Count pushes performed to force a stack limit check occasionally.
        int pushes = 0;

        for (int reg = 0; reg <= maxRegister; reg++)
        {
            if (!affectedRegisters.Get(reg)) continue;

            var info = new RegisterFlushInfo();
            ScanDeferredActions(this, reg, ref info);

            // Prepare for the undo-action (e.g., push if it's going to be popped).
            if (info.UndoAction == DeferredActionUndoType.RESTORE)
            {
                pushes++;
                RegExpMacroAssembler.StackCheckFlag stackCheck = RegExpMacroAssembler.StackCheckFlag.kNoStackLimitCheck;
                Debug.Assert(assembler.StackLimitSlackSlotCount > 0);
                if (pushes == assembler.StackLimitSlackSlotCount)
                {
                    stackCheck = RegExpMacroAssembler.StackCheckFlag.kCheckStackLimit;
                    pushes = 0;
                }

                assembler.PushRegister(reg, stackCheck);
                registersToPop.Set(reg);
            }
            else if (info.UndoAction == DeferredActionUndoType.CLEAR)
            {
                registersToClear.Set(reg);
            }
            // Perform the chronologically last action (or accumulated increment)
            // for the register.
            if (info.StorePosition != kNoStore)
            {
                assembler.WriteCurrentPositionToRegister(reg, info.StorePosition);
            }
            else if (info.Clear)
            {
                assembler.ClearRegisters(reg, reg);
            }
            else if (info.Absolute)
            {
                assembler.SetRegister(reg, info.Value);
            }
            else if (info.Value != 0)
            {
                assembler.AdvanceRegister(reg, info.Value);
            }
        }
    }

    // This is called as we come into a loop choice node and some other tricky
    // nodes.  It normalizes the state of the code generator to ensure we can
    // generate generic code.  If the mode indicates that we are in a success
    // situation then don't push anything, because the stack is about to be
    // discarded, and also don't update the current position.
    public EmitResult Flush(RegExpCompiler compiler, RegExpNode successor, FlushMode mode = FlushMode.kFlushFull)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;

        Debug.Assert(!IsTrivial);

        // Normally we don't need to update the current position register if we are
        // about to stop because we had a successful match, but the global mode
        // requires the current position register to be updated so it can start the
        // next match.  TODO(erikcorry): Perhaps it should use capture register 1
        // instead.
        bool updateCurrentPosition = _cpOffset != 0 && (mode != FlushMode.kFlushSuccess || assembler.Global);

        if (!HasAnyActions && (Backtrack is null || mode == FlushMode.kFlushSuccess))
        {
            // Here we just have some deferred cp advances to fix and we are back to
            // a normal situation.  We may also have to forget some information gained
            // through a quick check that was already performed.
            if (updateCurrentPosition) assembler.AdvanceCurrentPosition(_cpOffset);
            // Create a new trivial state and generate the node with that.
            var newState = new Trace();
            return successor.Emit(compiler, newState);
        }

        // Generate deferred actions here along with code to undo them again.
        var affectedRegisters = new DynamicBitSet();

        // Skip the per-flush bt-stack frame when the successor is an atomic
        // LoopChoice (see AtomicLoopKind): retreating before re-entering
        // the outer scan cannot turn a failed continuation into a match, and the
        // standard restore would make the outer retry O(n^2) on long runs.
        //
        // Two levels of skip:
        //   skip_position_save: drop PushCurrentPosition + PopCurrentPosition.
        //   skip_undo_frame: additionally drop PushBacktrack(&undo) and the undo
        //     body (requires all-IGNORE register undo).  With no frame pushed,
        //     the bt-stack dispatch would pop an outer frame too early, so the
        //     body's failures must GoTo the outer backtrack statically.
        //
        // Besides a single-code-unit body this requires a parked-position grant
        // strong enough for the pending cp advance and the loop kind (see the
        // ParkedGrant levels in regexp-nodes.h).  The gate below mirrors that:
        // kAtEnd/kDisjoint skip at cp_offset_ == 0 under any grant and with a pending
        // prefix only under a uniform-prefix grant; kBoundary skips only under a
        // materialized nonempty uniform prefix.
        LoopChoiceNode? drainUselessLoop = null;
        if (mode == FlushMode.kFlushFull && ParkedGrant != ParkedGrant.kNone)
        {
            Debug.Assert(Backtrack is not null);
            // Parking is only sound for a single-code-unit body (see
            // ChooseFixedLengthLoopDrainMode); a wider body could skip a matching start
            // position.
            LoopChoiceNode? loop = successor.AsLoopChoiceNode();
            if (loop is not null && loop.FixedLengthBodyIterationLength() == 1)
            {
                bool uniformPrefix = ParkedGrant is ParkedGrant.kParkedUniformPrefix or
                    ParkedGrant.kParkedNonEmptyUniformPrefix;
                switch (loop.GetAtomicLoopKind())
                {
                    case AtomicLoopKind.kNone:
                        break;
                    case AtomicLoopKind.kAtEnd:
                    case AtomicLoopKind.kTotal:
                    case AtomicLoopKind.kDisjoint:
                        if (_cpOffset == 0 || (_cpOffset > 0 && uniformPrefix)) drainUselessLoop = loop;
                        break;
                    case AtomicLoopKind.kBoundary:
                        if (_cpOffset > 0 && uniformPrefix) drainUselessLoop = loop;
                        break;
                }
            }
        }
        bool skipPositionSave = drainUselessLoop is not null;

        if (!skipPositionSave && Backtrack is not null && mode != FlushMode.kFlushSuccess)
        {
            // Here we have a concrete backtrack location.  These are set up by choice
            // nodes and so they indicate that we have a deferred save of the current
            // position which we may need to emit here.
            assembler.PushCurrentPosition();
        }

        int maxRegister = FindAffectedRegisters(affectedRegisters);

        // Decide whether the entire undo frame can be skipped.  Only IGNORE-only
        // undo qualifies: a RESTORE or CLEAR would need a pop or ClearRegisters in
        // the undo body we are dropping.  KeepRecursing gates the inline emit that
        // replaces the AddWork+GoTo fallback.
        bool skipUndoFrame = false;
        if (drainUselessLoop is not null && drainUselessLoop.KeepRecursing(compiler))
        {
            skipUndoFrame = true;
            for (int reg = 0; reg <= maxRegister; ++reg)
            {
                if (!affectedRegisters.Get(reg)) continue;
                var info = new RegisterFlushInfo();
                ScanDeferredActions(this, reg, ref info);
                if (info.UndoAction != DeferredActionUndoType.IGNORE)
                {
                    skipUndoFrame = false;
                    break;
                }
            }
        }

        var registersToPop = new DynamicBitSet();
        var registersToClear = new DynamicBitSet();
        PerformDeferredActions(assembler, maxRegister, affectedRegisters, registersToPop, registersToClear);
        if (updateCurrentPosition) assembler.AdvanceCurrentPosition(_cpOffset);

        if (mode == FlushMode.kFlushSuccess)
        {
            var newState = new Trace();
            return successor.Emit(compiler, newState);
        }

        if (skipUndoFrame)
        {
            // All-IGNORE undo (pre-scanned above), so PerformDeferredActions pushed
            // no registers and the undo body would be empty: skip PushBacktrack and
            // the undo body; the body's failures GoTo the outer backtrack directly.
            // Call ChoiceNode::Emit to bypass LoopChoiceNode::Emit's
            // non-trivial-trace Flush check (no second frame around this body).
            Debug.Assert(registersToPop.IsEmpty);
            Debug.Assert(registersToClear.IsEmpty);
            var newState = new Trace();
            newState.SetBacktrack(Backtrack);
            // Same target, so our grant transfers.  Materializing a nonempty prefix
            // (cp_offset_ > 0, admitted above only under a uniform-prefix grant)
            // upgrades it to kParkedNonEmptyUniformPrefix; see that enumerator.
            newState.SetParkedGrant(_cpOffset > 0 ? ParkedGrant.kParkedNonEmptyUniformPrefix : ParkedGrant);
            return drainUselessLoop!.EmitAsChoiceNode(compiler, newState);
        }

        // Create a new trivial state and generate the node with that.
        var undo = new Label();
        assembler.PushBacktrack(undo);
        if (successor.KeepRecursing(compiler))
        {
            var newState = new Trace();
            EmitResult r = successor.Emit(compiler, newState);
            if (r.IsError) return r;
        }
        else
        {
            compiler.AddWork(successor);
            assembler.GoTo(successor.Label);
        }

        // On backtrack we need to restore state.
        assembler.BindJumpTarget(undo);
        RestoreAffectedRegisters(assembler, maxRegister, registersToPop, registersToClear);
        if (Backtrack is null)
        {
            assembler.Backtrack();
        }
        else
        {
            if (!skipPositionSave) assembler.PopCurrentPosition();
            assembler.GoTo(Backtrack);
        }
        return EmitResult.Success();
    }

    public void InvalidateCurrentCharacter() => _charactersPreloaded = 0;

    public EmitResult AdvanceCurrentPositionInTrace(int by, RegExpCompiler compiler)
    {
        // We don't have an instruction for shifting the current character register
        // down or for using a shifted value for anything so lets just forget that
        // we preloaded any characters into it.
        _charactersPreloaded = 0;
        // Adjust the offsets of the quick check performed information.  This
        // information is used to find out what we already determined about the
        // characters by means of mask and compare.
        _quickCheckPerformed.Advance(by, compiler.OneByte);
        _cpOffset += by;
        _boundCheckedUpTo = Math.Max(0, _boundCheckedUpTo - by);
        if (Math.Abs(_cpOffset) + kCPOffsetSlack > RegExpMacroAssembler.kMaxCPOffset)
        {
            compiler.SetRegExpTooBig();
            _cpOffset = 0;
            return EmitResult.Error();
        }
        return EmitResult.Success();
    }
}

public sealed partial class NegativeSubmatchSuccess
{
    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;

        // Omit flushing the trace. We discard the entire stack frame anyway.

        if (!Label.IsBound)
        {
            // We are completely independent of the trace, since we ignore it,
            // so this code can be used as the generic version.
            assembler.Bind(Label);
        }

        // Throw away everything on the backtrack stack since the start
        // of the negative submatch and restore the character position.
        assembler.ReadCurrentPositionFromRegister(_currentPositionRegister);
        assembler.ReadStackPointerFromRegister(_stackPointerRegister);
        if (_clearCaptureCount > 0)
        {
            // Clear any captures that might have been performed during the success
            // of the body of the negative look-ahead.
            int clearCaptureEnd = _clearCaptureStart + _clearCaptureCount - 1;
            assembler.ClearRegisters(_clearCaptureStart, clearCaptureEnd);
        }
        // Now that we have unwound the stack we find at the top of the stack the
        // backtrack that the BeginNegativeSubmatch node got.
        assembler.Backtrack();

        return EmitResult.Success();
    }
}

public partial class EndNode
{
    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        if (_action == Action.BACKTRACK)
        {
            // This node always backtracks, and we can do that immediately without
            // flushing first.  In practice many of these nodes have been eliminated
            // already during the ToNode phase, so it should not happen often.
            if (trace.IsTrivial && !Label.IsBound)
            {
                // We only need this if the node was pushed on the work list with
                // AddWork, which can only happen with a stack overflow (KeepRecursing
                // returns false).
                assembler.Bind(Label);
            }
            assembler.GoTo(trace.Backtrack);
            return EmitResult.Success();
        }
        // The BACKTRACK case was handled above the NEGATIVE_SUBMATCH_SUCCESS is
        // handled in a different virtual method.
        if (_action != Action.ACCEPT) throw new InvalidOperationException("CHECK failed");
        if (!trace.IsTrivial) return trace.Flush(compiler, this, Trace.FlushMode.kFlushSuccess);
        if (!Label.IsBound) assembler.Bind(Label);
        assembler.Succeed();
        return EmitResult.Success();
    }

    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget)
    {
        details.SetCannotMatchFrom(charactersFilledIn);
    }
}

public partial class ChoiceNode
{
    void GenerateGuard(RegExpMacroAssembler macroAssembler, Guard guard, Trace trace)
    {
        switch (guard.Op)
        {
            case Guard.Relation.LT:
                Debug.Assert(!trace.MentionsReg(guard.Reg));
                macroAssembler.IfRegisterGE(guard.Reg, guard.Value, trace.Backtrack);
                break;
            case Guard.Relation.GEQ:
                Debug.Assert(!trace.MentionsReg(guard.Reg));
                macroAssembler.IfRegisterLT(guard.Reg, guard.Value, trace.Backtrack);
                break;
        }
    }
}

internal static partial class RegExpCompilerHelpers
{
    public static bool ShortCutEmitCharacterPair(RegExpMacroAssembler macroAssembler, bool oneByte, int c1, int c2,
        Label? onFailure)
    {
        uint charMask = CharMask(oneByte);
        int exor = c1 ^ c2;
        // Check whether exor has only one bit set.
        if (((exor - 1) & exor) == 0)
        {
            // If c1 and c2 differ only by one bit.
            // Ecma262UnCanonicalize always gives the highest number last.
            Debug.Assert(c2 > c1);
            uint mask = charMask ^ (uint)exor;
            macroAssembler.CheckNotCharacterAfterAnd((uint)c1, mask & 0xffff, onFailure);
            return true;
        }
        Debug.Assert(c2 > c1);
        int diff = c2 - c1;
        if (((diff - 1) & diff) == 0 && c1 >= diff)
        {
            // If the characters differ by 2^n but don't differ by one bit then
            // subtract the difference from the found character, then do the or
            // trick.  We avoid the theoretical case where negative numbers are
            // involved in order to simplify code generation.
            uint mask = charMask ^ (uint)diff;
            macroAssembler.CheckNotCharacterAfterMinusAnd((char)(c1 - diff), (char)diff, (char)mask, onFailure);
            return true;
        }
        return false;
    }

    public static void EmitBoundaryTest(RegExpMacroAssembler masm, int border, Label fallThrough,
        Label? aboveOrEqual, Label? below)
    {
        if (below != fallThrough)
        {
            masm.CheckCharacterLT((char)border, below);
            if (aboveOrEqual != fallThrough) masm.GoTo(aboveOrEqual);
        }
        else
        {
            masm.CheckCharacterGT((char)(border - 1), aboveOrEqual);
        }
    }

    public static void EmitDoubleBoundaryTest(RegExpMacroAssembler masm, int first, int last, Label fallThrough,
        Label? inRange, Label? outOfRange)
    {
        if (inRange == fallThrough)
        {
            if (first == last) masm.CheckNotCharacter((uint)first, outOfRange);
            else masm.CheckCharacterNotInRange((char)first, (char)last, outOfRange);
        }
        else
        {
            if (first == last) masm.CheckCharacter((uint)first, inRange);
            else masm.CheckCharacterInRange((char)first, (char)last, inRange);
            if (outOfRange != fallThrough) masm.GoTo(outOfRange);
        }
    }

    // even_label is for ranges[i] to ranges[i + 1] where i - start_index is even.
    // odd_label is for ranges[i] to ranges[i + 1] where i - start_index is odd.
    public static void EmitUseLookupTable(RegExpMacroAssembler masm, List<int> ranges, int startIndex, int endIndex,
        int minChar, Label fallThrough, Label? evenLabel, Label? oddLabel)
    {
        const int kSize = RegExpMacroAssembler.kTableSize;
        const int kMask = RegExpMacroAssembler.kTableMask;

        int @base = minChar & ~kMask;

        // Assert that everything is on one kTableSize page.
        for (int i = startIndex; i <= endIndex; i++) Debug.Assert((ranges[i] & ~kMask) == @base);
        Debug.Assert(startIndex == 0 || (ranges[startIndex - 1] & ~kMask) <= @base);

        byte[] templ = new byte[kSize];
        Label? onBitSet;
        Label? onBitClear;
        int bit;
        if (evenLabel == fallThrough)
        {
            onBitSet = oddLabel;
            onBitClear = evenLabel;
            bit = 1;
        }
        else
        {
            onBitSet = evenLabel;
            onBitClear = oddLabel;
            bit = 0;
        }
        for (int i = 0; i < (ranges[startIndex] & kMask) && i < kSize; i++) templ[i] = (byte)bit;
        int j = 0;
        bit ^= 1;
        for (int i = startIndex; i < endIndex; i++)
        {
            for (j = ranges[i] & kMask; j < (ranges[i + 1] & kMask); j++) templ[j] = (byte)bit;
            bit ^= 1;
        }
        for (int i = j; i < kSize; i++) templ[i] = (byte)bit;
        masm.CheckBitInTable(templ, onBitSet);
        if (onBitClear != fallThrough) masm.GoTo(onBitClear);
    }

    public static void CutOutRange(RegExpMacroAssembler masm, List<int> ranges, int startIndex, int endIndex,
        int cutIndex, Label? evenLabel, Label? oddLabel)
    {
        bool odd = ((cutIndex - startIndex) & 1) == 1;
        Label? inRangeLabel = odd ? oddLabel : evenLabel;
        var dummy = new Label();
        EmitDoubleBoundaryTest(masm, ranges[cutIndex], ranges[cutIndex + 1] - 1, dummy, inRangeLabel, dummy);
        Debug.Assert(!dummy.IsLinked);
        // Cut out the single range by rewriting the array.  This creates a new
        // range that is a merger of the two ranges on either side of the one we
        // are cutting out.  The oddity of the labels is preserved.
        for (int j = cutIndex; j > startIndex; j--) ranges[j] = ranges[j - 1];
        for (int j = cutIndex + 1; j < endIndex; j++) ranges[j] = ranges[j + 1];
    }

    // Unicode case.  Split the search space into kSize spaces that are handled
    // with recursion.
    public static void SplitSearchSpace(List<int> ranges, int startIndex, int endIndex, out int newStartIndex,
        out int newEndIndex, out int border)
    {
        const int kSize = RegExpMacroAssembler.kTableSize;
        const int kMask = RegExpMacroAssembler.kTableMask;

        int first = ranges[startIndex];
        int last = ranges[endIndex] - 1;

        newStartIndex = startIndex;
        border = (ranges[startIndex] & ~kMask) + kSize;
        while (newStartIndex < endIndex)
        {
            if (ranges[newStartIndex] > border) break;
            newStartIndex++;
        }
        // new_start_index is the index of the first edge that is beyond the
        // current kSize space.

        // For very large search spaces we do a binary chop search of the non-Latin1
        // space instead of just going to the end of the current kSize space.  The
        // heuristics are complicated a little by the fact that any 128-character
        // encoding space can be quickly tested with a table lookup, so we don't
        // wish to do binary chop search at a smaller granularity than that.  A
        // 128-character space can take up a lot of space in the ranges array if,
        // for example, we only want to match every second character (eg. the lower
        // case characters on some Unicode pages).
        int binaryChopIndex = (endIndex + startIndex) / 2;
        // The first test ensures that we get to the code that handles the Latin1
        // range with a single not-taken branch, speeding up this important
        // character range (even non-Latin1 charset-based text has spaces and
        // punctuation).
        if (border - 1 > kMaxOneByteCharCode &&  // Latin1 case.
            endIndex - startIndex > (newStartIndex - startIndex) * 2 && last - first > kSize * 2 &&
            binaryChopIndex > newStartIndex && ranges[binaryChopIndex] >= first + 2 * kSize)
        {
            int scanForwardForSectionBorder = binaryChopIndex;
            int newBorder = (ranges[binaryChopIndex] | kMask) + 1;

            while (scanForwardForSectionBorder < endIndex)
            {
                if (ranges[scanForwardForSectionBorder] > newBorder)
                {
                    newStartIndex = scanForwardForSectionBorder;
                    border = newBorder;
                    break;
                }
                scanForwardForSectionBorder++;
            }
        }

        Debug.Assert(newStartIndex > startIndex);
        newEndIndex = newStartIndex - 1;
        if (ranges[newEndIndex] == border) newEndIndex--;
        if (border >= ranges[endIndex])
        {
            border = ranges[endIndex];
            newStartIndex = endIndex;  // Won't be used.
            newEndIndex = endIndex - 1;
        }
    }

    // Gets a series of segment boundaries representing a character class.  If the
    // character is in the range between an even and an odd boundary (counting from
    // start_index) then go to even_label, otherwise go to odd_label.  We already
    // know that the character is in the range of min_char to max_char inclusive.
    // Either label can be nullptr indicating backtracking.  Either label can also
    // be equal to the fall_through label.
    public static void GenerateBranches(RegExpMacroAssembler masm, List<int> ranges, int startIndex, int endIndex,
        int minChar, int maxChar, Label fallThrough, Label? evenLabel, Label? oddLabel)
    {
        Debug.Assert(minChar <= kMaxUtf16CodeUnit);
        Debug.Assert(maxChar <= kMaxUtf16CodeUnit);

        int first = ranges[startIndex];
        int last = ranges[endIndex] - 1;

        Debug.Assert(minChar < first);

        // Just need to test if the character is before or on-or-after
        // a particular character.
        if (startIndex == endIndex)
        {
            EmitBoundaryTest(masm, first, fallThrough, evenLabel, oddLabel);
            return;
        }

        // Another almost trivial case:  There is one interval in the middle that is
        // different from the end intervals.
        if (startIndex + 1 == endIndex)
        {
            EmitDoubleBoundaryTest(masm, first, last, fallThrough, evenLabel, oddLabel);
            return;
        }

        // It's not worth using table lookup if there are very few intervals in the
        // character class.
        if (endIndex - startIndex <= 6)
        {
            // It is faster to test for individual characters, so we look for those
            // first, then try arbitrary ranges in the second round.
            const int kNoCutIndex = -1;
            int cut = kNoCutIndex;
            for (int i = startIndex; i < endIndex; i++)
            {
                if (ranges[i] == ranges[i + 1] - 1)
                {
                    cut = i;
                    break;
                }
            }
            if (cut == kNoCutIndex) cut = startIndex;
            CutOutRange(masm, ranges, startIndex, endIndex, cut, evenLabel, oddLabel);
            Debug.Assert(endIndex - startIndex >= 2);
            GenerateBranches(masm, ranges, startIndex + 1, endIndex - 1, minChar, maxChar, fallThrough, evenLabel,
                oddLabel);
            return;
        }

        // If there are a lot of intervals in the regexp, then we will use tables to
        // determine whether the character is inside or outside the character class.
        const int kBits = RegExpMacroAssembler.kTableSizeBits;

        if ((maxChar >> kBits) == (minChar >> kBits))
        {
            EmitUseLookupTable(masm, ranges, startIndex, endIndex, minChar, fallThrough, evenLabel, oddLabel);
            return;
        }

        if ((minChar >> kBits) != first >> kBits)
        {
            masm.CheckCharacterLT((char)first, oddLabel);
            GenerateBranches(masm, ranges, startIndex + 1, endIndex, first, maxChar, fallThrough, oddLabel, evenLabel);
            return;
        }

        SplitSearchSpace(ranges, startIndex, endIndex, out int newStartIndex, out int newEndIndex, out int border);

        var handleRest = new Label();
        Label? above = handleRest;
        if (border == last + 1)
        {
            // We didn't find any section that started after the limit, so everything
            // above the border is one of the terminal labels.
            above = (endIndex & 1) != (startIndex & 1) ? oddLabel : evenLabel;
            Debug.Assert(newEndIndex == endIndex - 1);
        }

        Debug.Assert(startIndex <= newEndIndex);
        Debug.Assert(newStartIndex <= endIndex);
        Debug.Assert(startIndex < newStartIndex);
        Debug.Assert(newEndIndex < endIndex);
        Debug.Assert(minChar < border - 1);
        Debug.Assert(border < maxChar);

        masm.CheckCharacterGT((char)(border - 1), above);
        var dummy = new Label();
        GenerateBranches(masm, ranges, startIndex, newEndIndex, minChar, border - 1, dummy, evenLabel, oddLabel);
        if (handleRest.IsLinked)
        {
            masm.Bind(handleRest);
            bool flip = (newStartIndex & 1) != (startIndex & 1);
            GenerateBranches(masm, ranges, newStartIndex, endIndex, border, maxChar, dummy,
                flip ? oddLabel : evenLabel, flip ? evenLabel : oddLabel);
        }
    }

    public static void EmitClassRanges(RegExpCompiler compiler, RegExpMacroAssembler macroAssembler,
        RegExpClassRanges cr, bool oneByte, Label? onFailure, int cpOffset, bool checkOffset, bool preloaded,
        QuickCheckDetails.Position? known)
    {
        List<CharacterRange> ranges = cr.Ranges;
        CharacterRange.Canonicalize(ranges);

        // Now that all processing (like case-insensitivity) is done, clamp the
        // ranges to the set of ranges that may actually occur in the subject string.
        if (oneByte) CharacterRange.ClampToOneByte(ranges);

        int rangesLength = ranges.Count;
        if (rangesLength == 0)
        {
            if (!cr.IsNegated) macroAssembler.GoTo(onFailure);
            if (checkOffset) macroAssembler.CheckPosition(cpOffset, onFailure);
            return;
        }

        int maxChar = (int)MaxCodeUnit(oneByte);
        if (rangesLength == 1 && ranges[0].IsEverything(maxChar))
        {
            if (cr.IsNegated)
            {
                macroAssembler.GoTo(onFailure);
            }
            else
            {
                // This is a common case hit by non-anchored expressions.
                if (checkOffset) macroAssembler.CheckPosition(cpOffset, onFailure);
            }
            return;
        }

        if (!preloaded) macroAssembler.LoadCurrentCharacter(cpOffset, onFailure, checkOffset);

        if (cr.IsStandard() && macroAssembler.CanOptimizeSpecialClassRanges(cr.StandardType))
        {
            macroAssembler.CheckSpecialClassRanges(cr.StandardType, onFailure);
            return;
        }

        // A class whose members differ only in a small set of bits, and which
        // exactly cover the mask equation's solutions within their span, is
        // checked with two fused operations -- (x & M) == c and a range check --
        // instead of a branch tree.  The canonical example is the four suits of
        // one card rank, {0xDCA1, 0xDCB1, 0xDCC1, 0xDCD1}: all solutions of
        // (x & 0xFF8F) == 0xDC81 within [0xDCA1, 0xDCD1] are exactly the class
        // members.
        //
        // Ranges are handled by treating the class as the set of its members: the
        // fold's correctness depends only on which values are present, not on how
        // they were spelled, so [a-c] folds like {a, b, c}.  Enumeration below is
        // O(members), so the member count is capped.
        //
        // Unlike the QuickCheck masked compare, which is only a necessary
        // pre-filter (false positives fall through to the full check), this fold
        // replaces the class check outright, so it must be exact.  Hence we walk
        // the submasks of |diff| below and only fold when every solution in the
        // span is a class member.
        do
        {
            const int kMinMembers = 3;
            const int kMaxMembers = 8;
            int lo = ranges[0].From;
            int hi = ranges[rangesLength - 1].To;
            if (hi > 0xffff) break;
            // diff = bits that vary across the class members.
            uint diff = 0;
            int memberCount = 0;
            for (int i = 0; i < rangesLength; i++)
            {
                CharacterRange r = ranges[i];
                for (int ch = r.From; ch <= r.To; ch++)
                {
                    if (++memberCount > kMaxMembers) break;
                    diff |= (uint)(ch ^ lo);
                }
            }
            if (memberCount > kMaxMembers || memberCount < kMinMembers) break;
            if (System.Numerics.BitOperations.PopCount(diff) > 4) break;
            // mask pins the non-varying bits to c.
            uint mask = CharMask(oneByte) & ~diff;
            uint c = (uint)lo & mask;
            // (x & mask) == c is necessary for membership but not sufficient: some
            // other codepoint in [lo, hi] could satisfy it too. Walk every submask
            // of diff (sub = (sub - diff) & diff enumerates them) and fold only if
            // all solutions in the span are class members.
            int solutions = 0;
            for (uint sub = 0; ; sub = (sub - diff) & diff)
            {
                uint x = c | sub;
                if (x >= lo && x <= hi) solutions++;
                if (sub == diff) break;
            }
            if (solutions != memberCount) break;
            // A passed quick check may have already established (x & M') == c' for
            // a superset mask M' of M; the mask equation then holds and only the
            // range check remains.
            bool maskKnown = known is not null && (known.Value.Mask & mask) == mask &&
                             (known.Value.Value & mask) == c;
            if (!cr.IsNegated)
            {
                if (!maskKnown) macroAssembler.CheckNotCharacterAfterAnd(c, mask, onFailure);
                macroAssembler.CheckCharacterNotInRange((char)lo, (char)hi, onFailure);
            }
            else if (maskKnown)
            {
                macroAssembler.CheckCharacterInRange((char)lo, (char)hi, onFailure);
            }
            else
            {
                var ok = new Label();
                macroAssembler.CheckNotCharacterAfterAnd(c, mask, ok);
                macroAssembler.CheckCharacterInRange((char)lo, (char)hi, onFailure);
                macroAssembler.Bind(ok);
            }
            return;
        } while (false);

        const int kMaxRangesForInlineBranchGeneration = 16;
        if (rangesLength > kMaxRangesForInlineBranchGeneration)
        {
            // For large range sets, emit a more compact instruction sequence to avoid
            // a potentially problematic increase in code size.
            // Note the flipped logic below (we check InRange if negated, NotInRange if
            // not negated); this is necessary since the method falls through on
            // failure whereas we want to fall through on success.
            if (cr.IsNegated)
            {
                if (macroAssembler.CheckCharacterInRangeArray(ranges, onFailure)) return;
            }
            else
            {
                if (macroAssembler.CheckCharacterNotInRangeArray(ranges, onFailure)) return;
            }
        }

        // Generate a flat list of range boundaries for consumption by
        // GenerateBranches. See the comment on that function for how the list should
        // be structured
        var rangeBoundaries = new List<int>(rangesLength * 2);

        bool zerothEntryIsFailure = !cr.IsNegated;

        for (int i = 0; i < rangesLength; i++)
        {
            CharacterRange range = ranges[i];
            if (range.From == 0)
            {
                Debug.Assert(i == 0);
                zerothEntryIsFailure = !zerothEntryIsFailure;
            }
            else
            {
                rangeBoundaries.Add(range.From);
            }
            // `+ 1` to convert from inclusive to exclusive `to`.
            // [from, to] == [from, to+1[.
            rangeBoundaries.Add(range.To + 1);
        }
        int endIndex = rangeBoundaries.Count - 1;
        if (rangeBoundaries[endIndex] > maxChar) endIndex--;

        var fallThrough = new Label();
        GenerateBranches(macroAssembler, rangeBoundaries, 0,  // start_index.
            endIndex, 0,  // min_char.
            maxChar, fallThrough, zerothEntryIsFailure ? fallThrough : onFailure,
            zerothEntryIsFailure ? onFailure : fallThrough);
        macroAssembler.Bind(fallThrough);
    }

    // Takes the left-most 1-bit and smears it out, setting all bits to its right.
    public static uint SmearBitsRight(uint v)
    {
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        return v;
    }

    // An approximate single-character quick check (Position::determines_perfectly
    // == false) admits many false positives: a scattered class like [+\-%&|^]
    // rationalizes to a weak common-bits mask, and merging a choice's alternatives
    // weakens it further.  When the node's own first-character set is small, reject
    // non-members exactly with a membership table instead. The table is indexed
    // modulo kTableSize (see CheckBitInTable), so in two-byte mode it still admits
    // false positives from aliasing; the body's full check backstops it.
    //
    // Returns a table whose bit is set for every first character the node can
    // match, or null when a table is not worthwhile.
    //
    // The set is recovered by a fresh position-0 FillInBMInfo walk of this node.
    // FillInBMInfo only ever over-approximates, so a nonzero result is a valid
    // superset (false positives allowed, false negatives not). The walk is fresh
    // rather than reading the shared bm_info_ because that slot holds the union
    // over sibling alternatives, not this node's own set. It runs on a non-caching
    // lookahead (set_caches_node_info) so the transient walk does not overwrite
    // that shared slot.
    public static byte[]? TryBuildFirstCharacterTable(RegExpNode node, uint mask, RegExpCompiler compiler,
        bool notAtStart)
    {
        if (node.EatsAtLeast(notAtStart) < 1) return null;

        var lookahead = new BoyerMooreLookahead(1, compiler);
        lookahead.CachesNodeInfo = false;
        node.FillInBMInfo(0, RegExpNode.kRecursionBudget, lookahead, notAtStart);

        int count = lookahead.At(0).MapCount;
        if (count == 0)
        {
            // First-character set unknown; keep the mask.
            return null;
        }

        // The mask already rejects everything outside 2^popcount(~mask) characters. A
        // table is only worth its load and heap object when it is strictly more
        // selective than that, so bail when the mask is at least as discriminating.
        //
        // Each table bit stands for (char_mask + 1) / kTableSize code units (2 in
        // one-byte mode, 512 in two-byte), since map_count() and CheckBitInTable both
        // key on character & kMask, so scale the count into code units before
        // comparing.
        uint charMask = CharMask(compiler.OneByte);
        int aliasFactor = (int)((charMask + 1) / RegExpMacroAssembler.kTableSize);
        int maskAccepts = 1 << System.Numerics.BitOperations.PopCount(~mask & charMask);
        if (count * aliasFactor >= maskAccepts) return null;

        // A single-position skip table is a membership table: one byte per character,
        // nonzero iff the character can start a match.
        byte[] table = new byte[RegExpMacroAssembler.kTableSize];
        lookahead.GetSkipTable(0, 0, table);
        return table;
    }

    // Check for [0-9A-Z_a-z].
    public static void EmitWordCheck(RegExpMacroAssembler assembler, Label? word, Label? nonWord,
        bool fallThroughOnWord)
    {
        StandardCharacterSet characterSet =
            fallThroughOnWord ? StandardCharacterSet.kWord : StandardCharacterSet.kNotWord;
        // \w and \W is supported on all platforms.
        Debug.Assert(assembler.CanOptimizeSpecialClassRanges(characterSet));
        assembler.CheckSpecialClassRanges(characterSet, fallThroughOnWord ? nonWord : word);
    }

    // Emit the code to check for a ^ in multiline mode (1-character lookbehind
    // that matches newline or the start of input).
    public static EmitResult EmitHat(RegExpCompiler compiler, RegExpNode onSuccess, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;

        // We will load the previous character into the current character register.
        var newTrace = new Trace(trace);
        newTrace.InvalidateCurrentCharacter();

        // A positive (> 0) cp_offset means we've already successfully matched a
        // non-empty-width part of the pattern, and thus cannot be at or before the
        // start of the subject string. We can thus skip both at-start and
        // bounds-checks when loading the one-character lookbehind.
        bool mayBeAtOrBeforeSubjectStringStart = newTrace.CpOffset <= 0;

        var ok = new Label();
        if (mayBeAtOrBeforeSubjectStringStart)
        {
            // The start of input counts as a newline in this context, so skip to ok if
            // we are at the start.
            assembler.CheckAtStart(newTrace.CpOffset, ok);
        }

        // If we've already checked that we are not at the start of input, it's okay
        // to load the previous character without bounds checks.
        bool canSkipBoundsCheck = !mayBeAtOrBeforeSubjectStringStart;
        assembler.LoadCurrentCharacter(newTrace.CpOffset - 1, newTrace.Backtrack, canSkipBoundsCheck);
        // Line Terminator is supported on all platforms.
        Debug.Assert(assembler.CanOptimizeSpecialClassRanges(StandardCharacterSet.kLineTerminator));
        assembler.CheckSpecialClassRanges(StandardCharacterSet.kLineTerminator, newTrace.Backtrack);
        assembler.Bind(ok);
        return onSuccess.Emit(compiler, newTrace);
    }

    public static bool DeterminedAlready(QuickCheckDetails? quickCheck, int offset)
    {
        if (quickCheck is null) return false;
        if (offset >= quickCheck.Characters) return false;
        return quickCheck.Positions(offset).DeterminesPerfectly;
    }

    public static void UpdateBoundsCheck(int index, ref int checkedUpTo)
    {
        if (index > checkedUpTo) checkedUpTo = index;
    }

    // We need to check for the following characters: 0x39C 0x3BC 0x178.
    public static bool RangeContainsLatin1Equivalents(CharacterRange range) =>
        // TODO(dcarney): this could be a lot more efficient.
        range.Contains(0x039C) || range.Contains(0x03BC) || range.Contains(0x0178);

    public static bool RangesContainLatin1Equivalents(List<CharacterRange> ranges)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            // TODO(dcarney): this could be a lot more efficient.
            if (RangeContainsLatin1Equivalents(ranges[i])) return true;
        }
        return false;
    }

    // Appends the characters |cr| matches to |out| as positive ranges; negated
    // classes are materialized as the complement of their listed ranges.  The
    // result may overapproximate, which only inhibits the optimization, never
    // unsoundly enables it.
    public static void AppendClassRangesMatchSet(RegExpClassRanges cr, List<CharacterRange> @out)
    {
        List<CharacterRange> ranges = cr.Ranges;
        if (!cr.IsNegated)
        {
            for (int i = 0; i < ranges.Count; i++) @out.Add(ranges[i]);
            return;
        }
        var positive = new List<CharacterRange>(ranges);
        CharacterRange.Canonicalize(positive);
        var negated = new List<CharacterRange>(positive.Count + 1);
        CharacterRange.Negate(positive, negated);
        for (int i = 0; i < negated.Count; i++) @out.Add(negated[i]);
    }

    // Returns true iff every character in |ranges| (canonical) is in
    // |special_class|, a [from, to+1) pair table terminated by kRangeEndMarker
    // (e.g. kWordRanges).  Each range must fit inside a single table entry.
    public static bool RangesSubsetOfSpecialClass(List<CharacterRange> ranges, int[] specialClass)
    {
        Debug.Assert(CharacterRange.IsCanonical(ranges));
        int length = specialClass.Length;
        Debug.Assert(specialClass[length - 1] == kRangeEndMarker);
        length--;  // Remove final marker.
        // |ci| walks the table entries in lockstep with |ranges| (both canonical);
        // it never rewinds because a later range starts no earlier than the current.
        int ci = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            CharacterRange range = ranges[i];
            while (ci < length && specialClass[ci + 1] <= range.From) ci += 2;
            if (ci >= length || range.From < specialClass[ci] || range.To >= specialClass[ci + 1]) return false;
        }
        return true;
    }

    // Facts about a fixed-length loop's body alternative, computed by
    // AnalyzeAtomicLoopBody in one walk of the body chain.
    public struct AtomicLoopBodyAnalysis
    {
        // The body is a fixed-length SeqNode chain returning to the loop.
        public bool FixedLengthEligible;
        // |body_set| overapproximates the characters the body can consume.  False
        // when the set is not statically known.
        public bool SetKnown;
    }

    // Appends the characters |elm| matches to |out|.  Returns false when the
    // match set is not statically known: class ranges have case equivalents
    // materialized before emission, but atoms only get theirs at emission time,
    // so atoms are rejected under ignore-case.
    public static bool AppendTextElementMatchSet(TextElement elm, RegExpFlags flags, List<CharacterRange> @out)
    {
        if (elm.Type == TextElement.TextType.CLASS_RANGES)
        {
            AppendClassRangesMatchSet(elm.ClassRanges, @out);
            return true;
        }
        Debug.Assert(elm.Type == TextElement.TextType.ATOM);
        if (flags.IsIgnoreCase()) return false;
        string data = elm.Atom.Data;
        for (int j = 0; j < data.Length; j++) @out.Add(CharacterRange.Singleton(data[j]));
        return true;
    }

    // Walks the loop body chain, collecting into |body_set| the characters it can
    // consume (see AtomicLoopBodyAnalysis).  This is exactly the set a drain retry
    // position can hold, since every such position was consumed by a body
    // iteration.
    public static AtomicLoopBodyAnalysis AnalyzeAtomicLoopBody(GuardedAlternative alt, RegExpNode loop,
        List<CharacterRange> bodySet)
    {
        var result = new AtomicLoopBodyAnalysis { SetKnown = true };
        RegExpNode node = alt.Node;
        for (int depth = 0; depth <= RegExpCompiler.kMaxRecursion; ++depth)
        {
            if (node == loop)
            {
                result.FixedLengthEligible = true;
                return result;
            }
            if (node.FixedLengthLoopLength() == RegExpNode.kNodeIsTooComplexForFixedLengthLoops) return result;
            SeqNode? seq = node.AsSeqNode();
            if (seq is null) return result;
            TextNode? text = node.AsTextNode();
            if (text is null || text.ReadBackward)
            {
                result.SetKnown = false;
            }
            else
            {
                List<TextElement> elms = text.Elements;
                for (int i = 0; i < elms.Count; i++)
                {
                    if (!AppendTextElementMatchSet(elms[i], text.Flags, bodySet)) result.SetKnown = false;
                }
            }
            node = seq.OnSuccess;
        }
        return result;
    }

    // Node-visit cap for ContinuationAlwaysSucceeds (see its |budget| comment).
    public const int kContinuationAlwaysSucceedsBudget = 1000;

    // Whether |node| has a guaranteed-success path: it reaches EndNode(ACCEPT)
    // consuming zero input and failing no assertion, so at any position it cannot
    // fail.  Then the loop's drain is dead code (see AtomicLoopKind::kTotal).
    //
    // |budget| bounds the total node visits: on a false result the walk explores
    // every path, and a re-convergent nullable subtree (/(?:a?|b?)(?:a?|b?).../)
    // is exponential in the number of forks, so it is capped and returns the
    // conservative false when spent.  Real always-succeeds continuations are short
    // nullable chains far under the cap.
    public static bool ContinuationAlwaysSucceeds(RegExpNode? node, int depth, ref int budget)
    {
        if (depth > RegExpCompiler.kMaxRecursion || node is null) return false;
        if (--budget < 0) return false;
        ActionNode? action = node.AsActionNode();
        if (action is not null)
        {
            switch (action.Type)
            {
                // These actions emit no failure branch of their own: ActionNode::Emit
                // defers them to the trace or emits a pure state write (register, flag,
                // position restore, or the no-op eats-at-least hint) and then
                // unconditionally emits on_success, so the path's outcome is whatever
                // follows.
                case ActionNode.ActionType.STORE_POSITION:
                case ActionNode.ActionType.CLEAR_CAPTURES:
                case ActionNode.ActionType.SET_REGISTER_FOR_LOOP:
                case ActionNode.ActionType.INCREMENT_REGISTER:
                case ActionNode.ActionType.EATS_AT_LEAST:
                case ActionNode.ActionType.RESTORE_POSITION:
                case ActionNode.ActionType.POSITIVE_SUBMATCH_SUCCESS:
                case ActionNode.ActionType.BEGIN_POSITIVE_SUBMATCH:
                    return ContinuationAlwaysSucceeds(action.OnSuccess, depth + 1, ref budget);
                // EMPTY_MATCH_CHECK emits a conditional backtrack, so the path can fail.
                // A negative lookaround fails whenever its inner match succeeds, which no
                // walk of a single always-succeeding path can rule out.
                case ActionNode.ActionType.EMPTY_MATCH_CHECK:
                case ActionNode.ActionType.BEGIN_NEGATIVE_SUBMATCH:
                    return false;
            }
        }
        EndNode? end = node.AsEndNode();
        if (end is not null) return end.EndAction == EndNode.Action.ACCEPT;
        // A LoopChoiceNode is a ChoiceNode too: a `*` / `{0,n}` loop reaches ACCEPT
        // through its unguarded exit (continue) alternative, while a `+` / `{m,}`
        // loop's exit carries a GEQ-min guard and is skipped below, leaving only its
        // consuming body -- correctly not nullable.
        ChoiceNode? choice = node.AsChoiceNode();
        if (choice is not null)
        {
            // A negative lookaround is not a plain disjunction (its first alternative
            // must fail); do not treat it as one.
            if (choice.AsNegativeLookaroundChoiceNode() is not null) return false;
            // The disjunction succeeds if any unguarded alternative does: a
            // lower-priority epsilon-accept alternative is still reached as a last
            // resort by the continuation's own backtracking.  Guarded alternatives
            // (e.g. a loop's GEQ-min exit guard) may be blocked, so they are skipped.
            foreach (GuardedAlternative alt in choice.Alternatives)
            {
                if (alt.Guards is not null && alt.Guards.Count != 0) continue;
                if (ContinuationAlwaysSucceeds(alt.Node, depth + 1, ref budget)) return true;
            }
            return false;
        }
        // A TextNode consumes; anything else is not provably infallible.
        return false;
    }
}

public abstract partial class RegExpNode
{
    // Emits some quick code that checks whether the preloaded characters match.
    // Falls through on certain failure, jumps to the label on possible success.
    // If the node cannot make a quick check it does nothing and returns false.
    public bool EmitQuickCheck(RegExpCompiler compiler, Trace boundsCheckTrace, Trace trace,
        bool preloadHasCheckedBounds, Label? onPossibleSuccess, QuickCheckDetails details,
        bool fallThroughOnFailure, ChoiceNode predecessor)
    {
        if (details.Characters == 0) return false;
        GetQuickCheckDetails(details, compiler, 0, trace.AtStart == Trace.TriBool.FALSE_VALUE, kRecursionBudget);
        if (details.CannotMatch()) return false;
        if (!details.Rationalize(compiler.OneByte)) return false;
        Debug.Assert(details.Characters == 1 || compiler.MacroAssembler.CanReadUnaligned());
        uint mask = details.Mask;
        uint value = details.Value;

        RegExpMacroAssembler assembler = compiler.MacroAssembler;

        if (trace.CharactersPreloaded != details.Characters)
        {
            Debug.Assert(trace.CpOffset == boundsCheckTrace.CpOffset);
            // The bounds check is performed using the minimum number of characters
            // any choice would eat, so if the bounds check fails, then none of the
            // choices can succeed, so we can just immediately backtrack, rather
            // than go to the next choice. The number of characters preloaded may be
            // less than the number used for the bounds check.
            int eatsAtLeast = (int)predecessor.EatsAtLeast(boundsCheckTrace.AtStart == Trace.TriBool.FALSE_VALUE);
            Debug.Assert(eatsAtLeast >= details.Characters);
            int cpOffset = trace.CpOffset;
            int boundsCheckOffset = assembler.CalculateBoundsCheckOffset(cpOffset, eatsAtLeast);
            assembler.LoadCurrentCharacter(cpOffset, boundsCheckTrace.Backtrack, !preloadHasCheckedBounds,
                details.Characters, boundsCheckOffset);
        }

        // Approximate single-character check: prefer an exact membership table (see
        // TryBuildFirstCharacterTable).
        if (details.Characters == 1 && !details.Positions(0).DeterminesPerfectly)
        {
            bool notAtStart = trace.AtStart == Trace.TriBool.FALSE_VALUE;
            byte[]? table = RegExpCompilerHelpers.TryBuildFirstCharacterTable(this, mask, compiler, notAtStart);
            if (table is not null)
            {
                // The table only proved that the character is in the set. It did not
                // prove the mask equation (x & mask) == value, which we never emitted.
                // Downstream code trusts a published mask/value and would skip a compare
                // on that basis, so clear both (on the position and on the Rationalize()
                // condensate) to signal that nothing is proven here; the body then
                // re-checks in full (see mask_known in EmitClassRanges).
                details.Positions(0).Mask = 0;
                details.Positions(0).Value = 0;
                details.Mask = 0;
                details.Value = 0;
                if (fallThroughOnFailure)
                {
                    assembler.CheckBitInTable(table, onPossibleSuccess);
                }
                else
                {
                    var matched = new Label();
                    assembler.CheckBitInTable(table, matched);
                    assembler.GoTo(trace.Backtrack);
                    assembler.Bind(matched);
                }
                return true;
            }
        }

        bool needMask = true;

        if (details.Characters == 1)
        {
            // If number of characters preloaded is 1 then we used a byte or 16 bit
            // load so the value is already masked down.
            uint charMask = CharMask(compiler.OneByte);
            if ((mask & charMask) == charMask) needMask = false;
            mask &= charMask;
        }
        else
        {
            // For 2-character preloads in one-byte mode or 1-character preloads in
            // two-byte mode we also use a 16 bit load with zero extend.
            const uint kTwoByteMask = 0xFFFF;
            const uint kFourByteMask = 0xFFFFFFFF;
            if (details.Characters == 2 && compiler.OneByte)
            {
                if ((mask & kTwoByteMask) == kTwoByteMask) needMask = false;
            }
            else if (details.Characters == 1 && !compiler.OneByte)
            {
                if ((mask & kTwoByteMask) == kTwoByteMask) needMask = false;
            }
            else
            {
                if (mask == kFourByteMask) needMask = false;
            }
        }

        if (fallThroughOnFailure)
        {
            if (needMask) assembler.CheckCharacterAfterAnd(value, mask, onPossibleSuccess);
            else assembler.CheckCharacter(value, onPossibleSuccess);
        }
        else
        {
            if (needMask) assembler.CheckNotCharacterAfterAnd(value, mask, trace.Backtrack);
            else assembler.CheckNotCharacter(value, trace.Backtrack);
        }
        return true;
    }

    protected LimitResult LimitVersions(RegExpCompiler compiler, Trace trace)
    {
        // If we are generating a fixed length loop then don't stop and don't reuse
        // code.
        if (trace.SpecialLoopState is not null) return LimitResult.CONTINUE;

        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        if (trace.IsTrivial)
        {
            if (_label.IsBound || OnWorkList || !KeepRecursing(compiler))
            {
                // If a generic version is already scheduled to be generated or we have
                // recursed too deeply then just generate a jump to that code.
                macroAssembler.GoTo(_label);
                // This will queue it up for generation of a generic version if it hasn't
                // already been queued.
                compiler.AddWork(this);
                return LimitResult.DONE;
            }
            // Generate generic version of the node and bind the label for later use.
            macroAssembler.Bind(_label);
            return LimitResult.CONTINUE;
        }

        // We are being asked to make a non-generic version.  Keep track of how many
        // non-generic versions we generate so as not to overdo it.
        _traceCount++;
        if (KeepRecursing(compiler) && compiler.Optimize && _traceCount < kMaxCopiesCodeGenerated)
        {
            return LimitResult.CONTINUE;
        }

        // If we get here code has been generated for this node too many times or
        // recursion is too deep.  Time to switch to a generic version.  The code for
        // generic versions above can handle deep recursion properly.
        bool wasLimiting = compiler.LimitingRecursion;
        compiler.LimitingRecursion = true;
        trace.Flush(compiler, this);
        compiler.LimitingRecursion = wasLimiting;
        return LimitResult.DONE;
    }
}

public sealed partial class QuickCheckDetails
{
    public bool Rationalize(bool asc)
    {
        bool foundUsefulOp = false;
        uint charMask = CharMask(asc);
        _mask = 0;
        _value = 0;
        int charShift = 0;
        for (int i = 0; i < _characters; i++)
        {
            ref Position pos = ref _positions[i];
            if ((pos.Mask & kMaxOneByteCharCode) != 0) foundUsefulOp = true;
            _mask |= (pos.Mask & charMask) << charShift;
            _value |= (pos.Value & charMask) << charShift;
            charShift += asc ? 8 : 16;
        }
        return foundUsefulOp;
    }

    public void Clear()
    {
        for (int i = 0; i < _characters; i++) _positions[i].Clear();
        _characters = 0;
    }

    // Advance the current position by some amount.
    public void Advance(int by, bool oneByte)
    {
        if (by >= _characters || by < 0)
        {
            Debug.Assert(by >= 0 || _characters == 0);
            Clear();
            return;
        }
        Debug.Assert(_characters - by <= 4);
        Debug.Assert(_characters <= 4);
        for (int i = 0; i < _characters - by; i++) _positions[i] = _positions[by + i];
        for (int i = _characters - by; i < _characters; i++) _positions[i].Clear();
        _characters -= by;
        // We could change mask_ and value_ here but we would never advance unless
        // they had already been used in a check and they won't be used again because
        // it would gain us nothing.  So there's no point.
    }

    // Merge in the information from another branch of an alternation.
    public void Merge(QuickCheckDetails other, int fromIndex)
    {
        Debug.Assert(_characters == other._characters);
        for (int i = fromIndex; i < _characters; i++)
        {
            ref Position pos = ref Positions(i);
            ref Position otherPos = ref other.Positions(i);
            if (pos.CannotMatch)
            {
                pos = otherPos;
            }
            else if (!otherPos.CannotMatch)
            {
                if (pos.Mask != otherPos.Mask || pos.Value != otherPos.Value || !otherPos.DeterminesPerfectly)
                {
                    // Our mask-compare operation will be approximate unless we have the
                    // exact same operation on both sides of the alternation.
                    pos.DeterminesPerfectly = false;
                }
                pos.Mask &= otherPos.Mask;
                pos.Value &= pos.Mask;
                otherPos.Value &= pos.Mask;
                uint differingBits = pos.Value ^ otherPos.Value;
                pos.Mask &= ~differingBits;
                pos.Value &= pos.Mask;
            }
        }
    }
}

public sealed partial class ActionNode
{
    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        switch (_actionType)
        {
            case ActionType.SET_REGISTER_FOR_LOOP:
            case ActionType.INCREMENT_REGISTER:
            case ActionType.STORE_POSITION:
            case ActionType.RESTORE_POSITION:
            case ActionType.BEGIN_NEGATIVE_SUBMATCH:
            case ActionType.EMPTY_MATCH_CHECK:
            case ActionType.CLEAR_CAPTURES:
            case ActionType.EATS_AT_LEAST:
                OnSuccess.FillInBMInfo(offset, budget - 1, bm, notAtStart);
                break;
            case ActionType.BEGIN_POSITIVE_SUBMATCH:
                // We use the node after the lookaround to fill in the eats_at_least info
                // so we have to use the same node to fill in the Boyer-Moore info.
                SuccessNode.OnSuccess.FillInBMInfo(offset, budget - 1, bm, notAtStart);
                break;
            case ActionType.POSITIVE_SUBMATCH_SUCCESS:
                // We don't use the node after a positive submatch success because it
                // rewinds the position. Since we returned 0 as the eats_at_least value
                // for this node, we don't need to fill in any data.
                break;
        }
        SaveBMInfo(bm, notAtStart, offset);
    }

    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler, int filledIn,
        bool notAtStart, int budget)
    {
        switch (_actionType)
        {
            case ActionType.SET_REGISTER_FOR_LOOP:
            case ActionType.INCREMENT_REGISTER:
            case ActionType.STORE_POSITION:
            case ActionType.RESTORE_POSITION:
            case ActionType.BEGIN_NEGATIVE_SUBMATCH:
            case ActionType.EMPTY_MATCH_CHECK:
            case ActionType.CLEAR_CAPTURES:
            case ActionType.EATS_AT_LEAST:
                OnSuccess.GetQuickCheckDetails(details, compiler, filledIn, notAtStart, budget - 1);
                break;
            case ActionType.BEGIN_POSITIVE_SUBMATCH:
                // We use the node after the lookaround to fill in the eats_at_least info
                // so we have to use the same node to fill in the QuickCheck info.
                SuccessNode.OnSuccess.GetQuickCheckDetails(details, compiler, filledIn, notAtStart, budget - 1);
                break;
            case ActionType.POSITIVE_SUBMATCH_SUCCESS:
                // We don't use the node after a positive submatch success because it
                // rewinds the position. Since we returned 0 as the eats_at_least value
                // for this node, we don't need to fill in any data.
                break;
        }
    }
}

public sealed partial class AssertionNode
{
    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        // Match the behaviour of EatsAtLeast on this node.
        if (_assertionType == AssertionType.AT_START && notAtStart) return;
        OnSuccess.FillInBMInfo(offset, budget - 1, bm, notAtStart);
        SaveBMInfo(bm, notAtStart, offset);
    }

    // Emit the code to handle \b and \B (word-boundary or non-word-boundary).
    EmitResult EmitBoundaryCheck(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        Trace.TriBool nextIsWordCharacter = Trace.TriBool.UNKNOWN;
        bool notAtStart = trace.AtStart == Trace.TriBool.FALSE_VALUE;
        BoyerMooreLookahead? lookahead = BmInfo(notAtStart);
        if (lookahead is null)
        {
            int eatsAtLeast = (int)Math.Min(kMaxLookaheadForBoyerMoore, EatsAtLeast(notAtStart));
            if (eatsAtLeast >= 1)
            {
                var bm = new BoyerMooreLookahead(eatsAtLeast, compiler);
                FillInBMInfo(0, kRecursionBudget, bm, notAtStart);
                if (bm.At(0).IsNonWord) nextIsWordCharacter = Trace.TriBool.FALSE_VALUE;
                if (bm.At(0).IsWord) nextIsWordCharacter = Trace.TriBool.TRUE_VALUE;
            }
        }
        else
        {
            if (lookahead.At(0).IsNonWord) nextIsWordCharacter = Trace.TriBool.FALSE_VALUE;
            if (lookahead.At(0).IsWord) nextIsWordCharacter = Trace.TriBool.TRUE_VALUE;
        }
        bool atBoundary = _assertionType == AssertionType.AT_BOUNDARY;
        if (nextIsWordCharacter == Trace.TriBool.UNKNOWN)
        {
            var beforeNonWord = new Label();
            var beforeWord = new Label();
            if (trace.CharactersPreloaded != 1)
            {
                assembler.LoadCurrentCharacter(trace.CpOffset, beforeNonWord);
            }
            // Fall through on non-word.
            RegExpCompilerHelpers.EmitWordCheck(assembler, beforeWord, beforeNonWord, false);
            // Next character is not a word character.
            assembler.Bind(beforeNonWord);
            var ok = new Label();
            EmitResult r = BacktrackIfPrevious(compiler, trace, atBoundary ? IfPrevious.kIsNonWord : IfPrevious.kIsWord);
            if (r.IsError) return r;
            assembler.GoTo(ok);

            assembler.Bind(beforeWord);
            r = BacktrackIfPrevious(compiler, trace, atBoundary ? IfPrevious.kIsWord : IfPrevious.kIsNonWord);
            if (r.IsError) return r;
            assembler.Bind(ok);
        }
        else if (nextIsWordCharacter == Trace.TriBool.TRUE_VALUE)
        {
            EmitResult r = BacktrackIfPrevious(compiler, trace, atBoundary ? IfPrevious.kIsWord : IfPrevious.kIsNonWord);
            if (r.IsError) return r;
        }
        else
        {
            Debug.Assert(nextIsWordCharacter == Trace.TriBool.FALSE_VALUE);
            EmitResult r = BacktrackIfPrevious(compiler, trace, atBoundary ? IfPrevious.kIsNonWord : IfPrevious.kIsWord);
            if (r.IsError) return r;
        }
        return EmitResult.Success();
    }

    EmitResult BacktrackIfPrevious(RegExpCompiler compiler, Trace trace, IfPrevious backtrackIfPrevious)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        var newTrace = new Trace(trace);
        newTrace.InvalidateCurrentCharacter();

        var fallThrough = new Label();
        Label? nonWord = backtrackIfPrevious == IfPrevious.kIsNonWord ? newTrace.Backtrack : fallThrough;
        Label? word = backtrackIfPrevious == IfPrevious.kIsNonWord ? fallThrough : newTrace.Backtrack;

        // A positive (> 0) cp_offset means we've already successfully matched a
        // non-empty-width part of the pattern, and thus cannot be at or before the
        // start of the subject string. We can thus skip both at-start and
        // bounds-checks when loading the one-character lookbehind.
        bool mayBeAtOrBeforeSubjectStringStart = newTrace.CpOffset <= 0;

        if (mayBeAtOrBeforeSubjectStringStart)
        {
            // The start of input counts as a non-word character, so the question is
            // decided if we are at the start.
            assembler.CheckAtStart(newTrace.CpOffset, nonWord);
        }

        // If we've already checked that we are not at the start of input, it's okay
        // to load the previous character without bounds checks.
        bool canSkipBoundsCheck = !mayBeAtOrBeforeSubjectStringStart;
        assembler.LoadCurrentCharacter(newTrace.CpOffset - 1, nonWord, canSkipBoundsCheck);
        RegExpCompilerHelpers.EmitWordCheck(assembler, word, nonWord, backtrackIfPrevious == IfPrevious.kIsNonWord);

        assembler.Bind(fallThrough);
        return OnSuccess.Emit(compiler, newTrace);
    }

    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler, int filledIn,
        bool notAtStart, int budget)
    {
        if (_assertionType == AssertionType.AT_START && notAtStart)
        {
            details.SetCannotMatchFrom(filledIn);
            return;
        }
        if (_assertionType == AssertionType.AT_END)
        {
            details.SetCannotMatchFrom(filledIn);
            return;
        }
        OnSuccess.GetQuickCheckDetails(details, compiler, filledIn, notAtStart, budget - 1);
    }

    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        switch (_assertionType)
        {
            case AssertionType.AT_END:
            {
                var ok = new Label();
                assembler.CheckPosition(trace.CpOffset, ok);
                assembler.GoTo(trace.Backtrack);
                assembler.Bind(ok);
                break;
            }
            case AssertionType.AT_START:
                if (trace.AtStart == Trace.TriBool.FALSE_VALUE)
                {
                    assembler.GoTo(trace.Backtrack);
                    return EmitResult.Success();
                }
                if (trace.AtStart == Trace.TriBool.UNKNOWN)
                {
                    assembler.CheckNotAtStart(trace.CpOffset, trace.Backtrack);
                    var atStartTrace = new Trace(trace);
                    atStartTrace.AtStart = Trace.TriBool.TRUE_VALUE;
                    return OnSuccess.Emit(compiler, atStartTrace);
                }
                break;
            case AssertionType.AFTER_NEWLINE:
                return RegExpCompilerHelpers.EmitHat(compiler, OnSuccess, trace);
            case AssertionType.AT_BOUNDARY:
            case AssertionType.AT_NON_BOUNDARY:
                return EmitBoundaryCheck(compiler, trace);
        }
        return OnSuccess.Emit(compiler, trace);
    }
}

public sealed partial class NegativeLookaroundChoiceNode
{
    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler, int filledIn,
        bool notAtStart, int budget)
    {
        RegExpNode node = ContinueNode;
        node.GetQuickCheckDetails(details, compiler, filledIn, notAtStart, budget - 1);
    }
}

public sealed partial class LoopChoiceNode
{
    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget)
    {
        if (_bodyCanBeZeroLength || budget <= 0) return;
        notAtStart = notAtStart || NotAtStart;
        Debug.Assert(_alternatives.Count == 2);  // There's just loop and continue.
        base.GetQuickCheckDetails(details, compiler, charactersFilledIn, notAtStart, budget);
    }

    public override void FillInBMInfo(int offset, int budget, BoyerMooreLookahead bm, bool notAtStart)
    {
        if (_bodyCanBeZeroLength || budget <= 0)
        {
            bm.SetRest(offset);
            SaveBMInfo(bm, notAtStart, offset);
            return;
        }
        base.FillInBMInfo(offset, budget - 1, bm, notAtStart);
        SaveBMInfo(bm, notAtStart, offset);
    }
}

public partial class ChoiceNode
{
    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget)
    {
        notAtStart = notAtStart || _notAtStart;
        int choiceCount = _alternatives.Count;
        Debug.Assert(choiceCount > 0);
        budget /= choiceCount;
        _alternatives[0].Node.GetQuickCheckDetails(details, compiler, charactersFilledIn, notAtStart, budget);
        for (int i = 1; i < choiceCount; i++)
        {
            var newDetails = new QuickCheckDetails(details.Characters);
            RegExpNode node = _alternatives[i].Node;
            node.GetQuickCheckDetails(newDetails, compiler, charactersFilledIn, notAtStart, budget);
            // Here we merge the quick match details of the two branches.
            details.Merge(newDetails, charactersFilledIn);
        }
    }

    // Finds the fixed match length of a sequence of nodes that goes from
    // this alternative and back to this choice node.  If there are variable
    // length nodes or other complications in the way then return a sentinel
    // value indicating that a fixed length loop cannot be constructed.
    protected int FixedLengthLoopLengthForAlternative(ref GuardedAlternative alternative)
    {
        int length = 0;
        RegExpNode node = alternative.Node;
        // Later we will generate code for all these text nodes using recursion
        // so we have to limit the max number.
        int recursionDepth = 0;
        while (node != this)
        {
            if (recursionDepth++ > RegExpCompiler.kMaxRecursion) return kNodeIsTooComplexForFixedLengthLoops;
            int nodeLength = node.FixedLengthLoopLength();
            if (nodeLength == kNodeIsTooComplexForFixedLengthLoops) return kNodeIsTooComplexForFixedLengthLoops;
            length += nodeLength;
            node = node.AsSeqNode()!.OnSuccess;
        }
        if (ReadBackward) length = -length;
        // Check that we can jump by the whole text length. If not, return sentinel
        // to indicate the we can't construct a fixed length loop.
        if (length < RegExpMacroAssembler.kMinCPOffset || length > RegExpMacroAssembler.kMaxCPOffset)
        {
            return kNodeIsTooComplexForFixedLengthLoops;
        }
        return length;
    }
}

public sealed partial class TextNode
{
    // Returns the number of characters in the equivalence class, omitting those
    // that cannot occur in the source string because it is Latin1.  This is called
    // both for unicode modes /ui and /vi, and also for legacy case independent
    // mode /i.  In the case of Unicode modes we handled surrogate pair expansions
    // earlier so at this point it's all about single-code-unit expansions.
    public int GetCaseIndependentLetters(RegExpCompiler compiler, int character, Span<int> letters)
    {
        bool oneByteSubject = compiler.OneByte;
        bool unicode = Flags.IsEitherUnicode();
        const int kMaxAscii = 0x7f;
        if (!unicode && character <= kMaxAscii)
        {
            // Fast case for common characters.
            int upper = character & ~0x20;
            if ('A' <= upper && upper <= 'Z')
            {
                letters[0] = upper;
                letters[1] = upper | 0x20;
                return 2;
            }
            letters[0] = character;
            return 1;
        }

        var set = new CodePointSet(character, character);
        CaseFolding.CloseOver(set, unicode ? CaseFolding.Mode.kUnicode : CaseFolding.Mode.kNonUnicode);

        int rangeCount = set.RangeCount;
        int items = 0;
        for (int i = 0; i < rangeCount; i++)
        {
            int start = set.GetRangeStart(i);
            int end = set.GetRangeEnd(i);
            if (!(end - start + items <= letters.Length)) throw new InvalidOperationException("CHECK failed");
            for (int cu = start; cu <= end; cu++)
            {
                if (oneByteSubject && cu > kMaxOneByteCharCode) continue;
                letters[items++] = cu;
            }
        }
        return items;
    }

    bool EmitSimpleCharacter(RegExpCompiler compiler, int c, Label? onFailure, int cpOffset, bool check,
        bool preloaded)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        bool boundChecked = false;
        if (!preloaded)
        {
            assembler.LoadCurrentCharacter(cpOffset, onFailure, check);
            boundChecked = true;
        }
        assembler.CheckNotCharacter((uint)c, onFailure);
        return boundChecked;
    }

    // Only emits non-letters (things that don't have case).  Only used for case
    // independent matches.
    bool EmitAtomNonLetter(RegExpCompiler compiler, int c, Label? onFailure, int cpOffset, bool check,
        bool preloaded)
    {
        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        bool oneByte = compiler.OneByte;
        Span<int> chars = stackalloc int[4];
        int length = GetCaseIndependentLetters(compiler, c, chars);
        if (length < 1)
        {
            // This can't match.  Must be an one-byte subject and a non-one-byte
            // character.  We do not need to do anything since the one-byte pass
            // already handled this.
            if (!oneByte) throw new InvalidOperationException("CHECK failed");
            return false;  // Bounds not checked.
        }
        bool isChecked = false;
        // We handle the length > 1 case in a later pass.
        if (length == 1)
        {
            // GetCaseIndependentLetters promises not to return characters that can't
            // match because of the subject encoding.  This case is already handled by
            // the one-byte pass.
            if (!preloaded)
            {
                macroAssembler.LoadCurrentCharacter(cpOffset, onFailure, check);
                isChecked = check;
            }
            macroAssembler.CheckNotCharacter((uint)chars[0], onFailure);
        }
        return isChecked;
    }

    // Only emits letters (things that have case).  Only used for case independent
    // matches.
    bool EmitAtomLetter(RegExpCompiler compiler, int c, Label? onFailure, int cpOffset, bool check, bool preloaded)
    {
        RegExpMacroAssembler macroAssembler = compiler.MacroAssembler;
        bool oneByte = compiler.OneByte;
        Span<int> chars = stackalloc int[4];
        int length = GetCaseIndependentLetters(compiler, c, chars);
        // The 0 and 1 case are handled by earlier passes.
        if (length <= 1) return false;
        // We may not need to check against the end of the input string
        // if this character lies before a character that matched.
        if (!preloaded) macroAssembler.LoadCurrentCharacter(cpOffset, onFailure, check);
        var ok = new Label();
        switch (length)
        {
            case 2:
                if (RegExpCompilerHelpers.ShortCutEmitCharacterPair(macroAssembler, oneByte, chars[0], chars[1],
                        onFailure))
                {
                }
                else
                {
                    macroAssembler.CheckCharacter((uint)chars[0], ok);
                    macroAssembler.CheckNotCharacter((uint)chars[1], onFailure);
                    macroAssembler.Bind(ok);
                }
                break;
            case 4:
                macroAssembler.CheckCharacter((uint)chars[3], ok);
                goto case 3;
            case 3:
                macroAssembler.CheckCharacter((uint)chars[0], ok);
                macroAssembler.CheckCharacter((uint)chars[1], ok);
                macroAssembler.CheckNotCharacter((uint)chars[2], onFailure);
                macroAssembler.Bind(ok);
                break;
            default:
                throw new InvalidOperationException("UNREACHABLE");
        }
        return true;
    }

    // Here is the meat of GetQuickCheckDetails (see also the comment on the
    // super-class in the .h file).
    //
    // We iterate along the text object, building up for each character a
    // mask and value that can be used to test for a quick failure to match.
    // The masks and values for the positions will be combined into a single
    // machine word for the current character width in order to be used in
    // generating a quick check.
    public override void GetQuickCheckDetails(QuickCheckDetails details, RegExpCompiler compiler,
        int charactersFilledIn, bool notAtStart, int budget)
    {
        // Do not collect any quick check details if the text node reads backward,
        // since it reads in the opposite direction than we use for quick checks.
        if (_readBackward) return;
        Debug.Assert(charactersFilledIn < details.Characters);
        int characters = details.Characters;
        uint charMask = CharMask(compiler.OneByte);
        Span<int> chars = stackalloc int[4];
        for (int k = 0; k < _elms.Count; k++)
        {
            TextElement elm = _elms[k];
            if (elm.Type == TextElement.TextType.ATOM)
            {
                string quarks = elm.Atom.Data;
                for (int i = 0; i < characters && i < quarks.Length; i++)
                {
                    ref QuickCheckDetails.Position pos = ref details.Positions(charactersFilledIn);
                    int c = quarks[i];
                    if (Flags.IsIgnoreCase())
                    {
                        int length = GetCaseIndependentLetters(compiler, c, chars);
                        if (length == 0)
                        {
                            // This can happen because all case variants are non-Latin1, but we
                            // know the input is Latin1.
                            details.SetCannotMatchFrom(charactersFilledIn);
                            pos.DeterminesPerfectly = false;
                            return;
                        }
                        if (length == 1)
                        {
                            // This letter has no case equivalents, so it's nice and simple
                            // and the mask-compare will determine definitely whether we have
                            // a match at this character position.
                            pos.Mask = charMask;
                            pos.Value = (uint)chars[0];
                            pos.DeterminesPerfectly = true;
                        }
                        else
                        {
                            uint commonBits = charMask;
                            uint bits = (uint)chars[0];
                            for (int j = 1; j < length; j++)
                            {
                                uint differingBits = ((uint)chars[j] & commonBits) ^ bits;
                                commonBits ^= differingBits;
                                bits &= commonBits;
                            }
                            // If length is 2 and common bits has only one zero in it then
                            // our mask and compare instruction will determine definitely
                            // whether we have a match at this character position.  Otherwise
                            // it can only be an approximate check.
                            uint oneZero = commonBits | ~charMask;
                            if (length == 2 && (~oneZero & (~oneZero - 1)) == 0) pos.DeterminesPerfectly = true;
                            pos.Mask = commonBits;
                            pos.Value = bits;
                        }
                    }
                    else
                    {
                        // Don't ignore case.  Nice simple case where the mask-compare will
                        // determine definitely whether we have a match at this character
                        // position.
                        if ((uint)c > charMask)
                        {
                            details.SetCannotMatchFrom(charactersFilledIn);
                            pos.DeterminesPerfectly = false;
                            return;
                        }
                        pos.Mask = charMask;
                        pos.Value = (uint)c;
                        pos.DeterminesPerfectly = true;
                    }
                    charactersFilledIn++;
                    Debug.Assert(charactersFilledIn <= details.Characters);
                    if (charactersFilledIn == details.Characters) return;
                }
            }
            else
            {
                ref QuickCheckDetails.Position pos = ref details.Positions(charactersFilledIn);
                RegExpClassRanges tree = elm.ClassRanges;
                List<CharacterRange> ranges = tree.Ranges;
                // Canonicalize ranges to ensure they are disjoint and sorted. This is
                // required for the correctness of the determines_perfectly check below,
                // which computes the total character count by summing the sizes of the
                // ranges.
                CharacterRange.Canonicalize(ranges);
                if (tree.IsNegated || ranges.Count == 0)
                {
                    // A quick check uses multi-character mask and compare.  There is no
                    // useful way to incorporate a negative char class into this scheme
                    // so we just conservatively create a mask and value that will always
                    // succeed.
                    // Likewise for empty ranges (empty ranges can occur e.g. when
                    // compiling for one-byte subjects and impossible (non-one-byte) ranges
                    // have been removed).
                    pos.Mask = 0;
                    pos.Value = 0;
                }
                else
                {
                    int firstRange = 0;
                    while ((uint)ranges[firstRange].From > charMask)
                    {
                        firstRange++;
                        if (firstRange == ranges.Count)
                        {
                            details.SetCannotMatchFrom(charactersFilledIn);
                            pos.DeterminesPerfectly = false;
                            return;
                        }
                    }
                    int totalCharacters = 0;
                    CharacterRange range = ranges[firstRange];
                    uint firstFrom = (uint)range.From;
                    uint firstTo = (uint)range.To > charMask ? charMask : (uint)range.To;
                    totalCharacters += (int)(firstTo - firstFrom + 1);
                    uint differingBits = firstFrom ^ firstTo;
                    uint commonBits = ~RegExpCompilerHelpers.SmearBitsRight(differingBits);
                    uint bits = firstFrom & commonBits;
                    for (int i = firstRange + 1; i < ranges.Count; i++)
                    {
                        range = ranges[i];
                        uint from = (uint)range.From;
                        if (from > charMask) continue;
                        uint to = (uint)range.To > charMask ? charMask : (uint)range.To;
                        totalCharacters += (int)(to - from + 1);
                        uint newCommonBits = from ^ to;
                        newCommonBits = ~RegExpCompilerHelpers.SmearBitsRight(newCommonBits);
                        commonBits &= newCommonBits;
                        bits &= newCommonBits;
                        uint newDifferingBits = (from & commonBits) ^ bits;
                        commonBits ^= newDifferingBits;
                        bits &= commonBits;
                    }
                    pos.Mask = commonBits;
                    pos.Value = bits;
                    // A mask-and-compare check is perfectly precise (no false positives)
                    // iff the number of matching characters in the class equals the number
                    // of combinations allowed by the ignored bits (2^zero_bits).
                    int zeroBits = System.Numerics.BitOperations.PopCount(~commonBits & charMask);
                    pos.DeterminesPerfectly = totalCharacters == (1 << zeroBits);
                }
                charactersFilledIn++;
                Debug.Assert(charactersFilledIn <= details.Characters);
                if (charactersFilledIn == details.Characters) return;
            }
        }
        Debug.Assert(charactersFilledIn != details.Characters);
        if (!details.CannotMatch())
        {
            OnSuccess.GetQuickCheckDetails(details, compiler, charactersFilledIn, true, budget - 1);
        }
    }

    // Returns false if the text node can't match in one-byte mode.
    public bool CanMatchLatin1(RegExpCompiler compiler)
    {
        RegExpFlags flags = Flags;
        int elementCount = _elms.Count;
        Span<int> chars = stackalloc int[4];
        for (int i = 0; i < elementCount; i++)
        {
            TextElement elm = _elms[i];
            if (elm.Type == TextElement.TextType.ATOM)
            {
                string quarks = elm.Atom.Data;
                for (int j = 0; j < quarks.Length; j++)
                {
                    int c = quarks[j];
                    if (!flags.IsIgnoreCase())
                    {
                        if (c > kMaxOneByteCharCode) return false;
                    }
                    else
                    {
                        int length = GetCaseIndependentLetters(compiler, c, chars);
                        if (length == 0 || chars[0] > kMaxOneByteCharCode) return false;
                    }
                }
            }
            else
            {
                // A character class can also be impossible to match in one-byte mode.
                Debug.Assert(elm.Type == TextElement.TextType.CLASS_RANGES);
                RegExpClassRanges cr = elm.ClassRanges;
                List<CharacterRange> ranges = cr.Ranges;
                CharacterRange.Canonicalize(ranges);
                // Now they are in order so we only need to look at the first.
                // If we are in non-Unicode case independent mode then we need
                // to be a bit careful here, because the character classes have
                // not been case-desugared yet, but there are characters and ranges
                // that can become Latin-1 when case is considered.
                int rangeCount = ranges.Count;
                if (cr.IsNegated)
                {
                    if (rangeCount != 0 && ranges[0].From == 0 && ranges[0].To >= kMaxOneByteCharCode)
                    {
                        bool caseComplications = !flags.IsEitherUnicode() && flags.IsIgnoreCase() &&
                                                 RegExpCompilerHelpers.RangesContainLatin1Equivalents(ranges);
                        if (!caseComplications) return false;
                    }
                }
                else
                {
                    if (rangeCount == 0 || ranges[0].From > kMaxOneByteCharCode)
                    {
                        bool caseComplications = !flags.IsEitherUnicode() && flags.IsIgnoreCase() &&
                                                 RegExpCompilerHelpers.RangesContainLatin1Equivalents(ranges);
                        if (!caseComplications) return false;
                    }
                }
            }
        }
        return true;  // It might match Latin1 input, we can't eliminate this node.
    }

    // We call this repeatedly to generate code for each pass over the text node.
    // The passes are in increasing order of difficulty because we hope one
    // of the first passes will fail in which case we are saved the work of the
    // later passes.  for example for the case independent regexp /%[asdfghjkl]a/
    // we will check the '%' in the first pass, the case independent 'a' in the
    // second pass and the character class in the last pass.
    //
    // The passes are done from right to left, so for example to test for /bar/
    // we will first test for an 'r' with offset 2, then an 'a' with offset 1
    // and then a 'b' with offset 0.  This means we can avoid the end-of-input
    // bounds check most of the time.  In the example we only need to check for
    // end-of-input when loading the putative 'r'.
    //
    // A slight complication involves the fact that the first character may already
    // be fetched into a register by the previous node.  In this case we want to
    // do the test for that character first.  We do this in separate passes.  The
    // 'preloaded' argument indicates that we are doing such a 'pass'.  If such a
    // pass has been performed then subsequent passes will have true in
    // first_element_checked to indicate that that character does not need to be
    // checked again.
    //
    // In addition to all this we are passed a Trace, which can
    // contain an AlternativeGeneration object.  In this AlternativeGeneration
    // object we can see details of any quick check that was already passed in
    // order to get to the code we are now generating.  The quick check can involve
    // loading characters, which means we do not need to recheck the bounds
    // up to the limit the quick check already checked.  In addition the quick
    // check can have involved a mask and compare operation which may simplify
    // or obviate the need for further checks at some character positions.
    void TextEmitPass(RegExpCompiler compiler, TextEmitPassType pass, bool preloaded, Trace trace,
        bool firstElementChecked, ref int checkedUpTo)
    {
        RegExpMacroAssembler assembler = compiler.MacroAssembler;
        bool oneByte = compiler.OneByte;
        Label? backtrack = trace.Backtrack;
        QuickCheckDetails quickCheck = trace.QuickCheckPerformed;
        int elementCount = _elms.Count;
        int backwardOffset = _readBackward ? -Length() : 0;
        Span<int> chars = stackalloc int[4];
        for (int i = preloaded ? 0 : elementCount - 1; i >= 0; i--)
        {
            TextElement elm = _elms[i];
            int cpOffset = trace.CpOffset + elm.CpOffset + backwardOffset;
            if (elm.Type == TextElement.TextType.ATOM)
            {
                string quarks = elm.Atom.Data;
                for (int j = preloaded ? 0 : quarks.Length - 1; j >= 0; j--)
                {
                    if (firstElementChecked && i == 0 && j == 0) continue;
                    if (RegExpCompilerHelpers.DeterminedAlready(quickCheck, elm.CpOffset + j)) continue;
                    int quark = quarks[j];
                    bool needsBoundsCheck = checkedUpTo < cpOffset + j || _readBackward;
                    bool boundsChecked = false;
                    switch (pass)
                    {
                        case TextEmitPassType.NON_LATIN1_MATCH:
                            Debug.Assert(oneByte);  // This pass is only done in one-byte mode.
                            if (Flags.IsIgnoreCase())
                            {
                                // We are compiling for a one-byte subject, case independent mode.
                                // We have to check whether any of the case alternatives are in
                                // the one-byte range.
                                // Only returns characters that are in the one-byte range.
                                int length = GetCaseIndependentLetters(compiler, quark, chars);
                                if (length == 0)
                                {
                                    assembler.GoTo(backtrack);
                                    return;
                                }
                            }
                            else
                            {
                                // Case-dependent mode.
                                if (quark > kMaxOneByteCharCode)
                                {
                                    assembler.GoTo(backtrack);
                                    return;
                                }
                            }
                            break;
                        case TextEmitPassType.NON_LETTER_CHARACTER_MATCH:
                            boundsChecked = EmitAtomNonLetter(compiler, quark, backtrack, cpOffset + j,
                                needsBoundsCheck, preloaded);
                            break;
                        case TextEmitPassType.SIMPLE_CHARACTER_MATCH:
                            boundsChecked = EmitSimpleCharacter(compiler, quark, backtrack, cpOffset + j,
                                needsBoundsCheck, preloaded);
                            break;
                        case TextEmitPassType.CASE_CHARACTER_MATCH:
                            boundsChecked = EmitAtomLetter(compiler, quark, backtrack, cpOffset + j,
                                needsBoundsCheck, preloaded);
                            break;
                        default:
                            break;
                    }
                    if (boundsChecked) RegExpCompilerHelpers.UpdateBoundsCheck(cpOffset + j, ref checkedUpTo);
                }
            }
            else
            {
                Debug.Assert(elm.Type == TextElement.TextType.CLASS_RANGES);
                if (pass == TextEmitPassType.CHARACTER_CLASS_MATCH)
                {
                    if (firstElementChecked && i == 0) continue;
                    if (RegExpCompilerHelpers.DeterminedAlready(quickCheck, elm.CpOffset)) continue;
                    RegExpClassRanges cr = elm.ClassRanges;
                    bool boundsCheck = checkedUpTo < cpOffset || _readBackward;
                    QuickCheckDetails.Position? known = elm.CpOffset < quickCheck.Characters
                        ? quickCheck.Positions(elm.CpOffset)
                        : null;
                    RegExpCompilerHelpers.EmitClassRanges(compiler, assembler, cr, oneByte, backtrack, cpOffset,
                        boundsCheck, preloaded, known);
                    RegExpCompilerHelpers.UpdateBoundsCheck(cpOffset, ref checkedUpTo);
                }
            }
        }
    }

    public int Length()
    {
        TextElement elm = _elms[^1];
        Debug.Assert(elm.CpOffset >= 0);
        return elm.CpOffset + elm.Length;
    }

    // Create TextNode for a single character class for the given ranges.
    public static TextNode CreateForCharacterRanges(List<CharacterRange> ranges, bool readBackward,
        RegExpNode onSuccess, RegExpFlags flags)
    {
        // TODO(jgruber): There's no fundamental need to create this
        // ClassRanges; we could refactor to avoid the allocation.
        return new TextNode(new RegExpClassRanges(ranges), readBackward, onSuccess, flags);
    }

    // Create TextNode for a surrogate pair (i.e. match a sequence of two uc16
    // code unit ranges).
    public static TextNode CreateForSurrogatePair(CharacterRange lead, List<CharacterRange> trailRanges,
        bool readBackward, RegExpNode onSuccess, RegExpFlags flags)
    {
        var elms = new List<TextElement>(2);
        if (lead.From == lead.To)
        {
            var atom = new RegExpAtom(((char)lead.From).ToString());
            elms.Add(TextElement.FromAtom(atom));
        }
        else
        {
            List<CharacterRange> leadRanges = CharacterRange.List(lead);
            elms.Add(TextElement.FromClassRanges(new RegExpClassRanges(leadRanges)));
        }
        elms.Add(TextElement.FromClassRanges(new RegExpClassRanges(trailRanges)));
        return new TextNode(elms, readBackward, onSuccess, flags);
    }

    public static TextNode CreateForSurrogatePair(List<CharacterRange> leadRanges, CharacterRange trail,
        bool readBackward, RegExpNode onSuccess, RegExpFlags flags)
    {
        List<CharacterRange> trailRanges = CharacterRange.List(trail);
        var elms = new List<TextElement>(2)
        {
            TextElement.FromClassRanges(new RegExpClassRanges(leadRanges)),
            TextElement.FromClassRanges(new RegExpClassRanges(trailRanges)),
        };
        return new TextNode(elms, readBackward, onSuccess, flags);
    }

    // This generates the code to match a text node.  A text node can contain
    // straight character sequences (possibly to be matched in a case-independent
    // way) and character classes.  For efficiency we do not do this in a single
    // pass from left to right.  Instead we pass over the text node several times,
    // emitting code for some character positions every time.  See the comment on
    // TextEmitPass for details.
    public override EmitResult Emit(RegExpCompiler compiler, Trace trace)
    {
        LimitResult limitResult = LimitVersions(compiler, trace);
        if (limitResult == LimitResult.DONE) return EmitResult.Success();
        Debug.Assert(limitResult == LimitResult.CONTINUE);

        int maxOffset = _readBackward ? trace.CpOffset - Length() : trace.CpOffset + Length();
        if (maxOffset < RegExpMacroAssembler.kMinCPOffset || maxOffset > RegExpMacroAssembler.kMaxCPOffset)
        {
            compiler.SetRegExpTooBig();
            return EmitResult.Error();
        }

        if (compiler.OneByte)
        {
            int dummy = 0;
            TextEmitPass(compiler, TextEmitPassType.NON_LATIN1_MATCH, false, trace, false, ref dummy);
        }

        bool firstEltDone = false;
        int boundCheckedTo = trace.CpOffset - 1;
        boundCheckedTo += trace.BoundCheckedUpTo;

        // If a character is preloaded into the current character register then
        // check that first to save reloading it.
        for (int twice = 0; twice < 2; twice++)
        {
            bool isPreloadedPass = twice == 0;
            if (isPreloadedPass && trace.CharactersPreloaded != 1) continue;
            if (Flags.IsIgnoreCase())
            {
                TextEmitPass(compiler, TextEmitPassType.NON_LETTER_CHARACTER_MATCH, isPreloadedPass, trace,
                    firstEltDone, ref boundCheckedTo);
                TextEmitPass(compiler, TextEmitPassType.CASE_CHARACTER_MATCH, isPreloadedPass, trace, firstEltDone,
                    ref boundCheckedTo);
            }
            else
            {
                TextEmitPass(compiler, TextEmitPassType.SIMPLE_CHARACTER_MATCH, isPreloadedPass, trace,
                    firstEltDone, ref boundCheckedTo);
            }
            TextEmitPass(compiler, TextEmitPassType.CHARACTER_CLASS_MATCH, isPreloadedPass, trace, firstEltDone,
                ref boundCheckedTo);
            firstEltDone = true;
        }

        var successorTrace = new Trace(trace);
        // If we advance backward, we may end up at the start.
        EmitResult r = successorTrace.AdvanceCurrentPositionInTrace(_readBackward ? -Length() : Length(), compiler);
        if (r.IsError) return r;
        successorTrace.AtStart = _readBackward ? Trace.TriBool.UNKNOWN : Trace.TriBool.FALSE_VALUE;
        using var rc = new RecursionCheck(compiler);
        return OnSuccess.Emit(compiler, successorTrace);
    }

    public void MakeCaseIndependent(bool isOneByte)
    {
        if (!Flags.IsIgnoreCase()) return;
        // This is done in an earlier step when generating the nodes from the AST
        // because we may have to split up into separate nodes.
        if (NeedsUnicodeCaseEquivalents(Flags)) return;

        int elementCount = _elms.Count;
        for (int i = 0; i < elementCount; i++)
        {
            TextElement elm = _elms[i];
            if (elm.Type == TextElement.TextType.CLASS_RANGES)
            {
                RegExpClassRanges cr = elm.ClassRanges;
                // None of the standard character classes is different in the case
                // independent case and it slows us down if we don't know that.
                if (cr.IsStandard()) continue;
                List<CharacterRange> ranges = cr.Ranges;
                CharacterRange.AddCaseEquivalents(ranges, isOneByte);
            }
        }
    }

    public override int FixedLengthLoopLength() => Length();

    public override RegExpNode? GetSuccessorOfOmnivorousTextNode(RegExpCompiler compiler)
    {
        if (_readBackward) return null;
        if (_elms.Count != 1) return null;
        TextElement elm = _elms[0];
        if (elm.Type != TextElement.TextType.CLASS_RANGES) return null;
        RegExpClassRanges node = elm.ClassRanges;
        List<CharacterRange> ranges = node.Ranges;
        CharacterRange.Canonicalize(ranges);
        if (node.IsNegated) return ranges.Count == 0 ? OnSuccess : null;
        if (ranges.Count != 1) return null;
        int maxChar = (int)MaxCodeUnit(compiler.OneByte);
        return ranges[0].IsEverything(maxChar) ? OnSuccess : null;
    }
}
