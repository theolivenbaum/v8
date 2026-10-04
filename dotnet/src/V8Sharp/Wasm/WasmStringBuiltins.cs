// The JS String Builtins (compile-time imports "wasm:js-string") and imported
// string constants: WasmJs::CompileTimeImportsFromArgument (wasm-js.cc),
// ValidateAndSetBuiltinImports (module-compiler.cc), the sanitizing of
// compile-time imports in InstanceBuilder::SanitizeImports
// (module-instantiate.cc), and the builtins of builtins/wasm-strings.tq.
//
// V8 implements the builtins as JS builtins that Liftoff/TurboFan call or
// inline. V8Sharp binds each such import to a host function of the import's
// signature that implements the builtin; strings reach wasm as
// Wacs.Core.Runtime.Builtins.JsStringRef externrefs (WasmEngine.ExternRefFor).
using System.Text;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Builtins;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using WasmModule = Wacs.Core.Module;

namespace V8Sharp.Wasm;

/// <summary>V8's CompileTimeImports: the builtin sets and the string constants module.</summary>
public sealed class CompileTimeImports
{
    public bool JsString;
    /// <summary>The module name whose global imports are string constants (importedStringConstants).</summary>
    public string? ConstantsModule;

    public bool IsEmpty => !JsString && ConstantsModule is null;

    /// <summary>WasmJs::CompileTimeImportsFromArgument (the options argument of compile/validate/Module/instantiate).</summary>
    public static CompileTimeImports FromArgument(Isolate isolate, JSValue arg)
    {
        var result = new CompileTimeImports();
        if (arg.HeapObjectOrNull is not JSReceiver receiver) return result;
        Factory factory = isolate.Factory;

        // ==================== Builtins ====================
        JSValue builtins = ObjectOps.GetProperty(isolate, receiver, factory.InternalizeString("builtins"));
        if (builtins.HeapObjectOrNull is JSReceiver builtinsObject)
        {
            double rawLength = ObjectOps.ToNumber(isolate,
                ObjectOps.GetProperty(isolate, builtinsObject, factory.InternalizeString("length"))).Number;
            // Saturating to-uint32 conversion, as V8.
            uint length = double.IsNaN(rawLength) || rawLength <= 0 ? 0
                : rawLength >= uint.MaxValue ? uint.MaxValue : (uint)rawLength;
            for (uint i = 0; i < length; i++)
            {
                if (!JSReceiver.HasElement(isolate, builtinsObject, i)) continue;
                JSValue value = JSReceiver.GetElement(isolate, builtinsObject, i);
                if (value.IsString && ObjectOps.ToString(isolate, value).ToString() == "js-string")
                {
                    result.JsString = true;
                }
            }
        }

        // ==================== String constants ====================
        JSString constantsKey = factory.InternalizeString("importedStringConstants");
        if (JSReceiver.HasProperty(isolate, receiver, constantsKey))
        {
            JSValue constants = ObjectOps.GetProperty(isolate, receiver, constantsKey);
            if (constants.IsString) result.ConstantsModule = ObjectOps.ToString(isolate, constants).ToString();
        }
        return result;
    }
}

/// <summary>A trap raised with a V8 message template (V8's runtime ThrowWasmError).</summary>
public sealed class WasmTemplateTrapException(MessageTemplate template, string message, double? argument = null)
    : TrapException(message)
{
    public MessageTemplate Template { get; } = template;
    /// <summary>The template's argument (a number), if any.</summary>
    public double? Argument { get; } = argument;
}

public static class WasmStringBuiltins
{
    public const string JsStringModule = "wasm:js-string";

    enum Sig { E_E, RE_E, I_E, I_EI, RE_EE, RE_EII, I_EE, RE_I, RE_A16II, I_EA16I }

    static readonly Dictionary<string, Sig> s_signatures = new(StringComparer.Ordinal)
    {
        ["cast"] = Sig.RE_E,
        ["test"] = Sig.I_E,
        ["fromCharCodeArray"] = Sig.RE_A16II,
        ["intoCharCodeArray"] = Sig.I_EA16I,
        ["fromCharCode"] = Sig.RE_I,
        ["fromCodePoint"] = Sig.RE_I,
        ["charCodeAt"] = Sig.I_EI,
        ["codePointAt"] = Sig.I_EI,
        ["length"] = Sig.I_E,
        ["concat"] = Sig.RE_EE,
        ["substring"] = Sig.RE_EII,
        ["equals"] = Sig.I_EE,
        ["compare"] = Sig.I_EE,
    };

