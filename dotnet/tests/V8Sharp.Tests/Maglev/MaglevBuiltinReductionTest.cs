// Tests of the builtin call reductions of the Maglev port
// (MaglevGraphBuilder::TryReduceBuiltin and the reductions it dispatches
// to): the reduced calls give the interpreter's results, and the
// optimized code keeps (or deopts from) its speculation as V8's does.
namespace V8Sharp.Tests.Maglev;

public class MaglevBuiltinReductionTest
{
    public static TheoryData<string> Snippets => new()
    {
        // Array.prototype.push / pop on every fast elements kind, several
        // arguments, growing, empty arrays, holes, kind changes (deopts).
        """
        (function() {
          function push1(a, x) { return a.push(x); }
          function push3(a, x, y, z) { return a.push(x, y, z); }
          function pop(a) { return a.pop(); }
          var out = [];
          for (var k = 0; k < 30; k++) {
            var smi = [1, 2], dbl = [1.5], obj = [{}], holey = [1, , 3], empty = [];
            out.push(push1(smi, k), push1(dbl, k + 0.5), push1(obj, 'x' + k), push1(holey, k), push1(empty, k));
            out.push(push3(smi, 1, 2, 3), push3(dbl, 0.25, 0.5, k), push3(empty, k, k, k));
            out.push(pop(smi), pop(dbl), pop(obj), pop(holey), pop(holey), pop(holey), pop(holey), pop([]), pop([, ]));
            out.push(smi.join(), dbl.join(), holey.length, empty.join());
          }
          var a = [1, 2];
          out.push(push1(a, 'str'), push1([1.5], {}), push3([1], 1, 'a', 2.5), a.join());
          var frozen = Object.freeze([1, 2]);
          try { push1(frozen, 3); } catch (e) { out.push(e.constructor.name); }
          return out.join();
        })()
        """,
    };

    [Theory]
    [MemberData(nameof(Snippets))]
    public void SameResultWhenOptimized(string source) => MaglevCompilerTest.AssertSameWhenOptimized(source);

    [Fact]
    public void FunctionsWithTooManyLiveValuesAreNotOptimized()
    {
        // maglev-compiler.cc kMaxStackSlots (mjsunit/maglev/regress-536945254):
        // 600 int32 values live at once need more than 4 KB of stack slots.
        Assert.Equal("true,0,true,8", MaglevCompilerTest.Run("--maglev", """
            function build(count) {
              let defs = '', sum = '0';
              for (let i = 0; i < count; i++) { defs += `let v${i} = (a ^ ${i}) | 0;`; sum += ` + v${i}`; }
              return Function(`return function wide(a) { a |= 0; ${defs} return ${sum}; };`)();
            }
            var r = [];
            for (const count of [600, 100]) {
              const wide = build(count);
              %PrepareFunctionForOptimization(wide);
              const expected = wide(5);
              %OptimizeMaglevOnNextCall(wide);
              r.push(wide(5) === expected, %GetOptimizationStatus(wide) & 8);
            }
            r.join();
            """));
    }

    [Fact]
    public void ArrayPushIsReducedAndDeoptsOnAnImpossibleType()
    {
        // mjsunit/maglev/array-push-with-smi-object.
        Assert.Equal("11,34,8,0,2", MaglevCompilerTest.Run("--maglev", """
            function foo(a, thing) { a.push(thing); }
            %PrepareFunctionForOptimization(foo);
            foo([{}, {}, {}], {});
            let a1 = [1, 2, 3];
            foo(a1, 11);
            %OptimizeMaglevOnNextCall(foo);
            foo(a1, 13);
            let objs = [{}, {}, {}];
            foo(objs, 34);
            let smis = [1, 2, 3];
            foo(smis, 11);
            var r = [smis[3], objs[3], %GetOptimizationStatus(foo) & 8];
            foo(smis, {a: 2});
            r.push(%GetOptimizationStatus(foo) & 8, smis[4].a);
            r.join();
            """));
    }
}
