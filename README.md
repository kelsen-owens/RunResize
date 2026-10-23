# RunResize

A free Revit 2027 add-in that resizes a run of pipes or ducts **together with its fittings** in one undoable step.

Revit 2027 dropped this from the Modify | Multi-Select ribbon, so segments and fittings now have to be resized separately. The [Revit Ideas request to restore it](https://forums.autodesk.com/t5/revit-ideas/revit-2027-duct-pipe-conduit-amp-tray-sizing/idi-p/14166334) drew 200 votes in about four months, and updates 2027.1–2027.3 haven't brought it back.

**Status: v0.1 compiles cleanly against the Revit 2027.0 API but hasn't been run inside Revit yet.** The test pass below is next.

## What v0.1 does

- Pipes, round ducts, rectangular ducts and oval ducts. Conduit and cable tray come next.
- Uses the current selection, or asks you to pick. Tab selects a connected run.
- Offers only valid sizes: the duct size table for the shape, or for pipes the sizes every selected pipe's segment allows.
- Fittings and accessories that sit **between** selected pipes or ducts are resized too, even if you didn't select them. Fittings at the edge of the selection keep their outward-facing size.
- Shows one summary instead of a stream of warnings. Anything it couldn't resize is left selected, with the reason.

## You'll need

1. **Revit 2027.** For development, ADN membership is free for startups for three years and includes Autodesk software licences: <https://aps.autodesk.com/developer/overview/autodesk-developer-network-membership>. A 30-day Revit trial also works to start.
2. **.NET 10 SDK.** Revit 2027 add-ins target `net10.0-windows`: <https://dotnet.microsoft.com/download/dotnet/10.0>.
3. An editor. VS Code with the C# Dev Kit extension, or Visual Studio 2026.

Revit doesn't need to be installed to *compile*: the Revit API comes from the `Nice3point.Revit.Api.*` reference packages on NuGet.

## Build and install

```bash
dotnet build RunResize
```

A Debug build copies `RunResize.addin` and `RunResize\RunResize.dll` into `%APPDATA%\Autodesk\Revit\Addins\2027`. Close Revit before building, because it locks the DLL. Start Revit, choose **Always Load** when asked, and the **Resize Run** button appears on the Add-Ins tab.

## Test pass

Use Revit's sample MEP models or any project with real runs. After each case, check that connections still hold, and that a single Ctrl+Z undoes the whole resize.

| # | Case | Expected |
|---|---|---|
| 1 | Straight pipe run with elbows, select the pipes only | Pipes and the elbows between them resize |
| 2 | Same run, select pipes and elbows with Tab | Same result |
| 3 | Run with a tee whose branch isn't selected | Main run resizes; summary reports any branch mismatch |
| 4 | Selection ending at an elbow that leads to an unselected pipe | Edge elbow untouched; summary is clear |
| 5 | Rectangular duct run with elbows and a transition | Width and height apply to ducts and fittings |
| 6 | Pipes of two different materials | Only sizes both segments allow are offered |
| 7 | Pipe and duct selected together | Asks for one kind of run at a time |
| 8 | Run including a valve or damper | Resized, or reported if its size is set by type |

Write down every fitting family that fails and the reason shown. That list decides what v0.2 handles.

## How it works

The code is small enough to read in one sitting and covers the three API ideas most MEP tools need:

- `RunSizing.CatalogSizes`: where valid sizes live. Pipe segments (`PipeSegment.GetSizes()`) and `DuctSizeSettings`.
- `RunSizing.SetCurveSize`: pipes and ducts are sized through built-in parameters such as `RBS_PIPE_DIAMETER_PARAM`.
- `RunSizing.SetConnectorSize`: fittings are user families whose connector sizes are driven by family parameters. The code asks the connector which instance parameter drives it (`MEPFamilyConnectorInfo.GetAssociateFamilyParameterId`) and sets that, falling back to setting the connector itself.

## Next

- Conduit and cable tray
- Regenerate fittings from routing preferences when a family can't be resized in place
- Autodesk App Store listing, as a free app
