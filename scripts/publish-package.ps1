#!/usr/bin/env pwsh
#Requires -Version 7.0
# Cross-platform script to publish the ApiPipeline.NET packages to GitHub Packages
# Works on Windows, macOS and Linux with PowerShell 7 (pwsh). Windows PowerShell 5.1 is not supported:
# the script uses ?? and three-argument Join-Path, and the #Requires line above refuses 5.1 up front.
# Usage: pwsh scripts/publish-package.ps1 [VERSION] [GITHUB_PAT] [-AllowUnsigned]
#        or: ./scripts/publish-package.ps1 [VERSION] [GITHUB_PAT] [-AllowUnsigned] (if executable)
#
# Features:
# - Publishes ApiPipeline.NET, ApiPipeline.NET.RateLimiting.Redis and ApiPipeline.NET.Versioning, all at the
#   committed <Version> in src/Shared/Shared.props; a VERSION argument only checks it
# - Refuses to run on a working tree with uncommitted or untracked changes, so the packages are built from
#   exactly one commit, and tags that commit
# - Finds a Microsoft .NET SDK for the version in global.json by itself. Source-built SDKs (Homebrew, some
#   Linux distributions) ship IIS and HTTP.sys as empty stubs and cannot build the core package
# - Runs the release workflow's gates and stops before packing if any fails: locked restore, release version,
#   API-compatibility baseline, source mapping, doc links, vulnerable packages, Native AOT smoke, then the
#   full test suite in Release (Docker required, as in CI)
# - Signs and verifies the packages when a certificate is configured; refuses to start without one unless
#   -AllowUnsigned is passed
# - Automatically deletes and republishes if a package version already exists
# - Creates a git tag and GitHub release (body from CHANGELOG.md) after a successful publish
# - Requires GitHub PAT with 'write:packages', 'delete:packages', and 'public_repo' (or 'repo') permissions

param(
    [string]$Version = "",
    [string]$GitHubPAT = $env:GITHUB_PAT,
    # Publishes without a signature. Consumers that require signed packages will refuse the result.
    [switch]$AllowUnsigned
)

$ErrorActionPreference = "Stop"

# Repository root. Git is required: the packages are built from one commit, and that commit is tagged.
$RepoRoot = (git -C ($PSScriptRoot ? $PSScriptRoot : (Get-Location).Path) rev-parse --show-toplevel 2>$null)
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepoRoot)) {
    Write-Host "Error: Not inside a git repository. Publish from a clone of apipipeline-net." -ForegroundColor Red
    exit 1
}
$RepoRoot = $RepoRoot.Trim()

# Untracked files count: every package compiles every .cs file under its folder, so an untracked one ships too.
function Get-WorkingTreeChanges {
    # stderr is not captured: a git warning (line endings, safe.directory hints) is not a change.
    $changes = git -C $RepoRoot status --porcelain --untracked-files=all 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: git status failed in $RepoRoot" -ForegroundColor Red
        exit 1
    }
    return $changes
}

function Get-HeadCommit {
    $sha = git -C $RepoRoot rev-parse --verify HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha)) {
        Write-Host "Error: Could not resolve HEAD; the repository has no commit to publish." -ForegroundColor Red
        exit 1
    }
    return $sha.Trim()
}

$pendingChanges = Get-WorkingTreeChanges
if ($pendingChanges) {
    Write-Host "Error: The working tree has uncommitted or untracked changes. Packages must be built from a commit," -ForegroundColor Red
    Write-Host "       so the tag points at exactly what shipped. Commit or stash these first:" -ForegroundColor Red
    $pendingChanges | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 1
}
$CommitSha = Get-HeadCommit

# Configuration (using Join-Path for cross-platform compatibility)
$SolutionPath = Join-Path $RepoRoot "ApiPipeline.NET.slnx"
$VersionPropsPath = Join-Path $RepoRoot "src" "Shared" "Shared.props"
$PackageOutput = Join-Path $RepoRoot "nupkgs"
$Namespace = $env:GITHUB_NAMESPACE ?? "baps-apps"
$RepoName = "apipipeline-net"

# Packages to publish, core first: the Redis package depends on it.
$Packages = @("ApiPipeline.NET", "ApiPipeline.NET.RateLimiting.Redis", "ApiPipeline.NET.Versioning")

# The version is the committed <Version> in Shared.props; this script never edits it. A version passed as an
# argument is only a check that the commit is the release you meant.
if (-not (Test-Path $VersionPropsPath)) {
    Write-Host "Error: Version file not found at $VersionPropsPath" -ForegroundColor Red
    exit 1
}

