#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace RunResize;

/// <summary>
/// Debug builds only. When testplan.json sits next to the add-in, Revit minimizes itself at startup, opens each model in
/// the plan in the background, runs the cases through the same Resize code as the button, writes testresults.txt,
/// closes the models without saving, and exits. Each case is rolled back, so the models never change.
/// </summary>
internal static class TestRunner
{
    internal sealed record Plan(List<ModelPlan> Models, bool Exit = true);

    internal sealed record ModelPlan(string Model, List<CasePlan> Cases);

    /// <summary>
    /// One case. Select lists element IDs, or Find picks a duct run ("round", "rectangular") or a duct plus a pipe ("mixed").
    /// Size (inches) sets a round target, Width and Height a rectangular one; otherwise Grow steps up the size list from the
    /// run's current size. Probe lists fittings whose connector-to-parameter ties are logged instead of resizing anything.
    /// </summary>
    internal sealed record CasePlan(string Name, long[]? Select, string? Find, double? Size, double? Width, double? Height, int Grow, long[]? Probe);

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    static string Folder => Path.GetDirectoryName(typeof(TestRunner).Assembly.Location)!;
    static string PlanFile => Path.Combine(Folder, "testplan.json");
    static string ResultsFile => Path.Combine(Folder, "testresults.txt");

    public static void Register(UIControlledApplication application)
    {
        if (File.Exists(PlanFile)) application.Idling += OnIdling;
    }

    static void OnIdling(object? sender, IdlingEventArgs e)
    {
        var uiapp = (UIApplication)sender!;
        uiapp.Idling -= OnIdling;
        ShowWindow(uiapp.MainWindowHandle, ShowMinimizedNoActivate);

        File.Delete(ResultsFile);
        Plan? plan = null;
        uiapp.DialogBoxShowing += Dismiss;
        try
        {
            string text = File.ReadAllText(PlanFile);
            File.Move(PlanFile, PlanFile + ".done", overwrite: true); // a crash mid-run mustn't rerun the plan on the next start
            plan = JsonSerializer.Deserialize<Plan>(text, Json)!;
            LogButton(uiapp);
            foreach (ModelPlan model in plan.Models) RunModel(uiapp.Application, model);
        }
        catch (Exception ex)
        {
            Log($"RUNNER ERROR {ex}");
        }
        finally
        {
            uiapp.DialogBoxShowing -= Dismiss;
        }

        Log("DONE");
        if (plan?.Exit != false) uiapp.PostCommand(RevitCommandId.LookupPostableCommandId(PostableCommand.ExitRevit));
    }

    /// <summary>Logs the Resize Run button's icons as Revit holds them, so a run shows they loaded.</summary>
    static void LogButton(UIApplication uiapp)
    {
        RibbonItem? button = uiapp.GetRibbonPanels()
            .Where(panel => panel.Name == "MEP Runs")
            .SelectMany(panel => panel.GetItems())
            .FirstOrDefault(item => item.Name == "RunResize.ResizeRun");
        Log(button is PushButton push ? $"button icons: {Describe(push.Image)} small, {Describe(push.LargeImage)} large" : "button not found");

        static string Describe(object? image) => image is System.Windows.Media.Imaging.BitmapSource bitmap
            ? $"{bitmap.PixelWidth}x{bitmap.PixelHeight} px at {bitmap.DpiX:0.##} dpi"
            : "none";
    }

