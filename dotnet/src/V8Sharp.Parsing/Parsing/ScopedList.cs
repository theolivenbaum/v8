// Copyright 2019 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/utils/scoped-list.h.

using System.Collections;
using System.Runtime.InteropServices;

namespace V8Sharp.Parsing;

// ScopedList is a scope-lifetime list with a List<T> backing that can be
// reused between ScopedLists. Note that a ScopedList in an outer scope cannot
// add any entries if there is a ScopedList with the same backing in an inner
// scope. Dispose() rewinds, like the C++ destructor.
public sealed class ScopedList<T> : IReadOnlyList<T>, IDisposable
{
    private readonly List<T> _buffer;
    private int _start;
    private int _end;

    public ScopedList(List<T> buffer)
    {
        _buffer = buffer;
        _start = buffer.Count;
        _end = buffer.Count;
    }

    public void Dispose() => Rewind();

    // Starts the list again at the end of its buffer, as a new ScopedList
    // over the same buffer would (for recycled expression scopes).
    public void Reopen()
    {
        _start = _buffer.Count;
        _end = _start;
    }

    public void Rewind()
    {
        if (_buffer.Count > _start) _buffer.RemoveRange(_start, _buffer.Count - _start);
        _end = _start;
    }

    public void MergeInto(ScopedList<T> parent)
    {
        parent._end = _end;
        _start = _end;
    }

    public int length() => _end - _start;

    public ref T at(int i) => ref CollectionsMarshal.AsSpan(_buffer)[_start + i];

    public void Add(T value)
    {
        _buffer.Add(value);
        ++_end;
    }

    public void AddAll(IReadOnlyList<T> list)
    {
        for (int i = 0; i < list.Count; i++) _buffer.Add(list[i]);
        _end += list.Count;
    }

    public Span<T> AsSpan() => CollectionsMarshal.AsSpan(_buffer).Slice(_start, _end - _start);

    public int Count => _end - _start;
    public T this[int index] => _buffer[_start + index];

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(ScopedList<T> list) : IEnumerator<T>
    {
        private int _index = -1;
        public readonly T Current => list[_index];
        readonly object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < list.Count;
        public void Reset() => _index = -1;
        public readonly void Dispose() { }
    }
}

// The list shapes ParserBase builds with pointer_buffer(): ScopedPtrList for
// the Parser, and the counting / no-op lists of the PreParser.
public interface IScopedPtrList<TSelf, TElem> : IDisposable
    where TSelf : IScopedPtrList<TSelf, TElem>
{
    static abstract TSelf New(List<object?> buffer);
    int length();
    void Add(TElem value);
    void Rewind();
    void MergeInto(TSelf parent);
}

// ScopedPtrList<T> = ScopedList<T*, void*>: a list of T over a shared
// untyped pointer buffer.
public sealed class ScopedPtrList<T> : IScopedPtrList<ScopedPtrList<T>, T>, IReadOnlyList<T>
    where T : class
{
    private readonly List<object?> _buffer;
    private int _start;
    private int _end;

    public ScopedPtrList(List<object?> buffer)
    {
        _buffer = buffer;
        _start = buffer.Count;
        _end = buffer.Count;
    }

    public static ScopedPtrList<T> New(List<object?> buffer) => new(buffer);

    public void Dispose() => Rewind();

    public void Rewind()
    {
        if (_buffer.Count > _start) _buffer.RemoveRange(_start, _buffer.Count - _start);
        _end = _start;
    }

    public void MergeInto(ScopedPtrList<T> parent)
    {
        parent._end = _end;
        _start = _end;
    }

    public int length() => _end - _start;

    public T at(int i) => (T)_buffer[_start + i]!;

    public void Set(int i, T value) => _buffer[_start + i] = value;

    public void Add(T value)
    {
        _buffer.Add(value);
        ++_end;
    }

    public void AddAll(IReadOnlyList<T> list)
    {
        for (int i = 0; i < list.Count; i++) _buffer.Add(list[i]);
        _end += list.Count;
    }

    public int Count => _end - _start;
    public T this[int index] => (T)_buffer[_start + index]!;

    public Enumerator GetEnumerator() => new(this);
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator(ScopedPtrList<T> list) : IEnumerator<T>
    {
        private int _index = -1;
        public readonly T Current => list[_index];
        readonly object? IEnumerator.Current => Current;
        public bool MoveNext() => ++_index < list.Count;
        public void Reset() => _index = -1;
        public readonly void Dispose() { }
    }
}