$committedVersion = $null
$versionMatch = Select-String -Path $VersionPropsPath -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
if ($versionMatch -and $versionMatch.Matches.Groups.Count -gt 1) {
    $committedVersion = $versionMatch.Matches.Groups[1].Value.Trim()
}

if ([string]::IsNullOrEmpty($committedVersion)) {
    Write-Host "Error: Could not extract <Version> from $VersionPropsPath" -ForegroundColor Red
    exit 1
}

if ([string]::IsNullOrEmpty($Version)) {
    $Version = $committedVersion
    Write-Host "Publishing the committed version: $Version" -ForegroundColor Yellow
} elseif ($Version.TrimStart('v') -ne $committedVersion) {
    Write-Host "Error: Version $Version was requested, but the committed Shared.props says $committedVersion." -ForegroundColor Red
    Write-Host "       Set <Version>$($Version.TrimStart('v'))</Version> in src/Shared/Shared.props, commit it, then run this script." -ForegroundColor Yellow
    exit 1
} else {
    $Version = $committedVersion
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

# Signing is decided before anything runs, so a missing certificate does not surface after minutes of gates.
#   NUGET_SIGN_CERT_PATH (+ NUGET_SIGN_CERT_PASSWORD)  a .pfx file, or
#   NUGET_SIGN_CERT_FINGERPRINT                         the SHA-256 fingerprint of a certificate in the user's store
#   NUGET_SIGN_TIMESTAMPER                              RFC 3161 timestamp server (default below)
$signCertPath = $env:NUGET_SIGN_CERT_PATH
$signCertFingerprint = $env:NUGET_SIGN_CERT_FINGERPRINT
$timestamper = $env:NUGET_SIGN_TIMESTAMPER ?? "http://timestamp.digicert.com"
$signingConfigured = -not [string]::IsNullOrEmpty($signCertPath) -or -not [string]::IsNullOrEmpty($signCertFingerprint)

if (-not $signingConfigured -and -not $AllowUnsigned) {
    Write-Host "Error: No signing certificate configured; nothing was run." -ForegroundColor Red
    Write-Host "       Set NUGET_SIGN_CERT_PATH or NUGET_SIGN_CERT_FINGERPRINT," -ForegroundColor Yellow
    Write-Host "       or pass -AllowUnsigned to publish unsigned packages." -ForegroundColor Yellow
    exit 1
}

# Tools the gates need. Python runs the repository's check scripts; -I keeps the current directory and user
# site-packages off the path. bash runs the Native AOT smoke, Docker the Redis tests.
$Python = Get-Command python3 -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $Python) {
    $Python = Get-Command python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
}
$missingTools = @()
if (-not $Python) { $missingTools += "Python 3" }
foreach ($tool in "bash", "docker") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { $missingTools += $tool }
}
if ($missingTools) {
    Write-Host "Error: The release gates need $($missingTools -join ', '); nothing was run." -ForegroundColor Red
    exit 1
}
docker info *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Docker is not running; the Redis tests and the AOT smoke need it. Nothing was run." -ForegroundColor Red
    exit 1
}

# The SDK: the first Microsoft build of the global.json version found in the usual install folders. A
# source-built SDK's ASP.NET Core reference pack ships IIS and HTTP.sys as empty stubs, so the core package
# fails with CS0246 (IISServerOptions); its IIS reference assembly lacks that type name.
$PinnedSdk = (Get-Content (Join-Path $RepoRoot "global.json") -Raw | ConvertFrom-Json).sdk.version
$DotnetExe = $IsWindows ? "dotnet.exe" : "dotnet"

