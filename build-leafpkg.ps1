<#
.SYNOPSIS
  Package Mail into a .leafpkg. Build ONLY: does not install, enable, reload, or
  restart anything.

.DESCRIPTION
  Builds Leaf.Sdk into scratch (the SDK checkout stays read-only), publishes the
  backend against it, builds the web bundle (library-mode ESM), collects seeds
  (including seeds/skills), and zips the .leafpkg layout:

    plugin.json         manifest with a build { commit, date } stamp
    backend/            published assembly + plugin-private deps
    web/dist/           plugin.js, plugin.css, remoteEntry.js, chunks/
    seeds/              *.json and skills/mail/SKILL.md
    README.md
    file-manifest.json  sha256 of every packaged file

  Output: $env:REDLEAF_SCRATCH_DIR/leaf-mail/dist/mail-<version>.leafpkg (+ .sha256
  and package-receipt.json). Kernel-provided assemblies (Leaf.Sdk, RedBamboo.AppHost)
  are never shipped: a plugin-side copy breaks ILeafPlugin type identity.

.PARAMETER LeafSdkProject
  Leaf.Sdk.csproj to build against. Defaults to a sibling RedLeaf checkout.

.PARAMETER AllowDirty
  Package uncommitted source (candidate builds only). Reviewed packages are built
  from a clean tree.
#>
[CmdletBinding()]
param(
    [string]$OutputDir,
    [string]$LeafSdkProject,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$ForbiddenAssemblies = @('Leaf.Sdk', 'RedBamboo.AppHost')

if (-not $env:REDLEAF_SCRATCH_DIR) { throw 'REDLEAF_SCRATCH_DIR is required' }
$scratchRoot = [IO.Path]::GetFullPath($env:REDLEAF_SCRATCH_DIR)
$buildRoot = Join-Path $scratchRoot ('leaf-mail/build-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $buildRoot | Out-Null
if (-not $OutputDir) { $OutputDir = Join-Path $scratchRoot 'leaf-mail/dist' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$sourceHead = (& git -C $PSScriptRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0 -or -not $sourceHead) { $sourceHead = 'uncommitted' }
$dirty = (& git -C $PSScriptRoot status --porcelain)
if ($dirty -and -not $AllowDirty) { throw 'Commit reviewed source first, or pass -AllowDirty for a candidate build' }

if (-not $LeafSdkProject) {
    $LeafSdkProject = Join-Path $PSScriptRoot '../redleaf/src/Leaf.Sdk/Leaf.Sdk.csproj'
}
$LeafSdkProject = [IO.Path]::GetFullPath($LeafSdkProject)
if (-not (Test-Path -LiteralPath $LeafSdkProject)) { throw "Leaf.Sdk project not found: $LeafSdkProject (pass -LeafSdkProject)" }

$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Raw | ConvertFrom-Json
$id = $manifest.id
$version = $manifest.version
if (-not $version) { throw 'plugin.json has no version' }

# Locked restore and redirected outputs leave the SDK checkout untouched.
Write-Host '  dotnet build Leaf.Sdk (scratch artifacts)' -ForegroundColor DarkGray
& dotnet build $LeafSdkProject -c Release --artifacts-path "$buildRoot/sdk" -p:RestoreLockedMode=true --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'SDK build failed' }
$sdkAssembly = Join-Path $buildRoot 'sdk/bin/Leaf.Sdk/release/Leaf.Sdk.dll'
if (-not (Test-Path -LiteralPath $sdkAssembly)) { throw "SDK assembly not produced at $sdkAssembly" }

$staging = Join-Path $buildRoot 'package'
New-Item -ItemType Directory -Path $staging | Out-Null
$backendDir = Join-Path $staging 'backend'
Write-Host '  dotnet publish Leaf.Plugins.Mail -c Release' -ForegroundColor DarkGray
& dotnet publish (Join-Path $PSScriptRoot 'src/Leaf.Plugins.Mail/Leaf.Plugins.Mail.csproj') -c Release -o $backendDir `
    "-p:LeafSdkAssembly=$sdkAssembly" --artifacts-path "$buildRoot/backend" --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Backend publish failed' }
foreach ($base in $ForbiddenAssemblies) {
    foreach ($extension in @('.dll', '.pdb', '.xml')) {
        $file = Join-Path $backendDir ($base + $extension)
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
}

Write-Host '  pnpm build:pkg (web/)' -ForegroundColor DarkGray
Push-Location (Join-Path $PSScriptRoot 'web')
try {
    & pnpm install --frozen-lockfile --prefer-offline
    if ($LASTEXITCODE -ne 0) { throw 'Frontend dependency install failed' }
    & pnpm run typecheck
    if ($LASTEXITCODE -ne 0) { throw 'Frontend typecheck failed' }
    & pnpm run build:pkg
    if ($LASTEXITCODE -ne 0) { throw 'Frontend build failed' }
}
finally { Pop-Location }
$webTarget = Join-Path $staging 'web/dist'
New-Item -ItemType Directory -Force -Path $webTarget | Out-Null
Copy-Item -Path (Join-Path $PSScriptRoot 'web/dist/*') -Destination $webTarget -Recurse

$buildData = [pscustomobject]@{ commit = $sourceHead; date = [DateTimeOffset]::UtcNow.ToString('O') }
$manifest | Add-Member -NotePropertyName build -NotePropertyValue $buildData -Force
[IO.File]::WriteAllText((Join-Path $staging 'plugin.json'), ($manifest | ConvertTo-Json -Depth 15), (New-Object Text.UTF8Encoding $false))
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'seeds') -Destination $staging -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $staging

$files = Get-ChildItem -LiteralPath $staging -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        path   = [IO.Path]::GetRelativePath($staging, $_.FullName).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
foreach ($base in $ForbiddenAssemblies) {
    if ($files | Where-Object { $_.path -like "backend/$base.*" }) { throw "Package would ship kernel assembly $base" }
}
[IO.File]::WriteAllText((Join-Path $staging 'file-manifest.json'), ($files | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))

$packagePath = Join-Path $OutputDir "$id-$version.leafpkg"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipCandidate = Join-Path $buildRoot 'candidate.leafpkg'
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $zipCandidate)
Copy-Item -LiteralPath $zipCandidate -Destination $packagePath -Force
$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$packagePath.sha256", "$hash`n")

$sdkRepo = Split-Path (Split-Path (Split-Path $LeafSdkProject -Parent) -Parent) -Parent
$sdkHead = (& git -C $sdkRepo rev-parse HEAD 2>$null)
[pscustomobject]@{
    package      = $packagePath
    sha256       = $hash
    sourceCommit = $sourceHead
    dirty        = [bool]$dirty
    sdkSource    = $LeafSdkProject
    sdkCommit    = $sdkHead
    files        = @($files).Count
    buildRoot    = $buildRoot
    deployed     = $false
    published    = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDir 'package-receipt.json') -Encoding utf8

Write-Host ''
Write-Host "  $packagePath" -ForegroundColor Green
Write-Host "  sha256: $hash" -ForegroundColor Green
Write-Host "  source: $sourceHead$(if ($dirty) { ' (dirty)' }); not installed, not deployed"
