# Builds the Release add-in and packs it as RunResize.bundle, the format the Autodesk store's installer is built from.
# Run from Windows PowerShell: .\tools\make-bundle.ps1  (writes dist\RunResize.bundle and dist\RunResize-<version>.zip)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'RunResize'

dotnet build $project -c Release --nologo -v q
if ($LASTEXITCODE) { exit $LASTEXITCODE }
$version = (dotnet msbuild $project -nologo -getProperty:Version -p:Configuration=Release).Trim()
$built = Join-Path $project 'bin\Release\net10.0-windows'

# RunResize.bundle\PackageContents.xml points Revit at Contents\2027\RunResize.addin, whose assembly path is RunResize\RunResize.dll.
$dist = Join-Path $root 'dist'
$bundle = Join-Path $dist 'RunResize.bundle'
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
$addins = Join-Path $bundle 'Contents\2027'
New-Item -ItemType Directory -Force (Join-Path $addins 'RunResize') | Out-Null
Copy-Item (Join-Path $built 'RunResize.addin') $addins
Copy-Item (Join-Path $built 'RunResize.dll') (Join-Path $addins 'RunResize')
Copy-Item (Join-Path $root 'bundle\Help.html') (Join-Path $bundle 'Contents')

[xml]$package = Get-Content (Join-Path $root 'bundle\PackageContents.xml') -Raw
$package.ApplicationPackage.AppVersion = $version
$package.ApplicationPackage.FriendlyVersion = $version
$package.ApplicationPackage.Components.ComponentEntry.Version = $version
foreach ($comment in @($package.SelectNodes('//comment()'))) { [void]$comment.ParentNode.RemoveChild($comment) }
$package.Save((Join-Path $bundle 'PackageContents.xml'))

# Entries are added one by one because .NET Framework's ZipFile.CreateFromDirectory and Compress-Archive
# both write backslashes into entry names, where the zip format expects forward slashes.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = Join-Path $dist "RunResize-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
foreach ($file in Get-ChildItem $bundle -Recurse -File) {
    $entry = 'RunResize.bundle/' + $file.FullName.Substring($bundle.Length + 1).Replace('\', '/')
    [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal)
}
$archive.Dispose()
$zip
