using System.Reflection;
using Autodesk.Revit.UI;

namespace RunResize;

/// <summary>Adds the Resize Run button to the Add-Ins tab.</summary>
public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        RibbonPanel panel = application.CreateRibbonPanel("MEP Runs");
        var button = new PushButtonData(
            "RunResize.ResizeRun",
            "Resize\nRun",
            Assembly.GetExecutingAssembly().Location,
            typeof(ResizeRunCommand).FullName!)
        {
            ToolTip = "Resize a run of pipes or ducts together with its fittings, in one undoable step.",
            LongDescription = "Select connected pipes or ducts (Tab selects a whole run), click Resize Run, and pick a size. " +
                              "Fittings and accessories between the selected pipes or ducts are resized to match.",
        };
        panel.AddItem(button);
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;
}
