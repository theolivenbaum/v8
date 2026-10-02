# CLAUDE.md

Guidance for AI coding agents working on **V8Sharp**, the port of V8 to pure,
safe C# on .NET 10. The deliverable is everything under `dotnet/`. The rest of
this repository is V8 itself, at the revision named in `dotnet/UPSTREAM.md`,
and it is the source being ported.

Read `dotnet/todo.md` for live status and the work queue, and
`dotnet/docs/architecture.md` for the design every component builds on.

## Ground rules

1. **The V8 tree outside `dotnet/` is read-only reference.** Never edit
   `src/`, `test/`, `include/`, `tools/` or anything else outside `dotnet/`
   (the only exception is the git-ignored `test/test262/data` checkout).
   A C# test that fails is fixed in C#.
2. **Port V8's architecture, not only its behaviour.** Same pipeline, same
   class and method names (PascalCase), same algorithms, same fast and slow
   paths, same thresholds and constants. A C# file names the V8 file it ports
   in its header comment, e.g. `// Port of src/objects/lookup.cc.` Read the
   V8 source completely before writing the C#. Write idiomatic modern C#,
   not transliterated C++, but keep the structure recognisable.
3. **Observable behaviour must match V8 exactly**: property enumeration order,
   error messages (`MessageTemplate` text), number formatting, `Function.prototype.toString`,
   stack-trace format, bytecode (checked against the golden files).
   The authority, in order: this tree's tests and golden files, then the
   oracle (real V8 via ClearScript), then the ECMAScript spec.
4. **Where C# deviates from V8 on purpose, say so in a comment at the
   deviation** (what V8 does, what we do, why) and record it in
   `dotnet/deviations.md` under the component's heading. Replacing V8's GC, handles, snapshot and
   machine-code backends is by design (architecture.md section 2) and needs no
   per-site comment.
5. **Safe C#.** `AllowUnsafeBlocks` is off. A project that needs `unsafe`
   turns it on in its own `.csproj` with a comment, and each `unsafe` block
   says why a safe alternative (spans, `MemoryMarshal`, `Unsafe.As`,
   `BitConverter`, `Vector<T>`) is not enough. Prefer `Span<T>`,
   `ReadOnlySpan<T>`, `stackalloc` into spans, `ArrayPool<T>`,
   `System.Numerics.Vector<T>`, `System.Runtime.Intrinsics`,
   `System.Numerics.Tensors` (`TensorPrimitives`) where they help.
6. **No new dependencies in the engine.** `V8Sharp.Base`, `.Parsing`,
   `.RegExp`, `V8Sharp` and `.D8` reference no packages at all. Only
   `V8Sharp.Oracle` (and the tests/tools that use it) may reference ClearScript.
7. **Performance is a design constraint.** No LINQ, no closures, no boxing and
   no per-operation allocation on hot paths (interpreter dispatch, property
   access, arithmetic, string building, scanning). Use `JSValue` structs,
   `in` parameters, `[MethodImpl(AggressiveInlining)]` for tiny helpers,
   sealed classes, and `switch` on `InstanceType` rather than type tests
   chains.

## Target and language level

- `net10.0`, `LangVersion` latest, nullable enabled, implicit usings enabled.
- File-scoped namespaces, primary constructors where they fit, collection
  expressions, pattern matching, `readonly struct`/`record struct` for values.
- `Directory.Packages.props` is the only place package versions are declared.
- `InvariantGlobalization` is on: never depend on the current culture. Use
  `CultureInfo.InvariantCulture` / ordinal comparisons everywhere.
- **Numbers are IEEE doubles with JS semantics.** Never use C#'s
  `double.ToString()` or `double.Parse()` for JS-visible conversions: use the
  ported `DoubleToCString`/`StringToDouble` (`V8Sharp.Base.Numbers`).
  `Math.Round` is not JS `Math.round`; `%` on doubles matches `fmod`, which is
  what JS wants; `(int)double` is not `ToInt32` (use `DoubleToInt32`).
- **Strings are UTF-16**, like V8's two-byte strings. Index by `char`
  (code unit), never by `Rune` or grapheme.

## Layout

