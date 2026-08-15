//! Auto transport selection: probe sibling client profiles and pick a working
//! masking mode (plain ⇄ fake-tls ⇄ reality-tls ⇄ obfs ⇄ QUIC), then fail over
//! when the current path is cut.
//!
//! Preference order (lower = preferred when RTT is similar):
//!   reality-tls → fake-tls → obfs → plain → udp/QUIC
//!
//! Clients may override via `auto_transport_order` (comma-separated labels).

use std::cmp::Ordering;
use std::net::{SocketAddr, TcpStream, ToSocketAddrs};
use std::time::{Duration, Instant};

use super::client_profiles::ClientProfileSpec;
use super::share::ClientLink;

/// Default preference labels used when ranking probed candidates.
pub const DEFAULT_ORDER: &[&str] = &[
    "reality-tls",
    "fake-tls",
    "obfs",
    "plain",
    "quic",
    "udp-quic",
    "udp-obfs",
    "udp",
];

/// Built-in SNI / TLS front-host presets (kept in sync with `protocol::tls::DEFAULT_SNI_POOL`).
pub const SNI_PRESETS: &[&str] = &[
    "www.cloudflare.com",
    "www.google.com",
    "www.microsoft.com",
    "www.apple.com",
    "www.amazon.com",
];

/// One connectable transport candidate extracted from a multi-profile bundle.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TransportCandidate {
    pub name: String,
    pub host: String,
    pub port: u16,
    pub protocol: String,
    pub mode: String,
    pub quic: bool,
    pub sni: Option<String>,
    /// Original profile body (`[qeli]` INI or `qeli://` link).
    pub body: String,
}

/// Result of a reachability probe against one candidate.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProbeOutcome {
    pub name: String,
    pub ok: bool,
    pub rtt_ms: Option<u64>,
    pub error: Option<String>,
}

/// Ranked pick ready for connect / failover.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RankedPick {
    pub index: usize,
    pub candidate: TransportCandidate,
    pub rtt_ms: Option<u64>,
    pub preference: u32,
}

/// Canonical transport label for UI / ranking (mode + quic/udp aliases).
pub fn transport_label(protocol: &str, mode: &str, quic: bool) -> String {
    let proto = protocol.trim().to_ascii_lowercase();
    let mode = mode.trim().to_ascii_lowercase();
    if proto == "udp" {
        if quic || mode == "udp-quic" {
            return "quic".into();
        }
        if mode == "obfs" || mode == "udp-obfs" {
            return "udp-obfs".into();
        }
        return "udp".into();
    }
    match mode.as_str() {
        "reality-tls" | "reality" => "reality-tls".into(),
        "obfs" => "obfs".into(),
        "plain" => "plain".into(),
        "fake-tls" | "tls" | "" => "fake-tls".into(),
        other => other.to_string(),
    }
}

/// Preference rank: lower is better. Unknown labels sort after known ones.
pub fn preference_rank(label: &str, order: &[&str]) -> u32 {
    let needle = label.trim().to_ascii_lowercase();
    order
        .iter()
        .position(|item| item.eq_ignore_ascii_case(&needle))
        .map(|i| i as u32)
        .unwrap_or(900 + needle.len() as u32)
}

/// Parse `auto_transport_order = a,b,c` into owned labels (empty → default).
pub fn parse_order(raw: Option<&str>) -> Vec<String> {
    let Some(raw) = raw.map(str::trim).filter(|s| !s.is_empty()) else {
        return DEFAULT_ORDER.iter().map(|s| (*s).to_string()).collect();
    };
    let parsed: Vec<String> = raw
        .split(',')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(|s| s.to_ascii_lowercase())
        .collect();
    if parsed.is_empty() {
        DEFAULT_ORDER.iter().map(|s| (*s).to_string()).collect()
    } else {
        parsed
    }
}

/// Build candidates from a multi-profile client bundle (INI or qeli:// lines).
pub fn candidates_from_bundle(source: &str) -> anyhow::Result<Vec<TransportCandidate>> {
    let profiles = super::client_profiles::split_client_profiles(source)?;
    let mut out = Vec::with_capacity(profiles.len());
    for profile in profiles {
        out.push(candidate_from_profile(&profile)?);
    }
    Ok(out)
}

