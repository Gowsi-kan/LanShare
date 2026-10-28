<#
.SYNOPSIS
  Build, package, tag and publish a LanShare GitHub release in one go.

.EXAMPLE
  .\release.ps1 -Version 1.0.0
  .\release.ps1 -Version 1.1.0-beta.1 -PreRelease
  .\release.ps1 -Version 1.0.0 -NotesFile RELEASE_NOTES.md
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$NotesFile = "",
    [switch]$PreRelease,
    [switch]$Draft
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$Tag = "v$Version"
$Dist = Join-Path $PSScriptRoot "dist"

function Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }

# Run a native command and stop the script if it fails
function Exec {
    param([string]$Cmd, [string[]]$Arguments)
    & $Cmd @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Cmd $($Arguments -join ' ')" }
}

# ---------- 1. Pre-flight checks ----------
Step "Checking prerequisites"
foreach ($tool in "git", "dotnet", "gh", "tar") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "'$tool' was not found in PATH. Install it and try again."
    }
}

& gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "GitHub CLI is not logged in. Run: gh auth login" }

if (-not (Test-Path "LICENSE")) {
    Write-Warning "No LICENSE file found in the repo root."
}

if (git status --porcelain) {
    throw "Working tree has uncommitted changes. Commit or stash them first."
}

if (git tag --list $Tag) { throw "Tag $Tag already exists locally." }
if (git ls-remote --tags origin $Tag) { throw "Tag $Tag already exists on origin." }

# ---------- 2. Clean old output ----------
Step "Cleaning previous build output"
Remove-Item -Recurse -Force "publish", $Dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $Dist | Out-Null

# ---------- 3. Build ----------
$targets = @(
    @{ Rid = "win-x64";   Out = "publish/win"   },
    @{ Rid = "linux-x64"; Out = "publish/linux" }
)

foreach ($t in $targets) {
    Step "Publishing $($t.Rid)"
    Exec "dotnet" @(
        "publish", "src/LanShare", "-c", "Release", "-r", $t.Rid, "--self-contained",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", $t.Out
    )
}

# ---------- 4. Package ----------
Step "Packaging"
$winZip   = Join-Path $Dist "LanShare-$Tag-win-x64.zip"
$linuxTgz = Join-Path $Dist "LanShare-$Tag-linux-x64.tar.gz"

Compress-Archive -Path "publish/win/*" -DestinationPath $winZip
Exec "tar" @("-czf", $linuxTgz, "-C", "publish/linux", ".")

# SHA-256 checksums
$sumFile = Join-Path $Dist "SHA256SUMS.txt"
Get-ChildItem $winZip, $linuxTgz | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "$h  $($_.Name)"
} | Set-Content $sumFile -Encoding ascii

Get-ChildItem $Dist | Format-Table Name, @{ n = "Size (MB)"; e = { [math]::Round($_.Length / 1MB, 2) } }

# ---------- 5. Release notes ----------
if ($NotesFile -and -not (Test-Path $NotesFile)) { throw "Notes file '$NotesFile' not found." }

$defaultNotes = @"
## LanShare $Tag

Fast, resumable large-file transfer over your LAN.

### Downloads
- **Windows:** ``LanShare-$Tag-win-x64.zip``
- **Linux:** ``LanShare-$Tag-linux-x64.tar.gz`` (after extracting, run ``chmod +x LanShare``)
- Checksums: ``SHA256SUMS.txt``

> Windows may show a SmartScreen warning for an unsigned app: click **More info**, then **Run anyway**.

### Known limitations
No encryption/auth (trusted LAN only), no settings persistence, no whole-file end-to-end hash.
"@

# ---------- 6. Confirm ----------
Write-Host ""
$answer = Read-Host "Create tag $Tag, push it and publish the release? (y/N)"
if ($answer -notin @("y", "Y")) { Write-Host "Aborted. Nothing was tagged or published."; exit 0 }

# ---------- 7. Tag + push ----------
Step "Tagging and pushing $Tag"
Exec "git" @("tag", "-a", $Tag, "-m", "LanShare $Tag")
Exec "git" @("push", "origin", $Tag)

# ---------- 8. GitHub release ----------
Step "Creating GitHub release"
$ghArgs = @("release", "create", $Tag, $winZip, $linuxTgz, $sumFile, "--title", "LanShare $Tag")

if ($NotesFile) {
    $ghArgs += @("--notes-file", $NotesFile)
} else {
    $ghArgs += @("--notes", $defaultNotes, "--generate-notes")
}
if ($PreRelease) { $ghArgs += "--prerelease" }
if ($Draft)      { $ghArgs += "--draft" }

Exec "gh" $ghArgs

Step "Done"
& gh release view $Tag --web
