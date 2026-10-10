// Differential coverage of the ported BytecodeGenerator against real V8 (the
// oracle, V8 14.7) on mjsunit-style snippets, beyond the golden files.
//
// A child process runs every snippet through the oracle with --print-bytecode
// --no-lazy and prints what V8 generated for every function; this test compiles
// the same snippets with UnoptimizedCompiler and compares function by function.
//
// The oracle is older than this tree, so an exact match is not expected: the
// test classifies each function (see Classify) and writes the classes to
// dotnet/artifacts/oracle-bytecode-report.txt (details in
// oracle-bytecode-differences.txt). It asserts that every function matches up
// to the version differences Normalized removes, except the ones listed in
// s_knownDifferences with their reason.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using V8Sharp.Ast;
using V8Sharp.Interpreter;
using V8Sharp.Oracle;

namespace V8Sharp.Tests.Interpreter;

public partial class OracleBytecodeGeneratorTest
{
    const string kChildEnv = "V8SHARP_ORACLE_BYTECODE_GENERATOR";
    const string kSnippetMarker = "@@@SNIPPET ";
    const string kEndMarker = "@@@END@@@";

    // mjsunit-style snippets: each defines functions with distinct names; every
    // function is compared (the top-level code under the name "").
    public static readonly string[] Snippets =
    [
        "function add(a, b) { return a + b; } add(1, 2);",
        "function loop(n) { var s = 0; for (var i = 0; i < n; i++) s += i; return s; }",
        "function whileLoop(n) { let s = 0; while (n-- > 0) { if (n % 3 == 0) continue; s += n; } return s; }",
        "function doWhile(n) { var i = 0; do { i++; } while (i < n); return i; }",
        "function forIn(o) { var r = []; for (var k in o) r.push(k); return r; }",
        "function forOf(a) { let s = 0; for (const x of a) s += x; return s; }",
        "function forOfDestructure(a) { let s = 0; for (const [k, v] of a) s += k + v; return s; }",
        "function closures() { var x = 1; function inner() { return x++; } return inner; }",
        "function letConst() { let a = 1; const b = 2; { let a = 3; b + a; } return a + b; }",
        "function tdz() { function g() { return x; } let x = g; return x; }",
        "function objLit(a) { return { a, b: 1, c: [1, 2, a], get d() { return 1; }, set d(v) {}, [a]: 2, ...a }; }",
        "function arrLit(a) { return [1, , 3, ...a, 'x', 1.5, {}, []]; }",
        "function strTemplate(a, b) { return `x${a}y${b}z`; }",
        "function taggedTemplate(f) { return f`a${1}b`; }",
        "function cls() { class A { constructor(x) { this.x = x; } m() { return this.x; } static s() { return 1; } get g() { return 2; } } return new A(1).m(); }",
        "function derived() { class B { constructor() { this.b = 1; } } class D extends B { constructor() { super(); this.d = super.constructor; } m() { return super.toString(); } } return new D(); }",
        "function privateFields() { class P { #x = 1; static #s = 2; #m() { return this.#x; } get() { return this.#m() + P.#s; } has(o) { return #x in o; } } return new P().get(); }",
        "function fields() { class F { a = 1; b; static c = 3; ['d'] = 4; static { this.e = 5; } } return new F(); }",
        "function destructuring(o) { var { a, b: { c }, ...rest } = o; var [x, , y = 3, ...z] = o.arr; return a + c + x + y; }",
        "function defaults(a = 1, { b } = {}, ...rest) { return a + b + rest.length; }",
        "function args() { return arguments[0] + arguments.length; }",
        "function strictArgs(a) { 'use strict'; return arguments[0] + a; }",
        "function sloppyMapped(a) { arguments[0] = 2; return a; }",
        "function tryCatch(f) { try { return f(); } catch (e) { return e; } }",
        "function tryFinally(f) { var r; try { r = f(); } finally { r = 1; } return r; }",
        "function tryCatchFinally(f) { try { f(); } catch { return 1; } finally { f(); } return 2; }",
        "function throwing(x) { if (x) throw new Error('x'); return 0; }",
        "function switchStmt(x) { switch (x) { case 1: return 'a'; case 2: case 3: return 'b'; case 'c': break; default: return 'd'; } return 'e'; }",
        "function switchDense(x) { switch (x) { case 0: return 0; case 1: return 1; case 2: return 2; case 3: return 3; case 4: return 4; case 5: return 5; } }",
        "function logical(a, b, c) { return (a && b || c) ?? a; }",
        "function nullish(a) { a ??= 1; a ||= 2; a &&= 3; return a?.b?.[1]?.(2); }",
        "function conditional(a) { return a ? 1 : a > 2 ? 2 : 3; }",
        "function typeofs(a) { return typeof a === 'number' || typeof x === 'undefined' || typeof a == 'function'; }",
        "function compare(a, b) { return [a < b, a <= b, a > b, a >= b, a == b, a != b, a === b, a !== b, a instanceof b, a in b]; }",
        "function unary(a) { return [-a, +a, !a, ~a, void a, a++, --a, delete a.b, typeof a]; }",
        "function binary(a, b) { return [a - b, a * b, a / b, a % b, a ** b, a | b, a & b, a ^ b, a << b, a >> b, a >>> b, a + 1, a * 2, a | 0]; }",
        "function calls(o, f) { f(); f(1, 2); o.m(); o.m(1); o[f](2); new f(1); f(...o); o.m(...o, 1); return f.call(o, 1); }",
        "function globals() { gx = 1; var y = gx + gy; return Math.max(y, 1); }",
        "function* gen(a) { yield 1; const x = yield a; yield* [1, 2]; return x; }",
        "async function asyncFn(p) { const x = await p; try { await x; } catch (e) {} return x; }",
        "async function* asyncGen(p) { for await (const x of p) yield x; }",
        "function arrows(a) { const f = (x) => x + a; const g = () => this; const h = async () => await a; return [f, g, h]; }",
        "function regexps(s) { return /a+b/g.test(s) && s.replace(/x/, 'y'); }",
        "function bigints(a) { return a + 10n * 20000000000000000000000n; }",
        "function numbers() { return [0, -0, 1.5, 0x7fffffff, 2147483648, 1e21, NaN, Infinity, -1]; }",
        "function withStmt(o) { with (o) { return x + y; } }",
        "function evalFn(s) { var x = 1; return eval(s); }",
        "function nested() { function a() { function b() { return c; } var c = 1; return b; } return a()(); }",
        "function labeled(n) { outer: for (let i = 0; i < n; i++) { for (let j = 0; j < n; j++) { if (j == 2) continue outer; if (i == 3) break outer; } } return n; }",
        "function letInLoop(n) { const fs = []; for (let i = 0; i < n; i++) fs.push(() => i); return fs; }",
        "function spreadCall(f, a) { return f(1, ...a, 2); }",
        "function superProp() { const o = { __proto__: { m() { return 1; } }, m() { return super.m() + super.x; } }; return o.m(); }",
        "function newTarget() { return new.target; }",
        "function objectSpread(a, b) { return { ...a, x: 1, ...b }; }",
        "function lotsOfLocals(a) { var v0=a,v1=v0+1,v2=v1+1,v3=v2+1,v4=v3+1,v5=v4+1,v6=v5+1,v7=v6+1,v8=v7+1,v9=v8+1; return v0+v1+v2+v3+v4+v5+v6+v7+v8+v9; }",
        "function usingDecl() { { using x = { [Symbol.dispose]() {} }; } }",
        "function optionalCall(o) { return o?.a.b(1)?.c; }",
        "function countOps(o, a) { o.x++; o[a]--; ++o.y; return a++; }",
        "function compoundAssign(o, a) { o.x += 1; o[a] *= 2; a -= 3; return a; }",
        "function getterSetter(o) { return Object.defineProperty(o, 'x', { get() { return 1; }, set(v) {} }); }",
        "function intrinsics(a) { return %IsSmi(a); }",
        "var topLevelVar = 1; let topLevelLet = 2; const topLevelConst = 3; function topFn() { return topLevelVar + topLevelLet + topLevelConst; }",
        "function protoAssign() { function C() {} C.prototype.a = 1; C.prototype.b = function () {}; C.prototype.c = 3; return C; }",
        "function iife() { return (function () { return 1; })() + (() => 2)(); }",
        "function deleteOps(o) { 'use strict'; delete o.x; return delete o[1]; }",
        "function inOperator(o) { return 'x' in o && 1 in o; }",
        "function returnInFinally(f) { try { return f(); } finally { f(); } }",
        "function breakInFinally(f) { for (;;) { try { break; } finally { f(); } } return 1; }",
        "function genericLoop(it) { let s = 0; for (let i = it.next(); !i.done; i = it.next()) s += i.value; return s; }",
        "let tlLet = 1;",
        "var tlVar = 1; let tlLet2 = 2;",
        "const tlConst = 1; tlConst + 1;",
    ];

