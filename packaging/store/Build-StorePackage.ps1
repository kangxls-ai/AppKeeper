[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../../artifacts/store'),
    [string]$DotNet = 'dotnet',
    [string]$MakeAppx
)
$ErrorActionPreference = 'Stop'

if (!$MakeAppx) {
    $tool = Get-Command makeappx.exe -ErrorAction SilentlyContinue
    if ($tool) { $MakeAppx = $tool.Source }
    else {
        $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $MakeAppx = Get-ChildItem -LiteralPath $sdk -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^10\.\d+\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\makeappx.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
}
if (!$MakeAppx -or !(Test-Path -LiteralPath $MakeAppx)) {
    throw 'MakeAppx.exe was not found. Install Windows SDK Build Tools or pass -MakeAppx.'
}

$output = (New-Item -ItemType Directory -Force $OutputDirectory).FullName
$stage = Join-Path $output ('.msix-stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
$stage = (Resolve-Path -LiteralPath $stage).ProviderPath
if (!(Split-Path -Parent $stage).Equals($output, [StringComparison]::OrdinalIgnoreCase) -or
    (Split-Path -Leaf $stage) -notmatch '^\.msix-stage-[a-f0-9]{32}$') {
    throw 'Unexpected MSIX staging directory.'
}

try {
    $project = Join-Path $PSScriptRoot '../../src/AppKeeper/AppKeeper.csproj'
    & $DotNet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o $stage | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Assets') -Destination $stage -Recurse
    # Both app hosts load the same local AppKeeper.dll. The startup host opens it in tray mode.
    Copy-Item -LiteralPath (Join-Path $stage 'AppKeeper.exe') -Destination (Join-Path $stage 'EasyAppKeeper.Startup.exe')
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $stage 'AppxManifest.xml') -Raw -Encoding utf8
    $version = $manifest.Package.Identity.Version
    $package = Join-Path $output "EasyAppKeeper_$($version)_x64.msix"
    & $MakeAppx pack /d $stage /p $package /o | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed ($LASTEXITCODE)." }
    Get-Item -LiteralPath $package
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
