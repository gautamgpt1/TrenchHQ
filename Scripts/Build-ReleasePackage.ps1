[CmdletBinding()]
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\AppPackages\Release-$(Get-Date -Format 'yyyyMMdd-HHmmss')"),
    [string]$CertificateThumbprint,
    [switch]$StoreUpload
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'src\TrenchHQ.App\TrenchHQ.csproj'
$manifest = Join-Path $repoRoot 'src\TrenchHQ.App\Package.appxmanifest'
$packageOutput = [IO.Path]::GetFullPath($OutputPath)

try {
    $dte = [Runtime.InteropServices.Marshal]::GetActiveObject('VisualStudio.DTE.18.0')
    $unsaved = @($dte.Documents | Where-Object {
        -not $_.Saved -and $_.FullName.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)
    } | ForEach-Object FullName)
    if ($unsaved.Count -ne 0) {
        throw "Visual Studio has unsaved documents: $($unsaved -join ', ')"
    }
}
catch [Runtime.InteropServices.COMException] {
    # Visual Studio is not running; command-line and CI builds remain valid.
}

if (Test-Path -LiteralPath $packageOutput) {
    if (Get-ChildItem -LiteralPath $packageOutput -Force | Select-Object -First 1) {
        throw "Release output must be new or empty: $packageOutput"
    }
}
else {
    New-Item -ItemType Directory -Path $packageOutput | Out-Null
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer vswhere.exe was not found.'
}

$vsInstall = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsInstall) {
    throw 'The Visual Studio x64 C++ build tools are required.'
}

$symbolTool = Get-ChildItem -LiteralPath (Join-Path $vsInstall 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'bin\Hostx64\x64\mspdbcmf.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } |
    Select-Object -First 1
if (-not $symbolTool) {
    throw 'mspdbcmf.exe was not found in the Visual Studio x64 C++ tools.'
}

$publishArguments = @(
    'publish', $project,
    '-c', 'Release',
    '-p:Platform=x64',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:AppxBundle=Never',
    "-p:UapAppxPackageBuildMode=$(if ($StoreUpload) { 'StoreUpload' } else { 'SideLoadOnly' })",
    '-p:ContinuousIntegrationBuild=true',
    '-p:RestoreLockedMode=true',
    "-p:AppxPackageDir=$($packageOutput.TrimEnd('\', '/'))/",
    "-p:MsPdbCmfExeFullpath=$symbolTool",
    "-p:PdbCmfx64ExeFullPath=$symbolTool",
    '-v:minimal'
)

if ($CertificateThumbprint) {
    $thumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue
    if (-not $certificate) {
        throw "Signing certificate was not found in CurrentUser\\My: $thumbprint"
    }
    if (-not $certificate.HasPrivateKey) {
        throw 'The signing certificate does not have an accessible private key.'
    }
    if ($certificate.NotBefore -gt (Get-Date) -or $certificate.NotAfter -le (Get-Date)) {
        throw 'The signing certificate is not currently valid.'
    }
    if (-not ($certificate.EnhancedKeyUsageList | Where-Object { $_.ObjectId.Value -eq '1.3.6.1.5.5.7.3.3' })) {
        throw 'The certificate is not valid for code signing.'
    }

    $publisher = ([xml](Get-Content -Raw -LiteralPath $manifest)).Package.Identity.Publisher
    if ($certificate.Subject -ne $publisher) {
        throw "Certificate subject '$($certificate.Subject)' does not match package publisher '$publisher'."
    }

    $publishArguments += '-p:AppxPackageSigningEnabled=true', "-p:PackageCertificateThumbprint=$thumbprint"
}
else {
    $publishArguments += '-p:AppxPackageSigningEnabled=false'
}

Push-Location $repoRoot
try {
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Release package build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$artifacts = @(Get-ChildItem -LiteralPath $packageOutput -Recurse -File |
    Where-Object { $_.Extension -in '.msix', '.appxsym', '.msixupload' -and $_.DirectoryName -notlike '*\Dependencies\*' })
if (-not ($artifacts | Where-Object Extension -eq '.msix') -or
    -not ($artifacts | Where-Object Extension -eq '.appxsym')) {
    throw 'The release build did not produce both the application MSIX and symbol package.'
}
if ($StoreUpload -and -not ($artifacts | Where-Object Extension -eq '.msixupload')) {
    throw 'The Store build did not produce a submission upload container.'
}

if ($certificate) {
    $signature = Get-AuthenticodeSignature -LiteralPath ($artifacts | Where-Object Extension -eq '.msix').FullName
    if ($signature.Status -ne 'Valid') {
        throw "The completed MSIX signature is not valid: $($signature.StatusMessage)"
    }
}

$artifacts | Sort-Object Extension | ForEach-Object {
    [pscustomobject]@{
        Path = $_.FullName
        Length = $_.Length
        SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