    // The functions that differ beyond Normalized, by snippet index and
    // function name, and why. Each is a difference between V8 14.7 (or the
    // ClearScript build of it) and this tree, not a generator bug.
    static readonly Dictionary<string, string> s_knownDifferences = new(StringComparer.Ordinal)
    {
        // The oracle build has 32-bit Smis (no pointer compression); this
        // tree's Smi range is 31 bits, and the switch jump table's range check
        // loads Smi::kMinValue.
        ["28:switchDense"] = "Smi range of the oracle build",
        // 14.7 compiles `typeof x == "literal"` as TypeOf (with a feedback slot)
        // and a compare; this tree emits TestTypeOf.
        ["32:typeofs"] = "TypeOf with feedback in 14.7",
        // This tree creates the iterator result of `yield` with the
        // _GeneratorYieldResult intrinsic; 14.7 used _CreateIterResultObject.
        ["38:gen"] = "_GeneratorYieldResult intrinsic",
        // This tree's Scope::UpdateNeedsHoleCheck elides the TDZ check of an
        // access from an inner closure that follows the initializer; 14.7
        // checks every cross-closure access.
        ["49:"] = "cross-closure TDZ check elision",
        // 14.7 reserves a register for .result in a script that has lexical
        // declarations and never uses it; this tree does not. Not covered by
        // the golden files.
        ["61:"] = "unused .result register in 14.7",
        ["69:"] = "unused .result register in 14.7",
        ["70:"] = "unused .result register in 14.7",
    };

