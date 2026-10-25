param([switch]$Verify)
. (Join-Path $PSScriptRoot 'Environment.ps1')
$ErrorActionPreference = 'Stop'
$AtriAsrPython = Join-Path $AtriProjectRoot 'runtime\voice\python\python.exe'
if (!(Test-Path -LiteralPath $AtriAsrPython)) { throw '请先生成隔离 Python 推理运行包。' }
$AtriAsrSite = Join-Path $AtriProjectRoot 'runtime\asr\site-packages'
$AtriAsrModels = Join-Path $AtriProjectRoot 'data\models\asr\SenseVoiceSmall'
$AtriAsrWheelCache = Join-Path $AtriProjectRoot 'data\cache\asr-wheels'
New-Item -ItemType Directory -Force -Path $AtriAsrSite,$AtriAsrModels,$AtriAsrWheelCache | Out-Null

function Receive-AtriPublicFile([string]$Url,[string]$Path,[string]$Hash) {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Hash) { return }
    $AtriDownloadPart = $Path + '.download'
    Invoke-WebRequest -Uri $Url -OutFile $AtriDownloadPart
    if ((Get-FileHash -LiteralPath $AtriDownloadPart -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Hash) { throw '公开依赖 SHA256 与锁定版本不符；未安装。' }
    Move-Item -LiteralPath $AtriDownloadPart -Destination $Path -Force
}

$AtriAsrWheels = @(
    @{Name='sherpa_onnx-1.13.8-cp312-cp312-win_amd64.whl';Url='https://files.pythonhosted.org/packages/24/af/33e9dcaa527bbc0c2526959713626607a46da2d7b19871b7773d8607d356/sherpa_onnx-1.13.8-cp312-cp312-win_amd64.whl';Hash='566e49ad3fb2aa8ceb723a52f9809aa6c2ee0a5eb3b0946c3fd2374af807a5c6'},
    @{Name='sherpa_onnx_core-1.13.8-py3-none-win_amd64.whl';Url='https://files.pythonhosted.org/packages/94/38/64356ad97f68fffcf01fe7545407ad18a2ea86c1ffae5b0950f7fac73638/sherpa_onnx_core-1.13.8-py3-none-win_amd64.whl';Hash='5579e80196d516e6dae23c8f629292ce3142ab8869925d32b94612fd86f93733'}
)
foreach ($AtriWheel in $AtriAsrWheels) {
    $AtriWheelPath = Join-Path $AtriAsrWheelCache $AtriWheel.Name
    Receive-AtriPublicFile $AtriWheel.Url $AtriWheelPath $AtriWheel.Hash
    [System.IO.Compression.ZipFile]::ExtractToDirectory($AtriWheelPath,$AtriAsrSite,$true)
}
$env:HF_HUB_OFFLINE='0'; $env:HF_HUB_DISABLE_XET='1'; $env:HF_HUB_DISABLE_IMPLICIT_TOKEN='1'
& $AtriAsrPython -m huggingface_hub.cli.hf download 'csukuangfj/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17' --revision '2365baeacb507f821a0c8120fcee3d484dba7a07' --include 'model.int8.onnx' 'tokens.txt' 'LICENSE' 'README.md' 'test_wavs/zh.wav' 'test_wavs/ja.wav' --local-dir $AtriAsrModels --max-workers 2 --quiet
if ($LASTEXITCODE -ne 0) { throw '固定版本 SenseVoiceSmall 下载失败。' }
Receive-AtriPublicFile 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx' (Join-Path $AtriProjectRoot 'data\models\asr\silero-vad.onnx') '9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6'
Receive-AtriPublicFile 'https://raw.githubusercontent.com/modelscope/FunASR/main/MODEL_LICENSE' (Join-Path $AtriAsrModels 'MODEL_LICENSE.SenseVoice') '7dba975a2069691db4992b0592d70828b330d2f8a30a71450f4e152a554e84f8'
Receive-AtriPublicFile 'https://raw.githubusercontent.com/snakers4/silero-vad/master/LICENSE' (Join-Path $AtriProjectRoot 'data\models\asr\LICENSE.silero-vad') '2e63e9a38b6e8fc0c7bc37ce174caca1862870856c6daf5697cfb785e925520b'
if ($Verify) {
    & $AtriAsrPython (Join-Path $AtriProjectRoot 'scripts\verify-asr.py')
    if ($LASTEXITCODE -ne 0) { throw '本地中日 ASR/VAD 验证失败。' }
}
Write-Output '离线 SenseVoiceSmall / Silero VAD 已部署，所有缓存位于项目 data 内。'
