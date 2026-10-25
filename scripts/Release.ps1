param([string]$Name = ('ATRI-' + (Get-Date -Format 'yyyyMMdd-HHmmss')))
. (Join-Path $PSScriptRoot 'Environment.ps1')
& (Join-Path $PSScriptRoot 'Build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
Push-Location $AtriProjectRoot
try {
    & 'runtime\voice\python\python.exe' 'runtime\capabilities\verify_reset.py'
    if ($LASTEXITCODE -ne 0) { throw 'Factory-reset verification failed; release refused.' }
    # Real isolated ledger/artifacts plus a deterministic in-process RPC
    # fixture. No user Work database, external model or paid API is used.
    & 'runtime\voice\python\python.exe' '-I' '-B' 'runtime\capabilities\verify_chat_work.py'
    if ($LASTEXITCODE -ne 0) { throw 'Chat Work verification failed; release refused.' }
    & 'runtime\voice\python\python.exe' 'scripts\freeze-runtime.py' '--release-version' $Name
    if ($LASTEXITCODE -ne 0) { throw 'Runtime inventory failed.' }
    & 'runtime\voice\python\python.exe' 'scripts\make-release.py' '--name' $Name
    if ($LASTEXITCODE -ne 0) { throw 'Release assembly failed.' }
} finally { Pop-Location }
