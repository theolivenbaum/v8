// Port of src/regexp/x64/regexp-macro-assembler-x64.h and
// src/regexp/x64/regexp-macro-assembler-x64.cc, together with the
// NativeRegExpMacroAssembler parts of src/regexp/regexp-macro-assembler.cc:
// V8's native irregexp tier, emitting .NET IL into a DynamicMethod instead of
// x64 machine code. The JIT then compiles the IL; DynamicMethods are
// collectible, so the code goes away with the CompiledRegExp that owns it.
//
// The machine model is the x64 one, register for register:
//
//   x64                              IL
//   rdi  current position            local `pos`: the absolute char index (V8
//                                    keeps a negative byte offset from the
//                                    subject end; only the encoding differs)
//   rdx  current character(s)        local `cc`, packed like V8 for 2/4-char
//                                    loads (LATIN1: 8 bits per char, UC16: 16)
//   rbx  backtrack stack pointer     locals `stack` (int[]) and `sp`
//   rbp-relative register file       one IL local per register (an int[] for
//                                    registers past kMaxLocalRegisters)
//   string start minus one           the constant -1 (positions are absolute)
//   kSuccessfulCapturesOffset        local `successCount`
//   kBacktrackCountOffset            local `backtrackCount`
//   kRegisterOutputOffset            local `outOffset` into the output span
//   backtrack_label_ / jmp rcx       an IL `switch` over the pushed label ids
//                                    (V8 pushes code offsets and jumps
//                                    indirectly; IL has no computed goto)
//
// The backtrack stack is an int[] with V8's limit handling: pushes that check
// the limit (PushBacktrack, PushCurrentPosition, PushRegister with
// kCheckStackLimit) grow it when fewer than kStackLimitSlackSlotCount slots are
// left, doubling up to RegExpStack::kMaximumStackSize (64 MB), past which the
// match returns EXCEPTION (V8: "Maximum call stack size exceeded").
//
// Deliberate differences from the x64 assembler:
//  - No CheckPreemption at backtracks and no JS stack guard check in the
//    prologue: there is no isolate to interrupt, and the register file lives in
//    IL locals, not on a stack V8 has to probe.
//  - The SIMD scans (SkipUntilChar/CharOrChar/CharAnd/BitInTable, taken under
//    the same *UseSimd predicates as x64) call a helper built on
//    MemoryExtensions.IndexOfAny with a SearchValues<char> set instead of
//    inline SSE code. The helper returns exactly where the scalar loop would
//    stop, so the tail loop V8 falls back to is never reached.
//  - SkipUntilOneOfMasked and SkipUntilOneOfMasked3 use the portable
//    RegExpMacroAssembler lowering (which still reaches the fast
//    SkipUntilBitInTable above).
//  - Back references, case-insensitive back references (also the LATIN1 one
//    x64 inlines) and range-array checks call C# helpers, as x64 calls C
//    functions for the two-byte and range-array cases.
//  - Subject reads go through ReadOnlySpan<char>'s bounds-checked indexer, so
//    a compiler bug surfaces as an IndexOutOfRangeException, never as an
//    out-of-bounds read.

using System.Buffers;
using System.Reflection;
using V8Sharp.RegExp.Unicode;
using DynamicMethod = System.Reflection.Emit.DynamicMethod;
using ILGenerator = System.Reflection.Emit.ILGenerator;
using ILLabel = System.Reflection.Emit.Label;
using LocalBuilder = System.Reflection.Emit.LocalBuilder;
using OpCode = System.Reflection.Emit.OpCode;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace V8Sharp.RegExp;

/// <summary>The signature of generated irregexp code.</summary>
internal delegate int RegExpILEntry(ReadOnlySpan<char> subject, int startIndex, Span<int> output);

/// <summary>
/// Native irregexp code (V8's Code object of kind REGEXP): a DynamicMethod
/// bound to this object, which carries the method's constant tables.
/// </summary>
public sealed class RegExpILCode
{
    internal RegExpILEntry? Entry;
    // Constants referenced by the generated code (loaded into locals in the
    // prologue). V8 embeds ByteArray/FixedUInt16Array handles in the code.
    internal byte[][] Tables = [];
    internal ushort[][] RangeArrays = [];
    internal SearchValues<char>[] SearchSets = [];

    internal RegExpILCode(RegExpMacroAssembler.Mode mode, int registersToSave)
    {
        Mode = mode;
        RegistersToSave = registersToSave;
    }

    /// <summary>The subject representation the code was generated for.</summary>
    public RegExpMacroAssembler.Mode Mode { get; }
    /// <summary>The number of capture registers written per match.</summary>
    public int RegistersToSave { get; }
    /// <summary>The number of registers (capture and internal) the code uses.</summary>
    public int RegisterCount { get; internal set; }
    /// <summary>The size of the generated IL in bytes.</summary>
    public int ILSize { get; internal set; }
    /// <summary>Whether the code uses the backtrack stack at all.</summary>
    public bool UsesBacktrackStack { get; internal set; }

    /// <summary>
    /// NativeRegExpMacroAssembler::Execute: runs the code on the subject from
    /// <paramref name="startIndex"/>. Returns SUCCESS (1), FAILURE (0),
    /// EXCEPTION (-1, backtrack stack overflow) or FALLBACK_TO_EXPERIMENTAL
    /// (-3); global code returns the number of matches written to
    /// <paramref name="output"/>. LATIN1 code requires every code unit of the
    /// subject to be at most 0xFF.
    /// </summary>
    public int Execute(ReadOnlySpan<char> subject, int startIndex, Span<int> output)
    {
        if ((uint)startIndex > (uint)subject.Length) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (output.Length < RegistersToSave) throw new ArgumentException("output span too small", nameof(output));
        Debug.Assert(Mode == RegExpMacroAssembler.Mode.UC16 || CompiledRegExp.IsOneByteSubject(subject));
        return Entry!(subject, startIndex, output);
    }
}

/// <summary>Runtime support called from generated code.</summary>
internal static class RegExpILRuntime
{
    /// <summary>The initial backtrack stack (V8: RegExpStack::kMinimumDynamicStackSize, in slots).</summary>
    public const int kInitialStackSize = 1024;
    /// <summary>RegExpStack::kMaximumStackSize (64 MB) in int slots.</summary>
    public const int kMaximumStackSize = 64 * 1024 * 1024 / sizeof(int);
    // Stacks larger than this are not kept for reuse.
    const int kMaxCachedStackSize = 1 << 16;

    [ThreadStatic] static int[]? t_cachedStack;

    // RegExpStack's thread-local memory: generated code takes the cached
    // stack on entry and hands it back on exit.
    public static int[] RentStack()
    {
        int[]? stack = t_cachedStack;
        if (stack is null) return new int[kInitialStackSize];
        t_cachedStack = null;
        return stack;
    }

    public static void ReturnStack(int[]? stack)
    {
        if (stack is not null && stack.Length <= kMaxCachedStackSize) t_cachedStack = stack;
    }

    // NativeRegExpMacroAssembler::GrowStack: doubles the stack, or returns
    // null past kMaximumStackSize.
    public static int[]? GrowStack(int[] stack)
    {
        long newSize = (long)stack.Length * 2;
        if (newSize > kMaximumStackSize) return null;
        int[] bigger = new int[newSize];
        Array.Copy(stack, bigger, stack.Length);
        return bigger;
    }

