// Differential tests of wasm inlining (src/V8Sharp/Wasm/Baseline/
// WasmInliningTree.cs, LiftoffCompiler.Inlining.cs; V8's
// src/wasm/inlining-tree.h and the inlining of turboshaft-graph-interface.cc):
// each script runs in the interpreter, compiled without inlining, compiled
// with inlining (the default) and compiled with a tiering budget so small
// that functions tier up at once (speculative call_indirect/call_ref
// inlining from feedback), and all four outputs must be the same. The
// scripts follow test/mjsunit/wasm/inlining.js and speculative-inlining.js:
// callee shapes, recursion, tail calls, traps and their stack traces,
// exceptions across inlined frames, and targets that change after the
// speculation.
using V8Sharp.Wasm;

namespace V8Sharp.Wasm.Tests;

public class WasmInliningTests
{
    const string NoInlining = "--allow-natives-syntax --no-wasm-inlining";
    const string Inlining = "--allow-natives-syntax";
    const string EagerTierUp = "--allow-natives-syntax --wasm-tiering-budget=200";

    /// <summary>
    /// Runs <paramref name="source"/> interpreted and compiled three ways,
    /// asserts the same output, and returns it with the number of calls the
    /// default configuration inlined.
    /// </summary>
    static (string Output, int Inlined) AllTiers(string source, bool compareInterpreter = true)
    {
        string compiled = WasmJsTester.Run(source, flags: NoInlining);
        string interpreted = compareInterpreter
            ? WasmJsTester.Run(source, interpret: true, flags: "--allow-natives-syntax")
            : compiled;
        int inlined = 0;
        string inlinedOutput = WasmJsTester.Run(source, flags: Inlining, inspect: isolate =>
        {
            foreach (WasmCode code in WasmEngine.Get(isolate).AllCode()) inlined += code.InlinedCalls;
        });
        string tieredUp = WasmJsTester.Run(source, flags: EagerTierUp);
        Assert.False(interpreted.Contains("uncaught", StringComparison.Ordinal), interpreted);
        Assert.Equal(interpreted, compiled);
        Assert.Equal(interpreted, inlinedOutput);
        Assert.Equal(interpreted, tieredUp);
        return (interpreted, inlined);
    }

    const string Prelude = """
        function run(f, ...args) {
          try { return String(f(...args)); } catch (e) { return String(e); }
        }
        // The wasm frames of an error's stack: function name, index and offset.
        function frames(e) {
          return [...String(e.stack).matchAll(/at ([^ ]+) \(wasm[^\[]+\[([0-9]+)\]:(0x[0-9a-f]+)\)/g)]
            .map(m => m[1] + "#" + m[2] + "@" + m[3]).join(" ");
        }
        function trace(f, ...args) {
          try { return String(f(...args)); } catch (e) { return String(e) + " [" + frames(e) + "]"; }
        }
        """;

