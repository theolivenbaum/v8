// Copyright 2025 Kelvin Nishikawa
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

using System.IO;
using Wacs.Core.Validation;
using Wacs.Core.Utilities;

namespace Wacs.Core.Types.Defs
{
    public class CatchType
    {
        public LabelIdx L;
        public CatchFlags Mode;
        public TagIdx X;

        public CatchType(CatchFlags mode, TagIdx x, LabelIdx l)
        {
            Mode = mode;
            X = x;
            L = l;
        }

        public CatchType(CatchFlags mode, LabelIdx l)
        {
            Mode = mode;
            L = l;
        }

        public static CatchType Parse(BinaryReader reader)
        {
            return (CatchFlags)reader.ReadByte() switch
            {
                CatchFlags.None => new CatchType(CatchFlags.None, (TagIdx)reader.ReadLeb128_u32(), (LabelIdx)reader.ReadLeb128_u32()),
                CatchFlags.CatchRef => new CatchType(CatchFlags.CatchRef, (TagIdx)reader.ReadLeb128_u32(), (LabelIdx)reader.ReadLeb128_u32()),
                CatchFlags.CatchAll => new CatchType(CatchFlags.CatchAll, (LabelIdx)reader.ReadLeb128_u32()),
                CatchFlags.CatchAllRef => new CatchType(CatchFlags.CatchAllRef, (LabelIdx)reader.ReadLeb128_u32()),
                _ => throw new InvalidDataException($"Invalid catch type")                
            };
        }


        // V8Sharp: plain code instead of a FluentValidation validator.
        public static class Validator
        {
            public static void Validate(CatchType ct, WasmValidationContext vContext)
            {
                if (ct.Mode is CatchFlags.None or CatchFlags.CatchRef && !vContext.Tags.Contains(ct.X))
                    throw new ValidationException($"Validation context did not contain Catch Tag Index {ct.X.Value}");
                if (!vContext.ContainsLabel(ct.L.Value))
                    throw new ValidationException($"Validation context did not contain Catch Label Index {ct.L.Value}");

                var labelIdx = ct.L;
                switch (ct.Mode)
                {
                    case CatchFlags.None:
                    case CatchFlags.CatchRef:
                    {
                        var tag = vContext.Tags[ct.X];
                        var typeIdx = tag.TypeIndex;
                        if (!vContext.Types.Contains(typeIdx))
                            throw new ValidationException($"Validation context did not contain Catch Tag Type Index {typeIdx.Value}");
                        var compType = vContext.Types[typeIdx].Expansion;
                        if (compType is not FunctionType functionType)
                            throw new ValidationException($"Catch Tag Type Index {typeIdx.Value} was not a FunctionType");
                        if (functionType.ResultType.Arity != 0)
                            throw new ValidationException($"Catch Tag Type Index {typeIdx.Value} had non-empty result type");
                        var controlFrame = vContext.ControlStack.PeekAt((int)labelIdx.Value);
                        // Spec: catch_ref appends a NON-NULLABLE (ref exn) to
                        // the tag's params; the captured exn is always present.
                        var pType = ct.Mode == CatchFlags.CatchRef
                            ? functionType.ParameterTypes.Append(ValType.ExnNN)
                            : functionType.ParameterTypes;
                        if (!pType.Matches(controlFrame.EndTypes, vContext.Types))
                            throw new ValidationException($"Catch Label {controlFrame.EndTypes.ToNotation()} did not match Catch Tag {functionType.ParameterTypes.ToNotation()}");
                        break;
                    }
                    case CatchFlags.CatchAll:
                    {
                        var controlFrame = vContext.ControlStack.PeekAt((int)labelIdx.Value);
                        if (controlFrame.StartTypes.Arity != 0)
                            throw new ValidationException($"Catch Label {labelIdx.Value} had non-empty start type");
                        break;
                    }
                    case CatchFlags.CatchAllRef:
                    {
                        var controlFrame = vContext.ControlStack.PeekAt((int)labelIdx.Value);
                        // Spec: catch_all_ref puts a NON-NULLABLE (ref exn) on
                        // the label's stack — the captured exn ref is always
                        // present in the catch handler.
                        var resultType = new ResultType(ValType.ExnNN);
                        if (controlFrame.StartTypes.Matches(resultType, vContext.Types))
                            throw new ValidationException($"Catch Label {labelIdx.Value} had non-exnref start type");
                        break;
                    }
                }
            }
        }
    }
}