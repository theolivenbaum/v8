// Copyright 2017 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/parsing/preparse-data.h, preparse-data-impl.h and
// preparse-data.cc.
//
// V8 serializes preparse data either into the zone (ZonePreparseData) or onto
// the heap (PreparseData, stored in UncompiledDataWithPreparseData). Both hold
// the same bytes and children; here both are the managed PreparseData class,
// which the engine can keep on its SharedFunctionInfo directly. The byte
// format is V8's release-build format (no debug markers).

/*

  Skipping inner functions.

  Consider the following code:
  (function eager_outer() {
    function lazy_inner() {
      let a;
      function skip_me() { a; }
    }

    return lazy_inner;
  })();

  ... lazy_inner(); ...

  When parsing the code the first time, eager_outer is parsed and lazy_inner
  (and everything inside it) is preparsed. When lazy_inner is called, we don't
  want to parse or preparse skip_me again. Instead, we want to skip over it,
  since it has already been preparsed once.

  In order to be able to do this, we need to store the information needed for
  allocating the variables in lazy_inner when we preparse it, and then later do
  scope allocation based on that data.

  We need the following data for each scope in lazy_inner's scope tree:
  For each Variable:
  - is_used
  - maybe_assigned
  - has_forced_context_allocation

  For each Scope:
  - inner_scope_calls_eval_.

  ProducedPreparseData implements storing the above mentioned data and
  ConsumedPreparseData implements restoring it (= setting the context
  allocation status of the variables in a Scope (and its subscopes) based on the
  data).

  Internal data format for the backing store of PreparseDataBuilder and
  PreparseData::scope_data:

  (Skippable function data:)
  ------------------------------------
  | data for inner function n        |
  | ...                              |
  ------------------------------------
  | data for inner function 1        |
  | ...                              |
  ------------------------------------
  (Scope allocation data:)
  ------------------------------------
  | eval                             |
  | ----------------------           |
  | | data for variables |           |
  | | ...                |           |
  | ----------------------           |
  ------------------------------------
  ------------------------------------
  | data for inner scope m           | << but not for function scopes
  | ...                              |
  ------------------------------------
  ...
  ------------------------------------
  | data for inner scope 1           |
  | ...                              |
  ------------------------------------

  PreparseData::child_data is an array of PreparseData objects, one
  for each skippable inner function.

  ConsumedPreparseData wraps a PreparseData and reads data from it.

 */

using V8Sharp.Ast;
using V8Sharp.Common;
using static V8Sharp.Common.Globals;

namespace V8Sharp.Parsing;

public static class PreparseByteDataConstants
{
    public const int kUint32Size = 4;
    public const int kVarint32MinSize = 1;
    public const int kVarint32MaxSize = 5;
    public const int kUint8Size = 1;
    public const int kPlaceholderSize = 0;

    public const int kSkippableFunctionMinDataSize = 4 * kVarint32MinSize + 1 * kUint8Size;
    // A skippable function writes up to 5 Varint32s (start_position,
    // end_position, has_data_and_num_parameters, function_length when
    // length != num_parameters, and num_inner_functions) plus 1 Quarter
    // (language_and_super).
    public const int kSkippableFunctionMaxDataSize = 5 * kVarint32MaxSize + 1 * kUint8Size;
}

// The serialized preparse data of a function: its scope data bytes and the
// data of its skippable inner functions that have data (V8's PreparseData /
// ZonePreparseData).
public sealed class PreparseData(byte[] data, PreparseData?[] children)
{
    public byte[] scope_data() => data;
    public int data_length() => data.Length;
    public byte get(int index) => data[index];
    public int children_length() => children.Length;
    public PreparseData? get_child(int index) => children[index];
    public void set_child(int index, PreparseData child) => children[index] = child;
}

public sealed class PreparseDataBuilder
{
    // Bit fields of the scope data.
    private const byte ScopeSloppyEvalCanExtendVarsBit = 1 << 0;
    private const byte InnerScopeCallsEvalField = 1 << 1;
    private const byte NeedsPrivateNameContextChainRecalcField = 1 << 2;
    private const byte ShouldSaveClassVariableIndexField = 1 << 3;

    private const byte VariableMaybeAssignedField = 1 << 0;
    private const byte VariableContextAllocatedField = 1 << 1;

