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
    public void ElementLoadsAndStores()
    {
        Assert.Equal("2|1.5|a|fromProto||9|0|5|fromProto|||10,2.5,3,4|str,2.5|1,2,3|70,8,9|1,2|0,0,44,0|1|1|0,own,2|7,8,9",
            RunString(Prelude + """
            var smi = [1, 2, 3], dbl = [1.5, 2.5], obj = ['a', {}], holey = [1, , 3], cow = [7, 8, 9];
            var frozen = Object.freeze([1, 2]); var ta = new Int8Array(4); var sparse = []; sparse[1000] = 5;
            var protoArr = [0, , 2]; Array.prototype[1] = 'fromProto';
            var out = [];
            out.push(ld(smi, 1), ld(dbl, 0), ld(obj, 0), ld(holey, 1), ld(holey, 5), ld(cow, 2), ld(ta, 1), ld(sparse, 1000), ld(protoArr, 1), ld(smi, -1), ld(smi, 1.5));
            st(smi, 0, 10); st(smi, 1, 2.5); st(smi, 3, 4); st(dbl, 0, 'str'); st(holey, 1, 2); st(cow, 0, 70); st(frozen, 0, 9); st(ta, 2, 300);
            st(sparse, 5, 1); st(obj, 1, 1); st(protoArr, 1, 'own');
            out.push(smi.join(), dbl.join(), holey.join(), cow.join(), frozen.join(), ta.join(), sparse[5], obj[1], protoArr.join(), [7, 8, 9].join());
            delete Array.prototype[1];
            out.join('|');
            """));
    }

    [Fact]
    public void ElementStoresIntoHolesAndAppends()
    {
        Assert.Equal("setter v|p|0,1,2,3,4,0|40|39|0.5,1.5,x|2|undefined|5|6|9,9,9|0|b|z|3|undefined|2|0,,,own,4|0,1,2,app",
            RunString(Prelude + """
            var out = [];
            var down = []; down[5] = 0; for (var i = 4; i >= 0; i--) st(down, i, i);
            var app = [1]; for (var i = 1; i < 40; i++) st(app, i, i);
            var dapp = [0.5]; st(dapp, 1, 1.5); st(dapp, 2, 'x');
            var ro = [1, 2]; Object.defineProperty(ro, 'length', { writable: false }); st(ro, 2, 3);
            function C() {} C.prototype = [9, 9, 9]; var c = new C(); c[0] = 1; c.length = 3; st(c, 1, 5); st(c, 2, 6);
            var withProtoElem = [, , ]; Object.setPrototypeOf(withProtoElem, { set 1(v) { out.push('setter ' + v); } }); st(withProtoElem, 1, 'v'); st(withProtoElem, 0, 'w');
            var holeyObj = { 0: 'a', 2: 'c' }; st(holeyObj, 1, 'b'); st(holeyObj, 9, 'z');
            var sealedArr = Object.seal([1, , 3]); st(sealedArr, 1, 2); st(sealedArr, 3, 4);
            var nonext = Object.preventExtensions([1, 2]); st(nonext, 2, 3);
            var protoTarget = [1, , 3]; var child = Object.create(protoTarget); st(protoTarget, 1, 'p'); out.push(child[1]);
            Array.prototype[3] = 'AP'; var apHole = [0, , , , 4]; st(apHole, 3, 'own'); var apApp = [0, 1, 2]; st(apApp, 3, 'app'); delete Array.prototype[3];
            out.push(down.join(), app.length, app[39], dapp.join(), ro.length, String(ro[2]), c[1], c[2], C.prototype.join(),
              Object.keys(withProtoElem).join(), holeyObj[1], holeyObj[9], sealedArr.length, String(sealedArr[3]), nonext.length, apHole.join(), apApp.join());
            out.join('|');
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
