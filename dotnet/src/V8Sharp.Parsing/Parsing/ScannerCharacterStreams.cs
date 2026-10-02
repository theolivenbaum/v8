// Copyright 2011 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of Utf16CharacterStream (src/parsing/scanner.h) and of
// src/parsing/scanner-character-streams.h/.cc.
//
// V8's buffer_start_/buffer_cursor_/buffer_end_ pointers become indices into
// `buffer_`. Positions are UTF-16 code units, as in V8. Of V8's streams only
// the ones that read UTF-16 or Latin-1 data from memory are ported
// (Unbuffered/BufferedCharacterStream over a TestingStream or a string, and
// the chunked variants used by the stream tests); the UTF-8 and
// Windows-1252 streaming decoders of the embedder streaming API are not.

using System.Runtime.CompilerServices;

namespace V8Sharp.Parsing;

// Checks used by Utf16CharacterStream.AdvanceUntil; a struct so the loop is
// specialized per check (V8 passes a lambda to a template).
public interface IAdvanceCheck
{
    bool Check(int c0);
}

// A check that also receives each contiguous range of skipped code units
// (V8's AdvanceUntilRange on_range callback).
public interface IAdvanceRangeCheck : IAdvanceCheck
{
    void OnRange(ReadOnlySpan<char> range);
}

// ---------------------------------------------------------------------
// Buffered stream of UTF-16 code units, using an internal UTF-16 buffer.
// A code unit is a 16 bit value representing either a 16 bit code point
// or one part of a surrogate pair that make a single 21 bit code point.
public abstract class Utf16CharacterStream
{
    public const int kEndOfInput = -1;

    private static readonly char[] s_emptyBuffer = [(char)0];

    // Fields describing the location of the current buffer physically in memory,
    // and semantically within the source string.
    //
    //                  0              buffer_pos_   pos()
    //                  |                        |   |
    //                  v________________________v___v_____________
    //                  |                        |        |        |
    //   Source string: |                        | Buffer |        |
    //                  |________________________|________|________|
    //                                           ^   ^    ^
    //                                           |   |    |
    //                   Pointers:   buffer_start_   |    buffer_end_
    //                                         buffer_cursor_
    protected internal char[] buffer_ = s_emptyBuffer;
    protected internal int buffer_start_;
    protected internal int buffer_cursor_;
    protected internal int buffer_end_;
    protected internal int buffer_pos_;
    private bool _hasParserError;

    protected Utf16CharacterStream() { }

    protected Utf16CharacterStream(char[] buffer, int buffer_start, int buffer_cursor, int buffer_end, int buffer_pos)
    {
        buffer_ = buffer;
        buffer_start_ = buffer_start;
        buffer_cursor_ = buffer_cursor;
        buffer_end_ = buffer_end;
        buffer_pos_ = buffer_pos;
    }

    protected static char[] EmptyBuffer => s_emptyBuffer;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void set_parser_error()
    {
        // source_pos() returns one previous position of the cursor.
        // Offset 1 cancels this out and makes it return exactly buffer_end_.
        buffer_cursor_ = buffer_end_ + 1;
        _hasParserError = true;
    }

    public void reset_parser_error_flag() => _hasParserError = false;

    public bool has_parser_error() => _hasParserError;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Peek()
    {
        if (buffer_cursor_ < buffer_end_)
        {
            return buffer_[buffer_cursor_];
        }
        else if (ReadBlockChecked(pos()))
        {
            return buffer_[buffer_cursor_];
        }
        else
        {
            return kEndOfInput;
        }
    }

    // Returns and advances past the next UTF-16 code unit in the input
    // stream. If there are no more code units it returns kEndOfInput.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Advance()
    {
        int result = Peek();
        buffer_cursor_++;
        return result;
    }

