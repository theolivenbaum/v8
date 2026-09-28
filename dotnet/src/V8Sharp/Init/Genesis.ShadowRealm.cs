// Port of Genesis::InitializeGlobal_harmony_shadow_realm (src/init/bootstrapper.cc):
// the ShadowRealm constructor and prototype. The WrappedFunction map and the
// ShadowRealmImportValueRejected function of that installer are created in
// InitializeGlobal (Genesis.InitializeGlobal.cs).
namespace V8Sharp.Init;

sealed partial class Genesis
{
    /// <summary>Genesis::InitializeGlobal_harmony_shadow_realm.</summary>
    void InitializeGlobal_harmony_shadow_realm()
    {
        if (!_isolate.Flags.harmony_shadow_realm) return;
        Isolate isolate = _isolate;
        // -- S h a d o w R e a l m
        // https://tc39.es/ecma262/#sec-shadowrealm-objects
        JSGlobalObject global = _nativeContext.GlobalObject;
        JSFunction shadowRealmFun = Bootstrapper.InstallFunction(isolate, global, "ShadowRealm", InstanceType.JSShadowRealmType,
            JSObject.GetHeaderSize(InstanceType.JSShadowRealmType), 0, JSValue.TheHole, Builtin.ShadowRealmConstructor, 0, false);

        // Setup %ShadowRealmPrototype%.
        var prototype = (JSObject)shadowRealmFun.InstancePrototype;

        Bootstrapper.InstallToStringTag(isolate, prototype, ReadOnlyRoots.ShadowRealm_string);

        Bootstrapper.SimpleInstallFunction(isolate, prototype, "evaluate", Builtin.ShadowRealmPrototypeEvaluate, 1, true);
        Bootstrapper.SimpleInstallFunction(isolate, prototype, "importValue", Builtin.ShadowRealmPrototypeImportValue, 2, true);
    }
}
