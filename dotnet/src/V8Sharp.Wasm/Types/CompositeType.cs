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
using Wacs.Core.Types.Defs;

namespace Wacs.Core.Types
{
    public abstract class CompositeType
    {
        public ValType HeapType =>
            this switch
            {
                FunctionType ft => ValType.FuncRef,
                ArrayType at => ValType.Array,
                StructType st => ValType.Struct,
                ContType ct => ValType.ContRef,
                _ => throw new InvalidDataException($"Unknown CompType:{this}"),
            };

        public static CompositeType ParseTagged(BinaryReader reader) =>
            reader.ReadByte() switch
            {
                (byte)CompType.ArrayAt => ArrayType.Parse(reader),
                (byte)CompType.StructSt => StructType.Parse(reader),
                (byte)CompType.FuncFt => FunctionType.Parse(reader),
                (byte)CompType.ContCt => ContType.Parse(reader),
                var form => throw new FormatException(
                    $"Invalid comptype format {form} at offset {reader.BaseStream.Position - 1}.")
            };

        /// <summary>
        /// https://webassembly.github.io/gc/core/bikeshed/index.html#composite-types⑤
        /// </summary>
        /// <param name="super"></param>
        /// <param name="types"></param>
        /// <returns></returns>
        public bool Matches(CompositeType super, TypesSpace? types) =>
            this switch
            {
                FunctionType ft1 when super is FunctionType ft2 => ft1.Matches(ft2, types),
                StructType st1 when super is StructType st2 => st1.Matches(st2, types),
                ArrayType at1 when super is ArrayType at2 => at1.Matches(at2, types),
                ContType ct1 when super is ContType ct2 => ct1.Matches(ct2, types),
                _ => false
            };

        public abstract int ComputeHash(int defIndexValue, List<DefType> defs);

        // V8Sharp: plain code instead of a FluentValidation validator.
        public static class Validator
        {
            public static void Validate(CompositeType ct, WasmValidationContext vContext)
            {
                switch (ct)
                {
                    case FunctionType ft:
                        FunctionType.Validator.Validate(ft, vContext);
                        break;
                    case StructType st:
                        foreach (var field in st.FieldTypes)
                            FieldType.Validator.Validate(field, vContext);
                        break;
                    case ArrayType at:
                        FieldType.Validator.Validate(at.ElementType, vContext);
                        break;
                }
            }
        }
    }
}