// Port of test/unittests/assembler/assembler-x64-unittest.cc (the idea: every
// instruction form the backend emits, disassembled) for V8Sharp.Codegen.X64:
// the encoder's bytes go through objdump, which must print the expected
// instruction (as Ryu's encoding test does). Plus execution tests of small
// functions through NativeCode.
using System.Diagnostics;
using V8Sharp.Codegen;
using V8Sharp.Codegen.X64;
using static V8Sharp.Codegen.X64.Register;
using static V8Sharp.Codegen.X64.XMMRegister;

namespace V8Sharp.Tests.Codegen;

public class AssemblerX64Test
{
    static readonly (Action<Assembler> Emit, string Expected)[] s_forms =
    [
        (a => a.Addl(rax, rcx), "add    eax,ecx"),
        (a => a.Addq(r8, r15), "add    r8,r15"),
        (a => a.Addl(rsi, 5), "add    esi,0x5"),
        (a => a.Addl(r12, 1000), "add    r12d,0x3e8"),
        (a => a.Addl(rdx, new Operand(rbx, 24)), "add    edx,DWORD PTR [rbx+0x18]"),
        (a => a.Addq(new Operand(rsp, 8), -1), "add    QWORD PTR [rsp+0x8],0xffffffffffffffff"),
        (a => a.Subl(r9, r10), "sub    r9d,r10d"),
        (a => a.Subq(rsp, 40), "sub    rsp,0x28"),
        (a => a.Andl(rdi, 31), "and    edi,0x1f"),
        (a => a.Orl(r13, rbp), "or     r13d,ebp"),
        (a => a.Xorl(rax, rax), "xor    eax,eax"),
        (a => a.Xorq(r11, rdx), "xor    r11,rdx"),
        (a => a.Cmpl(rax, rcx), "cmp    eax,ecx"),
        (a => a.Cmpq(rax, new Operand(r13, 0)), "cmp    rax,QWORD PTR [r13+0x0]"),
        (a => a.Cmpl(r14, new Operand(rax, 8)), "cmp    r14d,DWORD PTR [rax+0x8]"),
        (a => a.Cmpl(new Operand(rbx, 0x100), 0), "cmp    DWORD PTR [rbx+0x100],0x0"),
        (a => a.Cmpq(rdx, -7), "cmp    rdx,0xfffffffffffffff9"),
        (a => a.Testl(rsi, rsi), "test   esi,esi"),
        (a => a.Testl(new Operand(rax, 0), -1), "test   DWORD PTR [rax],0xffffffff"),
        (a => a.Movl(rax, r8), "mov    eax,r8d"),
        (a => a.Movq(r12, rdi), "mov    r12,rdi"),
        (a => a.Movq(rax, new Operand(rbx, 8, rcx, ScaleFactor.times_8)), "mov    rax,QWORD PTR [rbx+rcx*8+0x8]"),
        (a => a.Movq(new Operand(rbp, -16), r9), "mov    QWORD PTR [rbp-0x10],r9"),
        (a => a.Movl(rcx, new Operand(r12, 4)), "mov    ecx,DWORD PTR [r12+0x4]"),
        (a => a.Movl(new Operand(rbx, 32), rsi), "mov    DWORD PTR [rbx+0x20],esi"),
        (a => a.Movq(new Operand(rbx, 32), 7), "mov    QWORD PTR [rbx+0x20],0x7"),
        (a => a.Movl(new Operand(rbx, 32), -2), "mov    DWORD PTR [rbx+0x20],0xfffffffe"),
        (a => a.Movl(r10, 0x12345678), "mov    r10d,0x12345678"),
        (a => a.Movq(rdx, 0x123456789AL), "movabs rdx,0x123456789a"),
        (a => a.Movq(r11, -1L), "mov    r11,0xffffffffffffffff"),
        (a => a.Leaq(rax, new Operand(rax, 16, rdx, ScaleFactor.times_8)), "lea    rax,[rax+rdx*8+0x10]"),
        (a => a.Leal(rcx, new Operand(rcx, 1)), "lea    ecx,[rcx+0x1]"),
        (a => a.Movsxlq(rax, r9), "movsxd rax,r9d"),
        (a => a.Movzxbl(rax, rsi), "movzx  eax,sil"),
        (a => a.Movzxwl(rdx, new Operand(rax, 12, rcx, ScaleFactor.times_2)), "movzx  edx,WORD PTR [rax+rcx*2+0xc]"),
        (a => a.Setcc(Condition.less, rax), "setl   al"),
        (a => a.Setcc(Condition.above, rdi), "seta   dil"),
        (a => a.Cmovl(Condition.equal, r8, r9), "cmove  r8d,r9d"),
        (a => a.Imull(rax, r13), "imul   eax,r13d"),
        (a => a.Imulq(rcx, rdx), "imul   rcx,rdx"),
        (a => a.Imull(r9, r8, 7), "imul   r9d,r8d,0x7"),
        (a => a.Imull(rax, r13, 1000), "imul   eax,r13d,0x3e8"),
        (a => a.Cdq(), "cdq"),
        (a => a.Idivl(rcx), "idiv   ecx"),
        (a => a.Negl(r15), "neg    r15d"),
        (a => a.Notl(rdx), "not    edx"),
        (a => a.Decq(new Operand(rbx, 40)), "dec    QWORD PTR [rbx+0x28]"),
        (a => a.Shll(rax, 3), "shl    eax,0x3"),
        (a => a.Sarl(r8, 1), "sar    r8d,1"),
        (a => a.Shrq(rdx, 63), "shr    rdx,0x3f"),
        (a => a.Shll_cl(rsi), "shl    esi,cl"),
        (a => a.Sarl_cl(r10), "sar    r10d,cl"),
        (a => a.Shrl_cl(rax), "shr    eax,cl"),
        (a => a.Pushq(rbx), "push   rbx"),
        (a => a.Popq(r15), "pop    r15"),
        (a => a.Ret(), "ret"),
        (a => a.Movsd(xmm3, new Operand(rax, 16, rcx, ScaleFactor.times_8)), "movsd  xmm3,QWORD PTR [rax+rcx*8+0x10]"),
        (a => a.Movsd(new Operand(rbx, 64), xmm12), "movsd  QWORD PTR [rbx+0x40],xmm12"),
        (a => a.Movapd(xmm1, xmm9), "movapd xmm1,xmm9"),
        (a => a.Movq(xmm2, r11), "movq   xmm2,r11"),
        (a => a.Movq(rax, xmm14), "movq   rax,xmm14"),
        (a => a.Addsd(xmm0, xmm1), "addsd  xmm0,xmm1"),
        (a => a.Subsd(xmm8, xmm15), "subsd  xmm8,xmm15"),
        (a => a.Mulsd(xmm2, xmm3), "mulsd  xmm2,xmm3"),
        (a => a.Divsd(xmm4, xmm10), "divsd  xmm4,xmm10"),
        (a => a.Sqrtsd(xmm5, xmm5), "sqrtsd xmm5,xmm5"),
        (a => a.Ucomisd(xmm6, xmm7), "ucomisd xmm6,xmm7"),
        (a => a.Xorpd(xmm0, xmm0), "xorpd  xmm0,xmm0"),
        (a => a.Cvtlsi2sd(xmm1, r9), "cvtsi2sd xmm1,r9d"),
        (a => a.Cvtqsi2sd(xmm11, rax), "cvtsi2sd xmm11,rax"),
        (a => a.Cvttsd2si(r8, xmm2), "cvttsd2si r8d,xmm2"),
        (a => a.Cvttsd2siq(rax, xmm13), "cvttsd2si rax,xmm13"),
        (a => a.Roundsd(xmm1, xmm2, Assembler.RoundingMode.kRoundDown), "roundsd xmm1,xmm2,0x9"),
        (a => a.Jmp(rax), "jmp    rax"),
        (a => a.Fld_d(new Operand(rbx, 64)), "fld    QWORD PTR [rbx+0x40]"),
        (a => a.Fstp_d(new Operand(rbx, 72)), "fstp   QWORD PTR [rbx+0x48]"),
        (a => a.Fstp_st0(), "fstp   st(0)"),
        (a => a.Fprem(), "fprem"),
        (a => a.Fnstsw_ax(), "fnstsw ax"),
    ];