    private const uint HasDataField = 1 << 0;
    private const uint LengthEqualsParametersField = 1 << 1;
    private const int NumberOfParametersShift = 2;
    private const uint NumberOfParametersMask = 0xFFFF;

    private const byte LanguageField = 1 << 0;
    private const byte UsesSuperField = 1 << 1;

    private readonly PreparseDataBuilder? _parent;
    private readonly ByteData _byteData = new();
    private List<PreparseDataBuilder>? _childrenBuffer = [];
    private PreparseDataBuilder[] _children = [];

    private DeclarationScope? _functionScope;
    private int _functionLength;
    private int _numInnerFunctions;
    private int _numInnerWithData;

    // Whether we've given up producing the data for this function.
    private bool _bailedOut;
    private bool _hasData;

    // Create a PreparseDataBuilder object which will collect data as we
    // parse.
    public PreparseDataBuilder(PreparseDataBuilder? parent_builder)
    {
        _parent = parent_builder;
        _functionScope = null;
        _functionLength = -1;
        _numInnerFunctions = 0;
        _numInnerWithData = 0;
        _bailedOut = false;
        _hasData = false;
    }

    public PreparseDataBuilder? parent() => _parent;

    // For gathering the inner function data and splitting it up according to the
    // laziness boundaries. Each lazy function gets its own
    // ProducedPreparseData, and so do all lazy functions inside it.
    public struct DataGatheringScope(PreParser preparser)
    {
        private readonly PreParser _preparser = preparser;
        private PreparseDataBuilder? _builder = null;

        public void Start(DeclarationScope function_scope)
        {
            _builder = new PreparseDataBuilder(_preparser.preparse_data_builder());
            _preparser.set_preparse_data_builder(_builder);
            function_scope.set_preparse_data_builder(_builder);
        }

        public readonly void SetSkippableFunction(DeclarationScope function_scope, int function_length, int num_inner_functions)
        {
            _builder!._functionScope = function_scope;
            _builder._functionLength = function_length;
            _builder._numInnerFunctions = num_inner_functions;
            _builder._parent!._hasData = true;
        }

        // ~DataGatheringScope
        public readonly void Dispose()
        {
            if (_builder == null) return;
            Close();
        }

        private readonly void Close()
        {
            PreparseDataBuilder? parent = _builder!._parent;
            _preparser.set_preparse_data_builder(parent);
            _builder.FinalizeChildren();

            if (parent == null) return;
            if (!_builder.HasDataForParent()) return;
            parent.AddChild(_builder);
        }
    }

    public sealed class ByteData
    {
        private List<byte>? _byteData;
        private byte[]? _zoneByteData;
        private int _index;
        private byte _freeQuartersInLastByte;

        public void Start(List<byte> buffer)
        {
            _byteData = buffer;
            _byteData.Clear();
            _index = 0;
        }

        public void FinalizeData() // ByteData::Finalize
        {
            _zoneByteData = new byte[_index];
            for (int i = 0; i < _index; i++) _zoneByteData[i] = _byteData![i];
            _byteData!.Clear();
        }

        public PreparseData CopyToZone(int children_length) =>
            new(_zoneByteData ?? [], new PreparseData?[children_length]);

        public void Reserve(int bytes)
        {
            // Make sure we have at least {bytes} capacity left in the buffer_.
            int capacity = _byteData!.Count - length();
            if (capacity >= bytes) return;
            int delta = bytes - capacity;
            for (int i = 0; i < delta; i++) _byteData.Add(0);
        }

        public void Add(byte b) => _byteData![_index++] = b;

        public int length() => _index;

        public void WriteVarint32(uint data)
        {
            // See ValueSerializer::WriteVarint.
            do
            {
                byte next_byte = (byte)(data & 0x7F);
                data >>= 7;
                // Add continue bit.
                if (data != 0) next_byte |= 0x80;
                Add(next_byte);
            } while (data != 0);
            _freeQuartersInLastByte = 0;
        }

        public void WriteUint8(byte data)
        {
            Add(data);
            _freeQuartersInLastByte = 0;
        }

