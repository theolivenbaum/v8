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

using Wacs.Core.Validation;
using Wacs.Core.Types.Defs;

namespace Wacs.Core.Types
{
    public class Block
    {
        public static readonly Block Empty = new(ValType.Empty, InstructionSequence.Empty);

        public readonly ValType BlockType;

        public readonly InstructionSequence Instructions;

        public Block(ValType blockType, InstructionSequence seq)
        {
            BlockType = blockType;
            Instructions = seq;
        }

        private TypeIdx TypeIndex => BlockType.Index();

        /// <summary>
        /// The number of immediate child instructions 
        /// </summary>
        public int Length => Instructions.Count;

        /// <summary>
        /// The total number of instructions in the tree below
        /// </summary>
        public int Size => Instructions.Size;

        /// <summary>
        /// @Spec 3.2.2. Block Types
        /// </summary>
        // V8Sharp: plain code instead of a FluentValidation validator.
        public static class Validator
        {
            public static void Validate(Block b, WasmValidationContext ctx)
            {
                // @Spec 3.2.2.1. typeidx
                // @Spec 3.2.2.2. [valtype?]
                if (!ctx.ValidateBlockType(b.BlockType))
                    throw new ValidationException("Blocks must have a defined BlockType if not a ValType index");
            }
        }
    }


}