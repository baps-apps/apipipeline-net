#!/usr/bin/env pwsh
# Cross-platform script to publish ApiPipeline.NET packages to GitHub Packages
# Works on both Windows and macOS/Linux
# Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]
#        or: powershell scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]
#        or: ./scripts/publish-package.ps1 [VERSION] [GITHUB_PAT] (if executable)
#
# Features:
# - Publishes ApiPipeline.NET, ApiPipeline.NET.OpenTelemetry, and ApiPipeline.NET.Versioning
# - Automatically deletes and republishes if package version already exists
# - Creates a git tag and GitHub release (body sourced from CHANGELOG.md) after successful publish
# - Requires GitHub PAT with 'write:packages', 'delete:packages', and 'public_repo' (or 'repo') permissions

param(
    [string]$Version = "",
    [string]$GitHubPAT = $env:GITHUB_PAT
)

$ErrorActionPreference = "Stop"

# Get repository root directory (cross-platform)
try {
    $RepoRoot = (git rev-parse --show-toplevel 2>&1)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepoRoot)) {
        throw "Git command failed"
    }
} catch {
    if ($PSScriptRoot) {
        $RepoRoot = Split-Path -Parent $PSScriptRoot
    } else {
        $currentDir = (Get-Location).Path
        if ((Split-Path -Leaf $currentDir) -eq "scripts") {
            $RepoRoot = Split-Path -Parent $currentDir
        } else {
            $RepoRoot = $currentDir
        }
    }
}

# Configuration
$PackageOutput = Join-Path $RepoRoot "nupkgs"
$Namespace = $env:GITHUB_NAMESPACE ?? "baps-apps"
$RepoName = "apipipeline-net"

# Packages to publish (in build order — core first, then dependents)
$Packages = @(
    @{
        Name        = "ApiPipeline.NET"
        ProjectPath = Join-Path $RepoRoot "src" "ApiPipeline.NET" "ApiPipeline.NET.csproj"
    },
    @{
        Name        = "ApiPipeline.NET.OpenTelemetry"
        ProjectPath = Join-Path $RepoRoot "src" "ApiPipeline.NET.OpenTelemetry" "ApiPipeline.NET.OpenTelemetry.csproj"
    },
    @{
        Name        = "ApiPipeline.NET.Versioning"
        ProjectPath = Join-Path $RepoRoot "src" "ApiPipeline.NET.Versioning" "ApiPipeline.NET.Versioning.csproj"
    }
)

$CoreProjectPath = $Packages[0].ProjectPath

# Track if version was provided as parameter
$versionProvidedAsParam = $PSBoundParameters.ContainsKey('Version') -and -not [string]::IsNullOrEmpty($PSBoundParameters['Version'])

# Get version from argument or core .csproj file
if ([string]::IsNullOrEmpty($Version)) {
    if (-not (Test-Path $CoreProjectPath)) {
        Write-Host "Error: Project file not found at $CoreProjectPath" -ForegroundColor Red
        exit 1
    }

    $versionMatch = Select-String -Path $CoreProjectPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
    if ($versionMatch -and $versionMatch.Matches.Groups.Count -gt 1) {
        $Version = $versionMatch.Matches.Groups[1].Value.Trim()
    }

    if ([string]::IsNullOrEmpty($Version)) {
        Write-Host "Error: Could not extract version from .csproj file" -ForegroundColor Red
        Write-Host "Add <Version>1.0.0</Version> to ApiPipeline.NET.csproj or pass version: pwsh scripts/publish-package.ps1 1.0.0" -ForegroundColor Yellow
        exit 1
    }
    Write-Host "No version specified, using version from .csproj: $Version" -ForegroundColor Yellow
}

# Get GitHub PAT from argument or environment variable
if ([string]::IsNullOrEmpty($GitHubPAT)) {
    Write-Host "Error: GitHub Personal Access Token required" -ForegroundColor Red
    Write-Host "Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT]"
    Write-Host "Or set GITHUB_PAT environment variable"
    Write-Host ""
    Write-Host "Note: Your PAT needs 'write:packages' and 'delete:packages' to publish/overwrite packages,"
    Write-Host "      and 'repo' or 'public_repo' to create GitHub releases."
    exit 1
}

