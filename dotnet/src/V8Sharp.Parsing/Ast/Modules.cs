// Copyright 2015 the V8 project authors. All rights reserved.
// Use of this source code is governed by a BSD-style license that can be
// found in the LICENSE file.

// Port of src/ast/modules.h, src/ast/modules.cc,
// src/parsing/import-attributes.h and import-attributes.cc.
//
// The heap serialization (Entry::Serialize, AstModuleRequest::Serialize,
// SerializeRegularExports) belongs to the engine; the ordered maps it reads
// are exposed here in V8's iteration order.

using V8Sharp.Common;
using V8Sharp.Parsing;

namespace V8Sharp.Ast;

// Orders AstRawStrings by AstRawString::Compare (ImportAttributesKeyComparer,
// SourceTextModuleDescriptor::AstRawStringComparer).
public sealed class AstRawStringComparer : IComparer<AstRawString>
{
    public static readonly AstRawStringComparer Instance = new();
    public int Compare(AstRawString? lhs, AstRawString? rhs) => AstRawString.Compare(lhs!, rhs!);
}

// ImportAttributes: key -> (value, location), ordered by key.
public sealed class ImportAttributes() : SortedDictionary<AstRawString, (AstRawString value, Scanner.Location location)>(AstRawStringComparer.Instance)
{
}

public sealed class SourceTextModuleDescriptor
{
    public sealed class Entry(Scanner.Location loc)
    {
        public Scanner.Location location = loc;
        public AstRawString? export_name;
        public AstRawString? local_name;
        public AstRawString? import_name;

        // The module_request value records the order in which modules are
        // requested. It also functions as an index into the SourceTextModuleInfo's
        // array of module specifiers and into the Module's array of requested
        // modules.  A negative value means no module request.
        public int module_request = -1;

        // Import/export entries that are associated with a MODULE-allocated
        // variable (i.e. regular_imports and regular_exports after Validate) use
        // the cell_index value to encode the location of their cell.  During
        // variable allocation, this will be be copied into the variable's index
        // field.
        // Entries that are not associated with a MODULE-allocated variable have
        // GetCellIndexKind(cell_index) == kInvalid.
        public int cell_index;
    }

    public enum CellIndexKind { kInvalid, kExport, kImport }

    public static CellIndexKind GetCellIndexKind(int cell_index)
    {
        if (cell_index > 0) return CellIndexKind.kExport;
        if (cell_index < 0) return CellIndexKind.kImport;
        return CellIndexKind.kInvalid;
    }

    public sealed class AstModuleRequest(AstRawString specifier, ModuleImportPhase phase,
                                         ImportAttributes import_attributes, int position, int index)
    {
        private readonly AstRawString _specifier = specifier;
        private readonly ImportAttributes _importAttributes = import_attributes;
        private readonly ModuleImportPhase _phase = phase;
        private readonly int _position = position;
        private readonly int _index = index;

        public AstRawString specifier() => _specifier;
        public ImportAttributes import_attributes() => _importAttributes;
        public ModuleImportPhase phase() => _phase;

        // The JS source code position of the request, used for reporting errors.
        public int position() => _position;

        // The index at which we will place the request in SourceTextModuleInfo's
        // module_requests FixedArray.
        public int index() => _index;
    }

    // Custom content-based comparer, to keep the maps stable across parses.
    public sealed class ModuleRequestComparer : IComparer<AstModuleRequest>
    {
        public static readonly ModuleRequestComparer Instance = new();

        // ModuleRequestComparer::operator() is a less-than; this returns the
        // corresponding three-way comparison.
        public int Compare(AstModuleRequest? lhs, AstModuleRequest? rhs)
        {
            if (Less(lhs!, rhs!)) return -1;
            if (Less(rhs!, lhs!)) return 1;
            return 0;
        }

