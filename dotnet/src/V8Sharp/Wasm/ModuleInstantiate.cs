// Port of src/wasm/module-instantiate.cc: InstanceBuilder (looking up and
// checking the imports in the import object, instantiating, building the
// exports object, running the start function), and of the module-object
// parts of src/wasm/wasm-module.cc (GetImports, GetExports,
// GetCustomSections).
//
// V8 checks the imports itself and then links compiled code; V8Sharp checks
// them with V8's messages and hands the resolved store addresses to the WACS
// runtime, which allocates and initializes the instance.
using System.Text;
using Wacs.Core;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using WasmModule = Wacs.Core.Module;

namespace V8Sharp.Wasm;

/// <summary>V8's InstanceBuilder.</summary>
public sealed class InstanceBuilder
{
    readonly Isolate _isolate;
    readonly WasmEngine _engine;
    readonly ErrorThrower _thrower;
    readonly WasmModuleObject _moduleObject;
    readonly WasmModule _module;
    readonly JSReceiver? _ffi;
    readonly TypesSpace _types;
    readonly ModuleInstance _typesModule;
    readonly JSValue[] _sanitizedImports;

    InstanceBuilder(Isolate isolate, ErrorThrower thrower, WasmModuleObject moduleObject, JSReceiver? ffi)
    {
        _isolate = isolate;
        _engine = WasmEngine.Get(isolate);
        _thrower = thrower;
        _moduleObject = moduleObject;
        _module = moduleObject.Module;
        _ffi = ffi;
        _typesModule = new ModuleInstance(_module);
        _types = _typesModule.Types;
        _sanitizedImports = new JSValue[_module.Imports.Length];
    }

    /// <summary>
    /// WasmEngine::SyncInstantiate: builds an instance or throws (TypeError,
    /// LinkError, RuntimeError, or whatever an import getter or the start
    /// function throws).
    /// </summary>
    public static WasmInstanceObject Build(Isolate isolate, ErrorThrower thrower, WasmModuleObject moduleObject,
        JSReceiver? ffi) =>
        new InstanceBuilder(isolate, thrower, moduleObject, ffi).Build();

    string ImportName(int index)
    {
        WasmModule.Import import = _module.Imports[index];
        return $"Import #{index} \"{import.ModuleName}\" \"{import.Name}\"";
    }

    string ImportName(int index, string moduleName) => $"Import #{index} \"{moduleName}\"";

    WasmInstanceObject Build()
    {
        SanitizeImports();
        var imports = new IAddress?[_module.Imports.Length];
        ProcessImportedMemories(imports);
        ProcessImports(imports);

        ModuleInstance instance;
        try
        {
            instance = _engine.Runtime.InstantiateModule(_module, imports,
                new RuntimeOptions { SkipModuleValidation = true, SkipStartFunction = true });
        }
        catch (TrapException e)
        {
            _thrower.RuntimeError(MessageFormatter.TemplateString(WasmErrorMessages.TrapTemplate(e)));
            return null!;
        }
        catch (NotSupportedException e)
        {
            _thrower.LinkError(e.Message);
            return null!;
        }
        catch (Exception e) when (e is InstantiationException or WasmRuntimeException or InvalidDataException)
        {
            _thrower.RuntimeError(e.Message);
            return null!;
        }

        var instanceObject = (WasmInstanceObject)JSObject.NewWithMap(_isolate,
            _isolate.NativeContext.WasmInstanceConstructor.InitialMap);
        instanceObject.ModuleObject = _moduleObject;
        instanceObject.Instance = instance;
        ProcessExports(instanceObject, imports);

        // Run the start function if one was specified.
        if (instance.StartFunc != FuncAddr.Null)
        {
            ExecuteStartFunction(instance.StartFunc);
        }
        return instanceObject;
    }

    /// <summary>InstanceBuilder::SanitizeImports: reads every import from the import object.</summary>
    void SanitizeImports()
    {
        for (int index = 0; index < _module.Imports.Length; index++)
        {
            WasmModule.Import import = _module.Imports[index];
            if (_ffi is null)
            {
                // No point in continuing if we don't have an imports object.
                _thrower.TypeError("Imports argument must be present and must be an object");
            }
            _sanitizedImports[index] = LookupImport(index, import.ModuleName, import.Name);
        }
    }

