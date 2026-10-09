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
