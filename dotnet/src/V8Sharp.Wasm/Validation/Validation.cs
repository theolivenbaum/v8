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

using Wacs.Core.Runtime;
using Wacs.Core.Types;

namespace Wacs.Core.Validation
{
    /// <summary>
    /// @Spec 3.4. Modules
    /// </summary>
    // V8Sharp: plain code instead of a FluentValidation AbstractValidator<Module>;
    // the rules run in WACS's order and the first failure throws.
    public class ModuleValidator
    {
        private readonly RuntimeAttributes? _attributes;

        public ModuleValidator() : this(null) { }

        public ModuleValidator(RuntimeAttributes? attributes) => _attributes = attributes;

        /// <summary>V8's TruncatedUserString&lt;50&gt;: at most 50 bytes, the last three "...".</summary>
        private static string TruncatedUserString(string name)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(name);
            if (bytes.Length <= 50) return name;
            return System.Text.Encoding.UTF8.GetString(bytes, 0, 47) + "...";
        }

        private static string ExportKind(Module.ExportDesc desc) => desc switch
        {
            Module.ExportDesc.FuncDesc => "function",
            Module.ExportDesc.TableDesc => "table",
            Module.ExportDesc.MemDesc => "memory",
            Module.ExportDesc.GlobalDesc => "global",
            _ => "tag",
        };

        private static long ExportIndex(Module.ExportDesc desc) => desc switch
        {
            Module.ExportDesc.FuncDesc f => f.FunctionIndex.Value,
            Module.ExportDesc.TableDesc t => t.TableIndex.Value,
            Module.ExportDesc.MemDesc m => m.MemoryIndex.Value,
            Module.ExportDesc.GlobalDesc g => g.GlobalIndex.Value,
            Module.ExportDesc.TagDesc t => t.TagIndex.Value,
            _ => -1,
        };

        public void ValidateAndThrow(Module module)
        {
            //Set the validation context
            var vctx = new WasmValidationContext(module);
            if (_attributes != null) vctx.Attributes = _attributes;

            foreach (var type in module.Types)
                RecursiveType.Validator.Validate(type, vctx);
            foreach (var import in module.Imports)
                Module.Import.Validator.Validate(import, vctx);
            vctx.Globals.SetHighImportWatermark();
            foreach (var table in module.Tables)
                TableType.Validator.Validate(table, vctx);
            foreach (var mem in module.Memories)
                MemoryType.Validator.Validate(mem, vctx);
            foreach (var global in module.Globals)
                Module.Global.Validator.Validate(global, vctx);
            foreach (var tag in module.Tags)
                TagType.Validator.Validate(tag, vctx);
            foreach (var func in module.ValidationFuncs)
                Module.Function.Validator.Validate(func, vctx);
            // V8Sharp: duplicate export names are a validation error (V8's
            // module decoder, "Duplicate export name"); WACS rejected them at
            // instantiation.
            var exportNames = new System.Collections.Generic.Dictionary<string, Module.Export>();
            foreach (var export in module.Exports)
            {
                Module.Export.Validator.Validate(export, vctx);
                if (exportNames.TryGetValue(export.Name, out var first))
                    throw new ValidationException(
                        $"Duplicate export name '{TruncatedUserString(export.Name)}' for {ExportKind(first.Desc)} {ExportIndex(first.Desc)} and {ExportKind(export.Desc)} {ExportIndex(export.Desc)}");
                exportNames[export.Name] = export;
            }
            foreach (var elem in module.Elements)
                Module.ElementSegment.Validator.Validate(elem, vctx);
            foreach (var data in module.Datas)
                Module.Data.Validator.Validate(data, vctx);

            if (module.StartIndex.Value < module.Funcs.Count)
            {
                var idx = module.StartIndex;
                if (!vctx.Funcs.Contains(idx))
                    throw new ValidationException($"Invalid Start function index {idx.Value}");
                var typeIndex = vctx.Funcs[idx].TypeIndex;
                var type = vctx.Types[typeIndex].Expansion;
                //TODO: handle any type

                if (type is FunctionType funcType)
                {
                    if (funcType.ParameterTypes.Arity != 0 || funcType.ResultType.Arity != 0)
                    {
                        throw new ValidationException($"Invalid Start function with type: {type}");
                    }
                }
            }
        }
    }
}
