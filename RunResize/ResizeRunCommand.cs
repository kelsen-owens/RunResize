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

        ResizeOutcome outcome = Resize(doc, picked, curves, kind, picker.Selected);
        if (outcome.Highlight.Count > 0) uidoc.Selection.SetElementIds(outcome.Highlight);
        (string instruction, string content) = Summary(outcome, kind, picker.SelectedLabel);
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = instruction,
            MainContent = content,
        };
        dialog.Show();
        return outcome.RolledBack ? Result.Cancelled : Result.Succeeded;
    }

    /// <summary>Resizes the run and the fittings inside it in one transaction. No UI here, so the debug test runner calls it too.</summary>
    internal static ResizeOutcome Resize(Document doc, IReadOnlyList<Element> picked, IReadOnlyList<MEPCurve> curves, RunKind kind, RunSize target)
    {
        var selected = new HashSet<ElementId>(picked.Select(e => e.Id));
        List<FamilyInstance> fittings = picked.Where(RunSizing.IsInlineFamily).Cast<FamilyInstance>().ToList();
        foreach (FamilyInstance interior in RunSizing.InteriorFittings(curves, selected).ToList())
        {
            fittings.Add(interior);
            selected.Add(interior.Id);
        }

        var outcome = new ResizeOutcome();
        HashSet<ElementId> fittingsBefore = RunSizing.ConnectedFittings(doc, selected);
        // Revit may put a transition into any of these joins, but none may come apart.
        List<(ElementId Owner, int Connector, ElementId Other)> joins = RunSizing.Joins(doc, selected);

        using (var transaction = new Transaction(doc, "Resize run"))
        {
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(outcome.Failures);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            transaction.Start();

            foreach (MEPCurve curve in curves)
            {
                string? why = RunSizing.SetCurveSize(curve, kind, target);
                if (why is null) outcome.Curves++;
                else outcome.Failed.Add((curve.Id, Describe(curve), why));
            }

            var sized = new List<(FamilyInstance Fitting, (int Id, RunSize Size)[] Ends)>();
            var retyped = new List<FamilyInstance>();
            foreach (FamilyInstance fitting in fittings)
            {
                List<Connector> ends = RunSizing.ConnectorsIntoSelection(fitting, selected).ToList();
                if (ends.Count == 0) continue;

                (int, RunSize)[] expected = ends.Select(end => (end.Id, RunSizing.TargetFor(end, ends, kind, target))).ToArray();
                ElementId type = fitting.GetTypeId();
                string? why = RunSizing.SetFittingSize(fitting, ends, kind, target);
                if (why is null) sized.Add((fitting, expected));
                else outcome.Failed.Add((fitting.Id, Describe(fitting), why));
                if (!fitting.GetTypeId().Equals(type)) retyped.Add(fitting);
            }

            doc.Regenerate();
            foreach (FamilyInstance fitting in retyped)
                RunSizing.Reattach(fitting, joins.Where(j => j.Owner.Equals(fitting.Id)).Select(j => (j.Connector, j.Other)));

            foreach ((FamilyInstance fitting, (int Id, RunSize Size)[] ends) in sized)
            {
                ConnectorManager connectors = fitting.MEPModel.ConnectorManager;
                if (ends.All(end => RunSizing.HasSize(connectors.Lookup(end.Id), kind, end.Size))) outcome.Fittings++;
                else outcome.Failed.Add((fitting.Id, Describe(fitting), "its family ignored the new size"));
            }

            outcome.Detached.AddRange(joins.Where(j => !RunSizing.StillJoined(doc, j)).Select(j => j.Owner).Distinct());
            if (outcome.Detached.Count > 0)
            {
                transaction.RollBack();
                outcome.RolledBack = true;
            }
            else
            {
                outcome.RolledBack = transaction.Commit() != TransactionStatus.Committed;
            }
        }

        if (outcome.RolledBack)
        {
            outcome.Highlight.AddRange(outcome.Detached
                .Concat(outcome.Failures.Errors.SelectMany(e => e.Elements))
                .Distinct()
                .Where(id => doc.GetElement(id) is not null));
        }
        else
        {
            outcome.Transitions = RunSizing.ConnectedFittings(doc, selected).Count(id => !fittingsBefore.Contains(id) && !selected.Contains(id));
            outcome.Highlight.AddRange(outcome.Failed.Select(f => f.Id));
        }
        return outcome;
    }

    /// <summary>The summary dialog's heading and text.</summary>
    internal static (string Instruction, string Content) Summary(ResizeOutcome outcome, RunKind kind, string size)
    {
        var body = new StringBuilder();
        if (outcome.RolledBack)
        {
            var errors = outcome.Failures.Errors;
            if (outcome.Detached.Count > 0)
                body.AppendLine($"At {size}, Revit would have left {Plural(outcome.Detached.Count, "element")} unconnected, so nothing was changed.");
            else if (errors.Count > 0)
                body.AppendLine($"Revit couldn't keep the run connected at {size}. A common cause is a short {(kind == RunKind.Pipe ? "pipe" : "duct")} between fittings with no room for the new size: lengthen it, or leave it out of the selection.");
            else
                body.AppendLine("Revit couldn't apply the new size.");

            int involved = outcome.Highlight.Count;
            if (involved > 0) body.AppendLine($"The {Plural(involved, "element")} involved {(involved == 1 ? "is" : "are")} selected.");
            if (errors.Count > 0)
            {
                body.AppendLine();
                body.AppendLine("Revit reported:");
                foreach (string text in errors.Select(e => e.Text).Distinct().Take(5)) body.AppendLine($"• {text}");
            }
            return ("Nothing was resized", body.ToString().TrimEnd());
        }

        var failed = outcome.Failed;
        if (failed.Count > 0)
        {
            body.AppendLine($"{Plural(failed.Count, "element")} kept the old size and {(failed.Count == 1 ? "is" : "are")} selected now so you can fix {(failed.Count == 1 ? "it" : "them")}:");
            foreach (var f in failed.Take(8)) body.AppendLine($"• {f.What}: {f.Why}");
            if (failed.Count > 8) body.AppendLine($"…and {failed.Count - 8} more.");
        }
        if (outcome.Transitions > 0)
        {
            if (body.Length > 0) body.AppendLine();
            body.AppendLine($"Revit added {Plural(outcome.Transitions, "transition")} where the new size meets {(kind == RunKind.Pipe ? "pipes" : "ducts")} and fittings that kept the old size.");
        }
        var warnings = outcome.Failures.Warnings;
        if (warnings.Count > 0)
        {
            if (body.Length > 0) body.AppendLine();
            body.AppendLine("Revit also reported:");
            foreach (string w in warnings.Distinct().Take(5)) body.AppendLine($"• {w}");
        }

        string noun = kind == RunKind.Pipe ? Plural(outcome.Curves, "pipe") : Plural(outcome.Curves, "duct");
        return ($"Resized {noun} and {Plural(outcome.Fittings, "fitting")} to {size}", body.ToString().TrimEnd());
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

    static string Describe(Element e) =>
        e is FamilyInstance fi ? $"{fi.Symbol.FamilyName} – {fi.Name} (ID {e.Id})" : $"{e.Name} (ID {e.Id})";

    static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
}

/// <summary>
/// What a resize did. A Revit error or a join that came apart rolls it back whole; otherwise each element either took
/// the size or is listed in Failed.
/// </summary>
internal sealed class ResizeOutcome
{
    public bool RolledBack { get; set; }
    public int Curves { get; set; }
    public int Fittings { get; set; }
    public int Transitions { get; set; }
    public List<(ElementId Id, string What, string Why)> Failed { get; } = new();
    public FailureCollector Failures { get; } = new();

    /// <summary>Elements whose connection would have come apart, which rolls the resize back.</summary>
    public List<ElementId> Detached { get; } = new();

    /// <summary>Elements to select when the summary shows: the ones that kept the old size, or the ones a Revit error named.</summary>
    public List<ElementId> Highlight { get; } = new();
}

internal sealed class RunSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => RunSizing.IsRunElement(elem);
    public bool AllowReference(Reference reference, XYZ position) => false;
}
