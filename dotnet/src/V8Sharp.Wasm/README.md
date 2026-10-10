# V8Sharp.Wasm

The WebAssembly engine of V8Sharp: decoder, validator, runtime (store,
memories, tables, globals, tags, GC types, exceptions) and interpreter.

It is vendored from **WACS** (WebAssembly CSharp Toolchain) by Kelvin
Nishikawa, <https://github.com/kelnishi/WACS> (through the fork
<https://github.com/theolivenbaum/WACS>), at commit **cad6530**, project
`Wacs.Core/Wacs.Core`. WACS is licensed under the Apache License 2.0; the
license is in `LICENSE` and the attribution in `NOTICE`. Every vendored file
keeps its WACS copyright and license header, and the namespaces stay
`Wacs.Core.*` so that the code can be compared with upstream WACS.

V8 compiles WebAssembly to machine code (Liftoff, TurboFan) or runs it in
its DrumBrake interpreter; V8Sharp runs it in WACS's interpreter (see
`dotnet/deviations.md`, "WebAssembly"). The JavaScript API on top of this
project (`WebAssembly.*`, V8's `src/wasm/wasm-js.cc` and `wasm-objects.cc`)
is part of the engine project, `src/V8Sharp/Wasm/`.

## What was vendored

From `Wacs.Core/Wacs.Core`: `Attributes`, `BinaryFormat`, `Compatibility`,
`Compilation`, `Instructions`, `Modules`, `OpCodes`, `Runtime`, `Types`,
`Utilities`, `Validation`.

`Compilation/Generated/GeneratedDispatcher.g.cs` is the output of WACS's
Roslyn source generator `Wacs.Compilation.DispatchGenerator` (the switch
runtime's dispatch loop) for this source, checked in so that the build needs
no generator project.

## What was not vendored

- `WASIp1` (WASI preview 1: not part of a JavaScript engine).
- `Text` (the WAT/WAST parser and writer: V8 has no text format either),
  except `Text/LineMap.cs` and `Text/TriviaToken.cs`, two small types the
  stack-trace formatter and the custom-section codecs refer to.
- `Wacs.Compilation` (the source generators; see above), the
  `OpSourceGenerator` output (C# source strings of instructions, used only by
  WACS's tooling), and the `ThreadedExperiment` dispatcher (InlineIL.Fody).
- `Wacs.Transpiler` (WebAssembly to IL), `Wacs.ComponentModel`, `Wacs.WASI`,
  `Wacs.HostBindings` and the WACS tests and tools.

## Changes from WACS

Changes are listed in `dotnet/deviations.md` under "WebAssembly" and are in
the git history of this directory: the first commit is the unmodified import.
In short: the package dependencies are gone (FluentValidation, Microsoft.
Extensions.ObjectPool, Fody/InlineIL, System.Runtime.CompilerServices.Unsafe),
the `unsafe` native-memory mode of linear memory is removed (memory is a
managed `byte[]`), and the runtime gained the hooks the JavaScript API needs
(explicit import lists, host functions that take and return `Value`s).
