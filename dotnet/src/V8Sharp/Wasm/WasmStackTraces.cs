// Wasm frames in stack traces: the parts of src/wasm/wasm-engine.cc
// (CreateWasmScript: the script and its wasm:// URL), wasm-objects.cc
// (WasmModuleObject::GetModuleNameOrNull, GetFunctionNameOrNull) and
// module-decoder.cc (DecodeFunctionNames) that stack traces need, and the
// wasm activations that stand in for V8's wasm stack frames.
//
// V8 walks wasm frames on the machine stack. V8Sharp's wasm frames live on the
// interpreter's call stack, so each JS-to-wasm call records an activation (the
// call-stack height it starts at); the stack trace builder puts an
// activation's frames where the JS frames show the exported function's
// wrapper, which V8 does not show either.
using System.Text;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;

namespace V8Sharp.Wasm;

/// <summary>The names of a module's name section (V8 decodes them lazily and ignores malformed ones).</summary>
sealed class WasmNames
{
    public string? ModuleName;
    public readonly Dictionary<uint, string> FunctionNames = [];

    public static WasmNames Decode(byte[] bytes)
    {
        var names = new WasmNames();
        try
        {
            int pos = 8;
            while (pos < bytes.Length)
            {
                byte id = bytes[pos++];
                uint size = ReadLeb(bytes, ref pos);
                int end = checked(pos + (int)size);
                if (end > bytes.Length) break;
                if (id == 0)
                {
                    int p = pos;
                    if (ReadName(bytes, ref p, end) == "name") DecodeNameSection(bytes, p, end, names);
                }
                pos = end;
            }
        }
        catch (Exception e) when (e is IndexOutOfRangeException or OverflowException or ArgumentException)
        {
            // A malformed section ends decoding; what was decoded stays.
        }
        return names;
    }

    // DecodeFunctionNames and the module name subsection: subsections in
    // order, the first name of a function wins, invalid UTF-8 is ignored.
    static void DecodeNameSection(byte[] bytes, int pos, int end, WasmNames names)
    {
        while (pos < end)
        {
            byte subsection = bytes[pos++];
            uint size = ReadLeb(bytes, ref pos);
            int subEnd = checked(pos + (int)size);
            if (subEnd > end) return;
            int p = pos;
            if (subsection == 0)
            {
                names.ModuleName = ReadName(bytes, ref p, subEnd);
            }
            else if (subsection == 1)
            {
                uint count = ReadLeb(bytes, ref p);
                for (uint i = 0; i < count && p < subEnd; i++)
                {
                    uint index = ReadLeb(bytes, ref p);
                    string? name = ReadName(bytes, ref p, subEnd);
                    if (name is not null) names.FunctionNames.TryAdd(index, name);
                }
            }
            pos = subEnd;
        }
    }

    static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    static string? ReadName(byte[] bytes, ref int pos, int end)
    {
        uint length = ReadLeb(bytes, ref pos);
        int start = pos;
        pos = checked(pos + (int)length);
        if (pos > end) throw new ArgumentException("name out of bounds");
        try
        {
            return s_strictUtf8.GetString(bytes, start, (int)length);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    static uint ReadLeb(byte[] bytes, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28) throw new OverflowException();
        }
    }
}

public static class WasmStackTraces
{
    internal static WasmNames GetNames(WasmModuleObject module) =>
        module.Names ??= WasmNames.Decode(module.WireBytes);

    /// <summary>WasmModuleObject::GetModuleNameOrNull.</summary>
    public static string? GetModuleName(WasmModuleObject module) => GetNames(module).ModuleName;

    /// <summary>WasmModuleObject::GetFunctionNameOrNull.</summary>
    public static string? GetFunctionName(WasmModuleObject module, int functionIndex) =>
        GetNames(module).FunctionNames.TryGetValue((uint)functionIndex, out string? name) ? name : null;