Write-Host "Publishing ApiPipeline.NET packages v$Version to GitHub Packages" -ForegroundColor Green
Write-Host "  Packages: $($Packages | ForEach-Object { $_.Name } | Join-String -Separator ', ')"
Write-Host ""

# Step 1: Verify credentials
Write-Host "Step 1: Verifying credentials..." -ForegroundColor Yellow
$sourceUrl = "https://nuget.pkg.github.com/$Namespace/index.json"
Write-Host "Target feed: $sourceUrl" -ForegroundColor Green
Write-Host ""

# Step 2: Update version in all .csproj files if version was provided as parameter
if ($versionProvidedAsParam) {
    Write-Host "Step 2: Updating version in project files..." -ForegroundColor Yellow
    foreach ($pkg in $Packages) {
        try {
            $csprojContent = Get-Content $pkg.ProjectPath -Raw
            $originalContent = $csprojContent

            if ($csprojContent -match '<Version>([^<]+)</Version>') {
                $csprojContent = $csprojContent -replace '<Version>([^<]+)</Version>', "<Version>$Version</Version>"
            } else {
                $csprojContent = $csprojContent -replace '(<PropertyGroup>\s*)', "`$1`n    <Version>$Version</Version>`n"
            }

            if ($csprojContent -ne $originalContent) {
                Set-Content -Path $pkg.ProjectPath -Value $csprojContent -NoNewline
                Write-Host "  $($pkg.Name): version updated to $Version" -ForegroundColor Green
            }
        } catch {
            Write-Host "  Warning: Could not update version in $($pkg.Name).csproj: $_" -ForegroundColor Yellow
        }
    }
    Write-Host ""
}

# Step 3: Build all packages
Write-Host "Step 3: Building projects..." -ForegroundColor Yellow
foreach ($pkg in $Packages) {
    Write-Host "  Building $($pkg.Name)..." -ForegroundColor Cyan
    dotnet build $pkg.ProjectPath --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Build failed for $($pkg.Name)" -ForegroundColor Red
        exit 1
    }
}
Write-Host "Build completed" -ForegroundColor Green
Write-Host ""

# Step 4: Pack all packages
Write-Host "Step 4: Creating NuGet packages..." -ForegroundColor Yellow
if (-not (Test-Path $PackageOutput)) {
    New-Item -ItemType Directory -Path $PackageOutput | Out-Null
}
foreach ($pkg in $Packages) {
    Write-Host "  Packing $($pkg.Name)..." -ForegroundColor Cyan
    dotnet pack $pkg.ProjectPath --configuration Release --no-build --output $PackageOutput
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Package creation failed for $($pkg.Name)" -ForegroundColor Red
        exit 1
    }
}
Write-Host "Packages created" -ForegroundColor Green
Write-Host ""

# Helper functions
function Test-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName
    )
    try {
        $headers = @{
            "Authorization"        = "Bearer $Token"
            "Accept"               = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $versionsUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions"
        $versionsResponse = Invoke-RestMethod -Uri $versionsUrl -Method Get -Headers $headers -ErrorAction Stop
        $existingVersion = $versionsResponse | Where-Object { $_.name -eq $PackageVersion } | Select-Object -First 1
        if ($existingVersion) {
            return @{ Exists = $true; VersionId = $existingVersion.id }
        } else {
            return @{ Exists = $false; VersionId = $null }
        }
    } catch {
        return @{ Exists = $null; VersionId = $null }
    }
}

function Remove-PackageVersion {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$Token,
        [string]$OrgName,
        [string]$VersionId
    )
    try {
        $headers = @{
            "Authorization"        = "Bearer $Token"
            "Accept"               = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
        }
        $deleteUrl = "https://api.github.com/orgs/$OrgName/packages/nuget/$PackageName/versions/$VersionId"
        Invoke-RestMethod -Uri $deleteUrl -Method Delete -Headers $headers -ErrorAction Stop
        Write-Host "    Package version deleted successfully" -ForegroundColor Green
        return $true
    } catch {
        Write-Host "    Could not delete package via API: $_" -ForegroundColor Yellow
        return $false
    }
}

