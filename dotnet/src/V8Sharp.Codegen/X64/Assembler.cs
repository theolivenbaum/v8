// Port of src/codegen/x64/assembler-x64.{h,cc} (the subset the native Maglev
// backend uses) and src/codegen/x64/register-x64.h. Names follow V8's x64
// Assembler (movq, addl, j(cc, label), bind ...), in PascalCase. The encoder
// core (REX/ModRM/SIB, rel32 labels) comes from Ryu's X64Assembler (MIT,
// see THIRD_PARTY_NOTICES.md), extended with V8's SSE2/SSE4.1 scalar
// double instructions. Every form is checked against objdump by
// tests/V8Sharp.Tests/Codegen/AssemblerX64Test.cs.
namespace V8Sharp.Codegen.X64;

/// <summary>register-x64.h: the general registers (V8's names).</summary>
public enum Register : byte
{
    rax, rcx, rdx, rbx, rsp, rbp, rsi, rdi, r8, r9, r10, r11, r12, r13, r14, r15,
    no_reg = 0xFF,
}

/// <summary>register-x64.h: XMMRegister.</summary>
public enum XMMRegister : byte
{
    xmm0, xmm1, xmm2, xmm3, xmm4, xmm5, xmm6, xmm7, xmm8, xmm9, xmm10, xmm11, xmm12, xmm13, xmm14, xmm15,
}

/// <summary>assembler-x64.h: Condition (the low nibble of Jcc/SETcc/CMOVcc).</summary>
public enum Condition : byte
{
    overflow = 0,
    no_overflow = 1,
    below = 2,
    above_equal = 3,
    equal = 4,
    not_equal = 5,
    below_equal = 6,
    above = 7,
    negative = 8,
    positive = 9,
    parity_even = 10,
    parity_odd = 11,
    less = 12,
    greater_equal = 13,
    less_equal = 14,
    greater = 15,
}

public enum ScaleFactor : byte { times_1 = 0, times_2 = 1, times_4 = 2, times_8 = 3 }

/// <summary>assembler-x64.h: Operand, [base + index * scale + disp32].</summary>
public readonly struct Operand(Register baseReg, int disp = 0, Register index = Register.no_reg, ScaleFactor scale = ScaleFactor.times_1)
{
    public readonly Register Base = baseReg;
    public readonly Register Index = index;
    public readonly ScaleFactor Scale = scale;
    public readonly int Disp = disp;

    public Operand WithOffset(int offset) => new(Base, Disp + offset, Index, Scale);
}

/// <summary>A jump target (an index into its assembler's label table); default(Label) is invalid.</summary>
public readonly struct Label
{
    internal readonly int Id;
    internal Label(int id) { Id = id; }
    public bool IsValid => Id != 0;
}

/// <summary>Assembler (x64): an encoder into a growable byte buffer with rel32 labels.</summary>
public sealed class Assembler
{
    byte[] _buffer = new byte[4096];
    int _pc;
    int[] _labelPos = new int[64];
    int _labels;
    int[] _fixAt = new int[64], _fixLabel = new int[64];
    int _fixups;

    /// <summary>pc_offset().</summary>
    public int PcOffset => _pc;
    public int PendingFixups => _fixups;
    public ReadOnlySpan<byte> Code => new(_buffer, 0, _pc);