    /// <summary>InstanceBuilder::LookupImport.</summary>
    JSValue LookupImport(int index, string moduleName, string importName)
    {
        Factory factory = _isolate.Factory;
        JSValue module = ObjectOps.GetPropertyOrElement(_isolate, _ffi!, factory.InternalizeString(moduleName));
        if (!module.IsJSReceiver)
        {
            _thrower.TypeError(ImportName(index, moduleName) + ": module is not an object or function");
        }
        return ObjectOps.GetPropertyOrElement(_isolate, module, factory.InternalizeString(importName));
    }

    /// <summary>InstanceBuilder::ProcessImportedMemories.</summary>
    void ProcessImportedMemories(IAddress?[] imports)
    {
        for (int index = 0; index < _module.Imports.Length; index++)
        {
            if (_module.Imports[index].Desc is not WasmModule.ImportDesc.MemDesc memDesc) continue;
            JSValue value = _sanitizedImports[index];
            if (value.HeapObjectOrNull is not WasmMemoryObject memoryObject)
            {
                _thrower.LinkError(ImportName(index) + ": memory import must be a WebAssembly.Memory object");
                return;
            }
            Limits declared = memDesc.MemDef.Limits;
            Limits imported = memoryObject.Memory.Type.Limits;
            if (declared.AddressType != imported.AddressType)
            {
                _thrower.LinkError($"cannot import {AddressTypeToStr(imported.AddressType)} memory as {AddressTypeToStr(declared.AddressType)}");
            }
            long importedCurPages = memoryObject.Memory.Size;
            if (importedCurPages < declared.Minimum)
            {
                _thrower.LinkError($"{ImportName(index)}: memory import has {importedCurPages} pages which is smaller than the declared initial of {declared.Minimum}");
            }
            if (declared.Maximum is long declaredMax)
            {
                if (imported.Maximum is not long importedMax)
                {
                    _thrower.LinkError($"{ImportName(index)}: memory import has no maximum limit, expected at most {-1}");
                    return;
                }
                if (importedMax > declaredMax)
                {
                    _thrower.LinkError($"{ImportName(index)}: memory import has a larger maximum size {importedMax} than the module's declared maximum {declaredMax}");
                }
            }
            if (declared.Shared != imported.Shared)
            {
                _thrower.LinkError($"{ImportName(index)}: mismatch in shared state of memory, declared = {(declared.Shared ? 1 : 0)}, imported = {(imported.Shared ? 1 : 0)}");
            }
            imports[index] = memoryObject.Address;
        }
    }

    static string AddressTypeToStr(AddrType type) => type == AddrType.I64 ? "i64" : "i32";

    /// <summary>InstanceBuilder::ProcessImports.</summary>
    void ProcessImports(IAddress?[] imports)
    {
        for (int index = 0; index < _module.Imports.Length; index++)
        {
            WasmModule.Import import = _module.Imports[index];
            JSValue value = _sanitizedImports[index];
            switch (import.Desc)
            {
                case WasmModule.ImportDesc.FuncDesc funcDesc:
                    imports[index] = ProcessImportedFunction(index, funcDesc, value);
                    break;
                case WasmModule.ImportDesc.TableDesc tableDesc:
                    imports[index] = ProcessImportedTable(index, tableDesc.TableDef, value);
                    break;
                case WasmModule.ImportDesc.MemDesc:
                    // Imported memories are already handled earlier via
                    // {ProcessImportedMemories}.
                    break;
                case WasmModule.ImportDesc.GlobalDesc globalDesc:
                    imports[index] = ProcessImportedGlobal(index, globalDesc.GlobalDef, value);
                    break;
                case WasmModule.ImportDesc.TagDesc tagDesc:
                {
                    if (value.HeapObjectOrNull is not WasmTagObject importedTag)
                    {
                        _thrower.LinkError(ImportName(index) + ": tag import requires a WebAssembly.Tag");
                        return;
                    }
                    DefType expected = _types[tagDesc.TagDef.TypeIndex];
                    if (!importedTag.Tag.Type.Matches(expected, _types) ||
                        expected.Expansion is not FunctionType expectedSig ||
                        !importedTag.Signature.Matches(expectedSig, _types))
                    {
                        _thrower.LinkError(ImportName(index) + ": imported tag does not match the expected type");
                    }
                    imports[index] = importedTag.Address;
                    break;
                }
            }
        }
    }

