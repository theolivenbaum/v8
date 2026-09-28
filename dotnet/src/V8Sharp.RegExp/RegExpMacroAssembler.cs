// Port of src/regexp/regexp-macro-assembler.h and
// src/regexp/regexp-macro-assembler.cc (the architecture-independent parts).
//
// The native backends (x64, arm64 ...) are not ported. The bytecode generator
// (RegExpBytecodeGenerator) is one implementation of this abstraction; a later
// tier can add an assembler that emits .NET IL, so the virtual surface is kept
// exactly as V8 has it. ByteArray tables are byte[] of kTableSize entries.

using V8Sharp.RegExp.Unicode;

namespace V8Sharp.RegExp;

public abstract class RegExpMacroAssembler
{
    public const int kLeadSurrogateStart = 0xd800;
    public const int kLeadSurrogateEnd = 0xdbff;
    public const int kTrailSurrogateStart = 0xdc00;
    public const int kTrailSurrogateEnd = 0xdfff;
    public const int kNonBmpStart = 0x10000;
    public const int kNonBmpEnd = 0x10ffff;

    // The implementation must be able to handle at least:
    public const int kMaxRegisterCount = 1 << 16;
    public const int kMaxRegister = kMaxRegisterCount - 1;
    public const int kMaxCaptures = (kMaxRegister - 1) / 2;
    // Note the minimum value is chosen s.t. a negated valid offset is also a
    // valid offset.
    public const int kMaxCPOffset = (1 << 15) - 1;
    public const int kMinCPOffset = -kMaxCPOffset;

    public const int kMaxEatsAtLeastValue = byte.MaxValue;

    public const int kTableSizeBits = 7;
    public const int kTableSize = 1 << kTableSizeBits;
    public const int kTableMask = kTableSize - 1;

    /// <summary>JSRegExp::kNoBacktrackLimit.</summary>
    public const uint kNoBacktrackLimit = 0;

    /// <summary>RegExpStack::kStackLimitSlackSlotCount.</summary>
    public const int kStackLimitSlackSlotCount = 32;

    // Type of input string to generate code for.
    public enum Mode { LATIN1 = 1, UC16 = 2 }

    public enum StackCheckFlag : byte { kNoStackLimitCheck = 0, kCheckStackLimit = 1 }

    public enum IrregexpImplementation
    {
        kIA32Implementation,
        kARMImplementation,
        kARM64Implementation,
        kMIPSImplementation,
        kLOONG64Implementation,
        kRISCVImplementation,
        kRISCV32Implementation,
        kS390Implementation,
        kPPCImplementation,
        kX64Implementation,
        kBytecodeImplementation,
        /// <summary>RegExpMacroAssemblerIL (not in V8: the IL counterpart of the native assemblers).</summary>
        kILImplementation,
    }

    public enum GlobalMode
    {
        NOT_GLOBAL,
        GLOBAL_NO_ZERO_LENGTH_CHECK,
        GLOBAL,
        GLOBAL_UNICODE,
    }

    uint _backtrackLimit = kNoBacktrackLimit;
    bool _canFallback;
    bool _backtrackStackUsed;
    Label? _failLabel;
    GlobalMode _globalMode = GlobalMode.NOT_GLOBAL;
    readonly Mode _mode;

    protected RegExpMacroAssembler(Mode mode) => _mode = mode;

    /// <summary>Returns the generated code (bytecode for the bytecode generator).</summary>
    public abstract object GetCode(string source, RegExpFlags flags);

    // This function is called when code generation is aborted, so that
    // the assembler could clean up internal data structures.
    public virtual void AbortedCodeGeneration() { }

    // The maximal number of pushes between stack checks. Users must supply
    // kCheckStackLimit flag to push operations (instead of kNoStackLimitCheck)
    // at least once for every stack_limit() pushes that are executed.
    public int StackLimitSlackSlotCount => kStackLimitSlackSlotCount;

    /// <summary>
    /// kUnalignedReadSupported &amp;&amp; v8_flags.enable_regexp_unaligned_accesses.
    /// Unaligned reads are supported on every platform .NET runs on.
    /// </summary>
    public virtual bool CanReadUnaligned() => true;

