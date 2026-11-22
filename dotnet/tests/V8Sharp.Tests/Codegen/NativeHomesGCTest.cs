// The mechanism the native Maglev backend's GC-visible homes rely on: object
// references written raw (as native code writes them, no write barrier) into
// a JSValue buffer local of a managed frame are GC roots, kept alive and
// updated when a compacting GC moves their objects.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using V8Sharp.Codegen;
using V8Sharp.Objects;

namespace V8Sharp.Tests.Codegen;

public class NativeHomesGCTest
{
    [InlineArray(16)]
    struct Homes
    {
        JSValue _slot;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static nint WriteRaw(nint slot, int k)
    {
        // A young object only the raw slot will reference.
        var s = new SeqString("native-home-" + k);
        nint address = Unsafe.As<SeqString, nint>(ref s);
        Marshal.WriteIntPtr(slot, address);
        Marshal.WriteInt64(slot + 8, 0);
        return address;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Churn()
    {
        var keep = new object[64];
        for (int i = 0; i < 200_000; i++) keep[i & 63] = new byte[32];
    }

    [Fact]
    public void RawReferencesInAStackBufferAreRootsAndAreUpdated()
    {
        int moved = 0;
        for (int round = 0; round < 20; round++)
        {
            Homes homes = default;
            nint at = NativeCode.AddressOfLocal(ref homes[3]);
            nint before = WriteRaw(at, round);
            Churn();
            GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: true);
            Churn();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            JSValue v = homes[3];
            Assert.Equal("native-home-" + round, ((SeqString)v.Object).Value);
            if (Marshal.ReadIntPtr(at) != before) moved++;
        }
        // (The GC moved at least some of them: the slots were updated, not just kept.)
        Assert.True(moved > 0, "no object moved");
    }
}
