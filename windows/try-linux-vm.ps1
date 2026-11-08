<#
.SYNOPSIS
Creates and boots a Linux VM with WINQ-EMU (QEMU on the Windows Hypervisor
Platform, with Venus Vulkan / VirGL GPU acceleration).

.DESCRIPTION
First run with -Iso: creates a qcow2 disk and boots the ISO once so you can
install the distro. Later runs with just -Name boot the installed disk.
VMs live in %USERPROFILE%\LinuxVMs\<Name> by default.

.EXAMPLE
.\try-linux-vm.ps1 -Name fedora -Iso C:\ISOs\Fedora-Workstation-Live.iso

.EXAMPLE
.\try-linux-vm.ps1 -Name cachyos -Iso https://example.org/cachyos.iso -RamGB 12

.EXAMPLE
.\try-linux-vm.ps1 -Name fedora -SharedFolder C:\Users\me\Shared
#>
param(
    [Parameter(Mandatory)][string]$Name,
    [string]$Iso,                     # local path or http(s) URL
    [int]$DiskGB = 64,
    [int]$RamGB = 8,
    [int]$Cores = [Math]::Max(2, [int]([Environment]::ProcessorCount / 2)),
    [int]$GpuMemGB = 4,               # host memory for Venus blob resources
    [int]$SshPort = 2223,             # host port forwarded to guest :22
    [string]$SharedFolder,            # Windows folder exposed over virtio-9p
    [string]$WinqDir = 'C:\WINQ-EMU',
    [string]$VmRoot = (Join-Path $env:USERPROFILE 'LinuxVMs'),
    [switch]$NoVenus                  # plain VirGL (OpenGL only)
)
$ErrorActionPreference = 'Stop'

# WINQ-EMU keeps QEMU in bin\ next to its launcher, or alongside it.
$bin = Join-Path $WinqDir 'bin'
if (-not (Test-Path (Join-Path $bin 'qemu-system-x86_64w.exe'))) { $bin = $WinqDir }
$qemu = Join-Path $bin 'qemu-system-x86_64w.exe'
$qemuImg = Join-Path $bin 'qemu-img.exe'
if (-not (Test-Path $qemu)) {
    throw "WINQ-EMU not found in $WinqDir. Install it from https://github.com/cmspam/winq-emu/releases or pass -WinqDir."
}

# WHPX must be on; checking needs admin, so only warn when we can tell.
try {
    $whp = Get-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -ErrorAction Stop
    if ($whp.State -ne 'Enabled') {
        Write-Warning "Windows Hypervisor Platform is off. Turn it on in 'Turn Windows features on or off', then restart."
    }
} catch {}

$vmDir = Join-Path $VmRoot $Name
$disk = Join-Path $vmDir "$Name.qcow2"
New-Item -ItemType Directory -Force $vmDir | Out-Null

if (-not (Test-Path $disk)) {
    if (-not $Iso) { throw "VM '$Name' has no disk yet. Pass -Iso <path or URL> to install a distro." }
    & $qemuImg create -f qcow2 $disk "${DiskGB}G"
    if ($LASTEXITCODE) { throw "qemu-img failed to create $disk" }
}

if ($Iso -match '^https?://') {
    $file = Join-Path $vmDir ([IO.Path]::GetFileName(([Uri]$Iso).AbsolutePath))
    if (-not (Test-Path $file)) {
        Write-Host "Downloading $Iso"
        curl.exe -L --fail -o $file $Iso
        if ($LASTEXITCODE) { Remove-Item $file -ErrorAction SilentlyContinue; throw "Download failed" }
    }
    $Iso = $file
}
if ($Iso -and -not (Test-Path $Iso)) { throw "ISO not found: $Iso" }

if ($NoVenus) { $gpu = 'virtio-vga-gl' }
else { $gpu = "virtio-vga-gl,blob=on,hostmem=${GpuMemGB}G,venus=on" }

# BIOS boot (QEMU's default SeaBIOS): WINQ-EMU notes Vulkan is far slower under EFI.
$qargs = @(
    '-machine', 'q35,accel=whpx',
    '-cpu', 'host',
    '-smp', $Cores,
    '-m', "${RamGB}G",
    '-display', 'sdl,gl=on',
    '-device', $gpu,
    '-drive', "file=$disk,format=qcow2,if=virtio",
    '-device', 'virtio-net-pci,netdev=net0',
    '-netdev', "user,id=net0,hostfwd=tcp::${SshPort}-:22",
    '-usb', '-device', 'usb-tablet',
    '-device', 'intel-hda', '-device', 'hda-duplex'
)
if ($Iso) {
    # once=d boots the installer this time only; the reboot after install goes to disk.
    $qargs += '-cdrom', $Iso, '-boot', 'once=d'
}
if ($SharedFolder) {
    $qargs += '-fsdev', "local,id=fs0,path=$SharedFolder,security_model=mapped-xattr",
              '-device', 'virtio-9p-pci,fsdev=fs0,mount_tag=host'
}

Write-Host "Starting '$Name' ($Cores cores, ${RamGB} GB RAM). SSH: ssh -p $SshPort <user>@localhost"
if ($SharedFolder) {
    Write-Host "In the guest: sudo mkdir -p /mnt/host; sudo mount -t 9p -o trans=virtio,version=9p2000.L host /mnt/host"
}
& $qemu @qargs
