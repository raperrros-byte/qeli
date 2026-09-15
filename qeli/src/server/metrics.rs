//! Lightweight host + tunnel metrics for the web-panel dashboard (Tier-1
//! observability).
//!
//! A background sampler — spawned once by the supervisor — reads cheap `/proc`
//! counters and the data-plane worker's aggregate byte totals (over the control
//! socket) once a second, derives the quantities that need deltas (throughput,
//! CPU%), and keeps a short in-memory ring buffer. The panel polls
//! `GET /api/system` (latest host snapshot) and `GET /api/metrics` (the ring
//! buffer for the chart). No external deps, no disk, no per-request /proc parsing.

use serde::Serialize;
use serde_json::{json, Value};
use std::collections::VecDeque;
use std::sync::atomic::{AtomicI32, Ordering};
use std::sync::Arc;
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
use tokio::sync::Mutex;

/// 1 Hz sampling; keep 5 minutes of history (= the chart window).
const SAMPLE_INTERVAL: Duration = Duration::from_secs(1);
const HISTORY_LEN: usize = 300;

/// One point in the throughput / CPU / mem history (kept compact — the chart
/// transfers many of these on every poll).
#[derive(Clone, Serialize)]
pub struct Point {
    pub t: u64,
    pub up_mbps: f64,   // client -> server aggregate (tunnel upload)
    pub down_mbps: f64, // server -> client aggregate (tunnel download)
    pub cpu_pct: f64,
    pub mem_pct: f64,
    pub clients: usize,
}

/// Previous-sample counters needed to turn monotonic totals into rates.
#[derive(Default)]
struct Prev {
    cpu: Option<(u64, u64)>,   // (busy, total) jiffies
    proc_cpu: Option<u64>,     // worker (utime + stime) jiffies
    net: Option<(u64, u64)>,   // host (rx, tx) bytes
    bytes: Option<(u64, u64)>, // tunnel aggregate (sent, recv) bytes
    at: Option<Instant>,
}

/// Shared metrics state. Lives in `ServerState`; only the supervisor's sampler
/// writes it, the panel handlers read it.
pub struct MetricsState {
    history: Mutex<VecDeque<Point>>,
    latest: Mutex<Value>,
    prev: Mutex<Prev>,
    /// Data-plane worker pid (set by the supervise loop on each (re)spawn).
    pub worker_pid: AtomicI32,
}

impl MetricsState {
    pub fn new() -> Self {
        MetricsState {
            history: Mutex::new(VecDeque::with_capacity(HISTORY_LEN)),
            latest: Mutex::new(json!({"ok": true, "warming_up": true})),
            prev: Mutex::new(Prev::default()),
            worker_pid: AtomicI32::new(0),
        }
    }

    /// Latest host + tunnel snapshot (for `GET /api/system`).
    pub async fn latest_json(&self) -> Value {
        self.latest.lock().await.clone()
    }

    /// Throughput / CPU / mem ring buffer (for the dashboard chart).
    pub async fn history_json(&self) -> Value {
        let h = self.history.lock().await;
        json!({ "ok": true, "points": h.iter().cloned().collect::<Vec<_>>() })
    }
}

impl Default for MetricsState {
    fn default() -> Self {
        Self::new()
    }
}

/// Spawned by the supervisor: sample once a second forever.
///
/// `tun_names` lists the tunnel interfaces configured on this server, so the WAN counters
/// can exclude them by NAME. See `net_bytes`.
pub async fn run_sampler(metrics: Arc<MetricsState>, tun_names: Vec<String>) {
    let mut tick = tokio::time::interval(SAMPLE_INTERVAL);
    tick.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Skip);
    loop {
        tick.tick().await;
        sample_once(&metrics, &tun_names).await;
    }
}

