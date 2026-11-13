// A running system's activity beyond CPU: the memory Linux itself is using (from the balloon device's
// statistics, which Linux's own virtio_balloon driver reports) and how fast its disks are read and written
// (QEMU's query-blockstats). The same readings as the Windows edition.

use crate::vm::qmp_query;
use serde_json::Value;
use std::collections::HashMap;
use std::io::{BufRead, BufReader, Write};
use std::net::TcpStream;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[derive(Default)]
struct Seen {
    disk_bytes: Option<(u64, Instant)>,
    balloon_asked: bool,
    guest: Guest,
    net_bytes: Option<(u64, Instant)>,
    asking: bool,
    asked: Option<Instant>,
}

// What QEMU's guest agent in Linux reports (qemu-guest-agent): its address, name and version, and how much it has
// sent and received. Asked every few seconds, off to the side, as an agent that isn't there only shows by not
// answering.
#[derive(Default, Clone)]
pub struct Guest {
    pub answered: bool,
    pub ip: Option<String>,
    pub host: Option<String>,
    pub os: Option<String>,
    pub kernel: Option<String>,
    pub net_total: Option<u64>,
    pub net_rate: Option<f64>,
}

#[derive(Default)]
pub struct Live(Arc<Mutex<HashMap<String, Seen>>>);

pub struct Reading {
    pub guest_used: Option<u64>,
    pub disk_rate: Option<f64>,
}

// QEMU reports statistics it doesn't know as 2^64 - 1.
fn stat(v: &serde_json::Value, key: &str) -> Option<u64> {
    v.get(key)?.as_u64().filter(|n| *n < u64::MAX / 2)
}

