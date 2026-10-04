// Copyright 2026 Curiosity GmbH
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

// V8Sharp: WACS built its module validator on FluentValidation, whose
// ValidationException the instruction validators throw. The validators are
// plain code now (each Validate method throws at the first failure, as V8's
// decoder reports the first error), and this is the exception they throw.

using System;

namespace Wacs.Core.Validation
{
    /// <summary>A module failed validation (@Spec 3).</summary>
    public class ValidationException : Exception
    {
        public ValidationException(string message) : base(message) { }

        public ValidationException(string message, Exception inner) : base(message, inner) { }

        /// <summary>
        /// The index of the function whose body failed to validate, or -1
        /// when the failure is not in a function body.
        /// </summary>
        public int FunctionIndex { get; set; } = -1;

        /// <summary>The mnemonic of the instruction that failed, if any.</summary>
        public string? Instruction { get; set; }

        /// <summary>V8Sharp: the module offset of the instruction that failed, or -1.</summary>
        public int Offset { get; set; } = -1;
    }
}
