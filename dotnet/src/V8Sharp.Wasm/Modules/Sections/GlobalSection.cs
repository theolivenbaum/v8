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
using Wacs.Core.Validation;
using Wacs.Core.Attributes;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using Wacs.Core.Utilities;

namespace Wacs.Core
{

    public partial class Module
    {
        /// <summary>
        /// @Spec 2.5.6 Globals
        /// </summary>
        public List<Global> Globals { get; internal set; } = new();

        /// <summary>
        /// @Spec 2.5.6. Globals
        /// </summary>
        public class Global : IRenderable
        {
            public readonly Expression Initializer;
            public readonly GlobalType Type;

            public bool IsImport;

            public Global(GlobalType type) =>
                (Type, Initializer) = (type, Expression.Empty);

            // Used by the text parser to populate both type and init in one
            // shot (readonly fields preclude a mutation-based API).
            internal Global(GlobalType type, Expression init) =>
                (Type, Initializer) = (type, init);

            private Global(BinaryReader reader) =>
                (Type, Initializer) = (GlobalType.Parse(reader), Expression.ParseInitializer(reader));

            public string Id { get; set; } = "";

            public void RenderText(StreamWriter writer, Module module, string indent)
            {
                var id = string.IsNullOrWhiteSpace(Id) ? "" : $" (;{Id};)";
                
                var globalType = Type.Mutability == Mutability.Mutable
                    ? $" (mut {Type.ContentType.ToWat()})"
                    : $" {Type.ContentType.ToWat()}";
                
                var expr = Initializer.ToWat();
                var globalText = $"{indent}(global{id}{globalType}{expr})";
            
                writer.WriteLine(globalText);
            }

            /// <summary>
            /// @Spec 5.5.9. Global Section
            /// </summary>
            public static Global Parse(BinaryReader reader) => new(reader);

            /// <summary>
            /// @Spec 3.4.4.1 Globals
            /// </summary>
            // V8Sharp: plain code instead of a FluentValidation validator.
            public static class Validator
            {
                public static void Validate(Global g, WasmValidationContext validationContext)
                {
                    GlobalType.Validator.Validate(g.Type, validationContext);

                    var funcType = FunctionType.Empty;
                    validationContext.FunctionIndex = FuncIdx.Default;
                    validationContext.SetExecFrame(funcType, Array.Empty<ValType>());
                    Expression.Validator.Validate(g.Initializer, g.Type.ResultType, validationContext, isConstant: true);

                    validationContext.Globals.SetHighWatermark(g);
                }
            }
        }
    }
    
    public static partial class BinaryModuleParser
    {
        /// <summary>
        /// @Spec 5.5.9 Global Section
        /// </summary>
        private static List<Module.Global> ParseGlobalSection(BinaryReader reader) =>
            reader.ParseList(Module.Global.Parse);
    }
}