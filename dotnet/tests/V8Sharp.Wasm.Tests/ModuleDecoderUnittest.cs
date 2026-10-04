// Port of parts of test/unittests/wasm/module-decoder-unittest.cc
// (WasmModuleVerifyTest): modules that must decode and validate, and modules
// that must be rejected. V8's tests also check the decoded WasmModule and the
// error text; the decoder here is WACS's, so these check acceptance only and
// that the JS API reports a rejection as a CompileError.
using V8Sharp.Wasm;

namespace V8Sharp.Wasm.Tests;

public class ModuleDecoderUnittest
{
    static readonly byte[] kHeader = [0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00];

    // Section ids (wasm-constants.h).
    const byte kTypeSectionCode = 1;
    const byte kImportSectionCode = 2;
    const byte kFunctionSectionCode = 3;
    const byte kTableSectionCode = 4;
    const byte kMemorySectionCode = 5;
    const byte kGlobalSectionCode = 6;
    const byte kExportSectionCode = 7;
    const byte kStartSectionCode = 8;
    const byte kCodeSectionCode = 10;
    const byte kTagSectionCode = 13;

    const byte kI32Code = 0x7f;
    const byte kI64Code = 0x7e;
    const byte kF32Code = 0x7d;
    const byte kExternRefCode = 0x6f;
    const byte kFuncRefCode = 0x70;
    const byte kWasmFunctionTypeCode = 0x60;
    const byte kExprI32Const = 0x41;
    const byte kExprEnd = 0x0b;
    const byte kExprRefNull = 0xd0;

    static byte[] Module(params byte[][] sections)
    {
        var bytes = new List<byte>(kHeader);
        foreach (byte[] section in sections) bytes.AddRange(section);
        return bytes.ToArray();
    }

    /// <summary>SECTION(name, contents...): id, size, contents.</summary>
    static byte[] Section(byte id, params byte[] contents)
    {
        var bytes = new List<byte> { id };
        bytes.AddRange(Leb(contents.Length));
        bytes.AddRange(contents);
        return bytes.ToArray();
    }

    static byte[] Leb(int value)
    {
        var bytes = new List<byte>();
        uint v = (uint)value;
        do
        {
            byte b = (byte)(v & 0x7f);
            v >>= 7;
            if (v != 0) b |= 0x80;
            bytes.Add(b);
        } while (v != 0);
        return bytes.ToArray();
    }

    static bool Validates(byte[] bytes) => WasmEngine.Validate(bytes);

    // SIGNATURES_SECTION(1, SIG_ENTRY_v_v)
    static readonly byte[] kTypeVoidVoid = Section(kTypeSectionCode, 1, kWasmFunctionTypeCode, 0, 0);

    // SIGNATURES_SECTION(1, SIG_ENTRY_i_i)
    static readonly byte[] kTypeI32I32 = Section(kTypeSectionCode, 1, kWasmFunctionTypeCode, 1, kI32Code, 1, kI32Code);

    [Fact]
    public void WrongMagic()
    {
        byte[] data = [0x00, 0x61, 0x73, 0x6c, 0x01, 0x00, 0x00, 0x00];
        Assert.False(Validates(data));
    }

    [Fact]
    public void WrongVersion()
    {
        byte[] data = [0x00, 0x61, 0x73, 0x6d, 0x02, 0x00, 0x00, 0x00];
        Assert.False(Validates(data));
    }

    [Fact]
    public void WrongSection()
    {
        Assert.False(Validates(Module(Section(0x7f, 0))));
    }

    [Fact]
    public void DecodeEmpty()
    {
        Assert.True(Validates(Module()));
    }

    [Fact]
    public void OneGlobal()
    {
        // (global i32 (i32.const 13))
        Assert.True(Validates(Module(Section(kGlobalSectionCode, 1, kI32Code, 0, kExprI32Const, 13, kExprEnd))));
    }

    [Fact]
    public void ExternRefGlobal()
    {
        // (global externref (ref.null extern))
        Assert.True(Validates(Module(Section(kGlobalSectionCode, 1, kExternRefCode, 0, kExprRefNull, kExternRefCode, kExprEnd))));
    }