    void Emit(int b)
    {
        if (_pc == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
        _buffer[_pc++] = (byte)b;
    }

    void Emitl(int v) { Emit(v); Emit(v >> 8); Emit(v >> 16); Emit(v >> 24); }
    void Emitq(long v) { Emitl((int)v); Emitl((int)(v >> 32)); }

    /// <summary>Patches a 32-bit value at a position (an immediate or a displacement emitted before).</summary>
    public void PatchInt32(int position, int value)
    {
        _buffer[position] = (byte)value;
        _buffer[position + 1] = (byte)(value >> 8);
        _buffer[position + 2] = (byte)(value >> 16);
        _buffer[position + 3] = (byte)(value >> 24);
    }

    // ---- Encoding core -------------------------------------------------------------------------------------

    void EmitRex(bool w, int reg, int index, int rm, bool byteRegs = false)
    {
        int rex = 0x40 | (w ? 8 : 0) | ((reg >> 3) & 1) << 2 | ((index >> 3) & 1) << 1 | ((rm >> 3) & 1);
        // SPL/BPL/SIL/DIL need a REX prefix to be addressed as byte registers.
        if (rex != 0x40 || (byteRegs && ((reg is >= 4 and <= 7) || (rm is >= 4 and <= 7)))) Emit(rex);
    }

    void EmitRR(int prefix, bool w, int reg, int rm, bool byteRegs, ReadOnlySpan<byte> op)
    {
        if (prefix != 0) Emit(prefix);
        EmitRex(w, reg, 0, rm, byteRegs);
        foreach (byte b in op) Emit(b);
        Emit(0xC0 | (reg & 7) << 3 | (rm & 7));
    }

    void EmitRM(int prefix, bool w, int reg, Operand m, ReadOnlySpan<byte> op, bool byteRegs = false)
    {
        if (prefix != 0) Emit(prefix);
        int idx = m.Index == Register.no_reg ? 0 : (int)m.Index;
        EmitRex(w, reg, idx, (int)m.Base, byteRegs);
        foreach (byte b in op) Emit(b);
        int bas = (int)m.Base & 7;
        int mod = m.Disp == 0 && bas != 5 ? 0 : m.Disp is >= -128 and <= 127 ? 1 : 2;
        if (m.Index == Register.no_reg && bas != 4)
        {
            Emit(mod << 6 | (reg & 7) << 3 | bas);
        }
        else
        {
            if (m.Index == Register.rsp) throw new ArgumentException("rsp cannot be an index");
            int ix = m.Index == Register.no_reg ? 4 : (int)m.Index & 7;
            Emit(mod << 6 | (reg & 7) << 3 | 4);
            Emit((int)m.Scale << 6 | ix << 3 | bas);
        }
        if (mod == 1) Emit(m.Disp);
        else if (mod == 2) Emitl(m.Disp);
    }

    // ---- Arithmetic: add/or/adc/sbb/and/sub/xor/cmp (arithmetic_op, immediate_arithmetic_op) -----------------

    enum Alu { Add = 0, Or = 1, Adc = 2, Sbb = 3, And = 4, Sub = 5, Xor = 6, Cmp = 7 }

    void AluRR(Alu op, Register dst, Register src, bool w) => EmitRR(0, w, (int)src, (int)dst, false, [(byte)((int)op << 3 | 1)]);
    void AluRM(Alu op, Register dst, Operand src, bool w) => EmitRM(0, w, (int)dst, src, [(byte)((int)op << 3 | 3)]);
    void AluMR(Alu op, Operand dst, Register src, bool w) => EmitRM(0, w, (int)src, dst, [(byte)((int)op << 3 | 1)]);

    void AluRI(Alu op, Register dst, int imm, bool w)
    {
        if (imm is >= -128 and <= 127)
        {
            EmitRR(0, w, (int)op, (int)dst, false, [0x83]);
            Emit(imm);
        }
        else
        {
            EmitRR(0, w, (int)op, (int)dst, false, [0x81]);
            Emitl(imm);
        }
    }

    void AluMI(Alu op, Operand dst, int imm, bool w)
    {
        if (imm is >= -128 and <= 127)
        {
            EmitRM(0, w, (int)op, dst, [0x83]);
            Emit(imm);
        }
        else
        {
            EmitRM(0, w, (int)op, dst, [0x81]);
            Emitl(imm);
        }
    }

    public void Addl(Register dst, Register src) => AluRR(Alu.Add, dst, src, false);
    public void Addq(Register dst, Register src) => AluRR(Alu.Add, dst, src, true);
    public void Addl(Register dst, int imm) => AluRI(Alu.Add, dst, imm, false);
    public void Addq(Register dst, int imm) => AluRI(Alu.Add, dst, imm, true);
    public void Addl(Register dst, Operand src) => AluRM(Alu.Add, dst, src, false);
    public void Addq(Operand dst, int imm) => AluMI(Alu.Add, dst, imm, true);
    public void Subl(Register dst, Register src) => AluRR(Alu.Sub, dst, src, false);
    public void Subq(Register dst, Register src) => AluRR(Alu.Sub, dst, src, true);
    public void Subl(Register dst, int imm) => AluRI(Alu.Sub, dst, imm, false);
    public void Subq(Register dst, int imm) => AluRI(Alu.Sub, dst, imm, true);
    public void Subl(Register dst, Operand src) => AluRM(Alu.Sub, dst, src, false);
    public void Subq(Operand dst, int imm) => AluMI(Alu.Sub, dst, imm, true);
    public void Andl(Register dst, Register src) => AluRR(Alu.And, dst, src, false);
    public void Andq(Register dst, Register src) => AluRR(Alu.And, dst, src, true);
    public void Andl(Register dst, int imm) => AluRI(Alu.And, dst, imm, false);
    public void Andl(Register dst, Operand src) => AluRM(Alu.And, dst, src, false);
    public void Orl(Register dst, Register src) => AluRR(Alu.Or, dst, src, false);
    public void Orq(Register dst, Register src) => AluRR(Alu.Or, dst, src, true);
    public void Orl(Register dst, int imm) => AluRI(Alu.Or, dst, imm, false);
    public void Orl(Register dst, Operand src) => AluRM(Alu.Or, dst, src, false);
    public void Xorl(Register dst, Register src) => AluRR(Alu.Xor, dst, src, false);
    public void Xorq(Register dst, Register src) => AluRR(Alu.Xor, dst, src, true);
    public void Xorl(Register dst, int imm) => AluRI(Alu.Xor, dst, imm, false);
    public void Xorl(Register dst, Operand src) => AluRM(Alu.Xor, dst, src, false);
    public void Cmpl(Register a, Register b) => AluRR(Alu.Cmp, a, b, false);
    public void Cmpq(Register a, Register b) => AluRR(Alu.Cmp, a, b, true);
    public void Cmpl(Register a, int imm) => AluRI(Alu.Cmp, a, imm, false);
    public void Cmpq(Register a, int imm) => AluRI(Alu.Cmp, a, imm, true);
    public void Cmpl(Register a, Operand b) => AluRM(Alu.Cmp, a, b, false);
    public void Cmpq(Register a, Operand b) => AluRM(Alu.Cmp, a, b, true);
    public void Cmpl(Operand a, Register b) => AluMR(Alu.Cmp, a, b, false);
    public void Cmpq(Operand a, Register b) => AluMR(Alu.Cmp, a, b, true);
    public void Cmpl(Operand a, int imm) => AluMI(Alu.Cmp, a, imm, false);
    public void Cmpq(Operand a, int imm) => AluMI(Alu.Cmp, a, imm, true);

    public void Testl(Register a, Register b) => EmitRR(0, false, (int)b, (int)a, false, [0x85]);
    public void Testq(Register a, Register b) => EmitRR(0, true, (int)b, (int)a, false, [0x85]);
    public void Testl(Operand a, int imm) { EmitRM(0, false, 0, a, [0xF7]); Emitl(imm); }
    public void Testl(Register a, int imm) { EmitRR(0, false, 0, (int)a, false, [0xF7]); Emitl(imm); }

    // ---- Moves --------------------------------------------------------------------------------------------

    public void Movl(Register dst, Register src) => EmitRR(0, false, (int)src, (int)dst, false, [0x89]);
    public void Movq(Register dst, Register src) => EmitRR(0, true, (int)src, (int)dst, false, [0x89]);
    public void Movl(Register dst, Operand src) => EmitRM(0, false, (int)dst, src, [0x8B]);
    public void Movq(Register dst, Operand src) => EmitRM(0, true, (int)dst, src, [0x8B]);
    public void Movl(Operand dst, Register src) => EmitRM(0, false, (int)src, dst, [0x89]);
    public void Movq(Operand dst, Register src) => EmitRM(0, true, (int)src, dst, [0x89]);
    /// <summary>movl [m], imm32.</summary>
    public void Movl(Operand dst, int imm) { EmitRM(0, false, 0, dst, [0xC7]); Emitl(imm); }
    /// <summary>movq [m], imm32 (sign-extended).</summary>
    public void Movq(Operand dst, int imm) { EmitRM(0, true, 0, dst, [0xC7]); Emitl(imm); }
    /// <summary>movb [m], imm8.</summary>
    public void Movb(Operand dst, int imm) { EmitRM(0, false, 0, dst, [0xC6]); Emit(imm); }

    /// <summary>movl reg, imm32 (zero-extends); xorl for 0.</summary>
    public void Movl(Register dst, int imm)
    {
        if ((int)dst >= 8) Emit(0x41);
        Emit(0xB8 | ((int)dst & 7));
        Emitl(imm);
    }

    /// <summary>Move(reg, imm64): the shortest form.</summary>
    public void Movq(Register dst, long imm)
    {
        if (imm is >= 0 and <= uint.MaxValue)
        {
            Movl(dst, (int)(uint)imm);
            return;
        }
        if (imm is >= int.MinValue and <= int.MaxValue)
        {
            EmitRR(0, true, 0, (int)dst, false, [0xC7]);
            Emitl((int)imm);
            return;
        }
        Emit(0x48 | ((int)dst >> 3 & 1));
        Emit(0xB8 | ((int)dst & 7));
        Emitq(imm);
    }

    public void Leaq(Register dst, Operand src) => EmitRM(0, true, (int)dst, src, [0x8D]);
    public void Leal(Register dst, Operand src) => EmitRM(0, false, (int)dst, src, [0x8D]);
    /// <summary>movsxlq: sign-extends a 32-bit register.</summary>
    public void Movsxlq(Register dst, Register src) => EmitRR(0, true, (int)dst, (int)src, false, [0x63]);
    /// <summary>movzxbl reg, reg8.</summary>
    public void Movzxbl(Register dst, Register src) => EmitRR(0, false, (int)dst, (int)src, true, [0x0F, 0xB6]);
    public void Movzxbl(Register dst, Operand src) => EmitRM(0, false, (int)dst, src, [0x0F, 0xB6]);
    public void Movzxwl(Register dst, Operand src) => EmitRM(0, false, (int)dst, src, [0x0F, 0xB7]);

    public void Setcc(Condition cc, Register dst) => EmitRR(0, false, 0, (int)dst, true, [0x0F, (byte)(0x90 | (int)cc)]);
    public void Cmovl(Condition cc, Register dst, Register src) => EmitRR(0, false, (int)dst, (int)src, false, [0x0F, (byte)(0x40 | (int)cc)]);
    public void Cmovq(Condition cc, Register dst, Register src) => EmitRR(0, true, (int)dst, (int)src, false, [0x0F, (byte)(0x40 | (int)cc)]);

    // ---- Multiply, divide, unary, shifts ------------------------------------------------------------------

    public void Imull(Register dst, Register src) => EmitRR(0, false, (int)dst, (int)src, false, [0x0F, 0xAF]);
    public void Imulq(Register dst, Register src) => EmitRR(0, true, (int)dst, (int)src, false, [0x0F, 0xAF]);
    /// <summary>imull dst, src, imm: the three-operand form.</summary>
    public void Imull(Register dst, Register src, int imm)
    {
        if (imm is >= -128 and <= 127)
        {
            EmitRR(0, false, (int)dst, (int)src, false, [0x6B]);
            Emit(imm);
        }
        else
        {
            EmitRR(0, false, (int)dst, (int)src, false, [0x69]);
            Emitl(imm);
        }
    }
    /// <summary>cdq: sign-extends eax into edx.</summary>
    public void Cdq() => Emit(0x99);
    public void Idivl(Register src) => EmitRR(0, false, 7, (int)src, false, [0xF7]);
    public void Negl(Register r) => EmitRR(0, false, 3, (int)r, false, [0xF7]);
    public void Negq(Register r) => EmitRR(0, true, 3, (int)r, false, [0xF7]);
    public void Notl(Register r) => EmitRR(0, false, 2, (int)r, false, [0xF7]);
    public void Decl(Register r) => EmitRR(0, false, 1, (int)r, false, [0xFF]);
    public void Incl(Register r) => EmitRR(0, false, 0, (int)r, false, [0xFF]);
    public void Decq(Operand m) => EmitRM(0, true, 1, m, [0xFF]);
    public void Decq(Register r) => EmitRR(0, true, 1, (int)r, false, [0xFF]);

    enum Shift { Rol = 0, Ror = 1, Shl = 4, Shr = 5, Sar = 7 }

    void ShiftImm(Shift op, Register r, int amount, bool w)
    {
        if (amount == 1)
        {
            EmitRR(0, w, (int)op, (int)r, false, [0xD1]);
            return;
        }
        EmitRR(0, w, (int)op, (int)r, false, [0xC1]);
        Emit(amount);
    }

    public void Shll(Register r, int amount) => ShiftImm(Shift.Shl, r, amount, false);
    public void Shrl(Register r, int amount) => ShiftImm(Shift.Shr, r, amount, false);
    public void Sarl(Register r, int amount) => ShiftImm(Shift.Sar, r, amount, false);
    public void Shlq(Register r, int amount) => ShiftImm(Shift.Shl, r, amount, true);
    public void Shrq(Register r, int amount) => ShiftImm(Shift.Shr, r, amount, true);
    public void Sarq(Register r, int amount) => ShiftImm(Shift.Sar, r, amount, true);
    /// <summary>shll_cl: shifts by cl (masked to 5 bits by the CPU, as JS's shifts).</summary>
    public void Shll_cl(Register r) => EmitRR(0, false, (int)Shift.Shl, (int)r, false, [0xD3]);
    public void Shrl_cl(Register r) => EmitRR(0, false, (int)Shift.Shr, (int)r, false, [0xD3]);
    public void Sarl_cl(Register r) => EmitRR(0, false, (int)Shift.Sar, (int)r, false, [0xD3]);
    public void Shlq_cl(Register r) => EmitRR(0, true, (int)Shift.Shl, (int)r, false, [0xD3]);
    public void Shrq_cl(Register r) => EmitRR(0, true, (int)Shift.Shr, (int)r, false, [0xD3]);

    // ---- Stack and control -----------------------------------------------------------------------------------

    public void Pushq(Register r) { if ((int)r >= 8) Emit(0x41); Emit(0x50 | ((int)r & 7)); }
    public void Popq(Register r) { if ((int)r >= 8) Emit(0x41); Emit(0x58 | ((int)r & 7)); }
    public void Ret() => Emit(0xC3);
    public void Int3() => Emit(0xCC);
    public void Ud2() { Emit(0x0F); Emit(0x0B); }
    /// <summary>jmp reg.</summary>
    public void Jmp(Register target) => EmitRR(0, false, 4, (int)target, false, [0xFF]);

    public Label NewLabel()
    {
        if (_labels == _labelPos.Length) Array.Resize(ref _labelPos, _labels * 2);
        _labelPos[_labels] = -1;
        return new Label(++_labels);
    }

    public bool IsBound(Label l) => _labelPos[l.Id - 1] >= 0;

    /// <summary>bind(label): resolves the label's pending jumps.</summary>
    public void Bind(Label l)
    {
        if (_labelPos[l.Id - 1] >= 0) throw new InvalidOperationException("label bound twice");
        _labelPos[l.Id - 1] = _pc;
        for (int i = 0; i < _fixups;)
        {
            if (_fixLabel[i] == l.Id)
            {
                int at = _fixAt[i];
                PatchInt32(at, _pc - (at + 4));
                _fixups--;
                _fixAt[i] = _fixAt[_fixups];
                _fixLabel[i] = _fixLabel[_fixups];
            }
            else
            {
                i++;
            }
        }
    }

    void EmitRel32(Label l)
    {
        int pos = _labelPos[l.Id - 1];
        if (pos >= 0)
        {
            Emitl(pos - (_pc + 4));
            return;
        }
        if (_fixups == _fixAt.Length)
        {
            Array.Resize(ref _fixAt, _fixups * 2);
            Array.Resize(ref _fixLabel, _fixups * 2);
        }
        _fixAt[_fixups] = _pc;
        _fixLabel[_fixups++] = l.Id;
        Emitl(0);
    }

    /// <summary>j(cc, label): always the rel32 form (a pass that shortens jumps is not worth it here).</summary>
    public void J(Condition cc, Label l) { Emit(0x0F); Emit(0x80 | (int)cc); EmitRel32(l); }
    public void Jmp(Label l) { Emit(0xE9); EmitRel32(l); }

    // ---- SSE2 / SSE4.1 scalar double (sse2_instr, sse4_instr) ------------------------------------------------

    void Sse(int prefix, int reg, int rm, byte op, bool w = false) => EmitRR(prefix, w, reg, rm, false, [0x0F, op]);
    void SseM(int prefix, int reg, Operand m, byte op, bool w = false) => EmitRM(prefix, w, reg, m, [0x0F, op]);

    public void Movsd(XMMRegister dst, Operand src) => SseM(0xF2, (int)dst, src, 0x10);
    public void Movsd(Operand dst, XMMRegister src) => SseM(0xF2, (int)src, dst, 0x11);
    /// <summary>movapd dst, src: the register move V8's Move(XMMRegister, XMMRegister) uses.</summary>
    public void Movapd(XMMRegister dst, XMMRegister src) => Sse(0x66, (int)dst, (int)src, 0x28);
    /// <summary>movq xmm, r64.</summary>
    public void Movq(XMMRegister dst, Register src) => EmitRR(0x66, true, (int)dst, (int)src, false, [0x0F, 0x6E]);
    /// <summary>movq r64, xmm.</summary>
    public void Movq(Register dst, XMMRegister src) => EmitRR(0x66, true, (int)src, (int)dst, false, [0x0F, 0x7E]);
    public void Addsd(XMMRegister dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x58);
    public void Subsd(XMMRegister dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x5C);
    public void Mulsd(XMMRegister dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x59);
    public void Divsd(XMMRegister dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x5E);
    public void Sqrtsd(XMMRegister dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x51);
    public void Addsd(XMMRegister dst, Operand src) => SseM(0xF2, (int)dst, src, 0x58);
    public void Mulsd(XMMRegister dst, Operand src) => SseM(0xF2, (int)dst, src, 0x59);
    public void Ucomisd(XMMRegister a, XMMRegister b) => Sse(0x66, (int)a, (int)b, 0x2E);
    public void Xorpd(XMMRegister dst, XMMRegister src) => Sse(0x66, (int)dst, (int)src, 0x57);
    public void Andpd(XMMRegister dst, XMMRegister src) => Sse(0x66, (int)dst, (int)src, 0x54);
    /// <summary>cvtlsi2sd: int32 -> double.</summary>
    public void Cvtlsi2sd(XMMRegister dst, Register src) => Sse(0xF2, (int)dst, (int)src, 0x2A);
    /// <summary>cvtqsi2sd: int64 -> double.</summary>
    public void Cvtqsi2sd(XMMRegister dst, Register src) => Sse(0xF2, (int)dst, (int)src, 0x2A, w: true);
    /// <summary>cvttsd2si: double -> int32, truncating (0x80000000 for NaN and out of range).</summary>
    public void Cvttsd2si(Register dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x2C);
    /// <summary>cvttsd2siq: double -> int64, truncating (long.MinValue for NaN and out of range).</summary>
    public void Cvttsd2siq(Register dst, XMMRegister src) => Sse(0xF2, (int)dst, (int)src, 0x2C, w: true);

    // ---- x87 (fmod: fprem is exact, as V8's Float64Mod on ia32 and x64) ------------------------------------

    /// <summary>fld qword [m].</summary>
    public void Fld_d(Operand m) => EmitRM(0, false, 0, m, [0xDD]);
    /// <summary>fstp qword [m].</summary>
    public void Fstp_d(Operand m) => EmitRM(0, false, 3, m, [0xDD]);
    /// <summary>fstp st(0): pops the x87 stack.</summary>
    public void Fstp_st0() { Emit(0xDD); Emit(0xD8); }
    public void Fprem() { Emit(0xD9); Emit(0xF8); }
    /// <summary>fnstsw ax.</summary>
    public void Fnstsw_ax() { Emit(0xDF); Emit(0xE0); }

    /// <summary>RoundingMode (roundsd's immediate).</summary>
    public enum RoundingMode : byte { kRoundToNearest = 0, kRoundDown = 1, kRoundUp = 2, kRoundToZero = 3 }

    /// <summary>roundsd (SSE4.1): rounds without raising the precision exception (imm bit 3).</summary>
    public void Roundsd(XMMRegister dst, XMMRegister src, RoundingMode mode)
    {
        EmitRR(0x66, false, (int)dst, (int)src, false, [0x0F, 0x3A, 0x0B]);
        Emit((int)mode | 8);
    }
}