    [Fact]
    public void DirectCallShapes()
    {
        var (output, inlined) = AllTiers(Prelude + """
            const b = new WasmModuleBuilder();
            b.addMemory(1, 10);
            const g = b.addGlobal(kWasmI32, true, false, wasmI32Const(5));
            // Two results.
            const divmod = b.addFunction("divmod", kSig_ii_ii).addBody([
              kExprLocalGet, 0, kExprLocalGet, 1, kExprI32DivS, kExprLocalGet, 0, kExprLocalGet, 1, kExprI32RemS]);
            // A local that must start at zero on every call (called in a loop).
            const acc = b.addFunction("acc", kSig_i_i).addLocals(kWasmI32, 1).addLocals(kWasmF64, 1).addBody([
              kExprLocalGet, 1, kExprLocalGet, 0, kExprI32Add, kExprLocalSet, 1,
              kExprLocalGet, 2, ...wasmF64Const(1.5), kExprF64Add, kExprLocalSet, 2,
              kExprLocalGet, 1, kExprLocalGet, 2, kExprI32SConvertF64, kExprI32Add]);
            // A reference local, null on every call.
            const isnull = b.addFunction("isnull", kSig_i_v).addLocals(kWasmExternRef, 1).addBody([
              kExprLocalGet, 0, kExprRefIsNull]);
            // Returns from nested blocks, a br_table to the function, unreachable code after.
            const pick = b.addFunction("pick", kSig_i_i).addBody([
              kExprBlock, kWasmVoid,
                kExprBlock, kWasmVoid,
                  kExprLocalGet, 0, kExprBrTable, 2, 0, 1, 1,
                kExprEnd,
                kExprI32Const, 10, kExprReturn,
              kExprEnd,
              kExprI32Const, 20, kExprBr, 0, kExprI32Const, 99]);
            // Memory: the caller does not touch memory itself.
            const store = b.addFunction("store", kSig_v_ii).addBody([
              kExprLocalGet, 0, kExprLocalGet, 1, kExprI32StoreMem, 2, 0]);
            const load = b.addFunction("load", kSig_i_i).addBody([kExprLocalGet, 0, kExprI32LoadMem, 2, 0]);
            const grow = b.addFunction("grow", kSig_i_i).addBody([kExprLocalGet, 0, kExprMemoryGrow, 0]);
            // Globals.
            const bump = b.addFunction("bump", kSig_v_v).addBody([
              kExprGlobalGet, g.index, kExprI32Const, 1, kExprI32Add, kExprGlobalSet, g.index]);
            // Never returns.
            const fail = b.addFunction("fail", kSig_i_i).addBody([kExprUnreachable]);
            // Nested: calls two others.
            const both = b.addFunction("both", kSig_i_ii).addBody([
              kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, divmod.index, kExprI32Add,
              kExprLocalGet, 0, kExprCallFunction, acc.index, kExprI32Add]);
            b.addFunction("main", kSig_i_ii).addLocals(kWasmI32, 2).addBody([
              kExprLoop, kWasmVoid,
                kExprLocalGet, 3, kExprLocalGet, 2, kExprCallFunction, acc.index, kExprI32Add, kExprLocalSet, 3,
                kExprLocalGet, 2, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 2,
                kExprI32Const, 5, kExprI32LtS, kExprBrIf, 0,
              kExprEnd,
              kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, divmod.index, kExprI32Mul,
              kExprLocalGet, 3, kExprI32Add,
              kExprCallFunction, isnull.index, kExprI32Add,
              kExprLocalGet, 0, kExprCallFunction, pick.index, kExprI32Add,
              kExprI32Const, 8, kExprLocalGet, 0, kExprCallFunction, store.index,
              kExprI32Const, 8, kExprCallFunction, load.index, kExprI32Add,
              kExprCallFunction, bump.index, kExprCallFunction, bump.index, kExprGlobalGet, g.index, kExprI32Add,
              kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, both.index, kExprI32Add]).exportFunc();
            b.addFunction("growAndRead", kSig_i_v).addBody([
              kExprI32Const, 1, kExprCallFunction, grow.index, kExprDrop,
              ...wasmI32Const(65536 + 4), kExprI32Const, 77, kExprCallFunction, store.index,
              ...wasmI32Const(65536 + 4), kExprCallFunction, load.index, kExprMemorySize, 0, kExprI32Add]).exportFunc();
            b.addFunction("failing", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprIf, kWasmI32, kExprLocalGet, 0, kExprCallFunction, fail.index,
              kExprElse, kExprI32Const, 3, kExprEnd]).exportFunc();
            const e = b.instantiate().exports;
            for (const [x, y] of [[7, 2], [-9, 4], [0, 3], [1, 1], [2, 5]]) print(run(e.main, x, y));
            print(run(e.main, 1, 0));
            print(e.growAndRead());
            print(run(e.failing, 0), run(e.failing, 1));
            """);
        Assert.Contains("RuntimeError: divide by zero", output);
        Assert.Contains("RuntimeError: unreachable", output);
        Assert.True(inlined >= 10, $"{inlined} calls inlined");
    }