    /// <summary>Whether import (module, name) is a compile-time imported builtin.</summary>
    public static bool IsBuiltin(CompileTimeImports? imports, string module, string name) =>
        imports is { JsString: true } && module == JsStringModule && s_signatures.ContainsKey(name);

    /// <summary>Whether the import is a string constant (importedStringConstants).</summary>
    public static bool IsStringConstant(CompileTimeImports? imports, string module) =>
        imports?.ConstantsModule is { } constants && constants == module;

    /// <summary>
    /// ValidateAndSetBuiltinImports: a recognized builtin import must have
    /// exactly the builtin's signature, and a string constant import must be
    /// an immutable global of an externref type. Returns the error message
    /// (with V8's "@+offset"), or null.
    /// </summary>
    public static string? ValidateImports(WasmModule module, byte[] bytes, CompileTimeImports? imports)
    {
        if (imports is null || imports.IsEmpty) return null;
        List<int> importOffsets = ImportStartOffsets(bytes);
        var types = new ModuleInstance(module).Types;
        for (int i = 0; i < module.Imports.Length; i++)
        {
            WasmModule.Import import = module.Imports[i];
            int offset = i < importOffsets.Count ? importOffsets[i] : 0;
            if (IsStringConstant(imports, import.ModuleName))
            {
                if (import.Desc is not WasmModule.ImportDesc.GlobalDesc global ||
                    global.GlobalDef.Mutability != Mutability.Immutable ||
                    global.GlobalDef.ContentType is not (ValType.ExternRef or ValType.Extern))
                {
                    return $"String constant import #{i} \"{TruncatedUserString(import.Name)}\" must be an immutable global subtyping externref @+{offset}";
                }
            }
            if (import.Desc is not WasmModule.ImportDesc.FuncDesc func) continue;
            if (!imports.JsString || import.ModuleName != JsStringModule) continue;
            if (!s_signatures.TryGetValue(import.Name, out Sig expected)) continue;
            if (types[func.TypeIndex].Expansion is not FunctionType sig || !HasSignature(sig, expected, types))
            {
                return $"Imported builtin function \"{import.ModuleName}\" \"{import.Name}\" has incorrect signature @+{offset}";
            }
        }
        return null;
    }

    static string TruncatedUserString(string name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        return utf8.Length <= 50 ? name : Encoding.UTF8.GetString(utf8, 0, 47) + "...";
    }

    // The canonical signatures of the builtins (kPredefinedSigIndex_*):
    // externref is (ref null extern), and the arrays are the predefined
    // final (array (mut i16)) in its own recursion group.
    static bool HasSignature(FunctionType sig, Sig expected, TypesSpace types)
    {
        ValType[] p = sig.ParameterTypes.Types;
        ValType[] r = sig.ResultType.Types;
        const ValType E = ValType.ExternRef;
        const ValType RE = ValType.Extern;
        const ValType I = ValType.I32;
        return expected switch
        {
            Sig.E_E => Is(p, E) && Is(r, E),
            Sig.RE_E => Is(p, E) && Is(r, RE),
            Sig.I_E => Is(p, E) && Is(r, I),
            Sig.I_EI => Is(p, E, I) && Is(r, I),
            Sig.RE_EE => Is(p, E, E) && Is(r, RE),
            Sig.RE_EII => Is(p, E, I, I) && Is(r, RE),
            Sig.I_EE => Is(p, E, E) && Is(r, I),
            Sig.RE_I => Is(p, I) && Is(r, RE),
            Sig.RE_A16II => p.Length == 3 && IsArrayI16(p[0], types) && p[1] == I && p[2] == I && Is(r, RE),
            Sig.I_EA16I => p.Length == 3 && p[0] == E && IsArrayI16(p[1], types) && p[2] == I && Is(r, I),
            _ => false,
        };
    }

    static bool Is(ValType[] types, params ValType[] expected) => types.AsSpan().SequenceEqual(expected);

