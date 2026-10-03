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
