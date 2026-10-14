<#
    Tempo release script
    --------------------
    Publishes a new version and pushes it so every existing install updates itself.

      .\release.ps1 -Version 1.1.0 -Notes "Faster bloatware scan","Fixed uninstall progress"

    What it does, in order:
      1. writes <Version> into the .csproj
      2. builds the single-file self-contained .exe
      3. hashes it (SHA-256)
      4. creates GitHub release  v<Version>  with the .exe attached
      5. writes update/latest.json pointing at that asset
      6. commits and pushes, which is what existing installs actually read

    Nothing is published until the build succeeds, so a failed build can't ship.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string[]]$Notes = @(),
    [string]$Repo = 'halokid4287-sys/tempo',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root    = $PSScriptRoot
$csproj  = Join-Path $root 'src\Tempo.App\Tempo.App.csproj'
$outDir  = Join-Path $root 'share'
$exePath = Join-Path $outDir 'Tempo.exe'
$manifest= Join-Path $root 'update\latest.json'
$gh      = "$env:ProgramFiles\GitHub CLI\gh.exe"
if (-not (Test-Path $gh)) { $gh = (Get-Command gh -ErrorAction Stop).Source }

function Step($n, $msg) { Write-Host "`n[$n] $msg" -ForegroundColor Cyan }

# --- guard: don't ship a version that isn't newer than the published one ---
if (Test-Path $manifest) {
    $prev = (Get-Content $manifest -Raw | ConvertFrom-Json).version
    if ([version]$Version -le [version]$prev) {
        throw "Version $Version is not newer than the currently published $prev."
    }
    Write-Host "Previous published version: $prev" -ForegroundColor DarkGray
}

Step 1 "Setting version to $Version"
# read/write as explicit UTF-8 without BOM: Get-Content/Set-Content mangle non-ASCII
# characters in the .csproj (an em dash became 'â€”' the first time this ran)
$utf8 = New-Object System.Text.UTF8Encoding $false
$xml = [System.IO.File]::ReadAllText($csproj, $utf8)
$xml = $xml -replace '<Version>[\d\.]+</Version>', "<Version>$Version</Version>"
[System.IO.File]::WriteAllText($csproj, $xml, $utf8)

Step 2 "Building single-file release (this takes a minute)"
if (Test-Path $outDir) { Get-ChildItem $outDir -File | Remove-Item -Force }
dotnet publish $csproj -c Release -o $outDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed - nothing was published." }
Get-ChildItem $outDir -File | Where-Object { $_.Extension -in '.xml','.pdb' } | Remove-Item -Force
if (-not (Test-Path $exePath)) { throw "Tempo.exe was not produced - nothing was published." }
$sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "    built Tempo.exe ($sizeMB MB)" -ForegroundColor DarkGray

Step 3 "Hashing"
$sha = (Get-FileHash $exePath -Algorithm SHA256).Hash
Write-Host "    SHA-256 $sha" -ForegroundColor DarkGray

$noteText = if ($Notes.Count) { ($Notes | ForEach-Object { "- $_" }) -join "`n" } else { "- Maintenance update" }

if ($DryRun) {
    Write-Host "`nDRY RUN - stopping before anything is published." -ForegroundColor Yellow
    Write-Host "Would publish v$Version to $Repo with notes:`n$noteText"
    return
}

Step 4 "Creating GitHub release v$Version"
& $gh release create "v$Version" $exePath --repo $Repo --title "Tempo $Version" --notes $noteText
if ($LASTEXITCODE -ne 0) { throw "gh release create failed - manifest NOT updated, so nobody will be offered a broken update." }

Step 5 "Writing update manifest"
New-Item -ItemType Directory (Split-Path $manifest) -Force | Out-Null
$json = [ordered]@{
    version  = $Version
    url      = "https://github.com/$Repo/releases/download/v$Version/Tempo.exe"
    sha256   = $sha
    notes    = $noteText
    released = (Get-Date -Format 'yyyy-MM-dd')
} | ConvertTo-Json
[System.IO.File]::WriteAllText($manifest, $json, $utf8)

Step 6 "Pushing"
# git writes ordinary progress and warnings to stderr, which PowerShell would otherwise
# treat as a terminating error, so check exit codes instead.
$ErrorActionPreference = 'Continue'
git -C $root add -A 2>&1 | Out-Null
git -C $root commit -m "Release $Version`n`n$noteText" 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "    nothing new to commit" -ForegroundColor DarkGray }
git -C $root push 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "git push failed - the release exists but installs won't see it until the manifest is pushed." }
$ErrorActionPreference = 'Stop'

Write-Host "`nReleased $Version." -ForegroundColor Green
Write-Host "Every existing install will offer this update next time it starts." -ForegroundColor Green
