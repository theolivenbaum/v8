// Port of test/unittests/parser/scanner-streams-unittest.cc: the parts that
// apply to the streams this port has (UTF-16 and Latin-1 data from memory:
// strings, external/testing arrays and ONE_BYTE / TWO_BYTE chunked sources).
// The UTF-8, Windows-1252 and FLEXIBLE_UTF16 embedder streaming decoders are
// not ported, so their tests (Utf8*, Regress651333, Regress6377,
// Regress6836, TestOverlongAndInvalidSequences, Regress523440299,
// FlexibleStream*, StreamSizeExceedsMaxBounds) are not either; the
// Relocating* tests exercise GC moving on-heap strings, which has no
// counterpart here.

namespace V8Sharp.Parsing.Tests.Parser;

public class ScannerStreamsTest
{
    private const int kEndOfInput = Utf16CharacterStream.kEndOfInput;

    // ChunkSource(data, char_size, len, extra_chunky): if extra_chunky, use
    // increasingly large chunk sizes; if not, a single chunk of full length.
    // Returns the GetMoreData callback; an empty chunk ends the stream.
    private static Func<char[]?> ChunkSource(string data, bool extra_chunky)
    {
        var chunks = new List<char[]>();
        int len = data.Length;
        int chunk_size = extra_chunky ? 1 : len;
        for (int i = 0; i < len; i += chunk_size, chunk_size += 1)
        {
            chunks.Add(data.Substring(i, Math.Min(chunk_size, len - i)).ToCharArray());
        }
        chunks.Add([]);
        int current = 0;
        return () => chunks[current++];
    }

    private static Func<char[]?> ChunkSource(params string[] chunks)
    {
        int current = 0;
        return () => chunks[current++].ToCharArray();
    }

    private static void TestCharacterStream(string reference, Utf16CharacterStream stream, int length, int start,
                                            int end)
    {
        // Read streams one char at a time
        int i;
        for (i = start; i < end; i++)
        {
            Assert.Equal(i, stream.pos());
            Assert.Equal(reference[i], stream.Advance());
        }
        Assert.Equal(end, stream.pos());
        Assert.Equal(kEndOfInput, stream.Advance());
        Assert.Equal(end + 1, stream.pos());
        stream.Back();

        // Pushback, re-read, pushback again.
        while (i > end / 4)
        {
            int c0 = reference[i - 1];
            Assert.Equal(i, stream.pos());
            stream.Back();
            i--;
            Assert.Equal(i, stream.pos());
            int c1 = stream.Advance();
            i++;
            Assert.Equal(i, stream.pos());
            Assert.Equal(c0, c1);
            stream.Back();
            i--;
            Assert.Equal(i, stream.pos());
        }

        // Seek + read streams one char at a time.
        int halfway = end / 2;
        stream.Seek(stream.pos() + halfway - i);
        for (i = halfway; i < end; i++)
        {
            Assert.Equal(i, stream.pos());
            Assert.Equal(reference[i], stream.Advance());
        }
        Assert.Equal(i, stream.pos());
        Assert.True(Scanner.IsInvalid(stream.Advance()));

        // Seek back, then seek beyond end of stream.
        stream.Seek(start);
        if (start < length)
        {
            Assert.Equal(reference[start], stream.Advance());
        }
        else
        {
            Assert.True(Scanner.IsInvalid(stream.Advance()));
        }
        stream.Seek(length + 5);
        Assert.True(Scanner.IsInvalid(stream.Advance()));
    }

    private static void TestCloneCharacterStream(string reference, Utf16CharacterStream stream, int length)
    {
        // Test original stream through to the end.
        TestCharacterStream(reference, stream, length, 0, length);

        // Clone the stream after it completes.
        Utf16CharacterStream clone = stream.Clone();

        // Test that the clone through to the end.
        TestCharacterStream(reference, clone, length, 0, length);

        // Rewind original stream to a third.
        stream.Seek(length / 3);

        // Rewind clone stream to two thirds.
        clone.Seek(2 * length / 3);

        // Test seeking clone didn't affect original stream.
        TestCharacterStream(reference, stream, length, length / 3, length);

        // Test seeking original stream didn't affect clone.
        TestCharacterStream(reference, clone, length, 2 * length / 3, length);
    }

