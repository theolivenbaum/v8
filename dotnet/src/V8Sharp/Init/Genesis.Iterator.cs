// Port of the shipped iterator feature installers of src/init/bootstrapper.cc:
// Genesis::InitializeGlobal_js_iterator_join, _js_iterator_sequencing,
// _js_joint_iteration and _js_iterator_includes, run from
// InitializeExperimentalGlobal.
namespace V8Sharp.Init;

sealed partial class Genesis
{
    void InitializeGlobal_js_iterator_join()
    {
        if (!_isolate.Flags.js_iterator_join) return;
        JSObject iteratorPrototype = _nativeContext.InitialIteratorPrototype;
        Bootstrapper.SimpleInstallFunction(_isolate, iteratorPrototype, "join", Builtin.IteratorPrototypeJoin, 1, true);
    }

    void InitializeGlobal_js_iterator_sequencing()
    {
        if (!_isolate.Flags.js_iterator_sequencing) return;
        JSObject iteratorHelperPrototype = _nativeContext.InitialIteratorHelperPrototype;
        JSFunction iteratorFunction = _nativeContext.InitialIteratorFunction;
        Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSIteratorConcatHelperType,
            JSObject.GetHeaderSize(InstanceType.JSIteratorConcatHelperType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
        Map.SetPrototype(_isolate, map, iteratorHelperPrototype);
        map.SetConstructor(iteratorFunction);
        _nativeContext.IteratorConcatHelperMap = map;
        Bootstrapper.SimpleInstallFunction(_isolate, iteratorFunction, "concat", Builtin.IteratorConcat, 0, false);
    }

    void InitializeGlobal_js_joint_iteration()
    {
        if (!_isolate.Flags.js_joint_iteration) return;
        JSObject iteratorHelperPrototype = _nativeContext.InitialIteratorHelperPrototype;
        JSFunction iteratorFunction = _nativeContext.InitialIteratorFunction;
        {
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSIteratorZipHelperType,
                JSObject.GetHeaderSize(InstanceType.JSIteratorZipHelperType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
            Map.SetPrototype(_isolate, map, iteratorHelperPrototype);
            map.SetConstructor(iteratorFunction);
            _nativeContext.IteratorZipHelperMap = map;
            Bootstrapper.SimpleInstallFunction(_isolate, iteratorFunction, "zip", Builtin.IteratorZip, 1, false);
        }
        {
            Map map = _factory.NewContextfulMapForCurrentContext(InstanceType.JSIteratorZipKeyedHelperType,
                JSObject.GetHeaderSize(InstanceType.JSIteratorZipKeyedHelperType), ElementsKind.TERMINAL_FAST_ELEMENTS_KIND, 0);
            Map.SetPrototype(_isolate, map, iteratorHelperPrototype);
            map.SetConstructor(iteratorFunction);
            _nativeContext.IteratorZipKeyedHelperMap = map;
            Bootstrapper.SimpleInstallFunction(_isolate, iteratorFunction, "zipKeyed", Builtin.IteratorZipKeyed, 1, false);
        }
    }

    void InitializeGlobal_js_iterator_includes()
    {
        if (!_isolate.Flags.js_iterator_includes) return;
        JSObject iteratorPrototype = _nativeContext.InitialIteratorPrototype;
        Bootstrapper.SimpleInstallFunction(_isolate, iteratorPrototype, "includes", Builtin.IteratorPrototypeIncludes, 1, false);
    }
}
