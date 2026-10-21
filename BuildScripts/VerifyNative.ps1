#Requires -Version 7.0
<#
.SYNOPSIS
    Verifies the Native build of `easy` against the Full one: publishes both, requires the Native publish to be free of warnings, and runs every script in Examples/ with each, requiring the same output and a zero exit code from both.

.DESCRIPTION
    The Native build calls .NET through the curated Standard library its source generator wrote, where the Full build reflects; the unit tests hold the two libraries to each other under the JIT, but only a NativeAOT binary can show what the AOT compiler kept. So this publishes one, from a clean intermediate folder so every analyzer and ILC warning is reported again, and runs real scripts through it. It also checks the one difference that is meant to be there: a type outside the Standard library is reachable from Full and does not exist in Native.

    Exits with the number of checks that failed. Each publish log is kept in the output folder.

.PARAMETER Output
    Where the two builds and their logs go. Defaults to a folder under the temp directory.

.PARAMETER RuntimeIdentifier
    Target RID. Defaults to the one the SDK reports for this machine; it has to be one this machine can run.
#>
[CmdletBinding()]
param(
    [string]$Output = (Join-Path ([System.IO.Path]::GetTempPath()) 'easyshell-verify-native'),
    [string]$RuntimeIdentifier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$EasyShellRoot = Split-Path -Parent $PSScriptRoot
$ProjectPath   = Join-Path $EasyShellRoot 'EasyShell.Cli'
$Examples      = Join-Path $EasyShellRoot 'Examples'

if (-not $RuntimeIdentifier) {
    $match = (& dotnet --info 2>&1) | Select-String -Pattern '^\s*RID:\s*(\S+)\s*$' | Select-Object -First 1
    if (-not $match) { throw 'Could not determine the runtime identifier from dotnet --info.' }
    $RuntimeIdentifier = $match.Matches[0].Groups[1].Value
}

# NativeAOT links with the MSVC toolchain through Visual Studio's vcvarsall.bat, and Visual Studio 18's copy runs vswhere.exe by bare name; see BuildEasyShell.ps1.
if ($IsWindows -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$env:PATH;$installer" }
}

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$Exe = $IsWindows ? 'easy.exe' : 'easy'

$failures = [System.Collections.Generic.List[string]]::new()
function Check([bool]$ok, [string]$what) {
    Write-Host ('{0,-6} {1}' -f ($ok ? 'ok' : 'FAILED'), $what)
    if (-not $ok) { $failures.Add($what) }
}

function Publish([string]$build) {
    $folder = Join-Path $Output $build
    $log = Join-Path $Output "$build-publish.log"
    # A clean intermediate folder, so the compiler and ILC report every warning again instead of an incremental build staying quiet about the ones it reported last time.
    & dotnet clean $ProjectPath -c Release -r $RuntimeIdentifier "-p:EasyShellBuild=$build" --nologo -v q *> $null
    $text = & dotnet publish $ProjectPath -c Release -r $RuntimeIdentifier --self-contained -o $folder "-p:EasyShellBuild=$build" --nologo 2>&1 | Out-String
    Set-Content -LiteralPath $log -Value $text
    $warnings = @($text -split "`r?`n" | Where-Object { $_ -match ':\s+warning\s+[A-Z]+\d+' } | Sort-Object -Unique)
    return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0) -and (Test-Path -LiteralPath (Join-Path $folder $Exe)); Folder = $folder; Warnings = $warnings; Log = $log }
}

function Run([string]$folder, [string[]]$arguments) {
    $text = & (Join-Path $folder $Exe) @arguments 2>&1 | Out-String
    return [pscustomobject]@{ Exit = $LASTEXITCODE; Text = ($text -replace "`r`n", "`n").TrimEnd() }
}

Write-Host "Publishing the Native build ($RuntimeIdentifier)."
$native = Publish 'Native'
Check $native.Ok "Native publish succeeded (log: $($native.Log))"
Check ($native.Warnings.Count -eq 0) "Native publish has no warnings ($($native.Warnings.Count) found)"
$native.Warnings | ForEach-Object { Write-Host "         $_" }

Write-Host "Publishing the Full build ($RuntimeIdentifier)."
$full = Publish 'Full'
Check $full.Ok "Full publish succeeded (log: $($full.Log))"

if (-not ($native.Ok -and $full.Ok)) {
    Write-Host "$($failures.Count) check(s) failed; nothing to run."
    exit $failures.Count
}

$nativeBuild = (Run $native.Folder @('--build')).Text
$fullBuild = (Run $full.Folder @('--build')).Text
Check ($nativeBuild.StartsWith('Native')) "Native easy reports itself: $nativeBuild"
Check ($fullBuild.StartsWith('Full')) "Full easy reports itself: $fullBuild"

foreach ($script in Get-ChildItem -LiteralPath $Examples -Filter '*.easy' | Sort-Object Name) {
    $a = Run $full.Folder @($script.FullName)
    $b = Run $native.Folder @($script.FullName)
    Check ($a.Exit -eq 0) "$($script.Name) passes under Full"
    Check ($b.Exit -eq 0) "$($script.Name) passes under Native"
    Check ($a.Text -ceq $b.Text) "$($script.Name) prints the same under both"
    if ($a.Text -cne $b.Text) {
        Write-Host '--- Full'; Write-Host $a.Text
        Write-Host '--- Native'; Write-Host $b.Text
    }
}

# The difference that is meant to be there: Full reaches any type it can load; Native has the Standard library and nothing else.
$probe = Join-Path $Output 'outside-standard.easy'
Set-Content -LiteralPath $probe -Value 'print (System.Uri.EscapeDataString "a b")'
$a = Run $full.Folder @($probe)
$b = Run $native.Folder @($probe)
Check (($a.Exit -eq 0) -and ($a.Text -eq 'a%20b')) 'Full reaches a type outside the Standard library (System.Uri)'
Check ($b.Exit -ne 0) "Native does not: $($b.Text)"

$load = Join-Path $Output 'load-assembly.easy'
Set-Content -LiteralPath $load -Value 'LOADASSEMBLY "System.Text.Json"'
$b = Run $native.Folder @($load)
Check (($b.Exit -ne 0) -and ($b.Text -match 'fixed when it was built')) 'Native refuses LOADASSEMBLY'

$nativeSize = (Get-ChildItem -LiteralPath $native.Folder -Recurse -File | Where-Object Extension -ne '.pdb' | Measure-Object Length -Sum).Sum
$fullSize = (Get-ChildItem -LiteralPath $full.Folder -Recurse -File | Where-Object Extension -ne '.pdb' | Measure-Object Length -Sum).Sum
$fullCount = @(Get-ChildItem -LiteralPath $full.Folder -Recurse -File | Where-Object Extension -ne '.pdb').Count
Write-Host ('Native: {0:N1} MB in one executable. Full: {1:N1} MB in {2} files. (Without .pdb files.)' -f ($nativeSize / 1MB), ($fullSize / 1MB), $fullCount)

if ($failures.Count -eq 0) { Write-Host 'All checks passed.' } else { Write-Host "$($failures.Count) check(s) failed." }
exit $failures.Count