        public void WriteQuarter(byte data)
        {
            if (_freeQuartersInLastByte == 0)
            {
                Add(0);
                _freeQuartersInLastByte = 3;
            }
            else
            {
                --_freeQuartersInLastByte;
            }

            int shift_amount = _freeQuartersInLastByte * 2;
            _byteData![_index - 1] |= (byte)(data << shift_amount);
        }
    }

    // Saves the information needed for allocating the Scope's (and its
    // subscopes') variables.
    public void SaveScopeAllocationData(DeclarationScope scope, Parser parser)
    {
        if (!_hasData) return;

        _byteData.Start(parser.preparse_data_buffer());

        _byteData.Reserve(_children.Length * PreparseByteDataConstants.kSkippableFunctionMaxDataSize);
        foreach (PreparseDataBuilder builder in _children)
        {
            // Keep track of functions with inner data. {children_} contains also the
            // builders that have no inner functions at all.
            if (SaveDataForSkippableFunction(builder)) _numInnerWithData++;
        }

        // Don't save incomplete scope information when bailed out.
        if (!_bailedOut)
        {
            if (ScopeNeedsData(scope)) SaveDataForScope(scope);
        }
        _byteData.FinalizeData();
    }

    // In some cases, PreParser cannot produce the same Scope structure as
    // Parser. If it happens, we're unable to produce the data that would enable
    // skipping the inner functions of that function.
    public void Bailout()
    {
        _bailedOut = true;
        // We don't need to call Bailout on existing / future children: the only way
        // to try to retrieve their data is through calling Serialize on the parent,
        // and if the parent is bailed out, it won't call Serialize on its children.
    }

    public bool bailed_out() => _bailedOut;

    public bool HasInnerFunctions() => _children.Length != 0;

    public bool HasData() => !_bailedOut && _hasData;

    public bool HasDataForParent() => HasData() || _functionScope != null;

    public static bool ScopeNeedsData(Scope scope)
    {
        if (scope.is_function_scope())
        {
            // Default constructors don't need data (they cannot contain inner functions
            // defined by the user). Other functions do.
            return !IsDefaultConstructor(scope.AsDeclarationScope().function_kind());
        }
        if (!scope.is_hidden())
        {
            foreach (Variable var in scope.locals())
            {
                if (IsSerializableVariableMode(var.mode())) return true;
            }
        }
        for (Scope? inner = scope.inner_scope(); inner != null; inner = inner.sibling())
        {
            if (ScopeNeedsData(inner)) return true;
        }
        return false;
    }

    // Serialize the data (V8: Serialize(Zone*) / Serialize(Isolate*)).
    public PreparseData Serialize()
    {
        PreparseData data = _byteData.CopyToZone(_numInnerWithData);
        int i = 0;
        foreach (PreparseDataBuilder builder in _children)
        {
            if (!builder.HasData()) continue;
            PreparseData child = builder.Serialize();
            data.set_child(i++, child);
        }
        return data;
    }

    private void FinalizeChildren()
    {
        _children = _childrenBuffer!.ToArray();
        _childrenBuffer = null;
    }

    private void AddChild(PreparseDataBuilder child) => _childrenBuffer!.Add(child);

    private void SaveDataForScope(Scope scope)
    {
        byte scope_data_flags = 0;
        if (scope.is_declaration_scope() && scope.AsDeclarationScope().sloppy_eval_can_extend_vars())
            scope_data_flags |= ScopeSloppyEvalCanExtendVarsBit;
        if (scope.inner_scope_calls_eval()) scope_data_flags |= InnerScopeCallsEvalField;
        if (scope.is_function_scope() && scope.AsDeclarationScope().needs_private_name_context_chain_recalc())
            scope_data_flags |= NeedsPrivateNameContextChainRecalcField;
        if (scope.is_class_scope() && scope.AsClassScope().should_save_class_variable())
            scope_data_flags |= ShouldSaveClassVariableIndexField;
        _byteData.Reserve(PreparseByteDataConstants.kUint8Size);
        _byteData.WriteUint8(scope_data_flags);

        if (scope.is_function_scope())
        {
            Variable? function = scope.AsDeclarationScope().function_var();
            if (function != null) SaveDataForVariable(function);
        }

        foreach (Variable var in scope.locals())
        {
            if (IsSerializableVariableMode(var.mode())) SaveDataForVariable(var);
        }

        SaveDataForInnerScopes(scope);
    }

