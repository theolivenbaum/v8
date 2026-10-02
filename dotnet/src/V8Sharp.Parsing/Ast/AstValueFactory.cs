// Copyright 2014 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/ast-value-factory.h and ast-value-factory.cc.
//
// An AstRawString holds its contents as a .NET string (UTF-16 code units) and
// remembers whether V8 would have stored it one-byte (all code units <= 0xFF),
// since that is observable through byte_length() and Compare(). Strings are
// deduplicated per AstValueFactory, so reference equality is content equality
// within one factory, as in V8.
//
// Deviation: V8 hashes with StringHasher and the isolate's hash seed and
// packs array-index information into the hash field. Here the hash is .NET's
// (only used for hash tables, never for output order), and the integer/array
// index facts are computed directly with V8's rules.

using System.Runtime.CompilerServices;

namespace V8Sharp.Ast;

public sealed class AstRawString
{
    private readonly string _value;
    private readonly bool _isOneByte;
    private readonly int _hash;
    // Array index / integer index facts (V8 keeps these in raw_hash_field_).
    private readonly bool _isIntegerIndex;
    private readonly bool _isArrayIndex;
    private readonly uint _arrayIndex;

    // AstValueFactory::strings_ linked list (creation order), used by Internalize.
    internal AstRawString? next_;

    // The internalized heap string, set by the engine (V8: string_ / set_string).
    public object? string_ { get; private set; }

    internal AstRawString(string value, bool isOneByte) : this(value, isOneByte, AstStringTable.Hash(value)) { }

    internal AstRawString(string value, bool isOneByte, int hash)
    {
        _value = value;
        _isOneByte = isOneByte;
        _hash = hash;
        ComputeIndex(value, out _isIntegerIndex, out _isArrayIndex, out _arrayIndex);
    }

    private static void ComputeIndex(string s, out bool isIntegerIndex, out bool isArrayIndex, out uint arrayIndex)
    {
        isIntegerIndex = false;
        isArrayIndex = false;
        arrayIndex = 0;
        // String::kMaxIntegerIndexSize is 16 (digits of 2^53 - 1).
        if (s.Length == 0 || s.Length > 16) return;
        if (s[0] == '0')
        {
            if (s.Length != 1) return;
            isIntegerIndex = true;
            isArrayIndex = true;
            return;
        }
        ulong value = 0;
        foreach (char c in s)
        {
            if ((uint)(c - '0') > 9) return;
            value = value * 10 + (uint)(c - '0');
        }
        if (value > (1UL << 53) - 1) return;
        isIntegerIndex = true;
        // String::kMaxArrayIndex = kMaxUInt32 - 1.
        if (value <= uint.MaxValue - 1)
        {
            isArrayIndex = true;
            arrayIndex = (uint)value;
        }
    }

    // The contents as a .NET string.
    public string Value => _value;

    public static bool Equal(AstRawString lhs, AstRawString rhs) => string.Equals(lhs._value, rhs._value, StringComparison.Ordinal);

    // Returns 0 if lhs is equal to rhs.
    // Returns <0 if lhs is less than rhs in code point order.
    // Returns >0 if lhs is greater than than rhs in code point order.
    public static int Compare(AstRawString lhs, AstRawString rhs)
    {
        // Fast path for equal pointers.
        if (ReferenceEquals(lhs, rhs)) return 0;

        int length = Math.Min(lhs.length(), rhs.length());
        // Code point order by contents (CompareCharsUnsigned).
        for (int i = 0; i < length; i++)
        {
            int r = lhs._value[i] - rhs._value[i];
            if (r != 0) return r;
        }
        return lhs.byte_length() - rhs.byte_length();
    }

    public bool IsEmpty() => _value.Length == 0;
    public int length() => _value.Length;

    public bool AsArrayIndex(out uint index)
    {
        index = _arrayIndex;
        return _isArrayIndex;
    }

    public bool IsIntegerIndex() => _isIntegerIndex;