    public abstract void AdvanceCurrentPosition(int by);  // Signed cp change.
    public abstract void AdvanceRegister(int reg, int by);  // r[reg] += by.
    // Continues execution from the position pushed on the top of the backtrack
    // stack by an earlier PushBacktrack(Label*).
    public abstract void Backtrack();
    public abstract void Bind(Label label);
    public abstract void CheckCharacter(uint c, Label? onEqual);
    // Bitwise and the current character with the given constant and then
    // check for a match with c.
    public abstract void CheckCharacterAfterAnd(uint c, uint andWith, Label? onEqual);
    public abstract void CheckCharacterGT(char limit, Label? onGreater);
    public abstract void CheckCharacterLT(char limit, Label? onLess);
    public abstract void CheckFixedLengthLoop(Label? onTosEqualsCurrentPosition);
    public abstract void CheckAtStart(int cpOffset, Label? onAtStart);
    public abstract void CheckNotAtStart(int cpOffset, Label? onNotAtStart);
    public abstract void CheckNotBackReference(int startReg, bool readBackward, Label? onNoMatch);
    public abstract void CheckNotBackReferenceIgnoreCase(int startReg, bool readBackward, bool unicode, Label? onNoMatch);
    // Check the current character for a match with a literal character.  If we
    // fail to match then goto the on_failure label.  End of input always
    // matches.  If the label is nullptr then we should pop a backtrack address
    // off the stack and go to that.
    public abstract void CheckNotCharacter(uint c, Label? onNotEqual);
    public abstract void CheckNotCharacterAfterAnd(uint c, uint andWith, Label? onNotEqual);
    // Subtract a constant from the current character, then and with the given
    // constant and then check for a match with c.
    public abstract void CheckNotCharacterAfterMinusAnd(char c, char minus, char andWith, Label? onNotEqual);
    public abstract void CheckCharacterInRange(char from, char to, Label? onInRange);  // Both inclusive.
    public abstract void CheckCharacterNotInRange(char from, char to, Label? onNotInRange);  // Both inclusive.
    // Returns true if the check was emitted, false otherwise.
    public abstract bool CheckCharacterInRangeArray(List<CharacterRange> ranges, Label? onInRange);
    public abstract bool CheckCharacterNotInRangeArray(List<CharacterRange> ranges, Label? onNotInRange);

    // The current character (modulus the kTableSize) is looked up in the byte
    // array, and if the found byte is non-zero, we jump to the on_bit_set label.
    public abstract void CheckBitInTable(byte[] table, Label? onBitSet);

    // Scan helpers. Each loads the character at |cp_offset| and advances the
    // position by |advance_by| until its stop condition holds (a single char, one
    // of two chars, a bit-table test, ...). Position on exit, which SIMD
    // overrides must preserve:
    //  - on_match:    stop condition held; position unadvanced, so the matching
    //                 char is at |cp_offset| (not consumed).
    //  - on_no_match: bounds check failed first; position left where the load at
    //                 |cp_offset| went out of bounds, for the caller to step
    //                 back.
    public abstract void SkipUntilBitInTable(int cpOffset, byte[] table, byte[]? nibbleTable, int advanceBy,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch);
    public virtual bool SkipUntilBitInTableUseSimd(int advanceBy) => false;

    public virtual void SkipUntilCharAnd(int cpOffset, int advanceBy, uint character, uint mask,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch)
    {
        void EmitScalarCheck()
        {
            LoadCurrentCharacter(cpOffset, onNoMatch, true, 1, boundsCheckOffset);
            CheckCharacterAfterAnd(character, mask, onMatch);
            AdvanceCurrentPosition(advanceBy);
        }
        if (SkipUntilCharAndUseSimd(advanceBy))
        {
            // Scalar check for the first position to avoid SIMD setup overhead if we
            // find a potential match immediately.
            EmitScalarCheck();
            SkipUntilCharAndSimd(cpOffset, advanceBy, character, mask, boundsCheckOffset, onMatch, onNoMatch);
        }
        var loop = new Label();
        Bind(loop);
        EmitScalarCheck();
        GoTo(loop);
    }
    public virtual bool SkipUntilCharAndUseSimd(int advanceBy) => false;
    public virtual void SkipUntilCharAndSimd(int cpOffset, int advanceBy, uint character, uint mask,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) => throw new InvalidOperationException("UNREACHABLE");

