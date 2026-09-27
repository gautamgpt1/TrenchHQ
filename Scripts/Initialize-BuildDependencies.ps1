[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lock = Get-Content -Raw -LiteralPath (Join-Path $root 'release\dependencies.json') | ConvertFrom-Json
$runtime = Join-Path $root 'artifacts\runtime\node.exe'
if (-not (Test-Path -LiteralPath $runtime) -or
    (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash -ne $lock.node.sha256) {
    $temporary = Join-Path $env:TEMP ('TrenchHQ-node-' + [guid]::NewGuid().ToString('N') + '.exe')
    & curl.exe --fail --location --silent --show-error --connect-timeout 15 --max-time 180 --output $temporary $lock.node.url
    if ($LASTEXITCODE -ne 0) { throw 'The official Node download failed or timed out; no runtime was replaced.' }
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $lock.node.sha256) {
        throw 'Node download hash does not match the reviewed dependency lock.'
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $runtime) | Out-Null
    Copy-Item -LiteralPath $temporary -Destination $runtime
    Remove-Item -LiteralPath $temporary
}
Push-Location (Join-Path $root 'src\MarketSidecar')
try {
    $npmCli = Join-Path (Split-Path (Get-Command npm.cmd -ErrorAction Stop).Source) 'node_modules\npm\bin\npm-cli.js'
    if (-not (Test-Path -LiteralPath $npmCli)) { throw 'The npm CLI installation could not be located.' }
    & $runtime $npmCli ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Locked npm dependency restore failed.' }
    & $runtime 'node_modules\esbuild\bin\esbuild' 'main.cjs' '--bundle' '--platform=node' '--format=cjs' '--external:http-proxy-agent' '--external:https-proxy-agent' '--external:socks-proxy-agent' '--outfile=dist/sidecar.bundle.cjs'
    if ($LASTEXITCODE -ne 0) { throw 'Sidecar build failed.' }
} finally { Pop-Location }