    public bool IsOneByteEqualTo(string data) => _isOneByte && string.Equals(_value, data, StringComparison.Ordinal);

    public char FirstCharacter() => _value[0];

    // Access the physical representation:
    public bool is_one_byte() => _isOneByte;
    public int byte_length() => _isOneByte ? _value.Length : _value.Length * 2;

    public bool IsPrivateName() => length() > 0 && FirstCharacter() == '#';

    // For storing AstRawStrings in a hash map.
    public uint Hash() => (uint)_hash;

    public override int GetHashCode() => _hash;

    // Reference equality: strings are unique per factory.
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    // AstRawString::Internalize: the engine supplies the internalizer.
    public void Internalize(Func<AstRawString, object> internalize)
    {
        if (string_ is not null) return;
        string_ = internalize(this);
    }

    public void set_string(object s) => string_ = s;

    public override string ToString() => _value;
}

public sealed class AstConsString
{
    // The string segments in source order (V8 keeps a reversed linked list).
    private readonly List<AstRawString> _segments = [];

    // The heap string, set by the engine (V8: string_).
    public object? string_ { get; set; }

    internal AstConsString() { }

    public AstConsString AddString(AstRawString s)
    {
        if (s.IsEmpty()) return this;
        _segments.Add(s);
        return this;
    }

    public bool IsEmpty() => _segments.Count == 0;

    // The segments in order (V8: ToRawStrings()).
    public IReadOnlyList<AstRawString> ToRawStrings() => _segments;

    public AstRawString? last() => _segments.Count == 0 ? null : _segments[^1];

    // The concatenated contents (V8: AllocateFlat without the heap).
    public string ToFlatString()
    {
        if (_segments.Count == 0) return string.Empty;
        if (_segments.Count == 1) return _segments[0].Value;
        var sb = new System.Text.StringBuilder();
        foreach (var s in _segments) sb.Append(s.Value);
        return sb.ToString();
    }

    public override string ToString() => ToFlatString();
}

// |bigint| is the literal text of a BigInt (suitable for BigIntLiteral() from
// conversions.h), e.g. "0x1f" or "123", without the trailing 'n'.
public readonly record struct AstBigInt(string bigint)
{
    public string c_str() => bigint;
}

// Holds constant string values which are shared across the isolate
// (V8: AstStringConstants, AST_STRING_CONSTANTS).
public sealed class AstStringConstants
{
    public const int kMaxOneCharStringValue = 128;

    internal readonly AstStringTable string_table_ = new();
    private readonly List<AstRawString> _allStrings = [];
    private readonly AstRawString[] _oneCharacterStrings = new AstRawString[kMaxOneCharStringValue];

    public AstStringConstants()
    {
        for (int c = 0; c < kMaxOneCharStringValue; c++)
        {
            _oneCharacterStrings[c] = Add(((char)c).ToString());
        }
        anonymous_string = Add("anonymous");
        arguments_string = Add("arguments");
        as_string = Add("as");
        assert_string = Add("assert");
        async_string = Add("async");
        bigint_string = Add("bigint");
        boolean_string = Add("boolean");
        computed_string = Add("<computed>");
        constructor_string = Add("constructor");
        default_string = Add("default");
        defer_string = Add("defer");
        done_string = Add("done");
        dot_brand_string = Add(".brand");
        dot_catch_string = Add(".catch");
        dot_default_string = Add(".default");
        dot_for_string = Add(".for");
        dot_generator_object_string = Add(".generator_object");
        dot_home_object_string = Add(".home_object");
        dot_new_target_string = Add(".new.target");
        dot_repl_result_string = Add(".repl_result");
        dot_result_string = Add(".result");
        dot_static_home_object_string = Add(".static_home_object");
        dot_switch_tag_string = Add(".switch_tag");
        dot_this_function_string = Add(".this_function");
        empty_string = Add("");
        eval_string = Add("eval");
        from_string = Add("from");
        function_string = Add("function");
        get_space_string = Add("get ");
        length_string = Add("length");
        let_string = Add("let");
        meta_string = Add("meta");
        native_string = Add("native");
        next_string = Add("next");
        number_string = Add("number");
        object_string = Add("object");
        private_constructor_string = Add("#constructor");
        proto_string = Add("__proto__");
        prototype_string = Add("prototype");
        return_string = Add("return");
        set_space_string = Add("set ");
        source_string = Add("source");
        string_string = Add("string");
        symbol_string = Add("symbol");
        target_string = Add("target");
        this_string = Add("this");
        throw_string = Add("throw");
        undefined_string = Add("undefined");
        value_string = Add("value");
    }

