// Port of src/wasm/wasm-module-builder.{h,cc} of V8 14.7 (ZoneBuffer,
// WasmFunctionBuilder, WasmModuleBuilder) and of src/wasm/local-decl-encoder.cc
// and leb-helper.h, as far as the asm.js translator (AsmJs/AsmParser.cs) uses
// them: function, global and table types of the MVP (i32, i64, f32, f64,
// funcref), imports of functions and globals, one memory, active element
// segments, exports, the start function, the name section and the asm.js
// offset table. V8's builder also writes GC types, tags, data segments and
// memory64 for its tests and fuzzers; those parts are not ported.
//
// Deviation: none observable; the builder emits the same bytes as V8's for
// the module a given asm.js source translates to.
using System.Text;

namespace V8Sharp.Wasm;

/// <summary>value-type.h ValueTypeCode for the types the builder writes.</summary>
public enum WasmValueType : byte
{
    I32 = 0x7f,
    I64 = 0x7e,
    F32 = 0x7d,
    F64 = 0x7c,
    FuncRef = 0x70,
}

/// <summary>V8's FunctionSig (codegen/signature.h): returns and parameters.</summary>
public sealed class FunctionSig(WasmValueType[] returns, WasmValueType[] parameters) : IEquatable<FunctionSig>
{
    public WasmValueType[] Returns { get; } = returns;
    public WasmValueType[] Parameters { get; } = parameters;

    public int ReturnCount => Returns.Length;
    public int ParameterCount => Parameters.Length;

    public bool Equals(FunctionSig? other) =>
        other is not null && Returns.AsSpan().SequenceEqual(other.Returns) && Parameters.AsSpan().SequenceEqual(other.Parameters);

    public override bool Equals(object? obj) => obj is FunctionSig other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Returns.Length);
        foreach (WasmValueType t in Returns) hash.Add(t);
        foreach (WasmValueType t in Parameters) hash.Add(t);
        return hash.ToHashCode();
    }

    /// <summary>FunctionSig::Builder.</summary>
    public sealed class Builder(int returnCount, int parameterCount)
    {
        readonly WasmValueType[] _returns = new WasmValueType[returnCount];
        readonly WasmValueType[] _parameters = new WasmValueType[parameterCount];
        int _returnCount;
        int _parameterCount;

        public void AddReturn(WasmValueType type) => _returns[_returnCount++] = type;
        public void AddParam(WasmValueType type) => _parameters[_parameterCount++] = type;
        public FunctionSig Get() => new(_returns, _parameters);
    }
}

/// <summary>V8's WasmInitExpr, for the constant expressions the builder writes.</summary>
public readonly struct WasmInitExpr
{
    public enum Operator : byte { kI32Const, kI64Const, kF32Const, kF64Const, kGlobalGet }

    public Operator Kind { get; }
    readonly long _bits;

    WasmInitExpr(Operator kind, long bits)
    {
        Kind = kind;
        _bits = bits;
    }

    public static WasmInitExpr I32(int value) => new(Operator.kI32Const, value);
    public static WasmInitExpr I64(long value) => new(Operator.kI64Const, value);
    public static WasmInitExpr F32(float value) => new(Operator.kF32Const, BitConverter.SingleToInt32Bits(value));
    public static WasmInitExpr F64(double value) => new(Operator.kF64Const, BitConverter.DoubleToInt64Bits(value));
    public static WasmInitExpr GlobalGet(uint index) => new(Operator.kGlobalGet, index);

    /// <summary>WasmInitExpr::DefaultValue.</summary>
    public static WasmInitExpr DefaultValue(WasmValueType type) => type switch
    {
        WasmValueType.I32 => I32(0),
        WasmValueType.I64 => I64(0),
        WasmValueType.F32 => F32(0),
        WasmValueType.F64 => F64(0),
        _ => throw new InvalidOperationException("no default value for " + type),
    };

    /// <summary>WriteInitializerExpressionWithoutEnd.</summary>
    public void WriteWithoutEnd(ZoneBuffer buffer)
    {
        switch (Kind)
        {
            case Operator.kI32Const:
                buffer.WriteU8(WasmOpcodesConst.kExprI32Const);
                buffer.WriteI32V((int)_bits);
                break;
            case Operator.kI64Const:
                buffer.WriteU8(WasmOpcodesConst.kExprI64Const);
                buffer.WriteI64V(_bits);
                break;
            case Operator.kF32Const:
                buffer.WriteU8(WasmOpcodesConst.kExprF32Const);
                buffer.WriteU32((uint)(int)_bits);
                break;
            case Operator.kF64Const:
                buffer.WriteU8(WasmOpcodesConst.kExprF64Const);
                buffer.WriteU64((ulong)_bits);
                break;
            case Operator.kGlobalGet:
                buffer.WriteU8(WasmOpcodesConst.kExprGlobalGet);
                buffer.WriteU32V((uint)_bits);
                break;
        }
    }

    /// <summary>WriteInitializerExpression.</summary>
    public void Write(ZoneBuffer buffer)
    {
        WriteWithoutEnd(buffer);
        buffer.WriteU8(WasmOpcodesConst.kExprEnd);
    }
}

