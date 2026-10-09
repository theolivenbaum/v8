// Differential tests of the wasm compiler (src/V8Sharp/Wasm/Baseline, the
// Liftoff port): each script runs once with the compiler (the default) and
// once in the interpreter (--wasm-jitless), and the outputs must be the
// same. The scripts cover what V8's test/cctest/wasm/test-run-wasm*.cc and
// test/unittests/wasm/liftoff-*-unittest.cc cover: every numeric operation on
// edge values (float results compared bit for bit, NaNs as "nan" where wasm
// leaves the payload open), memory accesses at the bounds, control flow with
// values, calls of every kind, tail calls, exceptions, globals, tables, GC
// objects, SIMD, traps and their stack traces, and stack overflow.
namespace V8Sharp.Wasm.Tests;

public class WasmCompilerTests
{
    static string Compiled(string source, string? flags = null) => WasmJsTester.Run(source, interpret: false, flags: flags);
    static string Interpreted(string source) => WasmJsTester.Run(source, interpret: true);

    /// <summary>Runs <paramref name="source"/> in both tiers, asserts the same output, and returns it.</summary>
    static string Both(string source, string? flags = null)
    {
        string interpreted = Interpreted(source);
        string compiled = Compiled(source, flags);
        Assert.Equal(interpreted, compiled);
        Assert.DoesNotContain("uncaught", compiled);
        return compiled;
    }

    const string Prelude = """
        function fmt32(bits) {
          bits = bits >>> 0;
          if ((bits & 0x7f800000) == 0x7f800000 && (bits & 0x7fffff) != 0) return "nan";
          return "0x" + bits.toString(16);
        }
        function fmt64(bits) {
          bits = BigInt.asUintN(64, bits);
          if ((bits & 0x7ff0000000000000n) == 0x7ff0000000000000n && (bits & 0xfffffffffffffn) != 0n) return "nan";
          return "0x" + bits.toString(16);
        }
        function run(f, ...args) {
          try { return String(f(...args)); } catch (e) { return String(e); }
        }
        """;

    [Fact]
    public void I32Operations()
    {
        string output = Both(Prelude + """
            const binops = ["I32Add","I32Sub","I32Mul","I32DivS","I32DivU","I32RemS","I32RemU","I32And","I32Ior","I32Xor",
              "I32Shl","I32ShrS","I32ShrU","I32Rol","I32Ror","I32Eq","I32Ne","I32LtS","I32LtU","I32GtS","I32GtU","I32LeS",
              "I32LeU","I32GeS","I32GeU"];
            const unops = ["I32Clz","I32Ctz","I32Popcnt","I32Eqz","I32SExtendI8","I32SExtendI16"];
            const b = new WasmModuleBuilder();
            for (const op of binops) b.addFunction(op, kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, eval("kExpr" + op)]).exportFunc();
            for (const op of unops) b.addFunction(op, kSig_i_i).addBody([kExprLocalGet, 0, eval("kExpr" + op)]).exportFunc();
            const e = b.instantiate().exports;
            const values = [0, 1, -1, 2, -2, 7, 0x7fffffff, -0x80000000, 31, 32, 33, 0x12345678, 0x80, 0x8000, 255, 65535, -3];
            for (const op of binops) {
              const out = [];
              for (const x of values) for (const y of values) out.push(run(e[op], x, y));
              print(op, out.join(" "));
            }
            for (const op of unops) print(op, values.map(x => run(e[op], x)).join(" "));
            """);
        Assert.Contains("I32Add 0 1 -1", output);
        Assert.Contains("RuntimeError: divide by zero", output);
        Assert.Contains("RuntimeError: divide result unrepresentable", output);
        Assert.Contains("RuntimeError: remainder by zero", output);
    }

    [Fact]
    public void I64Operations()
    {
        Both(Prelude + """
            const binops = ["I64Add","I64Sub","I64Mul","I64DivS","I64DivU","I64RemS","I64RemU","I64And","I64Ior","I64Xor",
              "I64Shl","I64ShrS","I64ShrU","I64Rol","I64Ror"];
            const cmps = ["I64Eq","I64Ne","I64LtS","I64LtU","I64GtS","I64GtU","I64LeS","I64LeU","I64GeS","I64GeU"];
            const unops = ["I64Clz","I64Ctz","I64Popcnt","I64SExtendI8","I64SExtendI16","I64SExtendI32"];
            const b = new WasmModuleBuilder();
            for (const op of binops) b.addFunction(op, kSig_l_ll).addBody([kExprLocalGet, 0, kExprLocalGet, 1, eval("kExpr" + op)]).exportFunc();
            for (const op of cmps) b.addFunction(op, makeSig([kWasmI64, kWasmI64], [kWasmI32])).addBody([kExprLocalGet, 0, kExprLocalGet, 1, eval("kExpr" + op)]).exportFunc();
            for (const op of unops) b.addFunction(op, kSig_l_l).addBody([kExprLocalGet, 0, eval("kExpr" + op)]).exportFunc();
            b.addFunction("eqz", kSig_i_l).addBody([kExprLocalGet, 0, kExprI64Eqz]).exportFunc();
            b.addFunction("wrap", kSig_i_l).addBody([kExprLocalGet, 0, kExprI32ConvertI64]).exportFunc();
            b.addFunction("extend_s", kSig_l_i).addBody([kExprLocalGet, 0, kExprI64SConvertI32]).exportFunc();
            b.addFunction("extend_u", kSig_l_i).addBody([kExprLocalGet, 0, kExprI64UConvertI32]).exportFunc();
            const e = b.instantiate().exports;
            const values = [0n, 1n, -1n, 2n, -2n, 63n, 64n, 65n, 0x7fffffffffffffffn, -0x8000000000000000n, 0x123456789abcdef0n,
              0xffffffffn, 0x100000000n, -0x100000000n, 255n, -3n];
            for (const op of [...binops, ...cmps]) {
              const out = [];
              for (const x of values) for (const y of values) out.push(run(e[op], x, y));
              print(op, out.join(" "));
            }
            for (const op of [...unops, "eqz", "wrap"]) print(op, values.map(x => run(e[op], x)).join(" "));
            for (const x of [0, 1, -1, 0x7fffffff, -0x80000000]) print(e.extend_s(x), e.extend_u(x));
            """);
    }

