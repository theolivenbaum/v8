// Megamorphic keyed loads and stores (AccessorAssembler::KeyedLoadICGeneric,
// KeyedStoreGenericAssembler::KeyedStoreGeneric): they look the key up
// themselves or go to the runtime, and do not miss. Expected values are the
// oracle's (V8 14.7).
using V8Sharp.Codegen;

namespace V8Sharp.Tests.IC;

public class KeyedGenericTest : TestWithContext
{
    public KeyedGenericTest()
    {
        i_isolate.Flags.lazy_feedback_allocation = false;
    }

    string RunString(string source) => ObjectOps.ToString(i_isolate, Compiler.CompileAndRun(i_isolate, source)).ToString();

    // Twenty object shapes make the two sites megamorphic first.
    const string Prelude = """
        function ld(o, k) { return o[k]; }
        function st(o, k, v) { o[k] = v; }
        var objs = [];
        for (var i = 0; i < 20; i++) { var o = {}; o['p' + i] = i; o.x = 1; objs.push(o); }
        for (var i = 0; i < 20; i++) { ld(objs[i], 'x'); st(objs[i], 'x', i); }

        """;

    [Fact]
    public void LoadsOwnPrototypeDictionaryAndAbsent()
    {
        Assert.Equal("5,undefined,101,undefined,3,2,3,7,getter5,true,3,b,yes,yes,7,9,proxy:zz", RunString(Prelude + """
            var d = {}; for (var i = 0; i < 200; i++) d['k' + i] = i; delete d.k3;
            var a = [1, 2, 3];
            function P() {} P.prototype.m = function () { return 7; };
            Object.defineProperty(P.prototype, 'g', { get: function () { return 'getter' + this.x; } });
            var p = new P(); p.x = 5;
            var proto = { inherited: 'yes' }; var child = Object.create(proto);
            var dictProto = {}; for (var q = 0; q < 100; q++) dictProto['z' + q] = q; var c2 = Object.create(dictProto);
            var proxy = new Proxy({}, { get: function (t, k) { return 'proxy:' + String(k); } });
            [ld(d, 'k5'), String(ld(d, 'k3')), ld(d, 'k' + 101), String(ld(d, 'nope' + 1)), ld(a, 'length'), ld(a, 1), ld(a, '2'),
             ld(p, 'm')(), ld(p, 'g'), ld(p, 'toString') === Object.prototype.toString, ld('abc', 'length'), ld('abc', 1),
             ld(child, 'inherited'), ld(child, 'in' + 'herited'), ld(c2, 'z7'), ld(c2, 'z' + 9), ld(proxy, 'z' + 'z')].join();
            """));
    }

    [Fact]
    public void StoresOverwriteAddAndRespectAttributes()
    {
        Assert.Equal("seven,7,3,1,1,3,TypeError,1,2,str,set", RunString(Prelude + """
            var d = {}; for (var i = 0; i < 200; i++) d['k' + i] = i;
            var a = [1, 2, 3];
            var frozen = Object.freeze({ f: 1 });
            var ro = {}; Object.defineProperty(ro, 'r', { value: 3, writable: false });
            st(d, 'k7', 'sev' + 'en'); st(d, 'k' + 300, 7); st(a, 'length', 3); st(a, 'q', 1);
            st(frozen, 'f', 9); st(ro, 'r', 9);
            var out = [d.k7, d.k300, a.length, a.q, frozen.f, ro.r];
            try { (function () { 'use strict'; function sst(o, k, v) { o[k] = v; } for (var j = 0; j < 30; j++) sst(objs[j % 20], 'p' + j, j); sst(ro, 'r', 1); })(); }
            catch (e) { out.push(e.constructor.name); }
            var sym = Symbol('s'); var withSym = {}; st(withSym, sym, 1); out.push(ld(withSym, sym));
            var dbl = { v: 1.5 }; st(dbl, 'v', 2); out.push(dbl.v); st(dbl, 'v', 'str'); out.push(dbl.v);
            var setter = {}; Object.defineProperty(setter, 's', { set: function (v) { this.seen = v; } }); st(setter, 's', 'set'); out.push(setter.seen);
            out.join();
            """));
    }

    [Fact]
    public void MegamorphicKeyedAccessesDoNotMiss()
    {
        RunString(Prelude + """
            var tables = [];
            for (var t = 0; t < 8; t++) { var h = {}; for (var i = 0; i < 50; i++) h['n' + t + '_' + i] = i; tables.push(h); }
            function warm() { for (var t = 0; t < 8; t++) for (var i = 0; i < 50; i++) { st(tables[t], 'n' + t + '_' + i, ld(tables[t], 'n' + t + '_' + i) + 1); ld(tables[t], 'absent' + i); } }
            warm();
            """);
        var stats = V8Sharp.IC.ICIsolateState.Get(i_isolate);
        long loads = stats.KeyedLoadMisses, stores = stats.KeyedStoreMisses;
        RunString("warm()");
        Assert.Equal(loads, stats.KeyedLoadMisses);
        Assert.Equal(stores, stats.KeyedStoreMisses);
        Assert.Equal("9", RunString("tables[3]['n3_7']"));
    }
}