    private static void TestCharacterStreams(string one_byte_source, int length, int start = 0, int end = 0)
    {
        if (end == 0) end = length;
        one_byte_source = one_byte_source[..length];

        // 2-byte external string (an off-heap array).
        char[] uc16_buffer = one_byte_source.ToCharArray();
        TestCharacterStream(one_byte_source,
                            new UnbufferedCharacterStream(start, new ArrayCharacterSource(uc16_buffer, 0, end)),
                            length, start, end);

        // 1-byte external string.
        TestCharacterStream(one_byte_source,
                            new BufferedCharacterStream(start, new ArrayCharacterSource(uc16_buffer, 0, end)),
                            length, start, end);

        // Generic i::String.
        TestCharacterStream(one_byte_source, ScannerStream.For(one_byte_source, start, end), length, start, end);

        // Streaming has no notion of start/end, so let's skip streaming tests
        // for these cases.
        if (start != 0 || end != length) return;

        // 1-byte streaming stream, single + many chunks.
        TestCharacterStream(one_byte_source, ScannerStream.ForChunks(ChunkSource(one_byte_source, false), true),
                            length, start, end);
        TestCharacterStream(one_byte_source, ScannerStream.ForChunks(ChunkSource(one_byte_source, true), true),
                            length, start, end);

        // 2-byte streaming stream, single + many chunks.
        TestCharacterStream(one_byte_source, ScannerStream.ForChunks(ChunkSource(one_byte_source, false), false),
                            length, start, end);
        TestCharacterStream(one_byte_source, ScannerStream.ForChunks(ChunkSource(one_byte_source, true), false),
                            length, start, end);
    }

    [Fact]
    public void CharacterStreams()
    {
        TestCharacterStreams("abcdefghi", 9);
        TestCharacterStreams("abc\0\n\r\x7f", 7);
        TestCharacterStreams("\0", 1);
        TestCharacterStreams("", 0);

        // 4k large buffer.
        char[] buffer = new char[4096 + 1];
        for (int i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (char)(i & 0x7F);
        }
        buffer[^1] = '\0';
        string s = new(buffer);
        TestCharacterStreams(s, buffer.Length - 1);
        TestCharacterStreams(s, buffer.Length - 1, 576, 3298);
    }

    [Fact]
    public void CloneCharacterStreams()
    {
        const string one_byte_source = "abcdefghi";
        int length = one_byte_source.Length;

        // 2-byte external string
        {
            Utf16CharacterStream uc16_stream =
                new UnbufferedCharacterStream(0, new ArrayCharacterSource(one_byte_source.ToCharArray(), 0, length));
            Utf16CharacterStream cloned = uc16_stream.Clone();
            uc16_stream = cloned;
            TestCloneCharacterStream(one_byte_source, uc16_stream, length);
        }

        // 1-byte external string
        {
            Utf16CharacterStream one_byte_stream =
                new BufferedCharacterStream(0, new ArrayCharacterSource(one_byte_source.ToCharArray(), 0, length));
            TestCloneCharacterStream(one_byte_source, one_byte_stream, length);
        }

        // Relocatable streams are't clonable.
        {
            Utf16CharacterStream string_stream = ScannerStream.For(one_byte_source, 0, length);
            Assert.False(string_stream.can_be_cloned());
        }

        // Chunk sources are cloneable.
        {
            Utf16CharacterStream one_byte_streaming_stream =
                ScannerStream.ForChunks(ChunkSource("1234", "5678", ""), true);
            TestCloneCharacterStream("12345678", one_byte_streaming_stream, 8);
        }
        {
            Utf16CharacterStream two_byte_streaming_stream =
                ScannerStream.ForChunks(ChunkSource("1234", "5678", ""), false);
            Assert.True(two_byte_streaming_stream.can_be_cloned());
            TestCloneCharacterStream("12345678", two_byte_streaming_stream, 8);
        }
    }
}
