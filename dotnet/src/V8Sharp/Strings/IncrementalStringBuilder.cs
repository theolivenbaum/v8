// Port of IncrementalStringBuilder (src/strings/string-builder-inl.h,
// string-builder.cc). V8 accumulates parts into cons strings; V8Sharp appends
// into a pooled char buffer and allocates the result once. Overflow past
// String::kMaxLength is deferred to Finish(), as in V8.
using System.Buffers;
using System.Globalization;
using V8Sharp.Common;
using V8Sharp.Objects;
using V8Sharp.Roots;

namespace V8Sharp;

/// <summary>V8's IncrementalStringBuilder.</summary>
public sealed class IncrementalStringBuilder(Isolate isolate)
{
    readonly Isolate _isolate = isolate;
    char[] _buffer = ArrayPool<char>.Shared.Rent(64);
    int _length;
    bool _overflowed;

    public int Length => _length;

    /// <summary>True once the result would exceed String::kMaxLength.</summary>
    public bool HasOverflowed => _overflowed;

    void EnsureCapacity(int additional)
    {
        int needed = _length + additional;
        if (needed > JSString.kMaxLength)
        {
            _overflowed = true;
            return;
        }
        if (needed <= _buffer.Length) return;
        int newSize = Math.Max(needed, _buffer.Length * 2);
        char[] next = ArrayPool<char>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _length).CopyTo(next);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void AppendCharacter(char c)
    {
        if (_overflowed) return;
        EnsureCapacity(1);
        if (_overflowed) return;
        _buffer[_length++] = c;
    }

    public void AppendCStringLiteral(string literal) => AppendChars(literal);

    public void AppendCString(string s) => AppendChars(s);

    public void AppendChars(ReadOnlySpan<char> chars)
    {
        if (_overflowed) return;
        EnsureCapacity(chars.Length);
        if (_overflowed) return;
        chars.CopyTo(_buffer.AsSpan(_length));
        _length += chars.Length;
    }

    public void AppendString(JSString s)
    {
        if (s.Length == 0 || _overflowed) return;
        AppendChars(s.FlatSpan());
    }

    public void AppendInt(int i)
    {
        Span<char> tmp = stackalloc char[16];
        i.TryFormat(tmp, out int written, default, CultureInfo.InvariantCulture);
        AppendChars(tmp[..written]);
    }

    /// <summary>IncrementalStringBuilder::AppendStringCapped: at most max_length chars, then "&lt;...&gt;".</summary>
    public void AppendStringCapped(JSString s, int maxLength)
    {
        if (s.Length <= maxLength)
        {
            AppendString(s);
            return;
        }
        AppendChars(s.FlatSpan()[..maxLength]);
        AppendCStringLiteral("<...>");
    }

    /// <summary>
    /// IncrementalStringBuilder::Finish: the built string; throws
    /// "RangeError: Invalid string length" if it grew past String::kMaxLength.
    /// </summary>
    public JSString Finish()
    {
        char[] buffer = _buffer;
        int length = _length;
        _buffer = [];
        _length = 0;
        try
        {
            if (_overflowed)
            {
                _isolate.Throw(_isolate.Factory.NewInvalidStringLengthError());
            }
            if (length == 0) return ReadOnlyRoots.empty_string;
            if (length == 1) return _isolate.Factory.LookupSingleCharacterStringFromCode(buffer[0]);
            return new SeqString(new string(buffer, 0, length));
        }
        finally
        {
            if (buffer.Length > 0) ArrayPool<char>.Shared.Return(buffer);
        }
    }

    public override string ToString() => new(_buffer, 0, _length);
}
