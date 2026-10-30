# Renders Revit's windows (main window, then its dialogs and tooltips in Z-order) into one screen-sized image,
# so windows of other apps on top of Revit don't appear. Saves the main window's area only.
# Used for the store screenshots: .\tools\capture-revit.ps1 -Path dist\screenshots\shot.png  (Revit must be running)
param([string]$Path)
$Path = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Drawing; using System.Runtime.InteropServices;
public static class RevitShot {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

  // Visible top-level windows of a process, top of the Z-order first.
  public static List<IntPtr> WindowsOf(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h) && !IsIconic(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }

  // The window as drawn on screen, without its invisible resize border. PW_RENDERFULLCONTENT (2) includes DirectX content.
  public static Bitmap Capture(IntPtr hwnd, out Rectangle bounds) {
    RECT w; GetWindowRect(hwnd, out w);
    RECT f; if (DwmGetWindowAttribute(hwnd, 9, out f, Marshal.SizeOf(typeof(RECT))) != 0) f = w;
    bounds = Rectangle.FromLTRB(f.Left, f.Top, f.Right, f.Bottom);
    if (w.Right - w.Left < 8 || w.Bottom - w.Top < 8) return null;
    var full = new Bitmap(w.Right - w.Left, w.Bottom - w.Top);
    using (var g = Graphics.FromImage(full)) { IntPtr dc = g.GetHdc(); PrintWindow(hwnd, dc, 2); g.ReleaseHdc(dc); }
    var visible = Rectangle.Intersect(new Rectangle(f.Left - w.Left, f.Top - w.Top, bounds.Width, bounds.Height), new Rectangle(0, 0, full.Width, full.Height));
    var crop = full.Clone(visible, full.PixelFormat);
    full.Dispose();
    return crop;
  }
}
'@
[void][RevitShot]::SetProcessDpiAwarenessContext([IntPtr]-4)
$revit = Get-Process Revit | Select-Object -First 1
$main = $revit.MainWindowHandle
$others = @([RevitShot]::WindowsOf([uint32]$revit.Id) | Where-Object { $_ -ne $main })
[array]::Reverse($others)

$bounds = New-Object System.Drawing.Rectangle
$mainShot = [RevitShot]::Capture($main, [ref]$bounds)
$area = $bounds
$canvas = New-Object System.Drawing.Bitmap $area.Width, $area.Height
$g = [System.Drawing.Graphics]::FromImage($canvas)
$g.DrawImage($mainShot, 0, 0)
$mainShot.Dispose()
foreach ($h in $others) {
    $b = New-Object System.Drawing.Rectangle
    $shot = [RevitShot]::Capture($h, [ref]$b)
    if ($null -eq $shot -or -not $b.IntersectsWith($area)) { continue }
    $g.DrawImage($shot, $b.X - $area.X, $b.Y - $area.Y)
    "drew window at $($b.X),$($b.Y) size $($b.Width)x$($b.Height)"
    $shot.Dispose()
}
$g.Dispose()
$canvas.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
"main window $($area.X),$($area.Y) $($area.Width)x$($area.Height) -> $Path"
$canvas.Dispose()
