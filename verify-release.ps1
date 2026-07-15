param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = $(if ($IsWindows) { "win-x64" } else { "linux-x64" }),
    [switch]$SkipAot
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
    dotnet build TLBX.Ai.VoiceAssistant.slnx -c $Configuration --nologo
    Assert-LastCommandSucceeded "Solution build"

    dotnet run --project Tests\ContractTests\ContractTests.csproj -c $Configuration --no-build
    Assert-LastCommandSucceeded "Provider contract tests"

    dotnet run --project Demo\Ai.Tlbx.VoiceAssistant.Demo.Console\Ai.Tlbx.VoiceAssistant.Demo.Console.csproj -c $Configuration --no-build -- --smoke-test
    Assert-LastCommandSucceeded "Console demo smoke test"

    if (-not $SkipAot)
    {
        if ($IsWindows -and -not (Get-Command link.exe -ErrorAction SilentlyContinue))
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

        dotnet publish Tests\AotTest\AotTest.csproj -c $Configuration -r $RuntimeIdentifier --self-contained true --nologo
        Assert-LastCommandSucceeded "Native AOT publish"
    }

    Write-Host "Release verification passed." -ForegroundColor Green
}
finally
{
    Pop-Location
}