    /// <summary>InstanceBuilder::ProcessImportedFunction.</summary>
    FuncAddr ProcessImportedFunction(int index, WasmModule.ImportDesc.FuncDesc funcDesc, JSValue value)
    {
        JSReceiver callable;
        bool suspending = false;
        if (value.HeapObjectOrNull is WasmSuspendingObject suspendingObject)
        {
            callable = suspendingObject.Callable;
            suspending = true;
        }
        else if (!ObjectOps.IsCallable(value))
        {
            _thrower.LinkError(ImportName(index) + ": function import requires a callable");
            return default;
        }
        else
        {
            callable = (JSReceiver)value.Object;
        }

        DefType expectedType = _types[funcDesc.TypeIndex];
        var expectedSig = (FunctionType)expectedType.Expansion;
        if (!suspending && WasmObjects.GetExportedFunctionData(value) is { } data)
        {
            // kWasmToWasm: the import is a wasm function (of another instance);
            // its type must match.
            IFunctionInstance func = _engine.Store[data.Address];
            bool matches = func is FunctionInstance wasmFunc
                ? wasmFunc.DefType.Matches(expectedType, _types)
                : func.Type.Matches(expectedSig, _types);
            if (!matches)
            {
                _thrower.LinkError(ImportName(index) + ": imported function does not match the expected type");
            }
            return data.Address;
        }

        // A JS function: the WasmToJS wrapper.
        WasmModule.Import import = _module.Imports[index];
        return WasmJs.NewImportWrapper(_engine, callable, expectedSig, import.ModuleName, import.Name, suspending, _typesModule);
    }

    /// <summary>InstanceBuilder::ProcessImportedTable.</summary>
    TableAddr ProcessImportedTable(int index, TableType table, JSValue value)
    {
        if (value.HeapObjectOrNull is not WasmTableObject tableObject)
        {
            _thrower.LinkError(ImportName(index) + ": table import requires a WebAssembly.Table");
            return default;
        }
        long importedTableSize = tableObject.CurrentLength;
        if (importedTableSize < table.Limits.Minimum)
        {
            _thrower.LinkError($"table import {index} is smaller than initial {table.Limits.Minimum}, got {importedTableSize}");
        }
        if (table.Limits.Maximum is long declaredMax)
        {
            if (tableObject.Table.Type.Limits.Maximum is not long maxSize)
            {
                _thrower.LinkError($"table import {index} has no maximum length; required: {declaredMax}");
                return default;
            }
            if (maxSize > declaredMax)
            {
                _thrower.LinkError($"table import {index} has a larger maximum size {maxSize:x} than the module's declared maximum {declaredMax}");
            }
        }
        if (table.Limits.AddressType != tableObject.Table.Type.Limits.AddressType)
        {
            _thrower.LinkError($"cannot import {AddressTypeToStr(tableObject.Table.Type.Limits.AddressType)} table as {AddressTypeToStr(table.Limits.AddressType)}");
        }
        // Tables are mutable: the element types must be equivalent.
        ValType actual = tableObject.ElementType;
        TypesSpace? actualTypes = tableObject.Module?.Types;
        bool equivalent = actual.IsDefType() || table.ElementType.IsDefType()
            ? actual.IsDefType() && table.ElementType.IsDefType() && actualTypes is not null &&
              actualTypes[actual.Index()].Matches(_types[table.ElementType.Index()], _types) &&
              _types[table.ElementType.Index()].Matches(actualTypes[actual.Index()], actualTypes) &&
              actual.IsNullable() == table.ElementType.IsNullable()
            : actual == table.ElementType;
        if (!equivalent)
        {
            _thrower.LinkError(ImportName(index) + ": imported table does not match the expected type");
        }
        return tableObject.Address;
    }