    [Fact]
    public void FloatOperations()
    {
        string output = Both(Prelude + """
            const b = new WasmModuleBuilder();
            const f32bin = ["F32Add","F32Sub","F32Mul","F32Div","F32Min","F32Max","F32CopySign"];
            const f32un = ["F32Abs","F32Neg","F32Ceil","F32Floor","F32Trunc","F32NearestInt","F32Sqrt"];
            const f32cmp = ["F32Eq","F32Ne","F32Lt","F32Gt","F32Le","F32Ge"];
            const f64bin = ["F64Add","F64Sub","F64Mul","F64Div","F64Min","F64Max","F64CopySign"];
            const f64un = ["F64Abs","F64Neg","F64Ceil","F64Floor","F64Trunc","F64NearestInt","F64Sqrt"];
            const f64cmp = ["F64Eq","F64Ne","F64Lt","F64Gt","F64Le","F64Ge"];
            const F = kExprF32ReinterpretI32, I = kExprI32ReinterpretF32, D = kExprF64ReinterpretI64, L = kExprI64ReinterpretF64;
            for (const op of f32bin) b.addFunction(op, kSig_i_ii).addBody([kExprLocalGet, 0, F, kExprLocalGet, 1, F, eval("kExpr" + op), I]).exportFunc();
            for (const op of f32un) b.addFunction(op, kSig_i_i).addBody([kExprLocalGet, 0, F, eval("kExpr" + op), I]).exportFunc();
            for (const op of f32cmp) b.addFunction(op, kSig_i_ii).addBody([kExprLocalGet, 0, F, kExprLocalGet, 1, F, eval("kExpr" + op)]).exportFunc();
            for (const op of f64bin) b.addFunction(op, kSig_l_ll).addBody([kExprLocalGet, 0, D, kExprLocalGet, 1, D, eval("kExpr" + op), L]).exportFunc();
            for (const op of f64un) b.addFunction(op, kSig_l_l).addBody([kExprLocalGet, 0, D, eval("kExpr" + op), L]).exportFunc();
            for (const op of f64cmp) b.addFunction(op, makeSig([kWasmI64, kWasmI64], [kWasmI32])).addBody([kExprLocalGet, 0, D, kExprLocalGet, 1, D, eval("kExpr" + op)]).exportFunc();
            const e = b.instantiate().exports;
            const f32s = [0, 0x80000000, 0x3f800000, 0xbf800000, 0x3fc00000, 0x40200000, 0xc0200000, 0x3f000000, 0xbf000000,
              0x7f800000, 0xff800000, 0x7fc00000, 0x7fa00000, 0xffc00001, 0x00000001, 0x7f7fffff, 0x4b000001, 0xcf000000];
            const f64s = [0n, 0x8000000000000000n, 0x3ff0000000000000n, 0xbff0000000000000n, 0x3ff8000000000000n,
              0x4004000000000000n, 0xc004000000000000n, 0x3fe0000000000000n, 0x7ff0000000000000n, 0xfff0000000000000n,
              0x7ff8000000000000n, 0x7ff4000000000000n, 0xfff8000000000001n, 1n, 0x7fefffffffffffffn, 0x4330000000000001n];
            const exact = ["F32Abs","F32Neg","F32CopySign","F64Abs","F64Neg","F64CopySign"];
            for (const op of f32bin) {
              const out = [];
              for (const x of f32s) for (const y of f32s) out.push(exact.includes(op) ? "0x" + (e[op](x, y) >>> 0).toString(16) : fmt32(e[op](x, y)));
              print(op, out.join(" "));
            }
            for (const op of f32un) print(op, f32s.map(x => exact.includes(op) ? "0x" + (e[op](x) >>> 0).toString(16) : fmt32(e[op](x))).join(" "));
            for (const op of f32cmp) { const out = []; for (const x of f32s) for (const y of f32s) out.push(e[op](x, y)); print(op, out.join("")); }
            for (const op of f64bin) {
              const out = [];
              for (const x of f64s) for (const y of f64s) out.push(exact.includes(op) ? "0x" + BigInt.asUintN(64, e[op](x, y)).toString(16) : fmt64(e[op](x, y)));
              print(op, out.join(" "));
            }
            for (const op of f64un) print(op, f64s.map(x => exact.includes(op) ? "0x" + BigInt.asUintN(64, e[op](x)).toString(16) : fmt64(e[op](x))).join(" "));
            for (const op of f64cmp) { const out = []; for (const x of f64s) for (const y of f64s) out.push(e[op](x, y)); print(op, out.join("")); }
            """);
        Assert.Contains("F32Neg 0x80000000 0x0", output);
    }

