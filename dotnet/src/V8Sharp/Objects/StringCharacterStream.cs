// Port of ConsStringIterator (src/objects/string.h, string.cc, string-inl.h)
// and StringCharacterStream (src/objects/string-inl.h): traversal of a rope
// without flattening it.
using System.Runtime.CompilerServices;

namespace V8Sharp.Objects;

/// <summary>
/// ConsStringIterator: yields the flat leaves of a ConsString tree from left to
/// right, keeping a bounded stack of frames and restarting the search from the
/// root when the stack wraps (so arbitrarily deep ropes need no recursion).
/// </summary>
public sealed class ConsStringIterator
{
    const int kStackSize = 32;
    // Use a mask instead of doing modulo operations for stack wrapping.
    const int kDepthMask = kStackSize - 1;

    // Stack must always contain only frames for which right traversal
    // has not yet been performed.
    readonly ConsString?[] _frames = new ConsString?[kStackSize];
    ConsString? _root;
    int _depth;
    int _maximumDepth;
    int _consumed;

    public ConsStringIterator() { }

    public ConsStringIterator(ConsString consString, int offset = 0) => Reset(consString, offset);

    public void Reset(ConsString? consString, int offset = 0)
    {
        _depth = 0;
        // Next will always return null.
        if (consString is null) return;
        Initialize(consString, offset);
    }

    /// <summary>
    /// Returns null when complete. <paramref name="offsetOut"/> is the offset within
    /// the returned segment that the user should start looking at, to match the
    /// offset passed into the constructor or Reset; it is non-zero only immediately
    /// after construction or Reset, and only if those had a non-zero offset.
    /// </summary>
    public JSString? Next(out int offsetOut)
    {
        offsetOut = 0;
        if (_depth == 0) return null;
        return Continue(ref offsetOut);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static int OffsetForDepth(int depth) => depth & kDepthMask;

    void PushLeft(ConsString s) => _frames[_depth++ & kDepthMask] = s;

    // Inplace update.
    void PushRight(ConsString s) => _frames[(_depth - 1) & kDepthMask] = s;

    void AdjustMaximumDepth()
    {
        if (_depth > _maximumDepth) _maximumDepth = _depth;
    }

    void Pop()
    {
        Debug.Assert(_depth > 0 && _depth <= _maximumDepth);
        _depth--;
    }

    bool StackBlown() => _maximumDepth - _depth == kStackSize;

    // A flattened ConsString keeps the flat result as its first child and drops
    // the second (V8 stores the empty string there).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static JSString SecondOf(ConsString s) => s.Second ?? ReadOnlyRoots.empty_string;

    void Initialize(ConsString consString, int offset)
    {
        _root = consString;
        _consumed = offset;
        // Force stack blown condition to trigger restart.
        _depth = 1;
        _maximumDepth = kStackSize + _depth;
        Debug.Assert(StackBlown());
    }

    JSString? Continue(ref int offsetOut)
    {
        Debug.Assert(_depth != 0 && offsetOut == 0);
        bool blewStack = StackBlown();
        JSString? str = null;
        // Get the next leaf if there is one.
        if (!blewStack) str = NextLeaf(out blewStack);
        // Restart search from root.
        if (blewStack)
        {
            Debug.Assert(str is null);
            str = Search(out offsetOut);
        }
        // Ensure future calls return null immediately.
        if (str is null) Reset(null);
        return str;
    }

    JSString? Search(out int offsetOut)
    {
        offsetOut = 0;
        ConsString consString = _root!;
        // Reset the stack, pushing the root string.
        _depth = 1;
        _maximumDepth = 1;
        _frames[0] = consString;
        int consumed = _consumed;
        int offset = 0;
        while (true)
        {
            // Loop until the string is found which contains the target offset.
            JSString str = consString.First;
            int length = str.Length;
            if (consumed < offset + length)
            {
                // Target offset is in the left branch.
                // Keep going if we're still in a ConString.
                if (str is ConsString leftCons)
                {
                    consString = leftCons;
                    PushLeft(consString);
                    continue;
                }
                // Tell the stack we're done descending.
                AdjustMaximumDepth();
            }
            else
            {
                // Descend right.
                // Update progress through the string.
                offset += length;
                // Keep going if we're still in a ConString.
                str = SecondOf(consString);
                if (str is ConsString rightCons)
                {
                    consString = rightCons;
                    PushRight(consString);
                    continue;
                }
                // Need this to be updated for the current string.
                length = str.Length;
                // Account for the possibility of an empty right leaf.
                // This happens only if we have asked for an offset outside the string.
                if (length == 0)
                {
                    // Reset so future operations will return null immediately.
                    Reset(null);
                    return null;
                }
                // Tell the stack we're done descending.
                AdjustMaximumDepth();
                // Pop stack so next iteration is in correct place.
                Pop();
            }
            Debug.Assert(length != 0);
            // Adjust return values and exit.
            _consumed = offset + length;
            offsetOut = consumed - offset;
            return str;
        }
    }

    JSString? NextLeaf(out bool blewStack)
    {
        while (true)
        {
            // Tree traversal complete.
            if (_depth == 0)
            {
                blewStack = false;
                return null;
            }
            // We've lost track of higher nodes.
            if (StackBlown())
            {
                blewStack = true;
                return null;
            }
            // Go right.
            ConsString consString = _frames[OffsetForDepth(_depth - 1)]!;
            JSString str = SecondOf(consString);
            if (str is not ConsString rightCons)
            {
                // Pop stack so next iteration is in correct place.
                Pop();
                int length = str.Length;
                // Could be a flattened ConsString.
                if (length == 0) continue;
                _consumed += length;
                blewStack = false;
                return str;
            }
            consString = rightCons;
            PushRight(consString);
            // Need to traverse all the way left.
            while (true)
            {
                // Continue left.
                str = consString.First;
                if (str is not ConsString leftCons)
                {
                    AdjustMaximumDepth();
                    int length = str.Length;
                    if (length == 0) break;  // Skip empty left-hand sides of ConsStrings.
                    _consumed += length;
                    blewStack = false;
                    return str;
                }
                consString = leftCons;
                PushLeft(consString);
            }
        }
    }
}

/// <summary>
/// StringCharacterStream: the code units of any string, in order, read through
/// a ConsStringIterator so ropes are not flattened.
/// </summary>
public sealed class StringCharacterStream
{
    readonly ConsStringIterator _iter = new();
    // V8 keeps a one-byte or two-byte pointer pair; every V8Sharp string is
    // UTF-16, so the cursor is a position in a .NET string.
    string _buffer = string.Empty;
    int _pos;
    int _end;