        private static bool Less(AstModuleRequest lhs, AstModuleRequest rhs)
        {
            int specifier_comparison = AstRawString.Compare(lhs.specifier(), rhs.specifier());
            if (specifier_comparison != 0)
            {
                return specifier_comparison < 0;
            }

            using var lhsIt = lhs.import_attributes().GetEnumerator();
            using var rhsIt = rhs.import_attributes().GetEnumerator();
            while (true)
            {
                bool lhsHas = lhsIt.MoveNext();
                bool rhsHas = rhsIt.MoveNext();
                if (!lhsHas || !rhsHas) break;
                int assertion_key_comparison = AstRawString.Compare(lhsIt.Current.Key, rhsIt.Current.Key);
                if (assertion_key_comparison != 0)
                {
                    return assertion_key_comparison < 0;
                }

                int assertion_value_comparison = AstRawString.Compare(lhsIt.Current.Value.value, rhsIt.Current.Value.value);
                if (assertion_value_comparison != 0)
                {
                    return assertion_value_comparison < 0;
                }
            }

            if (lhs.import_attributes().Count != rhs.import_attributes().Count)
            {
                return lhs.import_attributes().Count < rhs.import_attributes().Count;
            }

            if (lhs.phase() != rhs.phase())
            {
                return lhs.phase() < rhs.phase();
            }

            return false;
        }
    }

    // ZoneMultimap<const AstRawString*, Entry*, AstRawStringComparer>: sorted by
    // key, equal keys in insertion order.
    public sealed class RegularExportMap : IEnumerable<KeyValuePair<AstRawString, Entry>>
    {
        private readonly List<KeyValuePair<AstRawString, Entry>> _entries = [];

        public int Count => _entries.Count;

        public KeyValuePair<AstRawString, Entry> this[int index] => _entries[index];

        public void Emplace(AstRawString key, Entry entry)
        {
            // upper_bound: insert after all entries with key <= key.
            int lo = 0, hi = _entries.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (AstRawString.Compare(key, _entries[mid].Key) < 0) hi = mid;
                else lo = mid + 1;
            }
            _entries.Insert(lo, new KeyValuePair<AstRawString, Entry>(key, entry));
        }

        public void RemoveAt(int index) => _entries.RemoveAt(index);

