// WebAssembly micro-benchmarks: loops, memory, calls, float math, and calls
// between JavaScript and wasm. Each bench() call does a fixed amount of work.
// The compiled code is warmed up first (compiled, run several times so .NET
// tiers the emitted IL, then settled) so that the scores measure the
// generated code, not the compilers.
load('../../../../test/mjsunit/wasm/wasm-module-builder.js');

var builder = new WasmModuleBuilder();
builder.addMemory(16, 16);
// sum(n): 0 + 1 + ... + n-1, an i32 loop.
builder.addFunction('sum', kSig_i_i).addLocals(kWasmI32, 2).addBody([
  kExprLoop, kWasmVoid,
    kExprLocalGet, 2, kExprLocalGet, 1, kExprI32Add, kExprLocalSet, 2,
    kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 1,
    kExprLocalGet, 0, kExprI32LtS, kExprBrIf, 0,
  kExprEnd, kExprLocalGet, 2]).exportFunc();
// fill(n): memory[i] = i for i32 words; then sumMem(n) reads them back.
builder.addFunction('fill', kSig_v_i).addLocals(kWasmI32, 1).addBody([
  kExprLoop, kWasmVoid,
    kExprLocalGet, 1, kExprI32Const, 2, kExprI32Shl, kExprLocalGet, 1, kExprI32StoreMem, 2, 0,
    kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 1,
    kExprLocalGet, 0, kExprI32LtU, kExprBrIf, 0,
  kExprEnd]).exportFunc();
builder.addFunction('sumMem', kSig_i_i).addLocals(kWasmI32, 2).addBody([
  kExprLoop, kWasmVoid,
    kExprLocalGet, 2, kExprLocalGet, 1, kExprI32Const, 2, kExprI32Shl, kExprI32LoadMem, 2, 0, kExprI32Add, kExprLocalSet, 2,
    kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 1,
    kExprLocalGet, 0, kExprI32LtU, kExprBrIf, 0,
  kExprEnd, kExprLocalGet, 2]).exportFunc();
// fib(n): recursive calls.
var fib = builder.addFunction('fib', kSig_i_i);
fib.addBody([
  kExprLocalGet, 0, kExprI32Const, 2, kExprI32LtS,
  kExprIf, kWasmI32, kExprLocalGet, 0,
  kExprElse,
    kExprLocalGet, 0, kExprI32Const, 1, kExprI32Sub, kExprCallFunction, fib.index,
    kExprLocalGet, 0, kExprI32Const, 2, kExprI32Sub, kExprCallFunction, fib.index,
    kExprI32Add,
  kExprEnd]).exportFunc();
// calls(n): a loop calling a small function and an indirect one.
var inc = builder.addFunction('inc', kSig_i_i).addBody([kExprLocalGet, 0, kExprI32Const, 1, kExprI32Add]);
var sigI = builder.addType(kSig_i_i);
builder.addTable(kWasmFuncRef, 1, 1);
builder.addActiveElementSegment(0, wasmI32Const(0), [inc.index]);
builder.addFunction('calls', kSig_i_i).addLocals(kWasmI32, 2).addBody([
  kExprLoop, kWasmVoid,
    kExprLocalGet, 2, kExprCallFunction, inc.index,
    kExprI32Const, 0, kExprCallIndirect, sigI, 0, kExprLocalSet, 2,
    kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 1,
    kExprLocalGet, 0, kExprI32LtS, kExprBrIf, 0,
  kExprEnd, kExprLocalGet, 2]).exportFunc();
// mandel(n): the Mandelbrot iteration count of an n x n grid, f64 math.
builder.addFunction('mandel', kSig_i_i).addLocals(kWasmI32, 3).addLocals(kWasmF64, 5).addBody([
  // locals: 0 n, 1 y, 2 x, 3 count; 4 cr, 5 ci, 6 zr, 7 zi, 8 t
  kExprLoop, kWasmVoid,
    kExprI32Const, 0, kExprLocalSet, 2,
    kExprLoop, kWasmVoid,
      kExprLocalGet, 2, kExprF64SConvertI32, kExprLocalGet, 0, kExprF64SConvertI32, kExprF64Div,
      ...wasmF64Const(3), kExprF64Mul, ...wasmF64Const(2), kExprF64Sub, kExprLocalSet, 4,
      kExprLocalGet, 1, kExprF64SConvertI32, kExprLocalGet, 0, kExprF64SConvertI32, kExprF64Div,
      ...wasmF64Const(2), kExprF64Mul, ...wasmF64Const(1), kExprF64Sub, kExprLocalSet, 5,
      ...wasmF64Const(0), kExprLocalSet, 6, ...wasmF64Const(0), kExprLocalSet, 7,
      kExprBlock, kWasmVoid,
        kExprLoop, kWasmVoid,
          kExprLocalGet, 6, kExprLocalGet, 6, kExprF64Mul, kExprLocalGet, 7, kExprLocalGet, 7, kExprF64Mul, kExprF64Add,
          ...wasmF64Const(4), kExprF64Gt, kExprBrIf, 1,
          kExprLocalGet, 3, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 3,
          ...wasmI32Const(0x3fffffff), kExprI32And, kExprI32Const, 50, kExprI32RemU, kExprI32Eqz, kExprBrIf, 1,
          kExprLocalGet, 6, kExprLocalGet, 6, kExprF64Mul, kExprLocalGet, 7, kExprLocalGet, 7, kExprF64Mul, kExprF64Sub,
          kExprLocalGet, 4, kExprF64Add, kExprLocalSet, 8,
          ...wasmF64Const(2), kExprLocalGet, 6, kExprF64Mul, kExprLocalGet, 7, kExprF64Mul, kExprLocalGet, 5, kExprF64Add, kExprLocalSet, 7,
          kExprLocalGet, 8, kExprLocalSet, 6,
          kExprBr, 0,
        kExprEnd,
      kExprEnd,
      kExprLocalGet, 2, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 2,
      kExprLocalGet, 0, kExprI32LtS, kExprBrIf, 0,
    kExprEnd,
    kExprLocalGet, 1, kExprI32Const, 1, kExprI32Add, kExprLocalTee, 1,
    kExprLocalGet, 0, kExprI32LtS, kExprBrIf, 0,
  kExprEnd, kExprLocalGet, 3]).exportFunc();
builder.addFunction('add', kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32Add]).exportFunc();

var e = builder.instantiate().exports;
var work = {
  WasmLoop: function () { return e.sum(1000000); },
  WasmMemory: function () { e.fill(100000); return e.sumMem(100000); },
  WasmFib: function () { return e.fib(25); },
  WasmCalls: function () { return e.calls(200000); },
  WasmFloat: function () { return e.mandel(100); },
  WasmJSCalls: function () { var s = 0; for (var i = 0; i < 100000; i++) s = e.add(s, i); return s; },
};
// Warm-up: compile, run the compiled code several times, wait for the
// compilers and .NET's tiering, settle.
for (var name in work) work[name]();
if (typeof waitForCompilations === 'function') waitForCompilations();
for (var r = 0; r < 5; r++) for (var name in work) work[name]();
if (typeof waitForCompilations === 'function') waitForCompilations();
if (typeof settle === 'function') settle();
for (var name in work) bench(name, work[name]);
