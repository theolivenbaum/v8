// Tests of the WebAssembly JS API (src/wasm/wasm-js.cc): the shape of the
// WebAssembly namespace, the constructors' errors, calls between JS and wasm,
// traps, exceptions, memories and tables. The expected texts are V8's (the
// oracle's output for the same scripts; see WasmParityTests for the
// differential form).
namespace V8Sharp.Wasm.Tests;

public class WasmJsApiTests
{
    static string Run(string source, bool withBuilder = true) => WasmJsTester.Run(source, withBuilder);

    [Fact]
    public void NamespaceShape()
    {
        string output = Run("""
            print(Object.getOwnPropertyNames(WebAssembly).join());
            print(Object.getOwnPropertyNames(WebAssembly.Module).join());
            print(Object.getOwnPropertyNames(WebAssembly.Memory.prototype).join());
            print(Object.prototype.toString.call(WebAssembly), String(WebAssembly.Module));
            var d = Object.getOwnPropertyDescriptor(WebAssembly, 'compile');
            print(d.writable, d.enumerable, d.configurable);
            d = Object.getOwnPropertyDescriptor(this, 'WebAssembly');
            print(d.writable, d.enumerable, d.configurable);
            """, withBuilder: false);
        Assert.Equal(
            "compile,validate,instantiate,Module,Instance,Table,Memory,Global,Tag,JSTag,Exception,CompileError,LinkError,RuntimeError,Suspending,promising,SuspendError\n" +
            "length,name,prototype,imports,exports,customSections\n" +
            "constructor,grow,buffer,toFixedLengthBuffer,toResizableBuffer\n" +
            "[object WebAssembly] function Module() { [native code] }\n" +
            "true true true\n" +
            "true false true\n",
            output);
    }

    [Fact]
    public void ConstructorErrors()
    {
        string output = Run("""
            try { WebAssembly.Module() } catch (e) { print(e) }
            try { new WebAssembly.Module() } catch (e) { print(e) }
            try { new WebAssembly.Module(new Uint8Array([1,2,3,4])) } catch (e) { print(e) }
            try { new WebAssembly.Module(new ArrayBuffer(0)) } catch (e) { print(e) }
            try { new WebAssembly.Instance(1) } catch (e) { print(e) }
            try { WebAssembly.Memory.prototype.buffer } catch (e) { print(e) }
            try { new WebAssembly.Memory({initial: 1, shared: true}) } catch (e) { print(e) }
            try { new WebAssembly.Table({element: 'i32', initial: 1}) } catch (e) { print(e) }
            try { new WebAssembly.Global({value: 'v128'}) } catch (e) { print(e) }
            """, withBuilder: false);
        Assert.Equal(
            "TypeError: WebAssembly.Module(): WebAssembly.Module must be invoked with 'new'\n" +
            "TypeError: WebAssembly.Module(): Argument 0 must be a buffer source\n" +
            "CompileError: WebAssembly.Module(): expected magic word 00 61 73 6d, found 01 02 03 04 @+0\n" +
            "CompileError: WebAssembly.Module(): BufferSource argument is empty\n" +
            "TypeError: WebAssembly.Instance(): Argument 0 must be a WebAssembly.Module\n" +
            "TypeError: WebAssembly.Memory.buffer: Receiver is not a WebAssembly.Memory\n" +
            "TypeError: WebAssembly.Memory(): If shared is true, maximum property should be defined.\n" +
            "TypeError: WebAssembly.Table(): Descriptor property 'element' must be a WebAssembly reference type\n" +
            "TypeError: WebAssembly.Global(): A global of type 'v128' cannot be created in JavaScript\n",
            output);
    }

    [Fact]
    public void CallsAndTraps()
    {
        string output = Run("""
            let builder = new WasmModuleBuilder();
            let imp = builder.addImport('m', 'f', kSig_i_i);
            builder.addFunction('add', kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32Add]).exportFunc();
            builder.addFunction('callimp', kSig_i_i).addBody([kExprLocalGet, 0, kExprCallFunction, imp]).exportFunc();
            builder.addFunction('trap', kSig_v_v).addBody([kExprUnreachable]).exportFunc();
            builder.addFunction('div', kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32DivS]).exportFunc();
            builder.addFunction('rem', kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32RemS]).exportFunc();
            builder.addFunction('i64', makeSig([kWasmI64], [kWasmI64])).addBody([kExprLocalGet, 0, kExprI64Const, 1, kExprI64Add]).exportFunc();
            builder.addFunction('multi', makeSig([], [kWasmI32, kWasmF64])).addBody([kExprI32Const, 7, kExprF64Const, 0, 0, 0, 0, 0, 0, 0xf8, 0x3f]).exportFunc();
            let e = builder.instantiate({m: {f: x => x * 2}}).exports;
            print(e.add(1, 2), e.add.name, e.add.length, e.callimp(21), e.i64(41n), JSON.stringify(e.multi()));
            print(e.add('3', {valueOf() { return 4; }}), e.add(2 ** 32 + 5, -1));
            try { e.trap(); } catch (err) { print(err, err instanceof WebAssembly.RuntimeError); }
            try { e.div(1, 0); } catch (err) { print(err); }
            try { e.rem(1, 0); } catch (err) { print(err); }
            try { e.div(-2147483648, -1); } catch (err) { print(err); }
            try { e.i64(1); } catch (err) { print(err); }
            print(e.add === e.add, typeof e.add, Object.isFrozen(e), Object.getPrototypeOf(e));
            """);
        Assert.Equal(
            "3 1 2 42 42 [7,1.5]\n" +
            "7 4\n" +
            "RuntimeError: unreachable true\n" +
            "RuntimeError: divide by zero\n" +
            "RuntimeError: remainder by zero\n" +
            "RuntimeError: divide result unrepresentable\n" +
            "TypeError: Cannot convert 1 to a BigInt\n" +
            "true function true null\n",
            output);
    }

