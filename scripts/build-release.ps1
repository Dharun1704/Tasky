<#
Builds Tasky's two release zips into dist\:
  Tasky-<version>-win-x64.zip             needs the .NET 10 Desktop Runtime
  Tasky-<version>-win-x64-standalone.zip  includes .NET
Each contains Tasky.exe and LICENSE.txt.

Each build starts from an empty obj\Release: a framework-dependent build made right after a
self-contained one reuses its intermediate files and comes out broken (it crashes at start-up).
The standalone exe isn't compressed: a compressed single file unpacks into memory and makes Tasky
use about twice the memory all day. The zip compresses it for the download instead.

The release workflow (.github/workflows/release.yml) runs this; run it locally the same way:
  powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1 [-Version 1.3.0]
Without -Version it uses the <Version> in Tasky.csproj.
#>
param([string]$Version)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'Tasky.csproj'
if (-not $Version) { $Version = @(([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ })[0] }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version '$Version' isn't of the form 1.2.3." }

$dist = Join-Path $root 'dist'
$work = Join-Path $dist 'build'
$obj = Join-Path $root 'obj\Release'

$builds = @(
    @{ Name = 'small'; Zip = "Tasky-$Version-win-x64.zip"; Extra = @() },
    @{ Name = 'standalone'; Zip = "Tasky-$Version-win-x64-standalone.zip"; Extra = @('-p:SelfContained=true') }
)

New-Item -ItemType Directory -Force $dist | Out-Null
foreach ($b in $builds) {
    $out = Join-Path $work $b.Name
    $zip = Join-Path $dist $b.Zip
    foreach ($old in $obj, $out, $zip) { if (Test-Path $old) { Remove-Item -LiteralPath $old -Recurse -Force } }

    Write-Host "Building $($b.Zip)"
    & dotnet publish $project -c Release -o $out "-p:Version=$Version" -v q -nologo @($b.Extra)
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($b.Name)." }

    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $out 'LICENSE.txt')
    Compress-Archive -Path (Join-Path $out 'Tasky.exe'), (Join-Path $out 'LICENSE.txt') -DestinationPath $zip -CompressionLevel Optimal
}
if (Test-Path $obj) { Remove-Item -LiteralPath $obj -Recurse -Force } # so the next ordinary build starts clean too

Get-ChildItem $dist -Filter "Tasky-$Version-*.zip" | ForEach-Object { '{0}  {1:N1} MB' -f $_.FullName, ($_.Length / 1MB) }
