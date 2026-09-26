[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$EvidencePath,
    [switch]$LiveExchanges,
    [switch]$SkipInteractiveWindowTests
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence = [IO.Path]::GetFullPath($EvidencePath)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
# No real-credential opt-ins are inherited by candidate fixture tests.
Get-ChildItem Env: | Where-Object { $_.Name -match '^TRENCHHQ_' } |
    ForEach-Object { Remove-Item -LiteralPath "Env:$($_.Name)" }
$results = @()
function Invoke-Gate([string]$Name, [string]$Program, [string[]]$Arguments) {
    $log = Join-Path $evidence ($Name + '.log')
    # Windows PowerShell wraps native stderr in ErrorRecord even for successful tools.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Program @Arguments *> $log
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previousPreference }
    $script:results += [pscustomobject]@{Gate=$Name;ExitCode=$code;Log=[IO.Path]::GetFileName($log)}
    $script:results | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $evidence 'gates.json')
    Write-Output "$Name exit=$code"
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 100
        throw "Candidate gate failed: $Name. See its evidence log."
    }
}
Push-Location $root
try {
    if (Test-Path CANDIDATE.json) { Invoke-Gate 'source-before' 'python' @('Scripts/Verify-Candidate.py') }
    Invoke-Gate 'restore' 'dotnet' @('restore','TrenchHQ.slnx','--locked-mode','-p:Platform=x64','-p:Configuration=Release')
    Invoke-Gate 'release-build' 'dotnet' @('build','TrenchHQ.slnx','-c','Release','-p:Platform=x64','-p:ContinuousIntegrationBuild=true','--no-restore')
    foreach ($name in @('LogicTests','SocialTests','IntegrationTests','OnChainIntegrationTests')) {
        Invoke-Gate $name 'dotnet' @('run','--project',"Tests/TrenchHQ.$name/TrenchHQ.$name.csproj",'-c','Release','-p:ContinuousIntegrationBuild=true')
    }
    Invoke-Gate 'sidecar-protocol' '.\SidecarRuntime\node.exe' @('--test','Tests/SidecarProtocol.test.cjs')
    Push-Location OnChainEngine
    try {
        Invoke-Gate 'rust-format' 'cargo' @('+1.88.0','fmt','--all','--','--check')
        Invoke-Gate 'rust-clippy' 'cargo' @('+1.88.0','clippy','--locked','--all-targets','--','-D','warnings')
        Invoke-Gate 'rust-tests' 'cargo' @('+1.88.0','test','--locked')
    } finally { Pop-Location }
    if ($SkipInteractiveWindowTests) {
        $reason = 'Requires an interactive, non-elevated Windows desktop; remains a release gate.'
        foreach ($name in @('pin-native','pin-embedded')) {
            $script:results += [pscustomobject]@{Gate=$name;ExitCode=$null;Status='skipped';Reason=$reason}
            Write-Warning "SKIPPED $name. $reason"
        }
        $script:results | ConvertTo-Json -Depth 3 | Set-Content -Encoding UTF8 (Join-Path $evidence 'gates.json')
    } else {
        Invoke-Gate 'pin-native' 'dotnet' @('run','--project','Tests/TrenchHQ.WindowPinTests','-c','Release','-p:ContinuousIntegrationBuild=true')
        Invoke-Gate 'pin-embedded' 'dotnet' @('run','--project','Tests/TrenchHQ.WindowPinTests','-c','Release','--no-build','--','--embedded-lock')
    }
    Invoke-Gate 'pin-cache' 'dotnet' @('run','--project','Tests/TrenchHQ.WindowPinTests','-c','Release','-p:ContinuousIntegrationBuild=true','--','--cache-only')
    if ($LiveExchanges) {
        Invoke-Gate 'exchanges-live' 'dotnet' @('run','--project','Tests/TrenchHQ.ProviderValidation','-c','Release','-p:ContinuousIntegrationBuild=true')
    }
    if (Test-Path CANDIDATE.json) { Invoke-Gate 'source-after' 'python' @('Scripts/Verify-Candidate.py') }
} finally { Pop-Location }
