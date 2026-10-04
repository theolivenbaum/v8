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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Wacs.Core.Validation;
using Wacs.Core.Instructions;
using Wacs.Core.OpCodes;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;

namespace Wacs.Core.Validation
{
    /// <summary>
    /// @Spec 3.1.1. Contexts
    /// </summary>
    public class WasmValidationContext : IWasmValidationContext
    {
        private static Stack<Value> _aside = new();

        /// <summary>
        /// @Spec 3.1.1. Contexts
        /// @Spec 3.4.10. Modules
        /// </summary>
        public WasmValidationContext(Module module)
        {
            ValidationModule = new ModuleInstance(module);

            Stack = new(this);
            
            Funcs = new FunctionsSpace(module);
            Tables = new TablesSpace(module);
            Mems = new MemSpace(module);

            Elements = new ElementsSpace(module.Elements.ToList());
            Datas = new DataValidationSpace(module.Datas.Length);

            Globals = new GlobalValidationSpace(module);

            Tags = new TagsSpace(module);
        }

        private Frame ExecFrame { get; set; } = null!;
        public ResultType ReturnType { get; set; } = null!;
        private ValidationOpStack Stack { get; }


        public ResultType Return => ControlFrame.EndTypes;
        private ModuleInstance ValidationModule { get; }
        public RuntimeAttributes Attributes { get; set; } = new();

        // V8Sharp: V8 rejects a module that mixes the legacy exception
        // handling instructions with try_table/throw_ref
        // (WasmDetectedFeatures legacy_eh and exnref).
        private bool _hasLegacyEH, _hasExnRef;

        public static void DetectExceptionHandling(IWasmValidationContext context, bool legacy)
        {
            if (context is not WasmValidationContext ctx) return;
            if (legacy) ctx._hasLegacyEH = true; else ctx._hasExnRef = true;
            if (ctx._hasLegacyEH && ctx._hasExnRef && !ctx.Attributes.AllowMixedExceptionHandling)
                throw new ValidationException("module uses a mix of legacy and new exception handling instructions");
        }

        public FuncIdx FunctionIndex { get; set; } = FuncIdx.Default;
        public IValidationOpStack OpStack => Stack;



        public Stack<ValidationControlFrame> ControlStack { get; } = new();
        public ValidationControlFrame ControlFrame => ControlStack.Peek();
        public TypesSpace Types => ValidationModule.Types;
        public FunctionsSpace Funcs { get; }
        public TablesSpace Tables { get; }
        public MemSpace Mems { get; }
        public GlobalValidationSpace Globals { get; }

        public Memory<Value> Locals =>
            ControlStack.Count == 0
                ? ExecFrame.Locals 
                : ControlFrame.Locals;

        public ElementsSpace Elements { get; set; }
        public DataValidationSpace Datas { get; set; }

        public TagsSpace Tags { get; }

        public bool Unreachable { get; set; }

        /// <summary>
        /// @Spec A.3 Validation Algorithm
        /// </summary>
        public void SetUnreachable()
        {
            PopOperandsToHeight(ControlFrame.Height);
            ControlFrame.Unreachable = true;
        }

        public void Assert(bool factIsTrue, string formatString, params object[] args)
        {
            if (factIsTrue) return;
            throw new ValidationException(string.Format(formatString, args));
        }

        public void Assert([NotNull] object? objIsNotNull, string formatString, params object[] args)
        {
            if (objIsNotNull != null) return;
            throw new ValidationException(string.Format(formatString, args));
        }

        // V8Sharp: plain code; WACS validated each instruction through a
        // FluentValidation sub-context.
        public void ValidateBlock(Block instructionBlock, int index = 0)
        {
            Block.Validator.Validate(instructionBlock, this);
            foreach (var inst in instructionBlock.Instructions)
                ValidateInstruction(inst, this);
        }

        public void ValidateCatches(CatchType[] catches)
        {
            foreach (var catchType in catches)
                CatchType.Validator.Validate(catchType, this);
        }

        /// <summary>
        /// Validates one instruction; a failure carries the instruction's
        /// mnemonic (V8Sharp: WACS appended it to the message).
        /// </summary>
        public static void ValidateInstruction(InstructionBase inst, WasmValidationContext ctx)
        {
            try
            {
                inst.Validate(ctx);
            }
            catch (ValidationException exc) when (exc.Instruction == null)
            {
                exc.Instruction = inst.Op.GetMnemonic();
                throw;
            }
            catch (NotImplementedException)
            {
                throw new ValidationException($"WASM Instruction `{inst.Op.GetMnemonic()}` is not implemented.")
                    { Instruction = inst.Op.GetMnemonic() };
            }
        }

        public void PushControlFrame(ByteCode opCode, FunctionType types)
        {
            var frame = new ValidationControlFrame
            {
                Opcode = opCode,
                Types = types,
                Height = OpStack.Height,
                //Local refs are only valid if initialized in the same block.
                //Copy locals state so child blocks won't propagate initialization.
                Locals = Locals.ToArray(),
            };
            
            ControlStack.Push(frame);
            
            OpStack.PushResult(types.ParameterTypes);
        }

        public ValidationControlFrame PopControlFrame()
        {
            if (ControlStack.Count == 0)
                throw new ValidationException("Validation Control Stack underflow");
            
            //Check to make sure we have the correct results, but only if we didn't jump
            OpStack.DiscardValues(ControlFrame.EndTypes);
            
            //Check the stack
            if (OpStack.Height != ControlFrame.Height)
                throw new ValidationException(
                    $"Operand stack height {OpStack.Height} differed from Control Frame height {ControlFrame.Height}");

            return ControlStack.Pop();
        }

        public bool ContainsLabel(uint label) => ControlStack.Count - 2 >= label;

        public bool ValidateBlockType(ValType type) => 
            type.Validate(Types) || type == ValType.Empty;


        public void PopOperandsToHeight(int height)
        {
            if (OpStack.Height < height)
                throw new InvalidDataException("Operand Stack underflow.");
            
            while (OpStack.Height > height)
            {
                OpStack.PopAny();
            }
        }

        public void SetExecFrame(FunctionType funcType, ValType[] localTypes)
        {
            ControlStack.Clear();
            var locals = CreateLocalsSpace(funcType.ParameterTypes.Types, localTypes);
            ExecFrame = new Frame
            {
                Module = ValidationModule,
                // Type = funcType,
                Locals = locals,
            };
            ReturnType = funcType.ResultType;
        }

        private static Memory<Value> CreateLocalsSpace(ValType[] parameters, ValType[] locals)
        {
            int parameterCount = parameters.Length;
            int localCount = locals.Length;
            int capacity = parameterCount + localCount;
            var data = new Value[capacity];
            for (int i = 0; i < parameterCount; i++)
            {
                data[i] = new Value(parameters[i]).MakeSet();
            }
            for (int i = parameterCount, t = 0; i < data.Length; i++, t++)
            {
                data[i] = new Value(locals[t]);
            }
            return new Memory<Value>(data);
        }

    }
}