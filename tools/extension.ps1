# SPDX-FileCopyrightText: Copyright Corsinvest Srl
# SPDX-License-Identifier: GPL-3.0-only

<#
.SYNOPSIS
    Report, install, uninstall or reinstall this extension in Visual Studio.

.DESCRIPTION
    Wraps VSIXInstaller.exe and the extension folders under
    %LOCALAPPDATA%\Microsoft\VisualStudio\<instance>\Extensions, so a test cycle is one command
    instead of a double-click, a dialog and a manual cleanup.

    Experimental hives by default: that is where an extension under test belongs, and it keeps the
    IDE you actually work in untouched.

    Why this exists rather than "just use Manage Extensions":

    - VS keys extensions by Identity Id, so a build with a changed identity installs *alongside* the
      old one, giving duplicate menu entries and two MCP servers racing for the same lock file —
      which reads as a bug in the code.
    - Deleting the folder is not enough on its own: VS caches menu entries, so a removed extension
      leaves dead commands behind until devenv /updateconfiguration runs.
    - VSIXInstaller writes to a randomly named folder, so there is nothing predictable to delete
      by hand. Every lookup here matches the manifest's Identity Id instead.

.PARAMETER Reinstall
    Remove every installed copy, then install. This is the normal test cycle.

.PARAMETER Install
    Install only, leaving any existing copy to be replaced by VSIXInstaller.

.PARAMETER Uninstall
    Remove every installed copy and refresh the affected hives.

.PARAMETER Path
    The .vsix to install. Defaults to the Release build output.

.PARAMETER Interactive
    Show VSIXInstaller's dialog (the instance checkboxes) instead of installing silently.

.PARAMETER Normal
    Act on the normal hives rather than the experimental ones.

.PARAMETER WhatIf
    List what would be removed and exit. Uninstall only.

.EXAMPLE
    .\tools\extension.ps1
    Lists what is currently installed. No action given means report, never change.

.EXAMPLE
    .\tools\extension.ps1 -Reinstall

.EXAMPLE
    .\tools\extension.ps1 -Uninstall -WhatIf

.EXAMPLE
    .\tools\extension.ps1 -Install -Normal

.EXAMPLE
    .\tools\extension.ps1 -Reinstall -Path C:\temp\Corsinvest.VisualStudio.Agents.vsix
    Installs a .vsix from elsewhere -- a CI artifact, say -- instead of the local build output.
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$Reinstall,
    [switch]$Install,
    [switch]$Uninstall,
    [string]$Path,
    [switch]$Interactive,
    [switch]$Normal
)

$ErrorActionPreference = 'Stop'

if (@($Reinstall, $Install, $Uninstall | Where-Object { $_ }).Count -gt 1) {
    Write-Host "Pick one of -Reinstall, -Install, -Uninstall." -ForegroundColor Red
    exit 1
}
$Status = -not ($Install -or $Uninstall -or $Reinstall)

# Every identity this extension has shipped under: the current one and the former ...ClaudeCode.
$IdentityPattern = 'Corsinvest\.VisualStudio\.(Agents|ClaudeCode)'

# -products * also returns SQL Server Management Studio, which ships through the VS installer but
# can't host a VSIX — name the three editions that can instead of filtering it out afterwards.
$Editions = @(
    'Microsoft.VisualStudio.Product.Community'
    'Microsoft.VisualStudio.Product.Professional'
    'Microsoft.VisualStudio.Product.Enterprise'
)

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$hiveRoot = Join-Path $env:LOCALAPPDATA 'Microsoft\VisualStudio'