    private void SaveDataForVariable(Variable var)
    {
        byte variable_data = 0;
        if (var.maybe_assigned() == MaybeAssignedFlag.kMaybeAssigned) variable_data |= VariableMaybeAssignedField;
        if (var.has_forced_context_allocation()) variable_data |= VariableContextAllocatedField;
        _byteData.Reserve(PreparseByteDataConstants.kUint8Size);
        _byteData.WriteQuarter(variable_data);
    }

    private void SaveDataForInnerScopes(Scope scope)
    {
        // Inner scopes are stored in the reverse order, but we'd like to write the
        // data in the logical order. There might be many inner scopes, so we don't
        // want to recurse here.
        for (Scope? inner = scope.inner_scope(); inner != null; inner = inner.sibling())
        {
            if (inner.IsSkippableFunctionScope())
            {
                // Don't save data about function scopes, since they'll have their own
                // PreparseDataBuilder where their data is saved.
                continue;
            }
            if (!ScopeNeedsData(inner)) continue;
            SaveDataForScope(inner);
        }
    }

    private bool SaveDataForSkippableFunction(PreparseDataBuilder builder)
    {
        DeclarationScope function_scope = builder._functionScope!;
        // Start position is used for a sanity check when consuming the data, we could
        // remove it in the future if we're very pressed for space but it's been good
        // at catching bugs in the wild so far.
        _byteData.WriteVarint32((uint)function_scope.start_position());
        _byteData.WriteVarint32((uint)function_scope.end_position());

        bool has_data = builder.HasData();
        bool length_equals_parameters = function_scope.num_parameters() == builder._functionLength;
        uint has_data_and_num_parameters =
            (has_data ? HasDataField : 0) |
            (length_equals_parameters ? LengthEqualsParametersField : 0) |
            (((uint)function_scope.num_parameters() & NumberOfParametersMask) << NumberOfParametersShift);
        _byteData.WriteVarint32(has_data_and_num_parameters);
        if (!length_equals_parameters)
        {
            _byteData.WriteVarint32((uint)builder._functionLength);
        }
        _byteData.WriteVarint32((uint)builder._numInnerFunctions);

        byte language_and_super =
            (byte)((is_strict(function_scope.language_mode()) ? LanguageField : 0) |
                   (function_scope.uses_super_property() ? UsesSuperField : 0));
        _byteData.WriteQuarter(language_and_super);
        return has_data;
    }

    // Decoding helpers shared with ConsumedPreparseData.
    internal static bool DecodeHasData(uint v) => (v & HasDataField) != 0;
    internal static bool DecodeLengthEqualsParameters(uint v) => (v & LengthEqualsParametersField) != 0;
    internal static int DecodeNumberOfParameters(uint v) => (int)((v >> NumberOfParametersShift) & NumberOfParametersMask);
    internal static LanguageMode DecodeLanguage(byte v) => (v & LanguageField) != 0 ? LanguageMode.Strict : LanguageMode.Sloppy;
    internal static bool DecodeUsesSuper(byte v) => (v & UsesSuperField) != 0;
    internal static bool DecodeSloppyEvalCanExtendVars(uint v) => (v & ScopeSloppyEvalCanExtendVarsBit) != 0;
    internal static bool DecodeInnerScopeCallsEval(uint v) => (v & InnerScopeCallsEvalField) != 0;
    internal static bool DecodeNeedsPrivateNameContextChainRecalc(uint v) => (v & NeedsPrivateNameContextChainRecalcField) != 0;
    internal static bool DecodeShouldSaveClassVariableIndex(uint v) => (v & ShouldSaveClassVariableIndexField) != 0;
    internal static bool DecodeVariableMaybeAssigned(byte v) => (v & VariableMaybeAssignedField) != 0;
    internal static bool DecodeVariableContextAllocated(byte v) => (v & VariableContextAllocatedField) != 0;
}

public abstract class ProducedPreparseData
{
    // If there is data (if the Scope contains skippable inner functions), return
    // the data; otherwise null.
    public abstract PreparseData? Serialize();

    // Create a ProducedPreparseData which is a proxy for a previous
    // produced PreparseData in zone.
    public static ProducedPreparseData For(PreparseDataBuilder builder) => new BuilderProducedPreparseData(builder);