    public virtual void SkipUntilChar(int cpOffset, int advanceBy, uint character, int boundsCheckOffset,
        Label? onMatch, Label? onNoMatch)
    {
        void EmitScalarCheck()
        {
            LoadCurrentCharacter(cpOffset, onNoMatch, true, 1, boundsCheckOffset);
            CheckCharacter(character, onMatch);
            AdvanceCurrentPosition(advanceBy);
        }
        if (SkipUntilCharUseSimd(advanceBy))
        {
            // Scalar check for the first position to avoid SIMD setup overhead if we
            // find a potential match immediately.
            EmitScalarCheck();
            SkipUntilCharSimd(cpOffset, advanceBy, character, boundsCheckOffset, onMatch, onNoMatch);
        }
        // Fallback scalar loop
        var loop = new Label();
        Bind(loop);
        EmitScalarCheck();
        GoTo(loop);
    }
    public virtual bool SkipUntilCharUseSimd(int advanceBy) => false;
    public virtual void SkipUntilCharSimd(int cpOffset, int advanceBy, uint character, int boundsCheckOffset,
        Label? onMatch, Label? onNoMatch) => throw new InvalidOperationException("UNREACHABLE");

    public virtual void SkipUntilCharOrChar(int cpOffset, int advanceBy, uint char1, uint char2,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch)
    {
        void EmitScalarCheck()
        {
            LoadCurrentCharacter(cpOffset, onNoMatch, true, 1, boundsCheckOffset);
            CheckCharacter(char1, onMatch);
            CheckCharacter(char2, onMatch);
            AdvanceCurrentPosition(advanceBy);
        }
        if (SkipUntilCharOrCharUseSimd(advanceBy))
        {
            // Scalar check for the first position to avoid SIMD setup overhead if we
            // find a potential match immediately.
            EmitScalarCheck();
            SkipUntilCharOrCharSimd(cpOffset, advanceBy, char1, char2, boundsCheckOffset, onMatch, onNoMatch);
        }
        var loop = new Label();
        Bind(loop);
        EmitScalarCheck();
        GoTo(loop);
    }
    public virtual bool SkipUntilCharOrCharUseSimd(int advanceBy) => false;
    public virtual void SkipUntilCharOrCharSimd(int cpOffset, int advanceBy, uint char1, uint char2,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) => throw new InvalidOperationException("UNREACHABLE");

    public virtual void SkipUntilGtOrNotBitInTable(int cpOffset, int advanceBy, uint character, byte[] table,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch)
    {
        Debug.Assert(character <= char.MaxValue);
        var loop = new Label();
        var advanceAndContinue = new Label();
        Bind(loop);
        LoadCurrentCharacter(cpOffset, onNoMatch, true, 1, boundsCheckOffset);
        CheckCharacterGT((char)character, onMatch);
        CheckBitInTable(table, advanceAndContinue);
        GoTo(onMatch);
        Bind(advanceAndContinue);
        AdvanceCurrentPosition(advanceBy);
        GoTo(loop);
    }

    public virtual void SkipUntilOneOfMasked(int cpOffset, int advanceBy, uint bothChars, uint bothMask,
        int maxOffset, uint chars1, uint mask1, uint chars2, uint mask2, Label? onMatch1, Label? onMatch2,
        Label? onFailure)
    {
        var loop = new Label();
        var found = new Label();
        Bind(loop);
        CheckPosition(maxOffset, onFailure);
        LoadCurrentCharacter(cpOffset, onFailure, false, 4);
        CheckCharacterAfterAnd(bothChars, bothMask, found);
        AdvanceCurrentPosition(advanceBy);
        GoTo(loop);
        Bind(found);
        CheckCharacterAfterAnd(chars1, mask1, onMatch1);
        CheckCharacterAfterAnd(chars2, mask2, onMatch2);
        AdvanceCurrentPosition(advanceBy);
        GoTo(loop);
    }

