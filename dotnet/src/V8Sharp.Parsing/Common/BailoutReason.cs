// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of BailoutReason in src/codegen/bailout-reason.h.

namespace V8Sharp.Common;

public enum BailoutReason : byte
{
    kNoReason,
    kFunctionTooBig,
    kTooManyArguments,
    kNativeFunctionLiteral,
    kNeverOptimize,
    kMaglevGraphBuildingFailed,
    kMaglevCodeGenerationFailed,
    kTurbofanGraphBuildingFailed,
    kTurbofanCodeGenerationFailed,
    kBailedOutDueToDependencyChange,
    kConcurrentMapDeprecation,
    kDetachedNativeContext,
    kCancelled,
    kLastErrorMessage,
}