    static void RunModel(Autodesk.Revit.ApplicationServices.Application app, ModelPlan model)
    {
        Log($"=== {Path.GetFileName(model.Model)}");
        Document doc = app.OpenDocumentFile(model.Model);
        try
        {
            foreach (CasePlan c in model.Cases)
            {
                try
                {
                    RunCase(doc, c);
                }
                catch (Exception ex)
                {
                    Log($"CASE ERROR {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            doc.Close(false);
        }
    }

    static void RunCase(Document doc, CasePlan plan)
    {
        Log($"--- {plan.Name}");
        if (plan.Probe is { } probe)
        {
            foreach (long id in probe) Probe(doc, doc.GetElement(new ElementId(id)));
            return;
        }

        List<Element?> found = plan.Find is { } find
            ? FindRun(doc, find)
            : plan.Select!.Select(id => (Element?)doc.GetElement(new ElementId(id))).ToList();
        if (found.Count == 0 || found.Any(e => e is null))
        {
            Log("elements not found");
            return;
        }
        List<Element> picked = found.Cast<Element>().ToList();
        Log($"select: {string.Join(", ", picked.Select(e => e.Id))}");

        List<MEPCurve> curves = picked.OfType<MEPCurve>().Where(c => c is Pipe or Duct).ToList();
        List<RunKind?> kinds = curves.Select(RunSizing.KindOf).Distinct().ToList();
        if (kinds.Count != 1 || kinds[0] is not RunKind kind)
        {
            Log($"kinds {string.Join(", ", kinds)}: the button asks for one kind of run at a time");
            return;
        }

        IReadOnlyList<SizeOption> sizes = RunSizing.CatalogSizes(doc, kind, curves);
        Log($"sizes: {string.Join(" | ", sizes.Select(s => s.Label))}");
        (RunSize target, string label) = Target(plan, kind, sizes, RunSizing.CurrentSize(curves[0], kind));
        Log($"target: {label}");

        var watched = new HashSet<ElementId>(picked.Select(e => e.Id));
        watched.UnionWith(RunSizing.InteriorFittings(curves, watched).Select(f => f.Id).ToList());
        Snapshot(doc, Around(doc, watched), "before");

        using var group = new TransactionGroup(doc, "RunResize test");
        group.Start();
        ResizeOutcome outcome = ResizeRunCommand.Resize(doc, picked, curves, kind, target);
        (string instruction, string content) = ResizeRunCommand.Summary(outcome, kind, label);
        Log($"SUMMARY: {instruction}");
        foreach (string line in content.Split('\n')) Log($"  | {line.TrimEnd('\r')}");
        Log($"highlight: {string.Join(", ", outcome.Highlight)}");
        Snapshot(doc, Around(doc, watched), "after");
        group.RollBack();
    }

    static (RunSize Size, string Label) Target(CasePlan plan, RunKind kind, IReadOnlyList<SizeOption> sizes, RunSize current)
    {
        SizeOption Nearest(double feet) => sizes.OrderBy(s => Math.Abs(s.Value - feet)).First();
        SizeOption Step(double feet) => sizes[Math.Min(sizes.ToList().IndexOf(Nearest(feet)) + plan.Grow, sizes.Count - 1)];

        if (RunSizing.IsRound(kind))
        {
            SizeOption diameter = plan.Size is double inches ? Nearest(inches / 12) : Step(current.Diameter);
            return (RunSize.Round(diameter.Value), diameter.Label);
        }
        SizeOption width = plan.Width is double w ? Nearest(w / 12) : Step(current.Width);
        SizeOption height = plan.Height is double h ? Nearest(h / 12) : Nearest(current.Height);
        return (RunSize.Rect(width.Value, height.Value), $"{width.Label} × {height.Label}");
    }

    /// <summary>
    /// A duct run for the HVAC cases: the two ducts around an elbow, extended across the elbows and transitions next to
    /// them. Rectangular runs must include a transition.
    /// </summary>
    static List<Element?> FindRun(Document doc, string what)
    {
        List<Element> ductsInModel = new FilteredElementCollector(doc).OfClass(typeof(Duct)).ToList();
        if (what == "mixed")
        {
            Element? pipe = new FilteredElementCollector(doc).OfClass(typeof(Pipe)).FirstElement();
            if (ductsInModel.Count == 0 || pipe is null) Log($"model has {ductsInModel.Count} ducts and {(pipe is null ? "no" : "some")} pipes");
            return [ductsInModel.FirstOrDefault(), pipe];
        }

        RunKind kind = what == "round" ? RunKind.RoundDuct : RunKind.RectangularDuct;
        List<FamilyInstance> fittings = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_DuctFitting)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .ToList();
        Log($"{ductsInModel.Count(d => RunSizing.KindOf((Duct)d) == kind)} {kind} ducts; duct fittings by part: " +
            string.Join(", ", fittings.GroupBy(PartOf).Select(g => $"{g.Key} {g.Count()}")));

        List<Element?>? fallback = null;
        foreach (FamilyInstance elbow in fittings.Where(f => PartOf(f) == PartType.Elbow))
        {
            if (DuctsAround(elbow, kind) is not { } ducts) continue;
            var run = new HashSet<ElementId>(ducts.Select(d => d.Id));
            bool hasTransition = false;
            foreach (Element duct in ducts)
            {
                foreach (FamilyInstance next in Neighbours(duct).OfType<FamilyInstance>().Where(f => !f.Id.Equals(elbow.Id)))
                {
                    PartType? part = PartOf(next);
                    if (part is not (PartType.Elbow or PartType.Transition) || DuctsAround(next, kind) is not { } more) continue;
                    hasTransition |= part == PartType.Transition;
                    run.UnionWith(more.Select(d => d.Id));
                }
            }
            List<Element?> found = run.Select(id => (Element?)doc.GetElement(id)).ToList();
            if (run.Count >= 3 && (kind == RunKind.RoundDuct || hasTransition)) return found;
            fallback ??= found;
        }
        if (fallback is not null) Log("no run with a transition beside an elbow; using the first elbow's ducts");
        return fallback ?? [];
    }

    /// <summary>The two ducts a fitting joins, when both are of the given kind.</summary>
    static List<Element>? DuctsAround(FamilyInstance fitting, RunKind kind)
    {
        List<Element> around = Neighbours(fitting).ToList();
        return around.Count == 2 && around.All(e => e is Duct d && RunSizing.KindOf(d) == kind) ? around : null;
    }

    /// <summary>Logs how a fitting's connector sizes are tied to family parameters, and the values its sibling types carry.</summary>
    static void Probe(Document doc, Element? e)
    {
        if (e is not FamilyInstance fitting)
        {
            Log("probe: not a family instance");
            return;
        }
        Log($"probe {fitting.Id} {Label(fitting)} part {PartOf(fitting)}");
        List<FamilySymbol> types = fitting.Symbol.Family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().ToList();
        Log($"  loaded types: {string.Join(" | ", types.Select(t => t.Name))}");
        foreach (Connector c in Ends(fitting))
        {
            var info = c.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
            Log($"  connector {c.Id} {Size(c)}{(info is null ? " (no family info)" : "")}");
            if (info is null) continue;
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.CONNECTOR_DIAMETER, BuiltInParameter.CONNECTOR_RADIUS, BuiltInParameter.CONNECTOR_WIDTH, BuiltInParameter.CONNECTOR_HEIGHT })
            {
                ElementId id = info.GetAssociateFamilyParameterId(new ElementId(bip));
                if (id.Equals(ElementId.InvalidElementId)) continue;
                Parameter? onInstance = ById(fitting, id), onType = ById(fitting.Symbol, id);
                Log($"    {bip} tied to {id}: instance {Show(onInstance)}; type {Show(onType)}");
                if (onType is not null)
                    Log($"      siblings: {string.Join(" | ", types.Select(t => $"{t.Name}={Show(ById(t, id))}"))}");
            }
        }
    }

