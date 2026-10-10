// Tests of the feedback the Maglev port speculates on and the checks it
// emits for it: deprecated maps (CheckMapsWithMigrationAndDeopt and the IC's
// migration), loads and stores on null/undefined (their IC feedback),
// typed array lengths (the TypedArrayPrototypeLength accessor with its
// prototype chain and detaching dependencies), stores into immutable
// buffers (CheckTypedArrayValid), and HeapObject field representations.
namespace V8Sharp.Tests.Maglev;

public class MaglevFeedbackTest
{
    const string kForced = "--maglev --optimize-on-next-call-optimizes-to-maglev";

    [Fact]
    public void DeprecatedMapIsMigratedAndLearned()
    {
        // Port of mjsunit/compiler/checkmaps-with-migration-and-deopt-poly.
        Assert.Equal("0,8,8", MaglevCompilerTest.Run(kForced, """
            class Vector { constructor(x) { this.x = x; } }
            function magnitude(v) { return v.x; }
            const zero = new Vector(0);
            const anotherOldObject = new Vector(0);
            %PrepareFunctionForOptimization(magnitude);
            magnitude(zero);
            magnitude({a: 0, b: 0, c: 0, x: 0});
            const nonzero = new Vector(0.6);  // deprecates zero's map
            %OptimizeFunctionOnNextCall(magnitude);
            magnitude(zero);
            var r = [%GetOptimizationStatus(magnitude) & 8];
            %OptimizeFunctionOnNextCall(magnitude);
            magnitude(zero);
            r.push(%GetOptimizationStatus(magnitude) & 8);
            magnitude(anotherOldObject);
            r.push(%GetOptimizationStatus(magnitude) & 8);
            r.join();
            """));
    }

    [Fact]
    public void MonomorphicDeprecatedMapIsReplacedByItsUpdatedMap()
    {
        // Port of mjsunit/maglev/checkmaps-with-migration-and-deopt-mono: the
        // check is on the updated map, the old object is migrated and deopts once.
        Assert.Equal("0,8,8", MaglevCompilerTest.Run("--maglev", """
            class Vector { constructor(x) { this.x = x; } }
            function magnitude(v) { return v.x; }
            const zero = new Vector(0);
            const anotherOldObject = new Vector(0);
            %PrepareFunctionForOptimization(magnitude);
            magnitude(zero);
            const nonzero = new Vector(0.6);
            %OptimizeMaglevOnNextCall(magnitude);
            magnitude(zero);
            var r = [%GetOptimizationStatus(magnitude) & 8];
            %OptimizeMaglevOnNextCall(magnitude);
            magnitude(zero);
            r.push(%GetOptimizationStatus(magnitude) & 8);
            magnitude(anotherOldObject);
            r.push(%GetOptimizationStatus(magnitude) & 8);
            r.join();
            """));
    }

    [Fact]
    public void PolymorphicAccessMigratesDeprecatedObjects()
    {
        // Port of mjsunit/maglev/no-deopt-deprecated-map (MigrateMapIfNeeded).
        Assert.Equal("8,8,2", MaglevCompilerTest.Run("--maglev", """
            let o1 = {y: 0, a: 1};
            let o2_1 = {y: 0, a: 1};
            let o2_2 = {y: 0, a: 1};
            let o3 = {x: 0, y: 0, a: 1};
            o2_1.a = 3.1415;
            o2_2.a = 4.12;
            function foo(o) { o.y = 2; }
            %PrepareFunctionForOptimization(foo);
            foo(o2_1); foo(o2_2); foo(o3);
            %OptimizeMaglevOnNextCall(foo);
            foo(o2_1);
            var r = [%GetOptimizationStatus(foo) & 8];
            foo(o1);
            r.push(%GetOptimizationStatus(foo) & 8, o1.y);
            r.join();
            """));
    }

    [Fact]
    public void StrictEqualsWithReceiverOrNullOrUndefinedFeedback()
    {
        // mjsunit/maglev/strict-equals-receiver-or-null-or-undefined.
        Assert.Equal("true,false,false,true,false,8,false,0", MaglevCompilerTest.Run("--maglev", """
            function strictEquals(a, b) { return a === b; }
            %PrepareFunctionForOptimization(strictEquals);
            strictEquals({}, null);
            %OptimizeMaglevOnNextCall(strictEquals);
            const o = {};
            var r = [strictEquals(null, null), strictEquals(null, undefined), strictEquals(o, {}), strictEquals(o, o),
                     strictEquals(undefined, o), %GetOptimizationStatus(strictEquals) & 8];
            r.push(strictEquals({}, ""), %GetOptimizationStatus(strictEquals) & 8);
            r.join();
            """));
    }

    [Fact]
    public void StringWrapperAddDependsOnTheProtector()
    {
        // mjsunit/maglev/string-wrapper-add-2 and regress-410867001.
        Assert.Equal("firstconstant,8,0,firstvalue", MaglevCompilerTest.Run("--maglev", """
            var stringWrapper = new String('constant');
            function add(a) { return a + stringWrapper; }
            %PrepareFunctionForOptimization(add);
            add('first');
            %OptimizeMaglevOnNextCall(add);
            var r = [add('first'), %GetOptimizationStatus(add) & 8];
            stringWrapper.valueOf = () => 'value';
            r.push(%GetOptimizationStatus(add) & 8, add('first'));
            r.join();
            """));
    }