    [Fact]
    public void RecursionAndTailCalls()
    {
        var (output, inlined) = AllTiers(Prelude + """
            const b = new WasmModuleBuilder();
            const fib = b.addFunction("fib", kSig_i_i);
            fib.addBody([
              kExprLocalGet, 0, kExprI32Const, 2, kExprI32LtS,
              kExprIf, kWasmI32, kExprLocalGet, 0,
              kExprElse,
                kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprCallFunction, fib.index,
                kExprLocalGet, 0, kExprI32Const, 2, kExprI32Sub, kExprCallFunction, fib.index,
                kExprI32Add,
              kExprEnd]).exportFunc();
            // A million deep: only tail calls survive, inlined or not.
            const count = b.addFunction("count", kSig_i_ii);
            count.addBody([
              kExprLocalGet, 0, kExprI32Eqz, kExprIf, kWasmI32, kExprLocalGet, 1,
              kExprElse, kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add,
              kExprReturnCall, count.index, kExprEnd]).exportFunc();
            const even = b.addFunction("even", kSig_i_i);
            const odd = b.addFunction("odd", kSig_i_i);
            even.addBody([kExprLocalGet, 0, kExprI32Eqz, kExprIf, kWasmI32, kExprI32Const, 1, kExprElse,
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprReturnCall, odd.index, kExprEnd]).exportFunc();
            odd.addBody([kExprLocalGet, 0, kExprI32Eqz, kExprIf, kWasmI32, kExprI32Const, 0, kExprElse,
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprReturnCall, even.index, kExprEnd]).exportFunc();
            // A tail call of a function that makes an ordinary call.
            const plus1 = b.addFunction("plus1", kSig_i_i).addBody([kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add]);
            const viaCall = b.addFunction("viaCall", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprCallFunction, plus1.index, kExprI32Const, 2, kExprI32Mul]);
            b.addFunction("tail", kSig_i_i).addBody([kExprLocalGet, 0, kExprReturnCall, viaCall.index]).exportFunc();
            // Unbounded recursion.
            const deep = b.addFunction("deep", kSig_i_i);
            deep.addBody([kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add, kExprCallFunction, deep.index]).exportFunc();
            const e = b.instantiate().exports;
            for (let i = 0; i < 3; i++) print(e.fib(20), e.count(1000000, 0), e.even(100001), e.odd(250000), e.tail(20));
            print(run(e.deep, 0));
            """);
        Assert.Contains("6765 1000000 0 0 42", output);
        Assert.Contains("RangeError: Maximum call stack size exceeded", output);
        Assert.True(inlined > 0, $"{inlined} calls inlined");
    }

    [Fact]
    public void TrapsInInlinedCalleesHaveTheirFrames()
    {
        // inlining.js InliningTrapFromCallee* (stack traces of traps in
        // inlined callees, through calls and tail calls). Compiled code only:
        // the interpreter shows the frame of a function that tail-called (V8
        // does not, and the compiled tier does as V8).
        var (output, _) = AllTiers(compareInterpreter: false, source: Prelude + """
            const b = new WasmModuleBuilder();
            for (let i = 0; i < 3; ++i) b.addFunction(null, makeSig([], [])).addBody([]);
            const callee = b.addFunction("callee", kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32DivU]);
            const intermediate = b.addFunction("intermediate", kSig_i_ii).addBody([
              kExprNop, kExprNop, kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, callee.index]).exportFunc();
            const tailIntermediate = b.addFunction("tailIntermediate", kSig_i_ii).addBody([
              kExprNop, kExprLocalGet, 0, kExprLocalGet, 1, kExprReturnCall, callee.index]).exportFunc();
            b.addFunction("main", kSig_ii_ii).addBody([
              kExprNop, kExprNop, kExprNop, kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, intermediate.index,
              kExprLocalGet, 1, kExprLocalGet, 0, kExprCallFunction, callee.index]).exportFunc();
            b.addFunction("mainTail", kSig_ii_ii).addBody([
              kExprNop, kExprLocalGet, 0, kExprLocalGet, 1, kExprCallFunction, tailIntermediate.index,
              kExprLocalGet, 1, kExprLocalGet, 0, kExprCallFunction, tailIntermediate.index]).exportFunc();
            b.addFunction("onlyTail", kSig_i_ii).addBody([
              kExprNop, kExprNop, kExprLocalGet, 0, kExprLocalGet, 1, kExprReturnCall, callee.index]).exportFunc();
            // A loop that calls the trapping callee, so the trap is in an inlined frame inside a loop.
            b.addFunction("loop", kSig_i_i).addLocals(kWasmI32, 1).addBody([
              kExprLoop, kWasmVoid,
                kExprI32Const, 100, kExprLocalGet, 0, kExprCallFunction, intermediate.index, kExprDrop,
                kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprLocalTee, 0,
                ...wasmI32Const(-5), kExprI32GtS, kExprBrIf, 0,
              kExprEnd, kExprI32Const, 0]).exportFunc();
            const e = b.instantiate().exports;
            for (let i = 0; i < 3; i++) {
              print(trace(e.main, 21, 3), trace(e.main, 1, 0), trace(e.main, 0, 1));
              print(trace(e.mainTail, 1, 0), trace(e.mainTail, 0, 1));
              print(trace(e.onlyTail, 1, 0), trace(e.intermediate, 1, 0), trace(e.loop, 3));
            }
            """);
        // As inlining.js expects: the tail-calling frame is gone.
        Assert.Contains("RuntimeError: divide by zero [callee#3@0xa7 intermediate#4@0xb1 main#6@0xc7]", output);
        Assert.Contains("RuntimeError: divide by zero [callee#3@0xa7 mainTail#7@0xd7]", output);
        Assert.Contains("RuntimeError: divide by zero [callee#3@0xa7] ", output);
        Assert.Contains("RuntimeError: divide by zero [callee#3@0xa7 intermediate#4@0xb1 loop#9@0xf5]", output);
    }