```
CLAUDE.md                        imports dotnet/CLAUDE.md
dotnet/
  CLAUDE.md                      this file
  todo.md                        status and work queue
  deviations.md                  every intentional difference from V8, and why
  UPSTREAM.md                    the V8 revision being ported
  docs/architecture.md           the design: values, objects, frames, tiers
  V8Sharp.slnx
  Directory.Build.props          shared settings; Directory.Packages.props: versions
  src/
    V8Sharp.Base/                <- src/base, src/numbers, src/bigint, src/strings/unicode*
    V8Sharp.Parsing/             <- src/parsing, src/ast
    V8Sharp.RegExp/              <- src/regexp
    V8Sharp/                     <- src/objects, execution, init, interpreter, builtins,
                                    runtime, ic, json, date, compiler (IL tiers) ...
    V8Sharp.D8/                  <- src/d8 : the d8sharp shell (also used in-process by tests)
    V8Sharp.Oracle/              the real V8 (ClearScript) for differential testing
  tests/
    V8Sharp.<Area>.Tests/        ports of test/unittests and test/cctest
    V8Sharp.Parity.Tests/        same script through V8Sharp and the oracle
    V8Sharp.Conformance.Tests/   curated mjsunit/test262 gates for CI
  tools/
    V8Sharp.TestRunner/          run-tests.py equivalent: mjsunit, test262, message ...
```

## Build and test

```bash
cd dotnet
dotnet build -c Release
dotnet test -c Release                               # everything
dotnet test -c Release tests/V8Sharp.Base.Tests       # one area
dotnet run -c Release --project src/V8Sharp.D8 -- -e 'print(1+1)'
dotnet run -c Release --project tools/V8Sharp.TestRunner -- mjsunit --engine v8sharp
dotnet run -c Release --project tools/V8Sharp.TestRunner -- test262 --engine oracle
```

- Tests are xUnit v3. `using Xunit;` is global in test projects.
- test262 lives at `test/test262/data` (git-ignored). Check it out from the
  fork at V8's pinned commit if it is missing:
  `git clone https://github.com/theolivenbaum/test262 test/test262/data` then
  `git -C test/test262/data checkout <commit from DEPS 'test/test262/data'>`.
- **The oracle** (`V8Sharp.Oracle.ReferenceV8`) is real V8 14.7 in-process. It
  accepts V8 flags through `ReferenceV8.EnsureFlags` (P/Invoke to
  `v8::V8::SetFlagsFromString`), set before the first isolate: e.g.
  `--allow-natives-syntax`, `--print-bytecode --print-bytecode-filter=f`.
  Flags are process-global; run tests needing different flag sets in
  different processes.
- **Shared machine: serialize benchmarks.** When several agents share a host,
  wrap every benchmark/timing run in `flock -x /home/user/locks/bench.lock <cmd>`
  and every full conformance run in `flock -s /home/user/locks/bench.lock <cmd>`
  (builds and unit tests need no lock). Benchmarks then run alone; numbers
  taken under load are not evidence for a decision.
- Running something long (a conformance sweep), use a timeout and write the
  results under `dotnet/artifacts/` (git-ignored).

## Porting workflow (per component)

1. Read the V8 source completely (`.h`, `.cc`, `-inl.h`, and the `.tq` for
   builtins). Note every public item.
2. Port the types and public surface, then the bodies.
3. Port the matching tests from `test/unittests/<area>` and `test/cctest`,
   keeping names so the mapping is obvious.
4. Run the area's tests; then the relevant mjsunit/test262 directories through
   the TestRunner; then compare against the oracle for anything the tests do
   not pin down.
5. Update `dotnet/todo.md` (tick the component, list what is missing) and
   `dotnet/deviations.md` (every deviation).

## Conventions

- Commits: short and factual, imperative, no AI filler, no em dashes. Name the
  V8 files ported (`Port src/objects/lookup.cc: LookupIterator`).
- Keep `todo.md` truthful: never mark something done that has failing tests.
- Don't add comments that restate the code. Do keep V8's comments where they
  explain *why* (spec step numbers, subtle invariants).
- When unsure what V8 does, run it: the oracle is one line away.
