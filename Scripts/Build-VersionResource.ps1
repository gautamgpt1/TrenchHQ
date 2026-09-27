param(
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [ValidateSet('Application','Library')][string]$FileType = 'Application'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = [xml][IO.File]::ReadAllText((Join-Path $root 'src\TrenchHQ.App\Package.appxmanifest'))
$version = [version]$manifest.Package.Identity.Version
$versionNumbers = @($version.Major, $version.Minor, $version.Build, $version.Revision) -join ','
$type = if ($FileType -eq 'Library') { 2 } else { 1 }
$output = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
$source = [IO.Path]::ChangeExtension($output, '.rc')
# Numeric VERSIONINFO constants keep this resource independent of SDK headers.
$resource = @"
1 VERSIONINFO
FILEVERSION $versionNumbers
PRODUCTVERSION $versionNumbers
FILEFLAGSMASK 0x3fL
FILEFLAGS 0
FILEOS 0x40004L
FILETYPE $type
FILESUBTYPE 0
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "GautamGupta"
      VALUE "FileDescription", "TrenchHQ"
      VALUE "FileVersion", "$version"
      VALUE "ProductName", "TrenchHQ"
      VALUE "ProductVersion", "$version"
      VALUE "LegalCopyright", "Copyright (c) 2026 Gautam Gupta"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x0409, 1200
  END
END
"@
[IO.File]::WriteAllText($source, $resource, [Text.Encoding]::ASCII)
$sdk = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$compiler = Get-ChildItem -LiteralPath $sdk -Directory | Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\rc.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$compiler) { throw 'Windows SDK x64 resource compiler is required.' }
& $compiler /nologo /fo $output $source
if ($LASTEXITCODE -ne 0) { throw 'Windows version-resource compilation failed.' }
