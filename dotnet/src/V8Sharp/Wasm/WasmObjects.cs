// Port of the JS API objects of src/wasm/wasm-objects.{h,cc}:
// WasmModuleObject, WasmInstanceObject, WasmMemoryObject, WasmTableObject,
// WasmGlobalObject, WasmTagObject, WasmExceptionPackage, WasmSuspendingObject
// and WasmExportedFunctionData.
//
// V8's objects point at its compiled NativeModule, trusted instance data and
// raw buffers; V8Sharp's point at the WACS runtime (V8Sharp.Wasm): a decoded
// Wacs.Core.Module, a ModuleInstance and the store addresses of memories,
// tables, globals and tags (deviations.md, "WebAssembly").
using Wacs.Core;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using WasmModule = Wacs.Core.Module;

namespace V8Sharp.Wasm;

/// <summary>V8's WasmModuleObject: a compiled (here: decoded and validated) module.</summary>
public sealed class WasmModuleObject(Map map) : JSObject(map)
{
    /// <summary>The decoded module.</summary>
    public WasmModule Module = null!;

    /// <summary>The module's wire bytes (V8's NativeModule::wire_bytes).</summary>
    public byte[] WireBytes = null!;

    /// <summary>The number of instances created (%WasmGetNumberOfInstances).</summary>
    public int InstanceCount;

    /// <summary>The module's script (V8's WasmModuleObject::script), named wasm://wasm/....</summary>
    public Script Script = null!;

    /// <summary>The compile-time imports the module was compiled with (V8: on the NativeModule).</summary>
    public CompileTimeImports? CompileImports;

    /// <summary>Whether <see cref="Module"/> was instantiated (linked) already.</summary>
    internal bool ModuleLinked;

    /// <summary>The decoded name section, lazily.</summary>
    internal WasmNames? Names;
}

/// <summary>V8's WasmInstanceObject.</summary>
public sealed class WasmInstanceObject(Map map) : JSObject(map)
{
    public WasmModuleObject ModuleObject = null!;
    public ModuleInstance Instance = null!;

    /// <summary>The frozen, null-prototype exports object.</summary>
    public JSObject ExportsObject = null!;
}

/// <summary>V8's WasmMemoryObject: a memory and the ArrayBuffer that aliases it.</summary>
public sealed class WasmMemoryObject(Map map) : JSObject(map)
{
    public const int kNoMaximum = -1;

    public MemAddr Address;
    public MemoryInstance Memory = null!;

    /// <summary>The current buffer (V8's array_buffer), created lazily.</summary>
    public JSArrayBuffer? ArrayBuffer;

    /// <summary>Whether <see cref="ArrayBuffer"/> is the resizable (toResizableBuffer) form.</summary>
    public bool BufferIsResizable;

    public bool IsMemory64 => Memory.Type.Limits.AddressType == AddrType.I64;

    public bool IsShared => Memory.Type.Limits.Shared;

    /// <summary>WasmMemoryObject::maximum_pages: the declared maximum, or the engine limit.</summary>
    public ulong MaximumPages =>
        Memory.Type.Limits.Maximum is long max ? (ulong)max : WasmLimits.MaxMemoryPages(IsMemory64);

    public bool HasMaximumPages => Memory.Type.Limits.Maximum.HasValue;
}

/// <summary>V8's WasmTableObject.</summary>
public sealed class WasmTableObject(Map map) : JSObject(map)
{
    public TableAddr Address;
    public TableInstance Table = null!;

    /// <summary>The module whose type indices the element type refers to, if any.</summary>
    public ModuleInstance? Module;

    public ValType ElementType => Table.Type.ElementType;

    public bool IsTable64 => Table.Type.Limits.AddressType == AddrType.I64;

    public int CurrentLength => Table.Elements.Count;
}

/// <summary>V8's WasmGlobalObject.</summary>
public sealed class WasmGlobalObject(Map map) : JSObject(map)
{
    public GlobalAddr Address;
    public GlobalInstance Global = null!;

    /// <summary>The module whose type indices the value type refers to, if any.</summary>
    public ModuleInstance? Module;

    public ValType Type => Global.Type.ContentType;

    public bool IsMutable => Global.Type.Mutability == Mutability.Mutable;
}

/// <summary>V8's WasmTagObject.</summary>
public sealed class WasmTagObject(Map map) : JSObject(map)
{
    TagAddr _address;
    TagInstance? _tag;
    FunctionType? _signature;

    /// <summary>
    /// WebAssembly.JSTag: its tag is the engine's, which is created with the
    /// engine (on first use of wasm), not with the context.
    /// </summary>
    public bool IsJSTag;

    public TagAddr Address
    {
        get
        {
            if (IsJSTag && _tag is null) BindJSTag();
            return _address;
        }
        set => _address = value;
    }

    public TagInstance Tag
    {
        get
        {
            if (IsJSTag && _tag is null) BindJSTag();
            return _tag!;
        }
        set => _tag = value;
    }

    /// <summary>The tag's parameter types (V8's serialized_signature).</summary>
    public FunctionType Signature
    {
        get
        {
            if (IsJSTag && _tag is null) BindJSTag();
            return _signature!;
        }
        set => _signature = value;
    }

    void BindJSTag()
    {
        WasmEngine engine = WasmEngine.Get(Isolate.Current!);
        _address = engine.JSTag;
        _tag = engine.Store[_address];
        _signature = (FunctionType)_tag.Type.Expansion;
        engine.RegisterTagObject(this);
    }