function Get-SdkProblem {
    param([string]$Root)

    $exe = Join-Path $Root $DotnetExe
    if (-not (Test-Path $exe -PathType Leaf)) {
        return "no $DotnetExe"
    }

    $sdkLine = $null
    Push-Location $RepoRoot
    try {
        # Output collected before filtering: Select-Object -First stops the pipeline and sets $LASTEXITCODE.
        $versionOutput = @(& $exe --version 2>$null)
        if ($LASTEXITCODE -eq 0 -and $versionOutput) {
            $sdkVersion = $versionOutput[0].Trim()
            $sdkLine = @(& $exe --list-sdks 2>$null) | Where-Object { $_ -like "$sdkVersion *" } | Select-Object -First 1
        }
    } catch {
        # Not executable; reported below.
    } finally {
        Pop-Location
    }
    if (-not $sdkLine) {
        return "no SDK matching global.json ($PinnedSdk or a later patch)"
    }

    $sdkRoot = Split-Path -Parent ($sdkLine -replace '^\S+\s+\[(.+)\]$', '$1')
    $refPack = Get-ChildItem (Join-Path $sdkRoot "packs" "Microsoft.AspNetCore.App.Ref") -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '-.*$', '') } -Descending | Select-Object -First 1
    $iisRef = if ($refPack) { Join-Path $refPack.FullName "ref" "net10.0" "Microsoft.AspNetCore.Server.IIS.dll" }
    if (-not $iisRef -or -not (Test-Path $iisRef) -or -not (Select-String -Path $iisRef -Pattern "IISServerOptions" -SimpleMatch -Quiet)) {
        return "SDK $sdkVersion is source-built (no IIS or HTTP.sys)"
    }
    return $null
}

$candidateRoots = @(
    $env:DOTNET_ROOT
    (Join-Path $HOME ".dotnet-$PinnedSdk")
    (Join-Path $HOME ".dotnet")
    $IsWindows ? (Join-Path ($env:ProgramFiles ?? "C:\Program Files") "dotnet") : $null
    $IsMacOS ? "/usr/local/share/dotnet" : $null
    $IsLinux ? "/usr/share/dotnet" : $null
    $IsLinux ? "/usr/lib/dotnet" : $null
    # Last: whatever `dotnet` on PATH resolves to, symlinks followed.
    (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 |
        ForEach-Object { Split-Path -Parent ((Get-Item $_.Source).ResolvedTarget ?? $_.Source) })
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

$DotnetRoot = $null
$rejected = @()
foreach ($root in $candidateRoots) {
    $problem = Get-SdkProblem $root
    if (-not $problem) {
        $DotnetRoot = $root
        break
    }
    $rejected += "  $root : $problem"
}

if (-not $DotnetRoot) {
    Write-Host "Error: No Microsoft .NET SDK $PinnedSdk found; nothing was run. Checked:" -ForegroundColor Red
    $rejected | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
    Write-Host "Install it where this script looks (CONTRIBUTING.md, Prerequisites):" -ForegroundColor Yellow
    Write-Host "  curl -fsSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh" -ForegroundColor Yellow
    Write-Host "  bash dotnet-install.sh --version $PinnedSdk --install-dir `"`$HOME/.dotnet-$PinnedSdk`" --no-path" -ForegroundColor Yellow
    exit 1
}

# Every dotnet call below, and those of the gate scripts, use this SDK.
$env:DOTNET_ROOT = $DotnetRoot
$env:PATH = "$DotnetRoot$([IO.Path]::PathSeparator)$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
# As in CI: a reproducible build (ContinuousIntegrationBuild, source paths mapped to /_/), and the Redis tests
# and AOT smoke fail without Docker instead of skipping.
$env:CI = "true"

Write-Host "Publishing ApiPipeline.NET packages v$Version from commit $CommitSha to GitHub Packages" -ForegroundColor Green
Write-Host "  Packages: $($Packages -join ', ')"
Write-Host "  SDK: $(dotnet --version) ($DotnetRoot)"
Write-Host ""

# Step 1: Verify credentials
Write-Host "Step 1: Verifying credentials..." -ForegroundColor Yellow
$sourceUrl = "https://nuget.pkg.github.com/$Namespace/index.json"
Write-Host "Target feed: $sourceUrl" -ForegroundColor Green
Write-Host ""

# Step 2: Release gates, as in .github/workflows/release.yml and ci.yml. Each one stops the script on failure.
Write-Host "Step 2: Running release gates..." -ForegroundColor Yellow

function Invoke-ReleaseGate {
    param(
        [string]$Name,
        [scriptblock]$Command
    )
    Write-Host "  Gate: $Name" -ForegroundColor Yellow
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Error: Release gate failed: $Name; nothing was packed or published" -ForegroundColor Red
        exit 1
    }
}

