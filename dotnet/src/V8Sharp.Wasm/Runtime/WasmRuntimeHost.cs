// Copyright 2026 Curiosity GmbH
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// V8Sharp: the runtime entry points a JavaScript embedding needs, which WACS's
// name-keyed host binding does not provide:
// - instantiation with an explicit list of resolved imports (the JS API reads
//   each import from the import object itself, V8's InstanceBuilder);
// - allocation of functions, memories, tables, globals and tags outside an
//   instantiation (WebAssembly.Memory and friends, JS functions as imports);
// - a synchronous, re-entrant invocation: wasm may call JavaScript, which may
//   call wasm again, and an exception leaving an inner invocation must leave
//   the outer one's call stack and operand stack as they were.

using System;
using System.Collections.Generic;
using Wacs.Core.Instructions;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;

namespace Wacs.Core.Runtime
{
    /// <summary>
    /// V8Sharp: thrown by a host function to throw a wasm exception at the
    /// call site (the JS API: a JavaScript exception that enters wasm, which
    /// wasm can catch with catch_all or a catch of WebAssembly.JSTag).
    /// </summary>
    public sealed class WasmHostException : Exception
    {
        public WasmHostException(Value exnRef) : base("wasm exception") => ExnRef = exnRef;

        /// <summary>The exnref to throw.</summary>
        public Value ExnRef { get; }
    }

    public partial class WasmRuntime
    {
        // The imports of the instantiation in progress, in import order, when
        // the caller resolved them (null: resolve by name, WACS's binding).
        private IAddress?[]? _explicitImports;

        /// <summary>
        /// Instantiates <paramref name="module"/> with <paramref name="imports"/>
        /// (one address per import, in import order).
        /// </summary>
        public ModuleInstance InstantiateModule(Module module, IAddress?[] imports, RuntimeOptions? options = default)
        {
            var saved = _explicitImports;
            var savedStore = Wacs.Core.Runtime.Store.Current;
            _explicitImports = imports;
            Wacs.Core.Runtime.Store.Current = RuntimeStore;
            try
            {
                return InstantiateModule(module, options);
            }
            finally
            {
                _explicitImports = saved;
                Wacs.Core.Runtime.Store.Current = savedStore;
            }
        }

        private IAddress? ResolveImport(int index, (string module, string entity) entityId) =>
            _explicitImports != null ? _explicitImports[index] : GetBoundEntity(entityId);

        private T Allocate<T>(Func<Store, T> allocate)
        {
            Store.OpenTransaction();
            try
            {
                return allocate(Store);
            }
            finally
            {
                Store.CommitTransaction();
            }
        }

        /// <summary>Allocates a raw host function (see <see cref="HostFunction.RawHostFunc"/>).</summary>
        public FuncAddr AllocateHostFunction(string module, string name, FunctionType type,
            HostFunction.RawHostFunc function, object? hostData = null) =>
            Allocate(store => store.AddFunction(new HostFunction((module, name), type, function) { HostData = hostData }));

        /// <summary>Allocates a memory (WebAssembly.Memory).</summary>
        public MemAddr AllocateMemory(MemoryType type) =>
            Allocate(store => store.AddMemory(new MemoryInstance(type, MemoryStorageMode.ManagedArray)));

        /// <summary>Allocates a table filled with <paramref name="init"/> (WebAssembly.Table).</summary>
        public TableAddr AllocateTable(TableType type, Value init) =>
            Allocate(store => store.AddTable(new TableInstance(type, init)));

        /// <summary>Allocates a global (WebAssembly.Global).</summary>
        public GlobalAddr AllocateGlobal(GlobalType type, Value value) =>
            Allocate(store => store.AddGlobal(new GlobalInstance(type, value)));

        /// <summary>Allocates a tag of the given defined type (WebAssembly.Tag).</summary>
        public TagAddr AllocateTag(DefType type) =>
            Allocate(store => store.AddTag(new TagInstance(type)));

        /// <summary>
        /// Calls the function at <paramref name="funcAddr"/> with
        /// <paramref name="args"/> and returns its results. Re-entrant: when a
        /// host function called from wasm calls this, the inner invocation
        /// runs on the same ExecContext above the caller's frames, and on an
        /// exception everything it pushed is popped again before the exception
        /// propagates.
        /// </summary>
        public Value[] Invoke(FuncAddr funcAddr, ReadOnlySpan<Value> args)
        {
            var ctx = GetExecContext();
            var funcInst = ctx.Store[funcAddr];
            var funcType = funcInst.Type;

            int savedPointer = ctx.InstructionPointer;
            int savedHeight = ctx.StackHeight;
            int savedFloor = ctx.UnwindFloor;
            int savedCount = ctx.OpStack.Count;
            var savedStore = Wacs.Core.Runtime.Store.Current;
            Wacs.Core.Runtime.Store.Current = RuntimeStore;

            ctx.OpStack.GuardExhaust(args.Length);
            for (int i = 0; i < args.Length; ++i)
                ctx.OpStack.PushValue(args[i]);

            ctx.InstructionPointer = ExecContext.AbortSequence;
            ctx.UnwindFloor = savedHeight;
            try
            {
                ctx.InvokeResolved(funcInst);
                RunUntilReturn(ctx);

                var results = funcType.ResultType.Arity == 0
                    ? Array.Empty<Value>()
                    : new Value[funcType.ResultType.Arity];
                for (int i = results.Length - 1; i >= 0; --i)
                    results[i] = ctx.OpStack.PopAny();
                ctx.GetModule(funcAddr)?.DerefTypes(results);
                return results;
            }
            catch
            {
                ctx.UnwindCallStackTo(savedHeight);
                ctx.OpStack.Count = savedCount;
                throw;
            }
            finally
            {
                ctx.InstructionPointer = savedPointer;
                ctx.UnwindFloor = savedFloor;
                Wacs.Core.Runtime.Store.Current = savedStore;
            }
        }

        /// <summary>
        /// The dispatch loop of <see cref="ProcessThreadAsync(ExecContext,long)"/>
        /// without gas, statistics or async host functions: runs until the
        /// invoked function returns to <see cref="ExecContext.AbortSequence"/>.
        /// </summary>
        private static void RunUntilReturn(ExecContext ctx)
        {
            InstructionBase inst;
            while (++ctx.InstructionPointer >= 0)
            {
                inst = ctx._currentSequence[ctx.InstructionPointer];
                if (inst.PointerAdvance > 0)
                    ctx.InstructionPointer += inst.PointerAdvance;
                if (inst.Nop)
                    continue;
                try
                {
                    inst.Execute(ctx);
                }
                catch (WasmHostException hx)
                {
                    // A host function threw: throw its exception in wasm at the
                    // call site, where try_table handlers can catch it.
                    ctx.OpStack.PushValue(hx.ExnRef);
                    Wacs.Core.Instructions.InstThrowRef.ExecuteInstruction(ctx, inst);
                }
                catch (TrapException te) when (te.WasmFrames == null)
                {
                    te.WasmFrames = ctx.SnapshotCallStack(inst);
                    throw;
                }
                catch (UnhandledWasmException ue) when (ue.WasmFrames == null)
                {
                    ue.WasmFrames = ctx.SnapshotCallStack(inst);
                    throw;
                }
                catch (WasmRuntimeException re) when (re.WasmFrames == null)
                {
                    re.WasmFrames = ctx.SnapshotCallStack(inst);
                    throw;
                }
            }
        }
    }
}
