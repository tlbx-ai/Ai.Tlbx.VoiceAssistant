# Script to build NuGet packages with auto-incremented version numbers
# This script will:
# 1. Commit and push any pending changes to git
# 2. Increment the requested semantic version segment
# 3. Build the packages with the version 1.0.x
# 4. If NUGET_API_KEY environment variable exists, upload packages to NuGet.org

param(
    [Parameter(Mandatory=$false)]
    [string]$Configuration = "Release",

    [Parameter(Mandatory=$false)]
    [string]$CommitMessage = "Automated version increment for NuGet publishing",

    [Parameter(Mandatory=$false)]
    [ValidateSet("patch", "minor", "major")]
    [string]$VersionBump = "patch"
)

$ErrorActionPreference = "Stop"

# Check for uncommitted changes
$gitStatus = git status --porcelain

if ($gitStatus) 
{
    Write-Host "Uncommitted changes detected. Committing changes..." -ForegroundColor Yellow
    
    # Add all changes
    git add .
    
    # Commit changes
    git commit -m $CommitMessage
    
    if ($LASTEXITCODE -ne 0) 
    {
        Write-Error "Failed to commit changes. Aborting."
        exit 1
    }
    
    Write-Host "Changes committed successfully." -ForegroundColor Green
}
else 
{
    Write-Host "No uncommitted changes detected." -ForegroundColor Green
}

# Push to remote
Write-Host "Pushing to remote repository..." -ForegroundColor Yellow
git push

if ($LASTEXITCODE -ne 0) 
{
    Write-Error "Failed to push to remote repository. Aborting."
    exit 1
}

Write-Host "Successfully pushed to remote repository." -ForegroundColor Green

# Create output directory if it doesn't exist
$nupkgDir = Join-Path $PSScriptRoot "nupkg"
if (-not (Test-Path $nupkgDir)) 
{
    New-Item -ItemType Directory -Path $nupkgDir | Out-Null
    Write-Host "Created nupkg directory: $nupkgDir"
}

# Get git hash
$gitHash = git rev-parse --short HEAD
if (-not $gitHash) 
{
    Write-Error "Could not get git hash. Make sure git is installed and this is a git repository."
    exit 1
}
Write-Host "Current git hash: $gitHash"

# Directory.Build.props file path
$propsFilePath = Join-Path $PSScriptRoot "Directory.Build.props"

# Read current version from Directory.Build.props
if (Test-Path $propsFilePath)
{
    $propsContent = Get-Content $propsFilePath -Raw
    if ($propsContent -match '<Version>(\d+)\.(\d+)\.(\d+)</Version>')
    {
        $major = [int]$Matches[1]
        $minor = [int]$Matches[2]
        $patch = [int]$Matches[3]

        $currentVersion = "$major.$minor.$patch"

        switch ($VersionBump)
        {
            "major"
            {
                $major++
                $minor = 0
                $patch = 0
            }
            "minor"
            {
                $minor++
                $patch = 0
            }
            default
            {
                $patch++
            }
        }

        $newVersion = "$major.$minor.$patch"
    }
    else
    {
        Write-Error "Could not find Version in Directory.Build.props"
        exit 1
    }
}
else
{
    Write-Error "Directory.Build.props not found"
    exit 1
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Current version: $currentVersion" -ForegroundColor Yellow
Write-Host "  Will publish:    $newVersion" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Format version - using semantic version only (no git hash)
$fullVersion = "$newVersion"

# Update the version in Directory.Build.props
$propsContent = $propsContent -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$newVersion</Version>"
$propsContent | Out-File -FilePath $propsFilePath -NoNewline

# Check for NuGet API key in environment
$apiKey = $env:NUGET_API_KEY
if ($apiKey) 
{
    Write-Host "NUGET_API_KEY environment variable found. Will attempt to publish packages."
    $willPublish = $true
}
else 
{
    Write-Host "NUGET_API_KEY environment variable not found. Packages will be built but not published."
    $willPublish = $false
}

# Projects to build
$projects = @(
    "Provider\Ai.Tlbx.VoiceAssistant\Ai.Tlbx.VoiceAssistant.csproj",
    "Provider\Ai.Tlbx.VoiceAssistant.Provider.OpenAi\Ai.Tlbx.VoiceAssistant.Provider.OpenAi.csproj",
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
    
    Write-Host "Building $projectName in $Configuration configuration..."
    
    # Clean and build the project
    dotnet clean $projectPath -c $Configuration

    # Make sure the project is restored first
    dotnet restore $projectPath
    
    # Build and pack the project
    dotnet build $projectPath -c $Configuration
    
    Write-Host "Packing $projectName..."
    dotnet pack $projectPath -c $Configuration --no-build
    
    # Find the generated package - check both nupkg directory and project's bin/Release
    $packagePattern1 = Join-Path $nupkgDir "$projectName.*.nupkg"
    $projectDir = Split-Path $projectPath -Parent
    $packagePattern2 = Join-Path $projectDir "bin\$Configuration\$projectName.*.nupkg"
    
    $packageFiles = @()
    if (Test-Path $packagePattern1) {
        $packageFiles += Get-ChildItem -Path $packagePattern1
    }
    if (Test-Path $packagePattern2) {
        $packageFiles += Get-ChildItem -Path $packagePattern2
    }
    
    $packageFiles = $packageFiles | Sort-Object LastWriteTime -Descending
    
    if ($packageFiles.Count -eq 0) 
    {
        Write-Error "No package found for $projectName"
        $allPackagesSuccessful = $false
        continue
    }
    
    $package = $packageFiles[0]
    Write-Host "Package created: $($package.Name)"
    
    # If package is not in nupkg directory, move it there
    if ($package.DirectoryName -ne $nupkgDir) 
    {
        $destination = Join-Path $nupkgDir $package.Name
        Move-Item -Path $package.FullName -Destination $destination -Force
        $package = Get-Item $destination
        Write-Host "Moved package to: $destination"
    }
    
    # Publish the package if API key is available
    if ($willPublish) 
    {
        Write-Host "Publishing $($package.Name) to NuGet.org..."
        try 
        {
            # Add --skip-duplicate to avoid errors when re-publishing the same version
            # Removing the default behavior of unlisted packages by NOT using --no-service-endpoint
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

# Commit the version change
Write-Host "Committing Directory.Build.props version change..." -ForegroundColor Yellow
git add $propsFilePath
git commit -m "Increment version to $newVersion"
git push

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  PUBLISH SUMMARY" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Published version: $newVersion" -ForegroundColor Green
Write-Host "  Packages location: $nupkgDir" -ForegroundColor Yellow
Write-Host ""

if ($willPublish)
{
    if ($publishedPackages.Count -gt 0)
    {
        Write-Host "  Successfully published $($publishedPackages.Count) packages:" -ForegroundColor Green
        foreach ($pkgName in $publishedPackages)
        {
            Write-Host "    - $pkgName" -ForegroundColor Green
        }
    }

    Write-Host ""
    if (-not $allPackagesSuccessful)
    {
        Write-Host "  Some packages were not published successfully. See errors above." -ForegroundColor Red
        Write-Host "========================================" -ForegroundColor Cyan
        exit 1
    }
    else
    {
        Write-Host "  All packages published successfully to NuGet.org!" -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Cyan
    }
}
else
{
    Write-Host "  Packages built but not published (no API key)" -ForegroundColor Yellow
    Write-Host "========================================" -ForegroundColor Cyan
} 