async fn sample_once(m: &MetricsState, tun_names: &[String]) {
    let now = Instant::now();
    let cpu_now = cpu_times();
    let mem = mem_info();
    let load = loadavg();
    let net_now = net_bytes(tun_names);
    let pid = m.worker_pid.load(Ordering::Relaxed);
    let proc_now = proc_stat(pid); // (cpu_jiffies, rss_bytes)
    let (agg_sent, agg_recv, clients) = tunnel_aggregate().await.unwrap_or((0, 0, 0));
    let (tcp, udp) = count_conns(pid);
    let cores = cpu_cores();

    let mut prev = m.prev.lock().await;
    let dt = prev
        .at
        .map(|p| now.duration_since(p).as_secs_f64())
        .filter(|&d| d > 0.05)
        .unwrap_or(1.0);

    let cpu_pct = match (prev.cpu, cpu_now) {
        (Some((pb, pt)), Some((b, t))) if t > pt => (b - pb) as f64 / (t - pt) as f64 * 100.0,
        _ => 0.0,
    };
    let proc_pct = match (prev.proc_cpu, proc_now, prev.cpu, cpu_now) {
        (Some(pp), Some((c, _)), Some((_, pt)), Some((_, t))) if t > pt && c >= pp => {
            // /proc/stat is the aggregate of all CPUs. Convert that fraction to the
            // conventional top(1) process scale where one saturated worker is 100%
            // and a process using N full cores may report N * 100%.
            (c - pp) as f64 / (t - pt) as f64 * cores as f64 * 100.0
        }
        _ => 0.0,
    };
    let (rx_mbps, tx_mbps) = match (prev.net, net_now) {
        (Some((prx, ptx)), Some((rx, tx))) => (
            rx.saturating_sub(prx) as f64 * 8.0 / 1e6 / dt,
            tx.saturating_sub(ptx) as f64 * 8.0 / 1e6 / dt,
        ),
        _ => (0.0, 0.0),
    };
    let (up_mbps, down_mbps) = match prev.bytes {
        Some((ps, pr)) => (
            agg_recv.saturating_sub(pr) as f64 * 8.0 / 1e6 / dt,
            agg_sent.saturating_sub(ps) as f64 * 8.0 / 1e6 / dt,
        ),
        None => (0.0, 0.0),
    };

    prev.cpu = cpu_now;
    prev.proc_cpu = proc_now.map(|(c, _)| c);
    prev.net = net_now;
    prev.bytes = Some((agg_sent, agg_recv));
    prev.at = Some(now);
    drop(prev);

    let t = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs())
        .unwrap_or(0);
    let (mem_used, mem_total) = mem.unwrap_or((0, 0));
    let mem_pct = if mem_total > 0 {
        mem_used as f64 / mem_total as f64 * 100.0
    } else {
        0.0
    };

    let point = Point {
        t,
        up_mbps: round1(up_mbps),
        down_mbps: round1(down_mbps),
        cpu_pct: round1(cpu_pct),
        mem_pct: round1(mem_pct),
        clients,
    };
    {
        let mut h = m.history.lock().await;
        if h.len() >= HISTORY_LEN {
            h.pop_front();
        }
        h.push_back(point);
    }

    let (l1, l5, l15) = load.unwrap_or((0.0, 0.0, 0.0));
    let proc_rss = proc_now.map(|(_, r)| r).unwrap_or(0);
    let snap = json!({
        "ok": true,
        "t": t,
        "uptime_secs": uptime_secs().unwrap_or(0),
        "cpu_pct": round1(cpu_pct),
        "cores": cores,
        "mem_used": mem_used,
        "mem_total": mem_total,
        "mem_pct": round1(mem_pct),
        "load": [round2(l1), round2(l5), round2(l15)],
        "disk_pct": disk_pct("/").map(round1),
        "proc": { "pid": pid, "cpu_pct": round1(proc_pct), "rss_bytes": proc_rss },
        "net": { "rx_mbps": round1(rx_mbps), "tx_mbps": round1(tx_mbps) },
        "conns": { "tcp": tcp, "udp": udp },
        "up_mbps": round1(up_mbps),
        "down_mbps": round1(down_mbps),
        "clients": clients,
    });
    *m.latest.lock().await = snap;
}

// ── /proc + syscall readers (all best-effort; None on any parse failure) ────

fn read_first_line(path: &str) -> Option<String> {
    std::fs::read_to_string(path)
        .ok()?
        .lines()
        .next()
        .map(|s| s.to_string())
}

/// (busy, total) jiffies from the aggregate `cpu` line of /proc/stat.
fn cpu_times() -> Option<(u64, u64)> {
    let line = read_first_line("/proc/stat")?;
    let mut it = line.split_whitespace();
    if it.next()? != "cpu" {
        return None;
    }
    let vals: Vec<u64> = it.filter_map(|x| x.parse().ok()).collect();
    if vals.len() < 4 {
        return None;
    }
    let total: u64 = vals.iter().sum();
    let idle = vals[3] + vals.get(4).copied().unwrap_or(0); // idle + iowait
    Some((total.saturating_sub(idle), total))
}

