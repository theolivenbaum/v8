// Port of V8 14.7's asm.js offset information: DecodeAsmJsOffsets
// (src/wasm/module-decoder.cc), AsmJsOffsetInformation (src/wasm/wasm-module.cc)
// and wasm::GetSourcePosition for asm.js modules. The table, written by the
// asm.js translator (WasmFunctionBuilder::AddAsmWasmOffset), maps byte
// offsets within a function's body to the JavaScript source positions of the
// call and of the number conversion of its result.
namespace V8Sharp.Wasm;

/// <summary>V8's AsmJsOffsetEntry.</summary>
public readonly record struct AsmJsOffsetEntry(int ByteOffset, int SourcePositionCall, int SourcePositionNumberConversion);

/// <summary>V8's AsmJsOffsetFunctionEntries.</summary>
public sealed class AsmJsOffsetFunctionEntries(int startOffset, int endOffset, List<AsmJsOffsetEntry> entries)
{
    public int StartOffset { get; } = startOffset;
    public int EndOffset { get; } = endOffset;
    public List<AsmJsOffsetEntry> Entries { get; } = entries;
}

/// <summary>V8's AsmJsOffsetInformation: the offset table, decoded lazily.</summary>
public sealed class AsmJsOffsetInformation(byte[] encodedOffsets)
{
    byte[]? _encodedOffsets = encodedOffsets;
    List<AsmJsOffsetFunctionEntries>? _decodedOffsets;

    /// <summary>The source position of the instruction at <paramref name="byteOffset"/> of a declared function.</summary>
    public int GetSourcePosition(int declaredFuncIndex, int byteOffset, bool isAtNumberConversion)
    {
        List<AsmJsOffsetEntry> functionOffsets = Decoded()[declaredFuncIndex].Entries;
        // If there are no positions recorded, map offset 0 (for function entry) to
        // position 0.
        if (functionOffsets.Count == 0 && byteOffset == 0) return 0;
        // std::lower_bound by byte offset.
        int lo = 0, hi = functionOffsets.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (functionOffsets[mid].ByteOffset < byteOffset) lo = mid + 1;
            else hi = mid;
        }
        if (lo == functionOffsets.Count) lo = functionOffsets.Count - 1;
        AsmJsOffsetEntry it = functionOffsets[lo];
        return isAtNumberConversion ? it.SourcePositionNumberConversion : it.SourcePositionCall;
    }

    /// <summary>The source range (start, end) of a declared function.</summary>
    public (int Start, int End) GetFunctionOffsets(int declaredFuncIndex)
    {
        AsmJsOffsetFunctionEntries functionInfo = Decoded()[declaredFuncIndex];
        return (functionInfo.StartOffset, functionInfo.EndOffset);
    }

    List<AsmJsOffsetFunctionEntries> Decoded()
    {
        lock (this)
        {
            if (_decodedOffsets is null)
            {
                _decodedOffsets = DecodeAsmJsOffsets(_encodedOffsets!);
                _encodedOffsets = null;
            }
            return _decodedOffsets;
        }
    }

    /// <summary>DecodeAsmJsOffsets (module-decoder.cc).</summary>
    public static List<AsmJsOffsetFunctionEntries> DecodeAsmJsOffsets(byte[] encodedOffsets)
    {
        int pos = 0;
        uint functionsCount = ReadU32V(encodedOffsets, ref pos);
        var functions = new List<AsmJsOffsetFunctionEntries>((int)functionsCount);
        for (uint i = 0; i < functionsCount; ++i)
        {
            uint size = ReadU32V(encodedOffsets, ref pos);
            if (size == 0)
            {
                functions.Add(new AsmJsOffsetFunctionEntries(0, 0, []));
                continue;
            }
            int tableEnd = pos + (int)size;
            uint localsSize = ReadU32V(encodedOffsets, ref pos);
            int functionStartPosition = (int)ReadU32V(encodedOffsets, ref pos);
            int functionEndPosition = functionStartPosition;
            int lastByteOffset = (int)localsSize;
            int lastAsmPosition = functionStartPosition;
            var funcAsmOffsets = new List<AsmJsOffsetEntry>((int)(size / 4))
            {
                // Add an entry for the stack check, associated with position 0.
                new(0, functionStartPosition, functionStartPosition),
            };
            while (pos < tableEnd)
            {
                lastByteOffset += (int)ReadU32V(encodedOffsets, ref pos);
                int callPosition = lastAsmPosition + ReadI32V(encodedOffsets, ref pos);
                int toNumberPosition = callPosition + ReadI32V(encodedOffsets, ref pos);
                lastAsmPosition = toNumberPosition;
                if (pos == tableEnd)
                {
                    // The last entry is the function end marker.
                    functionEndPosition = callPosition;
                }
                else
                {
                    funcAsmOffsets.Add(new AsmJsOffsetEntry(lastByteOffset, callPosition, toNumberPosition));
                }
            }
            functions.Add(new AsmJsOffsetFunctionEntries(functionStartPosition, functionEndPosition, funcAsmOffsets));
        }
        return functions;
    }

    static uint ReadU32V(byte[] bytes, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[pos++];
            result |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
    }

    static int ReadI32V(byte[] bytes, ref int pos)
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = bytes[pos++];
            result |= (b & 0x7f) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        if (shift < 32 && (b & 0x40) != 0) result |= -1 << shift;
        return result;
    }
}
