# loafcat installer for Windows.
#
#   irm https://raw.githubusercontent.com/aadisaraf/loafcat/main/install.ps1 | iex
#
# ---------------------------------------------------------------------------
# Why this exists, and why it is not a worse idea than downloading the zip
# ---------------------------------------------------------------------------
# Windows attaches a "mark of the web" to downloads — an alternate data stream
# named Zone.Identifier — and the app that downloads the file is what attaches it.
# Browsers do. Invoke-WebRequest does not. SmartScreen only warns about files that
# carry the mark, so an app installed by this script simply opens: no blue
# "Windows protected your PC" screen, no More info, no Run anyway.
#
# That is not a trick and it is not a bypass. SmartScreen's question is "did a human
# deliberately choose to run this", and typing an install command is a clearer yes
# than clicking through a warning. It is the same reason scoop, rustup and every
# other command line installer behave this way — and the exact mirror of what
# install.sh does about Gatekeeper on macOS.
#
# What you are trusting is this file. It is short on purpose. Read it first if you
# like — that is the whole point of it being one readable script:
#
#   irm https://.../install.ps1 -OutFile install.ps1; notepad install.ps1
#
# ---------------------------------------------------------------------------
[CmdletBinding()]
param(
    [switch]$Uninstall,
    # Deletes settings too. Without it, an uninstall keeps your theme and timers.
    [switch]$Purge,
    # Pin a specific release, e.g. -Version v0.1.0
    [string]$Version = "",
    # Install a local zip instead of downloading. For testing.
    [string]$LocalZip = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# `irm ... | iex` hands the script to the parser as a string with nothing after it,
# so parameters are unreachable that way — which is the only way most people will run
# this. Environment variables are, and install.sh already uses exactly these names:
#
#   $env:LOAFCAT_VERSION = 'v0.2.0-rc.1'   pin a release (including a prerelease)
#   $env:LOAFCAT_ZIP     = 'C:\x.zip'      install a local package instead
#
# A parameter still wins when one is given, so the two routes cannot disagree.
if (-not $Version -and $env:LOAFCAT_VERSION) { $Version = $env:LOAFCAT_VERSION }
if (-not $LocalZip -and $env:LOAFCAT_ZIP) { $LocalZip = $env:LOAFCAT_ZIP }

$repo = "aadisaraf/loafcat"
$appName = "loafcat.exe"
$installRoot = Join-Path $env:LOCALAPPDATA "Programs\loafcat"
$startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\loafcat.lnk"

function Say  { param($m) Write-Host "==> " -ForegroundColor Cyan -NoNewline; Write-Host $m }
function Note { param($m) Write-Host "    $m" -ForegroundColor DarkGray }
function Die  { param($m) Write-Host "error: " -ForegroundColor Red -NoNewline
                Write-Host $m; exit 1 }

# ---------------------------------------------------------------------------
# 0. Is this even a Windows loafcat runs on
# ---------------------------------------------------------------------------
# The version test comes first deliberately: `$IsWindows` does not exist in Windows
# PowerShell 5.1, and under StrictMode reading it would throw before the check could
# report anything useful. `-and` short-circuits, so 5.1 never evaluates the second half.
if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    Die "loafcat's Windows build is Windows only. On macOS use install.sh."
}
$build = [Environment]::OSVersion.Version.Build
if ($build -lt 17763) {
    Die ("loafcat needs Windows 10 version 1809 or later (this is build $build). " +
         "The layered-window compositor and the high-resolution frame clock both " +
         "need APIs added in that release.")
}
if ([Environment]::Is64BitOperatingSystem -eq $false) {
    Die "loafcat ships as x64 only."
}

function Stop-LoafCat {
    # Case-insensitive, and matched against the ASSEMBLY name rather than the file on
    # disk — an installed copy that predates the lower-case rename is still "loafcat"
    # here, so this keeps working across it.
    Get-Process -Name "loafcat" -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 400
}

