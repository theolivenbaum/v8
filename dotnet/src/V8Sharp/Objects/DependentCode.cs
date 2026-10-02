// Port of src/objects/dependent-code.{h,cc}: the optimized code that depends
// on a heap object (a map's stability, an initial map, a property cell's
// type ...), by dependency group, and its invalidation when the object
// changes (DependentCode::DeoptimizeDependencyGroups, called by the object
// model at the places V8 calls it).
//
// Deviation: V8 keeps the list on the object itself (Map::dependent_code,
// PropertyCell::dependent_code) as a weak array. V8Sharp keeps it in a
// ConditionalWeakTable keyed by the object, with weak references to the
// code, so a dependency neither keeps the object nor the code alive.
using System.Runtime.CompilerServices;
using V8Sharp.Deoptimizer;
using V8Sharp.Maglev;

namespace V8Sharp.Objects;

/// <summary>DependentCode.</summary>
public static class DependentCode
{
    /// <summary>DependentCode::DependencyGroup.</summary>
    [Flags]
    public enum DependencyGroups
    {
        None = 0,
        Transition = 1 << 0,
        PrototypeCheck = 1 << 1,
        PropertyCellChanged = 1 << 2,
        FieldConst = 1 << 3,
        FieldType = 1 << 4,
        FieldRepresentation = 1 << 5,
        InitialMapChanged = 1 << 6,
        AllocationSiteTenuringChanged = 1 << 7,
        AllocationSiteTransitionChanged = 1 << 8,
        ScriptContextSlotPropertyChanged = 1 << 9,
        EmptyContextExtension = 1 << 10,
    }

    sealed class Entries
    {
        public readonly List<(WeakReference<MaglevCode> Code, DependencyGroups Groups)> List = [];
    }

    static readonly ConditionalWeakTable<HeapObject, Entries> s_dependentCode = new();

    /// <summary>Whether any code was ever registered (a fast exit for the common no-optimizer case).</summary>
    static volatile bool s_any;

    /// <summary>DependentCode::InstallDependency.</summary>
    public static void InstallDependency(Isolate isolate, MaglevCode code, HeapObject obj, DependencyGroups groups)
    {
        s_any = true;
        Entries entries = s_dependentCode.GetValue(obj, static _ => new Entries());
        lock (entries)
        {
            // Compact entries whose code is gone or invalidated.
            entries.List.RemoveAll(static e => !e.Code.TryGetTarget(out MaglevCode? c) || c.MarkedForDeoptimization);
            entries.List.Add((new WeakReference<MaglevCode>(code), groups));
        }
    }

    /// <summary>Called when dependencies of <paramref name="obj"/> are invalidated (for tests and tracing).</summary>
    public static event Action<Isolate, HeapObject, DependencyGroups>? OnDeoptimize;

    /// <summary>DependentCode::DeoptimizeDependencyGroups: marks the code depending on <paramref name="obj"/> for deoptimization.</summary>
    public static void DeoptimizeDependencyGroups(Isolate isolate, HeapObject obj, DependencyGroups groups)
    {
        if (groups == DependencyGroups.None) return;
        OnDeoptimize?.Invoke(isolate, obj, groups);
        if (!s_any || !s_dependentCode.TryGetValue(obj, out Entries? entries)) return;
        List<MaglevCode>? invalidated = null;
        lock (entries)
        {
            for (int i = entries.List.Count - 1; i >= 0; i--)
            {
                (WeakReference<MaglevCode> weak, DependencyGroups g) = entries.List[i];
                if (!weak.TryGetTarget(out MaglevCode? code) || code.MarkedForDeoptimization)
                {
                    entries.List.RemoveAt(i);
                    continue;
                }
                if ((g & groups) == 0) continue;
                (invalidated ??= []).Add(code);
                entries.List.RemoveAt(i);
            }
        }
        if (invalidated is null) return;
        foreach (MaglevCode code in invalidated)
        {
            MaglevCompiler.InvalidateCode(isolate, code, ReasonFor(groups));
        }
    }

    // ---- Protectors ---------------------------------------------------------------------------------

    // V8 keeps each protector in a PropertyCell, and optimized code depends on
    // it with kPropertyCellChangedGroup (CompilationDependencies::
    // DependOnProtector). V8Sharp's protectors are bools (Protectors.cs), so
    // the dependency is registered on a stand-in Cell per isolate and
    // protector, invalidated through Protectors.OnInvalidate.
    static readonly ConditionalWeakTable<Isolate, Dictionary<string, Cell>> s_protectorCells = new();

    static DependentCode() => Protectors.OnInvalidate += static (isolate, name) =>
    {
        if (s_protectorCells.TryGetValue(isolate, out Dictionary<string, Cell>? cells) && cells.TryGetValue(name, out Cell? cell))
        {
            DeoptimizeDependencyGroups(isolate, cell, DependencyGroups.PropertyCellChanged);
        }
    };

    /// <summary>The object code depending on the protector <paramref name="name"/> registers on.</summary>
    public static HeapObject ProtectorCell(Isolate isolate, string name)
    {
        Dictionary<string, Cell> cells = s_protectorCells.GetValue(isolate, static _ => new Dictionary<string, Cell>());
        if (!cells.TryGetValue(name, out Cell? cell)) cells[name] = cell = new Cell(JSValue.Undefined);
        return cell;
    }

    static LazyDeoptimizeReason ReasonFor(DependencyGroups groups)
    {
        if ((groups & DependencyGroups.PropertyCellChanged) != 0) return LazyDeoptimizeReason.kPropertyCellChange;
        if ((groups & DependencyGroups.PrototypeCheck) != 0) return LazyDeoptimizeReason.kPrototypeChange;
        if ((groups & DependencyGroups.InitialMapChanged) != 0) return LazyDeoptimizeReason.kInitialMapChange;
        if ((groups & DependencyGroups.FieldConst) != 0) return LazyDeoptimizeReason.kFieldTypeConstChange;
        if ((groups & DependencyGroups.FieldType) != 0) return LazyDeoptimizeReason.kFieldTypeChange;
        if ((groups & DependencyGroups.FieldRepresentation) != 0) return LazyDeoptimizeReason.kFieldRepresentationChange;
        if ((groups & DependencyGroups.Transition) != 0) return LazyDeoptimizeReason.kMapDeprecated;
        return LazyDeoptimizeReason.kTesting;
    }
}