    // RegExpMacroAssembler::word_character_map.
    public static readonly byte[] WordCharacterMap = RegExpMacroAssembler.WordCharacterMap.ToArray();

    public static bool IsCharacterInRangeArray(int currentChar, ushort[] ranges) =>
        RegExpMacroAssembler.IsCharacterInRangeArray((uint)currentChar, ranges);

    // CheckNotBackReference: returns the position after the match (before it
    // when reading backward), the unchanged position when the capture is empty
    // or cleared, or -1 when the back reference does not match.
    public static int BackReference(ReadOnlySpan<char> subject, int from, int end, int position, bool readBackward)
    {
        int length = end - from;
        if (from < 0 || length <= 0) return position;
        int matchStart;
        if (readBackward)
        {
            matchStart = position - length;
            if (matchStart < 0) return -1;
        }
        else
        {
            if (position + length > subject.Length) return -1;
            matchStart = position;
        }
        if (!subject.Slice(from, length).SequenceEqual(subject.Slice(matchStart, length))) return -1;
        return readBackward ? matchStart : position + length;
    }

    public const int kIgnoreCaseLatin1 = 0;
    public const int kIgnoreCaseNonUnicode = 1;
    public const int kIgnoreCaseUnicode = 2;

    // CheckNotBackReferenceIgnoreCase, with the same results as BackReference.
    public static int BackReferenceIgnoreCase(ReadOnlySpan<char> subject, int from, int end, int position,
        bool readBackward, int mode)
    {
        int length = end - from;
        if (from < 0 || length <= 0) return position;
        int matchStart;
        if (readBackward)
        {
            matchStart = position - length;
            if (matchStart < 0) return -1;
        }
        else
        {
            if (position + length > subject.Length) return -1;
            matchStart = position;
        }
        ReadOnlySpan<char> capture = subject.Slice(from, length);
        ReadOnlySpan<char> input = subject.Slice(matchStart, length);
        bool equal = mode switch
        {
            kIgnoreCaseLatin1 => EqualsIgnoreCaseLatin1(capture, input),
            kIgnoreCaseNonUnicode => RegExpMacroAssembler.CaseInsensitiveCompareNonUnicode(capture, input),
            _ => RegExpMacroAssembler.CaseInsensitiveCompareUnicode(capture, input),
        };
        if (!equal) return -1;
        return readBackward ? matchStart : position + length;
    }

    // The inline LATIN1 loop of RegExpMacroAssemblerX64::
    // CheckNotBackReferenceIgnoreCase: letters are equal if or-ing with 0x20
    // makes them equal and in range 'a'-'z' or [224,254] except 247.
    static bool EqualsIgnoreCaseLatin1(ReadOnlySpan<char> capture, ReadOnlySpan<char> input)
    {
        for (int i = 0; i < capture.Length; i++)
        {
            int c = capture[i];
            int a = input[i];
            if (a == c) continue;
            a |= 0x20;
            c |= 0x20;
            if (a != c) return false;
            if ((uint)(a - 'a') <= 'z' - 'a') continue;
            if ((uint)(a - 224) > 254 - 224 || a == 247) return false;
        }
        return true;
    }

    // The SIMD SkipUntil* scans (advance_by 1): the first position p >= pos
    // whose char at p + cpOffset is in the set, while p + boundsCheckOffset
    // stays inside the subject. Returns p, or ~p for the position at which the
    // bounds check fails. Requires 0 <= cpOffset <= boundsCheckOffset.
    public static int SkipUntil(ReadOnlySpan<char> subject, int position, int cpOffset, int boundsCheckOffset,
        SearchValues<char> set)
    {
        int limit = subject.Length - boundsCheckOffset;
        if (position >= limit) return ~position;
        int i = subject.Slice(position + cpOffset, limit - position).IndexOfAny(set);
        return i >= 0 ? position + i : ~limit;
    }
}

/// <summary>
/// RegExpMacroAssemblerX64, emitting IL. GetCode returns a
/// <see cref="RegExpILCode"/>.
/// </summary>
public sealed class RegExpMacroAssemblerIL : RegExpMacroAssembler
{
    // Registers with a higher index live in an int[] allocated per execution.
    const int kMaxLocalRegisters = 1024;

    const int kArgSubject = 1;
    const int kArgOutput = 3;