    /// <summary>InstanceBuilder::ProcessImportedGlobal.</summary>
    GlobalAddr ProcessImportedGlobal(int index, GlobalType global, JSValue value)
    {
        ValType type = global.ContentType;
        bool mutable = global.Mutability == Mutability.Mutable;
        if (type == ValType.V128 && value.HeapObjectOrNull is not WasmGlobalObject)
        {
            _thrower.LinkError(ImportName(index) + ": global import of type v128 must be a WebAssembly.Global");
        }
        if (value.HeapObjectOrNull is WasmGlobalObject globalObject)
        {
            if (globalObject.IsMutable != mutable)
            {
                _thrower.LinkError(ImportName(index) + ": imported global does not match the expected mutability");
            }
            ValType actual = globalObject.Type;
            TypesSpace? sourceTypes = globalObject.Module?.Types;
            bool validType = actual.IsDefType() || type.IsDefType()
                ? actual.IsDefType() && type.IsDefType() && sourceTypes is not null &&
                  sourceTypes[actual.Index()].Matches(_types[type.Index()], _types) &&
                  (!mutable || _types[type.Index()].Matches(sourceTypes[actual.Index()], sourceTypes)) &&
                  (actual.IsNullable() == type.IsNullable() || (!mutable && type.IsNullable()))
                : mutable ? actual == type : actual.Matches(type, _types);
            if (!validType)
            {
                _thrower.LinkError(ImportName(index) + ": imported global does not match the expected type");
            }
            if (mutable) return globalObject.Address;
            return _engine.Runtime.AllocateGlobal(global, globalObject.Global.Value);
        }

        if (mutable)
        {
            _thrower.LinkError(ImportName(index) + ": imported mutable global must be a WebAssembly.Global object");
        }

        Value wasmValue;
        if (type.IsRefType())
        {
            if (!_engine.TryJSToWasmRef(value, type, _typesModule, out wasmValue, out string? error))
            {
                _thrower.LinkError(ImportName(index) + ": " + error);
            }
        }
        else if (value.IsNumber && type != ValType.I64)
        {
            double number = value.Number;
            wasmValue = type switch
            {
                ValType.I32 => new Value(Base.Numbers.Conversions.DoubleToInt32(number)),
                ValType.F32 => new Value((float)number),
                _ => new Value(number),
            };
        }
        else if (type == ValType.I64 && value.HeapObjectOrNull is BigInt bigint)
        {
            wasmValue = new Value(BigInt.AsInt64(bigint, out _));
        }
        else
        {
            _thrower.LinkError(ImportName(index) + ": global import must be a number, valid Wasm reference, or WebAssembly.Global object");
            return default;
        }
        return _engine.Runtime.AllocateGlobal(global, wasmValue);
    }

    /// <summary>InstanceBuilder::ProcessExports.</summary>
    void ProcessExports(WasmInstanceObject instanceObject, IAddress?[] imports)
    {
        Isolate isolate = _isolate;
        ModuleInstance instance = instanceObject.Instance;
        // If an imported WebAssembly global gets exported, the export has to be
        // identical to to import.
        var importedGlobals = new Dictionary<int, JSValue>();
        int globalIndex = 0;
        for (int index = 0; index < _module.Imports.Length; index++)
        {
            if (_module.Imports[index].Desc is not WasmModule.ImportDesc.GlobalDesc) continue;
            if (_sanitizedImports[index].HeapObjectOrNull is WasmGlobalObject)
            {
                importedGlobals[globalIndex] = _sanitizedImports[index];
            }
            globalIndex++;
        }

        JSObject exportsObject = isolate.Factory.NewSlowJSObjectWithNullProto();
        instanceObject.ExportsObject = exportsObject;
        const PropertyAttributes attributes = PropertyAttributes.READ_ONLY | PropertyAttributes.DONT_DELETE;

        foreach (WasmModule.Export export in _module.Exports)
        {
            JSValue value;
            switch (export.Desc)
            {
                case WasmModule.ExportDesc.FuncDesc fd:
                {
                    FuncAddr address = instance.FuncAddrs[fd.FunctionIndex];
                    value = _engine.GetOrCreateExportedFunction(address, (int)fd.FunctionIndex.Value, instanceObject);
                    break;
                }
                case WasmModule.ExportDesc.TableDesc td:
                    value = _engine.GetOrCreateTableObject(instance.TableAddrs[td.TableIndex], instance);
                    break;
                case WasmModule.ExportDesc.MemDesc md:
                    value = _engine.GetOrCreateMemoryObject(instance.MemAddrs[md.MemoryIndex]);
                    break;
                case WasmModule.ExportDesc.GlobalDesc gd:
                {
                    int gi = (int)gd.GlobalIndex.Value;
                    value = importedGlobals.TryGetValue(gi, out JSValue imported)
                        ? imported
                        : _engine.GetOrCreateGlobalObject(instance.GlobalAddrs[gd.GlobalIndex], instance);
                    break;
                }
                case WasmModule.ExportDesc.TagDesc tgd:
                    value = _engine.GetOrCreateTagObject(instance.TagAddrs[tgd.TagIndex], instance);
                    break;
                default:
                    continue;
            }
            JSString name = isolate.Factory.InternalizeString(export.Name);
            if (name.AsArrayIndex(out uint arrayIndex))
            {
                JSObject.AddDataElement(isolate, exportsObject, arrayIndex, value, attributes);
            }
            else
            {
                JSObject.AddProperty(isolate, exportsObject, name, value, attributes);
            }
        }
        JSReceiver.SetIntegrityLevel(isolate, exportsObject, JSReceiver.IntegrityLevel.FROZEN, ShouldThrow.DontThrow);
    }