/// Number of logical CPUs (count of `cpuN` lines in /proc/stat).
fn cpu_cores() -> usize {
    std::fs::read_to_string("/proc/stat")
        .map(|s| {
            s.lines()
                .filter(|l| {
                    l.starts_with("cpu")
                        && l.as_bytes().get(3).map(u8::is_ascii_digit).unwrap_or(false)
                })
                .count()
        })
        .ok()
        .filter(|&n| n > 0)
        .unwrap_or(1)
}

/// (used_bytes, total_bytes) from /proc/meminfo (used = total − available).
fn mem_info() -> Option<(u64, u64)> {
    let s = std::fs::read_to_string("/proc/meminfo").ok()?;
    let mut total = 0u64;
    let mut avail = 0u64;
    for l in s.lines() {
        if let Some(v) = l.strip_prefix("MemTotal:") {
            total = v.split_whitespace().next()?.parse().ok()?;
        } else if let Some(v) = l.strip_prefix("MemAvailable:") {
            avail = v.split_whitespace().next()?.parse().ok()?;
        }
    }
    if total == 0 {
        return None;
    }
    let total_b = total * 1024;
    Some((total_b.saturating_sub(avail * 1024), total_b))
}

fn loadavg() -> Option<(f64, f64, f64)> {
    let l = read_first_line("/proc/loadavg")?;
    let mut it = l.split_whitespace();
    Some((
        it.next()?.parse().ok()?,
        it.next()?.parse().ok()?,
        it.next()?.parse().ok()?,
    ))
}

fn uptime_secs() -> Option<u64> {
    let l = read_first_line("/proc/uptime")?;
    let f: f64 = l.split_whitespace().next()?.parse().ok()?;
    Some(f as u64)
}

/// Used-space percentage of the filesystem holding `path`, via statvfs(3).
fn disk_pct(path: &str) -> Option<f64> {
    use std::ffi::CString;
    let c = CString::new(path).ok()?;
    let mut s: libc::statvfs = unsafe { std::mem::zeroed() };
    if unsafe { libc::statvfs(c.as_ptr(), &mut s) } != 0 {
        return None;
    }
    let frsize = s.f_frsize as f64;
    let total = s.f_blocks as f64 * frsize;
    let avail = s.f_bavail as f64 * frsize;
    if total <= 0.0 {
        return None;
    }
    Some(((total - avail) / total * 100.0).clamp(0.0, 100.0))
}

/// Sum (rx, tx) bytes of physical-ish interfaces from /proc/net/dev — skips
/// loopback and the VPN's own tun interfaces (we want the WAN load, not the
/// tunnel doubled).
///
/// Exclusion is by CONFIGURED name (`tun_names`, from each profile's `tun.name`), with
/// the historical `vpn*`/`tun*`/`qtest*` prefixes kept as a fallback for interfaces this
/// process did not create. `tun.name` is a config key, so a profile named `qeli0` matched
/// none of those prefixes and its entire tunnel volume was counted as WAN — the dashboard
/// then showed roughly double the real load, on every graph an operator uses to judge
/// capacity. (Audit 2026-07-27, S4.)
fn net_bytes(tun_names: &[String]) -> Option<(u64, u64)> {
    let s = std::fs::read_to_string("/proc/net/dev").ok()?;
    let mut rx = 0u64;
    let mut tx = 0u64;
    for line in s.lines() {
        if let Some((name, rest)) = line.split_once(':') {
            let name = name.trim();
            if name == "lo"
                || tun_names.iter().any(|t| t == name)
                || name.starts_with("vpn")
                || name.starts_with("tun")
                || name.starts_with("qtest")
            {
                continue;
            }
            let f: Vec<u64> = rest
                .split_whitespace()
                .filter_map(|x| x.parse().ok())
                .collect();
            if f.len() >= 9 {
                rx += f[0];
                tx += f[8];
            }
        }
    }
    Some((rx, tx))
}