    private AstRawString Add(string s)
    {
        var str = new AstRawString(s, true);
        string_table_.Add(str);
        _allStrings.Add(str);
        return str;
    }

    public AstRawString one_character_string(int c) => _oneCharacterStrings[c];

    public AstRawString anonymous_string { get; }
    public AstRawString arguments_string { get; }
    public AstRawString as_string { get; }
    public AstRawString assert_string { get; }
    public AstRawString async_string { get; }
    public AstRawString bigint_string { get; }
    public AstRawString boolean_string { get; }
    public AstRawString computed_string { get; }
    public AstRawString constructor_string { get; }
    public AstRawString default_string { get; }
    public AstRawString defer_string { get; }
    public AstRawString done_string { get; }
    public AstRawString dot_brand_string { get; }
    public AstRawString dot_catch_string { get; }
    public AstRawString dot_default_string { get; }
    public AstRawString dot_for_string { get; }
    public AstRawString dot_generator_object_string { get; }
    public AstRawString dot_home_object_string { get; }
    public AstRawString dot_new_target_string { get; }
    public AstRawString dot_repl_result_string { get; }
    public AstRawString dot_result_string { get; }
    public AstRawString dot_static_home_object_string { get; }
    public AstRawString dot_switch_tag_string { get; }
    public AstRawString dot_this_function_string { get; }
    public AstRawString empty_string { get; }
    public AstRawString eval_string { get; }
    public AstRawString from_string { get; }
    public AstRawString function_string { get; }
    public AstRawString get_space_string { get; }
    public AstRawString length_string { get; }
    public AstRawString let_string { get; }
    public AstRawString meta_string { get; }
    public AstRawString native_string { get; }
    public AstRawString next_string { get; }
    public AstRawString number_string { get; }
    public AstRawString object_string { get; }
    public AstRawString private_constructor_string { get; }
    public AstRawString proto_string { get; }
    public AstRawString prototype_string { get; }
    public AstRawString return_string { get; }
    public AstRawString set_space_string { get; }
    public AstRawString source_string { get; }
    public AstRawString string_string { get; }
    public AstRawString symbol_string { get; }
    public AstRawString target_string { get; }
    public AstRawString this_string { get; }
    public AstRawString throw_string { get; }
    public AstRawString undefined_string { get; }
    public AstRawString value_string { get; }

    // All constant strings (the engine internalizes these once per isolate).
    public IEnumerable<AstRawString> AllStrings() => _allStrings;
}

public sealed class AstValueFactory
{
    // All strings are copied here.
    private readonly AstStringTable _stringTable;

    // Strings created by this factory, in creation order (V8: strings_ list).
    private AstRawString? _strings;
    private AstRawString? _stringsEnd;

    // Holds constant string values which are shared across the isolate.
    private readonly AstStringConstants _stringConstants;

    private readonly AstConsString _emptyConsString;

    public AstValueFactory(AstStringConstants string_constants)
    {
        _stringConstants = string_constants;
        _stringTable = new AstStringTable(string_constants.string_table_);
        _emptyConsString = new AstConsString();
    }

    public AstValueFactory() : this(new AstStringConstants()) { }

    public AstStringConstants string_constants() => _stringConstants;