/// <summary>The single-byte opcodes the builder and the asm.js translator emit (wasm-opcodes.h).</summary>
public static class WasmOpcodesConst
{
    public const byte kExprUnreachable = 0x00;
    public const byte kExprBlock = 0x02;
    public const byte kExprLoop = 0x03;
    public const byte kExprIf = 0x04;
    public const byte kExprElse = 0x05;
    public const byte kExprEnd = 0x0b;
    public const byte kExprBr = 0x0c;
    public const byte kExprBrIf = 0x0d;
    public const byte kExprReturn = 0x0f;
    public const byte kExprCallFunction = 0x10;
    public const byte kExprCallIndirect = 0x11;
    public const byte kExprDrop = 0x1a;
    public const byte kExprLocalGet = 0x20;
    public const byte kExprLocalSet = 0x21;
    public const byte kExprLocalTee = 0x22;
    public const byte kExprGlobalGet = 0x23;
    public const byte kExprGlobalSet = 0x24;
    public const byte kExprI32Const = 0x41;
    public const byte kExprI64Const = 0x42;
    public const byte kExprF32Const = 0x43;
    public const byte kExprF64Const = 0x44;
    public const byte kExprRefFunc = 0xd2;
    public const byte kVoidCode = 0x40;
}

/// <summary>
/// V8's ZoneBuffer: a growable byte buffer with the LEB128 writers of
/// leb-helper.h and patchable padded u32v slots.
/// </summary>
public sealed class ZoneBuffer(int initial = ZoneBuffer.kInitialSize)
{
    public const int kInitialSize = 1024;
    /// <summary>leb-helper.h kPaddedVarInt32Size / kMaxVarInt32Size.</summary>
    public const int kPaddedVarInt32Size = 5;

    byte[] _buffer = new byte[Math.Max(initial, 1)];
    int _pos;

    void EnsureSpace(int size)
    {
        if (_pos + size > _buffer.Length)
        {
            Array.Resize(ref _buffer, size + _buffer.Length * 2);
        }
    }

    public void WriteU8(byte x)
    {
        EnsureSpace(1);
        _buffer[_pos++] = x;
    }