        public List<KeyValuePair<AstRawString, Entry>>.Enumerator GetEnumerator() => _entries.GetEnumerator();
        IEnumerator<KeyValuePair<AstRawString, Entry>> IEnumerable<KeyValuePair<AstRawString, Entry>>.GetEnumerator() => _entries.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _entries.GetEnumerator();
    }

    private readonly SortedSet<AstModuleRequest> _moduleRequests = new(ModuleRequestComparer.Instance);
    private readonly List<Entry> _specialExports = [];
    private readonly SortedDictionary<AstRawString, Entry> _namespaceImports = new(AstRawStringComparer.Instance);
    private readonly RegularExportMap _regularExports = new();
    private readonly SortedDictionary<AstRawString, Entry> _regularImports = new(AstRawStringComparer.Instance);

    // Module requests.
    public SortedSet<AstModuleRequest> module_requests() => _moduleRequests;

    // Namespace imports.
    public SortedDictionary<AstRawString, Entry> namespace_imports() => _namespaceImports;

    // All the remaining imports, indexed by local name.
    public SortedDictionary<AstRawString, Entry> regular_imports() => _regularImports;

    // Star exports and explicitly indirect exports.
    public List<Entry> special_exports() => _specialExports;

    // All the remaining exports, indexed by local name.
    // After canonicalization (see Validate), these are exactly the local exports.
    public RegularExportMap regular_exports() => _regularExports;

    // The following Add* methods are high-level convenience functions for use by
    // the parser.

    // import x from "foo.js";
    // import {x} from "foo.js";
    // import {x as y} from "foo.js";
    public bool AddImport(AstRawString import_name, AstRawString local_name, AstRawString specifier,
                          ModuleImportPhase import_phase, ImportAttributes import_attributes,
                          Scanner.Location loc, Scanner.Location specifier_loc)
    {
        var entry = new Entry(loc)
        {
            local_name = local_name,
            import_name = import_name,
        };
        entry.module_request = AddModuleRequest(specifier, import_phase, import_attributes, specifier_loc);
        return AddRegularImport(entry);
    }

    // import * as x from "foo.js";
    public bool AddStarImport(AstRawString local_name, AstRawString specifier, ModuleImportPhase import_phase,
                              ImportAttributes import_attributes, Scanner.Location loc, Scanner.Location specifier_loc)
    {
        var entry = new Entry(loc) { local_name = local_name };
        entry.module_request = AddModuleRequest(specifier, import_phase, import_attributes, specifier_loc);
        return AddNamespaceImport(entry);
    }

    // import "foo.js";
    // import {} from "foo.js";
    // export {} from "foo.js";  (sic!)
    public void AddEmptyImport(AstRawString specifier, ImportAttributes import_attributes, Scanner.Location specifier_loc)
    {
        AddModuleRequest(specifier, ModuleImportPhase.kEvaluation, import_attributes, specifier_loc);
    }

    // export {x};
    // export {x as y};
    // export VariableStatement
    // export Declaration
    // export default ...
    public void AddExport(AstRawString local_name, AstRawString export_name, Scanner.Location loc)
    {
        var entry = new Entry(loc)
        {
            export_name = export_name,
            local_name = local_name,
        };
        AddRegularExport(entry);
    }

    // export {x} from "foo.js";
    // export {x as y} from "foo.js";
    public void AddExport(AstRawString import_name, AstRawString export_name, AstRawString specifier,
                          ImportAttributes import_attributes, Scanner.Location loc, Scanner.Location specifier_loc)
    {
        var entry = new Entry(loc)
        {
            export_name = export_name,
            import_name = import_name,
        };
        entry.module_request = AddModuleRequest(specifier, ModuleImportPhase.kEvaluation, import_attributes, specifier_loc);
        AddSpecialExport(entry);
    }

    // export * from "foo.js";
    public void AddStarExport(AstRawString specifier, ImportAttributes import_attributes, Scanner.Location loc,
                              Scanner.Location specifier_loc)
    {
        var entry = new Entry(loc);
        entry.module_request = AddModuleRequest(specifier, ModuleImportPhase.kEvaluation, import_attributes, specifier_loc);
        AddSpecialExport(entry);
    }

    public void AddRegularExport(Entry entry) => _regularExports.Emplace(entry.local_name!, entry);

    public void AddSpecialExport(Entry entry) => _specialExports.Add(entry);

    public bool AddRegularImport(Entry entry) => _regularImports.TryAdd(entry.local_name!, entry);

    public bool AddNamespaceImport(Entry entry) => _namespaceImports.TryAdd(entry.local_name!, entry);

    private int AddModuleRequest(AstRawString specifier, ModuleImportPhase import_phase,
                                 ImportAttributes import_attributes, Scanner.Location specifier_loc)
    {
        int module_requests_count = _moduleRequests.Count;
        var request = new AstModuleRequest(specifier, import_phase, import_attributes, specifier_loc.beg_pos,
                                           module_requests_count);
        if (_moduleRequests.TryGetValue(request, out AstModuleRequest? existing))
        {
            return existing.index();
        }
        _moduleRequests.Add(request);
        return request.index();
    }

    // Find any implicitly indirect exports and make them explicit.
    //
    // An explicitly indirect export is an export entry arising from an export
    // statement of the following form:
    //   export {a as c} from "X";
    // An implicitly indirect export corresponds to
    //   export {b as c};
    // in the presence of an import statement of the form
    //   import {a as b} from "X";
    // This function finds such implicitly indirect export entries and rewrites
    // them by filling in the import name and module request, as well as nulling
    // out the local name.  Effectively, it turns
    //   import {a as b} from "X"; export {b as c};
    // into:
    //   import {a as b} from "X"; export {a as c} from "X";
    // (The import entry is never deleted.)
    private void MakeIndirectExportsExplicit(ParsingFlags flags)
    {
        for (int it = 0; it < _regularExports.Count;)
        {
            Entry entry = _regularExports[it].Value;
            Entry? import_entry = null;
            if (_regularImports.TryGetValue(entry.local_name!, out Entry? import))
            {
                import_entry = import;
            }
            else if (flags.js_esm_ns_reexport && _namespaceImports.TryGetValue(entry.local_name!, out Entry? ns_import))
            {
                // Found a namespace re-export.
                import_entry = ns_import;
            }

            if (import_entry != null)
            {
                // Found an indirect export.  Patch export entry and move it from regular
                // to special.
                entry.import_name = import_entry.import_name;
                entry.module_request = import_entry.module_request;
                // Hack: When the indirect export cannot be resolved, we want the error
                // message to point at the import statement, not at the export statement.
                // Therefore we overwrite [entry]'s location here.  Note that Validate()
                // has already checked for duplicate exports, so it's guaranteed that we
                // won't need to report any error pointing at the (now lost) export
                // location.
                entry.location = import_entry.location;
                entry.local_name = null;
                AddSpecialExport(entry);
                _regularExports.RemoveAt(it);
            }
            else
            {
                it++;
            }
        }
    }

    // Assign a cell_index of -1,-2,... to regular imports.
    // Assign a cell_index of +1,+2,... to regular (local) exports.
    // Assign a cell_index of 0 to anything else.
    private void AssignCellIndices()
    {
        int export_index = 1;
        for (int it = 0; it < _regularExports.Count;)
        {
            AstRawString current_key = _regularExports[it].Key;
            // This local name may be exported under multiple export names.  Assign the
            // same index to each such entry.
            do
            {
                Entry entry = _regularExports[it].Value;
                entry.cell_index = export_index;
                it++;
            } while (it < _regularExports.Count && _regularExports[it].Key == current_key);
            export_index++;
        }

        int import_index = -1;
        foreach (var elem in _regularImports)
        {
            Entry entry = elem.Value;
            entry.cell_index = import_index;
            import_index--;
        }
    }

    private static Entry? BetterDuplicate(Entry candidate, SortedDictionary<AstRawString, Entry> export_names, Entry? current_duplicate)
    {
        if (export_names.TryAdd(candidate.export_name!, candidate)) return current_duplicate;
        current_duplicate ??= export_names[candidate.export_name!];
        return candidate.location.beg_pos > current_duplicate.location.beg_pos ? candidate : current_duplicate;
    }

    // If there are multiple export entries with the same export name, return the
    // last of them (in source order).  Otherwise return null.
    private Entry? FindDuplicateExport()
    {
        Entry? duplicate = null;
        // V8 uses a ZoneMap keyed by AstRawString pointers here, which only needs
        // identity; the result does not depend on the ordering.
        var export_names = new SortedDictionary<AstRawString, Entry>(AstRawStringComparer.Instance);
        foreach (var elem in _regularExports)
        {
            duplicate = BetterDuplicate(elem.Value, export_names, duplicate);
        }
        foreach (Entry entry in _specialExports)
        {
            if (entry.export_name == null) continue; // Star export.
            duplicate = BetterDuplicate(entry, export_names, duplicate);
        }
        return duplicate;
    }

    // Check if module is well-formed and report error if not.
    // Also canonicalize indirect exports.
    public bool Validate(ModuleScope module_scope, PendingCompilationErrorHandler error_handler, ParsingFlags flags)
    {
        // Report error iff there are duplicate exports.
        {
            Entry? entry = FindDuplicateExport();
            if (entry != null)
            {
                error_handler.ReportMessageAt(entry.location.beg_pos, entry.location.end_pos,
                                              MessageTemplate.DuplicateExport, entry.export_name);
                return false;
            }
        }

        // Report error iff there are exports of non-existent local names.
        foreach (var elem in _regularExports)
        {
            Entry entry = elem.Value;
            Variable? var = module_scope.LookupLocal(entry.local_name!);
            if (var == null)
            {
                error_handler.ReportMessageAt(entry.location.beg_pos, entry.location.end_pos,
                                              MessageTemplate.ModuleExportUndefined, entry.local_name);
                return false;
            }
            var.set_is_used();
        }

        MakeIndirectExportsExplicit(flags);
        AssignCellIndices();
        return true;
    }
}
