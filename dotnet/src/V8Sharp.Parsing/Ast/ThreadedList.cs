// Copyright 2018 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/base/threaded-list.h.
//
// A ThreadedList is an intrusive singly-linked list threaded through a `next`
// field of its elements. V8's iterators are `T**` pointers to a next-slot
// (either the list's head_ or some element's next_); here a slot is a
// (list, owner) pair where owner == null denotes the list's head. That keeps
// V8's semantics for iterators that survive appends (Scope::Snapshot),
// Rewind, MoveTail and RemoveAt.

namespace V8Sharp.Ast;

public interface IThreadedListNode<T> where T : class
{
    T? NextNode { get; set; }
}

public class ThreadedList<T> where T : class, IThreadedListNode<T>
{
    private T? _head;
    // Slot of the tail: owner == null means &head_.
    private T? _tailOwner;

    // A pointer to a next-slot.
    public readonly struct Slot(ThreadedList<T> list, T? owner)
    {
        public readonly ThreadedList<T> List = list;
        public readonly T? Owner = owner;

        public T? Get() => Owner == null ? List._head : Owner.NextNode;

        public void Set(T? value)
        {
            if (Owner == null) List._head = value;
            else Owner.NextNode = value;
        }

        public bool SameAs(Slot other) => ReferenceEquals(List, other.List) && ReferenceEquals(Owner, other.Owner);
    }

    // ThreadedListTraits::start / next; UnresolvedList overrides these to
    // skip removed entries.
    protected virtual Slot StartSlot(Slot head) => head;
    protected virtual Slot NextSlot(T t) => new(this, t);

    private Slot HeadSlot => new(this, null);
    private Slot TailSlot => new(this, _tailOwner);

    public void Add(T v)
    {
        TailSlot.Set(v);
        _tailOwner = v;
    }

    public void AddFront(T v)
    {
        v.NextNode = _head;
        if (_head == null) _tailOwner = v;
        _head = v;
    }

    public void DropHead()
    {
        T oldHead = _head!;
        _head = oldHead.NextNode;
        if (_head == null) _tailOwner = null;
        oldHead.NextNode = null;
    }

    public bool Contains(T v)
    {
        for (Iterator it = begin(); it != end(); it.MoveNext())
        {
            if (it.Current == v) return true;
        }
        return false;
    }

    public void Append(ThreadedList<T> list)
    {
        if (list.is_empty()) return;
        TailSlot.Set(list._head);
        _tailOwner = list._tailOwner;
        list.Clear();
    }

    public void Prepend(ThreadedList<T> list)
    {
        if (list._head == null) return;
        T newHead = list._head;
        list.TailSlot.Set(_head);
        if (_head == null)
        {
            _tailOwner = list._tailOwner;
        }
        _head = newHead;
        list.Clear();
    }

    public void Clear()
    {
        _head = null;
        _tailOwner = null;
    }

    // Move-assignment: take over `other`'s elements (V8 operator=(&&)).
    public void MoveFrom(ThreadedList<T> other)
    {
        _head = other._head;
        _tailOwner = other._head != null ? other._tailOwner : null;
        other.Clear();
    }

    public bool Remove(T v)
    {
        T? current = first();
        if (current == v)
        {
            DropHead();
            return true;
        }

        while (current != null)
        {
            T? next = current.NextNode;
            if (next == v)
            {
                current.NextNode = next.NextNode;
                next.NextNode = null;

                if (ReferenceEquals(_tailOwner, next))
                {
                    _tailOwner = current;
                }
                return true;
            }
            current = next;
        }
        return false;
    }

    public struct Iterator
    {
        internal Slot _entry;

        internal Iterator(Slot entry) => _entry = entry;

        public readonly T? Current => _entry.Get();

        public void MoveNext() => _entry = _entry.List.NextSlot(_entry.Get()!);

        // Iterator::operator=(T* entry): replace the element at this position.
        public readonly void Replace(T entry)
        {
            T? next = _entry.Get()!.NextNode;
            entry.NextNode = next;
            _entry.Set(entry);
        }

        public void InsertBefore(T value)
        {
            T? oldEntryValue = _entry.Get();
            _entry.Set(value);
            _entry = new Slot(_entry.List, value);
            _entry.Set(oldEntryValue);
        }

        public static bool operator ==(Iterator a, Iterator b) => a._entry.SameAs(b._entry);
        public static bool operator !=(Iterator a, Iterator b) => !a._entry.SameAs(b._entry);
        public override readonly bool Equals(object? obj) => obj is Iterator o && this == o;
        public override readonly int GetHashCode() => 0;
    }

    public Iterator begin() => new(StartSlot(HeadSlot));

    public Iterator end() => new(TailSlot);

    // Rewinds the list's tail to the reset point, i.e., cutting of the rest of
    // the list, including the reset_point.
    public void Rewind(Iterator reset_point)
    {
        System.Diagnostics.Debug.Assert(ReferenceEquals(reset_point._entry.List, this));
        _tailOwner = reset_point._entry.Owner;
        TailSlot.Set(null);
    }

    // Moves the tail of the from_list, starting at the from_location, to the end
    // of this list.
    public void MoveTail(ThreadedList<T> from_list, Iterator from_location)
    {
        if (from_list.end() != from_location)
        {
            TailSlot.Set(from_location.Current);
            _tailOwner = from_list._tailOwner;
            from_list.Rewind(from_location);
        }
    }

    // Removes the element at `it`, and returns a new iterator pointing to the
    // element following the removed element.
    public Iterator RemoveAt(Iterator it)
    {
        T current = it.Current!;
        if (current == _head)
        {
            DropHead();
            return begin();
        }
        else if (ReferenceEquals(_tailOwner, current))
        {
            _tailOwner = it._entry.Owner;
            it._entry.Set(null);
            return end();
        }
        else
        {
            it._entry.Set(current.NextNode);
            current.NextNode = null;
            return new Iterator(it._entry);
        }
    }

    public bool is_empty() => _head == null;

    public T? first() => _head;

    public int LengthForTest()
    {
        int result = 0;
        for (Iterator t = begin(); t != end(); t.MoveNext()) ++result;
        return result;
    }

    public T? AtForTest(int i)
    {
        Iterator t = begin();
        while (i-- > 0) t.MoveNext();
        return t.Current;
    }

    public Enumerator GetEnumerator() => new(this);

    public struct Enumerator(ThreadedList<T> list)
    {
        private Iterator _it = list.begin();
        private readonly Iterator _end = list.end();
        private T? _current;
        private bool _started;

        public readonly T Current => _current!;

        public bool MoveNext()
        {
            if (_started) _it.MoveNext();
            _started = true;
            if (_it == _end) return false;
            _current = _it.Current;
            return _current != null;
        }
    }
}
