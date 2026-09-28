// Copyright 2015 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/pending-compilation-error-handler.h and .cc.
//
// The engine turns the pending error into a SyntaxError
// (PendingCompilationErrorHandler::ReportErrors / ThrowPendingError) with
// message(), start_pos(), end_pos() and the argument strings; V8's
// argument-string handles are the arguments' contents here.

using V8Sharp.Ast;
using V8Sharp.Common;

namespace V8Sharp.Parsing;

public sealed class PendingCompilationErrorHandler
{
    public sealed class MessageDetails
    {
        public const int kMaxArgumentCount = 3;

        private readonly int _startPosition;
        private readonly int _endPosition;
        private readonly MessageTemplate _message;
        private readonly string?[] _args = new string?[kMaxArgumentCount];

        public MessageDetails()
        {
            _startPosition = -1;
            _endPosition = -1;
            _message = MessageTemplate.None;
        }

        public MessageDetails(int start_position, int end_position, MessageTemplate message, string? arg0 = null,
                              string? arg1 = null, string? arg2 = null)
        {
            _startPosition = start_position;
            _endPosition = end_position;
            _message = message;
            _args[0] = arg0;
            _args[1] = arg1;
            _args[2] = arg2;
        }

        // The argument string, or null if there are fewer arguments
        // (V8: ArgString(isolate, index)).
        public string? ArgString(int index) => _args[index];

        public int ArgCount()
        {
            int argc = 0;
            for (int i = 0; i < kMaxArgumentCount; i++)
            {
                if (_args[i] == null) break;
                argc++;
            }
            return argc;
        }

        public int start_pos() => _startPosition;
        public int end_pos() => _endPosition;
        public MessageTemplate message() => _message;

        // The arguments as MessageFormatter::Format takes them.
        public string[] Args()
        {
            int argc = ArgCount();
            var result = new string[argc];
            for (int i = 0; i < argc; i++) result[i] = _args[i]!;
            return result;
        }
    }

    private bool _hasPendingError;
    private bool _stackOverflow;
    private bool _unidentifiableError;

    private MessageDetails _errorDetails = new();

    // V8 keeps a std::forward_list and emplaces at the front, so iteration is
    // newest first; the list here is in that order too.
    private readonly List<MessageDetails> _warningMessages = [];

    private void ReportMessageAt(MessageDetails details)
    {
        if (_hasPendingError && details.end_pos() >= _errorDetails.start_pos()) return;

        _hasPendingError = true;
        _errorDetails = details;
    }

    public void ReportMessageAt(int start_position, int end_position, MessageTemplate message, string? arg = null)
    {
        if (_hasPendingError && end_position >= _errorDetails.start_pos()) return;
        _hasPendingError = true;
        _errorDetails = new MessageDetails(start_position, end_position, message, arg);
    }

    public void ReportMessageAt(int start_position, int end_position, MessageTemplate message, AstRawString? arg)
    {
        if (_hasPendingError && end_position >= _errorDetails.start_pos()) return;
        _hasPendingError = true;
        _errorDetails = new MessageDetails(start_position, end_position, message, arg?.Value);
    }

    public void ReportMessageAt(int start_position, int end_position, MessageTemplate message, AstRawString arg0, string arg1)
    {
        if (_hasPendingError && end_position >= _errorDetails.start_pos()) return;
        _hasPendingError = true;
        _errorDetails = new MessageDetails(start_position, end_position, message, arg0.Value, arg1);
    }

    public void ReportMessageAt(int start_position, int end_position, MessageTemplate message, AstRawString arg0,
                                AstRawString arg1, string arg2)
    {
        if (_hasPendingError && end_position >= _errorDetails.start_pos()) return;
        _hasPendingError = true;
        _errorDetails = new MessageDetails(start_position, end_position, message, arg0.Value, arg1.Value, arg2);
    }

    public void ReportWarningAt(int start_position, int end_position, MessageTemplate message, string? arg = null)
    {
        _warningMessages.Insert(0, new MessageDetails(start_position, end_position, message, arg));
    }

    public bool stack_overflow() => _stackOverflow;

    public void set_stack_overflow()
    {
        _hasPendingError = true;
        _stackOverflow = true;
    }

    public bool has_pending_error() => _hasPendingError;
    public bool has_pending_warnings() => _warningMessages.Count != 0;

    public void set_unidentifiable_error()
    {
        _hasPendingError = true;
        _unidentifiableError = true;
    }

    public void clear_unidentifiable_error()
    {
        _hasPendingError = false;
        _unidentifiableError = false;
    }

    public bool has_error_unidentifiable_by_preparser() => _unidentifiableError;

    // The pending error (valid when has_pending_error() and !stack_overflow()).
    public MessageDetails error_details() => _errorDetails;

    public IReadOnlyList<MessageDetails> warning_messages() => _warningMessages;

    // PendingCompilationErrorHandler::FormatErrorMessageForTest: the message
    // text of the pending error.
    public string FormatErrorMessageForTest() =>
        MessageFormatter.Format(_errorDetails.message(), _errorDetails.Args());
}
