# Unfortunately a ps1 script is needed to build the first ever `easy` on a fresh platform...
param(
    [string]$Configuration = 'Release'
)

Write-Host "Publish for Final Packaging build."

# Root of the build (three levels up from this script)
$ScriptRoot = $PSScriptRoot
$BuildRoot   = (Get-Item -LiteralPath $ScriptRoot).Parent.Parent.Parent.FullName

# Paths
$PublishFolder  = Join-Path $BuildRoot 'Publish/Utilities/EasyShell/Current'
$ProjectPath    = Join-Path $BuildRoot 'External/EasyShell/EasyShell.Cli'
$ArchiveFolder  = Join-Path $BuildRoot 'Publish/Packages'

# Clean publish folder
if (Test-Path -LiteralPath $PublishFolder) {
    Remove-Item -LiteralPath $PublishFolder -Recurse -Force
}

# Publish executable
dotnet publish $ProjectPath `
    --use-current-runtime `
    --self-contained `
    --configuration $Configuration `
    --output $PublishFolder

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

# Validation (equivalent to assert EXISTS)
$pdbPath = Join-Path $PublishFolder 'easy.pdb'
if (-not (Test-Path -LiteralPath $pdbPath)) {
    throw "Build failed. Missing PDB: $pdbPath"
}

# Pick an OS tag
if ($IsWindows) {
    $OS = 'win'
}
elseif ($IsLinux) {
    $OS = 'linux'
}
elseif ($IsMacOS) {
    $OS = 'osx'
}
else {
    $OS = 'unknown'
}

# Pick an architecture
if ([System.Environment]::Is64BitProcess) {
    $Arch = 'x64'
}
else {
    $Arch = 'x86'
}

# Runtime identifier, e.g. "win-x64"
$rid = '{0}-{1}' -f $OS, $Arch

# Archive path
$Date          = Get-Date -Format 'yyyyMMdd'
$archiveName   = "Utility_EasyShell_${rid}_B${Date}.zip"
$ArchivePath   = Join-Path $ArchiveFolder $archiveName

# Ensure archive folder exists
if (-not (Test-Path -LiteralPath $ArchiveFolder)) {
    New-Item -ItemType Directory -Path $ArchiveFolder -Force | Out-Null
}

# Create archive (zip)
Compress-Archive -Path (Join-Path $PublishFolder '*') -DestinationPath $ArchivePath -Force

Write-Host "Created package: $ArchivePath"
