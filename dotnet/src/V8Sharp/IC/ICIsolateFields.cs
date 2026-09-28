// Per-isolate and per-native-context IC state that V8 keeps on the isolate
// (the stub caches) and in the heap (the primitive maps of the root table).
namespace V8Sharp
{
    public sealed partial class Isolate
    {
        /// <summary>The stub caches (Isolate::load_stub_cache and friends).</summary>
        public V8Sharp.IC.ICIsolateState? ICState;
    }
}

namespace V8Sharp.Objects
{
    public sealed partial class NativeContext
    {
        /// <summary>The primitive stand-in maps IC feedback keys on (see ICMaps).</summary>
        public V8Sharp.IC.ICMaps.PrimitiveMaps? ICPrimitiveMaps;
    }
}
