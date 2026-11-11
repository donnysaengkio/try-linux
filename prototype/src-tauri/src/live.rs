// A running system's activity beyond CPU: the memory Linux itself is using (from the balloon device's
// statistics, which Linux's own virtio_balloon driver reports) and how fast its disks are read and written
// (QEMU's query-blockstats). The same readings as the Windows edition.

use crate::vm::qmp_query;
use std::collections::HashMap;
use std::sync::Mutex;
use std::time::Instant;

#[derive(Default)]
struct Seen {
    disk_bytes: Option<(u64, Instant)>,
    balloon_asked: bool,
}

#[derive(Default)]
pub struct Live(Mutex<HashMap<String, Seen>>);

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
