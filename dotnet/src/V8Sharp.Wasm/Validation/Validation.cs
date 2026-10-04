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
            foreach (var export in module.Exports)
                Module.Export.Validator.Validate(export, vctx);
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