    // Create a ProducedPreparseData which is a proxy for a previous
    // produced PreparseData.
    public static ProducedPreparseData For(PreparseData data) => new OnHeapProducedPreparseData(data);

    private sealed class BuilderProducedPreparseData(PreparseDataBuilder builder) : ProducedPreparseData
    {
        public override PreparseData? Serialize() => builder.Serialize();
    }

    private sealed class OnHeapProducedPreparseData(PreparseData data) : ProducedPreparseData
    {
        public override PreparseData? Serialize() => data;
    }
}

public sealed class ConsumedPreparseData
{
    private readonly PreparseData _data;
    private readonly ByteData _scopeData = new();
    // When consuming the data, these indexes point to the data we're going to
    // consume next.
    private int _childIndex;

    private ConsumedPreparseData(PreparseData data)
    {
        _data = data;
        _scopeData.SetData(data.scope_data());
    }

    // Creates a ConsumedPreparseData representing the data of |data|.
    public static ConsumedPreparseData? For(PreparseData? data) => data == null ? null : new ConsumedPreparseData(data);

    public sealed class ByteData
    {
        private byte[] _data = [];
        private int _index;
        private byte _storedQuarters;
        private byte _storedByte;

        internal void SetData(byte[] data) => _data = data;

        public void SetPosition(int position) => _index = position;

        public int RemainingBytes() => _data.Length - _index;

        public bool HasRemainingBytes(int bytes) => _index <= _data.Length && bytes <= RemainingBytes();

        public int ReadUint32()
        {
            int result = _data[_index] + (_data[_index + 1] << 8) + (_data[_index + 2] << 16) + (_data[_index + 3] << 24);
            _index += 4;
            _storedQuarters = 0;
            return result;
        }

        public int ReadVarint32()
        {
            int value = 0;
            bool has_another_byte;
            int shift = 0;
            do
            {
                byte b = _data[_index++];
                value |= (b & 0x7F) << shift;
                shift += 7;
                has_another_byte = (b & 0x80) != 0;
            } while (has_another_byte);
            _storedQuarters = 0;
            return value;
        }

        public byte ReadUint8()
        {
            _storedQuarters = 0;
            return _data[_index++];
        }

        public byte ReadQuarter()
        {
            if (_storedQuarters == 0)
            {
                _storedByte = _data[_index++];
                _storedQuarters = 4;
            }
            // Read the first 2 bits from stored_byte_.
            byte result = (byte)((_storedByte >> 6) & 3);
            --_storedQuarters;
            _storedByte <<= 2;
            return result;
        }
    }

    private ProducedPreparseData? GetChildData(int child_index)
    {
        if (_data.children_length() <= child_index) throw new InvalidOperationException("CHECK_GT(children_length, child_index)");
        PreparseData? child_data = _data.get_child(child_index);
        if (child_data == null) return null;
        return ProducedPreparseData.For(child_data);
    }

    public ProducedPreparseData? GetDataForSkippableFunction(int start_position, out int end_position, out int num_parameters,
                                                             out int function_length, out int num_inner_functions,
                                                             out bool uses_super_property, out LanguageMode language_mode)
    {
        // The skippable function *must* be the next function in the data. Use the
        // start position as a sanity check.
        if (!_scopeData.HasRemainingBytes(PreparseByteDataConstants.kSkippableFunctionMinDataSize))
            throw new InvalidOperationException("CHECK(scope_data_->HasRemainingBytes(kSkippableFunctionMinDataSize))");
        int start_position_from_data = _scopeData.ReadVarint32();
        if (start_position != start_position_from_data)
            throw new InvalidOperationException("CHECK_EQ(start_position, start_position_from_data)");
        end_position = _scopeData.ReadVarint32();

        uint has_data_and_num_parameters = (uint)_scopeData.ReadVarint32();
        bool has_data = PreparseDataBuilder.DecodeHasData(has_data_and_num_parameters);
        num_parameters = PreparseDataBuilder.DecodeNumberOfParameters(has_data_and_num_parameters);
        bool length_equals_parameters = PreparseDataBuilder.DecodeLengthEqualsParameters(has_data_and_num_parameters);
        if (length_equals_parameters)
        {
            function_length = num_parameters;
        }
        else
        {
            function_length = _scopeData.ReadVarint32();
        }
        num_inner_functions = _scopeData.ReadVarint32();

        byte language_and_super = _scopeData.ReadQuarter();
        language_mode = PreparseDataBuilder.DecodeLanguage(language_and_super);
        uses_super_property = PreparseDataBuilder.DecodeUsesSuper(language_and_super);

        if (!has_data) return null;

        // Retrieve the corresponding PreparseData and associate it to the
        // skipped function. If the skipped functions contains inner functions, those
        // can be skipped when the skipped function is eagerly parsed.
        return GetChildData(_childIndex++);
    }

