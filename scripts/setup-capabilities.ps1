param([switch]$Verify,[switch]$InstallProviders)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Environment.ps1')
$AtriCapabilityPython = Join-Path $AtriProjectRoot 'runtime\voice\python\python.exe'
if (-not (Test-Path -LiteralPath $AtriCapabilityPython -PathType Leaf)) { $AtriCapabilityPython = Join-Path $AtriProjectRoot 'GPT-SoVITS-v2pro-20261006\runtime\python.exe' }
if (-not (Test-Path -LiteralPath $AtriCapabilityPython -PathType Leaf)) { throw '未找到项目内 Python。控制台可指定已有 Python；能力桥只需标准库。' }
& $AtriCapabilityPython -c "import sqlite3, asyncio, hashlib, json; print('Capability standard-library dependencies ready; SQLite', sqlite3.sqlite_version)"
if ($LASTEXITCODE -ne 0) { throw '能力组件依赖检查失败。' }
if ($InstallProviders) {
    $AtriProviderRoot = Join-Path $AtriProjectRoot 'runtime\capabilities\node'
    $AtriProviderNode = Join-Path $AtriProviderRoot 'node.exe'
    if (-not (Test-Path -LiteralPath $AtriProviderNode -PathType Leaf)) {
        $AtriExistingNode = Get-Command node -ErrorAction SilentlyContinue
        if (-not $AtriExistingNode) { throw '需已有 Node.js 22.19+（建议24 LTS）；安装器不会改系统目录。' }
        Copy-Item -LiteralPath $AtriExistingNode.Source -Destination $AtriProviderNode
    }
    $AtriNpmRoot=Join-Path $AtriProjectRoot 'data\cache\npm-bootstrap'
    New-Item -ItemType Directory -Path $AtriNpmRoot -Force | Out-Null
    $AtriNpmArchive=Join-Path $AtriNpmRoot 'npm-10.9.3.tgz'
    if (-not (Test-Path -LiteralPath $AtriNpmArchive)) { Invoke-WebRequest -Uri 'https://registry.npmjs.org/npm/-/npm-10.9.3.tgz' -OutFile $AtriNpmArchive }
    & $AtriCapabilityPython -c 'import tarfile,sys; tarfile.open(sys.argv[1]).extractall(sys.argv[2],filter="data")' $AtriNpmArchive $AtriNpmRoot
    if ($LASTEXITCODE -ne 0) { throw '隔离npm解包失败。' }
    $env:npm_config_update_notifier='false'
    & $AtriProviderNode (Join-Path $AtriNpmRoot 'package\bin\npm-cli.js') ci --ignore-scripts --prefix $AtriProviderRoot --cache (Join-Path $AtriProjectRoot 'data\cache\npm') --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw '锁定Pi/Playwright依赖安装失败；本地Work仍可用。' }
    $env:PLAYWRIGHT_BROWSERS_PATH=Join-Path $AtriProjectRoot 'data\cache\browser'
    & $AtriProviderNode (Join-Path $AtriProviderRoot 'node_modules\playwright-core\cli.js') install chromium
    if ($LASTEXITCODE -ne 0) { throw '受管Chromium未安装；Pi文件工具仍可用，浏览器明确不可用。' }
}
if ($Verify) {
    & $AtriCapabilityPython (Join-Path $AtriProjectRoot 'runtime\capabilities\verify.py')
    if ($LASTEXITCODE -ne 0) { throw '能力组件实际验证失败。' }
    if (Test-Path -LiteralPath (Join-Path $AtriProjectRoot 'runtime\capabilities\node\node.exe')) {
        & $AtriCapabilityPython (Join-Path $AtriProjectRoot 'runtime\capabilities\verify_pi.py')
        if ($LASTEXITCODE -ne 0) { throw 'Pi/Browser本地真实进程验证失败。' }
    }
}