    public void WriteU32(uint x)
    {
        EnsureSpace(4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_pos), x);
        _pos += 4;
    }

    public void WriteU64(ulong x)
    {
        EnsureSpace(8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_pos), x);
        _pos += 8;
    }

    public void WriteU32V(uint val)
    {
        EnsureSpace(5);
        while (val >= 0x80)
        {
            _buffer[_pos++] = (byte)(0x80 | (val & 0x7f));
            val >>= 7;
        }
        _buffer[_pos++] = (byte)val;
    }

    public void WriteI32V(int val)
    {
        EnsureSpace(5);
        if (val >= 0)
        {
            while (val >= 0x40)
            {
                _buffer[_pos++] = (byte)(0x80 | (val & 0x7f));
                val >>= 7;
            }
            _buffer[_pos++] = (byte)val;
        }
        else
        {
            while ((val >> 6) != -1)
            {
                _buffer[_pos++] = (byte)(0x80 | (val & 0x7f));
                val >>= 7;
            }
            _buffer[_pos++] = (byte)(val & 0x7f);
        }
    }

    public void WriteI64V(long val)
    {
        EnsureSpace(10);
        if (val >= 0)
        {
            while (val >= 0x40)
            {
                _buffer[_pos++] = (byte)(0x80 | (val & 0x7f));
                val >>= 7;
            }
            _buffer[_pos++] = (byte)val;
        }
        else
        {
            while ((val >> 6) != -1)
            {
                _buffer[_pos++] = (byte)(0x80 | (val & 0x7f));
                val >>= 7;
            }
            _buffer[_pos++] = (byte)(val & 0x7f);
        }
    }

    public void WriteSize(int val) => WriteU32V((uint)val);

    public void WriteF32(float val) => WriteU32((uint)BitConverter.SingleToInt32Bits(val));

    public void WriteF64(double val) => WriteU64((ulong)BitConverter.DoubleToInt64Bits(val));

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        EnsureSpace(data.Length);
        data.CopyTo(_buffer.AsSpan(_pos));
        _pos += data.Length;
    }

    public void WriteString(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        WriteSize(bytes.Length);
        Write(bytes);
    }

    public int ReserveU32V()
    {
        int off = _pos;
        EnsureSpace(kPaddedVarInt32Size);
        _pos += kPaddedVarInt32Size;
        return off;
    }

    /// <summary>Patches a (padded) u32v at the given offset to be the given value.</summary>
    public void PatchU32V(int offset, uint val)
    {
        for (int pos = 0; pos != kPaddedVarInt32Size; ++pos)
        {
            uint next = val >> 7;
            byte @out = (byte)(val & 0x7f);
            if (pos != kPaddedVarInt32Size - 1)
            {
                _buffer[offset + pos] = (byte)(0x80 | @out);
                val = next;
            }
            else
            {
                _buffer[offset + pos] = @out;
            }
        }
    }

    public void PatchU8(int offset, byte val) => _buffer[offset] = val;

    public int Offset => _pos;
    public int Size => _pos;

    public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _pos);

    public byte[] ToArray() => _buffer.AsSpan(0, _pos).ToArray();

    public void Truncate(int size) => _pos = size;

    /// <summary>LEBHelper::sizeof_u32v.</summary>
    public static int SizeOfU32V(uint val)
    {
        int size = 0;
        do
        {
            size++;
            val >>= 7;
        } while (val > 0);
        return size;
    }
}

/// <summary>Port of src/wasm/local-decl-encoder.cc: the locals declarations of a function body.</summary>
sealed class LocalDeclEncoder
{
    readonly List<(uint Count, WasmValueType Type)> _localDecls = [];
    uint _total;

    public FunctionSig? Sig { get; set; }
    public bool HasSig => Sig is not null;

    public uint AddLocals(uint count, WasmValueType type)
    {
        uint result = (uint)(_total + (Sig?.ParameterCount ?? 0));
        _total += count;
        if (_localDecls.Count != 0 && _localDecls[^1].Type == type)
        {
            count += _localDecls[^1].Count;
            _localDecls.RemoveAt(_localDecls.Count - 1);
        }
        _localDecls.Add((count, type));
        return result;
    }

    public void Emit(ZoneBuffer buffer)
    {
        buffer.WriteU32V((uint)_localDecls.Count);
        foreach ((uint count, WasmValueType type) in _localDecls)
        {
            buffer.WriteU32V(count);
            buffer.WriteU8((byte)type);
        }
    }

    public int Size()
    {
        int size = ZoneBuffer.SizeOfU32V((uint)_localDecls.Count);
        foreach ((uint count, _) in _localDecls) size += ZoneBuffer.SizeOfU32V(count) + 1;
        return size;
    }
}

/// <summary>V8's WasmFunctionBuilder.</summary>
public sealed class WasmFunctionBuilder
{
    readonly WasmModuleBuilder _builder;
    readonly LocalDeclEncoder _locals = new();
    readonly ZoneBuffer _body = new(256);
    readonly List<(int Offset, uint DirectIndex)> _directCalls = [];
    string? _name;

    // Delta-encoded mapping from wasm bytes to asm.js source positions.
    readonly ZoneBuffer _asmOffsets = new(8);
    uint _lastAsmByteOffset;
    uint _lastAsmSourcePosition;
    uint _asmFuncStartSourcePosition;

    internal WasmFunctionBuilder(WasmModuleBuilder builder)
    {
        _builder = builder;
        FuncIndex = (uint)builder.NumDeclaredFunctions;
    }

    public WasmModuleBuilder Builder => _builder;
    public uint FuncIndex { get; }
    public uint SigIndex { get; private set; }
    public string? Name => _name;

    public void SetSignature(FunctionSig sig)
    {
        _locals.Sig = sig;
        SigIndex = _builder.AddSignature(sig);
    }

    public uint AddLocal(WasmValueType type) => _locals.AddLocals(1, type);

