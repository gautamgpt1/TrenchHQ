param([Parameter(Mandatory=$true)][string]$OutputPath, [Parameter(Mandatory=$true)][string]$FileName)
$ErrorActionPreference = 'Stop'
$pinOutput = [IO.Path]::GetFullPath($OutputPath)
$pinDll = Join-Path $pinOutput $FileName
if (Test-Path -LiteralPath $pinDll) { exit 0 } # Content-addressed, immutable while loaded in another app.
$vsInstall = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsInstall) { throw 'Window pinning requires the x64 MSVC build tools.' }
$msvc = (Get-ChildItem "$vsInstall\VC\Tools\MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdk = "${env:ProgramFiles(x86)}\Windows Kits\10"
$sdkVersion = (Get-ChildItem "$sdk\Include" -Directory | Where-Object { Test-Path "$($_.FullName)\um\Windows.h" } | Sort-Object Name -Descending | Select-Object -First 1).Name
if (!$msvc -or !$sdkVersion) { throw 'MSVC and Windows SDK headers are required.' }
New-Item -ItemType Directory -Path $pinOutput -Force | Out-Null
$compile = @('/nologo', '/LD', '/std:c++17', '/W4', '/WX', '/EHsc', '/MT', '/DUNICODE', '/D_UNICODE',
    "/I$msvc\include", "/I$sdk\Include\$sdkVersion\um", "/I$sdk\Include\$sdkVersion\shared", "/I$sdk\Include\$sdkVersion\ucrt",
    "$PSScriptRoot\WindowPin.cpp", "/Fo$pinOutput\WindowPin.obj", '/link', "/OUT:$pinDll",
    "/LIBPATH:$msvc\lib\x64", "/LIBPATH:$sdk\Lib\$sdkVersion\um\x64", "/LIBPATH:$sdk\Lib\$sdkVersion\ucrt\x64", 'user32.lib', 'comctl32.lib')
Push-Location $pinOutput
try { & "$msvc\bin\Hostx64\x64\cl.exe" @compile; if ($LASTEXITCODE -ne 0) { throw 'Native window-pin build failed.' } }
finally { Pop-Location }