    // Returns and advances past the next UTF-16 code unit in the input stream
    // that meets the checks requirement. If there are no more code units it
    // returns kEndOfInput.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AdvanceUntil<TCheck>(ref TCheck check) where TCheck : struct, IAdvanceCheck
    {
        while (true)
        {
            char[] buffer = buffer_;
            int end = buffer_end_;
            int i = buffer_cursor_;
            // A cursor past the end (after set_parser_error) behaves like
            // find_if on an empty range.
            if (i > end) i = end;
            for (; i < end; i++)
            {
                if (check.Check(buffer[i])) break;
            }

            if (i == end)
            {
                buffer_cursor_ = end;
                if (!ReadBlockChecked(pos()))
                {
                    buffer_cursor_++;
                    return kEndOfInput;
                }
            }
            else
            {
                buffer_cursor_ = i + 1;
                return buffer[i];
            }
        }
    }

    // Like AdvanceUntil, but additionally reports each contiguous range of
    // skipped-over (i.e. {check} returned false) code units via
    // {OnRange} before the underlying buffer is refilled and before returning.
    // The code unit that terminated the scan is not part of the range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int AdvanceUntilRange<TCheck>(ref TCheck check) where TCheck : struct, IAdvanceRangeCheck
    {
        while (true)
        {
            char[] buffer = buffer_;
            int end = buffer_end_;
            int rangeStart = buffer_cursor_;
            if (rangeStart > end) rangeStart = end;
            int i = rangeStart;
            for (; i < end; i++)
            {
                if (check.Check(buffer[i])) break;
            }

            if (i != rangeStart)
            {
                check.OnRange(new ReadOnlySpan<char>(buffer, rangeStart, i - rangeStart));
            }

            if (i == end)
            {
                buffer_cursor_ = end;
                if (!ReadBlockChecked(pos()))
                {
                    buffer_cursor_++;
                    return kEndOfInput;
                }
            }
            else
            {
                buffer_cursor_ = i + 1;
                return buffer[i];
            }
        }
    }