    // GetOneByteString / GetTwoByteString: `isOneByte` must be true iff all
    // code units are <= 0xFF (the scanner's LiteralBuffer tracks this).
    public AstRawString GetString(ReadOnlySpan<char> literal, bool isOneByte)
    {
        if (literal.Length == 1)
        {
            char key = literal[0];
            if (key < AstStringConstants.kMaxOneCharStringValue)
            {
                return _stringConstants.one_character_string(key);
            }
        }
        int hash = AstStringTable.Hash(literal);
        AstRawString? existing = _stringTable.Lookup(literal, hash);
        if (existing != null) return existing;
        var result = new AstRawString(literal.ToString(), isOneByte, hash);
        _stringTable.Add(result);
        AddString(result);
        return result;
    }

    public AstRawString GetOneByteString(string literal) => GetString(literal, IsOneByte(literal));

    public AstRawString GetOneByteString(ReadOnlySpan<char> literal) => GetString(literal, true);

    public AstRawString GetTwoByteString(ReadOnlySpan<char> literal) => GetString(literal, false);

    // AstValueFactory::GetString(Tagged<String>): any heap string contents.
    public AstRawString GetString(string literal) => GetString(literal, IsOneByte(literal));

    public static bool IsOneByte(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c > 0xFF) return false;
        }
        return true;
    }

    public AstConsString NewConsString() => new();

    public AstConsString NewConsString(AstRawString str) => NewConsString().AddString(str);

    public AstConsString NewConsString(AstRawString str1, AstRawString str2) => NewConsString().AddString(str1).AddString(str2);

    // Internalize all the strings in the factory. Multiple calls to Internalize
    // are allowed, for simplicity, where subsequent calls are a no-op.
    public void Internalize(Func<AstRawString, object> internalize)
    {
        for (AstRawString? current = _strings; current != null;)
        {
            AstRawString? next = current.next_;
            current.Internalize(internalize);
            current = next;
        }
        ResetStrings();
    }

    // The strings created since the last Internalize, in creation order.
    public IEnumerable<AstRawString> StringsForTesting()
    {
        for (AstRawString? current = _strings; current != null; current = current.next_)
            yield return current;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddString(AstRawString s)
    {
        if (_stringsEnd == null) _strings = s;
        else _stringsEnd.next_ = s;
        _stringsEnd = s;
    }

    private void ResetStrings()
    {
        _strings = null;
        _stringsEnd = null;
    }

    public AstConsString empty_cons_string() => _emptyConsString;

    public AstRawString one_character_string(int c) => _stringConstants.one_character_string(c);
    public AstRawString dot_string() => _stringConstants.one_character_string('.');
    public AstRawString anonymous_string() => _stringConstants.anonymous_string;
    public AstRawString arguments_string() => _stringConstants.arguments_string;
    public AstRawString as_string() => _stringConstants.as_string;
    public AstRawString assert_string() => _stringConstants.assert_string;
    public AstRawString async_string() => _stringConstants.async_string;
    public AstRawString bigint_string() => _stringConstants.bigint_string;
    public AstRawString boolean_string() => _stringConstants.boolean_string;
    public AstRawString computed_string() => _stringConstants.computed_string;
    public AstRawString constructor_string() => _stringConstants.constructor_string;
    public AstRawString default_string() => _stringConstants.default_string;
    public AstRawString defer_string() => _stringConstants.defer_string;
    public AstRawString done_string() => _stringConstants.done_string;
    public AstRawString dot_brand_string() => _stringConstants.dot_brand_string;
    public AstRawString dot_catch_string() => _stringConstants.dot_catch_string;
    public AstRawString dot_default_string() => _stringConstants.dot_default_string;
    public AstRawString dot_for_string() => _stringConstants.dot_for_string;
    public AstRawString dot_generator_object_string() => _stringConstants.dot_generator_object_string;
    public AstRawString dot_home_object_string() => _stringConstants.dot_home_object_string;
    public AstRawString dot_new_target_string() => _stringConstants.dot_new_target_string;
    public AstRawString dot_repl_result_string() => _stringConstants.dot_repl_result_string;
    public AstRawString dot_result_string() => _stringConstants.dot_result_string;
    public AstRawString dot_static_home_object_string() => _stringConstants.dot_static_home_object_string;
    public AstRawString dot_switch_tag_string() => _stringConstants.dot_switch_tag_string;
    public AstRawString dot_this_function_string() => _stringConstants.dot_this_function_string;
    public AstRawString empty_string() => _stringConstants.empty_string;
    public AstRawString eval_string() => _stringConstants.eval_string;
    public AstRawString from_string() => _stringConstants.from_string;
    public AstRawString function_string() => _stringConstants.function_string;
    public AstRawString get_space_string() => _stringConstants.get_space_string;
    public AstRawString length_string() => _stringConstants.length_string;
    public AstRawString let_string() => _stringConstants.let_string;
    public AstRawString meta_string() => _stringConstants.meta_string;
    public AstRawString native_string() => _stringConstants.native_string;
    public AstRawString next_string() => _stringConstants.next_string;
    public AstRawString number_string() => _stringConstants.number_string;
    public AstRawString object_string() => _stringConstants.object_string;
    public AstRawString private_constructor_string() => _stringConstants.private_constructor_string;
    public AstRawString proto_string() => _stringConstants.proto_string;
    public AstRawString prototype_string() => _stringConstants.prototype_string;
    public AstRawString return_string() => _stringConstants.return_string;
    public AstRawString set_space_string() => _stringConstants.set_space_string;
    public AstRawString source_string() => _stringConstants.source_string;
    public AstRawString string_string() => _stringConstants.string_string;
    public AstRawString symbol_string() => _stringConstants.symbol_string;
    public AstRawString target_string() => _stringConstants.target_string;
    public AstRawString this_string() => _stringConstants.this_string;
    public AstRawString throw_string() => _stringConstants.throw_string;
    public AstRawString undefined_string() => _stringConstants.undefined_string;
    public AstRawString value_string() => _stringConstants.value_string;
}