    [Fact]
    public void ExceptionsAcrossInlinedFrames()
    {
        // inlining.js: *InHandled*, TailCallInCatchBlock and the nested forms.
        AllTiers(Prelude + """
            const b = new WasmModuleBuilder();
            const tag = b.addTag(kSig_v_i);
            const thrower = b.addFunction("thrower", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprIf, kWasmVoid, kExprLocalGet, 0, kExprThrow, tag, kExprEnd, kExprI32Const, 7]);
            const tailToThrower = b.addFunction("tailToThrower", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprReturnCall, thrower.index]);
            const callsThrower = b.addFunction("callsThrower", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add, kExprCallFunction, thrower.index]);
            // A handler around an inlined call catches what the callee throws.
            b.addFunction("handled", kSig_i_i).addBody([
              kExprTry, kWasmI32, kExprLocalGet, 0, kExprCallFunction, thrower.index,
              kExprCatch, tag, ...wasmI32Const(100), kExprI32Add, kExprEnd]).exportFunc();
            // A tail call replaces the frame: its handler does not see the callee's exception.
            b.addFunction("tailInTry", kSig_i_i).addBody([
              kExprTry, kWasmI32, kExprLocalGet, 0, kExprReturnCall, thrower.index,
              kExprCatch, tag, kExprEnd]).exportFunc();
            b.addFunction("tailInNested", kSig_i_i).addBody([
              kExprTry, kWasmI32, kExprLocalGet, 0, kExprCallFunction, tailToThrower.index,
              kExprCatch, tag, ...wasmI32Const(200), kExprI32Add, kExprEnd]).exportFunc();
            b.addFunction("tailToCaller", kSig_i_i).addBody([
              kExprTry, kWasmI32, kExprLocalGet, 0, kExprReturnCall, callsThrower.index,
              kExprCatch, tag, kExprEnd]).exportFunc();
            b.addFunction("unhandled", kSig_i_i).addBody([kExprLocalGet, 0, kExprCallFunction, callsThrower.index]).exportFunc();
            const e = b.instantiate().exports;
            // try_table (a module may not mix it with the legacy instructions).
            const b2 = new WasmModuleBuilder();
            const tag2 = b2.addTag(kSig_v_i);
            const thrower2 = b2.addFunction("thrower", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprIf, kWasmVoid, kExprLocalGet, 0, kExprThrow, tag2, kExprEnd, kExprI32Const, 7]);
            const callsThrower2 = b2.addFunction("callsThrower", kSig_i_i).addBody([
              kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add, kExprCallFunction, thrower2.index]);
            b2.addFunction("handledTable", kSig_i_i).addBody([
              kExprBlock, kWasmI32,
                kExprTryTable, kWasmI32, 1, kCatchNoRef, tag2, 0,
                  kExprLocalGet, 0, kExprCallFunction, callsThrower2.index,
                kExprEnd,
                kExprReturn,
              kExprEnd,
              ...wasmI32Const(1000), kExprI32Add]).exportFunc();
            e.handledTable = b2.instantiate().exports.handledTable;
            const show = (f, x) => { try { return String(f(x)); } catch (ex) { return ex instanceof WebAssembly.Exception ? "exception" : String(ex); } };
            for (let i = 0; i < 3; i++) {
              for (const name of ["handled", "handledTable", "tailInTry", "tailInNested", "tailToCaller", "unhandled"]) {
                print(name, show(e[name], 0), show(e[name], 5), show(e[name], -1));
              }
            }
            """);
    }

