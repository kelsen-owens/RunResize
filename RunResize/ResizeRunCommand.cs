using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;

namespace RunResize;

/// <summary>
/// Restores what Revit 2027 removed from the Modify | Multi-Select ribbon: resizing a run of pipes or ducts
/// together with its fittings in one step.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ResizeRunCommand : IExternalCommand
{
    const string Title = "Resize Run";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        UIDocument uidoc = commandData.Application.ActiveUIDocument;
        Document doc = uidoc.Document;

        List<Element> picked = PickRun(uidoc);
        if (picked.Count == 0) return Result.Cancelled;

        List<MEPCurve> curves = picked.OfType<MEPCurve>().Where(c => c is Pipe or Duct).ToList();
        if (curves.Count == 0)
        {
            TaskDialog.Show(Title, "Select at least one pipe or duct. Fittings are resized to match the pipes or ducts they connect.");
            return Result.Cancelled;
        }

        List<RunKind?> kinds = curves.Select(RunSizing.KindOf).Distinct().ToList();
        if (kinds.Count != 1 || kinds[0] is not RunKind kind)
        {
            TaskDialog.Show(Title, "Select one kind of run at a time: pipes, round ducts, rectangular ducts, or oval ducts.");
            return Result.Cancelled;
        }

        IReadOnlyList<SizeOption> sizes = RunSizing.CatalogSizes(doc, kind, curves);
        if (sizes.Count == 0)
        {
            TaskDialog.Show(Title, kind == RunKind.Pipe
                ? "The selected pipes have no size in common. Check Mechanical Settings > Pipe Settings > Segments and Sizes."
                : "No duct sizes are defined for this shape. Add them under Mechanical Settings > Duct Settings > Sizes.");
            return Result.Cancelled;
        }

        using var picker = new SizePickerForm(kind, sizes, RunSizing.CurrentSize(curves[0], kind));
        if (picker.ShowDialog(new RevitWindow(commandData.Application.MainWindowHandle)) != System.Windows.Forms.DialogResult.OK)
            return Result.Cancelled;
        RunSize target = picker.Selected;

        var selected = new HashSet<ElementId>(picked.Select(e => e.Id));
        List<FamilyInstance> fittings = picked.Where(RunSizing.IsInlineFamily).Cast<FamilyInstance>().ToList();
        foreach (FamilyInstance interior in RunSizing.InteriorFittings(curves, selected).ToList())
        {
            fittings.Add(interior);
            selected.Add(interior.Id);
        }

        var failed = new List<(ElementId Id, string What, string Why)>();
        int resizedCurves = 0, resizedFittings = 0;
        var warnings = new WarningCollector();

        using (var transaction = new Transaction(doc, "Resize run"))
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(warnings);
            transaction.SetFailureHandlingOptions(options);
            transaction.Start();

            foreach (MEPCurve curve in curves)
            {
                string? why = RunSizing.SetCurveSize(curve, kind, target);
                if (why is null) resizedCurves++;
                else failed.Add((curve.Id, Describe(curve), why));
            }

            foreach (FamilyInstance fitting in fittings)
            {
                List<Connector> ends = RunSizing.ConnectorsIntoSelection(fitting, selected).ToList();
                if (ends.Count == 0) continue;

                List<string> problems = ends
                    .Select(end => RunSizing.SetConnectorSize(fitting, end, kind, target))
                    .OfType<string>()
                    .Distinct()
                    .ToList();
                if (problems.Count == 0) resizedFittings++;
                else failed.Add((fitting.Id, Describe(fitting), string.Join("; ", problems)));
            }

            if (transaction.Commit() != TransactionStatus.Committed)
            {
                message = "Revit couldn't apply the new size, so nothing was changed.";
                return Result.Failed;
            }
        }

        ShowReport(uidoc, kind, resizedCurves, resizedFittings, picker.SelectedLabel, failed, warnings.Messages);
        return Result.Succeeded;
    }

    /// <summary>Uses the current selection when it holds run elements; otherwise asks the user to pick.</summary>
    static List<Element> PickRun(UIDocument uidoc)
    {
        Document doc = uidoc.Document;
        List<Element> preselected = uidoc.Selection.GetElementIds()
            .Select(id => doc.GetElement(id))
            .Where(e => e is not null && RunSizing.IsRunElement(e))
            .ToList();
        if (preselected.Count > 0) return preselected;

        try
        {
            return uidoc.Selection
                .PickObjects(ObjectType.Element, new RunSelectionFilter(), "Select the pipes or ducts to resize (Tab selects a connected run), then click Finish")
                .Select(r => doc.GetElement(r))
                .ToList();
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return new List<Element>();
        }
    }

    static void ShowReport(UIDocument uidoc, RunKind kind, int curves, int fittings, string size,
        List<(ElementId Id, string What, string Why)> failed, IReadOnlyCollection<string> warnings)
    {
        string noun = kind == RunKind.Pipe ? Plural(curves, "pipe") : Plural(curves, "duct");
        var body = new StringBuilder();
        if (failed.Count > 0)
        {
            body.AppendLine($"{Plural(failed.Count, "element")} kept the old size and {(failed.Count == 1 ? "is" : "are")} selected now so you can fix {(failed.Count == 1 ? "it" : "them")}:");
            foreach (var f in failed.Take(8)) body.AppendLine($"• {f.What}: {f.Why}");
            if (failed.Count > 8) body.AppendLine($"…and {failed.Count - 8} more.");
            uidoc.Selection.SetElementIds(failed.Select(f => f.Id).ToList());
        }
        if (warnings.Count > 0)
        {
            if (body.Length > 0) body.AppendLine();
            body.AppendLine("Revit also reported:");
            foreach (string w in warnings.Distinct().Take(5)) body.AppendLine($"• {w}");
        }

        var dialog = new TaskDialog(Title)
        {
            MainInstruction = $"Resized {noun} and {Plural(fittings, "fitting")} to {size}",
            MainContent = body.ToString().TrimEnd(),
        };
        dialog.Show();
    }

    static string Describe(Element e) =>
        e is FamilyInstance fi ? $"{fi.Symbol.FamilyName} – {fi.Name} (ID {e.Id})" : $"{e.Name} (ID {e.Id})";

    static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}

internal sealed class RunSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => RunSizing.IsRunElement(elem);
    public bool AllowReference(Reference reference, XYZ position) => false;
}