    public sealed class SkipUntilOneOfMasked3Args
    {
        public int bc0_cp_offset;
        public int bc0_advance_by;
        public byte[] bc0_table = null!;
        public byte[]? bc0_nibble_table;
        public int bc1_bounds_check_offset;
        public Label? bc1_on_failure;
        public int bc1_cp_offset;
        public uint bc2_characters;
        public uint bc2_mask;
        public int bc3_by;
        public int bc4_bounds_check_offset;
        public int bc4_cp_offset;
        public uint bc5_characters;
        public uint bc5_mask;
        public Label? bc5_on_equal;
        public uint bc6_characters;
        public uint bc6_mask;
        public Label? bc6_on_equal;
        public uint bc7_characters;
        public uint bc7_mask;
        public Label? fallthrough_jump_target;
    }

    public virtual bool SkipUntilOneOfMasked3UseSimd(SkipUntilOneOfMasked3Args args) => false;

    public virtual void SkipUntilOneOfMasked3(SkipUntilOneOfMasked3Args args)
    {
        // The base implementation for architectures that don't implement simd
        // optimizations.
        //
        // See the definition of the kSkipUntilOneOfMasked3 peephole bytecode
        // for more context. The initial bytecode sequence is:
        //
        // sequence offset name
        // bc0   0  SkipUntilBitInTable
        // bc1  24  Load4CurrentChars
        // bc2  30  AndCheck4Chars
        // bc3  40  AdvanceCpAndGoto
        // bc4  48  Load4CurrentChars
        // bc5  54  AndCheck4Chars
        // bc6  64  AndCheck4Chars
        // bc7  74  AndCheckNot4Chars
        var bc0SkipUntilBitInTable = new Label();
        var bc1Load4CurrentChars = new Label();
        var bc3AdvanceCpAndGoto = new Label();
        var bc4Load4CurrentChars = new Label();
        Bind(bc0SkipUntilBitInTable);
        SkipUntilBitInTable(args.bc0_cp_offset, args.bc0_table, args.bc0_nibble_table, args.bc0_advance_by,
            args.bc0_cp_offset, bc1Load4CurrentChars, bc1Load4CurrentChars);
        Bind(bc1Load4CurrentChars);
        LoadCurrentCharacter(args.bc1_cp_offset, args.bc1_on_failure, true, 4, args.bc1_bounds_check_offset);
        CheckCharacterAfterAnd(args.bc2_characters, args.bc2_mask, bc4Load4CurrentChars);
        Bind(bc3AdvanceCpAndGoto);
        AdvanceCurrentPosition(args.bc3_by);
        GoTo(bc0SkipUntilBitInTable);
        Bind(bc4Load4CurrentChars);
        LoadCurrentCharacter(args.bc4_cp_offset, bc3AdvanceCpAndGoto, true, 4, args.bc4_bounds_check_offset);
        CheckCharacterAfterAnd(args.bc5_characters, args.bc5_mask, args.bc5_on_equal);
        CheckCharacterAfterAnd(args.bc6_characters, args.bc6_mask, args.bc6_on_equal);
        CheckNotCharacterAfterAnd(args.bc7_characters, args.bc7_mask, bc3AdvanceCpAndGoto);
        GoTo(args.fallthrough_jump_target);
    }

    // Dispatches on bits [shift, shift + log2(table_size)) of the current
    // character: control continues at the code offset stored in the table at
    // index (current_character >> shift) & (table_size - 1). table_size is a
    // power of two. The caller later emits the table data in place via
    // EmitTableSwitchTable, once every target label is bound. Only emitted
    // when CanTableSwitchOnBits() is true.
    public virtual bool CanTableSwitchOnBits() => false;
    public virtual void TableSwitchOnBits(int shift, int tableSize, Label table) => throw new InvalidOperationException("UNREACHABLE");
    public virtual void EmitTableSwitchTable(Label table, Label?[] targets) => throw new InvalidOperationException("UNREACHABLE");

    // Checks whether the given offset from the current position is is in-bounds.
    // May overwrite the current character.
    public abstract void CheckPosition(int cpOffset, Label? onOutsideInput);

    // Check whether a special character class has custom support for more
    // optimized code.
    public bool CanOptimizeSpecialClassRanges(StandardCharacterSet characterSet)
    {
        if (characterSet == StandardCharacterSet.kNotWhitespace)
        {
            // The emitted code for generic character classes is good enough.
            return false;
        }
        if (characterSet == StandardCharacterSet.kWhitespace && CurrentMode != Mode.LATIN1)
        {
            // TODO(pthier): Support \s for 2-byte inputs.
            return false;
        }
        return true;
    }

