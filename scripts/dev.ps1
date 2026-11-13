# Runs Try Linux in development mode on Windows: a window that updates as the code changes. Changes to the
# interface (ui/src) show at once; changes to the Rust side (src-tauri) rebuild and restart the app.
#
#   scripts\dev.ps1               uses a test folder of its own, %LOCALAPPDATA%\TryLinux Dev, with a few sample
#                                 systems to look at the first time
#   scripts\dev.ps1 -Folder PATH  uses another test folder
#
# It's "Try Linux Dev" (tauri.dev.conf.json), so it runs beside the installed Try Linux, and it never opens your
# real systems or preferences: those stay with the installed app. Stop it with Ctrl+C, or by quitting it.
param([string]$Folder = (Join-Path $env:LOCALAPPDATA "TryLinux Dev"))
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$real = Join-Path $env:LOCALAPPDATA "TryLinux"
if ((Resolve-Path -LiteralPath $Folder -ErrorAction SilentlyContinue).Path -eq (Resolve-Path -LiteralPath $real -ErrorAction SilentlyContinue).Path) {
    throw "That's the installed Try Linux's own folder. Choose a separate test folder."
}

# Sample systems the first time, so Virtual Machines has something to show. They're for looking at the list,
# groups and Settings: their disks are empty, so they don't start. Create makes real ones here.
$vms = Join-Path $Folder "vms"
if (-not (Test-Path $vms) -or -not (Get-ChildItem $vms -Directory -ErrorAction SilentlyContinue)) {
    $samples = @(
        @{ id = "sample-fedora"; name = "Fedora Workstation"; cores = 4; ram = 8; gb = 17.5 },
        @{ id = "sample-ubuntu"; name = "Ubuntu Desktop"; cores = 6; ram = 6; gb = 22.1 },
        @{ id = "sample-debian"; name = "Debian (Xfce)"; cores = 2; ram = 4; gb = 3.5 },
        @{ id = "sample-opensuse"; name = "openSUSE Tumbleweed"; cores = 4; ram = 6; gb = 9.8 }
    )
    foreach ($s in $samples) {
        $dir = Join-Path $vms $s.id
        New-Item -ItemType Directory -Force $dir | Out-Null
        $json = [ordered]@{ id = $s.id; display_name = $s.name; cores = $s.cores; ram_gb = $s.ram; profile = 2; pending_install = $false;
            iso = ""; created = (Get-Date -Format "yyyy-MM-dd HH:mm"); net_type = 0; forwards = @(); sound = $true; mic = $false } | ConvertTo-Json
        [IO.File]::WriteAllText((Join-Path $dir "vm.json"), $json, (New-Object Text.UTF8Encoding $false))
        # An empty disk file of that size (it takes no space), so DISK shows a figure.
        $disk = Join-Path $dir "disk.qcow2"
        [IO.File]::Create($disk).Close()
        fsutil sparse setflag $disk | Out-Null
        $f = [IO.File]::OpenWrite($disk); $f.SetLength([long]($s.gb * 1GB)); $f.Close()
    }
    Write-Host "Made sample systems in $vms"
}

$tauri = Join-Path $root "ui\node_modules\.bin\tauri.cmd"
if (-not (Test-Path $tauri)) { Push-Location (Join-Path $root "ui"); npm ci --no-audit --no-fund; Pop-Location }
$env:TRYLINUX_HOME = $Folder
Write-Host "Try Linux Dev, with its test folder $Folder"
Push-Location $root
try { & $tauri dev --config (Join-Path $root "src-tauri\tauri.dev.conf.json") } finally { Pop-Location }
