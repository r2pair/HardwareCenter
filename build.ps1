# Builds the standalone app: one self-contained HardwareCenter.exe in .\dist that runs on any
# 64-bit Windows 10/11 PC without .NET installed.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1

param(
    # Where to put the exe. Defaults to the dist folder; pass another folder if that exe is currently running.
    [string]$Output = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "HardwareCenter.csproj"
$dist = $Output

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false -p:DebugType=none -o $dist --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE" }

$exe = Get-Item (Join-Path $dist 'HardwareCenter.exe')
Write-Output "Built $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"