    static string? RunObjdump(byte[] code)
    {
        string dir = Path.Combine(Path.GetTempPath(), "v8sharp-asm-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "code.bin");
        File.WriteAllBytes(file, code);
        try
        {
            var psi = new ProcessStartInfo("objdump", ["-D", "-b", "binary", "-m", "i386:x86-64", "-M", "intel", file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public void EncodingMatchesObjdump()
    {
        var a = new Assembler();
        var starts = new List<int>();
        foreach ((Action<Assembler> emit, string _) in s_forms)
        {
            starts.Add(a.PcOffset);
            emit(a);
        }
        string? listing = RunObjdump(a.Code.ToArray());
        Assert.SkipWhen(listing is null, "objdump is not installed");
        // "   4:\t44 01 c0             \tadd    eax,r8d"
        var byOffset = new Dictionary<int, string>();
        foreach (string line in listing!.Split('\n'))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 3 || !parts[0].TrimEnd().EndsWith(':')) continue;
            if (!int.TryParse(parts[0].Trim().TrimEnd(':'), System.Globalization.NumberStyles.HexNumber, null, out int offset)) continue;
            byOffset[offset] = parts[2].Trim();
        }
        for (int i = 0; i < s_forms.Length; i++)
        {
            Assert.True(byOffset.TryGetValue(starts[i], out string? text), $"no instruction at {starts[i]:x} for {s_forms[i].Expected}");
            Assert.Equal(s_forms[i].Expected, text);
        }
    }

    [Fact]
    public void LabelsAndConditionsExecute()
    {
        Assert.SkipUnless(ExecutableMemory.Supported, "native code needs Linux on x64");
        // long f(long* ctx): sum of ctx[1]..ctx[ctx[0]] counted down in a loop.
        var a = new Assembler();
        Label loop = a.NewLabel(), done = a.NewLabel();
        a.Movq(rcx, new Operand(rdi, 0));
        a.Xorl(rax, rax);
        a.Bind(loop);
        a.Testq(rcx, rcx);
        a.J(Condition.equal, done);
        a.Addq(rax, rax);
        a.Movq(rdx, new Operand(rdi, 0, rcx, ScaleFactor.times_8));
        a.Addq(rax, rdx);
        a.Decl(rcx);
        a.Jmp(loop);
        a.Bind(done);
        a.Ret();
        Assert.Equal(0, a.PendingFixups);
        nint entry = ExecutableMemory.Add(a.Code);
        long[] ctx = NativeCode.NewContext(8);
        ctx[0] = 4;
        ctx[1] = 10; ctx[2] = 20; ctx[3] = 30; ctx[4] = 40;
        // rax doubles at each step (the add of rax to itself) before adding the element.
        long expected = 0;
        for (int i = 4; i >= 1; i--) expected = 2 * expected + ctx[i];
        Assert.Equal(expected, NativeCode.CallRaw(entry, ctx));
    }

    [Fact]
    public void DoubleArithmeticExecutes()
    {
        Assert.SkipUnless(ExecutableMemory.Supported, "native code needs Linux on x64");
        // ctx[2] = sqrt(ctx[0] * ctx[1] + 1) as doubles; returns (int)ctx[2] by truncation.
        var a = new Assembler();
        a.Movsd(xmm0, new Operand(rdi, 0));
        a.Movsd(xmm9, new Operand(rdi, 8));
        a.Mulsd(xmm0, xmm9);
        a.Movl(rax, 1);
        a.Cvtlsi2sd(xmm12, rax);
        a.Addsd(xmm0, xmm12);
        a.Sqrtsd(xmm0, xmm0);
        a.Movsd(new Operand(rdi, 16), xmm0);
        a.Cvttsd2si(rax, xmm0);
        a.Ret();
        nint entry = ExecutableMemory.Add(a.Code);
        long[] ctx = NativeCode.NewContext(4);
        ctx[0] = BitConverter.DoubleToInt64Bits(6.0);
        ctx[1] = BitConverter.DoubleToInt64Bits(8.0);
        Assert.Equal(7, NativeCode.CallRaw(entry, ctx));
        Assert.Equal(7.0, BitConverter.Int64BitsToDouble(ctx[2]));
    }
}
