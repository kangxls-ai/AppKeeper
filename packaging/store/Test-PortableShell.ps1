[CmdletBinding()]
param([Parameter(Mandatory)][string]$Executable,[Parameter(Mandatory)][string]$NativeTest)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'This integration test requires a clean GitHub Windows runner.' }
$executable=(Resolve-Path -LiteralPath $Executable).Path
$directory=Split-Path -Parent $executable
$root=(New-Item -ItemType Directory (Join-Path $env:RUNNER_TEMP ('portable-shell-'+[guid]::NewGuid().ToString('N')))).FullName
$library=Join-Path $directory 'AppKeeper.data.json'
'{"StartWithWindows":false,"Entries":[],"Tags":[]}' | Set-Content -LiteralPath $library -Encoding utf8
try {
    $process=Start-Process -FilePath $executable -ArgumentList '--background' -WindowStyle Hidden -PassThru
    $deadline=(Get-Date).AddSeconds(30)
    do { Start-Sleep -Milliseconds 250; $registered=Test-Path -LiteralPath 'HKCU:\Software\Classes\AllFilesystemObjects\shell\EasyAppKeeper.Add' } until ($registered -or (Get-Date) -gt $deadline)
    if (!$registered) { throw 'Portable shell command was not registered.' }
    $folder=(New-Item -ItemType Directory (Join-Path $root "中文 ' & 文件夹")).FullName
    $file=Join-Path $root "中文 ' & 文件.txt";Set-Content -LiteralPath $file -Value 'unchanged' -Encoding utf8
    & $NativeTest unused --legacy $file $folder
    if ($LASTEXITCODE -ne 0) { throw "Explorer legacy multi-selection invoke failed: $LASTEXITCODE" }
    & $NativeTest unused --confirm '绿色版标签'
    if ($LASTEXITCODE -ne 0) { throw "Portable add confirmation failed: $LASTEXITCODE" }
    $deadline=(Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $saved=Get-Content -LiteralPath $library -Raw -Encoding utf8 | ConvertFrom-Json
        $entries=@($saved.Entries | Where-Object { $_.Target -in @($file,$folder) -and $_.Tags -contains '绿色版标签' })
    } until ($entries.Count -eq 2 -or (Get-Date) -gt $deadline)
    if ($entries.Count -ne 2) { throw 'Portable Explorer selection did not deliver both the file and folder.' }
    Write-Output 'PASS - actual Explorer legacy context menu selection, multi-process batch delivery, tag dialog and portable persistence'
}
catch {
    Get-ChildItem (Join-Path $directory 'entry-inbox/*.json') -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -LiteralPath $_.FullName | Out-Host }
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $executable } | Select-Object ProcessId,ExecutablePath,CommandLine | Format-List | Out-Host
    throw
}
finally {
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq $executable } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach($kind in @('AllFilesystemObjects','*','Directory')) {
        $key='HKCU:\Software\Classes\'+$kind+'\shell\EasyAppKeeper.Add'
        if ((Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue).OwnerPath -eq $executable) { Remove-Item -LiteralPath $key -Recurse -Force }
    }
}