    const BindingFlags kStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags kInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static readonly MethodInfo s_subjectItem = typeof(ReadOnlySpan<char>).GetMethod("get_Item")!;
    static readonly MethodInfo s_subjectLength = typeof(ReadOnlySpan<char>).GetMethod("get_Length")!;
    static readonly MethodInfo s_outputItem = typeof(Span<int>).GetMethod("get_Item")!;
    static readonly MethodInfo s_outputLength = typeof(Span<int>).GetMethod("get_Length")!;
    static readonly FieldInfo s_tablesField = typeof(RegExpILCode).GetField(nameof(RegExpILCode.Tables), kInstance)!;
    static readonly FieldInfo s_rangeArraysField =
        typeof(RegExpILCode).GetField(nameof(RegExpILCode.RangeArrays), kInstance)!;
    static readonly FieldInfo s_searchSetsField =
        typeof(RegExpILCode).GetField(nameof(RegExpILCode.SearchSets), kInstance)!;
    static readonly FieldInfo s_wordMapField =
        typeof(RegExpILRuntime).GetField(nameof(RegExpILRuntime.WordCharacterMap), kStatic)!;
    static readonly MethodInfo s_rentStack = typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.RentStack), kStatic)!;
    static readonly MethodInfo s_returnStack =
        typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.ReturnStack), kStatic)!;
    static readonly MethodInfo s_growStack = typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.GrowStack), kStatic)!;
    static readonly MethodInfo s_inRangeArray =
        typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.IsCharacterInRangeArray), kStatic)!;
    static readonly MethodInfo s_backReference =
        typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.BackReference), kStatic)!;
    static readonly MethodInfo s_backReferenceIgnoreCase =
        typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.BackReferenceIgnoreCase), kStatic)!;
    static readonly MethodInfo s_skipUntil = typeof(RegExpILRuntime).GetMethod(nameof(RegExpILRuntime.SkipUntil), kStatic)!;
    static readonly ConstructorInfo s_invalidOperationCtor =
        typeof(InvalidOperationException).GetConstructor([typeof(string)])!;

    readonly RegExpILCode _code;
    readonly DynamicMethod _method;
    readonly ILGenerator _il;
    readonly int _numSavedRegisters;
    int _numRegisters;
    // Positions for the compiler-visible Label state (bound/linked).
    int _labelPos;

    readonly Dictionary<Label, ILLabel> _labels = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Label, int> _backtrackIds = new(ReferenceEqualityComparer.Instance);
    readonly List<Label> _backtrackTargets = [];

    readonly ILLabel _entryLabel;
    readonly ILLabel _startLabel;
    readonly ILLabel _successLabel;
    readonly ILLabel _backtrackLabel;
    readonly ILLabel _exitLabel;
    readonly ILLabel _returnLabel;
    readonly ILLabel _exceptionLabel;
    readonly ILLabel _fallbackLabel;
    readonly ILLabel _loadCharStartRegExp;
    bool _successUsed;
    bool _backtrackUsed;
    bool _exceptionUsed;
    bool _fallbackUsed;

    readonly LocalBuilder _pos;
    readonly LocalBuilder _cc;
    readonly LocalBuilder _length;
    readonly LocalBuilder _stack;
    readonly LocalBuilder _sp;
    readonly LocalBuilder _stackLimit;
    readonly LocalBuilder _result;
    readonly LocalBuilder _successCount;
    readonly LocalBuilder _outOffset;
    readonly LocalBuilder _backtrackCount;
    readonly LocalBuilder _tmp;
    readonly LocalBuilder _switchIndex;
    readonly LocalBuilder _registerArray;
    readonly List<LocalBuilder?> _registerLocals = [];

    readonly List<byte[]> _tables = [];
    readonly List<LocalBuilder> _tableLocals = [];
    readonly Dictionary<byte[], int> _tableIndex = new(ReferenceEqualityComparer.Instance);
    readonly List<ushort[]> _rangeArrays = [];
    readonly List<LocalBuilder> _rangeArrayLocals = [];
    readonly Dictionary<string, int> _rangeArrayIndex = new(StringComparer.Ordinal);
    readonly List<SearchValues<char>> _searchSets = [];
    readonly List<LocalBuilder> _searchSetLocals = [];
    readonly Dictionary<string, int> _searchSetIndex = new(StringComparer.Ordinal);

    public RegExpMacroAssemblerIL(Mode mode, int registersToSave) : base(mode)
    {
        Debug.Assert(registersToSave % 2 == 0);
        _numSavedRegisters = registersToSave;
        _numRegisters = registersToSave;
        _code = new RegExpILCode(mode, registersToSave);
        _method = new DynamicMethod(mode == Mode.LATIN1 ? "irregexp_latin1" : "irregexp_uc16", typeof(int),
            [typeof(RegExpILCode), typeof(ReadOnlySpan<char>), typeof(int), typeof(Span<int>)],
            typeof(RegExpMacroAssemblerIL).Module, skipVisibility: true);
        _il = _method.GetILGenerator(1024);

        _pos = _il.DeclareLocal(typeof(int));
        _cc = _il.DeclareLocal(typeof(int));
        _length = _il.DeclareLocal(typeof(int));
        _stack = _il.DeclareLocal(typeof(int[]));
        _sp = _il.DeclareLocal(typeof(int));
        _stackLimit = _il.DeclareLocal(typeof(int));
        _result = _il.DeclareLocal(typeof(int));
        _successCount = _il.DeclareLocal(typeof(int));
        _outOffset = _il.DeclareLocal(typeof(int));
        _backtrackCount = _il.DeclareLocal(typeof(int));
        _tmp = _il.DeclareLocal(typeof(int));
        _switchIndex = _il.DeclareLocal(typeof(int));
        _registerArray = _il.DeclareLocal(typeof(int[]));

        _entryLabel = _il.DefineLabel();
        _startLabel = _il.DefineLabel();
        _successLabel = _il.DefineLabel();
        _backtrackLabel = _il.DefineLabel();
        _exitLabel = _il.DefineLabel();
        _returnLabel = _il.DefineLabel();
        _exceptionLabel = _il.DefineLabel();
        _fallbackLabel = _il.DefineLabel();
        _loadCharStartRegExp = _il.DefineLabel();

        _il.Emit(OpCodes.Br, _entryLabel);  // We'll write the entry code when we know more.
        _il.MarkLabel(_startLabel);  // And then continue from here.
    }

    public override IrregexpImplementation Implementation() => IrregexpImplementation.kILImplementation;

    // The prologue pushes the fail label only if the body ended up using the
    // backtrack stack (see RegExpCompiler.Assemble).
    public override bool ProloguePushesFailLabel => true;

    // --- Emission helpers ---

    void Ldc(int value)
    {
        switch (value)
        {
            case -1: _il.Emit(OpCodes.Ldc_I4_M1); return;
            case 0: _il.Emit(OpCodes.Ldc_I4_0); return;
            case 1: _il.Emit(OpCodes.Ldc_I4_1); return;
            case 2: _il.Emit(OpCodes.Ldc_I4_2); return;
            case 3: _il.Emit(OpCodes.Ldc_I4_3); return;
            case 4: _il.Emit(OpCodes.Ldc_I4_4); return;
            case 5: _il.Emit(OpCodes.Ldc_I4_5); return;
            case 6: _il.Emit(OpCodes.Ldc_I4_6); return;
            case 7: _il.Emit(OpCodes.Ldc_I4_7); return;
            case 8: _il.Emit(OpCodes.Ldc_I4_8); return;
        }
        if (value is >= sbyte.MinValue and <= sbyte.MaxValue) _il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
        else _il.Emit(OpCodes.Ldc_I4, value);
    }

    void Ld(LocalBuilder local) => _il.Emit(OpCodes.Ldloc, local);
    void St(LocalBuilder local) => _il.Emit(OpCodes.Stloc, local);

    ILLabel ILLabelFor(Label label)
    {
        if (!_labels.TryGetValue(label, out ILLabel il))
        {
            il = _il.DefineLabel();
            _labels.Add(label, il);
        }
        return il;
    }

    // A reference to a label: keeps the compiler-visible linked state (the
    // compiler asks Label.IsLinked to find out whether a label was used).
    ILLabel Ref(Label label)
    {
        if (!label.IsBound) label.LinkTo(_labelPos++);
        return ILLabelFor(label);
    }

    // BranchOrBacktrack's target: the label, or the backtrack code for null.
    ILLabel Target(Label? label)
    {
        if (label is null)
        {
            _backtrackUsed = true;
            return _backtrackLabel;
        }
        return Ref(label);
    }

    void BranchOrBacktrack(OpCode branch, Label? to) => _il.Emit(branch, Target(to));

    void TouchRegister(int register)
    {
        Debug.Assert(register >= 0 && register < (1 << 30));
        if (register >= _numRegisters) _numRegisters = register + 1;
    }

    LocalBuilder RegisterLocal(int register)
    {
        while (_registerLocals.Count <= register) _registerLocals.Add(null);
        return _registerLocals[register] ??= _il.DeclareLocal(typeof(int));
    }

    void LdReg(int register)
    {
        TouchRegister(register);
        if (register < kMaxLocalRegisters)
        {
            Ld(RegisterLocal(register));
        }
        else
        {
            Ld(_registerArray);
            Ldc(register - kMaxLocalRegisters);
            _il.Emit(OpCodes.Ldelem_I4);
        }
    }

    // Storing a register: BeginStReg, push the value, EndStReg.
    void BeginStReg(int register)
    {
        TouchRegister(register);
        if (register >= kMaxLocalRegisters)
        {
            Ld(_registerArray);
            Ldc(register - kMaxLocalRegisters);
        }
    }

    void EndStReg(int register)
    {
        if (register < kMaxLocalRegisters) St(RegisterLocal(register));
        else _il.Emit(OpCodes.Stelem_I4);
    }

    // Pushes subject[pos + cpOffset] onto the evaluation stack.
    void LdChar(int cpOffset)
    {
        _il.Emit(OpCodes.Ldarga_S, (byte)kArgSubject);
        Ld(_pos);
        if (cpOffset != 0)
        {
            Ldc(cpOffset);
            _il.Emit(OpCodes.Add);
        }
        _il.Emit(OpCodes.Call, s_subjectItem);
        _il.Emit(OpCodes.Ldind_U2);
    }

    void LoadCurrentCharacterUnchecked(int cpOffset, int characters)
    {
        int bitsPerChar = CurrentMode == Mode.LATIN1 ? 8 : 16;
        Debug.Assert(characters == 1 || characters == 2 || (characters == 4 && CurrentMode == Mode.LATIN1));
        LdChar(cpOffset);
        for (int i = 1; i < characters; i++)
        {
            LdChar(cpOffset + i);
            Ldc(i * bitsPerChar);
            _il.Emit(OpCodes.Shl);
            _il.Emit(OpCodes.Or);
        }
        St(_cc);
    }

    // Push(value): BeginPush, push the value, EndPush.
    void BeginPush()
    {
        SetBacktrackStackUsed();
        Ld(_stack);
        Ld(_sp);
    }

    void EndPush()
    {
        _il.Emit(OpCodes.Stelem_I4);
        Ld(_sp);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Add);
        St(_sp);
    }

    // Pushes the popped value onto the evaluation stack.
    void Pop()
    {
        SetBacktrackStackUsed();
        Ld(_stack);
        Ld(_sp);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Sub);
        _il.Emit(OpCodes.Dup);
        St(_sp);
        _il.Emit(OpCodes.Ldelem_I4);
    }

    void Drop()
    {
        SetBacktrackStackUsed();
        Ld(_sp);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Sub);
        St(_sp);
    }

    // CheckStackLimit + the stack_overflow_label_ code (GrowStack).
    void CheckStackLimit()
    {
        ILLabel noStackOverflow = _il.DefineLabel();
        Ld(_sp);
        Ld(_stackLimit);
        _il.Emit(OpCodes.Blt, noStackOverflow);
        Ld(_stack);
        _il.Emit(OpCodes.Call, s_growStack);
        St(_stack);
        Ld(_stack);
        _exceptionUsed = true;
        _il.Emit(OpCodes.Brfalse, _exceptionLabel);
        SetStackLimit();
        _il.MarkLabel(noStackOverflow);
    }

    void SetStackLimit()
    {
        Ld(_stack);
        _il.Emit(OpCodes.Ldlen);
        _il.Emit(OpCodes.Conv_I4);
        Ldc(kStackLimitSlackSlotCount);
        _il.Emit(OpCodes.Sub);
        St(_stackLimit);
    }

    LocalBuilder TableLocal(byte[] table)
    {
        if (!_tableIndex.TryGetValue(table, out int index))
        {
            index = _tables.Count;
            _tables.Add(table);
            _tableLocals.Add(_il.DeclareLocal(typeof(byte[])));
            _tableIndex.Add(table, index);
        }
        return _tableLocals[index];
    }

    // NativeRegExpMacroAssembler::GetOrAddRangeArray / MakeRangeArray.
    LocalBuilder RangeArrayLocal(List<CharacterRange> ranges)
    {
        int rangesLength = ranges.Count;
        Debug.Assert(rangesLength > 0);
        bool openEnded = (ranges[rangesLength - 1].To & 0xffff) == 0xffff;
        ushort[] rangeArray = new ushort[openEnded ? rangesLength * 2 - 1 : rangesLength * 2];
        for (int i = 0; i < rangesLength; i++)
        {
            CharacterRange r = ranges[i];
            Debug.Assert(r.From <= 0xffff);
            rangeArray[i * 2] = (ushort)r.From;
            // CharacterRanges may use 0x10ffff as the end-of-range marker
            // irrespective of whether the regexp IsUnicode or not; translate
            // the marker value here (MaskEndOfRangeMarker).
            int to = r.To & 0xffff;
            if (i == rangesLength - 1 && to == 0xffff) break;  // Leave the last range open-ended.
            rangeArray[i * 2 + 1] = (ushort)(to + 1);  // Exclusive.
        }
        string key = new(Array.ConvertAll(rangeArray, static c => (char)c));
        if (!_rangeArrayIndex.TryGetValue(key, out int index))
        {
            index = _rangeArrays.Count;
            _rangeArrays.Add(rangeArray);
            _rangeArrayLocals.Add(_il.DeclareLocal(typeof(ushort[])));
            _rangeArrayIndex.Add(key, index);
        }
        return _rangeArrayLocals[index];
    }

    LocalBuilder SearchSetLocal(List<char> chars)
    {
        string key = new(chars.ToArray());
        if (!_searchSetIndex.TryGetValue(key, out int index))
        {
            index = _searchSets.Count;
            _searchSets.Add(SearchValues.Create(key));
            _searchSetLocals.Add(_il.DeclareLocal(typeof(SearchValues<char>)));
            _searchSetIndex.Add(key, index);
        }
        return _searchSetLocals[index];
    }

    // --- RegExpMacroAssembler ---

    public override void AdvanceCurrentPosition(int by)
    {
        if (by == 0) return;
        Ld(_pos);
        Ldc(by);
        _il.Emit(OpCodes.Add);
        St(_pos);
    }

    public override void AdvanceRegister(int reg, int by)
    {
        Debug.Assert(reg >= 0);
        if (by == 0) return;
        BeginStReg(reg);
        LdReg(reg);
        Ldc(by);
        _il.Emit(OpCodes.Add);
        EndStReg(reg);
    }

    public override void Backtrack()
    {
        // Emitted in GetCode (EmitBacktrack): only once the body is fully
        // emitted is it known whether anything was ever pushed.
        _backtrackUsed = true;
        _il.Emit(OpCodes.Br, _backtrackLabel);
    }

    // EmitBacktrack, without CheckPreemption.
    void EmitBacktrack()
    {
        if (HasBacktrackLimit)
        {
            ILLabel next = _il.DefineLabel();
            Ld(_backtrackCount);
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Add);
            _il.Emit(OpCodes.Dup);
            St(_backtrackCount);
            Ldc(unchecked((int)BacktrackLimit));
            _il.Emit(OpCodes.Bne_Un, next);

            // Backtrack limit exceeded.
            if (CanFallback)
            {
                _fallbackUsed = true;
                _il.Emit(OpCodes.Br, _fallbackLabel);
            }
            else
            {
                // Can't fallback, so we treat it as a failed match.
                Fail();
            }
            _il.MarkLabel(next);
        }
        // Pop the target id from the backtrack stack and dispatch on it.
        Pop();
        var targets = new ILLabel[_backtrackTargets.Count];
        for (int i = 0; i < targets.Length; i++) targets[i] = ILLabelFor(_backtrackTargets[i]);
        _il.Emit(OpCodes.Switch, targets);
        _il.Emit(OpCodes.Ldstr, "invalid irregexp backtrack target");
        _il.Emit(OpCodes.Newobj, s_invalidOperationCtor);
        _il.Emit(OpCodes.Throw);
    }

    public override void Bind(Label label)
    {
        Debug.Assert(!label.IsBound);
        _il.MarkLabel(ILLabelFor(label));
        label.BindTo(_labelPos++);
    }

    public override void CheckCharacter(uint c, Label? onEqual)
    {
        Ld(_cc);
        Ldc(unchecked((int)c));
        BranchOrBacktrack(OpCodes.Beq, onEqual);
    }

    public override void CheckCharacterGT(char limit, Label? onGreater)
    {
        Ld(_cc);
        Ldc(limit);
        BranchOrBacktrack(OpCodes.Bgt, onGreater);
    }

    public override void CheckAtStart(int cpOffset, Label? onAtStart)
    {
        // pos + cp_offset - 1 == string start minus one.
        Ld(_pos);
        Ldc(cpOffset);
        _il.Emit(OpCodes.Add);
        BranchOrBacktrack(OpCodes.Brfalse, onAtStart);
    }

    public override void CheckNotAtStart(int cpOffset, Label? onNotAtStart)
    {
        Ld(_pos);
        Ldc(cpOffset);
        _il.Emit(OpCodes.Add);
        BranchOrBacktrack(OpCodes.Brtrue, onNotAtStart);
    }

    public override void CheckCharacterLT(char limit, Label? onLess)
    {
        Ld(_cc);
        Ldc(limit);
        BranchOrBacktrack(OpCodes.Blt, onLess);
    }

    public override void CheckFixedLengthLoop(Label? onTosEqualsCurrentPosition)
    {
        SetBacktrackStackUsed();
        ILLabel fallthrough = _il.DefineLabel();
        Ld(_pos);
        Ld(_stack);
        Ld(_sp);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Sub);
        _il.Emit(OpCodes.Ldelem_I4);
        _il.Emit(OpCodes.Bne_Un, fallthrough);
        Drop();
        GoTo(onTosEqualsCurrentPosition);
        _il.MarkLabel(fallthrough);
    }

    public override void CheckNotBackReferenceIgnoreCase(int startReg, bool readBackward, bool unicode,
        Label? onNoMatch)
    {
        int mode = CurrentMode == Mode.LATIN1 ? RegExpILRuntime.kIgnoreCaseLatin1
            : unicode ? RegExpILRuntime.kIgnoreCaseUnicode : RegExpILRuntime.kIgnoreCaseNonUnicode;
        _il.Emit(OpCodes.Ldarg_1);
        LdReg(startReg);
        LdReg(startReg + 1);
        Ld(_pos);
        Ldc(readBackward ? 1 : 0);
        Ldc(mode);
        _il.Emit(OpCodes.Call, s_backReferenceIgnoreCase);
        EmitBackReferenceResult(onNoMatch);
    }

    public override void CheckNotBackReference(int startReg, bool readBackward, Label? onNoMatch)
    {
        _il.Emit(OpCodes.Ldarg_1);
        LdReg(startReg);
        LdReg(startReg + 1);
        Ld(_pos);
        Ldc(readBackward ? 1 : 0);
        _il.Emit(OpCodes.Call, s_backReference);
        EmitBackReferenceResult(onNoMatch);
    }

    void EmitBackReferenceResult(Label? onNoMatch)
    {
        St(_tmp);
        Ld(_tmp);
        _il.Emit(OpCodes.Ldc_I4_0);
        BranchOrBacktrack(OpCodes.Blt, onNoMatch);
        Ld(_tmp);
        St(_pos);
    }

    public override void CheckNotCharacter(uint c, Label? onNotEqual)
    {
        Ld(_cc);
        Ldc(unchecked((int)c));
        BranchOrBacktrack(OpCodes.Bne_Un, onNotEqual);
    }

    void EmitAnd(uint mask)
    {
        Ld(_cc);
        Ldc(unchecked((int)mask));
        _il.Emit(OpCodes.And);
    }

    public override void CheckCharacterAfterAnd(uint c, uint andWith, Label? onEqual)
    {
        EmitAnd(andWith);
        Ldc(unchecked((int)c));
        BranchOrBacktrack(OpCodes.Beq, onEqual);
    }

    public override void CheckNotCharacterAfterAnd(uint c, uint andWith, Label? onNotEqual)
    {
        EmitAnd(andWith);
        Ldc(unchecked((int)c));
        BranchOrBacktrack(OpCodes.Bne_Un, onNotEqual);
    }

    public override void CheckNotCharacterAfterMinusAnd(char c, char minus, char andWith, Label? onNotEqual)
    {
        Ld(_cc);
        Ldc(minus);
        _il.Emit(OpCodes.Sub);
        Ldc(andWith);
        _il.Emit(OpCodes.And);
        Ldc(c);
        BranchOrBacktrack(OpCodes.Bne_Un, onNotEqual);
    }

    // (cc - from) compared unsigned with (to - from).
    void EmitRangeCompare(char from, char to)
    {
        Ld(_cc);
        if (from != 0)
        {
            Ldc(from);
            _il.Emit(OpCodes.Sub);
        }
        Ldc(to - from);
    }

    public override void CheckCharacterInRange(char from, char to, Label? onInRange)
    {
        EmitRangeCompare(from, to);
        BranchOrBacktrack(OpCodes.Ble_Un, onInRange);
    }

    public override void CheckCharacterNotInRange(char from, char to, Label? onNotInRange)
    {
        EmitRangeCompare(from, to);
        BranchOrBacktrack(OpCodes.Bgt_Un, onNotInRange);
    }

    void CallIsCharacterInRangeArray(List<CharacterRange> ranges)
    {
        Ld(_cc);
        Ld(RangeArrayLocal(ranges));
        _il.Emit(OpCodes.Call, s_inRangeArray);
    }

    public override bool CheckCharacterInRangeArray(List<CharacterRange> ranges, Label? onInRange)
    {
        CallIsCharacterInRangeArray(ranges);
        BranchOrBacktrack(OpCodes.Brtrue, onInRange);
        return true;
    }

    public override bool CheckCharacterNotInRangeArray(List<CharacterRange> ranges, Label? onNotInRange)
    {
        CallIsCharacterInRangeArray(ranges);
        BranchOrBacktrack(OpCodes.Brfalse, onNotInRange);
        return true;
    }

    public override void CheckBitInTable(byte[] table, Label? onBitSet)
    {
        Ld(TableLocal(table));
        Ld(_cc);
        Ldc(kTableMask);
        _il.Emit(OpCodes.And);
        _il.Emit(OpCodes.Ldelem_U1);
        BranchOrBacktrack(OpCodes.Brtrue, onBitSet);
    }

    // The SIMD scan: see RegExpILRuntime.SkipUntil. Emits nothing (leaving the
    // caller's scalar loop to do the work) when the offsets are outside what
    // the helper handles.
    void EmitSkipUntilSet(int cpOffset, int boundsCheckOffset, List<char> chars, Label? onMatch, Label? onNoMatch)
    {
        if (cpOffset < 0 || boundsCheckOffset < cpOffset) return;
        LocalBuilder set = SearchSetLocal(chars);
        ILLabel notFound = _il.DefineLabel();
        _il.Emit(OpCodes.Ldarg_1);
        Ld(_pos);
        Ldc(cpOffset);
        Ldc(boundsCheckOffset);
        Ld(set);
        _il.Emit(OpCodes.Call, s_skipUntil);
        St(_tmp);
        Ld(_tmp);
        _il.Emit(OpCodes.Ldc_I4_0);
        _il.Emit(OpCodes.Blt, notFound);
        Ld(_tmp);
        St(_pos);
        LoadCurrentCharacterUnchecked(cpOffset, 1);
        _il.Emit(OpCodes.Br, Target(onMatch));
        _il.MarkLabel(notFound);
        Ld(_tmp);
        _il.Emit(OpCodes.Not);
        St(_pos);
        _il.Emit(OpCodes.Br, Target(onNoMatch));
    }

    List<char> CharsMatching(Func<int, bool> predicate)
    {
        var chars = new List<char>();
        int max = (int)CharMask;
        for (int c = 0; c <= max; c++)
        {
            if (predicate(c)) chars.Add((char)c);
        }
        return chars;
    }

    public override void SkipUntilBitInTable(int cpOffset, byte[] table, byte[]? nibbleTable, int advanceBy,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch)
    {
        void EmitScalarCheck()
        {
            CheckPosition(boundsCheckOffset, onNoMatch);
            LoadCurrentCharacterUnchecked(cpOffset, 1);
            CheckBitInTable(table, onMatch);
            AdvanceCurrentPosition(advanceBy);
        }

        if (SkipUntilBitInTableUseSimd(advanceBy))
        {
            // Scalar check for the first position to avoid SIMD setup overhead if we
            // find a potential match immediately.
            EmitScalarCheck();
            EmitSkipUntilSet(cpOffset, boundsCheckOffset, CharsMatching(c => table[c & kTableMask] != 0), onMatch,
                onNoMatch);
        }
        var scalarRepeat = new Label();
        Bind(scalarRepeat);
        EmitScalarCheck();
        GoTo(scalarRepeat);
    }

    public override bool SkipUntilBitInTableUseSimd(int advanceBy) => advanceBy * CharSize == 1;

    public override bool SkipUntilCharAndUseSimd(int advanceBy) => advanceBy == 1;

    public override void SkipUntilCharAndSimd(int cpOffset, int advanceBy, uint character, uint mask,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch) =>
        EmitSkipUntilSet(cpOffset, boundsCheckOffset, CharsMatching(c => ((uint)c & mask) == character), onMatch,
            onNoMatch);

    public override bool SkipUntilCharUseSimd(int advanceBy) => advanceBy == 1;

    public override void SkipUntilCharSimd(int cpOffset, int advanceBy, uint character, int boundsCheckOffset,
        Label? onMatch, Label? onNoMatch) =>
        EmitSkipUntilSet(cpOffset, boundsCheckOffset,
            character <= CharMask ? new List<char> { (char)character } : new List<char>(), onMatch, onNoMatch);

    public override bool SkipUntilCharOrCharUseSimd(int advanceBy) => advanceBy == 1;

    public override void SkipUntilCharOrCharSimd(int cpOffset, int advanceBy, uint char1, uint char2,
        int boundsCheckOffset, Label? onMatch, Label? onNoMatch)
    {
        var chars = new List<char>(2);
        if (char1 <= CharMask) chars.Add((char)char1);
        if (char2 <= CharMask && char2 != char1) chars.Add((char)char2);
        EmitSkipUntilSet(cpOffset, boundsCheckOffset, chars, onMatch, onNoMatch);
    }

    public override bool CanTableSwitchOnBits() => true;

    public override void TableSwitchOnBits(int shift, int tableSize, Label table)
    {
        Debug.Assert((tableSize & (tableSize - 1)) == 0);
        Ld(_cc);
        if (shift > 0)
        {
            Ldc(shift);
            _il.Emit(OpCodes.Shr_Un);
        }
        Ldc(tableSize - 1);
        _il.Emit(OpCodes.And);
        St(_switchIndex);
        _il.Emit(OpCodes.Br, Ref(table));
    }

    public override void EmitTableSwitchTable(Label table, Label?[] targets)
    {
        // The caller guarantees the preceding code ends with an unconditional
        // control transfer and that every target is already bound.
        Bind(table);
        var ilTargets = new ILLabel[targets.Length];
        for (int i = 0; i < targets.Length; i++) ilTargets[i] = Target(targets[i]);
        Ld(_switchIndex);
        _il.Emit(OpCodes.Switch, ilTargets);
        // Every index is covered; this is unreachable.
        _il.Emit(OpCodes.Ldstr, "invalid irregexp table switch index");
        _il.Emit(OpCodes.Newobj, s_invalidOperationCtor);
        _il.Emit(OpCodes.Throw);
    }

    public override void CheckPosition(int cpOffset, Label? onOutsideInput)
    {
        Ld(_pos);
        if (cpOffset != 0)
        {
            Ldc(cpOffset);
            _il.Emit(OpCodes.Add);
        }
        if (cpOffset >= 0)
        {
            Ld(_length);
            BranchOrBacktrack(OpCodes.Bge, onOutsideInput);
        }
        else
        {
            _il.Emit(OpCodes.Ldc_I4_0);
            BranchOrBacktrack(OpCodes.Blt, onOutsideInput);
        }
    }

    public override void CheckSpecialClassRanges(StandardCharacterSet type, Label? onNoMatch)
    {
        Debug.Assert(CanOptimizeSpecialClassRanges(type));
        // Range checks (c in min..max) are generally implemented by an unsigned
        // (c - min) <= (max - min) check.
        switch (type)
        {
            case StandardCharacterSet.kWhitespace:
            {
                // Match space-characters.
                Debug.Assert(CurrentMode == Mode.LATIN1);
                // One byte space characters are '\t'..'\r', ' ' and  .
                ILLabel success = _il.DefineLabel();
                Ld(_cc);
                Ldc(' ');
                _il.Emit(OpCodes.Beq, success);
                // Check range 0x09..0x0D.
                EmitRangeCompare('\t', '\r');
                _il.Emit(OpCodes.Ble_Un, success);
                //   (NBSP).
                Ld(_cc);
                Ldc(0x00A0);
                BranchOrBacktrack(OpCodes.Bne_Un, onNoMatch);
                _il.MarkLabel(success);
                break;
            }
            case StandardCharacterSet.kNotWhitespace:
                throw new InvalidOperationException("UNREACHABLE");
            case StandardCharacterSet.kDigit:
                // Match ASCII digits ('0'..'9').
                EmitRangeCompare('0', '9');
                BranchOrBacktrack(OpCodes.Bgt_Un, onNoMatch);
                break;
            case StandardCharacterSet.kNotDigit:
                // Match non ASCII-digits.
                EmitRangeCompare('0', '9');
                BranchOrBacktrack(OpCodes.Ble_Un, onNoMatch);
                break;
            case StandardCharacterSet.kNotLineTerminator:
            {
                // Match non-newlines (not 0x0A('\n'), 0x0D('\r'), 0x2028 and 0x2029).
                // See if current character is '\n'^1 or '\r'^1, i.e., 0x0B or 0x0C.
                EmitLineTerminatorKey();
                Ld(_tmp);
                Ldc(0x0C - 0x0B);
                BranchOrBacktrack(OpCodes.Ble_Un, onNoMatch);
                if (CurrentMode == Mode.UC16)
                {
                    // Compare original value to 0x2028 and 0x2029, using the already
                    // computed (current_char ^ 0x01 - 0x0B). I.e., check for
                    // 0x201D (0x2028 - 0x0B) or 0x201E.
                    Ld(_tmp);
                    Ldc(0x2028 - 0x0B);
                    _il.Emit(OpCodes.Sub);
                    Ldc(0x2029 - 0x2028);
                    BranchOrBacktrack(OpCodes.Ble_Un, onNoMatch);
                }
                break;
            }
            case StandardCharacterSet.kLineTerminator:
            {
                // Match newlines (0x0A('\n'), 0x0D('\r'), 0x2028 and 0x2029).
                EmitLineTerminatorKey();
                Ld(_tmp);
                Ldc(0x0C - 0x0B);
                if (CurrentMode == Mode.LATIN1)
                {
                    BranchOrBacktrack(OpCodes.Bgt_Un, onNoMatch);
                }
                else
                {
                    ILLabel done = _il.DefineLabel();
                    _il.Emit(OpCodes.Ble_Un, done);
                    Ld(_tmp);
                    Ldc(0x2028 - 0x0B);
                    _il.Emit(OpCodes.Sub);
                    Ldc(0x2029 - 0x2028);
                    BranchOrBacktrack(OpCodes.Bgt_Un, onNoMatch);
                    _il.MarkLabel(done);
                }
                break;
            }
            case StandardCharacterSet.kWord:
            {
                if (CurrentMode != Mode.LATIN1)
                {
                    // Table is 256 entries, so all Latin1 characters can be tested.
                    Ld(_cc);
                    Ldc('z');
                    BranchOrBacktrack(OpCodes.Bgt_Un, onNoMatch);
                }
                EmitWordMapLookup();
                BranchOrBacktrack(OpCodes.Brfalse, onNoMatch);
                break;
            }
            case StandardCharacterSet.kNotWord:
            {
                ILLabel done = _il.DefineLabel();
                if (CurrentMode != Mode.LATIN1)
                {
                    // Table is 256 entries, so all Latin1 characters can be tested.
                    Ld(_cc);
                    Ldc('z');
                    _il.Emit(OpCodes.Bgt_Un, done);
                }
                EmitWordMapLookup();
                BranchOrBacktrack(OpCodes.Brtrue, onNoMatch);
                _il.MarkLabel(done);
                break;
            }
            case StandardCharacterSet.kEverything:
                // Match any character.
                break;
        }
    }

    // tmp = (current_char ^ 0x01) - 0x0B.
    void EmitLineTerminatorKey()
    {
        Ld(_cc);
        _il.Emit(OpCodes.Ldc_I4_1);
        _il.Emit(OpCodes.Xor);
        Ldc(0x0B);
        _il.Emit(OpCodes.Sub);
        St(_tmp);
    }

    void EmitWordMapLookup()
    {
        _il.Emit(OpCodes.Ldsfld, s_wordMapField);
        Ld(_cc);
        _il.Emit(OpCodes.Ldelem_U1);
    }

    public override void Fail()
    {
        if (!Global)
        {
            Ldc(RegExpResult.RE_FAILURE);
            St(_result);
        }
        _il.Emit(OpCodes.Br, _exitLabel);
    }

    public override void GoTo(Label? label) => _il.Emit(OpCodes.Br, Target(label));

    public override void IfRegisterGE(int reg, int comparand, Label? ifGe)
    {
        LdReg(reg);
        Ldc(comparand);
        BranchOrBacktrack(OpCodes.Bge, ifGe);
    }

    public override void IfRegisterLT(int reg, int comparand, Label? ifLt)
    {
        LdReg(reg);
        Ldc(comparand);
        BranchOrBacktrack(OpCodes.Blt, ifLt);
    }

    public override void IfRegisterEqPos(int reg, Label? ifEq)
    {
        Ld(_pos);
        LdReg(reg);
        BranchOrBacktrack(OpCodes.Beq, ifEq);
    }

    public override void LoadCurrentCharacterImpl(int cpOffset, Label? onEndOfInput, bool checkBounds,
        int characters, int boundsCheckOffset)
    {
        if (cpOffset < kMinCPOffset || cpOffset > kMaxCPOffset) throw new InvalidOperationException("CHECK failed");
        if (checkBounds)
        {
            Debug.Assert(cpOffset <= boundsCheckOffset);
            CheckPosition(boundsCheckOffset, onEndOfInput);
        }
        LoadCurrentCharacterUnchecked(cpOffset, characters);
    }

    public override void PopCurrentPosition()
    {
        Pop();
        St(_pos);
    }

    public override void PopRegister(int registerIndex)
    {
        BeginStReg(registerIndex);
        Pop();
        EndStReg(registerIndex);
    }

    int BacktrackId(Label label)
    {
        if (!_backtrackIds.TryGetValue(label, out int id))
        {
            id = _backtrackTargets.Count;
            _backtrackTargets.Add(label);
            _backtrackIds.Add(label, id);
        }
        return id;
    }

    public override void PushBacktrack(Label label)
    {
        Ref(label);
        BeginPush();
        Ldc(BacktrackId(label));
        EndPush();
        CheckStackLimit();
    }

    public override void PushCurrentPosition()
    {
        BeginPush();
        Ld(_pos);
        EndPush();
        CheckStackLimit();
    }

    public override void PushRegister(int registerIndex, StackCheckFlag checkStackLimit)
    {
        BeginPush();
        LdReg(registerIndex);
        EndPush();
        if (checkStackLimit == StackCheckFlag.kCheckStackLimit) CheckStackLimit();
    }

    public override void ReadCurrentPositionFromRegister(int reg)
    {
        LdReg(reg);
        St(_pos);
    }

    // The backtrack stack pointer is an index, so it is position independent
    // as it is (x64 stores top - sp).
    public override void ReadStackPointerFromRegister(int reg)
    {
        SetBacktrackStackUsed();
        LdReg(reg);
        St(_sp);
    }

    public override void SetCurrentPositionFromEnd(int by)
    {
        ILLabel afterPosition = _il.DefineLabel();
        Ld(_pos);
        Ld(_length);
        Ldc(by);
        _il.Emit(OpCodes.Sub);
        _il.Emit(OpCodes.Bge, afterPosition);
        Ld(_length);
        Ldc(by);
        _il.Emit(OpCodes.Sub);
        St(_pos);
        // On RegExp code entry (where this operation is used), the character before
        // the current position is expected to be already loaded.
        // We have advanced the position, so it's safe to read backwards.
        LoadCurrentCharacterUnchecked(-1, 1);
        _il.MarkLabel(afterPosition);
    }

    public override void SetRegister(int registerIndex, int to)
    {
        BeginStReg(registerIndex);
        Ldc(to);
        EndStReg(registerIndex);
    }

    public override bool Succeed()
    {
        _successUsed = true;
        _il.Emit(OpCodes.Br, _successLabel);
        return Global;
    }

    public override void WriteCurrentPositionToRegister(int reg, int cpOffset)
    {
        BeginStReg(reg);
        Ld(_pos);
        if (cpOffset != 0)
        {
            Ldc(cpOffset);
            _il.Emit(OpCodes.Add);
        }
        EndStReg(reg);
    }

    public override void ClearRegisters(int regFrom, int regTo)
    {
        Debug.Assert(regFrom <= regTo);
        for (int reg = regFrom; reg <= regTo; reg++)
        {
            BeginStReg(reg);
            _il.Emit(OpCodes.Ldc_I4_M1);  // String start minus one.
            EndStReg(reg);
        }
    }

    public override void WriteStackPointerToRegister(int reg)
    {
        SetBacktrackStackUsed();
        BeginStReg(reg);
        Ld(_sp);
        EndStReg(reg);
    }

    public override void RecordComment(string comment)
    {
    }

    public override object GetCode(string source, RegExpFlags flags)
    {
        // The body has been fully emitted, so BacktrackStackUsed is now final.
        bool backtrackStackUsed = BacktrackStackUsed;

        // Entry code:
        _il.MarkLabel(_entryLabel);
        _il.Emit(OpCodes.Ldarga_S, (byte)kArgSubject);
        _il.Emit(OpCodes.Call, s_subjectLength);
        St(_length);
        _il.Emit(OpCodes.Ldarg_2);
        St(_pos);
        if (backtrackStackUsed)
        {
            _il.Emit(OpCodes.Call, s_rentStack);
            St(_stack);
            SetStackLimit();
        }
        if (_numRegisters > kMaxLocalRegisters)
        {
            Ldc(_numRegisters - kMaxLocalRegisters);
            _il.Emit(OpCodes.Newarr, typeof(int));
            St(_registerArray);
        }
        EmitLoadConstants(s_tablesField, _tableLocals);
        EmitLoadConstants(s_rangeArraysField, _rangeArrayLocals);
        EmitLoadConstants(s_searchSetsField, _searchSetLocals);

        {
            ILLabel startRegExp = _il.DefineLabel();
            // Load newline if index is at start, previous character otherwise.
            Ld(_pos);
            _il.Emit(OpCodes.Brtrue, _loadCharStartRegExp);
            Ldc('\n');
            St(_cc);
            _il.Emit(OpCodes.Br, startRegExp);

            // Global regexp restarts matching here.
            _il.MarkLabel(_loadCharStartRegExp);
            // Load previous char as initial value of current character register.
            LoadCurrentCharacterUnchecked(-1, 1);

            _il.MarkLabel(startRegExp);
        }

        // Fill saved registers with initial value = start offset - 1.
        for (int i = 0; i < _numSavedRegisters; i++)
        {
            BeginStReg(i);
            _il.Emit(OpCodes.Ldc_I4_M1);
            EndStReg(i);
        }

        if (backtrackStackUsed && FailLabel is not null)
        {
            // Push the fail label (see SetFailLabel / ProloguePushesFailLabel).
            // Global matches re-enter here per iteration, each having reset the
            // stack to its base first, so it is refreshed.
            PushBacktrack(FailLabel);
        }

        _il.Emit(OpCodes.Br, _startLabel);

        // Exit code:
        if (_successUsed)
        {
            // Save captures when successful.
            _il.MarkLabel(_successLabel);
            for (int i = 0; i < _numSavedRegisters; i++)
            {
                _il.Emit(OpCodes.Ldarga_S, (byte)kArgOutput);
                Ld(_outOffset);
                if (i != 0)
                {
                    Ldc(i);
                    _il.Emit(OpCodes.Add);
                }
                _il.Emit(OpCodes.Call, s_outputItem);
                LdReg(i);
                _il.Emit(OpCodes.Stind_I4);
            }

            if (Global)
            {
                // Restart matching if the regular expression is flagged as global.
                // Increment success counter.
                Ld(_successCount);
                _il.Emit(OpCodes.Ldc_I4_1);
                _il.Emit(OpCodes.Add);
                St(_successCount);
                // Capture results have been stored, so the number of remaining global
                // output registers is reduced by the number of stored captures.
                Ld(_outOffset);
                Ldc(_numSavedRegisters);
                _il.Emit(OpCodes.Add);
                St(_outOffset);
                // Check whether we have enough room for another set of capture results.
                _il.Emit(OpCodes.Ldarga_S, (byte)kArgOutput);
                _il.Emit(OpCodes.Call, s_outputLength);
                Ld(_outOffset);
                _il.Emit(OpCodes.Sub);
                Ldc(_numSavedRegisters);
                _il.Emit(OpCodes.Blt, _exitLabel);

                if (backtrackStackUsed)
                {
                    // Restore the original regexp stack pointer value (effectively, pop the
                    // stored base pointer).
                    _il.Emit(OpCodes.Ldc_I4_0);
                    St(_sp);
                }

                ILLabel reloadStringStartMinusOne = _il.DefineLabel();
                if (GlobalWithZeroLengthCheck)
                {
                    // Special case for zero-length matches.
                    Ld(_pos);
                    LdReg(0);
                    // Not a zero-length match, restart.
                    _il.Emit(OpCodes.Bne_Un, reloadStringStartMinusOne);
                    // Exit if we already reached the end.
                    Ld(_pos);
                    Ld(_length);
                    _il.Emit(OpCodes.Beq, _exitLabel);
                    // Advance current position after a zero-length match.
                    var advance = new Label();
                    Bind(advance);
                    AdvanceCurrentPosition(1);
                    if (GlobalUnicode) CheckNotInSurrogatePair(0, advance);
                }

                _il.MarkLabel(reloadStringStartMinusOne);
                _il.Emit(OpCodes.Br, _loadCharStartRegExp);
            }
            else
            {
                Ldc(RegExpResult.RE_SUCCESS);
                St(_result);
            }
        }

        _il.MarkLabel(_exitLabel);
        if (Global)
        {
            // Return the number of successful captures.
            Ld(_successCount);
            St(_result);
        }

        _il.MarkLabel(_returnLabel);
        if (backtrackStackUsed)
        {
            Ld(_stack);
            _il.Emit(OpCodes.Call, s_returnStack);
        }
        Ld(_result);
        _il.Emit(OpCodes.Ret);

        // Backtrack code (branch target for conditional backtracks).
        if (_backtrackUsed)
        {
            _il.MarkLabel(_backtrackLabel);
            if (backtrackStackUsed)
            {
                EmitBacktrack();
            }
            else
            {
                // Nothing was pushed, so the only possible backtrack target is the fail
                // label. Fail directly instead of popping a stack that was never set
                // up.
                Fail();
            }
        }

        if (_exceptionUsed)
        {
            // Exit with Result EXCEPTION(-1) to signal a backtrack stack overflow.
            _il.MarkLabel(_exceptionLabel);
            Ldc(RegExpResult.RE_EXCEPTION);
            St(_result);
            _il.Emit(OpCodes.Br, _returnLabel);
        }

        if (_fallbackUsed)
        {
            _il.MarkLabel(_fallbackLabel);
            Ldc(RegExpResult.RE_FALLBACK_TO_EXPERIMENTAL);
            St(_result);
            _il.Emit(OpCodes.Br, _returnLabel);
        }

        _code.Tables = _tables.ToArray();
        _code.RangeArrays = _rangeArrays.ToArray();
        _code.SearchSets = _searchSets.ToArray();
        _code.RegisterCount = _numRegisters;
        _code.ILSize = _il.ILOffset;
        _code.UsesBacktrackStack = backtrackStackUsed;
        _code.Entry = (RegExpILEntry)_method.CreateDelegate(typeof(RegExpILEntry), _code);
        return _code;
    }

    void EmitLoadConstants(FieldInfo field, List<LocalBuilder> locals)
    {
        for (int i = 0; i < locals.Count; i++)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, field);
            Ldc(i);
            _il.Emit(OpCodes.Ldelem_Ref);
            St(locals[i]);
        }
    }
}
