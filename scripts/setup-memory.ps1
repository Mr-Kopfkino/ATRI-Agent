param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath($ProjectRoot)
$taskPython = Join-Path $taskRoot 'runtime\voice\python\python.exe'
if (!(Test-Path -LiteralPath $taskPython)) { $taskPython = Join-Path $taskRoot 'GPT-SoVITS-v2pro-20261006\runtime\python.exe' }
if (!(Test-Path -LiteralPath $taskPython)) { throw "缺少已审计的 GPT-SoVITS Python runtime：$taskPython" }
$taskModel = Join-Path $taskRoot 'data\models\multilingual-e5-small'
$taskCache = Join-Path $taskRoot 'data\cache\huggingface'
$taskTemp = Join-Path $taskRoot 'data\temp\embedding'
New-Item -ItemType Directory -Path $taskModel,$taskCache,$taskTemp -Force | Out-Null
$env:HF_HOME = $taskCache
$env:HF_HUB_CACHE = Join-Path $taskCache 'hub'
$env:TORCH_HOME = Join-Path $taskRoot 'data\cache\torch'
$env:TEMP = $taskTemp
$env:TMP = $taskTemp
$env:HF_HUB_DISABLE_IMPLICIT_TOKEN = '1'
$env:HF_HUB_DISABLE_XET = '1'
$env:HF_HUB_DOWNLOAD_TIMEOUT = '180'
$env:PYTHONIOENCODING = 'utf-8'
$taskRevision = '614241f622f53c4eeff9890bdc4f31cfecc418b3'
& $taskPython -m huggingface_hub.cli.hf download intfloat/multilingual-e5-small config.json model.safetensors tokenizer.json tokenizer_config.json special_tokens_map.json sentencepiece.bpe.model README.md --revision $taskRevision --local-dir $taskModel --cache-dir $taskCache --max-workers 2
if ($LASTEXITCODE -ne 0) { throw 'E5 公开模型下载未完成。已下载部分保留，可重新运行同一脚本继续。' }
$taskManifest = [ordered]@{ model='intfloat/multilingual-e5-small'; revision=$taskRevision; dimension=384; device='cpu'; files=@() }
foreach ($taskFile in Get-ChildItem -LiteralPath $taskModel -File) {
    if ($taskFile.Name -eq 'deployment.json') { continue }
    $taskManifest.files += [ordered]@{ name=$taskFile.Name; bytes=$taskFile.Length; sha256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$taskManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskModel 'deployment.json') -Encoding utf8
Write-Output "本地 E5 CPU 模型部署完成：$taskModel"