Push-Location $RepoRoot
try {
    Invoke-ReleaseGate "release version" { & $Python.Source -I (Join-Path "scripts" "check-release-version.py") $Version }
    Invoke-ReleaseGate "API-compatibility baseline" { & $Python.Source -I (Join-Path "scripts" "check-api-baseline.py") }
    Invoke-ReleaseGate "package source mapping" { & $Python.Source -I (Join-Path "scripts" "check-source-mapping.py") }
    Invoke-ReleaseGate "doc links" { & $Python.Source -I (Join-Path "scripts" "check-doc-links.py") }
    Invoke-ReleaseGate "locked restore" { dotnet restore $SolutionPath --locked-mode }

    # Exits 0 whether or not it finds anything, so the report is read instead.
    Write-Host "  Gate: vulnerable packages" -ForegroundColor Yellow
    $vulnerableReport = dotnet list $SolutionPath package --vulnerable --include-transitive 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $vulnerableReport -match "has the following vulnerable packages") {
        Write-Host $vulnerableReport
        Write-Host "Error: Release gate failed: vulnerable packages; nothing was packed or published" -ForegroundColor Red
        exit 1
    }

    # Publishes the AOT smoke app (any trimming or AOT warning fails) and runs the native binary.
    Invoke-ReleaseGate "Native AOT smoke" { bash (Join-Path "scripts" "aot-smoke.sh") (Join-Path $RepoRoot "artifacts" "aot-smoke") }
} finally {
    Pop-Location
}
Write-Host "Release gates passed" -ForegroundColor Green
Write-Host ""

# Step 3: Build and test. Release is the configuration that ships; CI=true makes the Redis tests fail
# without Docker instead of skipping.
Write-Host "Step 3: Building and testing (Release)..." -ForegroundColor Yellow
dotnet build $SolutionPath --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Build failed; nothing was packed or published" -ForegroundColor Red
    exit 1
}
dotnet test --solution $SolutionPath --configuration Release --no-build
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Tests failed; nothing was packed or published" -ForegroundColor Red
    exit 1
}
Write-Host "Build and tests passed" -ForegroundColor Green
Write-Host ""

# Step 4: Pack every packable project, as the release workflow does.
Write-Host "Step 4: Creating NuGet packages..." -ForegroundColor Yellow
if (-not (Test-Path $PackageOutput)) {
    New-Item -ItemType Directory -Path $PackageOutput | Out-Null
}
foreach ($name in $Packages) {
    Remove-Item (Join-Path $PackageOutput "$name.$Version.*nupkg") -ErrorAction SilentlyContinue
}
dotnet pack $SolutionPath --configuration Release --no-build --output $PackageOutput
if ($LASTEXITCODE -ne 0) {
    Write-Host "Error: Package creation failed" -ForegroundColor Red
    exit 1
}

# CodeStyle.NET is a private build-time analyzer; as a package dependency it would break consumers' restore.
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($name in $Packages) {
    $PackageFile = Join-Path $PackageOutput "$name.$Version.nupkg"
    if (-not (Test-Path $PackageFile)) {
        Write-Host "Error: Package file not found: $PackageFile" -ForegroundColor Red
        $existingPackages = Get-ChildItem -Path $PackageOutput -Filter "*.nupkg" -ErrorAction SilentlyContinue
        if ($existingPackages) {
            Write-Host "Found package files:" -ForegroundColor Yellow
            $existingPackages | ForEach-Object { Write-Host "  - $($_.Name)" -ForegroundColor Yellow }
        }
        exit 1
    }

    $zip = [System.IO.Compression.ZipFile]::OpenRead($PackageFile)
    try {
        $nuspecEntry = $zip.Entries | Where-Object { $_.FullName -like "*.nuspec" } | Select-Object -First 1
        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try { $nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally {
        $zip.Dispose()
    }
    if ($nuspec -match 'id="CodeStyle.NET"') {
        Write-Host "Error: $name.$Version.nupkg declares CodeStyle.NET as a dependency; nothing was published" -ForegroundColor Red
        exit 1
    }
}
Write-Host "Packages created" -ForegroundColor Green
Write-Host ""

# The gates, tests and pack took minutes. Nothing may have changed the tree or moved HEAD meanwhile, or the
# packages would no longer match the commit about to be tagged.
$changesAfterPack = Get-WorkingTreeChanges
if ($changesAfterPack) {
    Write-Host "Error: The working tree changed while testing and packing; nothing was published:" -ForegroundColor Red
    $changesAfterPack | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    exit 1
}
$headAfterPack = Get-HeadCommit
if ($headAfterPack -ne $CommitSha) {
    Write-Host "Error: HEAD moved from $CommitSha to $headAfterPack while testing and packing; nothing was published." -ForegroundColor Red
    exit 1
}

# Step 5: Sign and verify. Without a certificate the script stopped at the start unless -AllowUnsigned was passed.
if ($signingConfigured) {
    Write-Host "Step 5: Signing packages..." -ForegroundColor Yellow
    foreach ($name in $Packages) {
        $PackageFile = Join-Path $PackageOutput "$name.$Version.nupkg"
        $signArgs = @("nuget", "sign", $PackageFile, "--timestamper", $timestamper, "--overwrite")
        if (-not [string]::IsNullOrEmpty($signCertPath)) {
            $signArgs += @("--certificate-path", $signCertPath)
            if (-not [string]::IsNullOrEmpty($env:NUGET_SIGN_CERT_PASSWORD)) {
                $signArgs += @("--certificate-password", $env:NUGET_SIGN_CERT_PASSWORD)
            }
        } else {
            $signArgs += @("--certificate-fingerprint", $signCertFingerprint)
        }

        & dotnet @signArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Error: Signing failed for $name; nothing was published" -ForegroundColor Red
            exit 1
        }

        dotnet nuget verify $PackageFile --all
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Error: Signature verification failed for $name; nothing was published" -ForegroundColor Red
            exit 1
        }
    }
    Write-Host "Packages signed and verified" -ForegroundColor Green
} else {
    Write-Host "Step 5: Signing skipped (-AllowUnsigned): the packages are UNSIGNED." -ForegroundColor Yellow
    Write-Host "  Consumers with signatureValidationMode=require will refuse them." -ForegroundColor Yellow
}
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