# Hive folders are named <version>_<instanceId>[Exp] — "17.0_456b4614Exp" names no product a human
# recognises, so resolve the instance id back to what vswhere calls it.
$script:instanceNames = $null
function Get-InstanceName {
    param([string]$HiveName)

    if ($null -eq $script:instanceNames) {
        $script:instanceNames = @{}
        if (Test-Path $vswhere) {
            foreach ($i in (& $vswhere -products $Editions -format json 2>$null | ConvertFrom-Json)) {
                $script:instanceNames[$i.instanceId] = $i.displayName
            }
        }
    }

    $id = ($HiveName -replace '^\d+\.\d+_', '') -replace 'Exp$', ''
    $name = $script:instanceNames[$id]
    if (-not $name) { $name = $HiveName }
    if ($HiveName -like '*Exp') { "$name (experimental)" } else { $name }
}

# Returns the folder of every installed copy, across both layouts: VSIXInstaller writes
# Extensions\<random>\ while the F5 deploy writes Extensions\Corsinvest\<display name>\, hence
# -Depth 2 and the match on the manifest rather than on the folder name.
function Find-InstalledCopies {
    if (-not (Test-Path $hiveRoot)) { return @() }

    Get-ChildItem $hiveRoot -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+' -and ($Normal -xor ($_.Name -like '*Exp')) } |
        ForEach-Object {
            $hive = $_
            $extensions = Join-Path $hive.FullName 'Extensions'
            if (-not (Test-Path $extensions)) { return }

            Get-ChildItem $extensions -Recurse -Depth 2 -Filter 'extension.vsixmanifest' -ErrorAction SilentlyContinue |
                Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -match $IdentityPattern } |
                ForEach-Object {
                    $identity = ([xml](Get-Content $_.FullName -Raw)).PackageManifest.Metadata.Identity
                    # No build date here: VSIXInstaller stamps the extracted files with the install
                    # time, so the DLL on disk only says when it was unpacked. The date printed when
                    # installing comes from inside the .vsix, where it survives.
                    [pscustomobject]@{
                        Hive    = $hive.Name
                        Name    = Get-InstanceName $hive.Name
                        Id      = $identity.Id
                        Version = $identity.Version
                        Path    = $_.DirectoryName
                    }
                }
        }
}

# VS holds its extension folders open, and VSIXInstaller writes into the hive VS reads at startup:
# either one under a running instance leaves a half-applied state.
function Assert-VisualStudioClosed {
    $running = Get-Process devenv -ErrorAction SilentlyContinue
    if (-not $running) { return }

    Write-Host "Visual Studio is running ($($running.Count) instance(s)). Close it first." -ForegroundColor Red
    $running | Select-Object Id, MainWindowTitle | Format-Table | Out-String | Write-Host
    exit 1
}

# Without this VS keeps serving cached menu entries for an extension that no longer exists.
function Update-VisualStudioConfiguration {
    if (-not (Test-Path $vswhere)) {
        Write-Host "`nvswhere not found — run 'devenv /rootsuffix Exp /updateconfiguration' by hand." -ForegroundColor Yellow
        return
    }

    foreach ($devenv in (& $vswhere -products $Editions -property productPath -format value 2>$null)) {
        if (-not (Test-Path $devenv)) { continue }
        Write-Host "refreshing $(Split-Path (Split-Path (Split-Path $devenv -Parent) -Parent) -Parent | Split-Path -Leaf)..."

        # -Wait matters: devenv detaches like VSIXInstaller does, and a still-running one makes the
        # next command fail its "Visual Studio is running" guard.
        $devenvArgs = if ($Normal) { @('/updateconfiguration') } else { @('/rootsuffix', 'Exp', '/updateconfiguration') }
        Start-Process $devenv -ArgumentList $devenvArgs -Wait
    }
}