    // Check whether a standard/default character class matches the current
    // character.
    // May clobber the current loaded character.
    public abstract void CheckSpecialClassRanges(StandardCharacterSet type, Label? onNoMatch);

    // Control-flow integrity:
    // Define a jump target and bind a label.
    public virtual void BindJumpTarget(Label label) => Bind(label);

    public abstract void Fail();
    public abstract void GoTo(Label? label);
    // Check whether a register is >= a given constant and go to a label if it
    // is.  Backtracks instead if the label is nullptr.
    public abstract void IfRegisterGE(int reg, int comparand, Label? ifGe);
    // Check whether a register is < a given constant and go to a label if it is.
    // Backtracks instead if the label is nullptr.
    public abstract void IfRegisterLT(int reg, int comparand, Label? ifLt);
    // Check whether a register is == to the current position and go to a
    // label if it is.
    public abstract void IfRegisterEqPos(int reg, Label? ifEq);

    public int CalculateBoundsCheckOffset(int cpOffset, int characters) =>
        cpOffset >= 0 ? cpOffset + characters - 1 : cpOffset;

    // Default value for bounds_check_limit.
    // If set to this default sentinel, bounds_check_offset is computed based on
    // `cp_offset` and `characters` using `CalculateBoundsCheckOffset()`.
    public const int kDefaultBoundsCheckOffset = int.MinValue;

    public void LoadCurrentCharacter(int cpOffset, Label? onEndOfInput, bool checkBounds = true,
        int characters = 1, int boundsCheckOffset = kDefaultBoundsCheckOffset)
    {
        Debug.Assert(boundsCheckOffset >= 0 || boundsCheckOffset == kDefaultBoundsCheckOffset ||
                     boundsCheckOffset == cpOffset);
        if (boundsCheckOffset == kDefaultBoundsCheckOffset)
        {
            boundsCheckOffset = CalculateBoundsCheckOffset(cpOffset, characters);
        }
        LoadCurrentCharacterImpl(cpOffset, onEndOfInput, checkBounds, characters, boundsCheckOffset);
    }

    public abstract void LoadCurrentCharacterImpl(int cpOffset, Label? onEndOfInput, bool checkBounds,
        int characters, int boundsCheckOffset);
    public abstract void PopCurrentPosition();
    public abstract void PopRegister(int registerIndex);
    // Pushes the label on the backtrack stack, so that a following Backtrack
    // will go to this label. Always checks the backtrack stack limit.
    public abstract void PushBacktrack(Label label);
    public abstract void PushCurrentPosition();
    public abstract void PushRegister(int registerIndex, StackCheckFlag checkStackLimit);
    public abstract void ReadCurrentPositionFromRegister(int reg);
    public abstract void ReadStackPointerFromRegister(int reg);
    public abstract void SetCurrentPositionFromEnd(int by);
    public abstract void SetRegister(int registerIndex, int to);
    // Return whether the matching (with a global regexp) will be restarted.
    public abstract bool Succeed();
    public abstract void WriteCurrentPositionToRegister(int reg, int cpOffset);
    public abstract void ClearRegisters(int regFrom, int regTo);
    public abstract void WriteStackPointerToRegister(int reg);
    public abstract void RecordComment(string comment);

    // Check that we are not in the middle of a surrogate pair.
    public void CheckNotInSurrogatePair(int cpOffset, Label? onFailure)
    {
        var ok = new Label();
        // Check that current character is not a trail surrogate.
        LoadCurrentCharacter(cpOffset, ok);
        CheckCharacterNotInRange((char)kTrailSurrogateStart, (char)kTrailSurrogateEnd, ok);
        // Check that previous character is not a lead surrogate.
        LoadCurrentCharacter(cpOffset - 1, ok);
        CheckCharacterInRange((char)kLeadSurrogateStart, (char)kLeadSurrogateEnd, onFailure);
        Bind(ok);
    }