// The AstValueFactory's string table (V8: a base::CustomMatcherHashMap keyed
// by the string's hash and contents). Open addressing with linear probing over
// a power-of-two array of the strings, which carry their hash; the factory
// starts from a copy of the constants' table, as V8's does.
internal sealed class AstStringTable
{
    private AstRawString?[] _slots;
    private int _count;

    public AstStringTable() => _slots = new AstRawString?[256];

    public AstStringTable(AstStringTable other)
    {
        _slots = (AstRawString?[])other._slots.Clone();
        _count = other._count;
    }

    // FNV-1a over the code units: cheap for the short identifiers the
    // scanner produces. Only used for hash tables, never for output order.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Hash(ReadOnlySpan<char> s)
    {
        uint h = 2166136261u;
        for (int i = 0; i < s.Length; i++) h = (h ^ s[i]) * 16777619u;
        return (int)(h ^ (h >> 15));
    }

    public AstRawString? Lookup(ReadOnlySpan<char> literal, int hash)
    {
        AstRawString?[] slots = _slots;
        int mask = slots.Length - 1;
        for (int i = hash & mask; ; i = (i + 1) & mask)
        {
            AstRawString? s = slots[i];
            if (s == null) return null;
            if (s.GetHashCode() == hash && literal.SequenceEqual(s.Value)) return s;
        }
    }

    public void Add(AstRawString s)
    {
        if ((_count + 1) * 2 > _slots.Length) Grow();
        Insert(_slots, s);
        _count++;
    }

    private static void Insert(AstRawString?[] slots, AstRawString s)
    {
        int mask = slots.Length - 1;
        int i = s.GetHashCode() & mask;
        while (slots[i] != null) i = (i + 1) & mask;
        slots[i] = s;
    }

    private void Grow()
    {
        var slots = new AstRawString?[_slots.Length * 2];
        foreach (AstRawString? s in _slots)
        {
            if (s != null) Insert(slots, s);
        }
        _slots = slots;
    }
}