    /// <summary>InstanceBuilder::ExecuteStartFunction.</summary>
    void ExecuteStartFunction(FuncAddr start) => WasmJs.InvokeWasm(_engine, start, []);
}

/// <summary>The module-object parts of src/wasm/wasm-module.cc.</summary>
public static class WasmModuleObjectOps
{
    static string ExternalKindName(object desc) => desc switch
    {
        WasmModule.ImportDesc.FuncDesc or WasmModule.ExportDesc.FuncDesc => "function",
        WasmModule.ImportDesc.TableDesc or WasmModule.ExportDesc.TableDesc => "table",
        WasmModule.ImportDesc.MemDesc or WasmModule.ExportDesc.MemDesc => "memory",
        WasmModule.ImportDesc.GlobalDesc or WasmModule.ExportDesc.GlobalDesc => "global",
        _ => "tag",
    };

    /// <summary>wasm::GetImports: [{module, name, kind}].</summary>
    public static JSArray GetImports(Isolate isolate, WasmModuleObject moduleObject)
    {
        Factory f = isolate.Factory;
        WasmModule.Import[] imports = moduleObject.Module.Imports;
        var elements = f.NewFixedArray(imports.Length);
        JSString moduleString = f.InternalizeString("module");
        JSString nameString = f.InternalizeString("name");
        JSString kindString = f.InternalizeString("kind");
        for (int i = 0; i < imports.Length; i++)
        {
            JSObject entry = f.NewJSObject(isolate.NativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, entry, moduleString, f.NewStringFromUtf16(imports[i].ModuleName), PropertyAttributes.NONE);
            JSObject.AddProperty(isolate, entry, nameString, f.NewStringFromUtf16(imports[i].Name), PropertyAttributes.NONE);
            JSObject.AddProperty(isolate, entry, kindString, f.InternalizeString(ExternalKindName(imports[i].Desc)), PropertyAttributes.NONE);
            elements[i] = entry;
        }
        return f.NewJSArrayWithElements(elements);
    }

    /// <summary>wasm::GetExports: [{name, kind}].</summary>
    public static JSArray GetExports(Isolate isolate, WasmModuleObject moduleObject)
    {
        Factory f = isolate.Factory;
        WasmModule.Export[] exports = moduleObject.Module.Exports;
        var elements = f.NewFixedArray(exports.Length);
        JSString nameString = f.InternalizeString("name");
        JSString kindString = f.InternalizeString("kind");
        for (int i = 0; i < exports.Length; i++)
        {
            JSObject entry = f.NewJSObject(isolate.NativeContext.ObjectFunction);
            JSObject.AddProperty(isolate, entry, nameString, f.NewStringFromUtf16(exports[i].Name), PropertyAttributes.NONE);
            JSObject.AddProperty(isolate, entry, kindString, f.InternalizeString(ExternalKindName(exports[i].Desc)), PropertyAttributes.NONE);
            elements[i] = entry;
        }
        return f.NewJSArrayWithElements(elements);
    }

    /// <summary>wasm::GetCustomSections: ArrayBuffer copies of the custom sections named <paramref name="name"/>.</summary>
    public static JSArray GetCustomSections(Isolate isolate, WasmModuleObject moduleObject, string name)
    {
        Factory f = isolate.Factory;
        var matches = new List<JSValue>();
        byte[] bytes = moduleObject.WireBytes;
        int pos = 8;
        while (pos < bytes.Length)
        {
            byte id = bytes[pos++];
            uint size = ReadLeb(bytes, ref pos);
            int end = pos + (int)size;
            if (id == 0)
            {
                int p = pos;
                uint nameLength = ReadLeb(bytes, ref p);
                string sectionName = Encoding.UTF8.GetString(bytes, p, (int)nameLength);
                p += (int)nameLength;
                if (sectionName == name)
                {
                    int payload = end - p;
                    JSArrayBuffer buffer = f.NewJSArrayBufferAndBackingStore((ulong)payload)!;
                    bytes.AsSpan(p, payload).CopyTo(buffer.BackingStoreBuffer);
                    matches.Add(buffer);
                }
            }
            pos = end;
        }
        return f.NewJSArrayWithElements(f.NewFixedArrayFrom(matches.ToArray()));
    }

    static uint ReadLeb(byte[] bytes, ref int pos)
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
}
