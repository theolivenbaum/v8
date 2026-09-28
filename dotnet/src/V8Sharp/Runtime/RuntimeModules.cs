// Port of the module variable access of src/runtime/runtime-module.cc and
// SourceTextModule::LoadVariable / StoreVariable
// (src/objects/source-text-module.cc) as used by LdaModuleVariable,
// StaModuleVariable and the lookup slots.
//
// ES modules are not ported yet (see todo.md): these entry points throw.
namespace V8Sharp.Runtime;

public static class RuntimeModules
{
    /// <summary>SourceTextModuleDescriptor::GetCellIndexKind: positive indices are exports.</summary>
    public static bool GetCellIndexKindIsExport(int cellIndex) => cellIndex > 0;

    static JSValue NotSupported(Isolate isolate) =>
        throw new NotSupportedException("V8Sharp: ES modules are not supported yet");

    /// <summary>LdaModuleVariable: SourceTextModule::LoadVariable of the context's module.</summary>
    public static JSValue LoadVariable(Isolate isolate, Context moduleContext, int cellIndex) => NotSupported(isolate);

    /// <summary>StaModuleVariable: SourceTextModule::StoreVariable of the context's module.</summary>
    public static void StoreVariable(Isolate isolate, Context moduleContext, int cellIndex, JSValue value) => NotSupported(isolate);

    /// <summary>SourceTextModule::LoadVariable for a module found by a lookup slot.</summary>
    public static JSValue LoadVariable(Isolate isolate, HeapObject module, int cellIndex) => NotSupported(isolate);

    /// <summary>SourceTextModule::StoreVariable for a module found by a lookup slot.</summary>
    public static void StoreVariable(Isolate isolate, HeapObject module, int cellIndex, JSValue value) => NotSupported(isolate);
}