    [Fact]
    public void MemoryBuffers()
    {
        string output = Run("""
            let builder = new WasmModuleBuilder();
            builder.addMemory(1, 3);
            builder.exportMemoryAs('mem');
            builder.addFunction('load', kSig_i_i).addBody([kExprLocalGet, 0, kExprI32LoadMem, 0, 0]).exportFunc();
            builder.addFunction('grow', kSig_i_i).addBody([kExprLocalGet, 0, kExprMemoryGrow, 0]).exportFunc();
            let e = builder.instantiate().exports;
            let buf = e.mem.buffer;
            new Uint32Array(buf)[0] = 0x12345678;
            print(e.load(0).toString(16), buf.byteLength, e.mem.buffer === buf);
            print(e.grow(1), buf.byteLength, e.mem.buffer.byteLength);
            print(e.mem.grow(1), e.mem.buffer.byteLength, e.grow(1));
            try { e.load(3 * 65536); } catch (err) { print(err); }
            try { e.mem.grow(1); } catch (err) { print(err); }
            let m = new WebAssembly.Memory({initial: 1, maximum: 2, shared: true});
            let sab = m.buffer;
            print(sab instanceof SharedArrayBuffer, m.grow(1), sab.byteLength, m.buffer.byteLength);
            new Uint8Array(m.buffer)[5] = 9;
            print(new Uint8Array(sab)[5]);
            """);
        Assert.Equal(
            "12345678 65536 true\n" +
            "1 0 131072\n" +
            "2 196608 -1\n" +
            "RuntimeError: memory access out of bounds\n" +
            "RangeError: WebAssembly.Memory.grow(): Maximum memory size exceeded\n" +
            "true 1 65536 131072\n" +
            "9\n",
            output);
    }

    [Fact]
    public void Exceptions()
    {
        string output = Run("""
            let b = new WasmModuleBuilder();
            let tag = b.addTag(kSig_v_i);
            let thrower = b.addImport('m', 'throws', kSig_v_v);
            b.addFunction('throw', kSig_v_i).addBody([kExprLocalGet, 0, kExprThrow, tag]).exportFunc();
            b.addFunction('catchjs', kSig_i_v).addBody([
              kExprBlock, kWasmVoid,
                kExprTryTable, kWasmVoid, 1, kCatchAllNoRef, 0,
                  kExprCallFunction, thrower,
                kExprEnd,
                kExprI32Const, 0, kExprReturn,
              kExprEnd,
              kExprI32Const, 1]).exportFunc();
            b.addFunction('passjs', kSig_v_v).addBody([kExprCallFunction, thrower]).exportFunc();
            b.addExportOfKind('tag', kExternalTag, tag);
            let error = new Error('boom');
            let i = b.instantiate({m: {throws: () => { throw error; }}});
            try { i.exports.throw(5); } catch (err) {
              print(err, err instanceof WebAssembly.Exception, err.is(i.exports.tag), err.getArg(i.exports.tag, 0));
            }
            print(i.exports.catchjs());
            try { i.exports.passjs(); } catch (err) { print(err === error); }
            let jsTag = new WebAssembly.Tag({parameters: ['i32', 'f64']});
            let ex = new WebAssembly.Exception(jsTag, [1, 2.5]);
            print(ex.getArg(jsTag, 1), ex.is(jsTag), ex.is(i.exports.tag));
            try { new WebAssembly.Exception(WebAssembly.JSTag, [1]); } catch (err) { print(err); }
            """);
        Assert.Equal(
            "[object WebAssembly.Exception] true true 5\n" +
            "1\n" +
            "true\n" +
            "2.5 true false\n" +
            "TypeError: WebAssembly.Exception(): Argument 0 cannot be WebAssembly.JSTag\n",
            output);
    }