    [Fact]
    public void SpeculativeCallIndirectAndCallRef()
    {
        // speculative-inlining.js and the message test
        // wasm-speculative-inlining.js: feedback with 0, 1, 4 and 5 targets,
        // the tier-up, then targets never seen, another signature, null and
        // out-of-bounds entries.
        var (output, inlined) = AllTiers(Prelude + """
            function build(kind) {
              const b = new WasmModuleBuilder();
              const sig = b.addType(kSig_i_i);
              const other = b.addType(kSig_i_ii);
              b.addTable(kWasmFuncRef, 8, 8).exportAs("table");
              const callees = [];
              for (let i = 0; i < 6; i++) {
                callees.push(b.addFunction("callee" + i, sig).addBody([kExprLocalGet, 0, kExprI32Const, i, kExprI32Add]));
              }
              const wrong = b.addFunction("wrong", other).addBody([kExprLocalGet, 0]);
              b.addActiveElementSegment(0, wasmI32Const(0), [...callees.map(f => f.index), wrong.index]);
              const body = kind === "call_ref"
                  ? [kExprLocalGet, 0, kExprLocalGet, 1, kExprTableGet, 0, kGCPrefix, kExprRefCast, sig, kExprCallRef, sig]
                  : kind === "return_call_indirect"
                  ? [kExprLocalGet, 0, kExprLocalGet, 1, kExprReturnCallIndirect, sig, 0]
                  : [kExprLocalGet, 0, kExprLocalGet, 1, kExprCallIndirect, sig, 0];
              b.addFunction("main", kSig_i_ii).addBody(body).exportFunc();
              // The same call in a loop, so that the budget runs out in it.
              b.addFunction("loop", kSig_i_ii).addLocals(kWasmI32, 2).addBody([
                kExprLoop, kWasmVoid,
                  kExprLocalGet, 3, kExprLocalGet, 2, kExprLocalGet, 1, kExprI32RemU, kExprCallIndirect, sig, 0,
                  kExprLocalSet, 3,
                  kExprLocalGet, 2, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 2,
                  kExprLocalGet, 0, kExprI32LtS, kExprBrIf, 0,
                kExprEnd, kExprLocalGet, 3]).exportFunc();
              return b.instantiate().exports;
            }
            for (const kind of ["call_indirect", "return_call_indirect", "call_ref"]) {
              for (const count of [0, 1, 4, 5]) {
                const e = build(kind);
                const out = [];
                for (let i = 0; i < 10; i++) for (let j = 0; j < count; j++) out.push(e.main(10, j));
                %WasmTierUpFunction(e.main);
                for (let j = 0; j < 6; j++) out.push(run(e.main, 10, j));
                out.push(run(e.main, 10, 6), run(e.main, 10, 7), run(e.main, 10, 100));
                out.push(run(e.loop, 3000, 1), run(e.loop, 3000, 2), run(e.loop, 3000, 5));
                // Targets that change after the tier-up.
                e.table.set(0, e.table.get(5));
                e.table.set(1, null);
                out.push(run(e.main, 10, 0), run(e.main, 10, 1));
                out.push(run(e.loop, 3000, 1), run(e.loop, 3000, 3), run(e.loop, 3000, 6));
                e.table.set(2, e.table.get(4));
                out.push(run(e.loop, 3000, 3), run(e.loop, 10, 8));
                print(kind, count, out.join(" "));
              }
            }
            """);
        Assert.Contains("RuntimeError: null function", output);
        Assert.Contains("signature mismatch", output);
        Assert.Contains("RuntimeError: table index is out of bounds", output);
        Assert.True(inlined > 0, $"{inlined} calls inlined");
    }

    [Fact]
    public void InliningBudgetFollowsV8()
    {
        // InliningTree::SmallEnoughToInline: a callee over
        // --wasm-inlining-max-size is not inlined; with --no-wasm-inlining
        // nothing is.
        const string source = """
            const b = new WasmModuleBuilder();
            const body = [kExprLocalGet, 0];
            for (let i = 0; i < 40; i++) body.push(kExprI32Const, 1, kExprI32Add);
            const big = b.addFunction("big", kSig_i_i).addBody(body);
            const small = b.addFunction("small", kSig_i_i).addBody([kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add]);
            // Big enough that its budget takes the 124-byte callee.
            const nops = new Array(80).fill(kExprNop);
            b.addFunction("main", kSig_i_i).addBody([
              ...nops, kExprLocalGet, 0, kExprCallFunction, big.index, kExprCallFunction, small.index]).exportFunc();
            print(b.instantiate().exports.main(1));
            """;
        int Inlined(string flags)
        {
            int n = 0;
            string output = WasmJsTester.Run(source, flags: flags, inspect: isolate =>
            {
                foreach (WasmCode code in WasmEngine.Get(isolate).AllCode()) n += code.InlinedCalls;
            });
            Assert.Equal("42\n", output);
            return n;
        }
        Assert.Equal(2, Inlined(""));
        Assert.Equal(1, Inlined("--wasm-inlining-max-size=100"));
        Assert.Equal(0, Inlined("--no-wasm-inlining"));
    }
}
