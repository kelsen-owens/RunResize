#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Autodesk.Revit.UI;

namespace RunResize;

/// <summary>
/// Debug builds only. Revit trusts only RunResize's dev host, so other add-ins in development start through it: each line
/// of devaddins.txt next to this DLL names an assembly and its IExternalApplication, as "path|Namespace.Type". One that
/// fails to start is logged to devaddins.log and skipped.
/// </summary>
internal static class DevAddins
{
    static readonly List<IExternalApplication> Started = [];

    static string Folder => Path.GetDirectoryName(typeof(DevAddins).Assembly.Location)!;

    public static void Start(UIControlledApplication application)
    {
        string list = Path.Combine(Folder, "devaddins.txt");
        if (!File.Exists(list)) return;
        foreach (string line in File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            try
            {
                string[] parts = line.Split('|');
                Assembly assembly = AssemblyLoadContext.GetLoadContext(typeof(DevAddins).Assembly)!.LoadFromAssemblyPath(parts[0]);
                var addin = (IExternalApplication)Activator.CreateInstance(assembly.GetType(parts[1], throwOnError: true)!)!;
                if (addin.OnStartup(application) == Result.Succeeded) Started.Add(addin);
            }
            catch (Exception ex)
            {
                File.AppendAllText(Path.Combine(Folder, "devaddins.log"), $"{DateTime.Now:s} {line}: {ex}{Environment.NewLine}");
            }
        }
    }

    public static void Stop(UIControlledApplication application)
    {
        foreach (IExternalApplication addin in Started) addin.OnShutdown(application);
    }
}
#endif