    [Fact]
    public void ImportErrors()
    {
        string output = Run("""
            let b = new WasmModuleBuilder();
            b.addImport('m', 'f', kSig_v_v);
            b.addImportedMemory('m', 'mem', 1);
            let module = b.toModule();
            try { new WebAssembly.Instance(module); } catch (e) { print(e); }
            try { new WebAssembly.Instance(module, {}); } catch (e) { print(e); }
            let mem = new WebAssembly.Memory({initial: 1});
            try { new WebAssembly.Instance(module, {m: {f: print}}); } catch (e) { print(e); }
            try { new WebAssembly.Instance(module, {m: {f: 1, mem}}); } catch (e) { print(e); }
            print(JSON.stringify(WebAssembly.Module.imports(module)));
            """);
        Assert.Equal(
            "TypeError: WebAssembly.Instance(): Imports argument must be present and must be an object\n" +
            "TypeError: WebAssembly.Instance(): Import #0 \"m\": module is not an object or function\n" +
            "LinkError: WebAssembly.Instance(): Import #1 \"m\" \"mem\": memory import must be a WebAssembly.Memory object\n" +
            "LinkError: WebAssembly.Instance(): Import #0 \"m\" \"f\": function import requires a callable\n" +
            "[{\"module\":\"m\",\"name\":\"f\",\"kind\":\"function\"},{\"module\":\"m\",\"name\":\"mem\",\"kind\":\"memory\"}]\n",
            output);
    }

    [Fact]
    public void TablesAndGlobals()
    {
        string output = Run("""
            let b = new WasmModuleBuilder();
            let f = b.addFunction('f', kSig_i_v).addBody([kExprI32Const, 42]).exportFunc();
            b.addTable(kWasmAnyFunc, 2).exportAs('table');
            b.addGlobal(kWasmI64, true, false, wasmI64Const(7)).exportAs('g');
            let e = b.instantiate().exports;
            print(e.table.length, e.table.get(0));
            e.table.set(1, e.f);
            print(e.table.get(1) === e.f, e.table.get(1)(), e.table.grow(2), e.table.length);
            try { e.table.set(0, () => 1); } catch (err) { print(err); }
            print(e.g.value, typeof e.g.value);
            e.g.value = 9n;
            print(e.g.valueOf());
            let g = new WebAssembly.Global({value: 'f32'}, 1.1);
            print(g.value);
            try { g.value = 2; } catch (err) { print(err); }
            """);
        Assert.Equal(
            "2 null\n" +
            "true 42 2 4\n" +
            "TypeError: WebAssembly.Table.set(): Argument 1 is invalid for table: function-typed object must be null (if nullable) or a Wasm function object\n" +
            "7 bigint\n" +
            "9\n" +
            "1.100000023841858\n" +
            "TypeError: set WebAssembly.Global.value): Can't set the value of an immutable global.\n",
            output);
    }

    [Fact]
    public void AsyncApis()
    {
        string output = Run("""
            let b = new WasmModuleBuilder();
            b.addFunction('add', kSig_i_ii).addBody([kExprLocalGet, 0, kExprLocalGet, 1, kExprI32Add]).exportFunc();
            let wire = b.toBuffer();
            Promise.all([
              WebAssembly.instantiate(wire),
              WebAssembly.compile(wire).then(m => WebAssembly.instantiate(m)),
              WebAssembly.compile(new Uint8Array([1, 2])).catch(e => String(e))
            ]).then(([r, i, e]) => print(Object.keys(r).join(), r.instance.exports.add(3, 4), i instanceof WebAssembly.Instance, e));
            print('sync', WebAssembly.validate(wire), WebAssembly.validate(new Uint8Array([0, 0x61, 0x73, 0x6d, 2, 0, 0, 0])));
            """);
        Assert.Equal(
            "sync true false\n" +
            "module,instance 7 true CompileError: WebAssembly.compile(): expected 4 bytes, fell off end @+0\n",
            output);
    }

    [Fact]
    public void StackOverflowIsRangeError()
    {
        string output = Run("""
            let b = new WasmModuleBuilder();
            b.addFunction('rec', kSig_i_i).addBody([kExprLocalGet, 0, kExprLocalGet, 0, kExprCallFunction, 0, kExprI32Add]).exportFunc();
            try { b.instantiate().exports.rec(1); } catch (err) { print(err); }
            """);
        Assert.Equal("RangeError: Maximum call stack size exceeded\n", output);
    }

    [Fact]
    public void CustomSectionsAndExports()
    {
        string output = Run("""
            let b = new WasmModuleBuilder();
            b.addFunction('f', kSig_v_v).addBody([]).exportFunc();
            b.addMemory(1);
            b.exportMemoryAs('m');
            b.addCustomSection('hello', [1, 2, 3]);
            let module = b.toModule();
            print(JSON.stringify(WebAssembly.Module.exports(module)));
            let sections = WebAssembly.Module.customSections(module, 'hello');
            print(sections.length, sections[0] instanceof ArrayBuffer, Array.from(new Uint8Array(sections[0])).join());
            print(WebAssembly.Module.customSections(module, 'other').length);
            """);
        Assert.Equal(
            "[{\"name\":\"f\",\"kind\":\"function\"},{\"name\":\"m\",\"kind\":\"memory\"}]\n" +
            "1 true 1,2,3\n" +
            "0\n",
            output);
    }
}