    // Go back one by one character in the input stream.
    // This undoes the most recent Advance().
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Back()
    {
        // The common case - if the previous character is within
        // buffer_start_ .. buffer_end_ will be handles locally.
        // Otherwise, a new block is requested.
        if (buffer_cursor_ > buffer_start_)
        {
            buffer_cursor_--;
        }
        else
        {
            ReadBlockChecked(pos() - 1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int pos() => buffer_pos_ + (buffer_cursor_ - buffer_start_);

    public void Seek(int pos)
    {
        if (pos >= buffer_pos_ && pos < (buffer_pos_ + (buffer_end_ - buffer_start_)))
        {
            buffer_cursor_ = buffer_start_ + (pos - buffer_pos_);
        }
        else
        {
            ReadBlockChecked(pos);
        }
    }

    // Returns true if the stream could access the V8 heap after construction.
    public bool can_be_cloned_for_parallel_access() => can_be_cloned() && !can_access_heap();

    // Returns true if the stream can be cloned with Clone.
    public abstract bool can_be_cloned();

    // Clones the character stream to enable another independent scanner to access
    // the same underlying stream.
    public abstract Utf16CharacterStream Clone();

    // Returns true if the stream could access the V8 heap after construction.
    public abstract bool can_access_heap();

    protected bool ReadBlockChecked(int position)
    {
        bool success = !has_parser_error() && ReadBlock(position);

        // Post-conditions: 1, We should always be at the right position.
        //                  2, Cursor should be inside the buffer.
        //                  3, We should have more characters available iff success.
        System.Diagnostics.Debug.Assert(pos() == position || has_parser_error());
        return success;
    }

    // Read more data, and update buffer_*_ to point to it.
    // Returns true if more data was available.
    //
    // ReadBlock(position) may modify any of the buffer_*_ members, but must make
    // sure that the result of pos() becomes |position|.
    protected abstract bool ReadBlock(int position);
}

// A random-access source of code units (V8's ByteStream<Char> template
// parameter: OnHeapStream, ExternalStringStream, TestingStream,
// ChunkedStream). GetDataAt returns the contiguous range available at `pos`.
public interface ICharacterSource
{
    // Returns (array, start, length) of the data at pos; length 0 at the end.
    (char[] Data, int Start, int Length) GetDataAt(int pos);
    bool CanBeCloned { get; }
    bool CanAccessHeap { get; }
    ICharacterSource CloneSource();
}

// A Char stream backed by an array, optionally a window [start_offset,
// start_offset + end) of it. Covers V8's TestingStream, OnHeapStream and
// ExternalStringStream.
public sealed class ArrayCharacterSource(char[] data, int start_offset, int length, bool can_access_heap = false) : ICharacterSource
{
    public (char[] Data, int Start, int Length) GetDataAt(int pos)
    {
        int p = Math.Min(length, pos);
        return (data, start_offset + p, length - p);
    }

    // OnHeapStream (can_access_heap) is relocatable and cannot be cloned;
    // ExternalStringStream and TestingStream can.
    public bool CanBeCloned => !can_access_heap;
    public bool CanAccessHeap => can_access_heap;
    public ICharacterSource CloneSource() => this;
}

// Provides an unbuffered utf-16 view on the characters from the underlying
// source.
public sealed class UnbufferedCharacterStream : Utf16CharacterStream
{
    private readonly ICharacterSource _source;

    public UnbufferedCharacterStream(int pos, ICharacterSource source)
    {
        _source = source;
        buffer_pos_ = pos;
    }

    public override bool can_access_heap() => _source.CanAccessHeap;
    public override bool can_be_cloned() => _source.CanBeCloned;
    // V8's copy constructor copies only the byte stream: the clone starts at 0.
    public override Utf16CharacterStream Clone() => new UnbufferedCharacterStream(0, _source.CloneSource());

    protected override bool ReadBlock(int position)
    {
        buffer_pos_ = position;
        var (data, start, length) = _source.GetDataAt(position);
        if (length == 0)
        {
            buffer_ = EmptyBuffer;
            buffer_start_ = 0;
            buffer_end_ = 0;
            buffer_cursor_ = 0;
            return false;
        }
        buffer_ = data;
        buffer_start_ = start;
        buffer_end_ = start + length;
        buffer_cursor_ = start;
        return true;
    }
}

// Provides a buffered utf-16 view on the characters from the underlying
// source (V8 uses this for one-byte data, widening into a 512-unit buffer).
public sealed class BufferedCharacterStream : Utf16CharacterStream
{
    private const int kBufferSize = 512;
    private readonly char[] _buffer = new char[kBufferSize];
    private readonly ICharacterSource _source;

    public BufferedCharacterStream(int pos, ICharacterSource source)
    {
        _source = source;
        buffer_ = _buffer;
        buffer_pos_ = pos;
    }

    public override bool can_be_cloned() => _source.CanBeCloned;
    public override bool can_access_heap() => _source.CanAccessHeap;
    // V8's copy constructor copies only the byte stream: the clone starts at 0.
    public override Utf16CharacterStream Clone() => new BufferedCharacterStream(0, _source.CloneSource());

    protected override bool ReadBlock(int position)
    {
        buffer_pos_ = position;
        buffer_ = _buffer;
        buffer_start_ = 0;
        buffer_cursor_ = 0;

        var (data, start, length) = _source.GetDataAt(position);
        if (length == 0)
        {
            buffer_end_ = 0;
            return false;
        }

        int n = Math.Min(kBufferSize, length);
        Array.Copy(data, start, _buffer, 0, n);
        buffer_end_ = n;
        return true;
    }
}

// A source backed by multiple chunks, fetched on demand (V8's ChunkedStream
// over ScriptCompiler::ExternalSourceStream). Chunks are UTF-16.
public sealed class ChunkedCharacterSource : ICharacterSource
{
    private readonly Func<char[]?>? _getMoreData;
    private readonly List<(int Position, char[] Data)> _chunks;

    public ChunkedCharacterSource(Func<char[]?> getMoreData)
    {
        _getMoreData = getMoreData;
        _chunks = [];
    }

    // Cloned ChunkedStreams share the chunks and have a null source, and
    // therefore can't fetch any new data.
    private ChunkedCharacterSource(ChunkedCharacterSource other)
    {
        _getMoreData = null;
        _chunks = other._chunks;
    }

    public (char[] Data, int Start, int Length) GetDataAt(int pos)
    {
        var chunk = FindChunk(pos);
        int bufferEnd = chunk.Data.Length;
        int offset = pos - chunk.Position;
        if (offset > bufferEnd) offset = bufferEnd;
        return (chunk.Data, offset, bufferEnd - offset);
    }

    private (int Position, char[] Data) FindChunk(int position)
    {
        if (_chunks.Count == 0) FetchChunk(0);

        // Walk forwards while the position is in front of the current chunk.
        while (position >= _chunks[^1].Position + _chunks[^1].Data.Length && _chunks[^1].Data.Length > 0)
        {
            FetchChunk(_chunks[^1].Position + _chunks[^1].Data.Length);
        }

        // Walk backwards.
        for (int i = _chunks.Count - 1; i >= 0; i--)
        {
            if (_chunks[i].Position <= position) return _chunks[i];
        }
        throw new InvalidOperationException("UNREACHABLE");
    }

    private void FetchChunk(int position)
    {
        if (_getMoreData == null) throw new InvalidOperationException("cloned ChunkedStream cannot fetch data");
        char[] data = _getMoreData() ?? [];
        _chunks.Add((position, data));
    }

    public bool CanBeCloned => true;
    public bool CanAccessHeap => false;
    public ICharacterSource CloneSource() => new ChunkedCharacterSource(this);
}

// ScannerStream: Create stream instances.
public static class ScannerStream
{
    // ScannerStream::For(isolate, data): a stream over a whole source string.
    public static Utf16CharacterStream For(string data) => For(data, 0, data.Length);

    // ScannerStream::For(isolate, data, start_pos, end_pos).
    public static Utf16CharacterStream For(string data, int start_pos, int end_pos)
    {
        if (start_pos < 0 || start_pos > end_pos || end_pos > data.Length) throw new ArgumentOutOfRangeException(nameof(start_pos));
        return new UnbufferedCharacterStream(start_pos, new ArrayCharacterSource(SourceChars(data, end_pos), 0, end_pos, can_access_heap: true));
    }

    // V8 scans the source string on the heap. The streams here read a char[];
    // a large source (a script, whose functions are each parsed again by lazy
    // compilation) is copied once and the copy kept while the string lives,
    // instead of copying the script up to the function for every lazy compile.
    const int kMinCachedSourceLength = 4096;
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, char[]> s_sourceChars = new();

    static char[] SourceChars(string data, int end_pos) =>
        data.Length < kMinCachedSourceLength
            ? data.ToCharArray(0, end_pos)
            : s_sourceChars.GetValue(data, static source => source.ToCharArray());

    // Stream over a slice of an existing char array (V8: a SlicedString's
    // parent plus offset); positions are relative to `offset`.
    public static Utf16CharacterStream For(char[] data, int offset, int start_pos, int end_pos)
        => new UnbufferedCharacterStream(start_pos, new ArrayCharacterSource(data, offset, end_pos, can_access_heap: true));

    public static Utf16CharacterStream For(ReadOnlyMemory<char> data)
        => new UnbufferedCharacterStream(0, new ArrayCharacterSource(data.ToArray(), 0, data.Length, can_access_heap: true));

    // ScannerStream::ForTesting(const char* data): one-byte data through a
    // BufferedCharacterStream<TestingStream>.
    public static Utf16CharacterStream ForTesting(string data)
    {
        var chars = data.ToCharArray();
        return new BufferedCharacterStream(0, new ArrayCharacterSource(chars, 0, chars.Length));
    }

    // ScannerStream::ForTesting(const uint16_t* data, size_t length).
    public static Utf16CharacterStream ForTesting(char[] data, int length)
        => new UnbufferedCharacterStream(0, new ArrayCharacterSource(data, 0, length));

    // ScannerStream::For(source_stream, ONE_BYTE / TWO_BYTE).
    public static Utf16CharacterStream ForChunks(Func<char[]?> getMoreData, bool oneByte)
        => oneByte
            ? new BufferedCharacterStream(0, new ChunkedCharacterSource(getMoreData))
            : new UnbufferedCharacterStream(0, new ChunkedCharacterSource(getMoreData));
}
