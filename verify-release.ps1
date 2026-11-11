param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = $(if ($IsWindows) { "win-x64" } else { "linux-x64" }),
    [switch]$SkipAot,
    [ValidateRange(1, 64)]
    [int]$MaxCpuCount = 1
)

$ErrorActionPreference = "Stop"

function Assert-LastCommandSucceeded([string]$Step)
{
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Step failed with exit code $LASTEXITCODE."
    }
}

Push-Location $PSScriptRoot
try
{
    dotnet build TLBX.Ai.VoiceAssistant.slnx -c $Configuration --nologo "-m:$MaxCpuCount"
    Assert-LastCommandSucceeded "Solution build"

    foreach ($framework in @("net10.0", "net11.0"))
    {
        dotnet run --project Tests\ContractTests\ContractTests.csproj -c $Configuration -f $framework --no-build
        Assert-LastCommandSucceeded "Provider contract tests ($framework)"
    }

    node Tests\Browser\conversation-support-contracts.mjs
    Assert-LastCommandSucceeded "Browser response control contracts"

    node Tests\Browser\translation-contracts.mjs
    Assert-LastCommandSucceeded "Browser translation and visual input contracts"

    node Tests\Browser\audio-playback-contracts.mjs
    Assert-LastCommandSucceeded "Browser audio playback contracts"

    node Tests\Browser\audio-capture-contracts.mjs
    Assert-LastCommandSucceeded "Browser audio capture contracts"

    dotnet run --project Demo\Ai.Tlbx.VoiceAssistant.Demo.Console\Ai.Tlbx.VoiceAssistant.Demo.Console.csproj -c $Configuration --no-build -- --smoke-test
    Assert-LastCommandSucceeded "Console demo smoke test"

    if (-not $SkipAot)
    {
        # Git for Windows also supplies link.exe. Always establish the MSVC
        # environment instead of treating any executable with that name as ready.
        if ($IsWindows)
        {
            $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
            if (-not (Test-Path -LiteralPath $vswhere))
            {
                throw "Visual Studio C++ build tools are required for the Native AOT gate."
            }

            $installationPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ([string]::IsNullOrWhiteSpace($installationPath))
            {
                throw "No Visual Studio installation with the C++ x64 toolchain was found."
            }

            $developerShell = Join-Path $installationPath "Common7\Tools\Launch-VsDevShell.ps1"
            & $developerShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
        }

        foreach ($framework in @("net10.0", "net11.0"))
        {
            dotnet publish Tests\AotTest\AotTest.csproj -c $Configuration -f $framework -r $RuntimeIdentifier --self-contained true --nologo "-m:$MaxCpuCount"
            Assert-LastCommandSucceeded "Native AOT publish ($framework)"
        }
    }

    Write-Host "Release verification passed." -ForegroundColor Green
}
finally
{
    Pop-Location
}
