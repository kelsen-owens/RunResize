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
        IEnumerable<double> values = kind == RunKind.Pipe ? PipeCatalog(curves.OfType<Pipe>()) : DuctCatalog(doc, kind);
        ForgeTypeId spec = kind == RunKind.Pipe ? SpecTypeId.PipeSize : SpecTypeId.DuctSize;
        Units units = doc.GetUnits();
        return values
            .OrderBy(v => v)
            .Select(v => new SizeOption(v, UnitFormatUtils.Format(units, spec, v, false)))
            .ToList();
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
    /// Sets one fitting connector's size. Families drive connector sizes from their own parameters, so this
    /// sets the instance parameter the connector is tied to, falling back to the connector itself.
    /// Returns null on success, otherwise why it failed.
    /// </summary>
    public static string? SetConnectorSize(FamilyInstance fitting, Connector connector, RunKind kind, RunSize size)
    {
        if (connector.GetMEPConnectorInfo() is not MEPFamilyConnectorInfo info)
            return "its connectors carry no size information";

        if (IsRound(kind))
        {
            Outcome round = SetDrivingParameter(fitting, info, BuiltInParameter.CONNECTOR_DIAMETER, size.Diameter);
            if (!round.Associated) round = SetDrivingParameter(fitting, info, BuiltInParameter.CONNECTOR_RADIUS, size.Diameter / 2);
            if (!round.Associated) round = TrySet(() => connector.Radius = size.Diameter / 2);
            return round.Problem;
        }

        Outcome width = SetDrivingParameter(fitting, info, BuiltInParameter.CONNECTOR_WIDTH, size.Width);
        if (!width.Associated) width = TrySet(() => connector.Width = size.Width);
        Outcome height = SetDrivingParameter(fitting, info, BuiltInParameter.CONNECTOR_HEIGHT, size.Height);
        if (!height.Associated) height = TrySet(() => connector.Height = size.Height);
        return width.Problem ?? height.Problem;
    }

    readonly record struct Outcome(bool Associated, string? Problem)
    {
        public static readonly Outcome NotAssociated = new(false, null);
        public static readonly Outcome Done = new(true, null);
        public static Outcome Fail(string problem) => new(true, problem);
    }

    static Outcome SetDrivingParameter(FamilyInstance fitting, MEPFamilyConnectorInfo info, BuiltInParameter connectorParameter, double value)
    {
        ElementId familyParameterId = info.GetAssociateFamilyParameterId(new ElementId(connectorParameter));
        if (familyParameterId.Equals(ElementId.InvalidElementId)) return Outcome.NotAssociated;

        // Missing from the instance parameters means the family sizes by type; the connector fallback reports that.
        Parameter? driver = fitting.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Equals(familyParameterId));
        if (driver is null) return Outcome.NotAssociated;
        if (driver.IsReadOnly) return Outcome.Fail($"\"{driver.Definition.Name}\" is calculated by a formula in the family");
        return driver.Set(value) ? Outcome.Done : Outcome.Fail($"Revit rejected the new \"{driver.Definition.Name}\"");
    }

    static Outcome TrySet(Action setSize)
    {
        try
        {
            setSize();
            return Outcome.Done;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return Outcome.Fail("its size is set by its type, not per instance");
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            return Outcome.Fail(ex.Message);
        }
    }

    static bool IsPhysical(Connector c) => c.ConnectorType is ConnectorType.End or ConnectorType.Curve;

    static string? Set(Element e, BuiltInParameter parameter, double value)
    {
        Parameter? p = e.get_Parameter(parameter);
        if (p is null || p.IsReadOnly) return "its size can't be edited";
        return p.Set(value) ? null : "Revit rejected the size";
    }

    static double Get(Element e, BuiltInParameter parameter) => e.get_Parameter(parameter)?.AsDouble() ?? 0;

    static long Key(double feet) => (long)Math.Round(feet * 1e6);
}
