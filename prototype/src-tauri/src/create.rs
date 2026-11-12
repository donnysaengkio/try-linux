// Create: finding a distribution's current installer on its mirror, downloading it (resuming a partial
// download) and checking it against the official checksum, then making the system (its disk and vm.ini, as
// the Windows edition writes them) and starting its installer. Progress goes to the page as events.

use crate::config::{self, Host, VmConfig};
use crate::vm::{self, Vms};
use crate::Probe;
use regex::Regex;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256, Sha512};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter, State};

const AGENT: &str = "TryLinux/1.0";
static CANCEL: AtomicBool = AtomicBool::new(false);

// Where things are kept: see paths.rs.

use crate::paths::{isos_dir, remember, vms_dir};

// ---------------------------------------------------------------- what Create starts from

#[derive(Serialize)]
pub struct Defaults {
    name: String,
    location: String,
    taken: Vec<String>,  // names already used (a new system needs its own)
    threads: usize,
    total_ram: u64,
    max_cores: usize,
    max_ram: u64,
    balanced: (usize, u64),
    maximum: (usize, u64),
}

// A name for the new system (the distribution's, numbered if it's taken), where it would go, and this PC's limits.
#[tauri::command]
pub fn create_defaults(probe: State<Probe>, seed: String) -> Defaults {
    let host = probe.host();
    let taken: Vec<String> = config::system_dirs().iter().filter_map(|d| VmConfig::load(d)).map(|c| c.name()).collect();
    let mut name = seed.clone();
    let mut i = 2;
    while taken.iter().any(|t| t.to_lowercase() == name.to_lowercase()) {
        name = format!("{seed} {i}");
        i += 1;
    }
    Defaults {
        name,
        location: vms_dir().to_string_lossy().into(),
        taken,
        threads: host.threads,
        total_ram: host.ram_gb,
        max_cores: host.max_cores(),
        max_ram: host.max_ram(),
        balanced: ((host.threads / 2).max(2), (host.ram_gb / 2).clamp(2, 16)),
        maximum: (host.max_cores(), host.max_ram()),
    }
}

#[derive(Serialize)]
pub struct Free {
    bytes: Option<u64>,
    drive: String,
}

// The room on the drive a folder is on (the folder needn't exist yet).
#[tauri::command(async)]
pub fn free_space(path: String) -> Free {
    match crate::settings::free_on(&path) {
        Some((bytes, drive)) => Free { bytes: Some(bytes), drive },
        None => Free { bytes: None, drive: String::new() },
    }
}

// ---------------------------------------------------------------- finding the installer

#[derive(Deserialize, Clone)]
pub struct Distro {
    id: String,
    name: String,
    base: String,
    steps: Vec<String>,
    checksum: Option<String>,
}

struct Resolved {
    url: String,
    file: String,
    size: Option<u64>,
    hash: Option<String>, // the official SHA-256, or SHA-512 when that's all a project publishes
}

fn agent() -> ureq::Agent {
    ureq::AgentBuilder::new().user_agent(AGENT).timeout_connect(Duration::from_secs(30)).timeout_read(Duration::from_secs(60)).build()
}
fn get_text(url: &str) -> Result<String, String> {
    agent().get(url).call().map_err(|e| e.to_string())?.into_string().map_err(|e| e.to_string())
}
fn head_size(url: &str) -> Option<u64> {
    agent().head(url).call().ok()?.header("Content-Length")?.parse().ok()
}

