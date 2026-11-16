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
    $deadline = (Get-Date).AddSeconds(35)
    while (!(Test-Path -LiteralPath $report) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (!(Test-Path -LiteralPath $report)) { throw "Packaged process $processId did not write its smoke report." }
    $lines = Get-Content -LiteralPath $report -Encoding utf8
    if ($lines[0] -ne 'EasyAppKeeper' -or $lines[1] -ne (Join-Path $localState 'Data')) {
        throw 'Packaged process selected the wrong product name or data path.'
    }
    Write-Output "PASS - registered $appId and activated it with writable LocalState"
}
finally {
    if ($installed) { Remove-AppxPackage -Package $installed.PackageFullName }
    if ($cert) {
        Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
    }
}