    /// <summary>GetWasmFunctionDebugName: the name, or "$func" and the index.</summary>
    public static string GetFunctionDebugName(WasmModuleObject module, int functionIndex) =>
        GetFunctionName(module, functionIndex) ?? "$func" + functionIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// CreateWasmScript: a script named wasm://wasm/&lt;hash&gt; or
    /// wasm://wasm/&lt;module name&gt;-&lt;hash&gt;, the hash being the
    /// first 8 hex digits of the wire bytes' string hash.
    /// </summary>
    internal static Script CreateScript(Isolate isolate, WasmModuleObject module)
    {
        uint hash = V8Sharp.Base.Strings.StringHasher.HashSequentialString(module.WireBytes,
            V8Sharp.Base.Strings.HashSeed.Default);
        string hex = hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        string? moduleName = GetModuleName(module);
        string url = moduleName is null ? "wasm://wasm/" + hex : "wasm://wasm/" + moduleName + "-" + hex;
        Script script = isolate.Factory.NewScript(JSValue.Undefined, Script.Type.Wasm);
        script.Name = isolate.Factory.NewStringFromUtf16(url);
        script.WasmModuleObject = module;
        return script;
    }

    /// <summary>
    /// Message::GetWasmFunctionIndex: the function whose code contains the
    /// module offset <paramref name="position"/>, or -1.
    /// </summary>
    public static int GetContainingFunction(WasmModuleObject module, int position)
    {
        var funcs = module.Module.Funcs;
        int imported = module.Module.ImportedFunctions.Count;
        for (int i = 0; i < funcs.Count; i++)
        {
            uint[] offsets = funcs[i].InstructionOffsets;
            if (offsets.Length == 0) continue;
            if (position >= offsets[0] && position <= offsets[^1]) return imported + i;
        }
        return -1;
    }

    /// <summary>The pc of a frame at its function's entry (the stack check).</summary>
    internal const int FunctionEntryPc = -2;

    /// <summary>
    /// A wasm frame: the instance, the function index, the module offset, the
    /// offset within the function's body (V8's code offset of the frame), and
    /// whether an asm.js frame is at the number conversion of an import's result.
    /// </summary>
    public readonly record struct Frame(WasmInstanceObject Instance, int FunctionIndex, int Offset, int BodyOffset = 0,
        bool AtNumberConversion = false);

    /// <summary>
    /// wasm::GetSourcePosition for an asm.js module (V8 14.7): the JavaScript
    /// source position of the instruction at <paramref name="bodyOffset"/> of
    /// function <paramref name="functionIndex"/>.
    /// </summary>
    public static int GetAsmJsSourcePosition(WasmModuleObject module, int functionIndex, int bodyOffset,
        bool atNumberConversion)
    {
        int declared = functionIndex - module.Module.ImportedFunctions.Count;
        return module.AsmJsOffsetInformation!.GetSourcePosition(declared, bodyOffset, atNumberConversion);
    }

    /// <summary>
    /// The wasm frames of the activation <paramref name="activationFromTop"/>
    /// (0 is the innermost JS-to-wasm call), top first.
    /// </summary>
    public static List<Frame> CollectFrames(Isolate isolate, int activationFromTop)
    {
        var result = new List<Frame>();
        if (isolate.WasmEngineField is not { } engine) return result;
        bool topInConversion = engine.IsInNumberConversion(activationFromTop);
        foreach (WasmStackFrame frame in engine.ActivationFrames(activationFromTop))
        {
            if (engine.Store[new FuncAddr((int)frame.FuncAddr)] is not FunctionInstance function) continue;
            if (engine.InstanceObjectFor(function.Module) is not { } instance) continue;
            uint[] offsets = function.Definition.InstructionOffsets;
            int index = frame.Pc - function.LinkedOffset;
            int offset = frame.Pc == FunctionEntryPc ? (int)function.Definition.BodyOffset
                : index >= 0 && index < offsets.Length ? (int)offsets[index] : 0;
            int bodyOffset = Math.Max(0, offset - (int)function.Definition.BodyOffset);
            result.Add(new Frame(instance, (int)function.Index.Value, offset, bodyOffset,
                AtNumberConversion: topInConversion && result.Count == 0));
        }
        return result;
    }
}
