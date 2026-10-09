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

// V8Sharp: the legacy exception handling instructions (try, catch, catch_all,
// delegate, rethrow), which WACS does not implement and V8 still accepts
// (V8's function-body-decoder-impl.h, kExprTry and friends).
//
// The flat layout is [try][body][catch x][handler]...[catch_all][handler][end],
// or [try][body][delegate l]. Like else, each catch is a BlockTarget that
// takes over the try's label while its handler is linked, so a label lookup
// inside a handler finds the catch, not the try: an exception thrown in a
// handler is not caught by the same try. InstThrowRef consults a try's
// handlers only when the throw is inside its body.

using System;
using System.Collections.Generic;
using System.IO;
using Wacs.Core.OpCodes;
using Wacs.Core.Runtime;
using Wacs.Core.Runtime.Exceptions;
using Wacs.Core.Runtime.Types;
using Wacs.Core.Types;
using Wacs.Core.Types.Defs;
using Wacs.Core.Utilities;
using Wacs.Core.Validation;
using InstructionPointer = System.Int32;

namespace Wacs.Core.Instructions
{
    //0x06
    public class InstTry : BlockTarget, IBlockInstruction, IExnHandler
    {
        public InstTry() : base(OpCode.Try) { }

        private readonly List<Block> _blocks = new();

        /// <summary>The catch and catch_all clauses, in order (set by Link).</summary>
        public readonly List<InstCatch> Handlers = new();

        /// <summary>The delegate ending this try, if any (set by Link).</summary>
        public InstDelegate? Delegate;

        public ValType BlockType => _blocks[0].BlockType;
        public int Count => _blocks.Count;
        public int BlockSize
        {
            get
            {
                int size = 1;
                foreach (var block in _blocks) size += block.Size;
                return size;
            }
        }
        public Block GetBlock(int idx) => _blocks[idx];

        private static bool IsBodyEnd(InstructionBase inst) => inst is InstEnd or InstCatch;

        public override InstructionBase Parse(BinaryReader reader)
        {
            var blockType = ValTypeParser.Parse(reader, parseBlockIndex: true, parseStorageType: false);
            _blocks.Clear();
            var seq = new InstructionSequence(reader.ParseUntil(BinaryModuleParser.ParseInstruction, IsBodyEnd));
            _blocks.Add(new Block(blockType, seq));
            while (seq.Count > 0 && seq.LastInstruction is InstCatch)
            {
                seq = new InstructionSequence(reader.ParseUntil(BinaryModuleParser.ParseInstruction, IsBodyEnd));
                _blocks.Add(new Block(blockType, seq));
            }
            if (seq.Count == 0 || seq.LastInstruction is not InstEnd)
                throw new FormatException("Try block did not terminate correctly.");
            return this;
        }

        public override void RenderBinary(BinaryWriter writer) =>
            ValTypeWriter.WriteBlockType(writer, BlockType);

        public override void Validate(IWasmValidationContext context)
        {
            WasmValidationContext.DetectExceptionHandling(context, legacy: true);
            var funcType = context.Types.ResolveBlockType(BlockType);
            context.Assert(funcType, "Invalid BlockType: {0}", BlockType);
            context.OpStack.DiscardValues(funcType.ParameterTypes);
            context.PushControlFrame(OpCode.Try, funcType);
            // The body ends with catch, catch_all, delegate or end, and each
            // handler with catch, catch_all or end: they manage the frames.
            for (int i = 0; i < _blocks.Count; i++)
                context.ValidateBlock(_blocks[i], i);
        }

        public override InstructionBase Link(ExecContext context, InstructionPointer pointer)
        {
            // Instructions are shared between instances of a module and
            // relinked for each.
            Handlers.Clear();
            Delegate = null;
            return base.Link(context, pointer);
        }

        public override void Execute(ExecContext context) { }

        internal void SetEnd(InstructionPointer pointer)
        {
            End = pointer;
            foreach (var handler in Handlers)
                handler.End = pointer;
        }

        /// <summary>
        /// Enters <paramref name="handler"/> for the exception
        /// <paramref name="exnref"/> thrown in this try's body: the operand
        /// stack is cut back to the try's entry height and receives the tag's
        /// values (catch) or nothing (catch_all).
        /// </summary>
        internal void EnterHandler(ExecContext context, InstCatch handler, Value exnref, ExnInstance exn)
        {
            var frame = context.Frame;
            context.OpStack.Count = Label.StackHeight - Label.Parameters + frame.ReturnLabel.StackHeight;
            if (handler is not InstCatchAll)
                context.OpStack.PushResults(exn.Fields);
            (frame.CaughtExceptions ??= new Dictionary<InstTry, Value>())[this] = exnref;
            context.InstructionPointer = handler.Head;
        }
    }

    //0x07
    public class InstCatch : BlockTarget
    {
        public InstCatch() : base(OpCode.Catch) { }
        protected InstCatch(ByteCode op) : base(op) { }

        protected TagIdx Tag;
        public TagIdx X => Tag;

        public InstTry Try = null!;

        public override InstructionBase Parse(BinaryReader reader)
        {
            Tag = (TagIdx)reader.ReadLeb128_u32();
            return this;
        }

        public override void RenderBinary(BinaryWriter writer) =>
            writer.WriteLeb128_u32(Tag.Value);

