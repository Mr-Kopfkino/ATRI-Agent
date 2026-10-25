. (Join-Path $PSScriptRoot 'Environment.ps1')
$AtriRequired = @('assets\derived\persona.json','assets\derived\knowledge.json','assets\derived\sprites\neutral.png','assets\derived\sprites\neutral_talk.png','runtime\voice\python\python.exe','runtime\voice\worker.py','runtime\asr\worker.py','models\voice\AtriTurbo','data\models\multilingual-e5-small','data\models\asr\SenseVoiceSmall\model.int8.onnx')
$AtriMissing = @()
$AtriAudioCatalog = Join-Path $AtriProjectRoot 'assets\derived\game-audio.json'
$AtriRequired += 'assets\derived\game-audio.json'
if (Test-Path -LiteralPath $AtriAudioCatalog) {
    $AtriAudioEntries = (Get-Content -LiteralPath $AtriAudioCatalog -Raw -Encoding UTF8 | ConvertFrom-Json).clips
    foreach ($AtriClip in $AtriAudioEntries.PSObject.Properties) { $AtriRequired += $AtriClip.Value.cache.Replace('/', '\') }
}
foreach ($AtriRelative in $AtriRequired) {
    $AtriPresent = Test-Path -LiteralPath (Join-Path $AtriProjectRoot $AtriRelative)
    $AtriLabel = if ($AtriPresent) { 'OK  ' } else { 'MISS' }
    Write-Output ($AtriLabel + ' ' + $AtriRelative)
    if (-not $AtriPresent) { $AtriMissing += $AtriRelative }
}
& dotnet --list-runtimes
if ($AtriMissing.Count -gt 0) { throw '环境存在缺失项，见上方列表。' }
