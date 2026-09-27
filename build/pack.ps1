<#
.SYNOPSIS
    Builds the extension in Release and packages it as a Playnite .pext file in ./dist.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build/pack.ps1
#>
param(
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/GGDealsWishlist/GGDealsWishlist.csproj'
$tests = Join-Path $root 'tests/GGDealsWishlist.Tests/GGDealsWishlist.Tests.csproj'
$output = Join-Path $root 'src/GGDealsWishlist/bin/Release'
$dist = Join-Path $root 'dist'

if (-not $SkipTests) {
    dotnet test $tests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed; package not created.' }
}

dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$manifest = Get-Content (Join-Path $output 'extension.yaml')
$version = ($manifest | Where-Object { $_ -match '^Version:\s*(.+)$' } | ForEach-Object { $Matches[1].Trim() })
$id = ($manifest | Where-Object { $_ -match '^Id:\s*(.+)$' } | ForEach-Object { $Matches[1].Trim() })

# Playnite supplies its own SDK assembly; shipping a copy causes version conflicts.
$exclude = @('Playnite.SDK.dll', 'Playnite.SDK.xml')
$staging = Join-Path ([IO.Path]::GetTempPath()) ('ggdeals-pack-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
Get-ChildItem $output -File |
    Where-Object { $exclude -notcontains $_.Name -and $_.Extension -notin @('.pdb') } |
    Copy-Item -Destination $staging

New-Item -ItemType Directory -Force -Path $dist | Out-Null
$zip = Join-Path $dist ($id + '_' + $version.Replace('.', '_') + '.zip')
$pext = [IO.Path]::ChangeExtension($zip, '.pext')
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
if (Test-Path $pext) { Remove-Item $pext -Force -Confirm:$false }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
Move-Item $zip $pext
Remove-Item $staging -Recurse -Force -Confirm:$false

Write-Host "Created $pext"