function Publish-NuGetPackage {
    param(
        [string]$PackageName,
        [string]$PackageVersion,
        [string]$PackageFile,
        [string]$Token,
        [string]$OrgName,
        [string]$SourceUrl
    )

    $versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $PackageVersion -Token $Token -OrgName $OrgName

    if ($versionCheck.Exists -eq $true) {
        Write-Host "    Version $PackageVersion already exists, deleting to override..." -ForegroundColor Yellow
        $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $PackageVersion -Token $Token -OrgName $OrgName -VersionId $versionCheck.VersionId
        if (-not $deleted) {
            Write-Host "Error: Could not delete existing package version for $PackageName" -ForegroundColor Red
            Write-Host "  Go to https://github.com/$OrgName/packages and delete version $PackageVersion manually, then re-run." -ForegroundColor Yellow
            exit 1
        }
        Start-Sleep -Seconds 2
        Write-Host "    Publishing new version..." -ForegroundColor Yellow
    } elseif ($versionCheck.Exists -eq $false) {
        Write-Host "    Version $PackageVersion does not exist, publishing..." -ForegroundColor Green
    } else {
        Write-Host "    Publishing package (will handle errors if version exists)..." -ForegroundColor Yellow
    }

    dotnet nuget push $PackageFile --api-key $Token --source $SourceUrl

    if ($LASTEXITCODE -ne 0) {
        $pushOutput = dotnet nuget push $PackageFile --api-key $Token --source $SourceUrl 2>&1 | Out-String
        $pushOutputLower = $pushOutput.ToLower()
        $packageExists = $pushOutputLower -match "already exists" -or $pushOutputLower -match "conflict" -or $pushOutputLower -match "409" -or $pushOutputLower -match "package.*exist"

        if ($packageExists) {
            Write-Host "    Package version still exists, attempting delete and republish..." -ForegroundColor Yellow
            $versionCheck = Test-PackageVersion -PackageName $PackageName -PackageVersion $PackageVersion -Token $Token -OrgName $OrgName
            if ($versionCheck.Exists -eq $true) {
                $deleted = Remove-PackageVersion -PackageName $PackageName -PackageVersion $PackageVersion -Token $Token -OrgName $OrgName -VersionId $versionCheck.VersionId
                if ($deleted) {
                    Start-Sleep -Seconds 2
                    dotnet nuget push $PackageFile --api-key $Token --source $SourceUrl
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host "Error: Package publish failed after deletion for $PackageName" -ForegroundColor Red
                        exit 1
                    }
                } else {
                    Write-Host "Error: Could not delete existing package version for $PackageName" -ForegroundColor Red
                    exit 1
                }
            } else {
                Write-Host "Error: Package publish failed for $PackageName" -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "Error: Package publish failed for $PackageName" -ForegroundColor Red
            Write-Host "Error details: $pushOutput" -ForegroundColor Red
            exit 1
        }
    }
}

# Step 5: Publish all packages
Write-Host "Step 5: Publishing packages to GitHub Packages..." -ForegroundColor Yellow

foreach ($pkg in $Packages) {
    Write-Host ""
    Write-Host "  Publishing $($pkg.Name)..." -ForegroundColor Cyan

    $PackageFile = Join-Path $PackageOutput "$($pkg.Name).$Version.nupkg"
    if (-not (Test-Path $PackageFile)) {
        Write-Host "Error: Package file not found: $PackageFile" -ForegroundColor Red
        $existingPackages = Get-ChildItem -Path $PackageOutput -Filter "*.nupkg" -ErrorAction SilentlyContinue
        if ($existingPackages) {
            Write-Host "Found package files:" -ForegroundColor Yellow
            $existingPackages | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Yellow }
        }
        exit 1
    }

    Publish-NuGetPackage `
        -PackageName $pkg.Name `
        -PackageVersion $Version `
        -PackageFile $PackageFile `
        -Token $GitHubPAT `
        -OrgName $Namespace `
        -SourceUrl $sourceUrl

    Write-Host "  $($pkg.Name) published successfully" -ForegroundColor Green
}

Write-Host ""
Write-Host "All packages published successfully!" -ForegroundColor Green
Write-Host ""

# Step 6: Create git tag and GitHub release
Write-Host "Step 6: Creating git tag and GitHub release..." -ForegroundColor Yellow
$tagName = "v$Version"

