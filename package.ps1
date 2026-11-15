<#
Builds the Windows release (ported from CheeseGit): a Native AOT publish, packed by Velopack into
  LocalFM-<rid>-Setup.exe         per-user installer (no admin), shortcuts, uninstall entry, in-app updates
  LocalFM-<rid>-Portable.zip      unzip and run; updates still work through the bundled Update.exe
  LocalFM-<v>-<rid>-full.nupkg    what the updater downloads
The Windows counterpart of package.sh; docs/release.md has the whole flow.

  .\package.ps1 -Version <x.y.z> [-Rid win-x64|win-arm64] [-Out <dir>] [-Notes <release-notes.md>] [-Previous <dir>] [-FfmpegPrefix <dir>]

Native code first: localfm_native and the audio-only FFmpeg go into native\out\<rid>\. -FfmpegPrefix names an
FFmpeg built by native/ffmpeg/build.sh (in MSYS2 with the MSVC environment; CI caches it), and native/build.sh
then runs in Git Bash. Without it, an existing native\out\<rid>\ with the bundled FFmpeg is required.

-Previous holds the last release of this channel (vpk download github): vpk then builds a delta package
against it, so updates download only what changed. The Velopack channel is the RID, so every architecture can
share one GitHub release. Signing goes through vpk's own environment variables (VPK_SIGN_TEMPLATE, or the Azure
Trusted Signing ones), so no secret passes through this script.

It needs the .NET 10 SDK, CMake and Visual Studio (or Build Tools) with the "Desktop development with C++"
workload. Works in Windows PowerShell 5.1 and PowerShell 7.
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Rid = "win-x64",
    [string]$Out = (Join-Path $env:TEMP "localfm-package"),
    [string]$Notes = "",
    [string]$Previous = "",
    [string]$FfmpegPrefix = $env:FFMPEG_PREFIX
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Invoke-Checked([scriptblock]$Command, [string]$What) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)" }
}

if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
$publish = Join-Path $Out "publish"
$releases = Join-Path $Out "releases"
New-Item -ItemType Directory -Force $releases | Out-Null
if ($Previous) { Copy-Item -Recurse -Force (Join-Path $Previous "*") $releases }

# Native code: the library and the FFmpeg it ships with.
if ($FfmpegPrefix) {
    Invoke-Checked { bash native/build.sh $Rid ($FfmpegPrefix -replace '\\', '/') } "native/build.sh"
} elseif (-not (Test-Path "native\out\$Rid\FFmpeg-COPYING.LGPLv2.1.txt")) {
    throw "native\out\$Rid has no bundled FFmpeg: build one with native/ffmpeg/build.sh (MSYS2) and pass -FfmpegPrefix"
}

Invoke-Checked { dotnet publish src\LocalFM.UI -c Release -r $Rid -o $publish -p:Version=$Version --nologo -v q } "dotnet publish"
Invoke-Checked { dotnet tool restore | Out-Null } "dotnet tool restore"

# The build generates the .ico from Assets/icon.png (LocalFM.UI.csproj, GenerateAppIcon).
$icon = Join-Path $PSScriptRoot "src\LocalFM.UI\obj\LocalFM.ico"
$log = Join-Path $Out "vpk.log"
$notesArgs = @()
if ($Notes) { $notesArgs = @("--releaseNotes", (Resolve-Path $Notes).Path) }
# Windows PowerShell 5.1 turns redirected stderr from a native command into a terminating error under "Stop".
$ErrorActionPreference = "Continue"
dotnet vpk pack --packId LocalFM --packVersion $Version --packTitle LocalFM --packAuthors "Peter Lenkefi" `
    --packDir $publish --mainExe LocalFM.exe --icon $icon --runtime $Rid --channel $Rid --outputDir $releases @notesArgs *> $log
$vpkExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
if ($vpkExit -ne 0) { Get-Content $log; throw "vpk pack failed (exit code $vpkExit)" }

# Invariant culture: a locale with a decimal comma would print "22,3 MB".
$inv = [Globalization.CultureInfo]::InvariantCulture
function Show-Size([string]$Name, [object[]]$Files) {
    $bytes = ($Files | Measure-Object -Property Length -Sum).Sum
    [Console]::WriteLine([string]::Format($inv, "{0,-28} {1,6:F1} MB", $Name, $bytes / 1e6))
}
Show-Size "publish folder" (Get-ChildItem $publish -Recurse -File)
Show-Size "  LocalFM.exe (AOT binary)" (Get-Item (Join-Path $publish "LocalFM.exe"))
Get-ChildItem $releases -File | Where-Object { $_.Name -match "Setup\.exe$|Portable\.zip$|-full\.nupkg$" } |
    ForEach-Object { Show-Size $_.Name @($_) }
Write-Output "output: $releases"
