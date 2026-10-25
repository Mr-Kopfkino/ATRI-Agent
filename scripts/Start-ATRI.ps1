. (Join-Path $PSScriptRoot 'Environment.ps1')
$AtriExecutable = Join-Path $AtriProjectRoot 'app\atri_agent.exe'
try {
    if (-not (Test-Path -LiteralPath $AtriExecutable)) { & (Join-Path $PSScriptRoot 'Build.ps1') }
    if (-not (Test-Path -LiteralPath (Join-Path $AtriProjectRoot 'assets\derived\persona.json'))) { throw '人格资料缺失，请运行 scripts\Check-Environment.ps1。' }
    Start-Process -FilePath $AtriExecutable -WorkingDirectory $AtriProjectRoot -WindowStyle Normal | Out-Null
} catch { Write-Error $_; exit 1 }