    [Fact]
    public void AccessesOnNullAndUndefinedHaveFeedback()
    {
        // Port of the null/undefined cases of mjsunit/compiler/misc-ensure-no-deopt:
        // the ICs record the oddball's map (and a failing keyed store goes
        // megamorphic), so the optimized code throws without deoptimizing.
        Assert.Equal("8,8,8,8,8", MaglevCompilerTest.Run(kForced, """
            function t(fn) {
              %PrepareFunctionForOptimization(fn);
              for (let i = 0; i < 5; i++) try { fn(); } catch (e) {}
              %OptimizeFunctionOnNextCall(fn);
              try { fn(); } catch (e) {}
              return %GetOptimizationStatus(fn) & 8;
            }
            [t(() => (undefined).val), t(() => (undefined).val = 1), t(() => (null)[0] = 1),
             t(() => (undefined)[NaN] = 1), t(() => (undefined)["k"])].join();
            """));
    }

    [Fact]
    public void TypedArrayLengthIsLoadedAndDependsOnThePrototypeChain()
    {
        // mjsunit/maglev/typed-array-length-custom-1b and -detached-1.
        Assert.Equal("100,8,0,3,8,0,0", MaglevCompilerTest.Run(kForced, """
            function foo(size) { let a = new Uint8Array(size); return a.length; }
            %PrepareFunctionForOptimization(foo);
            foo(100);
            %OptimizeFunctionOnNextCall(foo);
            var r = [foo(100), %GetOptimizationStatus(foo) & 8];
            Object.defineProperty(Uint8Array.prototype, 'length', {get: () => 3});
            r.push(%GetOptimizationStatus(foo) & 8, foo(100));
            const ta = new Uint16Array(128);
            function bar(a) { return a.length; }
            %PrepareFunctionForOptimization(bar);
            bar(ta);
            %OptimizeFunctionOnNextCall(bar);
            bar(ta);
            r.push(%GetOptimizationStatus(bar) & 8);
            ta.buffer.transfer();
            r.push(%GetOptimizationStatus(bar) & 8, bar(ta));
            r.join();
            """));
    }

    [Fact]
    public void LargeTypedArrayLengthsWithTheMockAllocator()
    {
        // mjsunit/maglev/typed-array-length-bitwise: --mock-arraybuffer-allocator
        // (d8's MockArrayBufferAllocator) gives huge buffers one page of memory;
        // the length is a full number, truncated by the bitwise or.
        Assert.Equal("8589934592,255,8", MaglevCompilerTest.Run(kForced + " --mock-arraybuffer-allocator", """
            function len(size) { return new Uint8Array(size).length; }
            function foo(size) { let a = new Uint8Array(size); return a.length | 0xff; }
            %PrepareFunctionForOptimization(foo);
            foo(100);
            %OptimizeFunctionOnNextCall(foo);
            foo(100);
            [len(8589934592), foo(8589934592), %GetOptimizationStatus(foo) & 8].join();
            """));
    }

    [Fact]
    public void StoresIntoImmutableBuffersAreNotDone()
    {
        // mjsunit/regress/immutable-ab-regress.
        Assert.Equal("30,30", MaglevCompilerTest.Run(kForced + " --js-immutable-arraybuffer", """
            function store(arr, val) { arr[0] = val; }
            let ab = new ArrayBuffer(16);
            let ta = new Uint8Array(ab);
            %PrepareFunctionForOptimization(store);
            store(ta, 10); store(ta, 20);
            %OptimizeFunctionOnNextCall(store);
            store(ta, 30);
            let ta_imm = new Uint8Array(ab.sliceToImmutable());
            store(ta_imm, 0xAA);
            [ta[0], ta_imm[0]].join();
            """));
    }

    [Fact]
    public void TypedArrayAccessesDependOnNoDetachedBuffers()
    {
        // mjsunit/maglev/constant-typed-array-load-deopt-detach.
        Assert.Equal("10,8,0,", MaglevCompilerTest.Run("--maglev", """
            const ab = new ArrayBuffer(100);
            var ta = new Uint16Array(ab);
            for (let i = 0; i < ta.length; ++i) ta[i] = i;
            function foo(i) { return ta[i]; }
            %PrepareFunctionForOptimization(foo);
            foo(3); foo(10);
            %OptimizeMaglevOnNextCall(foo);
            var r = [foo(10), %GetOptimizationStatus(foo) & 8];
            %ArrayBufferDetach(ab);
            r.push(%GetOptimizationStatus(foo) & 8, foo(10));
            r.join();
            """));
    }

    [Fact]
    public void UndefinedFitsHeapObjectFields()
    {
        // mjsunit/turbolev/new-obj: a literal field that only held undefined
        // (HeapObject representation) takes undefined without deoptimizing.
        Assert.Equal("8", MaglevCompilerTest.Run(kForced, """
            function create(c) { let x = { "a": 42, c }; return x; }
            %PrepareFunctionForOptimization(create);
            create(); create();
            %OptimizeFunctionOnNextCall(create);
            create();
            String(%GetOptimizationStatus(create) & 8);
            """));
    }
}
