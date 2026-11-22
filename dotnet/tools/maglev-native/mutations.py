#!/usr/bin/env python3
# Mutation checks of the native Maglev backend (as Ryu checks its native code
# generator): each mutation plants a bug in the generator; the native tests
# (snippets + random programs) must catch every one.
import subprocess, sys, os
root = "/home/user/v8/dotnet"
gen = root + "/src/V8Sharp/Maglev/MaglevNativeCodeGenerator.cs"
env = dict(os.environ, DOTNET_ROOT="/opt/dotnet", PATH="/opt/dotnet:" + os.environ["PATH"], DOTNET_CLI_TELEMETRY_OPTOUT="1",
           V8SHARP_NATIVE_FUZZ="150")
M = [
  ("no int32 overflow check", "            _masm.J(Condition.overflow, exit);\n            if (node.Opcode == Opcode.Int32MultiplyWithOverflow)",
                              "            if (node.Opcode == Opcode.Int32MultiplyWithOverflow)"),
  ("no -0 check of multiply", "                _masm.Testl(rdx, rdx);\n                _masm.J(Condition.negative, exit);\n                _masm.Bind(ok);",
                              "                _masm.Bind(ok);"),
  ("exit stubs store no registers", "            foreach (Val v in stores)\n            {\n                if (v.InGp) _masm.Movl(HomeOp(v), v.Gp);",
                                    "            foreach (Val v in stores.Take(0))\n            {\n                if (v.InGp) _masm.Movl(HomeOp(v), v.Gp);"),
  ("phi cycles not broken", "            Move m = pending[0];\n            Val src = V(m.Src!);",
                            "            EmitMove(pending[0]); pending.RemoveAt(0); continue;\n            Move m = pending[0];\n            Val src = V(m.Src!);"),
  ("float < as <=", "            case CompareOperation.kLessThan:\n                _masm.Ucomisd(b, a);\n                _masm.J(Condition.above, ifTrue);",
                    "            case CompareOperation.kLessThan:\n                _masm.Ucomisd(b, a);\n                _masm.J(Condition.above_equal, ifTrue);"),
  ("float == ignores NaN", "                _masm.J(Condition.parity_even, ifFalse);\n                _masm.J(Condition.equal, ifTrue);",
                           "                _masm.J(Condition.equal, ifTrue);"),
  ("no NaN canonicalization", "                _masm.J(Condition.parity_odd, store);", "                _masm.Jmp(store);"),
  ("ToInt32 slow path gives 0", "        _masm.Subl(rcx, 1075);\n        _masm.Cmpl(rcx, 31);\n        _masm.J(Condition.greater, done);",
                                "        _masm.Subl(rcx, 1075);\n        _masm.Cmpl(rcx, -10000);\n        _masm.J(Condition.greater, done);"),
  ("intervals share a register at one position", "                if (a.End < v.Start)", "                if (a.End <= v.Start)"),
  ("resume does not reload registers", "                else if (v.InGp)\n                {\n                    _masm.Movl(v.Gp, HomeOp(v));\n                }",
                                       "                else if (v.InGp && false)\n                {\n                    _masm.Movl(v.Gp, HomeOp(v));\n                }"),
  ("untagging without the round trip", "        _masm.Cmpq(rcx, rdx);\n        _masm.J(Condition.not_equal, exit);\n    }",
                                       "        _masm.Cmpq(rcx, rdx);\n    }"),
  ("deopt frame values not live", "                if (node.EagerDeoptInfo is { } e)\n                {\n                    var uses = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);\n                    DeoptUses(e, uses);\n                    foreach (ValueNode u in uses)\n                    {\n                        Extend(V(u), pos);",
                                  "                if (node.EagerDeoptInfo is { } e && false)\n                {\n                    var uses = new HashSet<ValueNode>(ReferenceEqualityComparer.Instance);\n                    DeoptUses(e, uses);\n                    foreach (ValueNode u in uses)\n                    {\n                        Extend(V(u), pos);"),
  ("fprem not repeated", "        _masm.J(Condition.not_equal, loop);\n        _masm.Fstp_d(dividend);", "        _masm.Fstp_d(dividend);"),
  ("min/max of equal values ignore the sign", "            _masm.J(Condition.negative, done);\n        }\n        else", "            _masm.Jmp(done);\n        }\n        else"),
  ("divide does not check the remainder", "            _masm.Testl(rdx, rdx);\n            _masm.J(Condition.not_equal, exit);\n            StoreInt32Result(r, rax);",
                                          "            StoreInt32Result(r, rax);"),
  ("phi kind words not copied", "            for (int w = 0; w < (dst.NeedsKind ? 3 : 2); w++)", "            for (int w = 0; w < 2; w++)"),
]
original = open(gen).read()
only = sys.argv[1:]
skip = int(os.environ.get('MUTATION_SKIP', '0'))
M = M[skip:]
results = []
try:
    for name, old, new in M:
        if only and name not in only: continue
        if original.count(old) != 1:
            results.append((name, "PATTERN NOT FOUND (%d)" % original.count(old))); continue
        open(gen, "w").write(original.replace(old, new))
        b = subprocess.run(["dotnet", "build", "-c", "Release", root + "/tests/V8Sharp.Tests"], env=env, capture_output=True, text=True)
        if b.returncode != 0:
            results.append((name, "BUILD FAILED")); continue
        try:
            t = subprocess.run([root + "/tests/V8Sharp.Tests/bin/Release/net11.0/V8Sharp.Tests", "-class", "V8Sharp.Tests.Maglev.MaglevNativeTest",
                                "-parallel", "all"], env=env, capture_output=True, text=True, timeout=300)
            failed = [l.strip() for l in t.stdout.splitlines() if "[FAIL]" in l]
            if failed: verdict = "caught: %d failing tests" % len(failed)
            elif t.returncode != 0: verdict = "caught: test process failed (exit %d)" % t.returncode
            else: verdict = "NOT CAUGHT"
        except subprocess.TimeoutExpired:
            subprocess.run(["pkill", "-f", "V8Sharp.Tests -class"])
            verdict = "caught: a test hung (killed after 300 s)"
        results.append((name, verdict))
        print(name, "->", results[-1][1], flush=True)
finally:
    open(gen, "w").write(original)
    subprocess.run(["dotnet", "build", "-c", "Release", root + "/tests/V8Sharp.Tests"], env=env, capture_output=True, text=True)
for name, r in results: print("%-50s %s" % (name, r))