    [Fact]
    public void OracleChild_PrintAllBytecode()
    {
        // Runs only in the child process started by the test below.
        if (Environment.GetEnvironmentVariable(kChildEnv) != "1") return;
        ReferenceV8.EnsureFlags("--allow-natives-syntax --no-lazy --no-enable-lazy-source-positions " +
                                "--print-bytecode --print-bytecode-filter=*");
        using var v8 = new ReferenceV8();
        for (int i = 0; i < Snippets.Length; i++)
        {
            Console.Out.Flush();
            Console.WriteLine(kSnippetMarker + i.ToString(CultureInfo.InvariantCulture));
            Console.Out.Flush();
            v8.Run(Snippets[i]);
            Console.Out.Flush();
        }
        Console.WriteLine(kEndMarker);
        Console.Out.Flush();
    }

    static string? RunChild()
    {
        string assembly = typeof(OracleBytecodeGeneratorTest).Assembly.Location;
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(assembly);
        psi.ArgumentList.Add("-method");
        psi.ArgumentList.Add(typeof(OracleBytecodeGeneratorTest).FullName + "." + nameof(OracleChild_PrintAllBytecode));
        psi.Environment[kChildEnv] = "1";
        using Process? p = Process.Start(psi);
        if (p is null) return null;
        Task<string> stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(300_000))
        {
            p.Kill();
            return null;
        }
        _ = stderr.Result;
        return stdout;
    }

    sealed class Function
    {
        public required string Name;
        public string Header = "";
        public readonly List<(string Position, string Text)> Lines = [];
        public string Tables = "";
    }

    // "[generated bytecode for function: add (0x0b9d0437c801 <SharedFunctionInfo add>)]"
    [GeneratedRegex(@"^\[generated bytecode for function: (?<name>.*?) \(0x[0-9a-f]+ <SharedFunctionInfo")]
    private static partial Regex FunctionHeaderRegex();

    // "   37 E> 0x3b90ddaf1335 @   21 : 6c f8 f7 f6 02    CallUndefinedReceiver2 r1, r2, r3, FBV[2]"
    [GeneratedRegex(@"^(?:\s*(?<pos>\d+ [SEse])> |\s+)0x[0-9a-f]+ @\s+\d+ : (?:[0-9a-f]{2} )+\s*(?<text>\S.*?)\s*$")]
    private static partial Regex InstructionRegex();

    static List<Function> ParseListing(string listing, string? name)
    {
        var functions = new List<Function>();
        Function? current = name is null ? null : new Function { Name = name };
        if (current is not null) functions.Add(current);
        foreach (string raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match header = FunctionHeaderRegex().Match(line);
            if (header.Success)
            {
                current = new Function { Name = header.Groups["name"].Value };
                functions.Add(current);
                continue;
            }
            if (current is null) continue;
            Match m = InstructionRegex().Match(line);
            if (m.Success)
            {
                current.Lines.Add((m.Groups["pos"].Value, m.Groups["text"].Value));
            }
            else if (line.StartsWith("Parameter count", StringComparison.Ordinal) ||
                     line.StartsWith("Register count", StringComparison.Ordinal))
            {
                current.Header += line + "; ";
            }
            else if (line.StartsWith("Constant pool", StringComparison.Ordinal) ||
                     line.StartsWith("Handler Table", StringComparison.Ordinal))
            {
                current.Tables += line + "; ";
            }
        }
        return functions;
    }

    [GeneratedRegex(@" \(0x[0-9a-f]+ @ \d+\)$")]
    private static partial Regex JumpTargetRegex();
    [GeneratedRegex(@"0x[0-9a-f]{6,} ")]
    private static partial Regex AddressRegex();
    [GeneratedRegex(@"FBV\[\d+\]|EmbeddedFeedback\[[^\]]*\]")]
    private static partial Regex FeedbackRegex();
    [GeneratedRegex(@"^(Jump\w*) \[\d+\]")]
    private static partial Regex JumpOffsetRegex();
    [GeneratedRegex(@"@\d+")]
    private static partial Regex SwitchTargetRegex();

    // Exact text, less the addresses V8 prints.
    static string Exact(string text) => AddressRegex().Replace(JumpTargetRegex().Replace(text, ""), "");

    [GeneratedRegex(@"<ScopeInfo[^>]*>")]
    private static partial Regex ScopeInfoRegex();
    [GeneratedRegex(@"<BigInt [^>]*>")]
    private static partial Regex BigIntRegex();

    // Up to the differences between V8 14.7 and this tree that are not the
    // generator's:
    //  - feedback operands: 14.7 allocates feedback vector slots where this
    //    tree embeds feedback in the bytecode, so slot numbers shift;
    //  - jump offsets, which follow from instruction sizes;
    //  - the TDZ hole bytecodes, renamed in this tree (LdaTdzHole,
    //    ThrowReferenceErrorIfTdzHole, ThrowSuperAlreadyCalledIfNotTdzHole);
    //  - how constants print where the port has no heap object behind them
    //    (ScopeInfo scope types, long BigInts, which V8 truncates).
    static string Normalized(string text)
    {
        string s = FeedbackRegex().Replace(Exact(text), "FB");
        s = JumpOffsetRegex().Replace(s, "$1 [J]");
        s = SwitchTargetRegex().Replace(s, "@J");
        s = s.Replace("LdaTdzHole", "LdaTheHole", StringComparison.Ordinal)
             .Replace("TdzHole", "Hole", StringComparison.Ordinal);
        return BigIntRegex().Replace(ScopeInfoRegex().Replace(s, "<ScopeInfo>"), "<BigInt>");
    }

    static string Mnemonic(string text)
    {
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    public enum Agreement
    {
        Identical,
        MatchUpToVersion,
        SameInstructionsOtherFrameOrPositions,
        SameMnemonics,
        Different,
    }

    static Agreement Classify(Function v8, Function ours, out int first_difference)
    {
        first_difference = -1;
        int n = Math.Min(v8.Lines.Count, ours.Lines.Count);
        for (int i = 0; i < n; i++)
        {
            if (Normalized(v8.Lines[i].Text) != Normalized(ours.Lines[i].Text) ||
                v8.Lines[i].Position != ours.Lines[i].Position)
            {
                first_difference = i;
                break;
            }
        }
        if (first_difference < 0 && v8.Lines.Count != ours.Lines.Count) first_difference = n;

        bool same_count = v8.Lines.Count == ours.Lines.Count;
        bool same_tables = v8.Header == ours.Header && v8.Tables == ours.Tables;
        bool identical = same_count && same_tables, normalized = same_count, instructions = same_count,
             mnemonics = same_count;
        for (int i = 0; same_count && i < n; i++)
        {
            string a = v8.Lines[i].Text, b = ours.Lines[i].Text;
            bool same_position = v8.Lines[i].Position == ours.Lines[i].Position;
            identical &= same_position && Exact(a) == Exact(b);
            bool same_normalized = Normalized(a) == Normalized(b);
            normalized &= same_position && same_normalized;
            instructions &= same_normalized;
            mnemonics &= Mnemonic(a) == Mnemonic(b);
        }
        if (identical) return Agreement.Identical;
        if (normalized && same_tables) return Agreement.MatchUpToVersion;
        if (instructions) return Agreement.SameInstructionsOtherFrameOrPositions;
        if (mnemonics) return Agreement.SameMnemonics;
        return Agreement.Different;
    }

    static List<Function> CompileOurs(string source)
    {
        var options = new BytecodeExpectationsHeaderOptions();
        GoldenBytecodeCompiler.CompiledScript compiled = GoldenBytecodeCompiler.CompileScript(source, options, function_context_cells: true);
        var functions = new List<Function>();
        foreach (KeyValuePair<FunctionLiteral, BytecodeArray> f in compiled.Functions)
        {
            functions.AddRange(ParseListing(f.Value.Disassemble(0x100000), f.Key.GetDebugName()));
        }
        return functions;
    }

    // Pairs an oracle function with the first unpaired one of ours of the same name.
    static Function? Pair(Function v8, List<Function> ours, HashSet<Function> used)
    {
        foreach (Function f in ours)
        {
            if (!used.Contains(f) && f.Name == v8.Name)
            {
                used.Add(f);
                return f;
            }
        }
        return null;
    }

    static string ArtifactsDirectory()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "V8Sharp.slnx"))) return Path.Combine(dir, "artifacts");
            dir = Path.GetDirectoryName(dir);
        }
        return "artifacts";
    }

    [Fact]
    public void BytecodeGenerator_MatchesOracle()
    {
        string? output = RunChild();
        Assert.NotNull(output);
        int end = output!.IndexOf(kEndMarker, StringComparison.Ordinal);
        Assert.True(end > 0, "oracle child did not run:\n" + output);

        var counts = new int[(int)Agreement.Different + 1];
        var report = new StringBuilder();
        var details = new StringBuilder();
        int unpaired = 0, compile_failures = 0, total = 0;
        var unexpected = new StringBuilder();
        for (int i = 0; i < Snippets.Length; i++)
        {
            string marker = kSnippetMarker + i.ToString(CultureInfo.InvariantCulture) + "\n";
            int begin = output.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(begin >= 0, "no oracle output for snippet " + i);
            begin += marker.Length;
            int next = output.IndexOf(kSnippetMarker, begin, StringComparison.Ordinal);
            if (next < 0) next = end;
            List<Function> v8 = ParseListing(output[begin..next], null);

            List<Function> ours;
            try
            {
                ours = CompileOurs(Snippets[i]);
            }
            catch (Exception e)
            {
                compile_failures++;
                details.Append("== snippet ").Append(i).Append(": compile failed: ").Append(e.Message).Append('\n');
                continue;
            }

            var used = new HashSet<Function>(ReferenceEqualityComparer.Instance);
            foreach (Function f in v8)
            {
                total++;
                Function? mine = Pair(f, ours, used);
                if (mine is null)
                {
                    unpaired++;
                    details.Append("== snippet ").Append(i).Append(" function '").Append(f.Name)
                           .Append("': not compiled by V8Sharp\n");
                    continue;
                }
                Agreement agreement = Classify(f, mine, out int first);
                counts[(int)agreement]++;
                report.Append(CultureInfo.InvariantCulture, $"{i,3} {f.Name,-24} {agreement}\n");
                if (agreement is Agreement.Identical) continue;
                string key = i.ToString(CultureInfo.InvariantCulture) + ":" + f.Name;
                if (agreement is not Agreement.MatchUpToVersion && !s_knownDifferences.ContainsKey(key))
                {
                    unexpected.Append(key).Append(' ').Append(agreement).Append('\n');
                }
                details.Append(CultureInfo.InvariantCulture, $"== snippet {i} function '{f.Name}': {agreement}\n");
                if (f.Header != mine.Header)
                    details.Append("  V8:      ").Append(f.Header).Append("\n  V8Sharp: ").Append(mine.Header).Append('\n');
                if (f.Tables != mine.Tables)
                    details.Append("  V8:      ").Append(f.Tables).Append("\n  V8Sharp: ").Append(mine.Tables).Append('\n');
                if (first < 0) continue;
                for (int k = Math.Max(0, first - 2); k < first + 4; k++)
                {
                    string a = k < f.Lines.Count ? f.Lines[k].Position + "> " + f.Lines[k].Text : "";
                    string b = k < mine.Lines.Count ? mine.Lines[k].Position + "> " + mine.Lines[k].Text : "";
                    details.Append(k == first ? " *" : "  ").Append(a.PadRight(60)).Append(" | ").Append(b).Append('\n');
                }
            }
        }

        var summary = new StringBuilder();
        summary.Append(CultureInfo.InvariantCulture,
                       $"oracle V8 {ReferenceV8.Version}; snippets {Snippets.Length}, functions {total}\n");
        for (int k = 0; k < counts.Length; k++) summary.Append(CultureInfo.InvariantCulture, $"  {(Agreement)k}: {counts[k]}\n");
        summary.Append(CultureInfo.InvariantCulture, $"  not paired: {unpaired}\n  compile failures: {compile_failures}\n");
        string dir = ArtifactsDirectory();
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "oracle-bytecode-report.txt"), summary.ToString() + report);
        File.WriteAllText(Path.Combine(dir, "oracle-bytecode-differences.txt"), details.ToString());

        Assert.True(compile_failures == 0 && unpaired == 0, summary.ToString() + details);
        Assert.True(unexpected.Length == 0, "unexplained differences from the oracle:\n" + unexpected + details);
    }
}
