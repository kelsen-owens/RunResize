# RunResize

A free Revit 2027 add-in that resizes a run of pipes or ducts **together with its fittings** in one undoable step.

Revit 2027 dropped this from the Modify | Multi-Select ribbon, so segments and fittings now have to be resized separately. The [Revit Ideas request to restore it](https://forums.autodesk.com/t5/revit-ideas/revit-2027-duct-pipe-conduit-amp-tray-sizing/idi-p/14166334) drew 200 votes in about four months, and updates 2027.1–2027.3 haven't brought it back.

**Status: v1.0.0, being prepared for the Autodesk App Store. Its resize code is v0.1.1's, tested in Revit 2027.3 on Autodesk's sample models (results below).**

## Use it

1. Select the pipes or ducts of a run. To select a whole connected run, hover over one, press Tab until the run highlights, and click.
2. On the **Add-Ins** tab, in the **MEP Runs** panel, click **Resize Run**. With nothing selected, it asks you to pick.
3. Pick the new size and click **Resize**. A summary says what changed, and one Ctrl+Z undoes the whole resize.

## What it does

- Pipes, round ducts, rectangular ducts and oval ducts. Conduit and cable tray come next.
- Offers only valid sizes: the duct size table for the shape, or for pipes the sizes every selected pipe's segment allows. When the project's display rounding would make two sizes read the same (4 1/2" showing as 5"), the labels get more precise.
- Fittings and accessories that sit **between** selected pipes or ducts are resized too, even if you didn't select them. Fittings at the edge of the selection keep their size, and Revit adds transitions there.
- Fittings sized by type, like most valves, switch to the family's type in the new size if it's loaded in the project.
- Rectangular fittings that sit a quarter turn to the duct, as many elbows do, get width and height the right way round.
- All or nothing: if Revit can't keep the run connected at the new size, nothing changes and the elements involved are selected.
- One summary instead of a stream of warnings: what was resized, how many transitions Revit added, and anything that kept its old size, with the reason.

## Privacy

RunResize collects no data. It runs only inside Revit on your computer, makes no network connections, and stores nothing outside the model you're editing, so there's nothing to keep, share or delete. The **Help and privacy** link in its size dialog, and F1 on its button, open this page in your browser.

## Support

Report problems or ask questions at <https://github.com/kelsen-owens/RunResize/issues>.

## You'll need

1. **Revit 2027.** For development, ADN membership is free for startups for three years and includes Autodesk software licences: <https://aps.autodesk.com/developer/overview/autodesk-developer-network-membership>. A 30-day Revit trial also works to start.
2. **.NET 10 SDK.** Revit 2027 add-ins target `net10.0-windows`: <https://dotnet.microsoft.com/download/dotnet/10.0>.
3. An editor. VS Code with the C# Dev Kit extension, or Visual Studio 2026.

Revit doesn't need to be installed to *compile*: the Revit API comes from the `Nice3point.Revit.Api.*` reference packages on NuGet.

## Build and run

```bash
dotnet build RunResize
```

Close Revit before building, because it locks the DLL.

Revit asks whether to load an unsigned add-in again every time its DLL changes, so Debug builds load through a small host, `RunResize.DevHost`, that rarely changes. Register it once, from a normal terminal with Revit closed:

```bash
dotnet build RunResize.DevHost -p:InstallToRevit=true
```

Start Revit, choose **Always Load** for "RunResize (dev host)", and the **Resize Run** button appears on the Add-Ins tab. After that, `dotnet build RunResize` is all a change needs.

Sandboxed shells, such as the Claude desktop app's, redirect writes to AppData, so run the registration from a normal terminal.

The button's icons are drawn by `tools\make-icons.ps1`, which writes the PNGs in `RunResize\Resources` and the store's listing icon in `store`. Rerun it after changing the drawing.

`tools\make-bundle.ps1` builds the Release add-in and packs it as `dist\RunResize.bundle` and `dist\RunResize-<version>.zip`, the file the Autodesk store builds its installer from. The bundle's `PackageContents.xml` comes from `bundle\`.

Store screenshots: `tools\capture-revit.ps1` captures Revit's own windows into `dist\screenshots\v2`, and `tools\make-screenshots.ps1` crops them and adds the headlines, writing the 1600x900 images to `dist\screenshots`.

## Automated tests

Debug builds include a test runner. Put a `testplan.json` next to the built DLL (`RunResize\bin\Debug\net10.0-windows\`) and start Revit. Revit minimizes itself, opens each model in the plan in the background, runs every case through the same code as the button, writes `testresults.txt` next to the DLL, and exits. Every case is rolled back and nothing is saved.

```json
{
  "models": [
    {
      "model": "C:\\Program Files\\Autodesk\\Revit 2027\\Samples\\Snowdon Towers Sample Plumbing.rvt",
      "cases": [
        { "name": "copper run to 3/4", "select": [1428328, 1431322, 1431372, 1431280], "size": 0.75 },
        { "name": "gate valve to 8", "select": [1591161, 1591150], "size": 8 },
        { "name": "how the valve's sizes are driven", "probe": [1389546] }
      ]
    },
    {
      "model": "C:\\Program Files\\Autodesk\\Revit 2027\\Samples\\Snowdon Towers Sample HVAC.rvt",
      "cases": [ { "name": "a rectangular run, one width up", "find": "rectangular", "grow": 1 } ]
    }
  ]
}
```

A case selects element IDs (`select`) or lets the runner find a duct run (`find`: `round`, `rectangular`, or `mixed` for a duct plus a pipe). The target is `size` in inches for round runs, `width` and `height` for rectangular ones, or `grow` steps up the size list. `probe` logs how a fitting's connectors are tied to family parameters instead of resizing anything. The log shows each case's summary exactly as the dialog would, plus every connection before and after.

## Test pass

Revit 2027.3, 2026-10-29, on the Snowdon Towers Plumbing and HVAC samples and the German MEP sample (BIM_Projekt_Golden_Nugget-Gebaeudetechnik), using the runner above.

| # | Case | Result |
|---|---|---|
| 1 | Copper run with three elbows, pipes selected (1/2" → 3/4") | Pass: 4 pipes and 3 elbows resized, Revit added transitions at both ends |
| 2 | Same run, pipes and elbows selected (→ 1") | Pass, same result |
| 3a | Sanitary tee between stub pipes under 1" long (2" → 3") | Refused cleanly: the stubs have no room for the bigger tee, so nothing changes and the stubs are selected |
| 3b | Sanitary tee with long pipes, branch not selected (4" → 5") | Pass: the tee's branch grows with the run, and Revit adds a reducer on the branch |
| 4 | PVC run ending at a bend that leads to an unselected pipe (4" → 3") | Pass: the edge bend keeps 4", reducers added |
| 5 | Rectangular duct run with a mitered elbow (14×4 → 15×4) | Pass: the elbow, turned against the duct, gets 4×15 in its own axes |
| 5 | Rectangular metric run with elbows (1000×200 → 1050×200) | Refused cleanly: Revit would have flipped the 830 mm duct between two elbows, so nothing changes |
| 5 | Round duct runs with elbows, imperial with half-inch sizes and metric | Pass |
| 6 | Copper and PVC pipes across a bend | Pass on sizes: only sizes both segments allow are offered. The resize to 2 1/2" is refused cleanly, because the copper pipe is a 0.63" stub |
| 7 | A duct and a pipe selected together | Pass: asks for one kind of run at a time |
| 8 | Gate valve between 6" pipes (→ 8" and → 4") | Pass: switches to the 8" or 4" type and stays connected, whether the valve is selected or not, and with the elbows at both ends selected too |

The size picker and both kinds of summary were also checked by hand in a live session: case 1 resized and then undid with one Ctrl+Z, and case 3a showed only RunResize's summary.

Not covered yet: oval ducts, a rectangular transition inside a selected run, and a type-sized valve whose new size isn't loaded.

## Compared with Revit's Change Size

Revit 2027.3 has **Modify | Pipes > Edit Multiple > Change Size**. On the same sample runs:

| Same run | Change Size | RunResize |
|---|---|---|
| Pipes selected, elbows between them not selected | Resizes the pipes only; the elbows keep the old size | Resizes the pipes and the elbows between them |
| Pipes and elbows selected | Resizes all of them | Resizes all of them |
| Selection includes a valve or other accessory | The button isn't offered | Resizes the run; the valve switches to its type in the new size |
| Pipes on both sides of a valve | Resizes the pipes; the valve keeps its size | Same as the row above |

## Families to know about

- **Tee Sanitary - PVC - Sch 40 - DWV**: one parameter drives all three connectors, so the branch grows with the run. Revit adds a reducer on the branch.
- **Gate Valve - 2-12 Inch** and other valves sized by type: RunResize switches to the type with the new size. If that type isn't loaded, the valve keeps its size and the summary says so.
- **Stub pipes into fixtures**: fittings next to pipes a fraction of an inch long can't grow, so Revit refuses the resize.

## How it works

The code is small enough to read in one sitting and covers the API ideas most MEP tools need:

- `RunSizing.CatalogSizes`: where valid sizes live. Pipe segments (`PipeSegment.GetSizes()`) and `DuctSizeSettings`.
- `RunSizing.SetCurveSize`: pipes and ducts are sized through built-in parameters such as `RBS_PIPE_DIAMETER_PARAM`.
- `RunSizing.SetFittingSize`: fittings are user families whose connector sizes are driven by family parameters. The code asks each connector which parameter drives it (`MEPFamilyConnectorInfo.GetAssociateFamilyParameterId`). It sets that parameter on the instance, switches to the matching type when the parameter is a type parameter, and otherwise sets the connector itself.
- `ResizeRunCommand.Resize`: everything happens in one transaction. After Revit regenerates, the code reads every size back, reconnects pipes beside a fitting whose type changed, and rolls the whole resize back if any connection came apart or Revit reported an error.

## Next

- Conduit and cable tray
- Regenerate fittings from routing preferences when a family can't be resized in place
- Autodesk App Store listing, as a free app
