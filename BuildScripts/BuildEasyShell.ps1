#Requires -Version 7.0
<#
.SYNOPSIS
    Bootstrap build for Easy Shell: publishes `easy` self-contained and packages it.

.DESCRIPTION
    Unfortunately a ps1 script is needed to build the first ever `easy` on a fresh platform -
    BuildEasyShell.easy does the same job, but cannot run until this one has produced the
    interpreter. The two are meant to stay behaviourally identical; when you change one, change
    the other.

.PARAMETER Configuration
    Build configuration. Applied to BOTH the test run and the publish, so what is validated is
    what is shipped.

.PARAMETER RuntimeIdentifier
    Target RID. Defaults to whatever the installed SDK reports for this machine. It is passed
    explicitly to 'dotnet publish' AND used to name the package, so the two can never disagree.

.PARAMETER SkipTests
    Publish without running the unit tests first. For when you know what you are doing.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateNotNullOrEmpty()]
    [string]$RuntimeIdentifier,

    [switch]$SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Decide failure by exit code alone. Otherwise a tool that merely writes a warning to stderr can abort the build on PowerShell versions where this preference defaults on.
$PSNativeCommandUseErrorActionPreference = $false

#region Helpers
function Invoke-Native {
    <# Run an external tool and abort unless it reports success. #>
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Activity
    )

    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Activity failed with exit code $LASTEXITCODE."
    }
}

function Get-SdkRuntimeIdentifier {
    <#
        The RID the SDK considers current - the same value --use-current-runtime would have
        resolved. Asking for it explicitly is what keeps the published binary and the package name
        in agreement on arm64 and musl, where guessing from pointer size gets it wrong.
    #>
    $info = & dotnet --info 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not query the .NET SDK. Is dotnet installed and on PATH?'
    }

    $match = $info | Select-String -Pattern '^\s*RID:\s*(\S+)\s*$' | Select-Object -First 1
    if (-not $match) {
        throw 'Could not determine the runtime identifier from dotnet --info.'
    }
    return $match.Matches[0].Groups[1].Value
}

function Remove-ItemResilient {
    <#
        Deleting a build output that something else is holding open is routine, not exceptional:
        cloud sync clients, indexers, antivirus and just-exited child processes all take transient
        locks. Retry with backoff, then say which files are to blame instead of surfacing a bare
        IOException. Mirrors CommonUtilities.Remove, which is what the .easy script gets for free.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$Attempts = 5,
        [int]$BaseDelayMilliseconds = 150
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }

    for ($attempt = 1; ; $attempt++) {
        try {
            # Read-only files (a checked-out artifact, a restored NuGet asset) refuse to be deleted.
            Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
                Where-Object { $_.IsReadOnly } |
                ForEach-Object { $_.IsReadOnly = $false }

            Remove-Item -LiteralPath $Path -Recurse -Force
            return
        }
        catch {
            if ($attempt -ge $Attempts) {
                $locked = Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
                    Where-Object {
                        try { $stream = $_.Open('Open', 'ReadWrite', 'None'); $stream.Dispose(); $false }
                        catch { $true }
                    } |
                    Select-Object -First 10

                $detail = if ($locked) {
                    "Locked file(s):`n  " + (($locked | ForEach-Object { $_.FullName }) -join "`n  ")
                }
                else {
                    'No specific locked file could be identified (the directory itself may be open, e.g. as a terminal working directory).'
                }

                throw "Cannot delete '$Path' after $Attempts attempts.`n$detail`nClose anything using this folder, then run again. ($($_.Exception.Message))"
            }

            Start-Sleep -Milliseconds ($BaseDelayMilliseconds * [Math]::Pow(2, $attempt - 1))
        }
    }
}

function Get-ProjectVersion {
    <# The <Version> from a csproj, so packages carry more identity than a date. #>
    param([Parameter(Mandatory)][string]$ProjectFile)

    $match = [regex]::Match((Get-Content -LiteralPath $ProjectFile -Raw), '<Version>([^<]+)</Version>')
    return $match.Success ? $match.Groups[1].Value.Trim() : 'unversioned'
}
#endregion