    static bool IsArrayI16(ValType type, TypesSpace types)
    {
        if (!type.IsDefType() || !type.IsNullable()) return false;
        DefType def = types[type.Index()];
        return def.Expansion is ArrayType array &&
               array.ElementType.StorageType == ValType.I16 &&
               array.ElementType.Mut == Mutability.Mutable &&
               def.SuperTypes.Count == 0 && def.RecType.SubTypes.Length == 1 && def.Unroll.Final;
    }

    /// <summary>The offsets ImportStartOffset reports: the start of each import's module name length.</summary>
    static List<int> ImportStartOffsets(byte[] bytes)
    {
        var offsets = new List<int>();
        try
        {
            int pos = 8;
            while (pos < bytes.Length)
            {
                byte id = bytes[pos++];
                uint size = ReadLeb(bytes, ref pos);
                int end = pos + (int)size;
                if (id != 2)
                {
                    pos = end;
                    continue;
                }
                uint count = ReadLeb(bytes, ref pos);
                for (uint i = 0; i < count; i++)
                {
                    offsets.Add(pos);
                    pos += (int)ReadLeb(bytes, ref pos);  // module name
                    pos += (int)ReadLeb(bytes, ref pos);  // field name
                    SkipImportDesc(bytes, ref pos);
                }
                break;
            }
        }
        catch (IndexOutOfRangeException)
        {
        }
        return offsets;
    }

    static void SkipImportDesc(byte[] bytes, ref int pos)
    {
        byte kind = bytes[pos++];
        switch (kind)
        {
            case 0x00: // function
            case 0x04: // tag (attribute + type index)
                if (kind == 0x04) pos++;
                ReadLeb(bytes, ref pos);
                break;
            case 0x01: // table: reftype then limits
                SkipValType(bytes, ref pos);
                SkipLimits(bytes, ref pos);
                break;
            case 0x02: // memory
                SkipLimits(bytes, ref pos);
                break;
            case 0x03: // global: valtype, mutability
                SkipValType(bytes, ref pos);
                pos++;
                break;
            default:
                throw new IndexOutOfRangeException();
        }
    }

    static void SkipValType(byte[] bytes, ref int pos)
    {
        byte code = bytes[pos++];
        if (code is 0x63 or 0x64)
        {
            // (ref null ht) / (ref ht): a heap type (s33), optionally exact.
            if (bytes[pos] is 0x62 or 0x65) pos++;
            ReadLeb(bytes, ref pos);
        }
    }