    static Parameter? ById(Element e, ElementId id) => e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Id.Equals(id));

    static string Show(Parameter? p) => p is null
        ? "-"
        : $"\"{p.Definition.Name}\"{(p.IsReadOnly ? " (read-only)" : "")} {(p.StorageType == StorageType.Double ? $"{p.AsDouble() * 12:0.###}in" : p.AsValueString())}";

    static List<ElementId> Around(Document doc, IEnumerable<ElementId> ids)
    {
        var all = new HashSet<ElementId>(ids);
        foreach (ElementId id in ids.ToList())
            foreach (Element neighbour in Neighbours(doc.GetElement(id)))
                if (neighbour is MEPCurve or FamilyInstance) all.Add(neighbour.Id);
        return all.OrderBy(id => id.Value).ToList();
    }

    static void Snapshot(Document doc, List<ElementId> ids, string tag)
    {
        Log($"{tag}:");
        foreach (ElementId id in ids)
        {
            Element? e = doc.GetElement(id);
            if (e is null)
            {
                Log($"  {id} gone");
                continue;
            }
            IEnumerable<string> ends = Ends(e).Select(c =>
            {
                Connector? other = c.AllRefs.Cast<Connector>().FirstOrDefault(o => o.ConnectorType == ConnectorType.End && !o.Owner.Id.Equals(id));
                if (other is null) return $"[{Size(c)} open]";
                bool turned = c.Shape != ConnectorProfileType.Round && Math.Abs(c.CoordinateSystem.BasisX.DotProduct(other.CoordinateSystem.BasisX)) < 0.7;
                bool same = c.Shape == ConnectorProfileType.Round
                    ? Size(c) == Size(other)
                    : turned ? Near(c.Width, other.Height) && Near(c.Height, other.Width) : Near(c.Width, other.Width) && Near(c.Height, other.Height);
                return $"[{Size(c)} -> {other.Owner.Id}{(turned ? " turned" : "")}{(same ? "" : $" MISMATCH {Size(other)}")}]";
            });
            string length = e is MEPCurve { Location: LocationCurve line } ? $" L={line.Curve.Length * 12:0.##}in" : "";
            Log($"  {id} {Label(e)}{length} {string.Join(" ", ends)}");
        }
    }

    static IEnumerable<Connector> Ends(Element? e)
    {
        ConnectorManager? connectors = e switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance instance => instance.MEPModel?.ConnectorManager,
            _ => null,
        };
        return connectors?.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End) ?? [];
    }

    static IEnumerable<Element> Neighbours(Element? e) => Ends(e)
        .SelectMany(c => c.AllRefs.Cast<Connector>())
        .Where(o => o.ConnectorType == ConnectorType.End && !o.Owner.Id.Equals(e!.Id))
        .Select(o => o.Owner);

    static PartType? PartOf(FamilyInstance f) => (f.MEPModel as MechanicalFitting)?.PartType;

    static string Label(Element e) => e is FamilyInstance fi ? $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}" : $"{e.GetType().Name} {e.Name}";

    static bool Near(double a, double b) => Math.Abs(a - b) < 1e-5;

    static string Size(Connector c) => c.Shape == ConnectorProfileType.Round
        ? $"{c.Radius * 24:0.###}in"
        : $"{c.Width * 12:0.#}x{c.Height * 12:0.#}";

    /// <summary>Unattended runs can't answer dialogs, so each one is logged and closed.</summary>
    static void Dismiss(object? sender, DialogBoxShowingEventArgs e)
    {
        string text = e switch
        {
            TaskDialogShowingEventArgs task => task.Message,
            MessageBoxShowingEventArgs box => box.Message,
            _ => "",
        };
        Log($"DIALOG {e.DialogId}: {text}");
        e.OverrideResult(e is TaskDialogShowingEventArgs ? (int)TaskDialogResult.Close : 1);
    }

    static void Log(string line) => File.AppendAllText(ResultsFile, line + Environment.NewLine);

    const int ShowMinimizedNoActivate = 7;

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);
}
#endif
