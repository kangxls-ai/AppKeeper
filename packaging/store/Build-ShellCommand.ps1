[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$output=(New-Item -ItemType Directory -Force $OutputDirectory).FullName
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio C++ x64 tools are required to build the Store shell extension.' }
$dev=Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
$source=Join-Path $PSScriptRoot 'ShellCommand.cpp'
$definition=Join-Path $PSScriptRoot 'ShellCommand.def'
$dll=Join-Path $output 'EasyAppKeeper.Shell.dll'
$object=Join-Path $output 'ShellCommand.obj'
$test=Join-Path $PSScriptRoot 'ShellCommandTest.cpp'
$testExe=Join-Path $output 'ShellCommandTest.exe'
$testObject=Join-Path $output 'ShellCommandTest.obj'
# Paths are quoted as cmd arguments; source and output paths are build-controlled.
$batch=Join-Path $output 'build-shell.cmd'
@"
@echo off
call "$dev" -arch=x64 -host_arch=x64
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /utf-8 /EHsc /MT /O2 /LD "$source" /Fo"$object" /link /DEF:"$definition" /OUT:"$dll" ole32.lib shell32.lib shlwapi.lib uuid.lib
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++17 /utf-8 /EHsc /MT /O2 "$test" /Fo"$testObject" /Fe"$testExe" /link ole32.lib oleaut32.lib shell32.lib shlwapi.lib uiautomationcore.lib uuid.lib user32.lib
exit /b %errorlevel%
"@ | Set-Content -LiteralPath $batch -Encoding utf8
& cmd.exe /d /c $batch | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Native shell extension build failed.' }
Get-Item -LiteralPath $dll