    // Step forward by 1 character/code point in the unanchored search loop.
    public void UnanchoredAdvance(bool unicode, Label? onFailure)
    {
        if (unicode && CurrentMode == Mode.UC16)
        {
            // 2-byte Unicode mode: Step forward by 1 code point dynamically.
            var isLeadSurrogate = new Label();
            var advance1Char = new Label();
            var advance2Chars = new Label();
            var done = new Label();
            LoadCurrentCharacter(0, onFailure, true);
            // Check if lead surrogate [0xD800, 0xDBFF]
            CheckCharacterInRange((char)kLeadSurrogateStart, (char)kLeadSurrogateEnd, isLeadSurrogate);
            // BMP character (non-lead-surrogate)
            AdvanceCurrentPosition(1);
            GoTo(done);

            Bind(isLeadSurrogate);
            LoadCurrentCharacter(1, advance1Char, true);
            // Check if trail surrogate [0xDC00, 0xDFFF]
            CheckCharacterInRange((char)kTrailSurrogateStart, (char)kTrailSurrogateEnd, advance2Chars);

            Bind(advance1Char);
            AdvanceCurrentPosition(1);
            GoTo(done);

            Bind(advance2Chars);
            AdvanceCurrentPosition(2);

            Bind(done);
        }
        else
        {
            // Non-Unicode or 1-byte: Step forward by 1 code unit without loading
            // character.
            CheckPosition(0, onFailure);
            AdvanceCurrentPosition(1);
        }
    }

    public abstract IrregexpImplementation Implementation();

