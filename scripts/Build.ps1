param([switch]$Tests)
. (Join-Path $PSScriptRoot 'Environment.ps1')
Push-Location $AtriProjectRoot
try {
    & dotnet publish 'src\Atri.Desktop\Atri.Desktop.csproj' -c Release --no-self-contained -o 'app' --nologo
    if ($LASTEXITCODE -ne 0) { throw '桌面构建失败。' }
    if ($Tests) {
        & dotnet run --project 'tests\Atri.Tests\Atri.Tests.csproj' -c Release
        if ($LASTEXITCODE -ne 0) { throw '验证失败。' }
    }
} finally { Pop-Location }
