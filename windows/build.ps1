# Builds loafcat for Windows and packages it into dist\loafcat-<version>-win-x64.zip
#
# The counterpart of tools/make-dmg.sh. Run it from anywhere:
#
#   pwsh windows\build.ps1
#
# It works on macOS and Linux too — the project sets EnableWindowsTargeting, so the
# whole thing cross-compiles. That is not a convenience: this port was written on a
# Mac, and being able to produce and inspect the real PE binary without a Windows
# machine is what makes it reviewable at all.

[CmdletBinding()]
param(
    # Skip the art regeneration. Only for a fast local loop — CI never passes this.
    [switch]$SkipAssets,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot "LoafCat\LoafCat.csproj"
$dist = Join-Path $repo "dist"

# The version lives in exactly one place per platform: build.sh for macOS,
# LoafCat.csproj here. The release workflow refuses to publish if either disagrees
# with the tag — a release whose filename and About box differ is worse than none.
[xml]$csproj = Get-Content -LiteralPath $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "could not read <Version> from $project" }
Write-Host "loafcat $version (win-x64)"

if (-not $SkipAssets) {
    # The art is generated, never hand-placed — including the .ico this build embeds.
    Write-Host "generating art..."
    foreach ($theme in @("mono", "tuxedo", "cream")) {
        & python3 (Join-Path $repo "tools\generate_art.py") --theme $theme | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "generate_art.py failed for $theme" }
    }
    & python3 (Join-Path $repo "tools\generate_icon.py") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "generate_icon.py failed" }
}

$stageName = "loafcat-$version-win-x64"
$stage = Join-Path $dist $stageName
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "publishing..."
& dotnet publish $project `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Self-contained, so there is no runtime to install. The cost is ~65MB; the benefit
# is that "download and run" is literally true, which for an app distributed outside
# any store is worth more than the megabytes.

# The assets are NOT embedded. They sit next to the executable exactly as they sit in
# the macOS bundle's Resources, so both platforms read the same generated files and
# a user can drop a community theme into assets\themes\ without repackaging anything.
Write-Host "staging assets..."
Copy-Item -Path (Join-Path $repo "assets") -Destination $stage -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $stage "hooks") -Force | Out-Null
Copy-Item -Path (Join-Path $repo "hooks\loafcat-hook.ps1") `
          -Destination (Join-Path $stage "hooks") -Force
Copy-Item -Path (Join-Path $repo "LICENSE") -Destination $stage -Force
Copy-Item -Path (Join-Path $repo "windows\README.md") `
          -Destination (Join-Path $stage "README.md") -Force

$zip = Join-Path $dist "$stageName.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Write-Host "compressing..."
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

# The same executable, on its own. It is a complete app — the art and the hook
# script are embedded and unpacked on first run — so this is the one file you can
# hand to somebody with no instructions attached. The zip still exists because it
# puts assets\ on disk, which is where a community theme goes.
#
# Published as plain `loafcat.exe`, with no version and no architecture in the name.
# The container carries those: the .dmg is loafcat-<version>.dmg and holds LoafCat.app,
# the .zip is loafcat-<version>-win-x64.zip and holds loafcat.exe. A bare executable IS
# the app rather than a container for it, so it is named the way the app is named —
# otherwise the thing sitting in the user's Downloads folder, and every shell surface
# that has nothing better to call it, says loafcat-0.2.0-win-x64.
$exe = Join-Path $dist "loafcat.exe"
if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }
Copy-Item -LiteralPath (Join-Path $stage "loafcat.exe") -Destination $exe

$size = [math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)
$exeSize = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "built $zip ($size MB)"
Write-Host "built $exe ($exeSize MB, standalone)"
Write-Host ""
Write-Host "NOT code signed. An Authenticode certificate costs a few hundred a year," -ForegroundColor Yellow
Write-Host "and SmartScreen additionally wants reputation the certificate does not buy" -ForegroundColor Yellow
Write-Host "on day one. install.ps1 sidesteps this the same way install.sh does on" -ForegroundColor Yellow
Write-Host "macOS: a file fetched by Invoke-WebRequest carries no mark of the web." -ForegroundColor Yellow
