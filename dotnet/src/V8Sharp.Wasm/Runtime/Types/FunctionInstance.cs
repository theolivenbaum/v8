// Copyright 2024 Kelvin Nishikawa
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

using System;
using System.Collections.Generic;
using Wacs.Core.OpCodes;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using InstructionPointer = System.Int32;

namespace Wacs.Core.Runtime.Types
{
    /// <summary>
    /// @Spec 4.2.6. Function Instances
    /// Represents a WebAssembly-defined function instance.
    /// </summary>
    public class FunctionInstance : IFunctionInstance
    {
        private readonly static ByteCode LabelInst = OpCode.Func;

        /// <summary>
        /// The function definition containing the raw code and locals.
        /// </summary>
        public readonly Module.Function Definition;

        public readonly DefType DefType;

        public readonly FuncIdx Index;

        public FuncAddr Address;
        public int CallCount;

        public readonly ModuleInstance Module;

        //Copied from the static Definition
        //Can be processed with optimization passes
        public Expression Body;
        public int Length;
        public int MaxStack;

        public InstructionPointer LinkedOffset;

        //Copied from the static Definition
        public ValType[] Locals;
        private int LocalCount;
        private int ParameterCount;
        private int TotalCount;

        /// <summary>
        /// @Spec 4.5.3.1. Functions
        /// Initializes a new instance of the <see cref="FunctionInstance"/> class.
        /// </summary>
        public FunctionInstance(ModuleInstance module, Module.Function definition)
        {
            var type = module.Types[definition.TypeIndex];
            if (!(type.Expansion is FunctionType funcType))
                throw new FormatException($"Function defined with type {type}");
            
            Type = funcType;
            Module = module;
            DefType = type;
            Definition = definition;
            Body = definition.Body;
            Body.LabelTarget.Label.Arity = Type.ResultType.Arity;
            Locals = definition.Locals;
            LocalCount = Locals.Length;
            ParameterCount = funcType.ParameterTypes.Arity;
            TotalCount = LocalCount + ParameterCount;
            Index = definition.Index;
            
            if (!string.IsNullOrEmpty(Definition.Id))
                Name = Definition.Id;
        }

        public string ModuleName => Module.Name;
        public string Name { get; set; } = "";
        public FunctionType Type { get; }
        public void SetName(string value) => Name = value;
        public string Id => string.IsNullOrEmpty(Name)?"":$"{ModuleName}.{Name}";
        public bool IsExport { get; set; }

        public bool IsAsync => false;

        /// <summary>
        /// Cached annotated-bytecode form, produced lazily by
        /// <c>Wacs.Core.Compilation.BytecodeCompiler</c> on the first switch-runtime
        /// invocation. Accessed via a simple field read on the hot path — no
        /// dictionary lookup, no lock. The compile pass is deterministic, so a benign
        /// race just produces both threads' work and discards one via last-writer-wins.
        /// Field lives here rather than in a side dictionary so it's GC-tied to the
        /// FunctionInstance lifetime and not leaked across module unloads.
        /// </summary>
        internal Wacs.Core.Compilation.CompiledFunction? SwitchCompiled;

        /// <summary>
        /// Sets Body and precomputes labels
        /// </summary>
        /// <param name="body"></param>
        public void SetBody(Expression body)
        {
            Body = body;
            Body.LabelTarget.Label.Arity = Type.ResultType.Arity;
        }

        /// <summary>
        /// V8Sharp: the function's compiled code (V8Sharp's wasm compiler),
        /// or null while it runs in the interpreter.
        /// </summary>
        public ICompiledFunctionCode? Compiled;

        /// <summary>V8Sharp: the compiler declined the function (it stays in the interpreter).</summary>
        public bool NotCompilable;

        /// <summary>V8Sharp: the compiled code, compiling it lazily if the module has a compiler.</summary>
        public ICompiledFunctionCode? GetCompiledCode()
        {
            var code = Compiled;
            if (code != null || NotCompilable) return code;
            if (Module.Compiler is not { } compiler)
            {
                return null;
            }
            code = compiler.GetCode(this);
            if (code == null) NotCompilable = true;
            return code;
        }