    /// <summary>Compare two-byte strings case insensitively (non-unicode /i).</summary>
    public static bool CaseInsensitiveCompareNonUnicode(ReadOnlySpan<char> substring1, ReadOnlySpan<char> substring2)
    {
        int length = substring1.Length;
        for (int i = 0; i < length; i++)
        {
            int c1 = substring1[i];
            int c2 = substring2[i];
            if (c1 == c2) continue;
            if (CaseFolding.EquivalenceKey(c1, CaseFolding.Mode.kNonUnicode) !=
                CaseFolding.EquivalenceKey(c2, CaseFolding.Mode.kNonUnicode))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Compare two-byte strings case insensitively (/ui, /vi).</summary>
    public static bool CaseInsensitiveCompareUnicode(ReadOnlySpan<char> str1, ReadOnlySpan<char> str2)
    {
        // Canonicalize (ECMA-262 22.2.2.9.2) applies the simple case folding of
        // each code point separately.
        int length = str1.Length;
        int i1 = 0;
        int i2 = 0;
        while (i1 < length)
        {
            int c1 = Next16(str1, ref i1, length);
            int c2 = Next16(str2, ref i2, length);
            if (i1 != i2) return false;
            if (c1 == c2) continue;
            if (CaseFolding.EquivalenceKey(c1, CaseFolding.Mode.kUnicode) !=
                CaseFolding.EquivalenceKey(c2, CaseFolding.Mode.kUnicode))
            {
                return false;
            }
        }
        return true;
    }

    // U16_NEXT.
    static int Next16(ReadOnlySpan<char> s, ref int i, int length)
    {
        int c = s[i++];
        if (Utf16.IsLeadSurrogate(c) && i != length)
        {
            int c2 = s[i];
            if (Utf16.IsTrailSurrogate(c2))
            {
                ++i;
                c = Utf16.CombineSurrogatePair(c, c2);
            }
        }
        return c;
    }

    // Controls after how many backtracks irregexp should abort execution.  If it
    // can fall back to the experimental engine (see `set_can_fallback`), it will
    // return the appropriate error code, otherwise it will return the number of
    // matches found so far (perhaps none).
    public virtual void SetBacktrackLimit(uint backtrackLimit) => _backtrackLimit = backtrackLimit;

    // Set whether or not irregexp can fall back to the experimental engine on
    // excessive backtracking.  The number of backtracks considered excessive can
    // be controlled with set_backtrack_limit.
    public virtual void SetCanFallback(bool val) => _canFallback = val;

    // Whether any op touched the backtrack stack (a push, pop or stackpointer
    // transfer) while emitting the body.
    public bool BacktrackStackUsed => _backtrackStackUsed;

    // The fail label: the bottom-of-stack backtrack target that exhausting all
    // real backtracks pops to fail the match.
    public virtual void SetFailLabel(Label? label) => _failLabel = label;

    // Whether GetCode pushes the fail label from the entry prologue instead of
    // relying on the compiler's up-front PushBacktrack.
    public virtual bool ProloguePushesFailLabel => false;

    // Set whether the regular expression has the global flag.  Exiting due to
    // a failure in a global regexp may still mean success overall.
    public virtual void SetGlobalMode(GlobalMode mode) => _globalMode = mode;
    public bool Global => _globalMode != GlobalMode.NOT_GLOBAL;
    public bool GlobalWithZeroLengthCheck => _globalMode is GlobalMode.GLOBAL or GlobalMode.GLOBAL_UNICODE;
    public bool GlobalUnicode => _globalMode == GlobalMode.GLOBAL_UNICODE;

    // Byte size of chars in the string to match (decided by the Mode argument).
    protected int CharSize => (int)_mode;
    protected uint CharMask => _mode == Mode.LATIN1 ? 0xffu : 0xffffu;

    protected bool HasBacktrackLimit => _backtrackLimit != kNoBacktrackLimit;
    protected uint BacktrackLimit => _backtrackLimit;
    protected bool CanFallback => _canFallback;
    protected void SetBacktrackStackUsed() => _backtrackStackUsed = true;
    protected Label? FailLabel => _failLabel;

    // Which mode to generate code for (LATIN1 or UC16).
    public Mode CurrentMode => _mode;

    // Byte map of one byte characters with a 0xff if the character is a word
    // character (digit, letter or underscore) and 0x00 otherwise.
    public static ReadOnlySpan<byte> WordCharacterMap =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // '0' - '7'
        0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,  // '8' - '9'

        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 'A' - 'G'
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 'H' - 'O'
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 'P' - 'W'
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF,  // 'X' - 'Z', '_'

        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 'a' - 'g'
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 'h' - 'o'
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,  // 'p' - 'w'
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00,  // 'x' - 'z'
        // Latin-1 range
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>
    /// RegExpMacroAssembler::IsCharacterInRangeArray over the encoded range
    /// array [from0, to0, from1, to1, ..., fromN(, toN)] (from inclusive, to
    /// exclusive; an odd length means the last range is open-ended).
    /// </summary>
    public static bool IsCharacterInRangeArray(uint currentChar, ReadOnlySpan<ushort> ranges)
    {
        int rangesLen = ranges.Length;
        Debug.Assert(rangesLen >= 1);

        // Shortcut for fully out of range chars.
        if (currentChar < ranges[0]) return false;
        if (currentChar >= ranges[rangesLen - 1])
        {
            // The last range may be open-ended.
            return (rangesLen % 2) != 0;
        }

        // Binary search for the matching range.
        int mid, lower = 0, upper = rangesLen;
        do
        {
            mid = lower + (upper - lower) / 2;
            ushort elem = ranges[mid];
            if (currentChar < elem)
            {
                upper = mid;
            }
            else if (currentChar > elem)
            {
                lower = mid + 1;
            }
            else
            {
                break;
            }
        } while (lower < upper);

        bool currentCharGeLastElem = currentChar >= ranges[mid];
        int currentRangeStartIndex = currentCharGeLastElem ? mid : mid - 1;

        // Ranges start at even indices and end at odd indices.
        return (currentRangeStartIndex % 2) == 0;
    }
}

/// <summary>
/// The result codes of irregexp execution (NativeRegExpMacroAssembler::Result,
/// IrregexpInterpreter::Result, RegExp::kInternalRegExp*).
/// </summary>
public static class RegExpResult
{
    public const int kInternalRegExpFailure = 0;
    public const int kInternalRegExpSuccess = 1;
    public const int kInternalRegExpException = -1;
    public const int kInternalRegExpRetry = -2;
    public const int kInternalRegExpFallbackToExperimental = -3;
    public const int kInternalRegExpSmallestResult = -3;

    public const int RE_FAILURE = kInternalRegExpFailure;
    public const int RE_SUCCESS = kInternalRegExpSuccess;
    public const int RE_EXCEPTION = kInternalRegExpException;
    public const int RE_RETRY = kInternalRegExpRetry;
    public const int RE_FALLBACK_TO_EXPERIMENTAL = kInternalRegExpFallbackToExperimental;
}