# Step 6: Publish all packages
Write-Host "Step 6: Publishing packages to GitHub Packages..." -ForegroundColor Yellow

foreach ($name in $Packages) {
    Write-Host ""
    Write-Host "  Publishing $name..." -ForegroundColor Cyan

    Publish-NuGetPackage `
        -PackageName $name `
        -PackageVersion $Version `
        -PackageFile (Join-Path $PackageOutput "$name.$Version.nupkg") `
        -Token $GitHubPAT `
        -OrgName $Namespace `
        -SourceUrl $sourceUrl

    Write-Host "  $name published successfully" -ForegroundColor Green
}

Write-Host ""
Write-Host "All packages published successfully!" -ForegroundColor Green
Write-Host ""

# Step 7: Create git tag and GitHub release
Write-Host "Step 7: Creating git tag and GitHub release..." -ForegroundColor Yellow
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
$tagExists = git -C $RepoRoot tag -l $tagName 2>&1
if (-not [string]::IsNullOrWhiteSpace($tagExists)) {
    Write-Host "  Tag $tagName already exists, deleting to recreate..." -ForegroundColor Yellow
    git -C $RepoRoot tag -d $tagName | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Local tag $tagName deleted" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not delete local tag $tagName" -ForegroundColor Yellow
    }
    git -C $RepoRoot push origin ":refs/tags/$tagName" 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Remote tag $tagName deleted" -ForegroundColor Green
    } else {
        Write-Host "  Warning: Could not delete remote tag $tagName (may not exist on origin)" -ForegroundColor Yellow
    }
}

# The commit the packages were built from, not whatever HEAD is now.
git -C $RepoRoot tag $tagName $CommitSha
if ($LASTEXITCODE -eq 0) {
    git -C $RepoRoot push origin $tagName
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

    # Delete ALL existing releases for this tag, including drafts, then recreate.
    # Deleting the remote tag above converts its published release into an orphan
    # draft, and the by-tag endpoint (releases/tags/{tag}) never returns drafts —
    # so list all releases and filter by tag_name instead.
    try {
        $allReleases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Namespace/$RepoName/releases?per_page=100" `
            -Method Get -Headers $headers -ErrorAction Stop
        $staleReleases = @($allReleases | Where-Object { $_.tag_name -eq $tagName })
        foreach ($stale in $staleReleases) {
            $kind = if ($stale.draft) { "draft" } else { "release" }
            Write-Host "  Existing $kind for $tagName found (id $($stale.id)), deleting to recreate..." -ForegroundColor Yellow
            try {
                Invoke-RestMethod -Uri "https://api.github.com/repos/$Namespace/$RepoName/releases/$($stale.id)" `
                    -Method Delete -Headers $headers -ErrorAction Stop | Out-Null
                Write-Host "  Existing $kind deleted" -ForegroundColor Green
            } catch {
                Write-Host "  Warning: Could not delete existing ${kind}: $_" -ForegroundColor Yellow
            }
        }
    } catch {
        Write-Host "  Warning: Could not list existing releases: $_" -ForegroundColor Yellow
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