/// (cpu_jiffies = utime+stime, rss_bytes) for `pid`, or None.
fn proc_stat(pid: i32) -> Option<(u64, u64)> {
    if pid <= 0 {
        return None;
    }
    let stat = std::fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    // The comm field (2nd) may contain spaces/parens — split after the LAST ')'.
    let rest = &stat[stat.rfind(')')? + 1..];
    let f: Vec<&str> = rest.split_whitespace().collect();
    // After comm: state(0) ppid(1) pgrp(2) session(3) tty(4) tpgid(5) flags(6)
    // minflt(7) cminflt(8) majflt(9) cmajflt(10) utime(11) stime(12) ...
    let utime: u64 = f.get(11)?.parse().ok()?;
    let stime: u64 = f.get(12)?.parse().ok()?;
    let statm = std::fs::read_to_string(format!("/proc/{pid}/statm")).ok()?;
    let rss_pages: u64 = statm.split_whitespace().nth(1)?.parse().ok()?;
    let page = unsafe { libc::sysconf(libc::_SC_PAGESIZE) }.max(4096) as u64;
    Some((utime + stime, rss_pages * page))
}

/// Socket inode numbers owned by one process. `/proc/<pid>/net/*` is network-namespace
/// wide, not process-local; joining it with the fd symlinks is what keeps unrelated host
/// services out of the qeli dashboard.
fn process_socket_inodes(pid: i32) -> std::collections::HashSet<u64> {
    if pid <= 0 {
        return std::collections::HashSet::new();
    }
    let Ok(entries) = std::fs::read_dir(format!("/proc/{pid}/fd")) else {
        return std::collections::HashSet::new();
    };
    entries
        .flatten()
        .filter_map(|entry| std::fs::read_link(entry.path()).ok())
        .filter_map(|target| {
            let text = target.to_string_lossy();
            text.strip_prefix("socket:[")
                .and_then(|value| value.strip_suffix(']'))
                .and_then(|value| value.parse().ok())
        })
        .collect()
}

fn count_socket_lines(
    text: &str,
    inodes: &std::collections::HashSet<u64>,
    established_only: bool,
) -> u64 {
    text.lines()
        .skip(1)
        .filter(|line| {
            let fields: Vec<&str> = line.split_whitespace().collect();
            if established_only && fields.get(3) != Some(&"01") {
                return false;
            }
            fields
                .get(9)
                .and_then(|inode| inode.parse::<u64>().ok())
                .is_some_and(|inode| inodes.contains(&inode))
        })
        .count() as u64
}

/// (established TCP, total UDP) sockets owned by the data-plane worker.
fn count_conns(pid: i32) -> (u64, u64) {
    let inodes = process_socket_inodes(pid);
    let count = |path: &str, established_only: bool| -> u64 {
        std::fs::read_to_string(path)
            .map(|text| count_socket_lines(&text, &inodes, established_only))
            .unwrap_or(0)
    };
    (
        count("/proc/net/tcp", true) + count("/proc/net/tcp6", true),
        count("/proc/net/udp", false) + count("/proc/net/udp6", false),
    )
}

/// Aggregate (bytes_sent, bytes_recv, client_count) across all live sessions,
/// read from the data-plane worker over the control socket.
async fn tunnel_aggregate() -> Option<(u64, u64, usize)> {
    let reply = crate::server::control::send_command(
        &crate::server::control::control_socket_path(),
        &json!({ "cmd": "list-clients" }).to_string(),
    )
    .await
    .ok()?;
    let v: Value = serde_json::from_str(&reply).ok()?;
    let clients = v.get("clients")?.as_array()?;
    let mut sent = 0u64;
    let mut recv = 0u64;
    for c in clients {
        sent += c.get("bytes_sent").and_then(Value::as_u64).unwrap_or(0);
        recv += c.get("bytes_recv").and_then(Value::as_u64).unwrap_or(0);
    }
    Some((sent, recv, clients.len()))
}

fn round1(x: f64) -> f64 {
    (x * 10.0).round() / 10.0
}
fn round2(x: f64) -> f64 {
    (x * 100.0).round() / 100.0
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn socket_count_is_process_scoped_and_tcp_state_aware() {
        let table = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n\
                     0: 00000000:01BB 00000000:0000 01 0:0 00:0 0 1000 0 111\n\
                     1: 00000000:01BC 00000000:0000 0A 0:0 00:0 0 1000 0 222\n\
                     2: 00000000:01BD 00000000:0000 01 0:0 00:0 0 1000 0 333\n";
        let owned = [111_u64, 222].into_iter().collect();
        assert_eq!(count_socket_lines(table, &owned, true), 1);
        assert_eq!(count_socket_lines(table, &owned, false), 2);
    }
}
