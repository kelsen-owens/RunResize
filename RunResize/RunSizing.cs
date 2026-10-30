using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace RunResize;

internal enum RunKind { Pipe, RoundDuct, RectangularDuct, OvalDuct }

/// <summary>A target size in Revit internal units (feet). Round runs use Diameter; rectangular and oval runs use Width and Height.</summary>
internal readonly record struct RunSize(double Diameter, double Width, double Height)
{
    public static RunSize Round(double diameter) => new(diameter, 0, 0);
    public static RunSize Rect(double width, double height) => new(0, width, height);
}

internal sealed record SizeOption(double Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Everything that reads or changes sizes. The command and dialog only orchestrate.</summary>
internal static class RunSizing
{
    public static bool IsRound(RunKind kind) => kind is RunKind.Pipe or RunKind.RoundDuct;

    public static bool IsRunElement(Element e) => e is Pipe or Duct || IsInlineFamily(e);

    /// <summary>Fittings and accessories sit inline in a run and take their size from their connectors.</summary>
    public static bool IsInlineFamily(Element e) =>
        e is FamilyInstance { MEPModel.ConnectorManager: not null } fi
        && fi.Category?.BuiltInCategory is BuiltInCategory.OST_PipeFitting or BuiltInCategory.OST_DuctFitting
            or BuiltInCategory.OST_PipeAccessory or BuiltInCategory.OST_DuctAccessory;

    public static RunKind? KindOf(MEPCurve curve)
    {
        if (curve is Pipe) return RunKind.Pipe;
        if (curve is not Duct) return null;
        Connector? end = curve.ConnectorManager.Connectors.Cast<Connector>().FirstOrDefault();
        return end?.Shape switch
        {
            ConnectorProfileType.Round => RunKind.RoundDuct,
            ConnectorProfileType.Rectangular => RunKind.RectangularDuct,
            ConnectorProfileType.Oval => RunKind.OvalDuct,
            _ => null,
        };
    }

    public static RunSize CurrentSize(MEPCurve curve, RunKind kind) => kind switch
    {
        RunKind.Pipe => RunSize.Round(Get(curve, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)),
        RunKind.RoundDuct => RunSize.Round(Get(curve, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM)),
        _ => RunSize.Rect(Get(curve, BuiltInParameter.RBS_CURVE_WIDTH_PARAM), Get(curve, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)),
    };

    /// <summary>
    /// Sizes the user may pick: the project's duct size table for the shape, or for pipes only the
    /// sizes every selected pipe's segment allows, so no pipe gets a size its material can't take.
    /// </summary>
    public static IReadOnlyList<SizeOption> CatalogSizes(Document doc, RunKind kind, IEnumerable<MEPCurve> curves)
    {
        List<double> values = (kind == RunKind.Pipe ? PipeCatalog(curves.OfType<Pipe>()) : DuctCatalog(doc, kind)).OrderBy(v => v).ToList();
        List<string> labels = Labels(doc.GetUnits(), kind == RunKind.Pipe ? SpecTypeId.PipeSize : SpecTypeId.DuctSize, values);
        return values.Select((v, i) => new SizeOption(v, labels[i])).ToList();
    }

    /// <summary>
    /// Sizes in the project's units. Coarse display rounding can merge sizes (4.5" reads as 5" when duct sizes show
    /// whole inches), so the labels get finer until every size reads differently.
    /// </summary>
    static List<string> Labels(Units units, ForgeTypeId spec, IReadOnlyList<double> values)
    {
        var options = new FormatOptions(units.GetFormatOptions(spec));
        for (int attempt = 0; ; attempt++)
        {
            var format = new FormatValueOptions();
            format.SetFormatOptions(options);
            List<string> labels = values.Select(v => UnitFormatUtils.Format(units, spec, v, false, format)).ToList();
            if (labels.Distinct().Count() == labels.Count || attempt == 6) return labels;

            ForgeTypeId unit = options.GetUnitTypeId();
            double finer = FormatOptions.IsValidAccuracy(unit, options.Accuracy / 2) ? options.Accuracy / 2 : options.Accuracy / 10;
            if (!FormatOptions.IsValidAccuracy(unit, finer)) return labels;
            options.Accuracy = finer;
        }
    }

    static IEnumerable<double> PipeCatalog(IEnumerable<Pipe> pipes)
    {
        Dictionary<long, double>? common = null;
        foreach (PipeSegment segment in pipes.Select(p => p.PipeSegment).Where(s => s is not null).DistinctBy(s => s.Id))
        {
            Dictionary<long, double> sizes = segment.GetSizes()
                .Where(s => s.UsedInSizeLists)
                .GroupBy(s => Key(s.NominalDiameter))
                .ToDictionary(g => g.Key, g => g.First().NominalDiameter);
            common = common is null
                ? sizes
                : common.Where(kv => sizes.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        }
        return common?.Values ?? Enumerable.Empty<double>();
    }

    static IEnumerable<double> DuctCatalog(Document doc, RunKind kind)
    {
        DuctShape shape = kind switch
        {
            RunKind.RoundDuct => DuctShape.Round,
            RunKind.OvalDuct => DuctShape.Oval,
            _ => DuctShape.Rectangular,
        };
        foreach (KeyValuePair<DuctShape, DuctSizes> table in DuctSizeSettings.GetDuctSizeSettings(doc))
        {
            if (table.Key != shape) continue;
            foreach (MEPSize size in table.Value)
                if (size.UsedInSizeLists) yield return size.NominalDiameter;
        }
    }

    /// <summary>Sets a pipe's or duct's size. Returns null on success, otherwise why it failed.</summary>
    public static string? SetCurveSize(MEPCurve curve, RunKind kind, RunSize size) => kind switch
    {
        RunKind.Pipe => Set(curve, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, size.Diameter),
        RunKind.RoundDuct => Set(curve, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, size.Diameter),
        _ => Set(curve, BuiltInParameter.RBS_CURVE_WIDTH_PARAM, size.Width)
             ?? Set(curve, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, size.Height),
    };

    /// <summary>
    /// Fittings and accessories that join two or more selected pipes or ducts. Including them means
    /// users needn't pick every elbow by hand, and the run doesn't end up with mismatched fittings.
    /// </summary>
    public static IEnumerable<FamilyInstance> InteriorFittings(IEnumerable<MEPCurve> curves, ISet<ElementId> selected)
    {
        var joins = new Dictionary<ElementId, (FamilyInstance Fitting, int Count)>();
        foreach (MEPCurve curve in curves)
            foreach (Connector end in curve.ConnectorManager.Connectors)
                foreach (Connector other in end.AllRefs)
                    if (other.Owner is FamilyInstance fi && !selected.Contains(fi.Id) && IsInlineFamily(fi))
                        joins[fi.Id] = (fi, joins.TryGetValue(fi.Id, out var seen) ? seen.Count + 1 : 1);
        return joins.Values.Where(j => j.Count >= 2).Select(j => j.Fitting);
    }

    /// <summary>A fitting's connectors that join another selected element. Connectors leading out of the selection keep their size.</summary>
    public static IEnumerable<Connector> ConnectorsIntoSelection(FamilyInstance fitting, ISet<ElementId> selected)
    {
        foreach (Connector c in fitting.MEPModel.ConnectorManager.Connectors)
        {
            if (!c.IsConnected) continue;
            foreach (Connector other in c.AllRefs)
            {
                if (!other.Owner.Id.Equals(fitting.Id) && selected.Contains(other.Owner.Id) && IsPhysical(other))
                {
                    yield return c;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Sets the size of a fitting's connectors that face the selection. Families drive connector sizes from their own
    /// parameters, so this sets the instance parameter each connector is tied to, falling back to the connector itself.
    /// Families that size by type (many valves) switch to the loaded type that has the new size.
    /// Returns null on success, otherwise why it failed.
    /// </summary>
    public static string? SetFittingSize(FamilyInstance fitting, IReadOnlyList<Connector> ends, RunKind kind, RunSize size)
    {
        var problems = new List<string?>();
        var typeValues = new Dictionary<ElementId, double>();
        foreach (Connector end in ends)
        {
            if (end.GetMEPConnectorInfo() is not MEPFamilyConnectorInfo info)
            {
                problems.Add("its connectors carry no size information");
                continue;
            }

            if (IsRound(kind))
            {
                problems.Add(SetDimension(fitting, info, typeValues, () => end.Radius = size.Diameter / 2,
                    (BuiltInParameter.CONNECTOR_DIAMETER, size.Diameter), (BuiltInParameter.CONNECTOR_RADIUS, size.Diameter / 2)));
            }
            else
            {
                RunSize own = TargetFor(end, ends, kind, size);
                problems.Add(SetDimension(fitting, info, typeValues, () => end.Width = own.Width, (BuiltInParameter.CONNECTOR_WIDTH, own.Width)));
                problems.Add(SetDimension(fitting, info, typeValues, () => end.Height = own.Height, (BuiltInParameter.CONNECTOR_HEIGHT, own.Height)));
            }
        }
        if (typeValues.Count > 0) problems.Add(SwitchType(fitting, typeValues));

        List<string> found = problems.OfType<string>().Distinct().ToList();
        return found.Count == 0 ? null : string.Join("; ", found);
    }

    /// <summary>
    /// The size one fitting connector should take. A rectangular connector's width runs along its own X axis, which can
    /// lie along the duct's height instead (many elbows sit that way), and then the run's width and height swap.
    /// A connector that meets another fitting follows a sibling connector that meets a duct.
    /// </summary>
    public static RunSize TargetFor(Connector end, IReadOnlyList<Connector> ends, RunKind kind, RunSize size)
    {
        if (IsRound(kind)) return size;
        bool turned = Turned(end) ?? ends.Select(Turned).FirstOrDefault(t => t is not null) ?? false;
        return turned ? RunSize.Rect(size.Height, size.Width) : size;
    }

    /// <summary>Whether a connector's width axis lies across the width axis of the duct it meets; null when it meets no duct.</summary>
    static bool? Turned(Connector end)
    {
        Connector? duct = end.AllRefs.Cast<Connector>().FirstOrDefault(o => o.Owner is MEPCurve && IsPhysical(o));
        return duct is null ? null : Math.Abs(end.CoordinateSystem.BasisX.DotProduct(duct.CoordinateSystem.BasisX)) < 0.7;
    }

    /// <summary>
    /// Sets one connector dimension through the first family parameter it's tied to (diameter or radius, say).
    /// A tie to a type parameter goes into typeValues for SwitchType; an untied dimension is set on the connector.
    /// </summary>
    static string? SetDimension(FamilyInstance fitting, MEPFamilyConnectorInfo info, Dictionary<ElementId, double> typeValues,
        Action setConnector, params (BuiltInParameter Parameter, double Value)[] ties)
    {
        foreach ((BuiltInParameter connectorParameter, double value) in ties)
        {
            ElementId familyParameterId = info.GetAssociateFamilyParameterId(new ElementId(connectorParameter));
            if (familyParameterId.Equals(ElementId.InvalidElementId)) continue;

            if (FindParameter(fitting, familyParameterId) is Parameter driver)
            {
                if (driver.IsReadOnly) return $"\"{driver.Definition.Name}\" is calculated by a formula in the family";
                return driver.Set(value) ? null : $"Revit rejected the new \"{driver.Definition.Name}\"";
            }
            if (FindParameter(fitting.Symbol, familyParameterId) is not null)
            {
                typeValues[familyParameterId] = value;
                return null;
            }
        }

        try
        {
            setConnector();
            return null;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return "its size is set by its type, not per instance";
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Switches a fitting that sizes by type to the one loaded type of its family whose size parameters hold the new values.</summary>
    static string? SwitchType(FamilyInstance fitting, IReadOnlyDictionary<ElementId, double> values)
    {
        bool HasValues(FamilySymbol type) => values.All(v =>
            FindParameter(type, v.Key) is { StorageType: StorageType.Double } p && Near(p.AsDouble(), v.Value));

        FamilySymbol current = fitting.Symbol;
        if (HasValues(current)) return null;
        List<FamilySymbol> matches = current.Family.GetFamilySymbolIds()
            .Select(id => fitting.Document.GetElement(id))
            .OfType<FamilySymbol>()
            .Where(HasValues)
            .ToList();
        if (matches.Count == 0) return $"its size comes from its type, and the project has no {current.FamilyName} type in the new size";
        if (matches.Count > 1) return $"its size comes from its type, and several {current.FamilyName} types have the new size, so pick one by hand";

        if (!matches[0].IsActive) matches[0].Activate();
        fitting.Symbol = matches[0];
        return null;
    }

    /// <summary>Whether a connector has the given size. Read it after Revit regenerates: some families ignore a new size without an error.</summary>
    public static bool HasSize(Connector connector, RunKind kind, RunSize size) => IsRound(kind)
        ? Near(connector.Radius * 2, size.Diameter)
        : Near(connector.Width, size.Width) && Near(connector.Height, size.Height);

    /// <summary>Fittings and accessories connected to the given elements. Comparing before and after a resize shows the transitions Revit added.</summary>
    public static HashSet<ElementId> ConnectedFittings(Document doc, IEnumerable<ElementId> ids)
    {
        var found = new HashSet<ElementId>();
        foreach (ElementId id in ids)
            foreach (Connector end in ConnectorsOf(doc.GetElement(id)))
                foreach (Connector other in end.AllRefs)
                    if (other.Owner is FamilyInstance neighbour && !neighbour.Id.Equals(id) && IsPhysical(other))
                        found.Add(neighbour.Id);
        return found;
    }

    /// <summary>Each connected end of the given elements: the element, its connector, and the element on the other side.</summary>
    public static List<(ElementId Owner, int Connector, ElementId Other)> Joins(Document doc, IEnumerable<ElementId> ids)
    {
        var joins = new List<(ElementId, int, ElementId)>();
        foreach (ElementId id in ids)
        {
            foreach (Connector end in ConnectorsOf(doc.GetElement(id)))
            {
                Connector? other = end.AllRefs.Cast<Connector>().FirstOrDefault(o => !o.Owner.Id.Equals(id) && IsPhysical(o));
                if (other is not null) joins.Add((id, end.Id, other.Owner.Id));
            }
        }
        return joins;
    }

    /// <summary>Whether an end from Joins is still connected, if only to a transition Revit put in.</summary>
    public static bool StillJoined(Document doc, (ElementId Owner, int Connector, ElementId Other) join) =>
        ConnectorsOf(doc.GetElement(join.Owner)).FirstOrDefault(c => c.Id == join.Connector) is { IsConnected: true };

    /// <summary>
    /// Switching a fitting to another type (a valve to its bigger size, say) leaves the pipes or ducts beside it unattached.
    /// Moves each loose end along its run onto the fitting's connector and connects it again.
    /// </summary>
    public static void Reattach(FamilyInstance fitting, IEnumerable<(int Connector, ElementId Other)> joins)
    {
        Document doc = fitting.Document;
        foreach ((int connectorId, ElementId otherId) in joins)
        {
            Connector end = fitting.MEPModel.ConnectorManager.Lookup(connectorId);
            if (end.IsConnected || doc.GetElement(otherId) is not MEPCurve { Location: LocationCurve { Curve: Line line } location } curve) continue;

            bool movesStart = line.GetEndPoint(0).DistanceTo(end.Origin) < line.GetEndPoint(1).DistanceTo(end.Origin);
            XYZ kept = line.GetEndPoint(movesStart ? 1 : 0);
            if (line.Direction.CrossProduct((end.Origin - kept).Normalize()).GetLength() > 1e-6) continue; // the fitting moved off the run's line

            location.Curve = movesStart ? Line.CreateBound(end.Origin, kept) : Line.CreateBound(kept, end.Origin);
            doc.Regenerate();
            end = fitting.MEPModel.ConnectorManager.Lookup(connectorId);
            if (end.IsConnected) continue;
            curve.ConnectorManager.Connectors.Cast<Connector>()
                .FirstOrDefault(c => c.ConnectorType == ConnectorType.End && !c.IsConnected && c.Origin.IsAlmostEqualTo(end.Origin, 1e-6))
                ?.ConnectTo(end);
        }
    }

    static IEnumerable<Connector> ConnectorsOf(Element? e) => (e switch
    {
        MEPCurve curve => curve.ConnectorManager,
        FamilyInstance instance => instance.MEPModel?.ConnectorManager,
        _ => null,
    })?.Connectors.Cast<Connector>() ?? [];

    static bool IsPhysical(Connector c) => c.ConnectorType is ConnectorType.End or ConnectorType.Curve;

    static Parameter? FindParameter(Element e, ElementId id) => e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Equals(id));

    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-5;

    static string? Set(Element e, BuiltInParameter parameter, double value)
    {
        Parameter? p = e.get_Parameter(parameter);
        if (p is null || p.IsReadOnly) return "its size can't be edited";
        return p.Set(value) ? null : "Revit rejected the size";
    }

    static double Get(Element e, BuiltInParameter parameter) => e.get_Parameter(parameter)?.AsDouble() ?? 0;

    static long Key(double feet) => (long)Math.Round(feet * 1e6);
}