# ---------------------------------------------------------------------------
# Uninstall
# ---------------------------------------------------------------------------
if ($Uninstall) {
    Say "removing loafcat"

    $claude = Join-Path $env:USERPROFILE ".claude\settings.json"
    if ((Test-Path -LiteralPath $claude) -and
        (Select-String -LiteralPath $claude -Pattern "loafcat" -Quiet)) {
        Write-Host ""
        Write-Host "Claude Code is still connected." -ForegroundColor Yellow
        Write-Host "Open loafcat first and use Settings > Claude Code > Disconnect, so its"
        Write-Host "hook entries come out of ~\.claude\settings.json cleanly. Nothing here"
        Write-Host "will edit that file for you."
        Write-Host ""
    }

    Stop-LoafCat
    Remove-Item -LiteralPath $installRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $startMenu -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $env:USERPROFILE ".loafcat\endpoint.json") `
                -Force -ErrorAction SilentlyContinue
    # Whatever the Settings checkbox wrote, taken back out.
    Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
                        -Name "loafcat" -ErrorAction SilentlyContinue

    if ($Purge) {
        Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA "loafcat") `
                    -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $env:USERPROFILE ".loafcat") `
                    -Recurse -Force -ErrorAction SilentlyContinue
        Say "removed, along with its settings."
    } else {
        # Settings survive by default. Most people who uninstall an app they liked
        # enough to install by hand are about to reinstall it, and silently throwing
        # away their theme and their timers is a rude way to be helpful.
        Say "removed. Settings kept -- add -Purge to delete those too."
    }
    exit 0
}

# ---------------------------------------------------------------------------
# 1. Find the package
# ---------------------------------------------------------------------------
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("loafcat-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    if ($LocalZip) {
        Say "using $LocalZip"
        $zip = $LocalZip
    } else {
        $api = if ($Version) {
            "https://api.github.com/repos/$repo/releases/tags/$Version"
        } else {
            "https://api.github.com/repos/$repo/releases/latest"
        }
        Say "looking up the $(if ($Version) { $Version } else { 'latest' }) release"

        try {
            $release = Invoke-RestMethod -Uri $api -Headers @{
                "User-Agent" = "loafcat-install"
            }
        } catch {
            Die ("could not reach GitHub. Check your connection, or download the zip " +
                 "from https://github.com/$repo/releases")
        }

        $asset = $release.assets | Where-Object { $_.name -like "*win-x64*.zip" } |
                 Select-Object -First 1
        if (-not $asset) { Die "that release has no Windows package attached." }

        Say "downloading loafcat $($release.tag_name)"
        $zip = Join-Path $tmp "loafcat.zip"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip

        # Checksums are published next to the package. Verified when present rather
        # than required, so an older release without one still installs.
        $sums = $release.assets | Where-Object { $_.name -like "*win-x64*.sha256" } |
                Select-Object -First 1
        if ($sums) {
            $expected = ((Invoke-WebRequest -Uri $sums.browser_download_url).Content `
                         -split '\s+')[0].Trim().ToLowerInvariant()
            $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($expected -ne $actual) {
                Die "checksum mismatch. Expected $expected, got $actual. Not installing."
            }
            Note "sha256 verified"
        }
    }

    # ---------------------------------------------------------------------------
    # 2. Install it
    # ---------------------------------------------------------------------------
    # Per-user, under %LOCALAPPDATA%. Never elevated: an installer that asks for
    # administrator to copy one folder has not earned it, and loafcat needs no part
    # of the machine it does not already own.
    Say "extracting"
    $unpack = Join-Path $tmp "unpack"
    Expand-Archive -LiteralPath $zip -DestinationPath $unpack -Force

    # The zip contains one top-level directory named after the version.
    $payload = Get-ChildItem -LiteralPath $unpack -Directory | Select-Object -First 1
    if (-not $payload) { $payload = Get-Item -LiteralPath $unpack }
    if (-not (Test-Path -LiteralPath (Join-Path $payload.FullName $appName))) {
        Die "$appName is not in that package."
    }

    Stop-LoafCat

    Say "installing to $installRoot"
    if (Test-Path -LiteralPath $installRoot) {
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $payload.FullName "*") -Destination $installRoot `
              -Recurse -Force

    # Invoke-WebRequest does not attach Zone.Identifier, so there should be nothing
    # to strip. Done anyway, because "should be" is not a thing to leave a first
    # launch to — and Expand-Archive propagates the mark to every extracted file if
    # the zip ever picked one up.
    Get-ChildItem -LiteralPath $installRoot -Recurse -File | ForEach-Object {
        Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
    }

    # A Start Menu entry, so loafcat is reachable from the Start search box like any
    # other app. This is the closest thing Windows has to being in /Applications.
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenu)
    $shortcut.TargetPath = Join-Path $installRoot $appName
    $shortcut.WorkingDirectory = $installRoot
    $shortcut.Description = "A pixel cat that reacts to your cursor and your typing"
    $shortcut.Save()

    Say "starting loafcat"
    Start-Process -FilePath (Join-Path $installRoot $appName)

    Write-Host ""
    Write-Host "loafcat is installed and running." -ForegroundColor Green
    Write-Host ""
    Write-Host "  Where       $installRoot"
    Write-Host "  Find it     in the notification area, as a cat's face. If you cannot"
    Write-Host "              see it, drag it out of the ^ overflow — Windows hides new"
    Write-Host "              tray icons there by default."
    Write-Host "  Settings    left-click the tray cat; right-click for quit"
    Write-Host "  Turn it off tray menu or Settings; opening the app again turns it on"
    Write-Host ""
    Write-Host "It asked for no permissions, and it never will. Source and issues:"
    Write-Host "https://github.com/$repo"
    Write-Host ""
    # `irm | iex` cannot pass arguments — the pipe hands `iex` a string with nothing
    # after it — so uninstalling means saving the file first. Two lines rather than
    # one, and no cleverness to explain.
    Write-Host "To remove it:"
    Write-Host "  irm https://raw.githubusercontent.com/$repo/main/install.ps1 -OutFile `$env:TEMP\loafcat.ps1"
    Write-Host "  & `$env:TEMP\loafcat.ps1 -Uninstall"
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
