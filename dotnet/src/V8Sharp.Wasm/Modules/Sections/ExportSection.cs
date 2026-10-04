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
using System.IO;
using Wacs.Core.Validation;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using Wacs.Core.Utilities;

namespace Wacs.Core
{
    public partial class Module
    {
        /// <summary>
        /// @Spec 2.5.10. Exports
        /// </summary>
        public Export[] Exports { get; internal set; } = Array.Empty<Export>();

        /// <summary>
        /// @Spec 2.5.10. Exports
        /// </summary>
        public class Export : IRenderable
        {
            public string Name { get; internal set; } = null!;

            public ExportDesc Desc { get; internal set; } = null!;

            public void RenderText(StreamWriter writer, Module module, string indent)
            {
                var nameText = $" \"{Name}\"";
                var expText = Desc switch
                {
                    ExportDesc.FuncDesc fd => $" (func {fd.FunctionIndex.Value})",
                    ExportDesc.TableDesc td => $" (table {td.TableIndex.Value})",
                    ExportDesc.MemDesc md => $" (memory {md.MemoryIndex.Value})",
                    ExportDesc.GlobalDesc gd => $" (global {gd.GlobalIndex.Value})",
                    ExportDesc.TagDesc td => $" (tag {td.TagIndex.Value})",
                    _ => throw new InvalidDataException($"Unknown Export type:{Desc}")
                };
                var tableText = $"{indent}(export{nameText}{expText})";
            
                writer.WriteLine(tableText);
            }

            /// <summary>
            /// @Spec 3.4.8.1.
            /// </summary>
            // V8Sharp: plain code instead of FluentValidation validators.
            public static class Validator
            {
                public static void Validate(Export export, WasmValidationContext ctx)
                {
                    switch (export.Desc)
                    {
                        case ExportDesc.FuncDesc fd:
                            if (!ctx.Funcs.Contains(fd.FunctionIndex))
                                throw new ValidationException($"Validation context did not contain FuncDesc Index {fd.FunctionIndex.Value}");
                            break;
                        case ExportDesc.TableDesc td:
                            if (!ctx.Tables.Contains(td.TableIndex))
                                throw new ValidationException($"Validation context did not contain TableDesc Index {td.TableIndex.Value}");
                            break;
                        case ExportDesc.MemDesc md:
                            if (!ctx.Mems.Contains(md.MemoryIndex))
                                throw new ValidationException($"Validation context did not contain MemDesc Index {md.MemoryIndex.Value}");
                            break;
                        case ExportDesc.GlobalDesc gd:
                            if (!ctx.Globals.Contains(gd.GlobalIndex))
                                throw new ValidationException($"Validation context did not contain GlobalDesc Index {gd.GlobalIndex.Value}");
                            break;
                        case ExportDesc.TagDesc tgd:
                            if (!ctx.Tags.Contains(tgd.TagIndex))
                                throw new ValidationException($"Validation context did not contain TagDesc Index {tgd.TagIndex.Value}");
                            break;
                    }
                }
            }
        }


        public abstract class ExportDesc
        {
            public class FuncDesc : ExportDesc
            {
                public FuncIdx FunctionIndex { get; internal set; }

            }

            public class TableDesc : ExportDesc
            {
                public TableIdx TableIndex { get; internal set; }

            }

            public class MemDesc : ExportDesc
            {
                public MemIdx MemoryIndex { get; internal set; }

            }

            public class GlobalDesc : ExportDesc
            {
                public GlobalIdx GlobalIndex { get; internal set; }

            }

            public class TagDesc : ExportDesc
            {
                public TagIdx TagIndex { get; internal set; }

            }
        }
    }

    public static partial class BinaryModuleParser
    {
        private static Module.ExportDesc ParseExportDesc(BinaryReader reader) =>
            ExternalKindParser.Parse(reader) switch
            {
                ExternalKind.Function => new Module.ExportDesc.FuncDesc
                    { FunctionIndex = (FuncIdx)reader.ReadLeb128_u32() },
                ExternalKind.Table => new Module.ExportDesc.TableDesc
                    { TableIndex = (TableIdx)reader.ReadLeb128_u32() },
                ExternalKind.Memory => new Module.ExportDesc.MemDesc { MemoryIndex = (MemIdx)reader.ReadLeb128_u32() },
                ExternalKind.Global => new Module.ExportDesc.GlobalDesc
                    { GlobalIndex = (GlobalIdx)reader.ReadLeb128_u32() },
                ExternalKind.Tag => new Module.ExportDesc.TagDesc
                    { TagIndex = (TagIdx)reader.ReadLeb128_u32() },
                var kind => throw new FormatException(
                    $"Malformed Module Export section {kind} at {reader.BaseStream.Position - 1}")
            };

        private static Module.Export ParseExport(BinaryReader reader) =>
            new()
            {
                Name = reader.ReadUtf8String(),
                Desc = ParseExportDesc(reader)
            };

        /// <summary>
        /// @Spec 5.5.10 Export Section
        /// </summary>
        private static Module.Export[] ParseExportSection(BinaryReader reader) =>
            reader.ParseVector(ParseExport);
    }
}