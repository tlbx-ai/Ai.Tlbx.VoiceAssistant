# Script to build and publish NuGet packages with current version numbers
# This script will:
# 1. Build the packages with the current version from Directory.Build.props
# 2. If NUGET_API_KEY environment variable exists, upload packages to NuGet.org

param(
    [Parameter(Mandatory=$false)]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# Read current version from Directory.Build.props
$propsFilePath = Join-Path $PSScriptRoot "Directory.Build.props"
if (Test-Path $propsFilePath) 
{
    $propsContent = Get-Content $propsFilePath -Raw
    if ($propsContent -match '<Version>(\d+\.\d+\.\d+)</Version>') 
    {
        $currentVersion = $Matches[1]
        Write-Host "Building packages with version: $currentVersion" -ForegroundColor Cyan
    }
}

# Create output directory if it doesn't exist
$nupkgDir = Join-Path $PSScriptRoot "nupkg"
if (-not (Test-Path $nupkgDir)) 
{
    New-Item -ItemType Directory -Path $nupkgDir | Out-Null
    Write-Host "Created nupkg directory: $nupkgDir"
}

# Clean the nupkg directory first
Write-Host "Cleaning nupkg directory..." -ForegroundColor Yellow
Remove-Item -Path "$nupkgDir\*.nupkg" -Force -ErrorAction SilentlyContinue

# Check for NuGet API key in environment
$apiKey = $env:NUGET_API_KEY
if ($apiKey) 
{
    Write-Host "NUGET_API_KEY environment variable found. Will attempt to publish packages." -ForegroundColor Green
    $willPublish = $true
}
else 
{
    Write-Host "NUGET_API_KEY environment variable not found. Packages will be built but not published." -ForegroundColor Yellow
    $willPublish = $false
}

# Projects to build
$projects = @(
    "Provider\Ai.Tlbx.VoiceAssistant\Ai.Tlbx.VoiceAssistant.csproj",
    "Provider\Ai.Tlbx.VoiceAssistant.Provider.OpenAi\Ai.Tlbx.VoiceAssistant.Provider.OpenAi.csproj",
    "Provider\Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore\Ai.Tlbx.VoiceAssistant.Provider.OpenAi.AspNetCore.csproj",
    "Provider\Ai.Tlbx.VoiceAssistant.Provider.Google\Ai.Tlbx.VoiceAssistant.Provider.Google.csproj",
    "Provider\Ai.Tlbx.VoiceAssistant.Provider.XAi\Ai.Tlbx.VoiceAssistant.Provider.XAi.csproj",
    "Hardware\Ai.Tlbx.VoiceAssistant.Hardware.Windows\Ai.Tlbx.VoiceAssistant.Hardware.Windows.csproj",
    "Hardware\Ai.Tlbx.VoiceAssistant.Hardware.Web\Ai.Tlbx.VoiceAssistant.Hardware.Web.csproj",
    "Hardware\Ai.Tlbx.VoiceAssistant.Hardware.Linux\Ai.Tlbx.VoiceAssistant.Hardware.Linux.csproj",
    "WebUi\Ai.Tlbx.VoiceAssistant.WebUi\Ai.Tlbx.VoiceAssistant.WebUi.csproj"
)

$allPackagesSuccessful = $true
$publishedPackages = @()

# Build each project
foreach ($project in $projects) 
{
    $projectPath = Join-Path $PSScriptRoot $project
    $projectName = Split-Path $project -Leaf
    $projectName = $projectName -replace "\.csproj$", ""
    
    Write-Host "Building $projectName in $Configuration configuration..." -ForegroundColor Cyan
    
    # Clean and build the project
    dotnet clean $projectPath -c $Configuration
    
    # Make sure the project is restored first
    dotnet restore $projectPath
    
    # Build and pack the project
    dotnet build $projectPath -c $Configuration
    
    if ($LASTEXITCODE -ne 0) 
    {
        Write-Error "Failed to build $projectName"
        $allPackagesSuccessful = $false
        continue
    }
    
    Write-Host "Packing $projectName..." -ForegroundColor Cyan
    dotnet pack $projectPath -c $Configuration --no-build --output $nupkgDir
    
    if ($LASTEXITCODE -ne 0) 
    {
        Write-Error "Failed to pack $projectName"
        $allPackagesSuccessful = $false
        continue
    }
    
    # Find the generated package
    $packagePattern = Join-Path $nupkgDir "$projectName.*.nupkg"
    $packageFiles = Get-ChildItem -Path $packagePattern | Sort-Object LastWriteTime -Descending
    
    if ($packageFiles.Count -eq 0) 
    {
        Write-Error "No package found for $projectName"
        $allPackagesSuccessful = $false
        continue
    }
    
    $package = $packageFiles[0]
    Write-Host "Package created: $($package.Name)" -ForegroundColor Green
    
    # Publish the package if API key is available
    if ($willPublish) 
    {
        Write-Host "Publishing $($package.Name) to NuGet.org..." -ForegroundColor Yellow
        try 
        {
            # Add --skip-duplicate to avoid errors when re-publishing the same version
            $pushResult = dotnet nuget push $package.FullName --api-key $apiKey --source https://api.nuget.org/v3/index.json --skip-duplicate
            
            # Check if the push was successful
            if ($LASTEXITCODE -eq 0) 
            {
                Write-Host "Package $($package.Name) published successfully" -ForegroundColor Green
                $publishedPackages += $package.Name
            } 
            else 
            {
                Write-Host "Failed to publish package $($package.Name)" -ForegroundColor Red
                $allPackagesSuccessful = $false
            }
        }
        catch 
        {
            Write-Host "Exception while publishing package $($package.Name): $_" -ForegroundColor Red
            $allPackagesSuccessful = $false
        }
    }
    
    Write-Host ""
}

Write-Host "Package building completed." -ForegroundColor Cyan
Write-Host "NuGet packages are available in: $nupkgDir" -ForegroundColor Cyan

if ($willPublish) 
{
    if ($publishedPackages.Count -gt 0) 
    {
        Write-Host "Successfully published packages:" -ForegroundColor Green
        foreach ($pkgName in $publishedPackages) 
        {
            Write-Host "  - $pkgName" -ForegroundColor Green
        }
    }
    
    if (-not $allPackagesSuccessful) 
    {
        Write-Host "Some packages were not published successfully. See errors above." -ForegroundColor Red
        exit 1
    }
    else 
    {
        Write-Host "All packages were published successfully." -ForegroundColor Green
    }
}
else
{
    Write-Host "To publish packages, set the NUGET_API_KEY environment variable and run this script again." -ForegroundColor Yellow
}
