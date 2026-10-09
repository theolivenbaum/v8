// Smoke tests of the vendored WACS engine (V8Sharp.Wasm) without the JS API:
// decode, validate, instantiate and run small modules.
using Wacs.Core;
using Wacs.Core.Runtime;
using Wacs.Core.Validation;

namespace V8Sharp.Wasm.Tests;

public class RuntimeSmokeTests
{
    // (module (func (export "add") (param i32 i32) (result i32)
    //   local.get 0 local.get 1 i32.add))
    internal static readonly byte[] AddModule =
    [
        0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00,
        0x01, 0x07, 0x01, 0x60, 0x02, 0x7f, 0x7f, 0x01, 0x7f, // type section
        0x03, 0x02, 0x01, 0x00,                                // function section
        0x07, 0x07, 0x01, 0x03, 0x61, 0x64, 0x64, 0x00, 0x00,  // export "add"
        0x0a, 0x09, 0x01, 0x07, 0x00, 0x20, 0x00, 0x20, 0x01, 0x6a, 0x0b, // code
    ];

    static Module Parse(byte[] bytes) => BinaryModuleParser.ParseWasm(new MemoryStream(bytes));

    [Fact]
    public void AddExport()
    {
        var runtime = new WasmRuntime();
        var module = Parse(AddModule);
        var instance = runtime.InstantiateModule(module);
        runtime.RegisterModule("m", instance);
        Assert.True(runtime.TryGetExportedFunction(("m", "add"), out var addr));
        var invoker = runtime.CreateStackInvoker(addr);
        var results = invoker([new Value(40), new Value(2)]);
        Assert.Single(results);
        Assert.Equal(42, results[0].Data.Int32);
    }

    [Fact]
    public void TypeMismatchFailsValidation()
    {
        // Same module with i32.add replaced by i64.add (0x7c).
        var bytes = (byte[])AddModule.Clone();
        bytes[^2] = 0x7c;
        var module = Parse(bytes);
        Assert.Throws<ValidationException>(() => module.ValidateAndThrow());
    }
}
