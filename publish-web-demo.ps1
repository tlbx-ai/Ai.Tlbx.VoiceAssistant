[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location $PSScriptRoot
try {
    [xml]$props = Get-Content Directory.Build.props
    $version = [string]$props.Project.PropertyGroup.Version
    if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "artifacts/web-demo/$version" }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path $output) { throw 'Choose a fresh OutputDirectory; existing artifacts are preserved.' }
    # This framework-dependent Blazor app does not publish Native AOT binaries.
    & ./verify-release.ps1 -Configuration Release -SkipAot
    $app = Join-Path $output 'app'
    dotnet publish Demo/Ai.Tlbx.VoiceAssistant.Demo.Web -c Release -f net11.0 --no-restore --self-contained false -p:UseAppHost=false -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Web demo publish failed.' }
    Copy-Item Demo/Ai.Tlbx.VoiceAssistant.Demo.Web/README.md (Join-Path $app 'README.md')
    Copy-Item LICENSE (Join-Path $app 'LICENSE')
    $files = @(Get-ChildItem $app -Recurse -File)
    if ($files | Where-Object Name -match '\.(user|pfx|key|pem|log)$|\.secrets\.') { throw 'Unexpected private/development files in publish output.' }
    foreach ($config in Get-ChildItem $app -Filter 'appsettings*.json') {
        $settings = Get-Content -Raw $config.FullName | ConvertFrom-Json
        if ($settings.PSObject.Properties.Name | Where-Object { $_ -notin @('Logging', 'AllowedHosts') }) { throw 'Review application configuration before publication.' }
    }
    $deps = Get-Content -Raw (Join-Path $app 'Ai.Tlbx.VoiceAssistant.Demo.Web.deps.json') | ConvertFrom-Json
    foreach ($dependency in @('Ai.Tlbx.VoiceAssistant.Sip/11.9.0','Ai.Tlbx.Sip/1.0.0')) {
        if ($dependency -notin $deps.libraries.PSObject.Properties.Name) { throw "Missing published SIP dependency: $dependency" }
    }
    foreach ($file in @('Ai.Tlbx.VoiceAssistant.Demo.Web.dll','Ai.Tlbx.VoiceAssistant.Demo.Web.runtimeconfig.json','sip-client.example.json')) {
        if (!(Test-Path (Join-Path $app $file))) { throw "Missing published file: $file" }
    }
    if (!(Get-ChildItem (Join-Path $app 'wwwroot/_framework') -File -Filter '*.js*')) { throw 'Missing Blazor browser assets.' }
    $zip = Join-Path $output "Ai.Tlbx.VoiceAssistant.Demo.Web.$version.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($app, $zip)
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($zip))" | Set-Content (Join-Path $output 'SHA256SUMS')
    Write-Output "Web demo archive: $zip"
} finally { Pop-Location }
