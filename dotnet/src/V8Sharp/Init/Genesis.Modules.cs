// Port of Genesis::InitializeGlobal_js_source_phase_imports (src/init/bootstrapper.cc):
// %AbstractModuleSource% and its prototype's @@toStringTag getter.
namespace V8Sharp.Init;

sealed partial class Genesis
{
    void InitializeGlobal_js_source_phase_imports()
    {
        if (!_isolate.Flags.js_source_phase_imports) return;

        // -- %AbstractModuleSource%
        // https://tc39.es/ecma262/#sec-%abstractmodulesource%
        // https://tc39.es/proposal-source-phase-imports/#sec-%abstractmodulesource%
        JSFunction abstractModuleSourceFun = Bootstrapper.CreateFunction(_isolate, "AbstractModuleSource",
            InstanceType.JSObjectType, JSObject.kHeaderSize, 0, JSValue.TheHole, Builtin.IllegalInvocationThrower, 0, false);
        _nativeContext.AbstractModuleSourceFunction = abstractModuleSourceFun;

        // Setup %AbstractModuleSourcePrototype%.
        var abstractModuleSourcePrototype = (JSObject)abstractModuleSourceFun.InstancePrototype;
        _nativeContext.AbstractModuleSourcePrototype = abstractModuleSourcePrototype;

        Bootstrapper.SimpleInstallGetter(_isolate, abstractModuleSourcePrototype, ReadOnlyRoots.to_string_tag_symbol,
            Builtin.AbstractModuleSourceToStringTag, true);
    }
}
