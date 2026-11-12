# Builds the Windows installer of Try Linux Preview and the files for a GitHub release, in prototype\dist:
#   TryLinux-Preview-<version>-x64-setup.exe   the installer (NSIS, installs for the current user)
#   SHA256SUMS.txt                              its checksum
#   latest.json                                 what the app's updater reads: the version and the signed installer
# The installer is signed for updates with the private key in %USERPROFILE%\.tauri\try-linux-updates.key (never
# put it in the repository); the app only installs updates signed with it.
#
# Then: a release tagged tauri-v<version>-preview with the installer and SHA256SUMS.txt, and latest.json uploaded
# to the "tauri-updates" release (replacing the old one), which is where installed copies look for updates.

param([string]$Notes = "")
# Programs here print progress to their error output, which "Stop" would treat as a failure: each step's exit
# code is checked instead.
$ErrorActionPreference = "Continue"
function Check($what) { if ($LASTEXITCODE) { throw "$what failed (exit code $LASTEXITCODE)." } }
$root = Split-Path $PSScriptRoot -Parent   # prototype\
$key = Join-Path $env:USERPROFILE ".tauri\try-linux-updates.key"
if (-not (Test-Path $key)) { throw "The update signing key isn't at $key." }
$version = (Get-Content (Join-Path $root "src-tauri\tauri.conf.json") -Raw | ConvertFrom-Json).version
$tag = "tauri-v$version-preview"
$name = "TryLinux-Preview-$version-x64-setup.exe"
$repo = "https://github.com/donnysaengkio/try-linux/releases/download"

Push-Location (Join-Path $root "ui")
npm ci; Check "npm ci"
npm run build; Check "Building the interface"
Pop-Location

Push-Location $root
$env:TAURI_SIGNING_PRIVATE_KEY = Get-Content $key -Raw
$env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD = ""
try { & .\ui\node_modules\.bin\tauri.cmd build 2>&1 | Out-Host; Check "Building the installer" }
finally { Remove-Item Env:TAURI_SIGNING_PRIVATE_KEY, Env:TAURI_SIGNING_PRIVATE_KEY_PASSWORD -ErrorAction SilentlyContinue }
Pop-Location

$built = Join-Path $root "src-tauri\target\release\bundle\nsis\Try Linux Preview_${version}_x64-setup.exe"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null
Get-ChildItem $dist | Remove-Item -Force
Copy-Item $built (Join-Path $dist $name)
$hash = (Get-FileHash (Join-Path $dist $name) -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS.txt"), "$hash  $name`n")
$latest = [ordered]@{
    version = $version
    notes = $Notes
    pub_date = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    platforms = [ordered]@{
        "windows-x86_64" = [ordered]@{ signature = (Get-Content "$built.sig" -Raw).Trim(); url = "$repo/$tag/$name" }
    }
}
[IO.File]::WriteAllText((Join-Path $dist "latest.json"), ($latest | ConvertTo-Json -Depth 4))
Get-ChildItem $dist | Select-Object Name, Length
"Release tag: $tag"
