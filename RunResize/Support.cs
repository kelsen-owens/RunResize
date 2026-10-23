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

/// <summary>Collects Revit's warnings for the summary instead of interrupting the user with each one.</summary>
internal sealed class WarningCollector : IFailuresPreprocessor
{
    public List<string> Messages { get; } = new();

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
        {
            if (failure.GetSeverity() != FailureSeverity.Warning) continue;
            Messages.Add(failure.GetDescriptionText());
            failuresAccessor.DeleteWarning(failure);
        }
        return FailureProcessingResult.Continue;
    }
}
