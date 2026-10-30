using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RunResize;

/// <summary>Adds the Resize Run button to the Add-Ins tab.</summary>
public sealed class App : IExternalApplication
{
    /// <summary>The user help, which holds the privacy policy. F1 on the button and the size dialog's link open it.</summary>
    internal const string HelpUrl = "https://github.com/kelsen-owens/RunResize#readme";

    internal const string SupportEmail = "runresize@gmail.com";

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
            Image = Icon("ResizeRun16.png"),
            LargeImage = Icon("ResizeRun32.png"),
        };
        button.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpUrl));
        panel.AddItem(button);
#if DEBUG
        TestRunner.Register(application);
#endif
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    /// <summary>
    /// Reads an icon embedded from Resources (drawn by tools\make-icons.ps1). A resource stream rather than a pack URI,
    /// because Debug builds are loaded by RunResize.DevHost, where WPF can't resolve this assembly by name.
    /// </summary>
    static BitmapSource Icon(string name)
    {
        using Stream stream = typeof(App).Assembly.GetManifestResourceStream($"RunResize.Resources.{name}")!;
        return BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