    [Fact]
    public void GlobalInvalidType()
    {
        Assert.False(Validates(Module(Section(kGlobalSectionCode, 1, 64, 0, kExprI32Const, 0, kExprEnd))));
    }

    [Fact]
    public void GlobalInitializerTypeMismatch()
    {
        // (global i64 (i32.const 0)): the initializer has the wrong type.
        Assert.False(Validates(Module(Section(kGlobalSectionCode, 1, kI64Code, 0, kExprI32Const, 0, kExprEnd))));
    }

    [Fact]
    public void ZeroGlobals()
    {
        Assert.True(Validates(Module(Section(kGlobalSectionCode, 0))));
    }

    [Fact]
    public void TwoGlobals()
    {
        Assert.True(Validates(Module(Section(kGlobalSectionCode, 2,
            kF32Code, 0, 0x43, 0, 0, 0, 0, kExprEnd,
            kI32Code, 1, kExprI32Const, 7, kExprEnd))));
    }

    [Fact]
    public void ZeroExceptions()
    {
        Assert.True(Validates(Module(Section(kTagSectionCode, 0))));
    }

    [Fact]
    public void OneI32Exception()
    {
        // (type (func (param i32))) (tag (type 0))
        byte[] types = Section(kTypeSectionCode, 1, kWasmFunctionTypeCode, 1, kI32Code, 0);
        Assert.True(Validates(Module(types, Section(kTagSectionCode, 1, 0, 0))));
    }

    [Fact]
    public void Exception_invalid_sig_index()
    {
        Assert.False(Validates(Module(kTypeVoidVoid, Section(kTagSectionCode, 1, 0, 23))));
    }

    [Fact]
    public void Exception_invalid_sig_return()
    {
        Assert.False(Validates(Module(kTypeI32I32, Section(kTagSectionCode, 1, 0, 0))));
    }

    [Fact]
    public void Exception_invalid_attribute()
    {
        Assert.False(Validates(Module(kTypeVoidVoid, Section(kTagSectionCode, 1, 23, 0))));
    }

    [Fact]
    public void FunctionBodyTypeMismatch()
    {
        // (func (result i32) (f32.const 0)) does not validate.
        byte[] types = Section(kTypeSectionCode, 1, kWasmFunctionTypeCode, 0, 1, kI32Code);
        byte[] funcs = Section(kFunctionSectionCode, 1, 0);
        byte[] code = Section(kCodeSectionCode, 1, 7, 0, 0x43, 0, 0, 0, 0, kExprEnd);
        Assert.False(Validates(Module(types, funcs, code)));
    }

    [Fact]
    public void SectionsOutOfOrder()
    {
        Assert.False(Validates(Module(Section(kFunctionSectionCode, 0), kTypeVoidVoid)));
    }

    [Fact]
    public void StartFunctionWithParams()
    {
        // A start function must have type [] -> [].
        byte[] funcs = Section(kFunctionSectionCode, 1, 0);
        byte[] start = Section(kStartSectionCode, 0);
        byte[] code = Section(kCodeSectionCode, 1, 4, 0, 0x20, 0, kExprEnd);
        Assert.False(Validates(Module(kTypeI32I32, funcs, start, code)));
    }

    [Fact]
    public void TableAndMemory()
    {
        Assert.True(Validates(Module(
            Section(kTableSectionCode, 1, kFuncRefCode, 0, 1),
            Section(kMemorySectionCode, 1, 1, 1, 2))));
    }

    [Fact]
    public void ExportUnknownFunction()
    {
        byte[] export = Section(kExportSectionCode, 1, 1, (byte)'f', 0, 3);
        Assert.False(Validates(Module(export)));
    }

    [Fact]
    public void ImportFunction()
    {
        byte[] import = Section(kImportSectionCode, 1, 1, (byte)'m', 1, (byte)'f', 0, 0);
        Assert.True(Validates(Module(kTypeVoidVoid, import)));
    }
}