        public void Invoke(ExecContext context)
        {
            context.CheckInterrupt();

            // V8Sharp: compiled code runs on the .NET stack.
            if (GetCompiledCode() is { } compiled)
            {
                CallCount++;
                compiled.InvokeFromInterpreter(context);
                return;
            }

            //3.
            var funcType = Type;
            //4.
            var t = Locals;
            //5. *Instructions will be handled in EnterSequence below
            //var seq = Body.Instructions;
            //6.
#if STRICT_EXECUTION
            context.Assert( context.OpStack.Count >= funcType.ParameterTypes.Arity,
                $"Function invocation failed. Operand Stack underflow.");
#endif
            //8.
            //Push the frame and operate on the frame on the stack.
            var frame = context.ReserveFrame(Module, funcType.ResultType.Arity);
            frame.FuncAddr = (ushort)Address.Value;
            frame.Locals = context.OpStack.ReserveLocals(ParameterCount, TotalCount);
            try
            {
                context.OpStack.GuardExhaust(MaxStack);
            }
            catch (Wacs.Core.Runtime.Exceptions.WasmRuntimeException e) when (e.CalleeFuncAddr < 0)
            {
                e.CalleeFuncAddr = Address.Value;
                throw;
            }
                
            //Return the stack to this height after the function returns
            frame.ReturnLabel.StackHeight += LocalCount;
            
            //Set the Locals to default
            var slice = frame.Locals.Span[ParameterCount..TotalCount];
            for (int ti = 0; ti < LocalCount; ti++)
            {
                slice[ti].ResetToDefault(t[ti]);
            }

            //9.
            try
            {
                context.PushFrame(frame);
            }
            catch (Wacs.Core.Runtime.Exceptions.WasmRuntimeException e) when (e.CalleeFuncAddr < 0)
            {
                e.CalleeFuncAddr = Address.Value;
                throw;
            }
            
            //10.
            frame.ReturnLabel.Arity = funcType.ResultType.Arity;
            frame.ReturnLabel.Instruction = LabelInst;
            frame.ReturnLabel.ContinuationAddress = context.GetPointer();
            frame.Head = LinkedOffset;
            
            context.InstructionPointer = LinkedOffset - 1;
            CallCount++;
        }
        
        public void TailInvoke(ExecContext context)
        {
            // V8Sharp: a tail call to compiled code is a call and a return.
            if (GetCompiledCode() is { } compiled)
            {
                CallCount++;
                context.TailCallCompiled(compiled);
                return;
            }
            var frame = context.ReuseFrame();
            //3.
            var funcType = Type;
            //4.
            var t = Locals;
            
            frame.Module = Module;

            int lastLocalsCount = frame.Locals.Length;
            int resultsHeight = frame.ReturnLabel.StackHeight - lastLocalsCount + ParameterCount;
            context.OpStack.ShiftResults(ParameterCount, resultsHeight);

            frame.Locals = context.OpStack.ReserveLocals(ParameterCount, TotalCount);
            try
            {
                context.OpStack.GuardExhaust(MaxStack);
            }
            catch (Wacs.Core.Runtime.Exceptions.WasmRuntimeException e) when (e.CalleeFuncAddr < 0)
            {
                e.CalleeFuncAddr = Address.Value;
                throw;
            }
            
            //Return the stack to this height after the function returns
            frame.ReturnLabel.StackHeight = resultsHeight + LocalCount;
            
            //Set the Locals to default
            var slice = frame.Locals.Span[ParameterCount..TotalCount];
            for (int ti = 0; ti < LocalCount; ti++)
            {
                slice[ti] = new Value(t[ti]);
            }
            
            //10.
            frame.ReturnLabel.Arity = funcType.ResultType.Arity;
            frame.ReturnLabel.Instruction = LabelInst;
            frame.Head = LinkedOffset;
            
            context.InstructionPointer = LinkedOffset - 1;
            
            CallCount++;
        }

        public override string ToString() => $"FunctionInstance[{Id}] (Type: {Type}, IsExport: {IsExport})";
    }
}