    public void EmitByte(byte val) => _body.WriteU8(val);
    public void EmitI32V(int val) => _body.WriteI32V(val);
    public void EmitU32V(uint val) => _body.WriteU32V(val);

    public void Emit(byte opcode) => _body.WriteU8(opcode);

    /// <summary>EmitWithPrefix: a prefixed opcode (prefix &lt;&lt; 8 | index).</summary>
    public void EmitWithPrefix(int opcode)
    {
        _body.WriteU8((byte)(opcode >> 8));
        _body.WriteU32V((uint)(opcode & 0xff));
    }

    public void EmitGetLocal(uint index) => EmitWithU32V(WasmOpcodesConst.kExprLocalGet, index);
    public void EmitSetLocal(uint index) => EmitWithU32V(WasmOpcodesConst.kExprLocalSet, index);
    public void EmitTeeLocal(uint index) => EmitWithU32V(WasmOpcodesConst.kExprLocalTee, index);

    public void EmitI32Const(int value) => EmitWithI32V(WasmOpcodesConst.kExprI32Const, value);

    public void EmitF32Const(float value)
    {
        _body.WriteU8(WasmOpcodesConst.kExprF32Const);
        _body.WriteF32(value);
    }

    public void EmitF64Const(double value)
    {
        _body.WriteU8(WasmOpcodesConst.kExprF64Const);
        _body.WriteF64(value);
    }

    public void EmitWithU8(byte opcode, byte immediate)
    {
        _body.WriteU8(opcode);
        _body.WriteU8(immediate);
    }

    public void EmitWithI32V(byte opcode, int immediate)
    {
        _body.WriteU8(opcode);
        _body.WriteI32V(immediate);
    }

    public void EmitWithU32V(byte opcode, uint immediate)
    {
        _body.WriteU8(opcode);
        _body.WriteU32V(immediate);
    }

    /// <summary>A placeholder for a direct call index, patched with the import count at WriteBody.</summary>
    public void EmitDirectCallIndex(uint index)
    {
        _directCalls.Add((_body.Size, index));
        Span<byte> placeholder = stackalloc byte[ZoneBuffer.kPaddedVarInt32Size];
        _body.Write(placeholder);
    }

    public void SetName(string name) => _name = name;

    public void AddAsmWasmOffset(int callPosition, int toNumberPosition)
    {
        // We only want to emit one mapping per byte offset.
        uint byteOffset = (uint)_body.Size;
        _asmOffsets.WriteU32V(byteOffset - _lastAsmByteOffset);
        _lastAsmByteOffset = byteOffset;

        uint callPositionU32 = (uint)callPosition;
        _asmOffsets.WriteI32V((int)(callPositionU32 - _lastAsmSourcePosition));

        uint toNumberPositionU32 = (uint)toNumberPosition;
        _asmOffsets.WriteI32V((int)(toNumberPositionU32 - callPositionU32));
        _lastAsmSourcePosition = toNumberPositionU32;
    }

    public void SetAsmFunctionStartPosition(int functionPosition)
    {
        // Must be called before emitting any asm.js source position.
        _asmFuncStartSourcePosition = (uint)functionPosition;
        _lastAsmSourcePosition = (uint)functionPosition;
    }

    public int GetPosition() => _body.Size;

    public void FixupByte(int position, byte value) => _body.PatchU8(position, value);

    public void DeleteCodeAfter(int position) => _body.Truncate(position);

    internal void WriteSignature(ZoneBuffer buffer) => buffer.WriteU32V(SigIndex);

    internal void WriteBody(ZoneBuffer buffer)
    {
        int localsSize = _locals.Size();
        buffer.WriteSize(localsSize + _body.Size);
        _locals.Emit(buffer);
        if (_body.Size > 0)
        {
            int @base = buffer.Offset;
            buffer.Write(_body.AsSpan());
            foreach ((int offset, uint directIndex) in _directCalls)
            {
                buffer.PatchU32V(@base + offset, directIndex + (uint)_builder.NumImportedFunctions);
            }
        }
    }

