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
using System.IO;
using System.Linq;
using Wacs.Core.Validation;
using Wacs.Core.Instructions;
using Wacs.Core.OpCodes;
using Wacs.Core.Runtime;
using Wacs.Core.Utilities;

namespace Wacs.Core.Types
{
    /// <summary>
    /// @Spec 2.4.9. Expressions
    /// </summary>
    public class Expression
    {
        public static readonly Expression Empty = new(0, InstructionSequence.Empty, true);
        public readonly InstructionSequence Instructions;

        public readonly bool IsStatic;

        public InstExpressionProxy LabelTarget;

        /// <summary>
        /// For parsing normal Code sections
        /// </summary>
        /// <param name="seq"></param>
        /// <param name="isStatic"></param>
        private Expression(InstructionSequence seq, bool isStatic, OpCode inst = OpCode.Expr)
        {
            Instructions = seq;
            IsStatic = isStatic;
            LabelTarget = new(new Label
            {
                //Compute Arity in during link
                ContinuationAddress = 1,
                Instruction = inst,
                StackHeight = -1,
            });
        }

        /// <summary>
        /// Manual construction (from optimizers or statics)
        /// </summary>
        /// <param name="arity"></param>
        /// <param name="seq"></param>
        /// <param name="isStatic"></param>
        public Expression(int arity, InstructionSequence seq, bool isStatic)
        {
            Instructions = seq;
            IsStatic = isStatic;
            LabelTarget = new(new Label
            {
                Arity = arity,
                ContinuationAddress = 1,
                Instruction = OpCode.Expr,
                StackHeight = 0,
            });
        }

        //Single Initializer
        public Expression(int arity, InstructionBase single)
        {
            IsStatic = true;
            Instructions = new InstructionSequence(new List<InstructionBase> { single });
            LabelTarget = new (new Label
            {
                Arity = arity,
                ContinuationAddress = 1,
                Instruction = OpCode.Expr,
                StackHeight = 0,
            });
        }

        public int Size => Instructions.Size;

        /// <summary>
        /// Leaves the result on the OpStack
        /// </summary>
        /// <param name="context"></param>
        public void ExecuteInitializer(ExecContext context)
        {
            int callStackHeight = context.StackHeight;
            var frame = context.ReserveFrame(context.Frame.Module, 0);
            if (context.OpStack.Count != 0)
                throw new InvalidDataException("OpStack should be empty");
            frame.ReturnLabel = new (LabelTarget.Label);
            context.PushFrame(frame);
            foreach (var inst in Instructions)
            {
                inst.Execute(context);
            }
            
            //Our expression may have popped the callstack for us,
            // but if it hasn't we should clean up.
            while (context.StackHeight > callStackHeight)
            {
                context.PopFrame();
            }
        }

        /// <summary>
        /// @Spec 5.4.9 Expressions
        /// </summary>
        public static Expression ParseFunc(BinaryReader reader) =>
            new(new InstructionSequence(reader.ParseUntil(BinaryModuleParser.ParseInstruction, InstructionBase.IsEnd), true), true, OpCode.Func);

        public static Expression ParseInitializer(BinaryReader reader) =>
            new(1, new InstructionSequence(reader.ParseUntil(BinaryModuleParser.ParseInstruction, InstructionBase.IsEnd)), true);

        /// <summary>
        /// For Single instruction renders (globals, elements)
        /// </summary>
        /// <returns></returns>
        public string ToWat()
        {
            var inst = Instructions[0];
            var instText = $" ({inst?.RenderText(null)??"null"})";
            return instText;
        }

        public bool ContainsInstructions(HashSet<ByteCode> opcodes) => 
            Instructions.ContainsInstruction(opcodes);

        /// <summary>
        /// @Spec 3.3.10. Expressions
        /// </summary>
        // V8Sharp: plain code instead of a FluentValidation validator.
        public static class Validator
        {
            public static void Validate(Expression e, ResultType resultType, WasmValidationContext validationContext,
                bool isConstant = false)
            {
                if (isConstant)
                    if (!e.Instructions.IsConstant(validationContext))
                        throw new ValidationException($"Expression must be constant");

                var funcType = new FunctionType(ResultType.Empty, resultType);
                validationContext.PushControlFrame(OpCode.Expr, funcType); //The root frame
                validationContext.PushControlFrame(OpCode.Block, funcType); //For the end instruction

                int lastIndex = e.Instructions.Count - 1;
                // @Spec 3.3.9. Instruction Sequences
                int index = 0;
                foreach (var inst in e.Instructions)
                {
                    //Skip End for constant expressions
                    if (isConstant && index == lastIndex && inst.Op == OpCode.End)
                        break;
                    WasmValidationContext.ValidateInstruction(inst, validationContext);
                    index++;
                }

                validationContext.PopControlFrame();
                if (validationContext.OpStack.Height != 0)
                    throw new ValidationException($"Expression had leftover operands on the stack");
            }
        }
    }
}