    static void SkipLimits(byte[] bytes, ref int pos)
    {
        byte flags = bytes[pos++];
        ReadLeb64(bytes, ref pos);
        if ((flags & 1) != 0) ReadLeb64(bytes, ref pos);
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
            if (shift > 35) throw new IndexOutOfRangeException();
        }
    }

    static void ReadLeb64(byte[] bytes, ref int pos)
    {
        while ((bytes[pos++] & 0x80) != 0)
        {
        }
    }

    // ---- The builtins (builtins/wasm-strings.tq) -------------------------------------------

    static WasmTemplateTrapException IllegalCast() =>
        new(MessageTemplate.WasmTrapIllegalCast, "illegal cast");

    static string CastString(in Value value) =>
        !value.IsNullRef && value.GcRef is JsStringRef s ? s.Value : throw IllegalCast();

    static Value StringValue(string s) => new(ValType.Extern, 0L, new JsStringRef(s));

    // WasmCastToSpecialPrimitiveArray: null dereference for null, illegal
    // cast for anything but an (array (mut i16)).
    static StoreArray CastArrayI16(in Value value)
    {
        if (value.IsNullRef) throw new WasmTemplateTrapException(MessageTemplate.WasmTrapNullDereference, "null dereference");
        if (value.GcRef is not StoreArray array || array.DefType?.Expansion is not ArrayType type ||
            type.ElementType.StorageType != ValType.I16)
        {
            throw IllegalCast();
        }
        return array;
    }

    static WasmTemplateTrapException ArrayOutOfBounds() =>
        new(MessageTemplate.WasmTrapArrayOutOfBounds, "array element access out of bounds");

    /// <summary>The host function implementing builtin <paramref name="name"/>.</summary>
    public static HostFunction.RawHostFunc Create(string name) => name switch
    {
        "cast" => static (ctx, a, r) => r[0] = StringValue(CastString(a[0])),
        "test" => static (ctx, a, r) => r[0] = new Value(!a[0].IsNullRef && a[0].GcRef is JsStringRef ? 1 : 0),
        "length" => static (ctx, a, r) => r[0] = new Value(CastString(a[0]).Length),
        "concat" => static (ctx, a, r) =>
        {
            string first = CastString(a[0]);
            string second = CastString(a[1]);
            r[0] = StringValue(string.Concat(first, second));
        },
        // WasmStringViewWtf16Slice: start and end are clamped to the length.
        "substring" => static (ctx, a, r) =>
        {
            string s = CastString(a[0]);
            uint start = Math.Min(a[1].Data.UInt32, (uint)s.Length);
            uint end = Math.Min(a[2].Data.UInt32, (uint)s.Length);
            r[0] = StringValue(start >= end ? string.Empty : s.Substring((int)start, (int)(end - start)));
        },
        "equals" => static (ctx, a, r) =>
        {
            Value x = a[0], y = a[1];
            if (x.IsNullRef)
            {
                if (y.IsNullRef) r[0] = new Value(1);
                else if (y.GcRef is JsStringRef) r[0] = new Value(0);
                else throw IllegalCast();
                return;
            }
            string left = CastString(x);
            if (y.IsNullRef)
            {
                r[0] = new Value(0);
                return;
            }
            string right = CastString(y);
            r[0] = new Value(string.Equals(left, right, StringComparison.Ordinal) ? 1 : 0);
        },
        "compare" => static (ctx, a, r) =>
        {
            string first = CastString(a[0]);
            string second = CastString(a[1]);
            int c = string.CompareOrdinal(first, second);
            r[0] = new Value(c < 0 ? -1 : c > 0 ? 1 : 0);
        },
        "charCodeAt" => static (ctx, a, r) =>
        {
            string s = CastString(a[0]);
            uint index = a[1].Data.UInt32;
            if (index >= (uint)s.Length)
                throw new WasmTemplateTrapException(MessageTemplate.WasmTrapStringOffsetOutOfBounds, "string offset out of bounds");
            r[0] = new Value((int)s[(int)index]);
        },
        "codePointAt" => static (ctx, a, r) =>
        {
            string s = CastString(a[0]);
            uint index = a[1].Data.UInt32;
            if (index >= (uint)s.Length)
                throw new WasmTemplateTrapException(MessageTemplate.WasmTrapStringOffsetOutOfBounds, "string offset out of bounds");
            int i = (int)index;
            int code = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                code = char.ConvertToUtf32(s[i], s[i + 1]);
            }
            r[0] = new Value(code);
        },
        "fromCharCode" => static (ctx, a, r) => r[0] = StringValue(((char)(a[0].Data.UInt32 & 0xFFFF)).ToString()),
        "fromCodePoint" => static (ctx, a, r) =>
        {
            uint code = a[0].Data.UInt32;
            if (code <= 0xFFFF)
            {
                r[0] = StringValue(((char)code).ToString());
                return;
            }
            if (code > 0x10FFFF)
                throw new WasmTemplateTrapException(MessageTemplate.InvalidCodePoint, "invalid code point", code);
            r[0] = StringValue(char.ConvertFromUtf32((int)code));
        },
        // WasmStringNewWtf16Array.
        "fromCharCodeArray" => static (ctx, a, r) =>
        {
            StoreArray array = CastArrayI16(a[0]);
            uint start = a[1].Data.UInt32;
            uint end = a[2].Data.UInt32;
            if (start > end || end > (uint)array.Length) throw ArrayOutOfBounds();
            var chars = new char[end - start];
            for (uint i = start; i < end; i++) chars[i - start] = (char)(ushort)array[(int)i].Data.Int32;
            r[0] = StringValue(new string(chars));
        },
        // WasmStringEncodeWtf16Array.
        "intoCharCodeArray" => static (ctx, a, r) =>
        {
            string s = CastString(a[0]);
            StoreArray array = CastArrayI16(a[1]);
            uint start = a[2].Data.UInt32;
            if (start > (uint)array.Length || (uint)s.Length > (uint)array.Length - start) throw ArrayOutOfBounds();
            for (int i = 0; i < s.Length; i++) array[(int)start + i] = new Value((int)s[i]);
            r[0] = new Value(s.Length);
        },
        _ => throw new ArgumentException(name),
    };
}