function Show-Status {
    $hives = if ($Normal) { 'normal' } else { 'experimental' }
    $copies = @(Find-InstalledCopies)
    if (-not $copies) {
        Write-Host "Nothing installed in the $hives hives." -ForegroundColor Yellow
        Write-Host "Install it with:  .\tools\extension.ps1 -Install"
        return
    }

    Write-Host "Installed ($hives hives):`n"
    foreach ($group in ($copies | Group-Object Hive | Sort-Object Name)) {
        Write-Host "  $(Get-InstanceName $group.Name)" -ForegroundColor Cyan

        foreach ($copy in $group.Group) {
            # The identity is always the same one — printing it every time is noise. It earns a
            # line only when it is the former ...ClaudeCode, which is a leftover to remove.
            $version = "v$($copy.Version)"
            if ($copy.Id -notlike '*.Agents') { $version += "  [$($copy.Id)]" }
            Write-Host "    $version"
            # Path on its own line: Format-Table would truncate it to the console width, dropping
            # exactly the random folder name you need to look at.
            Write-Host "      $($copy.Path)" -ForegroundColor DarkGray
        }

        # More than one copy in a hive is the failure this script exists for: VS loads both, so menu
        # entries double up and two MCP servers race for the same lock file.
        if ($group.Count -gt 1) {
            Write-Host "    $($group.Count) copies — run -Reinstall to collapse them." -ForegroundColor Red
        }
    }

    # An instance missing from the list is worth saying out loud: it reads as "not installed
    # anywhere" only if you already knew how many instances you have.
    if (Test-Path $vswhere) {
        # Compare instance ids, not hive names: a hive is <version>_<id>[Exp] and only the id is
        # known here. Find-InstalledCopies has already filtered to the right Normal/Exp set.
        $installedIds = $copies.Hive | ForEach-Object { ($_ -replace '^\d+\.\d+_', '') -replace 'Exp$', '' }
        foreach ($i in (& $vswhere -products $Editions -format json 2>$null | ConvertFrom-Json)) {
            if ($installedIds -notcontains $i.instanceId) {
                Write-Host "`n  $($i.displayName): not installed" -ForegroundColor DarkYellow
            }
        }
    }
}

# SkipRefresh: during a reinstall the install that follows refreshes anyway, so doing it here too
# just pays for a second devenv run.
function Invoke-Uninstall {
    param([switch]$SkipRefresh)

    $copies = @(Find-InstalledCopies)
    if (-not $copies) {
        Write-Host "Nothing installed." -ForegroundColor Green
        return
    }

    $removed = $false
    foreach ($copy in $copies) {
        if ($PSCmdlet.ShouldProcess($copy.Path, 'Remove extension folder')) {
            Write-Host "removing  $($copy.Path)"
            Remove-Item $copy.Path -Recurse -Force
            $removed = $true
        }
    }

    # -WhatIf: found something but removed nothing, so there is no hive to refresh either.
    if (-not $removed) {
        Write-Host "`n$($copies.Count) extension folder(s) would be removed. Re-run without -WhatIf." -ForegroundColor Yellow
        return
    }

    if (-not $SkipRefresh) { Update-VisualStudioConfiguration }
    Write-Host "`nRemoved from: $(($copies.Name | Sort-Object -Unique) -join ', ')" -ForegroundColor Green
}

