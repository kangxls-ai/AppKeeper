[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$SignTool
)
$ErrorActionPreference = 'Stop'
$root = (New-Item -ItemType Directory (Join-Path $env:RUNNER_TEMP ('easyappkeeper-test-' + [guid]::NewGuid().ToString('N')))).FullName
[xml]$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw -Encoding utf8
$identity = $manifest.Package.Identity
if (Get-AppxPackage -Name $identity.Name) {
    throw 'The smoke test requires a clean Windows runner.'
}

$installed = $null
$cert = $null
try {
    $signed = Join-Path $root 'test.msix'
    Copy-Item -LiteralPath $Package -Destination $signed
    $cert = New-SelfSignedCertificate -Subject $identity.Publisher -Type CodeSigningCert -CertStoreLocation 'Cert:\CurrentUser\My'
    $certificate = Join-Path $root 'test.cer'
    Export-Certificate -Cert $cert -FilePath $certificate | Out-Null
    Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    & $SignTool sign /fd SHA256 /sha1 $cert.Thumbprint $signed
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed ($LASTEXITCODE)." }

    Add-AppxPackage -Path $signed
    $installed = Get-AppxPackage -Name $identity.Name
    if (!$installed -or $installed.Version -ne [version]$identity.Version) {
        throw 'Windows did not register the expected package.'
    }

    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class StoreActivation
{
    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class Manager { }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string id,
            [MarshalAs(UnmanagedType.LPWStr)] string args, uint options, out uint processId);
    }

    public static uint Run(string appId)
    {
        var manager = (IManager)new Manager();
        try
        {
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(appId, "--store-self-test", 2, out var pid));
            return pid;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }
}
'@
    $appId = $installed.PackageFamilyName + '!' + $manifest.Package.Applications.Application.Id
    $processId = [StoreActivation]::Run($appId)
    $localState = Join-Path $env:LOCALAPPDATA "Packages\$($installed.PackageFamilyName)\LocalState"
    $report = Join-Path $localState 'package-smoke.txt'
    $deadline = (Get-Date).AddSeconds(120)
    while (!(Test-Path -LiteralPath $report) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (!(Test-Path -LiteralPath $report)) {
        $selfTest = Join-Path $localState 'package-self-test.txt'
        if (Test-Path -LiteralPath $selfTest) { Get-Content -LiteralPath $selfTest -Encoding utf8 }
        throw "Packaged process $processId did not write its smoke report."
    }
    $lines = Get-Content -LiteralPath $report -Encoding utf8
    if ($lines[0] -ne 'EasyAppKeeper' -or $lines[1] -ne (Join-Path $localState 'Data')) {
        throw 'Packaged process selected the wrong product name or data path.'
    }
    Write-Output "PASS - registered $appId and activated it with writable LocalState"
    Get-Content -LiteralPath (Join-Path $localState 'package-self-test.txt') -Encoding utf8
    $dataRoot = Join-Path $localState 'Data'
    New-Item -ItemType Directory -Force $dataRoot | Out-Null
    $library = Join-Path $dataRoot 'AppKeeper.data.json'
    '{"StartWithWindows":false,"Entries":[],"Tags":[]}' | Set-Content -LiteralPath $library -Encoding utf8
    $nativeTest=Join-Path (Split-Path -Parent $Package) 'native/ShellCommandTest.exe'
    $dll=Join-Path $installed.InstallLocation 'EasyAppKeeper.Shell.dll'
    & $nativeTest --registered --metadata
    if ($LASTEXITCODE -ne 0) { throw "Packaged native context menu metadata failed: $LASTEXITCODE" }
    $sampleFolder=(New-Item -ItemType Directory (Join-Path $root "中文 ' & 文件夹")).FullName
    $sampleFile=Join-Path $root "中文 ' & 文件.txt"
    Set-Content -LiteralPath $sampleFile -Value 'source stays unchanged' -Encoding utf8
    foreach ($round in 1,2) {
        & $nativeTest --registered --invoke $sampleFile $sampleFolder
        if ($LASTEXITCODE -ne 0) { throw "Native multi-selection activation failed ($round)." }
        $arguments=@($dll,'--confirm')
        if ($round -eq 2) { $arguments+='右键验收' }
        & $nativeTest @arguments
        if ($LASTEXITCODE -ne 0) { throw "Add dialog confirmation failed ($round): $LASTEXITCODE" }
        $deadline=(Get-Date).AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 200
            $saved=Get-Content -LiteralPath $library -Raw -Encoding utf8 | ConvertFrom-Json
            $entries=@($saved.Entries | Where-Object { $_.Target -in @($sampleFile,$sampleFolder) })
            $complete=($entries.Count -eq 2)
            if ($round -eq 2) { $complete=$complete -and @($entries | Where-Object { $_.Tags -contains '右键验收' }).Count -eq 2 }
        } until ($complete -or (Get-Date) -gt $deadline)
        if (!$complete) { throw "External entries were not persisted ($round)." }
    }
    if ((Get-Content -LiteralPath $sampleFile -Raw).Trim() -ne 'source stays unchanged') { throw 'Original source file changed.' }
    $marker=Join-Path $dataRoot 'shell-disabled'
    Set-Content -LiteralPath $marker -Value ''
    & $nativeTest --registered --hidden
    if ($LASTEXITCODE -ne 0) { throw 'Context-menu disable switch failed.' }
    Remove-Item -LiteralPath $marker
    Write-Output 'PASS - native Store context menu cold start, warm batch delivery, duplicate reuse, new tag selection, source preservation and disable switch'
}
finally {
    if ($installed) {
        $executable=Join-Path $installed.InstallLocation 'AppKeeper.exe'
        Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $executable } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
    if ($installed) { Remove-AppxPackage -Package $installed.PackageFullName }
    if ($cert) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    }
}