    internal void WriteAsmWasmOffsetTable(ZoneBuffer buffer)
    {
        if (_asmFuncStartSourcePosition == 0 && _asmOffsets.Size == 0)
        {
            buffer.WriteSize(0);
            return;
        }
        int localsEncSize = ZoneBuffer.SizeOfU32V((uint)_locals.Size());
        int funcStartSize = ZoneBuffer.SizeOfU32V(_asmFuncStartSourcePosition);
        buffer.WriteSize(_asmOffsets.Size + localsEncSize + funcStartSize);
        // Offset of the recorded byte offsets.
        buffer.WriteU32V((uint)_locals.Size());
        // Start position of the function.
        buffer.WriteU32V(_asmFuncStartSourcePosition);
        buffer.Write(_asmOffsets.AsSpan());
    }
}

/// <summary>V8's WasmModuleBuilder.</summary>
public sealed class WasmModuleBuilder
{
    // Section codes (wasm-constants.h SectionCode).
    const byte kUnknownSectionCode = 0;
    const byte kTypeSectionCode = 1;
    const byte kImportSectionCode = 2;
    const byte kFunctionSectionCode = 3;
    const byte kTableSectionCode = 4;
    const byte kMemorySectionCode = 5;
    const byte kGlobalSectionCode = 6;
    const byte kExportSectionCode = 7;
    const byte kStartSectionCode = 8;
    const byte kElementSectionCode = 9;
    const byte kCodeSectionCode = 10;
    const byte kWasmFunctionTypeCode = 0x60;
    const byte kExternalFunction = 0;
    const byte kExternalGlobal = 3;
    const byte kNameSectionFunctionCode = 1;
    const uint kWasmMagic = 0x6d736100;
    const uint kWasmVersion = 0x01;

    /// <summary>wasm-limits.h kV8MaxWasmTableSize (wasm::max_table_size()).</summary>
    public const uint kV8MaxWasmTableSize = 10_000_000;

    /// <summary>asm.js gives function indices starting with the first non-imported function.</summary>
    public enum FunctionIndexingMode { kRelativeToImports, kRelativeToDeclaredFunctions }

    sealed record ElemSegment(uint TableIndex, WasmInitExpr Offset, FunctionIndexingMode IndexingMode, uint[] FunctionEntries);

    readonly List<FunctionSig> _types = [];
    readonly Dictionary<FunctionSig, uint> _signatureMap = [];
    readonly List<(string Module, string Name, uint SigIndex)> _functionImports = [];
    readonly List<(string Module, string Name, WasmValueType Type, bool Mutability)> _globalImports = [];
    readonly List<(string Name, byte Kind, int Index)> _exports = [];
    readonly List<WasmFunctionBuilder> _functions = [];
    readonly List<(WasmValueType Type, uint MinSize, uint MaxSize)> _tables = [];
    readonly List<uint> _memories = [];
    readonly List<ElemSegment> _elementSegments = [];
    readonly List<(WasmValueType Type, bool Mutability, WasmInitExpr Init)> _globals = [];
    int _startFunctionIndex = -1;

    public int NumTables => _tables.Count;
    public int NumImportedFunctions => _functionImports.Count;
    public int NumDeclaredFunctions => _functions.Count;
    public int NumGlobals => _globals.Count;

    public WasmFunctionBuilder GetFunction(int index) => _functions[index];

    public uint AddImport(string name, FunctionSig sig, string module = "")
    {
        uint sigIndex = AddSignature(sig);
        _functionImports.Add((module, name, sigIndex));
        return (uint)(_functionImports.Count - 1);
    }

    public WasmFunctionBuilder AddFunction(FunctionSig? sig = null)
    {
        var function = new WasmFunctionBuilder(this);
        _functions.Add(function);
        if (sig is not null) function.SetSignature(sig);
        return function;
    }

    public uint AddGlobal(WasmValueType type, bool mutability, WasmInitExpr init)
    {
        _globals.Add((type, mutability, init));
        return (uint)(_globals.Count - 1);
    }

    public uint AddGlobalImport(string name, WasmValueType type, bool mutability, string module = "")
    {
        _globalImports.Add((module, name, type, mutability));
        return (uint)(_globalImports.Count - 1);
    }

    /// <summary>Helper method to create an active segment with one function.</summary>
    public void SetIndirectFunction(uint tableIndex, uint indexInTable, uint directFunctionIndex,
        FunctionIndexingMode indexingMode)
    {
        _elementSegments.Add(new ElemSegment(tableIndex, WasmInitExpr.I32((int)indexInTable), indexingMode,
            [directFunctionIndex]));
    }

