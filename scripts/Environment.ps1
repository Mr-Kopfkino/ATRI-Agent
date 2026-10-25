$ErrorActionPreference = 'Stop'
$AtriProjectRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$env:ATRI_ROOT = $AtriProjectRoot
$AtriData = Join-Path $AtriProjectRoot 'data'
$AtriVariables = @{
    TEMP = (Join-Path $AtriData 'temp'); TMP = (Join-Path $AtriData 'temp')
    HF_HOME = (Join-Path $AtriData 'cache\huggingface'); TORCH_HOME = (Join-Path $AtriData 'cache\torch')
    PIP_CACHE_DIR = (Join-Path $AtriData 'cache\pip'); NUGET_PACKAGES = (Join-Path $AtriData 'cache\nuget')
    DOTNET_CLI_HOME = (Join-Path $AtriData 'cache\dotnet'); XDG_CACHE_HOME = (Join-Path $AtriData 'cache')
    MPLCONFIGDIR = (Join-Path $AtriData 'cache\matplotlib'); NUMBA_CACHE_DIR = (Join-Path $AtriData 'cache\numba')
}
foreach ($AtriEntry in $AtriVariables.GetEnumerator()) {
    New-Item -ItemType Directory -Force -Path $AtriEntry.Value | Out-Null
    [Environment]::SetEnvironmentVariable($AtriEntry.Key, $AtriEntry.Value, 'Process')
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:MSBuildEnableWorkloadResolver = 'false'
$env:PYTHONUTF8 = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