    // Restores the information needed for allocating the Scope's (and its
    // subscopes') variables.
    public void RestoreScopeAllocationData(DeclarationScope scope, AstValueFactory ast_value_factory)
    {
        RestoreDataForScope(scope, ast_value_factory);
    }

    private void RestoreDataForScope(Scope scope, AstValueFactory ast_value_factory)
    {
        if (scope.is_declaration_scope() && scope.AsDeclarationScope().is_skipped_function())
        {
            return;
        }

        // It's possible that scope is not present in the data at all (since PreParser
        // doesn't create the corresponding scope). In this case, the Scope won't
        // contain any variables for which we need the data.
        if (!PreparseDataBuilder.ScopeNeedsData(scope)) return;

        if (!_scopeData.HasRemainingBytes(PreparseByteDataConstants.kUint8Size))
            throw new InvalidOperationException("CHECK(scope_data_->HasRemainingBytes(kUint8Size))");
        uint scope_data_flags = _scopeData.ReadUint8();
        if (PreparseDataBuilder.DecodeSloppyEvalCanExtendVars(scope_data_flags))
        {
            scope.RecordEvalCall();
        }
        if (PreparseDataBuilder.DecodeInnerScopeCallsEval(scope_data_flags))
        {
            scope.RecordInnerScopeEvalCall();
        }
        if (PreparseDataBuilder.DecodeNeedsPrivateNameContextChainRecalc(scope_data_flags))
        {
            scope.AsDeclarationScope().RecordNeedsPrivateNameContextChainRecalc();
        }
        if (PreparseDataBuilder.DecodeShouldSaveClassVariableIndex(scope_data_flags))
        {
            Variable? var = scope.AsClassScope().class_variable();
            // An anonymous class whose class variable needs to be saved might not
            // have the class variable created during reparse since we skip parsing
            // the inner scopes that contain potential access to static private
            // methods. So create it now.
            if (var == null)
            {
                var = scope.AsClassScope().DeclareClassVariable(ast_value_factory, ast_value_factory.empty_string(),
                                                                kNoSourcePosition);
                var factory = new AstNodeFactory(ast_value_factory);
                Declaration declaration = factory.NewVariableDeclaration(kNoSourcePosition);
                scope.declarations().Add(declaration);
                declaration.set_var(var);
            }
            var.set_is_used();
            var.ForceContextAllocation();
            var.set_maybe_assigned();
            scope.AsClassScope().set_should_save_class_variable();
        }

        if (scope.is_function_scope())
        {
            Variable? function = scope.AsDeclarationScope().function_var();
            if (function != null) RestoreDataForVariable(function);
        }
        foreach (Variable var in scope.locals())
        {
            if (IsSerializableVariableMode(var.mode())) RestoreDataForVariable(var);
        }

        RestoreDataForInnerScopes(scope, ast_value_factory);
    }

    private void RestoreDataForVariable(Variable var)
    {
        byte variable_data = _scopeData.ReadQuarter();
        if (PreparseDataBuilder.DecodeVariableMaybeAssigned(variable_data))
        {
            var.SetMaybeAssigned();
        }
        if (PreparseDataBuilder.DecodeVariableContextAllocated(variable_data))
        {
            var.set_is_used();
            var.ForceContextAllocation();
        }
    }

    private void RestoreDataForInnerScopes(Scope scope, AstValueFactory ast_value_factory)
    {
        for (Scope? inner = scope.inner_scope(); inner != null; inner = inner.sibling())
        {
            RestoreDataForScope(inner, ast_value_factory);
        }
    }
}