    public StringCharacterStream(JSString str, int offset = 0) => Reset(str, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort GetNext()
    {
        // Advance cursor if needed.
        if (_pos == _end) HasMore();
        Debug.Assert(_pos < _end);
        return _buffer[_pos++];
    }

    public void Reset(JSString str, int offset = 0)
    {
        _buffer = string.Empty;
        _pos = _end = 0;

        ConsString? consString = VisitFlat(str, offset);
        _iter.Reset(consString, offset);
        if (consString is not null)
        {
            JSString? leaf = _iter.Next(out offset);
            if (leaf is not null) VisitFlat(leaf, offset);
        }
    }

    public bool HasMore()
    {
        if (_pos != _end) return true;
        JSString? str = _iter.Next(out int offset);
        Debug.Assert(offset == 0);
        if (str is null) return false;
        VisitFlat(str, 0);
        Debug.Assert(_pos != _end);
        return true;
    }

    /// <summary>
    /// String::VisitFlat: points the cursor at the characters of a flat string,
    /// or returns the ConsString to iterate.
    /// </summary>
    ConsString? VisitFlat(JSString str, int offset)
    {
        switch (str)
        {
            case SeqString seq:
                Visit(seq.Value, offset, seq.Value.Length);
                return null;
            case SlicedString sliced:
                Visit(sliced.Parent.Flatten(), sliced.Offset + offset, sliced.Offset + sliced.Length);
                return null;
            case ConsString cons:
                return cons;
            default:
                string flat = str.Flatten();
                Visit(flat, offset, flat.Length);
                return null;
        }
    }

    void Visit(string chars, int start, int end)
    {
        _buffer = chars;
        _pos = start;
        _end = end;
    }
}
