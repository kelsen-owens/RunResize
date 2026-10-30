using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.UI;

namespace RunResize.DevHost;

/// <summary>
/// Development only. Revit asks again whether to load an unsigned add-in each time its DLL changes, so Revit trusts this
/// small, rarely rebuilt host instead. It loads the RunResize.dll built next to it and hands startup and shutdown over.
/// </summary>
public sealed class DevHost : IExternalApplication
{
    IExternalApplication? _addin;

    public Result OnStartup(UIControlledApplication application)
    {
        string folder = Path.GetDirectoryName(typeof(DevHost).Assembly.Location)!;
        AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(typeof(DevHost).Assembly)!;
        Assembly addin = context.LoadFromAssemblyPath(Path.Combine(folder, "RunResize.dll"));
        _addin = (IExternalApplication)Activator.CreateInstance(addin.GetType("RunResize.App", throwOnError: true)!)!;
        return _addin.OnStartup(application);
    }

    public Result OnShutdown(UIControlledApplication application) => _addin?.OnShutdown(application) ?? Result.Succeeded;
}