#region Paths
# Project and tests sit beside this script's folder; only the shared output tree is repo-relative,
# so EasyShell keeps building if it is ever vendored at a different depth.
$ScriptRoot    = $PSScriptRoot
$EasyShellRoot = Split-Path -Parent $ScriptRoot
$RepoRoot      = (Get-Item -LiteralPath $EasyShellRoot).Parent.Parent.FullName

$ProjectPath   = Join-Path $EasyShellRoot 'EasyShell.Cli'
$ProjectFile   = Join-Path $ProjectPath 'EasyShell.Cli.csproj'
$TestsPath     = Join-Path $EasyShellRoot 'EasyShell.Tests'
$PublishFolder = Join-Path $RepoRoot 'Publish/Utilities/EasyShell/Current'
$ArchiveFolder = Join-Path $RepoRoot 'Publish/Packages'
#endregion

Write-Host 'Publish for Final Packaging build.'

if (-not $RuntimeIdentifier) {
    $RuntimeIdentifier = Get-SdkRuntimeIdentifier
}
Write-Host "Target runtime: $RuntimeIdentifier ($Configuration)"

# Do not publish something that does not pass its own tests
if (-not $SkipTests) {
    Write-Host 'Running unit tests.'
    Invoke-Native dotnet @('test', $TestsPath, '--configuration', $Configuration, '--nologo') 'Unit tests'
}

# Refuse to delete the interpreter that is running this build. Only a concern once `easy` has been installed from this very folder, which is exactly the steady state on a developer machine.
$RunningHost = [System.Environment]::ProcessPath
if ($RunningHost -and (Test-Path -LiteralPath $PublishFolder)) {
    $PublishFull = (Get-Item -LiteralPath $PublishFolder).FullName
    if ($RunningHost.StartsWith($PublishFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to overwrite '$PublishFull': the process running this build ('$RunningHost') lives inside it. Run the build from a copy located elsewhere."
    }
}

# Clean publish folder
Remove-ItemResilient -Path $PublishFolder

# Publish executable. An explicit -r beats --use-current-runtime: same result, but the RID is now a
# value this script holds, and can therefore label the package with.
Invoke-Native dotnet @(
    'publish', $ProjectPath,
    '--runtime', $RuntimeIdentifier,
    '--self-contained',
    '--configuration', $Configuration,
    '--output', $PublishFolder
) 'dotnet publish'

# Validation. The executable is the deliverable; a PDB only ever was a proxy for one.
$ExecutableName = $RuntimeIdentifier.StartsWith('win') ? 'easy.exe' : 'easy'
$ExecutablePath = Join-Path $PublishFolder $ExecutableName
if (-not (Test-Path -LiteralPath $ExecutablePath)) {
    throw "Build failed. Missing executable: $ExecutablePath"
}

# Smoke test: a self-contained layout can be complete on disk and still not start. Only meaningful
# when the thing we just built can run here, which cross-compiling to another RID rules out.
if ($RuntimeIdentifier -eq (Get-SdkRuntimeIdentifier)) {
    Write-Host 'Smoke testing the published executable.'
    Invoke-Native $ExecutablePath @('--version') 'Published executable smoke test'
}

# Strip development-only files from the package
foreach ($pattern in @('*.pdb', '*.xml')) {
    Get-ChildItem -LiteralPath $PublishFolder -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

# Archive path. Version and date together, so two builds of one day stay distinguishable.
$Version     = Get-ProjectVersion -ProjectFile $ProjectFile
$Date        = Get-Date -Format 'yyyyMMdd'
$ArchiveName = "Utility_EasyShell_${RuntimeIdentifier}_v${Version}_B${Date}.zip"
$ArchivePath = Join-Path $ArchiveFolder $ArchiveName

if (-not (Test-Path -LiteralPath $ArchiveFolder)) {
    New-Item -ItemType Directory -Path $ArchiveFolder -Force | Out-Null
}
if (Test-Path -LiteralPath $ArchivePath) {
    Remove-Item -LiteralPath $ArchivePath -Force
}

# ZipFile rather than Compress-Archive: the cmdlet drops the Unix executable bit, which would ship
# an `easy` that cannot be run after extracting on Linux or macOS.
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Get-Item -LiteralPath $PublishFolder).FullName,
    $ArchivePath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host "Created package: $ArchivePath"