    /// <summary>
    /// Increases the starting size of the table at <paramref name="tableIndex"/>
    /// by <paramref name="count"/>, and the maximum if needed. Returns the former
    /// starting size, or uint.MaxValue if the maximum table size was exceeded.
    /// </summary>
    public uint IncreaseTableMinSize(int tableIndex, uint count)
    {
        (WasmValueType type, uint oldMinSize, uint maxSize) = _tables[tableIndex];
        if (count > kV8MaxWasmTableSize - oldMinSize) return uint.MaxValue;
        _tables[tableIndex] = (type, oldMinSize + count, Math.Max(oldMinSize + count, maxSize));
        return oldMinSize;
    }

    /// <summary>Adds the signature to the module if it does not already exist.</summary>
    public uint AddSignature(FunctionSig sig)
    {
        if (_signatureMap.TryGetValue(sig, out uint index)) return index;
        index = (uint)_types.Count;
        _signatureMap[sig] = index;
        _types.Add(sig);
        return index;
    }

    public uint AddTable(WasmValueType type, uint minSize)
    {
        _tables.Add((type, minSize, 0));
        return (uint)(_tables.Count - 1);
    }

    public uint AddMemory(uint minPages)
    {
        _memories.Add(minPages);
        return (uint)(_memories.Count - 1);
    }

    public void MarkStartFunction(WasmFunctionBuilder function) => _startFunctionIndex = (int)function.FuncIndex;

    public void AddExport(string name, WasmFunctionBuilder function) =>
        _exports.Add((name, kExternalFunction, (int)function.FuncIndex));

    // Emit a section code and the size as a padded varint that can be patched later.
    static int EmitSection(byte code, ZoneBuffer buffer)
    {
        buffer.WriteU8(code);
        return buffer.ReserveU32V();
    }

    // Patch the size of a section after it's finished.
    static void FixupSection(ZoneBuffer buffer, int start) =>
        buffer.PatchU32V(start, (uint)(buffer.Offset - start - ZoneBuffer.kPaddedVarInt32Size));