        protected virtual ResultType ValidateParams(IWasmValidationContext context)
        {
            context.Assert(context.Tags.Contains(Tag), "Invalid tag index: {0}", Tag.Value);
            var tag = context.Tags[Tag];
            var functionType = context.Types[tag.TypeIndex].Expansion as FunctionType;
            context.Assert(functionType, "Tag {0} is not a function type.", Tag.Value);
            return functionType.ParameterTypes;
        }

        public override void Validate(IWasmValidationContext context)
        {
            WasmValidationContext.DetectExceptionHandling(context, legacy: true);
            var opcode = context.ControlFrame.Opcode;
            string name = this is InstCatchAll ? "catch-all" : "catch";
            context.Assert(opcode == OpCode.Try || opcode == OpCode.Catch || opcode == OpCode.CatchAll,
                "{0} does not match a try", name);
            context.Assert(opcode != OpCode.CatchAll,
                this is InstCatchAll ? "catch-all already present for try" : "catch after catch-all for try");
            var types = ValidateParams(context);
            var frame = context.PopControlFrame();
            context.PushControlFrame(Op, new FunctionType(ResultType.Empty, frame.Types.ResultType));
            context.OpStack.PushResult(types);
        }

        protected virtual int ParamCount(ExecContext context)
        {
            var tagType = context.Store[context.Frame.Module.TagAddrs[Tag]].Type;
            return ((FunctionType)tagType.Expansion).ParameterTypes.Arity;
        }

        public override InstructionBase Link(ExecContext context, InstructionPointer pointer)
        {
            var previous = context.PopLabel();
            Try = previous as InstTry ?? ((InstCatch)previous).Try;
            Try.Handlers.Add(this);

            Head = pointer;
            EnclosingBlock = Try.EnclosingBlock;
            Label = Try.Label;
            LabelHeight = Try.LabelHeight;
            context.PushLabel(this);

            int height = Label.StackHeight - Label.Parameters + ParamCount(context);
            context.DeltaStack(height - context.LinkOpStackHeight, 0);
            context.LinkUnreachable = false;
            return this;
        }

        // Reached at the end of the body or of the previous handler: leave
        // the try.
        public override void Execute(ExecContext context)
        {
            context.InstructionPointer = End - 1;
        }
    }

    //0x19
    public sealed class InstCatchAll : InstCatch
    {
        public InstCatchAll() : base(OpCode.CatchAll) { }

        public override InstructionBase Parse(BinaryReader reader) => this;

        public override void RenderBinary(BinaryWriter writer) { }

        protected override ResultType ValidateParams(IWasmValidationContext context) => ResultType.Empty;

        protected override int ParamCount(ExecContext context) => 0;
    }

    //0x18
    public sealed class InstDelegate : InstEnd
    {
        public InstDelegate() : base(OpCode.Delegate) { }

        private LabelIdx L;

        /// <summary>The block whose handlers the exception is delegated to.</summary>
        public BlockTarget? DelegateTarget;

        public override InstructionBase Parse(BinaryReader reader)
        {
            L = (LabelIdx)reader.ReadLeb128_u32();
            return this;
        }

        public override void RenderBinary(BinaryWriter writer) =>
            writer.WriteLeb128_u32(L.Value);

        public override void Validate(IWasmValidationContext context)
        {
            WasmValidationContext.DetectExceptionHandling(context, legacy: true);
            context.Assert(context.ControlFrame.Opcode == OpCode.Try, "delegate does not match a try");
            var frame = context.PopControlFrame();
            context.Assert(context.ControlStack.Count - 1 >= L.Value, "invalid branch depth: {0}", L.Value);
            context.OpStack.ReturnResults(frame.EndTypes);
        }

        public override InstructionBase Link(ExecContext context, InstructionPointer pointer)
        {
            var target = context.PeekLabel() as InstTry
                         ?? throw new InstantiationException("delegate does not end a try");
            _ = base.Link(context, pointer);
            target.Delegate = this;
            DelegateTarget = InstBranch.PrecomputeStack(context, L);
            return this;
        }
    }

    //0x09
    public sealed class InstRethrow : InstructionBase
    {
        public InstRethrow() : base(OpCode.Rethrow) { }

        private LabelIdx L;
        private BlockTarget? _target;

        public override InstructionBase Parse(BinaryReader reader)
        {
            L = (LabelIdx)reader.ReadLeb128_u32();
            return this;
        }

        public override void RenderBinary(BinaryWriter writer) =>
            writer.WriteLeb128_u32(L.Value);

        public override void Validate(IWasmValidationContext context)
        {
            WasmValidationContext.DetectExceptionHandling(context, legacy: true);
            context.Assert(context.ContainsLabel(L.Value), "invalid branch depth: {0}", L.Value);
            var opcode = context.ControlStack.PeekAt((int)L.Value).Opcode;
            context.Assert(opcode == OpCode.Catch || opcode == OpCode.CatchAll,
                "rethrow not targeting catch or catch-all");
            context.SetUnreachable();
        }

        public override InstructionBase Link(ExecContext context, InstructionPointer pointer)
        {
            _target = InstBranch.PrecomputeStack(context, L);
            context.LinkUnreachable = true;
            return base.Link(context, pointer);
        }

        public override void Execute(ExecContext context)
        {
            var tryInst = ((InstCatch)_target!).Try;
            var exnref = context.Frame.CaughtExceptions![tryInst];
            context.OpStack.PushValue(exnref);
            InstThrowRef.ExecuteInstruction(context, this);
        }
    }
}