function Get-ChangelogSection {
    param(
        [string]$Version,
        [string]$ChangelogPath
    )
    if (-not (Test-Path $ChangelogPath)) {
        return $null
    }

    # Escape regex metacharacters in version
    $escapedVersion = [Regex]::Escape($Version)
    # Match a top-level "## ..." heading containing the version (with or without v-prefix, optional [link]).
    # Stops at the next top-level "## " heading or EOF.
    $content = Get-Content $ChangelogPath -Raw
    $pattern = '(?ms)^##\s+\[?v?' + $escapedVersion + '\]?[^\r\n]*\r?\n(.*?)(?=^##\s|\z)'
    $match = [Regex]::Match($content, $pattern)
    if (-not $match.Success) {
        return $null
    }

    $section = $match.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($section)) {
        return $null
    }
    return $section
}

# Delete existing tag (local + remote) if present, then create and push fresh
$tagExists = git tag -l $tagName 2>&1
if (-not [string]::IsNullOrWhiteSpace($tagExists)) {
    Write-Host "  Tag $tagName already exists, deleting to recreate..." -ForegroundColor Yellow
    git tag -d $tagName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Local tag $tagName deleted" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not delete local tag $tagName" -ForegroundColor Yellow
    }
    git push origin ":refs/tags/$tagName" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Remote tag $tagName deleted" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not delete remote tag $tagName (may not exist on origin)" -ForegroundColor Yellow
    }
}

git tag $tagName
if ($LASTEXITCODE -eq 0) {
    git push origin $tagName
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Git tag $tagName pushed to origin" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not push tag $tagName to origin" -ForegroundColor Yellow
    }
} else {
    Write-Host "  Warning: Could not create git tag $tagName" -ForegroundColor Yellow
}

# Create GitHub release (requires 'repo' or 'public_repo' scope on the PAT)
try {
    $headers = @{
        "Authorization"        = "Bearer $GitHubPAT"
        "Accept"               = "application/vnd.github+json"
        "X-GitHub-Api-Version" = "2022-11-28"
    }

    # Delete existing release if present, then recreate
    $existingReleaseUrl = "https://api.github.com/repos/$Namespace/$RepoName/releases/tags/$tagName"
    try {
        $existingRelease = Invoke-RestMethod -Uri $existingReleaseUrl -Method Get -Headers $headers -ErrorAction Stop
        Write-Host "  GitHub release for $tagName already exists, deleting to recreate..." -ForegroundColor Yellow
        $deleteUrl = "https://api.github.com/repos/$Namespace/$RepoName/releases/$($existingRelease.id)"
        try {
            Invoke-RestMethod -Uri $deleteUrl -Method Delete -Headers $headers -ErrorAction Stop | Out-Null
            Write-Host "  Existing release deleted" -ForegroundColor Green
        } catch {
            Write-Host "  Warning: Could not delete existing release: $_" -ForegroundColor Yellow
        }
    } catch {
        # Release does not exist; nothing to delete
    }

    $changelogPath = Join-Path $RepoRoot "CHANGELOG.md"
    $changelogBody = Get-ChangelogSection -Version $Version -ChangelogPath $changelogPath
    if ($changelogBody) {
        Write-Host "  Using CHANGELOG.md section for $Version as release body" -ForegroundColor Green
        $releaseBody = $changelogBody
    } else {
        Write-Host "  No CHANGELOG.md section found for $Version, publishing release with empty body" -ForegroundColor Yellow
        $releaseBody = ""
    }

    $releasePayload = @{
        tag_name               = $tagName
        name                   = $tagName
        body                   = $releaseBody
        draft                  = $false
        prerelease             = $false
        generate_release_notes = $false
    } | ConvertTo-Json

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Namespace/$RepoName/releases" `
        -Method Post -Headers $headers -Body $releasePayload -ContentType "application/json" -ErrorAction Stop
    Write-Host "  GitHub release created: $($release.html_url)" -ForegroundColor Green
} catch {
    Write-Host "  Warning: Could not create GitHub release: $_" -ForegroundColor Yellow
    Write-Host "  Note: PAT needs 'repo' or 'public_repo' scope to create releases" -ForegroundColor Yellow
    Write-Host "  Create manually: https://github.com/$Namespace/$RepoName/releases/new?tag=$tagName" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "View your packages at: https://github.com/$Namespace/$RepoName/packages"
Write-Host "View your release at:  https://github.com/$Namespace/$RepoName/releases/tag/$tagName"
Write-Host ""