    public void WriteTo(ZoneBuffer buffer)
    {
        buffer.WriteU32(kWasmMagic);
        buffer.WriteU32(kWasmVersion);

        if (_types.Count != 0)
        {
            int start = EmitSection(kTypeSectionCode, buffer);
            buffer.WriteSize(_types.Count);
            foreach (FunctionSig sig in _types)
            {
                buffer.WriteU8(kWasmFunctionTypeCode);
                buffer.WriteSize(sig.ParameterCount);
                foreach (WasmValueType param in sig.Parameters) buffer.WriteU8((byte)param);
                buffer.WriteSize(sig.ReturnCount);
                foreach (WasmValueType ret in sig.Returns) buffer.WriteU8((byte)ret);
            }
            FixupSection(buffer, start);
        }

        if (_globalImports.Count + _functionImports.Count > 0)
        {
            int start = EmitSection(kImportSectionCode, buffer);
            buffer.WriteSize(_globalImports.Count + _functionImports.Count);
            foreach ((string module, string name, WasmValueType type, bool mutability) in _globalImports)
            {
                buffer.WriteString(module);  // module name
                buffer.WriteString(name);    // field name
                buffer.WriteU8(kExternalGlobal);
                buffer.WriteU8((byte)type);
                buffer.WriteU8(mutability ? (byte)1 : (byte)0);
            }
            foreach ((string module, string name, uint sigIndex) in _functionImports)
            {
                buffer.WriteString(module);  // module name
                buffer.WriteString(name);    // field name
                buffer.WriteU8(kExternalFunction);
                buffer.WriteU32V(sigIndex);
            }
            FixupSection(buffer, start);
        }

        int numFunctionNames = 0;
        if (_functions.Count != 0)
        {
            int start = EmitSection(kFunctionSectionCode, buffer);
            buffer.WriteSize(_functions.Count);
            foreach (WasmFunctionBuilder function in _functions)
            {
                function.WriteSignature(buffer);
                if (!string.IsNullOrEmpty(function.Name)) ++numFunctionNames;
            }
            FixupSection(buffer, start);
        }

        if (_tables.Count != 0)
        {
            int start = EmitSection(kTableSectionCode, buffer);
            buffer.WriteSize(_tables.Count);
            foreach ((WasmValueType type, uint minSize, _) in _tables)
            {
                buffer.WriteU8((byte)type);
                buffer.WriteU8(0); // limits: no maximum
                buffer.WriteU32V(minSize);
            }
            FixupSection(buffer, start);
        }

        if (_memories.Count != 0)
        {
            int start = EmitSection(kMemorySectionCode, buffer);
            buffer.WriteSize(_memories.Count);
            foreach (uint minPages in _memories)
            {
                buffer.WriteU8(0); // limits: no maximum, not shared, 32-bit
                buffer.WriteU32V(minPages);
            }
            FixupSection(buffer, start);
        }

        if (_globals.Count != 0)
        {
            int start = EmitSection(kGlobalSectionCode, buffer);
            buffer.WriteSize(_globals.Count);
            foreach ((WasmValueType type, bool mutability, WasmInitExpr init) in _globals)
            {
                buffer.WriteU8((byte)type);
                buffer.WriteU8(mutability ? (byte)1 : (byte)0);
                init.Write(buffer);
            }
            FixupSection(buffer, start);
        }

        if (_exports.Count != 0)
        {
            int start = EmitSection(kExportSectionCode, buffer);
            buffer.WriteSize(_exports.Count);
            foreach ((string name, byte kind, int index) in _exports)
            {
                buffer.WriteString(name);
                buffer.WriteU8(kind);
                buffer.WriteSize(kind == kExternalFunction ? index + _functionImports.Count : index + _globalImports.Count);
            }
            FixupSection(buffer, start);
        }

        if (_startFunctionIndex >= 0)
        {
            int start = EmitSection(kStartSectionCode, buffer);
            buffer.WriteSize(_startFunctionIndex + _functionImports.Count);
            FixupSection(buffer, start);
        }

        if (_elementSegments.Count != 0)
        {
            int start = EmitSection(kElementSectionCode, buffer);
            buffer.WriteSize(_elementSegments.Count);
            foreach (ElemSegment segment in _elementSegments)
            {
                // Active, with an explicit table index and expressions as elements.
                const byte kindMask = 0b10;
                const byte expressionsAsElementsMask = 0b100;
                buffer.WriteU8(kindMask | expressionsAsElementsMask);
                buffer.WriteU32V(segment.TableIndex);
                segment.Offset.Write(buffer);
                buffer.WriteU8((byte)WasmValueType.FuncRef);
                buffer.WriteSize(segment.FunctionEntries.Length);
                foreach (uint entry in segment.FunctionEntries)
                {
                    bool needsFunctionOffset = segment.IndexingMode == FunctionIndexingMode.kRelativeToDeclaredFunctions;
                    uint index = entry + (needsFunctionOffset ? (uint)_functionImports.Count : 0);
                    buffer.WriteU8(WasmOpcodesConst.kExprRefFunc);
                    buffer.WriteU32V(index);
                    buffer.WriteU8(WasmOpcodesConst.kExprEnd);
                }
            }
            FixupSection(buffer, start);
        }

        if (_functions.Count != 0)
        {
            int start = EmitSection(kCodeSectionCode, buffer);
            buffer.WriteSize(_functions.Count);
            foreach (WasmFunctionBuilder function in _functions) function.WriteBody(buffer);
            FixupSection(buffer, start);
        }

        if (numFunctionNames > 0 || _functionImports.Count != 0)
        {
            buffer.WriteU8(kUnknownSectionCode);
            int start = buffer.ReserveU32V();
            buffer.WriteString("name");
            buffer.WriteU8(kNameSectionFunctionCode);
            int functionsStart = buffer.ReserveU32V();
            int numImports = _functionImports.Count;
            buffer.WriteSize(numImports + numFunctionNames);
            int functionIndex = 0;
            for (; functionIndex < numImports; ++functionIndex)
            {
                buffer.WriteU32V((uint)functionIndex);
                buffer.WriteString(_functionImports[functionIndex].Name);
            }
            if (numFunctionNames > 0)
            {
                foreach (WasmFunctionBuilder function in _functions)
                {
                    if (!string.IsNullOrEmpty(function.Name))
                    {
                        buffer.WriteU32V((uint)functionIndex);
                        buffer.WriteString(function.Name);
                    }
                    ++functionIndex;
                }
            }
            FixupSection(buffer, functionsStart);
            FixupSection(buffer, start);
        }
    }

    public void WriteAsmJsOffsetTable(ZoneBuffer buffer)
    {
        buffer.WriteSize(_functions.Count);
        foreach (WasmFunctionBuilder function in _functions) function.WriteAsmWasmOffsetTable(buffer);
    }
}