fn candidate_from_profile(profile: &ClientProfileSpec) -> anyhow::Result<TransportCandidate> {
    let body = profile.body.trim();
    if body.starts_with("qeli://") {
        let link = ClientLink::from_uri(body)?;
        return Ok(TransportCandidate {
            name: profile.name.clone(),
            host: link.host.clone(),
            port: link.port,
            protocol: if link.proto.is_empty() {
                "tcp".into()
            } else {
                link.proto.clone()
            },
            mode: if link.mode.is_empty() {
                "fake-tls".into()
            } else {
                link.mode.clone()
            },
            quic: link.quic,
            sni: link.sni.clone(),
            body: body.to_string(),
        });
    }

    // Flat / [qeli] INI — parse via ClientConfig so aliases stay consistent.
    let doc = super::format::IniDoc::parse(body)?;
    let cfg = super::client::ClientConfig::from_ini(&doc)?;
    Ok(TransportCandidate {
        name: profile.name.clone(),
        host: cfg.server.address.clone(),
        port: cfg.server.port,
        protocol: cfg.server.protocol.clone(),
        mode: cfg.obfuscation.mode.clone(),
        quic: cfg.obfuscation.quic.enabled,
        sni: cfg.obfuscation.sni.clone(),
        body: body.to_string(),
    })
}

/// TCP connect probe (UDP candidates are marked unreachable here — callers that
/// own a UDP handshake probe should fill those outcomes themselves).
pub fn probe_tcp(candidate: &TransportCandidate, timeout: Duration) -> ProbeOutcome {
    if candidate.protocol.eq_ignore_ascii_case("udp") {
        return ProbeOutcome {
            name: candidate.name.clone(),
            ok: false,
            rtt_ms: None,
            error: Some("udp requires native handshake probe".into()),
        };
    }
    let target = format!("{}:{}", candidate.host, candidate.port);
    let start = Instant::now();
    match resolve_and_connect(&target, timeout) {
        Ok(()) => ProbeOutcome {
            name: candidate.name.clone(),
            ok: true,
            rtt_ms: Some(start.elapsed().as_millis().min(u128::from(u64::MAX)) as u64),
            error: None,
        },
        Err(error) => ProbeOutcome {
            name: candidate.name.clone(),
            ok: false,
            rtt_ms: None,
            error: Some(error),
        },
    }
}

fn resolve_and_connect(target: &str, timeout: Duration) -> Result<(), String> {
    let addrs: Vec<SocketAddr> = target
        .to_socket_addrs()
        .map_err(|e| format!("resolve: {e}"))?
        .collect();
    if addrs.is_empty() {
        return Err("no addresses".into());
    }
    let mut last = "connect failed".to_string();
    for addr in addrs {
        match TcpStream::connect_timeout(&addr, timeout) {
            Ok(stream) => {
                let _ = stream.shutdown(std::net::Shutdown::Both);
                return Ok(());
            }
            Err(e) => last = e.to_string(),
        }
    }
    Err(last)
}

/// Probe every candidate with a TCP connect; UDP rows stay failed unless the
/// caller replaces them.
pub fn probe_all(candidates: &[TransportCandidate], timeout: Duration) -> Vec<ProbeOutcome> {
    candidates
        .iter()
        .map(|c| probe_tcp(c, timeout))
        .collect()
}

/// Rank working candidates: reachable first, then lower RTT, then preference.
pub fn rank(
    candidates: &[TransportCandidate],
    outcomes: &[ProbeOutcome],
    order: &[&str],
) -> Vec<RankedPick> {
    let mut picks = Vec::new();
    for (index, candidate) in candidates.iter().enumerate() {
        let outcome = outcomes.get(index);
        let ok = outcome.map(|o| o.ok).unwrap_or(false);
        if !ok {
            continue;
        }
        let label = transport_label(&candidate.protocol, &candidate.mode, candidate.quic);
        picks.push(RankedPick {
            index,
            candidate: candidate.clone(),
            rtt_ms: outcome.and_then(|o| o.rtt_ms),
            preference: preference_rank(&label, order),
        });
    }
    picks.sort_by(|a, b| {
        match (a.rtt_ms, b.rtt_ms) {
            (Some(ra), Some(rb)) => ra
                .cmp(&rb)
                .then_with(|| a.preference.cmp(&b.preference))
                .then_with(|| a.index.cmp(&b.index)),
            (Some(_), None) => Ordering::Less,
            (None, Some(_)) => Ordering::Greater,
            (None, None) => a
                .preference
                .cmp(&b.preference)
                .then_with(|| a.index.cmp(&b.index)),
        }
    });
    // Soft RTT banding: within 40ms prefer masking preference over tiny RTT noise.
    picks.sort_by(|a, b| {
        let ra = a.rtt_ms.unwrap_or(u64::MAX);
        let rb = b.rtt_ms.unwrap_or(u64::MAX);
        let band_a = ra / 40;
        let band_b = rb / 40;
        band_a
            .cmp(&band_b)
            .then_with(|| a.preference.cmp(&b.preference))
            .then_with(|| ra.cmp(&rb))
            .then_with(|| a.index.cmp(&b.index))
    });
    picks
}

