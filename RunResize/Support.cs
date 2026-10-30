using System;
using System.Collections.Generic;
using System.Windows.Forms;
using Autodesk.Revit.DB;

namespace RunResize;

/// <summary>Lets WinForms dialogs open on top of Revit's main window.</summary>
internal sealed class RevitWindow(IntPtr handle) : IWin32Window
{
    public IntPtr Handle { get; } = handle;
}

/// <summary>
/// Collects Revit's warnings for the summary instead of interrupting the user with each one.
/// Errors roll the whole resize back; they're kept so the summary can say what blocked it.
/// </summary>
internal sealed class FailureCollector : IFailuresPreprocessor
{
    public List<string> Warnings { get; } = new();
    public List<(string Text, ICollection<ElementId> Elements)> Errors { get; } = new();

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                Warnings.Add(failure.GetDescriptionText());
                failuresAccessor.DeleteWarning(failure);
            }
            else
            {
                Errors.Add((failure.GetDescriptionText(), failure.GetFailingElementIds()));
            }
        }
        return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
