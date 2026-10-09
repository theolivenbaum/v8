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

// V8Sharp: the frames of compiled wasm code (V8Sharp's wasm compiler, in the
// engine project). Compiled functions run on the .NET stack, not on the
// interpreter's call stack, so each one records its function and, before
// each call, its position here, as V8's frames hold a pc. Stack traces merge
// these frames with the interpreter's: every entry into compiled code from
// the interpreter (or from the embedder) opens a segment that records the
// interpreter's call-stack height at that point.

using System;
using System.Runtime.CompilerServices;
using Wacs.Core.Instructions;

namespace Wacs.Core.Runtime
{
    /// <summary>
    /// The compiled-code half of the wasm call stack. Compiled code reads and
    /// writes the arrays directly; everything else goes through the methods.
    /// </summary>
    public interface ICompiledFunctionCode
    {
        /// <summary>
        /// Runs the function with its arguments on <paramref name="context"/>'s
        /// operand stack and pushes its results (the interpreter calling
        /// compiled code).
        /// </summary>
        void InvokeFromInterpreter(ExecContext context);
    }

    public sealed class CompiledFrames
    {
        /// <summary>The deepest compiled call stack (with the interpreter's: V8's stack limit).</summary>
        public const int MaxFrames = 32768;

        /// <summary>The store address of each frame's function.</summary>
        public readonly int[] Func = new int[MaxFrames + 1];

        /// <summary>
        /// Each frame's position: the linked instruction index of the call it
        /// is in (or of the trap), as the interpreter's frames record it.
        /// </summary>
        public readonly int[] Pc = new int[MaxFrames + 1];

        /// <summary>The number of compiled frames.</summary>
        public int Sp;

        /// <summary>The frame count at which entering another frame overflows.</summary>
        public int Limit = MaxFrames;

        /// <summary>Extra results of a multi-value return (results 1..n-1; result 0 is the return value).</summary>
        public Value[] Returns = new Value[16];

        // Segments: entries into compiled code from the interpreter or the
        // embedder, in order.
        int[] _segStart = new int[64];
        int[] _segWacsHeight = new int[64];
        int[] _segCallerPc = new int[64];
        int _segCount;

        public int SegmentCount => _segCount;

        /// <summary>Opens a segment at the current frame count (an entry into compiled code).</summary>
        public void PushSegment(int wacsHeight, int callerPc)
        {
            if (_segCount == _segStart.Length)
            {
                Array.Resize(ref _segStart, _segCount * 2);
                Array.Resize(ref _segWacsHeight, _segCount * 2);
                Array.Resize(ref _segCallerPc, _segCount * 2);
            }
            _segStart[_segCount] = Sp;
            _segWacsHeight[_segCount] = wacsHeight;
            _segCallerPc[_segCount] = callerPc;
            _segCount++;
        }

        /// <summary>Closes segments down to <paramref name="count"/> (an entry returned or threw).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void PopSegments(int count) => _segCount = count;

        public Value[] EnsureReturns(int count)
        {
            if (Returns.Length < count) Returns = new Value[Math.Max(count, Returns.Length * 2)];
            return Returns;
        }

        /// <summary>
        /// The merged frames (interpreted and compiled), bottom first:
        /// for each, whether it is compiled and its index on its own stack.
        /// </summary>
        internal (bool Compiled, int Index, int CallerPc)[] Merge(int wacsCount)
        {
            var merged = new (bool, int, int)[wacsCount + Sp];
            int n = 0, wi = 0;
            for (int s = 0; s < _segCount; s++)
            {
                int start = _segStart[s];
                int end = s + 1 < _segCount ? _segStart[s + 1] : Sp;
                if (start >= Sp) break;
                if (end > Sp) end = Sp;
                int height = Math.Min(_segWacsHeight[s], wacsCount);
                while (wi < height) merged[n++] = (false, wi++, -1);
                for (int c = start; c < end; c++)
                {
                    // The first frame of a segment records the pc of the
                    // interpreted frame below it (its caller).
                    merged[n++] = (true, c, c == start ? _segCallerPc[s] : -1);
                }
            }
            while (wi < wacsCount) merged[n++] = (false, wi++, -1);
            if (n != merged.Length) Array.Resize(ref merged, n);
            return merged;
        }
    }
}
