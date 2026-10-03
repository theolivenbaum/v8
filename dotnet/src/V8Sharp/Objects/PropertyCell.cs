// Port of src/objects/property-cell{.h,-inl.h} and the PropertyCell parts of
// src/objects/objects.cc: the cells of global object properties.
namespace V8Sharp.Objects;

/// <summary>
/// V8's PropertyCell: one global property (name, value, details), shared with
/// inline caches so they can check constness and type.
/// </summary>
public sealed class PropertyCell : HeapObject
{
    public readonly Name Name;
    public JSValue Value;
    public PropertyDetails PropertyDetails;

    public PropertyCell(Name name, PropertyDetails details, JSValue value) : base(InstanceType.PropertyCellType)
    {
        Name = name;
        PropertyDetails = details;
        Value = value;
    }

    /// <summary>PropertyCell::Transition: update details and value together.</summary>
    public void Transition(PropertyDetails newDetails, JSValue newValue)
    {
        PropertyDetails = newDetails;
        Value = newValue;
    }

    /// <summary>PropertyCell::UpdatePropertyDetailsExceptCellType.</summary>
    public void UpdatePropertyDetailsExceptCellType(PropertyDetails details)
    {
        PropertyDetails oldDetails = PropertyDetails;
        PropertyDetails = details.SetCellType(oldDetails.CellType);
        // Deopt when making a writable property read-only. The reverse direction
        // is uninteresting because Turbofan does not currently rely on read-only
        // unless the property is also configurable, in which case it will stay
        // read-only forever.
        if (!oldDetails.IsReadOnly && details.IsReadOnly && Isolate.Current is { } isolate)
        {
            DependentCode.DeoptimizeDependencyGroups(isolate, this, DependentCode.DependencyGroups.PropertyCellChanged);
        }
    }

    public static PropertyCellType InitialType(Isolate isolate, in JSValue value) =>
        value.IsUndefined ? PropertyCellType.Undefined : PropertyCellType.Constant;

    static bool RemainsConstantType(PropertyCell cell, in JSValue value)
    {
        // TODO(dcarney): double->smi and smi->double transition from kConstant
        if (cell.Value.IsSmi && value.IsSmi) return true;
        if (cell.Value.HeapObjectOrNull is JSReceiver a && value.HeapObjectOrNull is JSReceiver b)
        {
            Map map = b.Map;
            return ReferenceEquals(a.Map, map) && map.IsStable;
        }
        return false;
    }

    public static PropertyCellType UpdatedType(Isolate isolate, PropertyCell cell, in JSValue value, PropertyDetails details)
    {
        switch (details.CellType)
        {
            case PropertyCellType.Undefined:
                return PropertyCellType.Constant;
            case PropertyCellType.Constant:
                if (value.IsIdenticalTo(cell.Value)) return PropertyCellType.Constant;
                goto case PropertyCellType.ConstantType;
            case PropertyCellType.ConstantType:
                if (RemainsConstantType(cell, value)) return PropertyCellType.ConstantType;
                return PropertyCellType.Mutable;
            case PropertyCellType.Mutable:
                return PropertyCellType.Mutable;
            default:
                throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>PropertyCell::ClearAndInvalidate: the cell is removed from its dictionary.</summary>
    public void ClearAndInvalidate(Isolate isolate)
    {
        PropertyDetails details = PropertyDetails.SetCellType(PropertyCellType.Constant);
        Transition(details, Oddball.PropertyCellHole);
        DependentCode.DeoptimizeDependencyGroups(isolate, this, DependentCode.DependencyGroups.PropertyCellChanged);
    }

    public static PropertyCell InvalidateAndReplaceEntry(Isolate isolate, GlobalDictionary dictionary, InternalIndex entry,
        PropertyDetails newDetails, JSValue newValue)
    {
        PropertyCell cell = dictionary.CellAt(entry);
        var newCell = new PropertyCell(cell.Name, newDetails, newValue);
        dictionary.SetEntry(entry, newCell, newDetails);
        cell.ClearAndInvalidate(isolate);
        return newCell;
    }

    /// <summary>PropertyCell::PrepareForAndSetValue.</summary>
    public static PropertyCell PrepareForAndSetValue(Isolate isolate, GlobalDictionary dictionary, InternalIndex entry,
        JSValue value, PropertyDetails details)
    {
        PropertyCell rawCell = dictionary.CellAt(entry);
        PropertyDetails originalDetails = rawCell.PropertyDetails;
        // Data accesses could be cached in ics or optimized code.
        bool invalidate = originalDetails.Kind == PropertyKind.Data && details.Kind == PropertyKind.Accessor;
        int index = originalDetails.DictionaryIndex;
        details = details.SetIndex(index);

        PropertyCellType newType = UpdatedType(isolate, rawCell, value, originalDetails);
        details = details.SetCellType(newType);

        PropertyCell cell = rawCell;
        if (invalidate)
        {
            cell = InvalidateAndReplaceEntry(isolate, dictionary, entry, details, value);
        }
        else
        {
            cell.Transition(details, value);
            // Deopt when transitioning from a constant type or when making a writable
            // property read-only.
            if (originalDetails.CellType != newType || (!originalDetails.IsReadOnly && details.IsReadOnly))
            {
                DependentCode.DeoptimizeDependencyGroups(isolate, cell, DependentCode.DependencyGroups.PropertyCellChanged);
            }
        }
        return cell;
    }
}