function Invoke-Install {
    if (-not $Path) {
        $repoRoot = Split-Path $PSScriptRoot -Parent
        $Path = Join-Path $repoRoot 'src\Corsinvest.VisualStudio.Agents\bin\Release\Corsinvest.VisualStudio.Agents.vsix'
    }
    if (-not (Test-Path $Path)) {
        Write-Host "VSIX not found: $Path" -ForegroundColor Red
        Write-Host "Build it first:  msbuild cv4vs-agents.sln -t:Build -p:Configuration=Release" -ForegroundColor Yellow
        exit 1
    }
    $Path = (Resolve-Path $Path).Path

    if (-not (Test-Path $vswhere)) {
        Write-Host "vswhere not found — install by double-clicking the .vsix instead." -ForegroundColor Yellow
        exit 1
    }

    $instances = & $vswhere -products $Editions -format json 2>$null | ConvertFrom-Json
    if (-not $instances) {
        Write-Host "No Visual Studio installation found." -ForegroundColor Red
        exit 1
    }

    $installer = Join-Path (Split-Path $instances[0].productPath -Parent) 'VSIXInstaller.exe'
    if (-not (Test-Path $installer)) {
        Write-Host "VSIXInstaller.exe not found next to $($instances[0].productPath)" -ForegroundColor Red
        exit 1
    }

    # Say which build this is before installing it: the file name is the same for every build, so
    # without this there is no way to tell a fresh VSIX from a stale download. The .vsix file date
    # is no help either -- for a CI artifact it is when it was downloaded.
    #
    # The zip entry date is the packaging time, and it is what tells two builds apart. It is printed
    # verbatim because zip stores a bare clock reading with no time zone: the number is whatever the
    # machine that built the package saw, so a CI artifact carries the runner's UTC and a local build
    # carries local time. Converting it would be worse than leaving it alone -- .NET attaches this
    # machine's offset on read, so ToLocalTime()/UtcDateTime shift a value that was never in this
    # zone to begin with.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifest = $zip.Entries | Where-Object Name -eq 'extension.vsixmanifest'
        $reader = New-Object IO.StreamReader($manifest.Open())
        $identity = ([xml]$reader.ReadToEnd()).PackageManifest.Metadata.Identity
        $reader.Dispose()
        $packaged = ($zip.Entries | Where-Object Name -eq 'Corsinvest.VisualStudio.Agents.dll').LastWriteTime
        $webView = @($zip.Entries | Where-Object FullName -like 'WebView2/*').Count
    }
    finally { $zip.Dispose() }

    Write-Host "`ninstalling $(Split-Path $Path -Leaf)"
    Write-Host "  version $($identity.Version)   packaged $($packaged.ToString('yyyy-MM-dd HH:mm'))   $webView WebView2 files" -ForegroundColor DarkGray
    foreach ($i in $instances) { Write-Host "  -> $($i.displayName) [$($i.instanceId)]" }

    $vsixArgs = @()
    if (-not $Interactive) { $vsixArgs += '/quiet' }
    if (-not $Normal) { $vsixArgs += '/rootSuffix:Exp' }
    $vsixArgs += "/instanceIds:$($instances.instanceId -join ',')"
    $vsixArgs += $Path

    # VSIXInstaller.exe is a GUI process: called directly it detaches and $LASTEXITCODE stays empty,
    # so the script would report success before the install had even started.
    $code = (Start-Process $installer -ArgumentList $vsixArgs -PassThru -Wait).ExitCode

    # 1001 means "already installed at this version" — not a failure worth stopping a test cycle for.
    switch ($code) {
        0 { Write-Host "`nInstalled." -ForegroundColor Green }
        1001 { Write-Host "`nAlready installed at this version. Uninstall first, or bump VsixVersion." -ForegroundColor Yellow }
        default {
            Write-Host "`nVSIXInstaller exited with $code" -ForegroundColor Red
            exit $code
        }
    }

    # /quiet means the installer says nothing, so confirm the folders exist rather than trusting the
    # exit code alone.
    $landed = @(Find-InstalledCopies)
    if (-not $landed) {
        Write-Host "...but no installed copy was found. Check the log in %TEMP%\dd_VSIXInstaller_*.log" -ForegroundColor Red
        exit 1
    }
    foreach ($copy in $landed) {
        Write-Host "  $($copy.Name)" -ForegroundColor Cyan
        Write-Host "    $($copy.Path)" -ForegroundColor DarkGray
    }

    # Copying the files is not enough: until the configuration is refreshed VS ignores the pkgdef,
    # so the extension is on disk but absent from the menus and from Manage Extensions.
    Write-Host ""
    Update-VisualStudioConfiguration

    Write-Host "`nStart VS to pick it up." -ForegroundColor Green
}

# Reporting doesn't touch anything, so it works with VS open — which is when you most want to ask
# what is installed.
if ($Status) {
    Show-Status
    return
}

Assert-VisualStudioClosed

if ($Uninstall) { Invoke-Uninstall }
elseif ($Install) { Invoke-Install }
else {
    Invoke-Uninstall -SkipRefresh
    Invoke-Install
}