fn unescape(s: &str) -> String {
    let b = s.as_bytes();
    let mut out = Vec::with_capacity(b.len());
    let mut i = 0;
    while i < b.len() {
        if b[i] == b'%' && i + 2 < b.len() {
            if let Some(v) = std::str::from_utf8(&b[i + 1..i + 3]).ok().and_then(|h| u8::from_str_radix(h, 16).ok()) {
                out.push(v);
                i += 3;
                continue;
            }
        }
        out.push(b[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

// Numbers compared as numbers, ignoring architecture tags, then the text itself.
fn compare_versions(a: &str, b: &str) -> std::cmp::Ordering {
    let arch = Regex::new(r"(?i)(x86_|amd)64|64-?bit").unwrap();
    let (a2, b2) = (arch.replace_all(a, ""), arch.replace_all(b, ""));
    let digits = Regex::new(r"\d+").unwrap();
    let na: Vec<&str> = digits.find_iter(&a2).map(|m| m.as_str().trim_start_matches('0')).collect();
    let nb: Vec<&str> = digits.find_iter(&b2).map(|m| m.as_str().trim_start_matches('0')).collect();
    for (x, y) in na.iter().zip(&nb) {
        let c = x.len().cmp(&y.len()).then_with(|| x.cmp(y));
        if c.is_ne() {
            return c;
        }
    }
    na.len().cmp(&nb.len()).then_with(|| a2.cmp(&b2))
}

// The links in a directory listing that match `pattern`, newest first. A (?<v>...) group marks the version,
// which outranks the rest of the name (so Zorin 18.1 beats 18 re-spin r3).
fn matches(url: &str, pattern: &str) -> Result<Vec<String>, String> {
    let html = get_text(url)?;
    let re = Regex::new(pattern).map_err(|e| e.to_string())?;
    let href = Regex::new(r#"(?i)href="([^"?#]+)""#).unwrap();
    let mut found: Vec<String> = vec![];
    for c in href.captures_iter(&html) {
        let h = &c[1];
        let dir = h.ends_with('/');
        let seg = h.trim_end_matches('/');
        let mut seg = seg[seg.rfind('/').map_or(0, |i| i + 1)..].to_string();
        if seg.is_empty() || seg == ".." {
            continue;
        }
        if dir {
            seg.push('/');
        }
        if re.is_match(&unescape(&seg)) && !found.contains(&seg) {
            found.push(seg);
        }
    }
    let version = |s: &str| {
        let u = unescape(s);
        re.captures(&u).and_then(|c| c.name("v").map(|m| m.as_str().to_string())).unwrap_or(u)
    };
    found.sort_by(|a, b| compare_versions(&version(a), &version(b)).then_with(|| compare_versions(a, b)));
    found.reverse();
    Ok(found)
}

// Each step is added to the URL; one starting with ~ is a pattern for the newest matching link. An older
// match is tried when the newest isn't fully published yet.
fn walk(url: &str, steps: &[String]) -> Option<String> {
    let Some((step, rest)) = steps.split_first() else { return Some(url.to_string()) };
    match step.strip_prefix('~') {
        None => walk(&format!("{url}{step}"), rest),
        Some(pattern) => matches(url, pattern).ok()?.iter().take(3).find_map(|m| walk(&format!("{url}{m}"), rest)),
    }
}

fn find_hash(iso_url: &str, spec: &str, file: &str) -> Option<String> {
    let dir = &iso_url[..iso_url.rfind('/')? + 1];
    let name = match spec.strip_prefix('~') {
        Some(p) => matches(dir, p).ok()?.into_iter().next()?,
        None => spec.replace("{iso}", &iso_url[dir.len()..]),
    };
    let text = get_text(&format!("{dir}{name}")).ok()?;
    let hex = Regex::new(r"\b([0-9a-fA-F]{128}|[0-9a-fA-F]{64})\b").unwrap();
    for line in text.lines() {
        if line.contains(file) {
            if let Some(m) = hex.find(line) {
                return Some(m.as_str().to_lowercase());
            }
        }
    }
    // A checksum file for a single image (e.g. openSUSE's *-Current.iso.sha256).
    let mut all: Vec<String> = hex.find_iter(&text).map(|m| m.as_str().to_lowercase()).collect();
    all.dedup();
    (all.len() == 1).then(|| all.remove(0))
}

fn resolve(d: &Distro) -> Result<Resolved, String> {
    if !d.base.starts_with("https://") {
        return Err(format!("{}'s mirror isn't an https address, so Try Linux won't download from it.", d.name));
    }
    let url = walk(&d.base, &d.steps)
        .ok_or_else(|| format!("Couldn't find the current {} download. The mirror may be updating; try again later.", d.name))?;
    let path = url.split(['?', '#']).next().unwrap_or(&url);
    let file = unescape(&path[path.rfind('/').map_or(0, |i| i + 1)..]).replace(['\\', '/', ':', '*', '?', '"', '<', '>', '|'], "_");
    let size = head_size(&url);
    let hash = d.checksum.as_deref().and_then(|c| find_hash(&url, c, &file));
    Ok(Resolved { url, file, size, hash })
}

// ---------------------------------------------------------------- progress

#[derive(Serialize, Clone)]
struct Progress {
    status: String,
    value: f64, // 0 to 1, or -1 while it can't tell how far along it is
    detail: String,
}
fn report(app: &AppHandle, status: &str, value: f64, detail: &str) {
    let _ = app.emit("create-progress", Progress { status: status.into(), value, detail: detail.into() });
}
fn cancelled() -> Result<(), String> {
    if CANCEL.load(Ordering::Relaxed) { Err("cancelled".into()) } else { Ok(()) }
}
pub fn bytes(n: u64) -> String {
    let f = n as f64;
    // As Windows shows sizes: GB from a gigabyte, MB below it, with a decimal under 10 MB.
    let mb = f / (1u64 << 20) as f64;
    if f >= (1u64 << 30) as f64 { format!("{:.1} GB", f / (1u64 << 30) as f64) } else if mb < 10.0 { format!("{mb:.1} MB") } else { format!("{mb:.0} MB") }
}
pub fn transfer(got: u64, total: Option<u64>, rate: f64) -> String {
    let mut s = bytes(got) + &total.map(|t| format!(" of {}", bytes(t))).unwrap_or_default() + &format!("  ·  {}/s", bytes(rate as u64));
    if let Some(t) = total.filter(|_| rate > 1.0) {
        let secs = t.saturating_sub(got) as f64 / rate;
        s += &if secs < 60.0 { "  ·  under a minute left".into() } else { format!("  ·  {} min left", (secs / 60.0).ceil()) };
    }
    s
}

// Downloads to `part`, resuming what's there when the server allows it, with Create's progress.
fn download(app: &AppHandle, what: &str, url: &str, part: &Path, expected: Option<u64>) -> Result<(), String> {
    report(app, what, if expected.is_some() { 0.0 } else { -1.0 }, "Connecting…");
    download_with(url, part, expected, &mut |got, total, rate| {
        report(app, what, total.map_or(-1.0, |t| got as f64 / t as f64), &transfer(got, total, rate))
    }, &|| CANCEL.load(Ordering::Relaxed))
}

// The download itself: `progress` gets (bytes so far, total if known, bytes a second) every 200 ms;
// Err("cancelled") once `cancelled` says so.
pub fn download_with(url: &str, part: &Path, expected: Option<u64>, progress: &mut dyn FnMut(u64, Option<u64>, f64),
                     cancelled: &dyn Fn() -> bool) -> Result<(), String> {
    let mut have = fs::metadata(part).map(|m| m.len()).unwrap_or(0);
    if let Some(e) = expected {
        if have == e {
            return Ok(());
        }
        if have > e {
            fs::remove_file(part).ok();
            have = 0;
        }
    }
    let mut req = agent().get(url);
    if have > 0 {
        req = req.set("Range", &format!("bytes={have}-"));
    }
    let resp = match req.call() {
        Ok(r) => r,
        Err(ureq::Error::Status(416, _)) if have > 0 => return Ok(()), // already complete
        Err(e) => return Err(format!("The download failed: {e}")),
    };
    let append = have > 0 && resp.status() == 206;
    if !append {
        have = 0;
    }
    let total = resp.header("Content-Length").and_then(|l| l.parse::<u64>().ok()).map(|l| l + have).or(expected);
    let mut out = if append { OpenOptions::new().append(true).open(part) } else { File::create(part) }.map_err(|e| e.to_string())?;
    let mut src = resp.into_reader();
    let mut buf = vec![0u8; 1 << 20];
    let (clock, start) = (Instant::now(), have);
    let mut last = Duration::ZERO;
    loop {
        if cancelled() {
            return Err("cancelled".into());
        }
        let n = src.read(&mut buf).map_err(|e| format!("The download was interrupted ({e}). Try again to resume it."))?;
        if n == 0 {
            break;
        }
        out.write_all(&buf[..n]).map_err(|e| e.to_string())?;
        have += n as u64;
        if clock.elapsed() - last >= Duration::from_millis(200) {
            last = clock.elapsed();
            progress(have, total, (have - start) as f64 / clock.elapsed().as_secs_f64().max(0.001));
        }
    }
    if total.is_some_and(|t| have < t) {
        return Err("The download was interrupted. Try again to resume it.".into());
    }
    Ok(())
}

fn hash_file(app: &AppHandle, file: &Path, sha512: bool, detail: &str) -> Result<String, String> {
    let mut f = File::open(file).map_err(|e| e.to_string())?;
    let len = f.metadata().map(|m| m.len()).unwrap_or(1).max(1);
    let (mut s256, mut s512) = (Sha256::new(), Sha512::new());
    let mut buf = vec![0u8; 1 << 20];
    let (mut done, mut tick) = (0u64, 0u32);
    loop {
        cancelled()?;
        let n = f.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        if sha512 { s512.update(&buf[..n]) } else { s256.update(&buf[..n]) }
        done += n as u64;
        tick += 1;
        if tick % 32 == 0 {
            report(app, "Verifying the download", done as f64 / len as f64, detail);
        }
    }
    let digest: Vec<u8> = if sha512 { s512.finalize().to_vec() } else { s256.finalize().to_vec() };
    Ok(digest.iter().map(|b| format!("{b:02x}")).collect())
}

// ---------------------------------------------------------------- making the system

// Where the system comes from: a distribution's download, an ISO file on this PC, an ISO at a URL, or an
// existing virtual disk to import (exactly one).
#[derive(Deserialize)]
pub struct Plan {
    distro: Option<Distro>,
    iso: Option<String>,
    url: Option<String>,
    disk: Option<String>,
    name: String,
    description: String,
    location: String,
    profile: u64,
    ram: u64,
    cores: u64,
    disk_gb: u64,
    disk_full: bool,
    install_winq: bool, // install WINQ-EMU first (the page asked)
}

// The folder and internal name: made from its name, unique among your systems and where it goes.
pub fn folder_name(seed: &str, parent: &Path) -> String {
    let mut base = Regex::new("[^a-z0-9_-]+").unwrap().replace_all(&seed.to_lowercase(), "-").trim_matches('-').to_string();
    if base.is_empty() {
        base = "linux".into();
    }
    base.truncate(32);
    let taken: Vec<String> = config::system_dirs().iter().filter_map(|d| VmConfig::load(d)).map(|c| c.id.to_lowercase()).collect();
    let mut n = base.clone();
    let mut i = 2;
    while taken.contains(&n.to_lowercase()) || parent.join(&n).exists() {
        n = format!("{base}-{i}");
        i += 1;
    }
    n
}

// SSH for the new system on the next free port after the others' (2223 for the first).
fn ssh_port() -> u16 {
    let re = Regex::new(r"(?i)tcp:(\d+):22\b").unwrap();
    let ports: Vec<u16> = config::system_dirs()
        .iter()
        .filter_map(|d| VmConfig::load(d))
        .filter_map(|c| match c.get("forwards") {
            "" => c.get("sshport").parse().ok(),
            f => re.captures(f).and_then(|m| m[1].parse().ok()),
        })
        .collect();
    ports.iter().max().map_or(2223, |m| (*m).max(2222) + 1)
}

pub fn qemu_img(args: &[&str]) -> Result<(), String> {
    let img = vm::qemu_img().ok_or("qemu-img isn't installed, so Try Linux can't make the virtual disk.")?;
    let mut cmd = std::process::Command::new(img);
    cmd.args(args);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
    let out = cmd.output().map_err(|e| e.to_string())?;
    if out.status.success() { Ok(()) } else { Err(String::from_utf8_lossy(&out.stderr).trim().to_string()) }
}

// Turns a file's NTFS sparse flag off, so its whole length is taken on the drive now (FSCTL_SET_SPARSE).
#[cfg(windows)]
fn clear_sparse(file: &Path) -> Result<(), String> {
    use std::os::windows::io::AsRawHandle;
    #[link(name = "kernel32")]
    extern "system" {
        fn DeviceIoControl(h: isize, code: u32, input: *const u8, in_size: u32, out: *mut u8, out_size: u32, returned: *mut u32, ov: isize) -> i32;
    }
    let f = OpenOptions::new().read(true).write(true).open(file).map_err(|e| e.to_string())?;
    let (flag, mut returned) = (0u8, 0u32);
    let ok = unsafe { DeviceIoControl(f.as_raw_handle() as isize, 0x0009_00C4, &flag, 1, std::ptr::null_mut(), 0, &mut returned, 0) };
    if ok != 0 { Ok(()) } else { Err("Couldn't allocate the disk's full size.".into()) }
}
#[cfg(not(windows))]
fn clear_sparse(_file: &Path) -> Result<(), String> {
    Ok(())
}

// Every setting, in the order and format this computer's edition saves them (vm.ini on Windows; vm.json or
// config.json, with their own names, on Linux and macOS: see config.rs).
fn write_ini(dir: &Path, p: &Plan, iso: Option<&Path>) -> std::io::Result<()> {
    let b = |v: bool| if v { "1" } else { "0" }.to_string();
    let desc = p.description.trim().replace("\r\n", "\n").replace('\\', "\\\\").replace('\n', "\\n");
    let ssh = ssh_port();
    let mut values: Vec<(&str, String)> = vec![
        ("name", p.name.trim().into()),
        ("distro", p.distro.as_ref().map_or("", |d| d.id.as_str()).into()),
        ("iso", iso.map(|i| i.to_string_lossy().to_string()).unwrap_or_default()),
        ("shared", String::new()),
        ("profile", p.profile.min(2).to_string()),
        ("ram", p.ram.max(1).to_string()),
        ("cores", p.cores.max(1).to_string()),
    ];
    if config::NATIVE == config::Format::Ini {
        values.extend([("gpumem", "4".into()), ("sshport", ssh.to_string()), ("venus", "1".into())]);
    }
    values.extend([
        ("fullscreen", "0".into()),
        ("pending", b(iso.is_some())), // an imported disk has Linux already
        ("diskgb", "0".into()),
    ]);
    if config::NATIVE == config::Format::Ini {
        values.push(("displays", "1".into()));
    }
    values.extend([("render", "0".into()), ("sharedwrite", "0".into()), ("sound", "1".into()), ("mic", "1".into())]);
    if config::NATIVE == config::Format::Ini {
        // Windows keeps the SSH port on its own until port forwards are changed.
        values.extend([("shortcutdirect", "1".into()), ("forwards", String::new()), ("fsdisplay", String::new())]);
    } else {
        values.push(("forwards", format!("tcp:{ssh}:22")));
    }
    values.extend([
        ("lastrender", String::new()),
        ("disks", String::new()),
        ("reclaim", "1".into()),
        ("cd", String::new()),
        ("nettype", "0".into()),
        ("diskfull", b(p.disk_full)),
        ("description", desc),
    ]);
    config::create(dir, &values).map(|_| ())
}

// Downloads `url` into the isos folder unless it's already there, checking it against `hash` if there is one.
fn fetch(app: &AppHandle, url: &str, file: &str, size: Option<u64>, hash: Option<&str>) -> Result<PathBuf, String> {
    let isos = isos_dir();
    let iso = isos.join(file);
    if iso.exists() {
        return Ok(iso);
    }
    fs::create_dir_all(&isos).map_err(|e| e.to_string())?;
    let part = PathBuf::from(format!("{}.part", iso.to_string_lossy()));
    download(app, &format!("Downloading {file}"), url, &part, size)?;
    if let Some(want) = hash {
        let sha512 = want.len() == 128;
        let against = format!("Comparing with the official {} checksum", if sha512 { "SHA-512" } else { "SHA-256" });
        report(app, "Verifying the download", 0.0, &against);
        if hash_file(app, &part, sha512, &against)? != want {
            fs::remove_file(&part).ok();
            return Err("The download didn't match its official checksum, so it was deleted. Try again.".into());
        }
    }
    fs::rename(&part, &iso).map_err(|e| e.to_string())?;
    Ok(iso)
}

// A safe file name for a URL's last part, ending in .iso.
fn url_file(url: &str) -> String {
    let path = url.split(['?', '#']).next().unwrap_or(url);
    let bad: &[char] = &['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
    let mut file = unescape(&path[path.rfind('/').map_or(0, |i| i + 1)..]).replace(bad, "_");
    if file.is_empty() {
        file = "download".into();
    }
    if !file.to_lowercase().ends_with(".iso") {
        file += ".iso";
    }
    file
}

// The installer for a plan with one: found and downloaded for a distribution, downloaded from a URL, or the file.
fn installer(app: &AppHandle, p: &Plan) -> Result<PathBuf, String> {
    if let Some(d) = &p.distro {
        report(app, &format!("Finding the latest {}", d.name), -1.0, d.base.split('/').nth(2).unwrap_or(""));
        let r = resolve(d)?;
        cancelled()?;
        return fetch(app, &r.url, &r.file, r.size, r.hash.as_deref());
    }
    if let Some(url) = &p.url {
        if !url.starts_with("https://") && !url.starts_with("http://") {
            return Err("That isn't a web address. It should start with https://".into());
        }
        report(app, "Connecting", -1.0, url.split('/').nth(2).unwrap_or(""));
        return fetch(app, url, &url_file(url), head_size(url), None);
    }
    let iso = PathBuf::from(p.iso.as_deref().unwrap_or(""));
    if !iso.is_file() {
        return Err(format!("{} can't be found any more.", iso.to_string_lossy()));
    }
    Ok(iso)
}

// Copies a disk into the new system's qcow2 (any format qemu-img reads), showing how far along it is.
// The original isn't changed. Cancel stops it.
fn import(app: &AppHandle, from: &str, to: &Path, name: &str) -> Result<(), String> {
    let file = Path::new(from).file_name().map(|f| f.to_string_lossy().to_string()).unwrap_or_default();
    let what = format!("Importing {file}");
    let detail = format!("{}, copied into a new disk for {name}", fs::metadata(from).map(|m| bytes(m.len())).unwrap_or_default());
    report(app, &what, 0.0, &detail);
    // macOS keeps raw disks: a raw one is copied as it is (a quick clone on APFS); others need qemu-img.
    let mac = config::NATIVE == config::Format::Mac;
    if mac && vm::qemu_img().is_none() {
        let lower = file.to_lowercase();
        if lower.ends_with(".img") || lower.ends_with(".raw") {
            report(app, &what, -1.0, &detail);
            return fs::copy(from, to).map(|_| ()).map_err(|e| format!("Couldn't import {file}: {e}"));
        }
        return Err(format!("{file} isn't a raw disk image, so it needs qemu-img to be converted. Install it with Homebrew (brew install qemu), then try again."));
    }
    let to = to.to_string_lossy().to_string();
    let format = if mac { "raw" } else { "qcow2" };
    qemu_img_progress(&["convert", "-p", "-O", format, from, &to], |v| report(app, &what, v, &detail), || CANCEL.load(Ordering::Relaxed))
        .map_err(|e| if e == "cancelled" { e } else { format!("Couldn't import {file}. qemu-img said:\n\n{e}") })
}

// Runs qemu-img with -p, passing on how far along it is (0 to 1); Err("cancelled") if `cancelled` says so,
// else Err with what qemu-img said.
pub fn qemu_img_progress(args: &[&str], mut progress: impl FnMut(f64), cancelled: impl Fn() -> bool) -> Result<(), String> {
    use std::process::{Command, Stdio};
    let img = vm::qemu_img().ok_or("qemu-img isn't installed.")?;
    let mut cmd = Command::new(img);
    cmd.args(args).stdout(Stdio::piped()).stderr(Stdio::piped());
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000);
    }
    let mut child = cmd.spawn().map_err(|e| e.to_string())?;
    let mut out = child.stdout.take().unwrap();
    let mut err_pipe = child.stderr.take().unwrap();
    let err_text = std::thread::spawn(move || {
        let mut t = String::new();
        let _ = err_pipe.read_to_string(&mut t);
        t
    });
    // qemu-img -p writes "(12.34/100%)" again and again on one line.
    let pct = Regex::new(r"\((\d+(?:\.\d+)?)/100%\)").unwrap();
    let mut buf = [0u8; 256];
    let mut tail = String::new();
    loop {
        if cancelled() {
            let _ = child.kill();
            let _ = child.wait();
            return Err("cancelled".into());
        }
        let n = out.read(&mut buf).unwrap_or(0);
        if n == 0 {
            break;
        }
        tail.push_str(&String::from_utf8_lossy(&buf[..n]));
        if let Some(v) = pct.captures_iter(&tail).last().and_then(|c| c[1].parse::<f64>().ok()) {
            progress(v / 100.0);
        }
        if tail.len() > 512 {
            tail = tail[tail.len() - 64..].to_string();
        }
    }
    let status = child.wait().map_err(|e| e.to_string())?;
    if status.success() { Ok(()) } else { Err(err_text.join().unwrap_or_default().trim().to_string()) }
}

// Makes the system; Ok(None) once it has started, or Ok(Some(note)) when it's ready but Linux can't run yet.
fn run(app: &AppHandle, probe: &Probe, vms: &Vms, p: &Plan) -> Result<Option<String>, String> {
    // What runs systems first: WINQ-EMU, when the page has asked to install it (Windows); QEMU has to be
    // installed with the distribution's packages (Linux); macOS has Apple's framework built in.
    if cfg!(windows) && vm::winq_bin().is_none() {
        if !p.install_winq {
            return Err("WINQ-EMU isn't installed.".into());
        }
        crate::setup::install_winq(&mut |status, value, detail| report(app, status, value, detail), &|| CANCEL.load(Ordering::Relaxed))?;
    }
    if cfg!(all(unix, not(target_os = "macos"))) && (vm::qemu_system().is_none() || vm::qemu_img().is_none()) {
        return Err(format!("QEMU isn't installed. Install it, then try again:\n\n{}", crate::setup::install_hint()));
    }
    // The installer first (it may take a while to download), unless a disk is being imported.
    let iso = if p.disk.is_none() { Some(installer(app, p)?) } else { None };
    cancelled()?;

    let parent = PathBuf::from(&p.location);
    let id = folder_name(&p.name, &parent);
    let dir = parent.join(&id);
    let disk = dir.join(config::disk_name(config::NATIVE, &id, 1));
    fs::create_dir_all(&dir).map_err(|e| format!("Couldn't make the folder {}: {e}", dir.to_string_lossy()))?;
    if let Some(from) = &p.disk {
        if let Err(e) = import(app, from, &disk, &p.name) {
            fs::remove_file(&disk).ok();
            fs::remove_dir(&dir).ok();
            return Err(e);
        }
    } else if config::NATIVE == config::Format::Mac {
        // macOS: a raw disk.img, as the Mac edition makes it: a sparse file of its full size, which takes space
        // on the drive only as Linux stores data.
        report(app, "Creating the virtual disk", -1.0, &format!("{} GiB", p.disk_gb));
        File::create(&disk)
            .and_then(|f| f.set_len(p.disk_gb.clamp(16, 2048) << 30))
            .map_err(|e| format!("Couldn't make the virtual disk: {e}"))?;
    } else {
        report(app, "Creating the virtual disk", -1.0, &format!("{} GiB{}", p.disk_gb, if p.disk_full { ", allocating its full size" } else { "" }));
        let size = format!("{}G", p.disk_gb.clamp(16, 2048));
        let disk_s = disk.to_string_lossy().to_string();
        let mut args = vec!["create", "-f", "qcow2"];
        if p.disk_full {
            args.extend(["-o", "preallocation=metadata"]);
        }
        args.extend([disk_s.as_str(), size.as_str()]);
        qemu_img(&args).map_err(|e| format!("Couldn't make the virtual disk: {e}"))?;
        if p.disk_full {
            clear_sparse(&disk)?;
        }
    }
    write_ini(&dir, p, iso.as_deref()).map_err(|e| format!("Couldn't save the system's settings: {e}"))?;
    remember(&dir).map_err(|e| format!("Couldn't remember the system's location: {e}"))?;
    crate::changed(app);
    // It's ready; it starts only once Linux can run (after a restart, or a BIOS/UEFI setting).
    let virt = crate::setup::virt();
    if virt != crate::setup::Virt::Ready {
        let restart = matches!(virt, crate::setup::Virt::FeatureOff | crate::setup::Virt::NotRunning);
        return Ok(Some(format!("{} is ready to {}. When Linux can run, open Try Linux and click {}.\n\n{}", p.name,
            if iso.is_some() { "install" } else { "start" }, if iso.is_some() { "Install" } else { "Start" },
            if restart { format!("Restart Windows to finish turning on Windows Hypervisor Platform.\n\n\
                If Try Linux still can't start it after restarting, hardware virtualization is probably turned off in your PC's BIOS/UEFI settings.\n\n{}", crate::setup::BIOS_STEPS) }
            else { virt.message() })));
    }
    report(app, if iso.is_some() { "Starting the installer" } else { "Starting it" }, -1.0, &p.name);
    let host: Host = probe.host();
    vm::start(app, vms, &host, &id, None, false).map_err(|e| format!("{}\n\n{}", e.title, e.text))?;
    Ok(None)
}

// ---------------------------------------------------------------- an existing disk

// What a disk tells about itself (format and size, from qemu-img) and the settings saved beside it by Try Linux
// (vm.ini, or a backup's .ini), VMware (.vmx) or VirtualBox (.vbox), for Use Your Own Installer.
#[derive(Serialize, Default)]
pub struct DiskSource {
    format: String,
    size: Option<u64>,
    name: String,
    description: String,
    ram_gb: u64,
    cores: u64,
    from: String,
}

#[tauri::command(async)]
pub fn read_disk(probe: State<Probe>, path: String) -> DiskSource {
    let mut s = DiskSource::default();
    if let Some(img) = vm::qemu_img() {
        let mut cmd = std::process::Command::new(img);
        cmd.args(["info", "--force-share", "--output=json", &path]);
        #[cfg(windows)]
        {
            use std::os::windows::process::CommandExt;
            cmd.creation_flags(0x0800_0000);
        }
        if let Ok(o) = cmd.output() {
            let v: serde_json::Value = serde_json::from_slice(&o.stdout).unwrap_or_default();
            s.format = v["format"].as_str().unwrap_or("").into();
            s.size = v["virtual-size"].as_u64();
        }
    }
    beside(&mut s, Path::new(&path), &probe.host());
    s
}

fn mb_to_gb(mb: u64) -> u64 {
    ((mb + 1023) / 1024).max(1)
}

fn beside(s: &mut DiskSource, disk: &Path, host: &Host) -> Option<()> {
    let dir = disk.parent()?;
    let file = disk.file_name()?.to_string_lossy().to_lowercase();
    // Try Linux (any edition): a backup's settings, or those of the system whose Disk 1 this is.
    let backup = PathBuf::from(format!("{}.ini", disk.to_string_lossy()));
    let own = VmConfig::load(dir).filter(|c| c.disk().file_name().is_some_and(|n| n.to_string_lossy().to_lowercase() == file));
    let found = if backup.exists() { VmConfig::load_ini(&backup, dir) } else { own };
    if let Some(c) = found {
        s.name = c.name();
        s.description = c.get("description").replace("\\n", "\n");
        let (n, gb) = c.resources(host);
        s.cores = n as u64;
        s.ram_gb = gb;
        s.from = "Try Linux".into();
        return Some(());
    }
    // VMware: displayName, annotation, memsize (MB) and numvcpus in a .vmx that names this disk.
    for e in fs::read_dir(dir).ok()?.flatten() {
        let p = e.path();
        if !p.extension().is_some_and(|x| x.eq_ignore_ascii_case("vmx")) {
            continue;
        }
        let Ok(text) = fs::read_to_string(&p) else { continue };
        if !text.to_lowercase().contains(&file) {
            continue;
        }
        let get = |key: &str| {
            let re = Regex::new(&format!(r#"(?mi)^\s*{}\s*=\s*"([^"]*)""#, regex::escape(key))).unwrap();
            re.captures(&text).map(|c| c[1].to_string()).unwrap_or_default()
        };
        s.name = get("displayName");
        s.description = get("annotation").replace("|0A", "\n").replace("|0D", "");
        s.ram_gb = get("memsize").parse().map(mb_to_gb).unwrap_or(0);
        s.cores = get("numvcpus").parse().unwrap_or(0);
        s.from = format!("VMware ({})", p.file_name()?.to_string_lossy());
        return Some(());
    }
    // VirtualBox: the machine's name, description, RAMSize (MB) and CPU count, in a .vbox beside it or one folder up.
    let disk_ref = Regex::new(r#"<HardDisk\b[^>]*\blocation="([^"]*)""#).unwrap();
    for folder in [Some(dir), dir.parent()].into_iter().flatten() {
        let Ok(list) = fs::read_dir(folder) else { continue };
        for e in list.flatten() {
            let p = e.path();
            if !p.extension().is_some_and(|x| x.eq_ignore_ascii_case("vbox")) {
                continue;
            }
            let Ok(text) = fs::read_to_string(&p) else { continue };
            if !disk_ref.captures_iter(&text).any(|c| c[1].to_lowercase().ends_with(&file)) {
                continue;
            }
            let attr = |tag: &str, a: &str| {
                Regex::new(&format!(r#"<{tag}\b[^>]*\b{a}="([^"]*)""#)).unwrap().captures(&text).map(|c| c[1].to_string())
            };
            let xml = |t: &str| t.replace("&#10;", "\n").replace("&quot;", "\"").replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&");
            s.name = xml(&attr("Machine", "name").unwrap_or_default());
            s.description = Regex::new(r"(?s)<Description>(.*?)</Description>").unwrap().captures(&text).map(|c| xml(c[1].trim())).unwrap_or_default();
            s.ram_gb = attr("Memory", "RAMSize").and_then(|v| v.parse().ok()).map(mb_to_gb).unwrap_or(0);
            s.cores = attr("CPU", "count").and_then(|v| v.parse().ok()).unwrap_or(1);
            s.from = format!("VirtualBox ({})", p.file_name()?.to_string_lossy());
            return Some(());
        }
    }
    None
}

// Makes the system and starts its installer; Err("cancelled") when Cancel stopped it. Off the main thread.
#[tauri::command(async)]
pub fn create(app: AppHandle, probe: State<Probe>, vms: State<Vms>, plan: Plan) -> Result<Option<String>, String> {
    CANCEL.store(false, Ordering::Relaxed);
    run(&app, &probe, &vms, &plan)
}

// Stops a download or check at its next step; what's downloaded so far is kept for next time.
#[tauri::command]
pub fn cancel_create() {
    CANCEL.store(true, Ordering::Relaxed);
}

// Finds the built-in five's current installers on their mirrors (listings and checksums only; nothing large).
// cargo test --release resolves_built_in -- --ignored --nocapture
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    #[ignore]
    fn resolves_built_in() {
        let d = |id: &str, base: &str, steps: &[&str], sum: &str| Distro {
            id: id.into(), name: id.into(), base: base.into(), steps: steps.iter().map(|s| s.to_string()).collect(), checksum: Some(sum.into()),
        };
        for x in [
            d("fedora", "https://mirrors.kernel.org/fedora/releases/", &[r"~^\d+/$", "Workstation/x86_64/iso/", r"~^Fedora-Workstation-Live-.*\.iso$"], "~CHECKSUM$"),
            d("ubuntu", "https://releases.ubuntu.com/", &[r"~^\d\d\.\d\d(\.\d+)?/$", r"~^ubuntu-.*-desktop-amd64\.iso$"], "SHA256SUMS"),
            d("debian", "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/", &[r"~^debian-live-.*-amd64-gnome\.iso$"], "SHA256SUMS"),
            d("mint", "https://mirrors.edge.kernel.org/linuxmint/stable/", &[r"~^\d+(\.\d+)?/$", r"~^linuxmint-.*-cinnamon-64bit\.iso$"], "sha256sum.txt"),
            d("zorin", "https://mirrors.edge.kernel.org/zorinos-isos/", &[r"~^\d+/$", r"~^Zorin-OS-(?<v>\d+(\.\d+)*)-Core-64-bit(-r\d+)?\.iso$"], "SHA256SUMS.txt"),
        ] {
            match resolve(&x) {
                Ok(r) => println!("{}\t{}\t{:?}\t{}", x.id, r.url, r.size, r.hash.map_or("no checksum".into(), |h| h[..12].to_string())),
                Err(e) => println!("{}\tERROR {e}", x.id),
            }
        }
    }
}

// Reads what an existing system's disk says about itself (read-only).
// TRYLINUX_TEST_DISK=path cargo test reads_disk -- --ignored --nocapture
#[cfg(test)]
mod disk_tests {
    #[test]
    #[ignore]
    fn reads_disk() {
        let path = std::env::var("TRYLINUX_TEST_DISK").unwrap();
        let host = crate::config::Host { threads: 32, ram_gb: 63 };
        let mut s = super::DiskSource::default();
        super::beside(&mut s, std::path::Path::new(&path), &host);
        println!("from={} name={} ram={} cores={} desc={:?}", s.from, s.name, s.ram_gb, s.cores, s.description);
    }
}

// The ARM64 built-ins (the Mac edition's four) found on their mirrors, as on an Apple silicon Mac.
// cargo test resolves_arm_built_in -- --ignored --nocapture
#[cfg(test)]
mod arm_tests {
    use super::*;
    #[test]
    #[ignore]
    fn resolves_arm_built_in() {
        let d = |id: &str, base: &str, steps: &[&str], sum: &str| Distro {
            id: id.into(), name: id.into(), base: base.into(), steps: steps.iter().map(|s| s.to_string()).collect(), checksum: Some(sum.into()),
        };
        for x in [
            d("fedora", "https://mirrors.kernel.org/fedora/releases/", &[r"~^\d+/$", "Workstation/aarch64/iso/", r"~^Fedora-Workstation-Live-.*\.aarch64\.iso$"], "~CHECKSUM$"),
            d("ubuntu", "https://cdimage.ubuntu.com/releases/", &[r"~^\d\d\.\d\d(\.\d+)?/$", "release/", r"~^ubuntu-.*-desktop-arm64\.iso$"], "SHA256SUMS"),
            d("debian", "https://cdimage.debian.org/debian-cd/current/arm64/iso-cd/", &[r"~^debian-.*-arm64-netinst\.iso$"], "SHA256SUMS"),
            d("opensuse", "https://download.opensuse.org/ports/aarch64/tumbleweed/iso/", &["openSUSE-Tumbleweed-KDE-Live-aarch64-Current.iso"], "{iso}.sha256"),
        ] {
            match resolve(&x) {
                Ok(r) => println!("{}\t{}\t{:?}\t{}", x.id, r.url, r.size, r.hash.map_or("no checksum".into(), |h| h[..12].to_string())),
                Err(e) => println!("{}\tERROR {e}", x.id),
            }
        }
    }
}