impl Live {
    pub fn read(&self, id: &str, port: u16) -> Reading {
        let mut map = self.0.lock().unwrap();
        let seen = map.entry(id.to_string()).or_default();

        // Linux's driver reports only once asked to, every couple of seconds.
        if !seen.balloon_asked {
            let set = qmp_query(port, "qom-set",
                Some(r#"{"path":"/machine/peripheral/balloon0","property":"guest-stats-polling-interval","value":2}"#));
            seen.balloon_asked = set.map(|v| v.get("return").is_some()).unwrap_or(false);
        }
        let guest_used = qmp_query(port, "qom-get", Some(r#"{"path":"/machine/peripheral/balloon0","property":"guest-stats"}"#))
            .ok()
            .and_then(|v| {
                let ret = v.get("return")?;
                if stat(ret, "last-update").unwrap_or(0) == 0 {
                    return None;
                }
                let s = ret.get("stats")?;
                let total = stat(s, "stat-total-memory")?;
                let avail = stat(s, "stat-available-memory").or_else(|| stat(s, "stat-free-memory"))?;
                Some(total.saturating_sub(avail))   // as Linux's "free" counts used
            });

        // Bytes read and written so far across its drives (each drive's own counts, not its layers').
        let now = Instant::now();
        let total = qmp_query(port, "query-blockstats", None).ok().and_then(|v| {
            Some(v.get("return")?.as_array()?.iter().filter_map(|d| {
                let s = d.get("stats")?;
                Some(s.get("rd_bytes")?.as_u64()? + s.get("wr_bytes")?.as_u64()?)
            }).sum::<u64>())
        });
        let disk_rate = match (total, seen.disk_bytes) {
            (Some(t), Some((before, at))) => {
                let secs = now.duration_since(at).as_secs_f64();
                (secs > 0.2).then(|| t.saturating_sub(before) as f64 / secs)
            }
            _ => None,
        };
        if let Some(t) = total {
            seen.disk_bytes = Some((t, now));
        }
        Reading { guest_used, disk_rate }
    }

    // The guest agent's latest report for a system, and a new question for it when the last is a few seconds old
    // (longer when it didn't answer). `dir` is its folder, where the distribution it reports is kept for the list.
    pub fn guest(&self, id: &str, agent: u16, dir: std::path::PathBuf) -> Guest {
        let mut map = self.0.lock().unwrap();
        let seen = map.entry(id.to_string()).or_default();
        let wait = if seen.guest.answered { 4 } else { 15 };
        if !seen.asking && seen.asked.is_none_or(|t| t.elapsed() > Duration::from_secs(wait)) {
            seen.asking = true;
            let (all, id) = (self.0.clone(), id.to_string());
            std::thread::spawn(move || {
                let report = ask_agent(agent);
                let mut map = all.lock().unwrap();
                let Some(seen) = map.get_mut(&id) else { return };
                seen.asking = false;
                seen.asked = Some(Instant::now());
                let Some(r) = report else {
                    seen.guest = Guest::default();
                    return;
                };
                let now = Instant::now();
                let net_rate = match (r.net_total, seen.net_bytes) {
                    (Some(t), Some((before, at))) => Some(t.saturating_sub(before) as f64 / now.duration_since(at).as_secs_f64().max(0.5)),
                    _ => None,
                };
                if let Some(t) = r.net_total {
                    seen.net_bytes = Some((t, now));
                }
                if let Some(os) = &r.os {
                    let file = dir.join("guest-os.txt");
                    if std::fs::read_to_string(&file).map(|t| t.trim() != os).unwrap_or(true) {
                        let _ = std::fs::write(file, os);
                    }
                }
                seen.guest = Guest { net_rate: net_rate.or(seen.guest.net_rate), ..r };
            });
        }
        seen.guest.clone()
    }

    pub fn forget(&self, id: &str) {
        self.0.lock().unwrap().remove(id);
    }
}

// The control port QEMU was started with ("-qmp tcp:127.0.0.1:PORT,..."; the first one), for a system another
// copy of Try Linux started.
pub fn port_from_args(args: &[String]) -> Option<u16> {
    args.windows(2)
        .find(|w| w[0] == "-qmp")
        .and_then(|w| w[1].strip_prefix("tcp:127.0.0.1:"))
        .and_then(|rest| rest.split(',').next())
        .and_then(|p| p.parse().ok())
        .or_else(|| {
            // One string (as the Windows edition passes them): find it in the joined command line.
            let joined = args.join(" ");
            let i = joined.find("-qmp tcp:127.0.0.1:")? + "-qmp tcp:127.0.0.1:".len();
            joined[i..].split(|c: char| !c.is_ascii_digit()).next()?.parse().ok()
        })
}

// The guest agent's port ("-chardev socket,id=qga0,host=127.0.0.1,port=PORT,..."), from QEMU's command line.
pub fn agent_port_from_args(args: &[String]) -> Option<u16> {
    let joined = args.join(" ");
    let i = joined.find("id=qga0,")?;
    let rest = &joined[i..];
    let p = rest.find("port=")? + 5;
    rest[p..].split(|c: char| !c.is_ascii_digit()).next()?.parse().ok()
}

// One round of questions to the guest agent: None when it doesn't answer (not installed in Linux, or not started).
fn ask_agent(port: u16) -> Option<Guest> {
    let addr = std::net::SocketAddr::from(([127, 0, 0, 1], port));
    let stream = TcpStream::connect_timeout(&addr, Duration::from_millis(500)).ok()?;
    stream.set_read_timeout(Some(Duration::from_millis(1500))).ok()?;
    let mut w = stream.try_clone().ok()?;
    let mut r = BufReader::new(stream);
    // guest-sync: anything left over from before is skipped until its answer, with this number, comes back.
    let sync = 0x7472_6c78u32;
    w.write_all(format!("{{\"execute\":\"guest-sync\",\"arguments\":{{\"id\":{sync}}}}}\n").as_bytes()).ok()?;
    let mut line = String::new();
    loop {
        line.clear();
        if r.read_line(&mut line).ok()? == 0 {
            return None;
        }
        if line.contains(&sync.to_string()) {
            break;
        }
    }
    let mut ask = |cmd: &str| -> Option<Value> {
        w.write_all(format!("{{\"execute\":\"{cmd}\"}}\n").as_bytes()).ok()?;
        let mut line = String::new();
        r.read_line(&mut line).ok()?;
        serde_json::from_str::<Value>(line.trim_start_matches('\u{ff}')).ok()?.get("return").cloned()
    };
    let nics = ask("guest-network-get-interfaces").unwrap_or_default();
    let host = ask("guest-get-host-name").and_then(|v| v["host-name"].as_str().map(String::from));
    let os = ask("guest-get-osinfo");
    let real = nics.as_array().map(|a| a.iter().filter(|n| n["name"].as_str() != Some("lo")).cloned().collect::<Vec<_>>()).unwrap_or_default();
    let ip = real.iter().flat_map(|n| n["ip-addresses"].as_array().cloned().unwrap_or_default())
        .find(|a| a["ip-address-type"].as_str() == Some("ipv4"))
        .and_then(|a| a["ip-address"].as_str().map(String::from));
    let stats: Vec<u64> = real.iter().filter_map(|n| {
        let s = n.get("statistics")?;
        Some(s["rx-bytes"].as_u64()? + s["tx-bytes"].as_u64()?)
    }).collect();
    let name = os.as_ref().and_then(|o| {
        let n = o["name"].as_str()?;
        Some(match o["version-id"].as_str() { Some(v) if !n.contains(v) => format!("{n} {v}"), _ => n.to_string() })
    });
    Some(Guest {
        answered: true,
        ip,
        host,
        kernel: os.as_ref().and_then(|o| o["kernel-release"].as_str().map(String::from)),
        os: name,
        net_total: (!stats.is_empty()).then(|| stats.iter().sum()),
        net_rate: None,
    })
}

// The Mac: a system's address from the Mac's DHCP leases (/var/db/dhcpd_leases), by its network card's address.
#[cfg(target_os = "macos")]
pub fn mac_ip(mac: &str) -> Option<String> {
    let norm = |a: &str| a.split(':').map(|o| o.trim_start_matches('0').to_lowercase()).collect::<Vec<_>>();
    let want = norm(mac);
    let text = std::fs::read_to_string("/var/db/dhcpd_leases").ok()?;
    text.split('{').find_map(|entry| {
        let get = |k: &str| entry.lines().find_map(|l| l.trim().strip_prefix(k).map(|v| v.trim().to_string()));
        let hw = get("hw_address=")?;
        let hw = hw.split_once(',').map(|(_, a)| a.to_string()).unwrap_or(hw);
        if norm(&hw) == want { get("ip_address=") } else { None }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn finds_the_agent_port() {
        let args: Vec<String> = ["-qmp", "tcp:127.0.0.1:1,server=on", "-chardev", "socket,id=qga0,host=127.0.0.1,port=39813,server=on,wait=off"]
            .iter().map(|s| s.to_string()).collect();
        assert_eq!(agent_port_from_args(&args), Some(39813));
        assert_eq!(port_from_args(&args), Some(1));
    }
}
