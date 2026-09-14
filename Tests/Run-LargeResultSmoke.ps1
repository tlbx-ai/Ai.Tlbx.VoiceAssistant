param(
    [ValidateSet('client', 'managed', 'realtime', 'wire-limit')]
    [string]$Mode = 'client',
    [ValidateSet('catalog', 'dossier', 'both')]
    [string]$Document = 'catalog',
    [string]$WavPath,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
$artifactPath = Join-Path $repoPath '.logs/large-results'
New-Item -ItemType Directory -Force -Path $artifactPath | Out-Null
if (-not $env:OPENAI_API_KEY) { throw 'OPENAI_API_KEY is required. This explicit integration test incurs API cost.' }
if ($Mode -eq 'managed' -and $Document -ne 'catalog') { throw 'The expected-failure managed baseline uses the catalog.' }

if ($Mode -ne 'wire-limit' -and -not $WavPath) {
    if (-not $IsWindows) { throw 'Supply -WavPath with mono PCM16/24kHz audio on non-Windows systems.' }
    Add-Type -AssemblyName System.Speech
    $synth = [System.Speech.Synthesis.SpeechSynthesizer]::new()
    try {
        $germanVoice = $synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.TwoLetterISOLanguageName -eq 'de' } | Select-Object -First 1
        if (-not $germanVoice) { throw 'Install a German SAPI voice or provide -WavPath.' }
        $synth.SelectVoice($germanVoice.VoiceInfo.Name)
        $format = [System.Speech.AudioFormat.SpeechAudioFormatInfo]::new(24000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $WavPath = Join-Path $artifactPath "$Document-request.wav"
        $synth.SetOutputToWaveFile($WavPath, $format)
        $spokenRequest = switch ($Document) {
            'catalog' { 'Kannst du mir aus dem langen Bauteilkatalog den Freigabecode und die freigegebene Menge vorlesen?' }
            'dossier' { 'Was steht in der umfangreichen Projektakte zum Freigabecode und zur freigegebenen Menge?' }
            'both' { 'Nenne mir aus dem langen Bauteilkatalog und der umfangreichen Projektakte jeweils den Freigabecode und die freigegebene Menge.' }
        }
        $synth.Speak($spokenRequest)
    } finally { $synth.Dispose() }
}
$savedWav = $env:LARGE_RESULT_SMOKE_WAV
$savedDossier = $env:LARGE_RESULT_SMOKE_DOSSIER
$savedBoth = $env:LARGE_RESULT_SMOKE_BOTH
Push-Location $repoPath
try {
    $env:LARGE_RESULT_SMOKE_WAV = $WavPath
    $env:LARGE_RESULT_SMOKE_DOSSIER = if ($Document -eq 'dossier') { '1' } else { '0' }
    $env:LARGE_RESULT_SMOKE_BOTH = if ($Document -eq 'both') { '1' } else { '0' }
    if (-not $NoBuild) {
        dotnet build Tests/ContractTests/ContractTests.csproj -c Release --nologo > (Join-Path $artifactPath 'build.log') 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Build failed. See $artifactPath\build.log" }
    }
    $logPath = Join-Path $artifactPath "$Mode-$Document.log"
    dotnet Tests/ContractTests/bin/Release/net10.0/ContractTests.dll "--large-result-$Mode" > $logPath 2>&1
    if ($LASTEXITCODE -ne 0) { Get-Content $logPath -Tail 15; throw "Large-result integration failed. See $logPath" }
    Get-Content $logPath
} finally {
    Pop-Location
    $env:LARGE_RESULT_SMOKE_WAV = $savedWav
    $env:LARGE_RESULT_SMOKE_DOSSIER = $savedDossier
    $env:LARGE_RESULT_SMOKE_BOTH = $savedBoth
}
