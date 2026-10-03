// Port of Genesis::InitializeGlobal_queueMicrotask (src/init/bootstrapper.cc).
namespace V8Sharp.Init;

sealed partial class Genesis
{
    void InitializeGlobal_queueMicrotask()
    {
        if (!_isolate.Flags.enable_queue_microtask) return;

        // Install Global.queueMicrotask
        JSGlobalObject globalObject = _nativeContext.GlobalObject;
        Bootstrapper.InstallFunctionWithBuiltinId(_isolate, globalObject, "queueMicrotask", Builtin.GlobalQueueMicrotask, 1, true);
    }
}
