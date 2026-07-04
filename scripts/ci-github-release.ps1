<#
.SYNOPSIS
    Build Velopack packages and publish them as GitHub Release assets.

.DESCRIPTION
    Called by CI on tag push. The generated GitHub Release contains the
    Velopack feed file, RELEASES file, nupkg packages, and portable zip.
#>

param(
    [string]$Channel          = 'win',
    [string]$PackId           = 'XIVLauncherCN',
    [string]$PackDir          = '.\bin\win-x64',
    [string]$OutputDir        = '.\Releases',
    [string]$MainExe          = 'XIVLauncherCN.exe',
    [string]$PackAuthors      = 'MilkVio',
    [string]$ReleaseNotesPath = '.\XIVLauncher\Resources\CHANGELOG.txt',
    [string]$IconPath         = '.\XIVLauncher\Resources\dalamud_icon.ico',
    [string]$SplashPath       = '.\XIVLauncher\Resources\pink_logo.png',
    [string]$Framework        = 'net10.0-x64-desktop'
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host ">>> $Message"
}

$refver = $env:GITHUB_REF -replace '.*/'
if ([string]::IsNullOrWhiteSpace($refver)) {
    throw 'GITHUB_REF is empty; cannot determine release version.'
}

Write-Step "Release version: $refver"

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    dotnet tool install -g vpk
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

Write-Step "Packing release $refver..."
$packArgs = @(
    '-u', $PackId,
    '-v', $refver,
    '-p', $PackDir,
    '-o', $OutputDir,
    '-e', $MainExe,
    '--channel', $Channel,
    '--packAuthors', $PackAuthors,
    '--releaseNotes', $ReleaseNotesPath,
    '--icon', $IconPath,
    '--splashImage', $SplashPath,
    '--framework', $Framework,
    '--noInst'
)
& vpk pack @packArgs
if ($LASTEXITCODE -ne 0) {
    throw "vpk pack failed with exit code $LASTEXITCODE"
}

$releaseJsonPath = Join-Path $OutputDir "releases.$Channel.json"
if (-not (Test-Path -LiteralPath $releaseJsonPath)) {
    throw "Velopack feed was not generated: $releaseJsonPath"
}

$localJson = Get-Content -LiteralPath $releaseJsonPath -Encoding utf8 | ConvertFrom-Json
$localAssets = @($localJson.Assets)

$releasesContent = ($localAssets | ForEach-Object { "$($_.SHA1) $($_.FileName) $($_.Size)" }) -join "`n"
$releasesPath = Join-Path $OutputDir 'RELEASES'
$releasesContent | Set-Content -LiteralPath $releasesPath -Encoding utf8NoBOM -NoNewline

$assets = @()
$assets += Get-ChildItem -LiteralPath $OutputDir -Filter '*.nupkg' -File
$assets += Get-ChildItem -LiteralPath $OutputDir -Filter '*-Portable.zip' -File
$assets += Get-Item -LiteralPath $releaseJsonPath
$assets += Get-Item -LiteralPath $releasesPath

$releaseNotes = Get-Content -LiteralPath $ReleaseNotesPath -Encoding utf8 -Raw

gh release view $refver --repo $env:GITHUB_REPOSITORY *> $null
$releaseExists = $LASTEXITCODE -eq 0

if ($releaseExists) {
    Write-Step "GitHub Release exists; uploading assets with --clobber."
    foreach ($asset in $assets) {
        gh release upload $refver $asset.FullName --repo $env:GITHUB_REPOSITORY --clobber
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to upload release asset: $($asset.Name)"
        }
    }
}
else {
    Write-Step "Creating GitHub Release..."
    $ghArgs = @(
        'release', 'create', $refver,
        '--repo', $env:GITHUB_REPOSITORY,
        '--title', "Release $refver",
        '--notes', $releaseNotes
    )

    foreach ($asset in $assets) {
        $ghArgs += $asset.FullName
    }

    gh @ghArgs
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub Release creation failed with exit code $LASTEXITCODE"
    }
}

Write-Step "GitHub Release assets published."
