$ErrorActionPreference = 'Stop'
$probeRoot = $PSScriptRoot
$vsInstall = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vsInstall) { throw 'The x64 MSVC build tools are required for this isolated experiment.' }
& dotnet build "$probeRoot\TrenchHQ.WindowContainmentProbe.csproj" -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
$probeOutput = Join-Path $probeRoot 'bin\x64\Debug\net8.0-windows10.0.19041.0'
$msvc = (Get-ChildItem "$vsInstall\VC\Tools\MSVC" -Directory | Sort-Object Name -Descending | Select-Object -First 1).FullName
$sdk = "${env:ProgramFiles(x86)}\Windows Kits\10"
$sdkVersion = (Get-ChildItem "$sdk\Include" -Directory | Where-Object { Test-Path "$($_.FullName)\um\Windows.h" } | Sort-Object Name -Descending | Select-Object -First 1).Name
if (!$msvc -or !$sdkVersion) { throw 'MSVC and Windows SDK headers are required.' }
$compile = @('/nologo', '/LD', '/std:c++17', '/W4', '/WX', '/EHsc', '/MT', '/DUNICODE', '/D_UNICODE',
    "/I$msvc\include", "/I$sdk\Include\$sdkVersion\um", "/I$sdk\Include\$sdkVersion\shared", "/I$sdk\Include\$sdkVersion\ucrt",
    "$probeRoot\WindowLockProbe.cpp", "/Fo$probeOutput\WindowLockProbe.obj", '/link', "/OUT:$probeOutput\WindowLockProbe.dll",
    "/LIBPATH:$msvc\lib\x64", "/LIBPATH:$sdk\Lib\$sdkVersion\um\x64", "/LIBPATH:$sdk\Lib\$sdkVersion\ucrt\x64", 'user32.lib', 'comctl32.lib')
Push-Location $probeOutput
try { & "$msvc\bin\Hostx64\x64\cl.exe" @compile; if ($LASTEXITCODE -ne 0) { throw 'Native probe build failed.' } }
finally { Pop-Location }