    [Fact]
    public void Conversions()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            const F = kExprF32ReinterpretI32, I = kExprI32ReinterpretF32, D = kExprF64ReinterpretI64, L = kExprI64ReinterpretF64;
            const fromF32 = ["I32SConvertF32","I32UConvertF32","I32SConvertSatF32","I32UConvertSatF32"];
            const fromF32to64 = ["I64SConvertF32","I64UConvertF32","I64SConvertSatF32","I64UConvertSatF32"];
            const fromF64 = ["I32SConvertF64","I32UConvertF64","I32SConvertSatF64","I32UConvertSatF64"];
            const fromF64to64 = ["I64SConvertF64","I64UConvertF64","I64SConvertSatF64","I64UConvertSatF64"];
            const op = n => { const v = eval("kExpr" + n); return n.includes("Sat") ? [kNumericPrefix, v] : [v]; };
            for (const n of fromF32) b.addFunction(n, kSig_i_i).addBody([kExprLocalGet, 0, F, ...op(n)]).exportFunc();
            for (const n of fromF32to64) b.addFunction(n, kSig_l_i).addBody([kExprLocalGet, 0, F, ...op(n)]).exportFunc();
            for (const n of fromF64) b.addFunction(n, kSig_i_l).addBody([kExprLocalGet, 0, D, ...op(n)]).exportFunc();
            for (const n of fromF64to64) b.addFunction(n, kSig_l_l).addBody([kExprLocalGet, 0, D, ...op(n)]).exportFunc();
            b.addFunction("f32_i32_s", kSig_i_i).addBody([kExprLocalGet, 0, kExprF32SConvertI32, I]).exportFunc();
            b.addFunction("f32_i32_u", kSig_i_i).addBody([kExprLocalGet, 0, kExprF32UConvertI32, I]).exportFunc();
            b.addFunction("f32_i64_s", kSig_i_l).addBody([kExprLocalGet, 0, kExprF32SConvertI64, I]).exportFunc();
            b.addFunction("f32_i64_u", kSig_i_l).addBody([kExprLocalGet, 0, kExprF32UConvertI64, I]).exportFunc();
            b.addFunction("f64_i32_s", kSig_l_i).addBody([kExprLocalGet, 0, kExprF64SConvertI32, L]).exportFunc();
            b.addFunction("f64_i32_u", kSig_l_i).addBody([kExprLocalGet, 0, kExprF64UConvertI32, L]).exportFunc();
            b.addFunction("f64_i64_s", kSig_l_l).addBody([kExprLocalGet, 0, kExprF64SConvertI64, L]).exportFunc();
            b.addFunction("f64_i64_u", kSig_l_l).addBody([kExprLocalGet, 0, kExprF64UConvertI64, L]).exportFunc();
            b.addFunction("demote", kSig_i_l).addBody([kExprLocalGet, 0, D, kExprF32ConvertF64, I]).exportFunc();
            b.addFunction("promote", kSig_l_i).addBody([kExprLocalGet, 0, F, kExprF64ConvertF32, L]).exportFunc();
            const e = b.instantiate().exports;
            const f32s = [0, 0x80000000, 0x3f800000, 0xbf800000, 0x3f7fffff, 0xbf7fffff, 0x4effffff, 0x4f000000, 0xcf000000,
              0xcf000001, 0x4f7fffff, 0x4f800000, 0x5effffff, 0x5f000000, 0xdf000000, 0xdf000001, 0x5f7fffff, 0x5f800000,
              0x7f800000, 0xff800000, 0x7fc00000];
            const f64s = [0n, 0x8000000000000000n, 0x3ff0000000000000n, 0xbfefffffffffffffn, 0x41dfffffffc00000n,
              0x41e0000000000000n, 0xc1e0000000200000n, 0xc1e0000000000000n, 0x41efffffffe00000n, 0x41f0000000000000n,
              0x43dfffffffffffffn, 0x43e0000000000000n, 0xc3e0000000000000n, 0xc3e0000000000001n, 0x43efffffffffffffn,
              0x43f0000000000000n, 0x7ff0000000000000n, 0x7ff8000000000000n];
            for (const n of [...fromF32, ...fromF32to64]) print(n, f32s.map(x => run(e[n], x)).join(" "));
            for (const n of [...fromF64, ...fromF64to64]) print(n, f64s.map(x => run(e[n], x)).join(" "));
            const i32s = [0, 1, -1, 0x7fffffff, -0x80000000, 0x1000001, 16777217];
            const i64s = [0n, 1n, -1n, 0x7fffffffffffffffn, -0x8000000000000000n, 0x8000008000000001n, 0x20000000000001n,
              0xffffffffffffffffn, 0x7fffff4000000001n, 0x8000000000000401n];
            for (const n of ["f32_i32_s", "f32_i32_u"]) print(n, i32s.map(x => fmt32(e[n](x))).join(" "));
            for (const n of ["f32_i64_s", "f32_i64_u"]) print(n, i64s.map(x => fmt32(e[n](x))).join(" "));
            for (const n of ["f64_i32_s", "f64_i32_u"]) print(n, i32s.map(x => fmt64(e[n](x))).join(" "));
            for (const n of ["f64_i64_s", "f64_i64_u"]) print(n, i64s.map(x => fmt64(e[n](x))).join(" "));
            print("demote", f64s.map(x => fmt32(e.demote(x))).join(" "));
            print("promote", f32s.map(x => fmt64(e.promote(x))).join(" "));
            """);
    }

    [Fact]
    public void MemoryAccesses()
    {
        string output = Both(Prelude + """
            const b = new WasmModuleBuilder();
            b.addMemory(1, 3);
            const loads = [["I32LoadMem", kSig_i_i], ["I64LoadMem", kSig_l_i], ["I32LoadMem8S", kSig_i_i], ["I32LoadMem8U", kSig_i_i],
              ["I32LoadMem16S", kSig_i_i], ["I32LoadMem16U", kSig_i_i], ["I64LoadMem8S", kSig_l_i], ["I64LoadMem8U", kSig_l_i],
              ["I64LoadMem16S", kSig_l_i], ["I64LoadMem16U", kSig_l_i], ["I64LoadMem32S", kSig_l_i], ["I64LoadMem32U", kSig_l_i]];
            for (const [n, sig] of loads) {
              b.addFunction(n, sig).addBody([kExprLocalGet, 0, eval("kExpr" + n), 0, 0]).exportFunc();
              b.addFunction(n + "_off", sig).addBody([kExprLocalGet, 0, eval("kExpr" + n), 0, 3]).exportFunc();
            }
            b.addFunction("f32", kSig_i_i).addBody([kExprLocalGet, 0, kExprF32LoadMem, 0, 0, kExprI32ReinterpretF32]).exportFunc();
            b.addFunction("f64", kSig_l_i).addBody([kExprLocalGet, 0, kExprF64LoadMem, 0, 0, kExprI64ReinterpretF64]).exportFunc();
            b.addFunction("st32", kSig_v_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32StoreMem, 0, 0]).exportFunc();
            b.addFunction("st8", kSig_v_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32StoreMem8, 0, 0]).exportFunc();
            b.addFunction("st16", kSig_v_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32StoreMem16, 0, 0]).exportFunc();
            b.addFunction("st64", makeSig([kWasmI32, kWasmI64], [])).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI64StoreMem, 0, 0]).exportFunc();
            b.addFunction("st64_32", makeSig([kWasmI32, kWasmI64], [])).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI64StoreMem32, 0, 0]).exportFunc();
            b.addFunction("stf32", kSig_v_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprF32ReinterpretI32, kExprF32StoreMem, 0, 0]).exportFunc();
            b.addFunction("size", kSig_i_v).addBody([kExprMemorySize, 0]).exportFunc();
            b.addFunction("grow", kSig_i_i).addBody([kExprLocalGet, 0, kExprMemoryGrow, 0]).exportFunc();
            b.addFunction("fill", kSig_v_iii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprLocalGet, 2, kNumericPrefix, kExprMemoryFill, 0]).exportFunc();
            b.addFunction("copy", kSig_v_iii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprLocalGet, 2, kNumericPrefix, kExprMemoryCopy, 0, 0]).exportFunc();
            const e = b.instantiate().exports;
            e.st64(0, 0x8899aabbccddeeffn);
            e.st32(65532, -0x12345678);
            e.stf32(16, 0x7fa00001);
            const addrs = [0, 1, 4, 16, 65528, 65531, 65532, 65533, 65535, 65536, -1, -8];
            for (const [n] of loads) {
              print(n, addrs.map(a => run(e[n], a)).join(" "));
              print(n + "_off", addrs.map(a => run(e[n + "_off"], a)).join(" "));
            }
            print("f32", run(e.f32, 16), "f64", run(e.f64, 0));
            print(run(e.st8, 65535, 0x1ff), run(e.st8, 65536, 1), run(e.st16, 65534, 1), run(e.st16, 65535, 1));
            print(run(e.st64_32, 65532, -1n), run(e.st64_32, 65533, 1n), run(e.st64, 65528, 1n), run(e.st64, 65529, 1n));
            print(e.size(), e.grow(1), e.size(), e.grow(5), e.size(), run(e.I32LoadMem, 65536), run(e.I32LoadMem, 131068), run(e.I32LoadMem, 131069));
            print(run(e.fill, 100, 0x41, 10), e.I64LoadMem(100), run(e.fill, 131000, 1, 100), run(e.fill, 131072, 1, 0), run(e.fill, 131073, 1, 0));
            print(run(e.copy, 200, 100, 10), e.I64LoadMem(202), run(e.copy, 102, 100, 20), e.I64LoadMem(104), run(e.copy, 0, 131070, 4));
            """);
        Assert.Contains("RuntimeError: memory access out of bounds", output);
    }

    [Fact]
    public void ControlFlowWithValues()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            const sig_ii_i = b.addType(makeSig([kWasmI32], [kWasmI32, kWasmI32]));
            const sig_i_ii = b.addType(makeSig([kWasmI32, kWasmI32], [kWasmI32]));
            // block with params and multiple results, br with values from nested blocks
            b.addFunction("multi", makeSig([kWasmI32], [kWasmI32, kWasmI64, kWasmF64])).addBody([
              kExprLocalGet, 0, kExprI64Const, 5, kExprF64Const, 0, 0, 0, 0, 0, 0, 0xf0, 0x3f]).exportFunc();
            b.addFunction("block_params", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprBlock, sig_ii_i, kExprLocalGet, 0, kExprI32Const, 3, kExprI32Mul, kExprEnd, kExprI32Sub]).exportFunc();
            b.addFunction("br_values", kSig_i_i).addBody([
              kExprBlock, kWasmI32,
                kExprI32Const, 10, kExprI32Const, 20,
                kExprBlock, kWasmI32, kExprI32Const, 7, kExprLocalGet, 0, kExprBrIf, 1, kExprDrop, kExprI32Const, 8, kExprEnd,
                kExprI32Add, kExprI32Add,
              kExprEnd]).exportFunc();
            b.addFunction("br_table", kSig_i_i).addBody([
              kExprBlock, kWasmVoid, kExprBlock, kWasmVoid, kExprBlock, kWasmVoid,
                kExprLocalGet, 0, kExprBrTable, 3, 0, 1, 2, 1,
              kExprEnd, ...wasmI32Const(100), kExprReturn,
              kExprEnd, ...wasmI32Const(101), kExprReturn,
              kExprEnd, ...wasmI32Const(102)]).exportFunc();
            b.addFunction("br_table_values", kSig_i_i).addBody([
              kExprBlock, kWasmI32, kExprBlock, kWasmI32,
                kExprI32Const, 55, kExprLocalGet, 0, kExprBrTable, 2, 0, 1, 0,
              kExprEnd, kExprI32Const, 1, kExprI32Add,
              kExprEnd]).exportFunc();
            b.addFunction("br_table_return", kSig_i_i).addBody([
              kExprBlock, kWasmI32, kExprI32Const, 9, kExprLocalGet, 0, kExprBrTable, 1, 1, 0, kExprEnd, kExprI32Const, 1, kExprI32Add]).exportFunc();
            b.addFunction("loop_params", kSig_i_i).addBody([
              kExprI32Const, 0, kExprLocalGet, 0,
              kExprLoop, sig_i_ii,
                kExprI32Add,
                kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprLocalTee, 0,
                kExprLocalGet, 0, kExprBrIf, 0,
                kExprDrop,
              kExprEnd]).exportFunc();
            b.addFunction("if_params", kSig_i_i).addBody([
              kExprI32Const, 4, kExprLocalGet, 0,
              kExprIf, b.addType(makeSig([kWasmI32], [kWasmI32])), kExprI32Const, 10, kExprI32Mul, kExprElse, kExprI32Const, 1, kExprI32Sub, kExprEnd]).exportFunc();
            b.addFunction("if_no_else", kSig_i_i).addBody([
              kExprI32Const, 4, kExprLocalGet, 0,
              kExprIf, b.addType(makeSig([kWasmI32], [kWasmI32])), kExprI32Const, 10, kExprI32Mul, kExprEnd]).exportFunc();
            b.addFunction("select", kSig_i_iii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprLocalGet, 2, kExprSelect]).exportFunc();
            b.addFunction("select_f64", makeSig([kWasmF64, kWasmF64, kWasmI32], [kWasmF64])).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprLocalGet, 2, kExprSelectWithType, 1, kWasmF64]).exportFunc();
            b.addFunction("local_tee_stack", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add, kExprLocalSet, 0, kExprLocalGet, 0, kExprI32Sub]).exportFunc();
            b.addFunction("unreachable_tail", kSig_i_i).addBody([
              kExprBlock, kWasmI32, kExprLocalGet, 0, kExprBr, 0, kExprI32Const, 1, kExprI32Add, kExprUnreachable, kExprEnd]).exportFunc();
            b.addFunction("trap", kSig_i_i).addBody([kExprLocalGet, 0, kExprIf, kWasmVoid, kExprUnreachable, kExprEnd, kExprI32Const, 3]).exportFunc();
            const e = b.instantiate().exports;
            print(e.multi(3));
            for (const x of [0, 1, 2, 3, 7, -1]) {
              print(x, e.block_params(x), e.br_values(x), e.br_table(x), e.br_table_values(x), e.br_table_return(x), e.loop_params(x > 0 ? x : 1),
                e.if_params(x), e.if_no_else(x), e.select(1, 2, x), e.select_f64(1.5, -0, x), e.local_tee_stack(x), e.unreachable_tail(x), run(e.trap, x));
            }
            """);
    }

    [Fact]
    public void Calls()
    {
        string output = Both(Prelude + """
            const b = new WasmModuleBuilder();
            const imp = b.addImport("m", "f", kSig_i_ii);
            const impMulti = b.addImport("m", "g", makeSig([kWasmI32], [kWasmI32, kWasmF64]));
            const sig = b.addType(kSig_i_ii);
            const sigL = b.addType(kSig_l_l);
            b.addTable(kWasmFuncRef, 4, 4);
            const add = b.addFunction("add", kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32Add]).exportFunc();
            const sub = b.addFunction("sub", kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32Sub]).exportFunc();
            const neg = b.addFunction("neg", kSig_l_l).addBody([kExprI64Const, 0, kExprLocalGet, 0, kExprI64Sub]).exportFunc();
            b.addActiveElementSegment(0, wasmI32Const(0), [add.index, sub.index, neg.index]);
            b.addFunction("indirect", kSig_i_iii).addBody([kExprLocalGet, 1, kExprLocalGet, 2, kExprLocalGet, 0, kExprCallIndirect, sig, 0]).exportFunc();
            b.addFunction("call_import", kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, imp, kExprI32Const, 1, kExprI32Add]).exportFunc();
            b.addFunction("call_multi", makeSig([kWasmI32], [kWasmI32, kWasmF64])).addBody([kExprLocalGet, 0, kExprCallFunction, impMulti]).exportFunc();
            b.addFunction("call_ref", kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprRefFunc, add.index, kExprCallRef, add.type_index]).exportFunc();
            b.addFunction("fact", kSig_l_l).addBody([
              kExprLocalGet, 0, kExprI64Eqz, kExprIf, kWasmI64, kExprI64Const, 1,
              kExprElse, kExprLocalGet, 0, kExprLocalGet, 0, kExprI64Const, 1, kExprI64Sub, kExprCallFunction, 9, kExprI64Mul, kExprEnd]).exportFunc();
            b.addFunction("many", makeSig([kWasmI32, kWasmI64, kWasmF32, kWasmF64, kWasmI32, kWasmI64, kWasmF32, kWasmF64, kWasmI32, kWasmI32,
              kWasmI32, kWasmI32, kWasmI32, kWasmI32, kWasmI32, kWasmI32, kWasmI32, kWasmI32], [kWasmF64])).addBody([
              kExprLocalGet, 3, kExprLocalGet, 7, kExprF64Add, kExprLocalGet, 17, kExprF64SConvertI32, kExprF64Add]).exportFunc();
            const e = b.instantiate({m: {f: (x, y) => x * y, g: x => [x + 1, x / 2]}}).exports;
            print(e.add(1, 2), e.indirect(0, 5, 3), e.indirect(1, 5, 3), run(e.indirect, 2, 5, 3), run(e.indirect, 3, 1, 1), run(e.indirect, 4, 1, 1));
            print(e.call_import(6, 7), e.call_multi(5), e.call_ref(4, 5), e.fact(20n));
            print(e.many(1, 2n, 3, 4.5, 5, 6n, 7, 8.25, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18));
            """);
        Assert.Contains("RuntimeError: null function", output);
        Assert.Contains("RuntimeError: function signature mismatch", output);
        Assert.Contains("RuntimeError: table index is out of bounds", output);
    }

    [Fact]
    public void TailCalls()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            const sig = b.addType(kSig_i_ii);
            b.addTable(kWasmFuncRef, 2, 2);
            // count(n, acc): a million deep, which only a tail call survives.
            const count = b.addFunction("count", kSig_i_ii).addBody([
              kExprLocalGet, 0, kExprI32Eqz, kExprIf, kWasmI32, kExprLocalGet, 1,
              kExprElse, kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add,
              kExprReturnCall, 0, kExprEnd]).exportFunc();
            const even = b.addFunction("even", kSig_i_ii);
            const odd = b.addFunction("odd", kSig_i_ii);
            even.addBody([kExprLocalGet, 0, kExprI32Eqz, kExprIf, kWasmI32, kExprI32Const, 1, kExprElse,
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprLocalGet, 1, kExprI32Const, 1, kExprReturnCallIndirect, sig, 0, kExprEnd]).exportFunc();
            odd.addBody([kExprLocalGet, 0, kExprI32Eqz, kExprIf, kWasmI32, kExprI32Const, 0, kExprElse,
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprLocalGet, 1, kExprI32Const, 0, kExprReturnCallIndirect, sig, 0, kExprEnd]).exportFunc();
            b.addActiveElementSegment(0, wasmI32Const(0), [even.index, odd.index]);
            const e = b.instantiate().exports;
            print(e.count(1000000, 0), e.even(100001, 0), e.even(250000, 0));
            """);
    }

    [Fact]
    public void Exceptions()
    {
        string output = Both(Prelude + """
            const b = new WasmModuleBuilder();
            const tag = b.addTag(kSig_v_i);
            const tag2 = b.addTag(makeSig([kWasmI64, kWasmF64], []));
            const imp = b.addImport("m", "thrower", kSig_v_i);
            b.addExportOfKind("tag", kExternalTag, tag);
            const thrower = b.addFunction("thrower", kSig_v_i).addBody([
              kExprLocalGet, 0, kExprI32Eqz, kExprBrIf, 0,
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Eq, kExprIf, kWasmVoid, kExprLocalGet, 0, kExprThrow, tag, kExprEnd,
              kExprLocalGet, 0, kExprI32Const, 2, kExprI32Eq, kExprIf, kWasmVoid, kExprI64Const, 7, kExprF64Const, 0, 0, 0, 0, 0, 0, 0x10, 0x40, kExprThrow, tag2, kExprEnd,
              kExprLocalGet, 0, kExprI32Const, 3, kExprI32Eq, kExprIf, kWasmVoid, kExprLocalGet, 0, kExprCallFunction, imp, kExprEnd,
              kExprUnreachable]).exportFunc();
            b.addFunction("catcher", kSig_i_i).addBody([
              kExprBlock, kWasmI32,
                kExprBlock, b.addType(makeSig([], [kWasmI64, kWasmF64])),
                  kExprBlock, kWasmVoid,
                    kExprTryTable, kWasmVoid, 3, kCatchNoRef, tag, 2, kCatchNoRef, tag2, 1, kCatchAllNoRef, 0,
                      kExprLocalGet, 0, kExprCallFunction, thrower.index,
                    kExprEnd,
                    ...wasmI32Const(100), kExprReturn,
                  kExprEnd,
                  ...wasmI32Const(200), kExprReturn,
                kExprEnd,
                kExprDrop, kExprI32ConvertI64, kExprReturn,
              kExprEnd,
              ...wasmI32Const(1000), kExprI32Add]).exportFunc();
            b.addFunction("catch_ref", kSig_i_i).addLocals(kWasmExnRef, 1).addBody([
              kExprBlock, b.addType(makeSig([], [kWasmI32, kWasmExnRef])),
                kExprTryTable, kWasmVoid, 1, kCatchRef, tag, 0,
                  kExprLocalGet, 0, kExprCallFunction, thrower.index,
                kExprEnd,
                kExprI32Const, 5, kExprReturn,
              kExprEnd,
              kExprLocalSet, 1,
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Eq, kExprIf, kWasmVoid, kExprLocalGet, 1, kExprThrowRef, kExprEnd]).exportFunc();
            const e = b.instantiate({m: {thrower: x => { throw new Error("from js " + x); }}}).exports;
            for (const x of [0, 1, 2, 3, 4]) print(x, run(e.catcher, x), run(e.catch_ref, x));
            try { e.thrower(1); } catch (ex) { print(ex instanceof WebAssembly.Exception, ex.is(e.tag), ex.getArg(e.tag, 0)); }
            """);
        Assert.Contains("1 1001", output);
        Assert.Contains("RuntimeError: unreachable", output);
    }

    [Fact]
    public void LegacyExceptions()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            const tag = b.addTag(kSig_v_i);
            const thrower = b.addFunction("thrower", kSig_v_i).addBody([kExprLocalGet, 0, kExprThrow, tag]).exportFunc();
            b.addFunction("try_catch", kSig_i_i).addBody([
              kExprTry, kWasmI32,
                kExprLocalGet, 0, kExprIf, kWasmVoid, kExprLocalGet, 0, kExprCallFunction, thrower.index, kExprEnd,
                kExprI32Const, 1,
              kExprCatch, tag,
                ...wasmI32Const(100), kExprI32Add,
              kExprCatchAll,
                ...wasmI32Const(-1),
              kExprEnd]).exportFunc();
            b.addFunction("rethrow", kSig_i_i).addBody([
              kExprTry, kWasmI32,
                kExprTry, kWasmI32,
                  kExprLocalGet, 0, kExprCallFunction, thrower.index, kExprI32Const, 0,
                kExprCatch, tag,
                  kExprDrop, kExprRethrow, 0,
                kExprEnd,
              kExprCatch, tag,
                ...wasmI32Const(1000), kExprI32Add,
              kExprEnd]).exportFunc();
            b.addFunction("delegate", kSig_i_i).addBody([
              kExprTry, kWasmI32,
                kExprTry, kWasmI32,
                  kExprTry, kWasmI32,
                    kExprLocalGet, 0, kExprCallFunction, thrower.index, kExprI32Const, 0,
                  kExprDelegate, 1,
                kExprCatch, tag,
                  kExprI32Const, 7, kExprI32Add,
                kExprEnd,
              kExprCatch, tag,
                ...wasmI32Const(9000), kExprI32Add,
              kExprEnd]).exportFunc();
            b.addFunction("delegate_out", kSig_i_i).addBody([
              kExprTry, kWasmI32,
                kExprTry, kWasmI32,
                  kExprLocalGet, 0, kExprCallFunction, thrower.index, kExprI32Const, 0,
                kExprDelegate, 1,
              kExprCatch, tag,
                kExprI32Const, 5, kExprI32Add,
              kExprEnd]).exportFunc();
            const e = b.instantiate().exports;
            for (const x of [0, 3]) print(x, run(e.try_catch, x), run(e.rethrow, x), run(e.delegate, x), run(e.delegate_out, x));
            """);
    }

    [Fact]
    public void GlobalsAndTables()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            const imported = b.addImportedGlobal("m", "g", kWasmI32, false);
            const gi = b.addGlobal(kWasmI32, true, false, wasmI32Const(5));
            const gl = b.addGlobal(kWasmI64, true, false, [kExprI64Const, 3]);
            const gf = b.addGlobal(kWasmF64, true, false, [kExprF64Const, 0, 0, 0, 0, 0, 0, 0xf0, 0x3f]);
            const gr = b.addGlobal(kWasmExternRef, true, false, [kExprRefNull, kExternRefCode]);
            b.addTable(kWasmExternRef, 2, 10).exportAs("t");
            b.addFunction("bump", kSig_i_v).addBody([
              kExprGlobalGet, gi.index, kExprI32Const, 1, kExprI32Add, kExprGlobalSet, gi.index, kExprGlobalGet, gi.index,
              kExprGlobalGet, imported, kExprI32Add]).exportFunc();
            b.addFunction("bump64", kSig_l_v).addBody([kExprGlobalGet, gl.index, kExprI64Const, 2, kExprI64Mul, kExprGlobalSet, gl.index, kExprGlobalGet, gl.index]).exportFunc();
            b.addFunction("half", kSig_d_v).addBody([kExprGlobalGet, gf.index, kExprF64Const, 0, 0, 0, 0, 0, 0, 0, 0x40, kExprF64Div, kExprGlobalSet, gf.index, kExprGlobalGet, gf.index]).exportFunc();
            b.addFunction("setref", makeSig([kWasmExternRef], [])).addBody([kExprLocalGet, 0, kExprGlobalSet, gr.index]).exportFunc();
            b.addFunction("getref", makeSig([], [kWasmExternRef])).addBody([kExprGlobalGet, gr.index]).exportFunc();
            b.addFunction("tget", makeSig([kWasmI32], [kWasmExternRef])).addBody([kExprLocalGet, 0, kExprTableGet, 0]).exportFunc();
            b.addFunction("tset", makeSig([kWasmI32, kWasmExternRef], [])).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprTableSet, 0]).exportFunc();
            b.addFunction("tgrow", makeSig([kWasmExternRef, kWasmI32], [kWasmI32])).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kNumericPrefix, kExprTableGrow, 0]).exportFunc();
            b.addFunction("tsize", kSig_i_v).addBody([kNumericPrefix, kExprTableSize, 0]).exportFunc();
            b.addFunction("isnull", makeSig([kWasmExternRef], [kWasmI32])).addBody([kExprLocalGet, 0, kExprRefIsNull]).exportFunc();
            const e = b.instantiate({m: {g: 100}}).exports;
            print(e.bump(), e.bump(), e.bump64(), e.bump64(), e.half(), e.half());
            const o = {x: 1};
            e.setref(o); print(e.getref() === o, e.isnull(null), e.isnull(o));
            e.tset(1, "hi"); print(e.tget(1), e.tget(0), run(e.tget, 2), run(e.tset, 5, 1), e.tgrow("z", 3), e.tsize(), e.tget(4), e.tgrow(null, 100));
            """);
    }

    [Fact]
    public void GCObjects()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            const point = b.addStruct([makeField(kWasmI32, true), makeField(kWasmF64, true)]);
            const byteArray = b.addArray(kWasmI8);
            b.addFunction("point", makeSig([kWasmI32, kWasmF64], [kWasmF64])).addBody([
              kExprLocalGet, 0, kExprLocalGet, 1, kGCPrefix, kExprStructNew, point,
              kGCPrefix, kExprStructGet, point, 1]).exportFunc();
            b.addFunction("array", kSig_i_ii).addBody([
              kExprLocalGet, 0, kExprLocalGet, 1, kGCPrefix, kExprArrayNew, byteArray,
              kExprI32Const, 2, kGCPrefix, kExprArrayGetS, byteArray]).exportFunc();
            b.addFunction("cast_i31", kSig_i_i).addBody([
              kExprBlock, kAnyRefCode,
                kExprLocalGet, 0, kGCPrefix, kExprRefI31,
                kGCPrefix, kExprBrOnCast, 0b11, 0, kAnyRefCode, kStructRefCode,
                kExprDrop, ...wasmI32Const(-1), kExprReturn,
              kExprEnd, kExprDrop, ...wasmI32Const(99)]).exportFunc();
            b.addFunction("cast_struct", kSig_i_i).addBody([
              kExprBlock, kAnyRefCode,
                kExprLocalGet, 0, kExprF64Const, 0, 0, 0, 0, 0, 0, 0, 0, kGCPrefix, kExprStructNew, point,
                kGCPrefix, kExprBrOnCast, 0b11, 0, kAnyRefCode, kStructRefCode,
                kExprDrop, ...wasmI32Const(-1), kExprReturn,
              kExprEnd, kExprDrop, ...wasmI32Const(99)]).exportFunc();
            b.addFunction("i31", kSig_i_i).addBody([kExprLocalGet, 0, kGCPrefix, kExprRefI31, kGCPrefix, kExprI31GetS]).exportFunc();
            const e = b.instantiate().exports;
            print(e.point(1, 2.5), e.array(5, 200), run(e.array, 2, 1), e.cast_i31(-42), e.cast_struct(1), e.i31(-42));
            """);
    }

    [Fact]
    public void Simd()
    {
        Both(Prelude + """
            const b = new WasmModuleBuilder();
            b.addMemory(1, 1);
            b.addFunction("dot", kSig_i_v).addBody([
              kExprI32Const, 0, kSimdPrefix, kExprS128LoadMem, 0, 0,
              kExprI32Const, 16, kSimdPrefix, kExprS128LoadMem, 0, 0,
              ...SimdInstr(kExprI32x4Mul),
              kSimdPrefix, kExprI32x4ExtractLane, 3]).exportFunc();
            b.addFunction("store", kSig_v_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32StoreMem, 0, 0]).exportFunc();
            const e = b.instantiate().exports;
            for (let i = 0; i < 8; i++) e.store(i * 4, i + 2);
            print(e.dot());
            """);
    }

    [Fact]
    public void SimdOperations()
    {
        // Every SIMD operation on vectors, compiled (Vector128) against the
        // interpreter, on random and special lane values. Float results print
        // NaN lanes as "nan" (wasm leaves their payload open), except abs and
        // neg, which only change the sign bit.
        Both("""
            const simdOps = [
              [0xe,"s_ss","I8x16Swizzle"],[0x23,"s_ss","I8x16Eq"],[0x24,"s_ss","I8x16Ne"],[0x25,"s_ss","I8x16LtS"],[0x26,"s_ss","I8x16LtU"],
              [0x27,"s_ss","I8x16GtS"],[0x28,"s_ss","I8x16GtU"],[0x29,"s_ss","I8x16LeS"],[0x2a,"s_ss","I8x16LeU"],[0x2b,"s_ss","I8x16GeS"],
              [0x2c,"s_ss","I8x16GeU"],[0x2d,"s_ss","I16x8Eq"],[0x2e,"s_ss","I16x8Ne"],[0x2f,"s_ss","I16x8LtS"],[0x30,"s_ss","I16x8LtU"],
              [0x31,"s_ss","I16x8GtS"],[0x32,"s_ss","I16x8GtU"],[0x33,"s_ss","I16x8LeS"],[0x34,"s_ss","I16x8LeU"],[0x35,"s_ss","I16x8GeS"],
              [0x36,"s_ss","I16x8GeU"],[0x37,"s_ss","I32x4Eq"],[0x38,"s_ss","I32x4Ne"],[0x39,"s_ss","I32x4LtS"],[0x3a,"s_ss","I32x4LtU"],
              [0x3b,"s_ss","I32x4GtS"],[0x3c,"s_ss","I32x4GtU"],[0x3d,"s_ss","I32x4LeS"],[0x3e,"s_ss","I32x4LeU"],[0x3f,"s_ss","I32x4GeS"],
              [0x40,"s_ss","I32x4GeU"],[0x41,"s_ss","F32x4Eq"],[0x42,"s_ss","F32x4Ne"],[0x43,"s_ss","F32x4Lt"],[0x44,"s_ss","F32x4Gt"],
              [0x45,"s_ss","F32x4Le"],[0x46,"s_ss","F32x4Ge"],[0x47,"s_ss","F64x2Eq"],[0x48,"s_ss","F64x2Ne"],[0x49,"s_ss","F64x2Lt"],
              [0x4a,"s_ss","F64x2Gt"],[0x4b,"s_ss","F64x2Le"],[0x4c,"s_ss","F64x2Ge"],[0x4d,"s_s","S128Not"],[0x4e,"s_ss","S128And"],
              [0x4f,"s_ss","S128AndNot"],[0x50,"s_ss","S128Or"],[0x51,"s_ss","S128Xor"],[0x52,"s_sss","S128Select"],
              [0x53,"i_s","V128AnyTrue"],[0x5e,"s_s","F32x4DemoteF64x2Zero"],[0x5f,"s_s","F64x2PromoteLowF32x4"],[0x60,"s_s","I8x16Abs"],
              [0x61,"s_s","I8x16Neg"],[0x62,"s_s","I8x16Popcnt"],[0x63,"i_s","I8x16AllTrue"],[0x64,"i_s","I8x16BitMask"],
              [0x65,"s_ss","I8x16SConvertI16x8"],[0x66,"s_ss","I8x16UConvertI16x8"],[0x67,"s_s","F32x4Ceil"],[0x68,"s_s","F32x4Floor"],
              [0x69,"s_s","F32x4Trunc"],[0x6a,"s_s","F32x4NearestInt"],[0x6b,"s_si","I8x16Shl"],[0x6c,"s_si","I8x16ShrS"],
              [0x6d,"s_si","I8x16ShrU"],[0x6e,"s_ss","I8x16Add"],[0x6f,"s_ss","I8x16AddSatS"],[0x70,"s_ss","I8x16AddSatU"],
              [0x71,"s_ss","I8x16Sub"],[0x72,"s_ss","I8x16SubSatS"],[0x73,"s_ss","I8x16SubSatU"],[0x74,"s_s","F64x2Ceil"],
              [0x75,"s_s","F64x2Floor"],[0x76,"s_ss","I8x16MinS"],[0x77,"s_ss","I8x16MinU"],[0x78,"s_ss","I8x16MaxS"],
              [0x79,"s_ss","I8x16MaxU"],[0x7a,"s_s","F64x2Trunc"],[0x7b,"s_ss","I8x16RoundingAverageU"],
              [0x7c,"s_s","I16x8ExtAddPairwiseI8x16S"],[0x7d,"s_s","I16x8ExtAddPairwiseI8x16U"],[0x7e,"s_s","I32x4ExtAddPairwiseI16x8S"],
              [0x7f,"s_s","I32x4ExtAddPairwiseI16x8U"],[0x80,"s_s","I16x8Abs"],[0x81,"s_s","I16x8Neg"],[0x82,"s_ss","I16x8Q15MulRSatS"],
              [0x83,"i_s","I16x8AllTrue"],[0x84,"i_s","I16x8BitMask"],[0x85,"s_ss","I16x8SConvertI32x4"],[0x86,"s_ss","I16x8UConvertI32x4"],
              [0x87,"s_s","I16x8SConvertI8x16Low"],[0x88,"s_s","I16x8SConvertI8x16High"],[0x89,"s_s","I16x8UConvertI8x16Low"],
              [0x8a,"s_s","I16x8UConvertI8x16High"],[0x8b,"s_si","I16x8Shl"],[0x8c,"s_si","I16x8ShrS"],[0x8d,"s_si","I16x8ShrU"],
              [0x8e,"s_ss","I16x8Add"],[0x8f,"s_ss","I16x8AddSatS"],[0x90,"s_ss","I16x8AddSatU"],[0x91,"s_ss","I16x8Sub"],
              [0x92,"s_ss","I16x8SubSatS"],[0x93,"s_ss","I16x8SubSatU"],[0x94,"s_s","F64x2NearestInt"],[0x95,"s_ss","I16x8Mul"],
              [0x96,"s_ss","I16x8MinS"],[0x97,"s_ss","I16x8MinU"],[0x98,"s_ss","I16x8MaxS"],[0x99,"s_ss","I16x8MaxU"],
              [0x9b,"s_ss","I16x8RoundingAverageU"],[0x9c,"s_ss","I16x8ExtMulLowI8x16S"],[0x9d,"s_ss","I16x8ExtMulHighI8x16S"],
              [0x9e,"s_ss","I16x8ExtMulLowI8x16U"],[0x9f,"s_ss","I16x8ExtMulHighI8x16U"],[0xa0,"s_s","I32x4Abs"],[0xa1,"s_s","I32x4Neg"],
              [0xa3,"i_s","I32x4AllTrue"],[0xa4,"i_s","I32x4BitMask"],[0xa7,"s_s","I32x4SConvertI16x8Low"],
              [0xa8,"s_s","I32x4SConvertI16x8High"],[0xa9,"s_s","I32x4UConvertI16x8Low"],[0xaa,"s_s","I32x4UConvertI16x8High"],
              [0xab,"s_si","I32x4Shl"],[0xac,"s_si","I32x4ShrS"],[0xad,"s_si","I32x4ShrU"],[0xae,"s_ss","I32x4Add"],[0xb1,"s_ss","I32x4Sub"],
              [0xb5,"s_ss","I32x4Mul"],[0xb6,"s_ss","I32x4MinS"],[0xb7,"s_ss","I32x4MinU"],[0xb8,"s_ss","I32x4MaxS"],
              [0xb9,"s_ss","I32x4MaxU"],[0xba,"s_ss","I32x4DotI16x8S"],[0xbc,"s_ss","I32x4ExtMulLowI16x8S"],
              [0xbd,"s_ss","I32x4ExtMulHighI16x8S"],[0xbe,"s_ss","I32x4ExtMulLowI16x8U"],[0xbf,"s_ss","I32x4ExtMulHighI16x8U"],
              [0xc0,"s_s","I64x2Abs"],[0xc1,"s_s","I64x2Neg"],[0xc3,"i_s","I64x2AllTrue"],[0xc4,"i_s","I64x2BitMask"],
              [0xc7,"s_s","I64x2SConvertI32x4Low"],[0xc8,"s_s","I64x2SConvertI32x4High"],[0xc9,"s_s","I64x2UConvertI32x4Low"],
              [0xca,"s_s","I64x2UConvertI32x4High"],[0xcb,"s_si","I64x2Shl"],[0xcc,"s_si","I64x2ShrS"],[0xcd,"s_si","I64x2ShrU"],
              [0xce,"s_ss","I64x2Add"],[0xd1,"s_ss","I64x2Sub"],[0xd5,"s_ss","I64x2Mul"],[0xd6,"s_ss","I64x2Eq"],[0xd7,"s_ss","I64x2Ne"],
              [0xd8,"s_ss","I64x2LtS"],[0xd9,"s_ss","I64x2GtS"],[0xda,"s_ss","I64x2LeS"],[0xdb,"s_ss","I64x2GeS"],
              [0xdc,"s_ss","I64x2ExtMulLowI32x4S"],[0xdd,"s_ss","I64x2ExtMulHighI32x4S"],[0xde,"s_ss","I64x2ExtMulLowI32x4U"],
              [0xdf,"s_ss","I64x2ExtMulHighI32x4U"],[0xe0,"s_s","F32x4Abs"],[0xe1,"s_s","F32x4Neg"],[0xe3,"s_s","F32x4Sqrt"],
              [0xe4,"s_ss","F32x4Add"],[0xe5,"s_ss","F32x4Sub"],[0xe6,"s_ss","F32x4Mul"],[0xe7,"s_ss","F32x4Div"],[0xe8,"s_ss","F32x4Min"],
              [0xe9,"s_ss","F32x4Max"],[0xea,"s_ss","F32x4Pmin"],[0xeb,"s_ss","F32x4Pmax"],[0xec,"s_s","F64x2Abs"],[0xed,"s_s","F64x2Neg"],
              [0xef,"s_s","F64x2Sqrt"],[0xf0,"s_ss","F64x2Add"],[0xf1,"s_ss","F64x2Sub"],[0xf2,"s_ss","F64x2Mul"],[0xf3,"s_ss","F64x2Div"],
              [0xf4,"s_ss","F64x2Min"],[0xf5,"s_ss","F64x2Max"],[0xf6,"s_ss","F64x2Pmin"],[0xf7,"s_ss","F64x2Pmax"],
              [0xf8,"s_s","I32x4SConvertF32x4"],[0xf9,"s_s","I32x4UConvertF32x4"],[0xfa,"s_s","F32x4SConvertI32x4"],
              [0xfb,"s_s","F32x4UConvertI32x4"],[0xfc,"s_s","I32x4TruncSatF64x2SZero"],[0xfd,"s_s","I32x4TruncSatF64x2UZero"],
              [0xfe,"s_s","F64x2ConvertLowI32x4S"],[0xff,"s_s","F64x2ConvertLowI32x4U"]];
            const b = new WasmModuleBuilder();
            b.addMemory(1, 1);
            const ld = off => [...wasmI32Const(off), kSimdPrefix, kExprS128LoadMem, 0, 0];
            for (const [code, sig, name] of simdOps) {
              const op = SimdInstr(code);
              let body;
              if (sig == "s_s") body = [...wasmI32Const(64), ...ld(0), ...op, kSimdPrefix, kExprS128StoreMem, 0, 0];
              else if (sig == "s_ss") body = [...wasmI32Const(64), ...ld(0), ...ld(16), ...op, kSimdPrefix, kExprS128StoreMem, 0, 0];
              else if (sig == "s_sss") body = [...wasmI32Const(64), ...ld(0), ...ld(16), ...ld(32), ...op, kSimdPrefix, kExprS128StoreMem, 0, 0];
              else if (sig == "s_si") body = [...wasmI32Const(64), ...ld(0), ...wasmI32Const(48), kExprI32LoadMem, 0, 0, ...op, kSimdPrefix, kExprS128StoreMem, 0, 0];
              else body = [...wasmI32Const(64), ...ld(0), ...op, kExprI32StoreMem, 0, 0];
              b.addFunction(name, kSig_v_v).addBody(body).exportFunc();
            }
            b.exportMemoryAs("mem");
            const e = b.instantiate().exports;
            const mem8 = new Uint8Array(e.mem.buffer);
            let seed = 12345;
            function rnd() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed >> 7; }
            const f32s = [0, -0, 1, -1, 1.5, -2.5, 0.5, -0.5, NaN, Infinity, -Infinity, 3.4e38, -3.4e38, 1e-45, 2147483648, -2147483904,
              4294967296, 65535.5, -0.75, 2.5];
            const f64s = [0, -0, 1, -1, 1.5, -2.5, NaN, Infinity, -Infinity, 1.7e308, 5e-324, 2147483647.5, -2147483648.9, 4294967295.5,
              -0.5, 0.5];
            function fill(set) {
              const dv = new DataView(e.mem.buffer);
              for (let i = 0; i < 48; i++) mem8[i] = rnd() & 0xff;
              if (set == 1) for (let i = 0; i < 12; i++) dv.setFloat32(i * 4, f32s[rnd() % f32s.length], true);
              if (set == 2) for (let i = 0; i < 6; i++) dv.setFloat64(i * 8, f64s[rnd() % f64s.length], true);
              if (set == 3) for (let i = 0; i < 48; i++) mem8[i] = [0, 0x80, 0x7f, 0xff, 1, 0xfe][rnd() % 6];
              dv.setInt32(48, [0, 1, 7, 8, 15, 16, 31, 33, 63, 64, -1][rnd() % 11], true);
            }
            function show(name, sig) {
              if (sig == "i_s") return String(new Int32Array(e.mem.buffer, 64, 1)[0]);
              const exact = /Abs|Neg|Eq|Ne|Lt|Gt|Le|Ge/.test(name) || !/^F(32|64)/.test(name);
              if (exact) return Array.from(mem8.subarray(64, 80)).map(x => x.toString(16)).join(".");
              const lanes = name.startsWith("F32") ? new Float32Array(e.mem.buffer, 64, 4) : new Float64Array(e.mem.buffer, 64, 2);
              return Array.from(lanes).map(x => Number.isNaN(x) ? "nan" : Object.is(x, -0) ? "-0" : String(x)).join(",");
            }
            for (const [code, sig, name] of simdOps) {
              const out = [];
              for (let set = 0; set < 4; set++) for (let k = 0; k < 4; k++) { fill(set); e[name](); out.push(show(name, sig)); }
              print(name, out.join(" "));
            }
            """);
    }

    [Fact]
    public void TrapStackTraces()
    {
        string output = Both(Prelude + """
            const b = new WasmModuleBuilder();
            const imp = b.addImport("m", "f", kSig_v_v);
            const inner = b.addFunction("inner", kSig_i_i).addBody([kExprLocalGet, 0, kExprI32LoadMem, 0, 0]);
            const mid = b.addFunction("mid", kSig_i_i).addBody([kExprNop, kExprLocalGet, 0, kExprCallFunction, inner.index]);
            b.addMemory(1, 1);
            b.addFunction("outer", kSig_i_i).addBody([kExprLocalGet, 0, kExprCallFunction, mid.index]).exportFunc();
            b.addFunction("callsjs", kSig_v_v).addBody([kExprNop, kExprCallFunction, imp]).exportFunc();
            b.addFunction("recurse", kSig_i_i).addBody([kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add, kExprCallFunction, 5]).exportFunc();
            const e = b.instantiate({m: {f: () => { print(new Error("in js").stack); }}}).exports;
            try { e.outer(70000); } catch (ex) { print(ex.stack); }
            e.callsjs();
            try { e.recurse(0); } catch (ex) { print(ex instanceof RangeError, ex.message); }
            """);
        Assert.Contains("at inner (wasm://wasm/", output);
        Assert.Contains("at callsjs (wasm://wasm/", output);
        Assert.Contains("true Maximum call stack size exceeded", output);
    }

    [Fact]
    public void LazyAndEagerCompilationAgree()
    {
        string source = Prelude + """
            const b = new WasmModuleBuilder();
            b.addFunction("f", kSig_i_i).addBody([kExprLocalGet, 0, kExprI32Const, 3, kExprI32Mul]).exportFunc();
            b.addFunction("g", kSig_i_i).addBody([kExprLocalGet, 0, kExprCallFunction, 0, kExprI32Const, 1, kExprI32Add]).exportFunc();
            const e = b.instantiate().exports;
            print(e.g(5), e.f(2));
            """;
        Assert.Equal("16 6\n", Compiled(source));
        Assert.Equal("16 6\n", Compiled(source, "--no-wasm-lazy-compilation"));
    }
}