    /// <summary>The module whose type indices the signature refers to, if any.</summary>
    public ModuleInstance? Module;
}

/// <summary>
/// V8's WasmExceptionPackage: the JS object of a wasm exception
/// (WebAssembly.Exception). It holds the wasm exception reference, so that an
/// exception that leaves wasm, passes through JavaScript and enters wasm again
/// is the same exception.
/// </summary>
public sealed class WasmExceptionPackage(Map map) : JSObject(map)
{
    /// <summary>The exnref (its GcRef is the WACS ExnInstance).</summary>
    public Value ExnRef;

    public ExnInstance Exn => (ExnInstance)ExnRef.GcRef!;

    /// <summary>The JS tag object, when known.</summary>
    public WasmTagObject? TagObject;
}

/// <summary>V8's WasmSuspendingObject (JSPI: WebAssembly.Suspending).</summary>
public sealed class WasmSuspendingObject(Map map) : JSObject(map)
{
    public JSReceiver Callable = null!;
}

/// <summary>
/// The JS object of a wasm GC struct or array that reaches JavaScript. V8's
/// WasmStruct and WasmArray are themselves receivers (opaque, with a null
/// prototype); V8Sharp wraps the WACS object (deviations.md).
/// </summary>
public sealed class WasmGCObjectWrapper(Map map) : JSObject(map)
{
    /// <summary>The wasm reference (type, address and GC object).</summary>
    public Value Ref;
}

/// <summary>
/// V8's WasmExportedFunctionData: the function data of an exported wasm
/// function (a JSFunction). V8Sharp's exported functions are API functions
/// whose callback is the JS-to-wasm wrapper (WasmJs.CallExportedFunction).
/// </summary>
public sealed class WasmExportedFunctionData(BuiltinFunction callback) : FunctionTemplateInfo(callback)
{
    public WasmEngine Engine = null!;
    public FuncAddr Address;
    public FunctionType Signature = null!;

    /// <summary>The function index in the module that first exported it (V8's function_index).</summary>
    public int FunctionIndex;

    /// <summary>The instance the function belongs to (null for host functions).</summary>
    public WasmInstanceObject? Instance;

    /// <summary>WebAssembly.promising: the JSPI entry wrapper.</summary>
    public bool IsPromising;
}

/// <summary>Engine limits (src/wasm/wasm-limits.h), as far as the JS API checks them.</summary>
public static class WasmLimits
{
    /// <summary>kSpecMaxMemory32Pages.</summary>
    public const ulong kSpecMaxMemory32Pages = 65536;

    /// <summary>kSpecMaxMemory64Pages.</summary>
    public const ulong kSpecMaxMemory64Pages = 262144;

    /// <summary>kV8MaxWasmTableInitEntries.</summary>
    public const uint kV8MaxWasmTableInitEntries = 10_000_000;

    /// <summary>kV8MaxWasmTableSize.</summary>
    public const uint kV8MaxWasmTableSize = 10_000_000;

    /// <summary>kV8MaxWasmFunctionParams.</summary>
    public const int kV8MaxWasmFunctionParams = 1000;

    /// <summary>kV8MaxWasmModuleSize.</summary>
    public const int kV8MaxWasmModuleSize = 1024 * 1024 * 1024;

    /// <summary>
    /// The maximum number of pages a memory can have here: V8's
    /// kV8MaxWasmMemory{32,64}Pages, bounded by what a managed array holds
    /// (V8Sharp: 32767 pages, deviations.md).
    /// </summary>
    public static ulong MaxMemoryPages(bool memory64) =>
        Math.Min(memory64 ? kSpecMaxMemory64Pages : kSpecMaxMemory32Pages, (ulong)Wacs.Core.Utilities.Constants.HostMaxPages);
}

public static class WasmObjects
{
    /// <summary>The JSObject.AllocateForMap cases of the wasm instance types.</summary>
    public static JSObject AllocateForMap(Map map) => map.InstanceType switch
    {
        InstanceType.WasmModuleObjectType => new WasmModuleObject(map),
        InstanceType.WasmInstanceObjectType => new WasmInstanceObject(map),
        InstanceType.WasmMemoryObjectType => new WasmMemoryObject(map),
        InstanceType.WasmTableObjectType => new WasmTableObject(map),
        InstanceType.WasmGlobalObjectType => new WasmGlobalObject(map),
        InstanceType.WasmTagObjectType => new WasmTagObject(map),
        InstanceType.WasmExceptionPackageType => new WasmExceptionPackage(map),
        InstanceType.WasmSuspendingObjectType => new WasmSuspendingObject(map),
        InstanceType.WasmStructType or InstanceType.WasmArrayType => new WasmGCObjectWrapper(map),
        _ => throw new InvalidOperationException("AllocateForMap: " + map.InstanceType),
    };

    /// <summary>WasmExportedFunction::IsWasmExportedFunction.</summary>
    public static bool IsWasmExportedFunction(JSValue value) =>
        value.HeapObjectOrNull is JSFunction f && f.Shared.FunctionData is WasmExportedFunctionData;

    public static WasmExportedFunctionData? GetExportedFunctionData(JSValue value) =>
        value.HeapObjectOrNull is JSFunction f ? f.Shared.FunctionData as WasmExportedFunctionData : null;
}
