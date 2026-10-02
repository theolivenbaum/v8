// Copyright 2019 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/literal-buffer.h and literal-buffer.cc.
//
// V8 keeps one-byte literals as bytes and switches the backing store to
// UTF-16 on the first char above Latin-1. Here the backing store is always
// UTF-16 (`char[]`), and the one-byte-ness V8 tracks is kept as a flag, so
// is_one_byte() answers exactly as V8's does and literals come out as spans
// of the same code units.

using System.Runtime.CompilerServices;

namespace V8Sharp.Parsing;

public sealed class LiteralBuffer
{
    private const int kInitialCapacity = 256;
    private const int kGrowthFactor = 4;
    private const int kMaxGrowth = 1 * 1024 * 1024;

    private char[] _backingStore = [];
    private int _position;
    private bool _isOneByte = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddChar(char codeUnit) => AddChar((int)codeUnit);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddChar(int codeUnit)
    {
        if (codeUnit <= 0xFF)
        {
            if (_position >= _backingStore.Length) ExpandBuffer();
            _backingStore[_position++] = (char)codeUnit;
            return;
        }
        _isOneByte = false;
        AddTwoByteChar(codeUnit);
    }

    // Adds a range of UTF-16 code units. Callers batch only ASCII ranges while
    // the buffer is one-byte.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRangeFromUtf16(ReadOnlySpan<char> range)
    {
        if (_position + range.Length > _backingStore.Length) ExpandBufferTo(_position + range.Length);
        range.CopyTo(_backingStore.AsSpan(_position));
        _position += range.Length;
    }

    public bool is_one_byte() => _isOneByte;

    public bool Equals(ReadOnlySpan<char> keyword) =>
        is_one_byte() && keyword.SequenceEqual(_backingStore.AsSpan(0, _position));

    // The literal's code units (V8: one_byte_literal()/two_byte_literal()).
    public ReadOnlySpan<char> literal() => new(_backingStore, 0, _position);

    public ReadOnlySpan<char> one_byte_literal()
    {
        System.Diagnostics.Debug.Assert(_isOneByte);
        return new(_backingStore, 0, _position);
    }

    public ReadOnlySpan<char> two_byte_literal()
    {
        System.Diagnostics.Debug.Assert(!_isOneByte);
        return new(_backingStore, 0, _position);
    }

    public int length() => _position;

    public void Start()
    {
        _position = 0;
        _isOneByte = true;
    }

    // LiteralBuffer::Internalize: the engine internalizes the returned string.
    public string Internalize() => new(_backingStore, 0, _position);

    public override string ToString() => new(_backingStore, 0, _position);

    private static int NewCapacity(int minCapacity) =>
        minCapacity < (kMaxGrowth / (kGrowthFactor - 1)) ? minCapacity * kGrowthFactor : minCapacity + kMaxGrowth;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExpandBuffer()
    {
        if (_backingStore.Length == 0 && t_poolCount > 0)
        {
            _backingStore = t_pool![--t_poolCount]!;
            t_pool[t_poolCount] = null;
            return;
        }
        int minCapacity = Math.Max(kInitialCapacity, _backingStore.Length);
        Array.Resize(ref _backingStore, NewCapacity(minCapacity));
    }

    // V8 allocates the backing store with new[] and frees it with the scanner.
    // Here a scanner's token buffers go back to a small per-thread pool when
    // its parse is done (Scanner.ReleaseLiteralBuffers), so a lazy compile does
    // not allocate them again.
    private const int kPoolSize = 16;
    [ThreadStatic] private static char[]?[]? t_pool;
    [ThreadStatic] private static int t_poolCount;

    public void ReleaseBackingStore()
    {
        char[] store = _backingStore;
        _backingStore = [];
        _position = 0;
        _isOneByte = true;
        if (store.Length != NewCapacity(kInitialCapacity)) return;
        t_pool ??= new char[]?[kPoolSize];
        if (t_poolCount < kPoolSize) t_pool[t_poolCount++] = store;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExpandBufferTo(int minSize)
    {
        int minCapacity = Math.Max(Math.Max(kInitialCapacity, _backingStore.Length), minSize);
        Array.Resize(ref _backingStore, NewCapacity(minCapacity));
    }

    private void AddTwoByteChar(int codeUnit)
    {
        if (codeUnit <= Utf16.kMaxNonSurrogateCharCode)
        {
            if (_position >= _backingStore.Length) ExpandBuffer();
            _backingStore[_position++] = (char)codeUnit;
        }
        else
        {
            if (_position + 1 >= _backingStore.Length) ExpandBuffer();
            _backingStore[_position++] = Utf16.LeadSurrogate(codeUnit);
            _backingStore[_position++] = Utf16.TrailSurrogate(codeUnit);
        }
    }
}