/// Next failover candidate after `current_index` in the ranked list (wraps once).
pub fn next_failover(ranked: &[RankedPick], current_index: usize) -> Option<&RankedPick> {
    if ranked.len() < 2 {
        return None;
    }
    let pos = ranked.iter().position(|p| p.index == current_index)?;
    ranked.get((pos + 1) % ranked.len()).filter(|p| p.index != current_index)
}

/// Apply an SNI override into a flat-INI or qeli:// profile body.
pub fn apply_sni_override(body: &str, sni: &str) -> anyhow::Result<String> {
    let sni = sni.trim();
    if sni.is_empty() {
        anyhow::bail!("sni must not be empty");
    }
    let trimmed = body.trim();
    if trimmed.starts_with("qeli://") {
        let mut link = ClientLink::from_uri(trimmed)?;
        link.sni = Some(sni.to_string());
        return Ok(link.to_uri());
    }
    let mut doc = super::format::IniDoc::parse(trimmed)?;
    let q = doc.section_mut("qeli").ok_or_else(|| {
        anyhow::anyhow!("profile body has no [qeli] section")
    })?;
    q.set("sni", sni);
    Ok(doc.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn labels_cover_requested_modes() {
        assert_eq!(transport_label("tcp", "reality-tls", false), "reality-tls");
        assert_eq!(transport_label("tcp", "fake-tls", false), "fake-tls");
        assert_eq!(transport_label("tcp", "obfs", false), "obfs");
        assert_eq!(transport_label("tcp", "plain", false), "plain");
        assert_eq!(transport_label("udp", "fake-tls", true), "quic");
    }

    #[test]
    fn preference_follows_default_order() {
        assert!(preference_rank("reality-tls", DEFAULT_ORDER) < preference_rank("plain", DEFAULT_ORDER));
        assert!(preference_rank("fake-tls", DEFAULT_ORDER) < preference_rank("obfs", DEFAULT_ORDER));
        assert!(preference_rank("obfs", DEFAULT_ORDER) < preference_rank("quic", DEFAULT_ORDER));
    }

    #[test]
    fn rank_prefers_faster_then_masking() {
        let cands = vec![
            TransportCandidate {
                name: "a".into(),
                host: "h".into(),
                port: 1,
                protocol: "tcp".into(),
                mode: "plain".into(),
                quic: false,
                sni: None,
                body: String::new(),
            },
            TransportCandidate {
                name: "b".into(),
                host: "h".into(),
                port: 2,
                protocol: "tcp".into(),
                mode: "reality-tls".into(),
                quic: false,
                sni: None,
                body: String::new(),
            },
        ];
        let outs = vec![
            ProbeOutcome {
                name: "a".into(),
                ok: true,
                rtt_ms: Some(12),
                error: None,
            },
            ProbeOutcome {
                name: "b".into(),
                ok: true,
                rtt_ms: Some(15),
                error: None,
            },
        ];
        let ranked = rank(&cands, &outs, DEFAULT_ORDER);
        // Same 40ms band → reality-tls wins on preference.
        assert_eq!(ranked[0].candidate.name, "b");
    }

    #[test]
    fn next_failover_skips_self() {
        let cands: Vec<RankedPick> = (0..3)
            .map(|i| RankedPick {
                index: i,
                candidate: TransportCandidate {
                    name: format!("p{i}"),
                    host: "h".into(),
                    port: 1,
                    protocol: "tcp".into(),
                    mode: "fake-tls".into(),
                    quic: false,
                    sni: None,
                    body: String::new(),
                },
                rtt_ms: Some(10),
                preference: 1,
            })
            .collect();
        assert_eq!(next_failover(&cands, 0).unwrap().index, 1);
        assert_eq!(next_failover(&cands, 2).unwrap().index, 0);
    }

    #[test]
    fn apply_sni_updates_ini() {
        let src = "[qeli]\nserver = 1.2.3.4:443\nuser = u\npass = p\nmode = fake-tls\n";
        let out = apply_sni_override(src, "www.example.com").unwrap();
        assert!(out.contains("sni = www.example.com"));
    }
}
