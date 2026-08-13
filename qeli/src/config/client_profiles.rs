//! Multi-profile client config splitting — mirrors `VpnConfig::ParseMany` /
//! `Config.parseMany` in the GUI clients and the panel's Share bundle format.
//!
//! Supported inputs:
//! - one `[qeli]` INI (optionally with a shared leading `[logging]` section);
//! - several `[qeli]` blocks separated by `# Profile: <name>` comments;
//! - several `qeli://` links (one per line), when the paste contains no INI.

use crate::config::share::ClientLink;

/// One connectable profile extracted from a bundle file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientProfileSpec {
    pub name: String,
    /// Config text passed to the connection parser (`[logging]` prefix + one `[qeli]` block,
    /// or a single `qeli://` link).
    pub body: String,
}

/// Split a client config paste/file into named profiles.
pub fn split_client_profiles(source: &str) -> anyhow::Result<Vec<ClientProfileSpec>> {
    let trimmed = source.trim();
    if trimmed.is_empty() {
        anyhow::bail!("configuration is empty");
    }

    let normalized = trimmed.replace("\r\n", "\n").replace('\r', "\n");
    let has_ini = normalized
        .lines()
        .any(|line| line.trim().eq_ignore_ascii_case("[qeli]"));

    let link_lines: Vec<&str> = normalized
        .lines()
        .map(str::trim)
        .filter(|line| line.starts_with("qeli://"))
        .collect();

    if !link_lines.is_empty() && !has_ini {
        return split_link_profiles(link_lines);
    }

    split_ini_profiles(&normalized)
}

/// Pick one profile from a bundle. When several profiles exist, `profile` must be set.
pub fn resolve_client_profile(
    source: &str,
    profile: Option<&str>,
) -> anyhow::Result<(String, String)> {
    let profiles = split_client_profiles(source)?;
    if profiles.is_empty() {
        anyhow::bail!("no client profiles found");
    }
    if profiles.len() == 1 {
        let only = &profiles[0];
        if let Some(want) = profile {
            if want != only.name {
                anyhow::bail!(
                    "profile '{want}' not found (file contains only '{}')",
                    only.name
                );
            }
        }
        return Ok((only.name.clone(), only.body.clone()));
    }

    let want = profile.ok_or_else(|| {
        let names: Vec<_> = profiles.iter().map(|p| p.name.as_str()).collect();
        anyhow::anyhow!(
            "config contains {} profiles ({}); pass --profile <name> or run \
             `qeli client ls`",
            profiles.len(),
            names.join(", ")
        )
    })?;

    let available: Vec<String> = profiles.iter().map(|p| p.name.clone()).collect();
    profiles
        .into_iter()
        .find(|p| p.name == want)
        .map(|p| (p.name, p.body))
        .ok_or_else(|| {
            anyhow::anyhow!(
                "profile '{want}' not found; available: {}",
                available.join(", ")
            )
        })
}

fn split_link_profiles(lines: Vec<&str>) -> anyhow::Result<Vec<ClientProfileSpec>> {
    let mut out = Vec::with_capacity(lines.len());
    for line in lines {
        let link = ClientLink::from_uri(line)?;
        let name = link
            .label
            .as_deref()
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .map(str::to_string)
            .unwrap_or_else(|| format!("link-{}", out.len() + 1));
        out.push(ClientProfileSpec {
            name,
            body: line.to_string(),
        });
    }
    ensure_unique_names(&out)?;
    Ok(out)
}

fn split_ini_profiles(normalized: &str) -> anyhow::Result<Vec<ClientProfileSpec>> {
    let global_prefix = ini_global_prefix(normalized);
    let names_by_section = profile_names_before_qeli(normalized);
    let sections = split_qeli_ini_sections(normalized);

    if sections.is_empty() {
        let name = default_profile_name(normalized, 0);
        return Ok(vec![ClientProfileSpec {
            name,
            body: normalized.to_string(),
        }]);
    }

    let mut out = Vec::with_capacity(sections.len());
    for (index, section) in sections.into_iter().enumerate() {
        let name = names_by_section
            .get(index)
            .cloned()
            .or_else(|| profile_name_from_section(&section))
            .unwrap_or_else(|| format!("profile-{}", index + 1));
        let body = compose_profile_body(&global_prefix, &section);
        out.push(ClientProfileSpec { name, body });
    }
    ensure_unique_names(&out)?;
    Ok(out)
}

/// Lines before the first `[qeli]` header — typically a shared `[logging]` section.
fn ini_global_prefix(text: &str) -> String {
    for (index, line) in text.lines().enumerate() {
        if line.trim().eq_ignore_ascii_case("[qeli]") {
            return text
                .lines()
                .take(index)
                .collect::<Vec<_>>()
                .join("\n")
                .trim()
                .to_string();
        }
    }
    String::new()
}

/// Map each `[qeli]` block to the nearest preceding `# Profile: <name>` comment.
fn profile_names_before_qeli(text: &str) -> Vec<String> {
    let mut pending: Option<String> = None;
    let mut names = Vec::new();
    for line in text.lines() {
        let trimmed = line.trim();
        if let Some(name) = trimmed.strip_prefix("# Profile:") {
            let name = name.trim();
            if !name.is_empty() {
                pending = Some(name.to_string());
            }
            continue;
        }
        if trimmed.eq_ignore_ascii_case("[qeli]") {
            if let Some(name) = pending.take() {
                names.push(name);
            }
        }
    }
    names
}

fn profile_name_from_section(section: &str) -> Option<String> {
    for line in section.lines() {
        let trimmed = line.trim();
        if trimmed.starts_with('#') || trimmed.starts_with(';') || trimmed.is_empty() {
            continue;
        }
        if trimmed.starts_with('[') {
            continue;
        }
        if let Some((key, value)) = trimmed.split_once('=') {
            if key.trim().eq_ignore_ascii_case("name") {
                let value = value.trim();
                if !value.is_empty() {
                    return Some(value.to_string());
                }
            }
        }
    }
    None
}

fn default_profile_name(text: &str, index: usize) -> String {
    profile_names_before_qeli(text)
        .into_iter()
        .nth(index)
        .or_else(|| profile_name_from_section(text))
        .unwrap_or_else(|| "default".to_string())
}

fn compose_profile_body(global_prefix: &str, section: &str) -> String {
    if global_prefix.is_empty() {
        return section.trim().to_string();
    }
    format!("{}\n\n{}", global_prefix.trim_end(), section.trim())
}

/// Split a multi-profile INI into one blob per `[qeli]` section (mirrors GUI clients).
fn split_qeli_ini_sections(text: &str) -> Vec<String> {
    let mut chunks = Vec::new();
    let mut current: Option<String> = None;
    for raw in text.lines() {
        if raw.trim().eq_ignore_ascii_case("[qeli]") {
            if let Some(chunk) = current.take() {
                if !chunk.trim().is_empty() {
                    chunks.push(chunk);
                }
            }
            current = Some("[qeli]\n".to_string());
            continue;
        }
        if let Some(cur) = current.as_mut() {
            cur.push_str(raw);
            cur.push('\n');
        }
    }
    if let Some(chunk) = current {
        if !chunk.trim().is_empty() {
            chunks.push(chunk);
        }
    }
    chunks
}

fn ensure_unique_names(profiles: &[ClientProfileSpec]) -> anyhow::Result<()> {
    let mut seen = std::collections::HashSet::new();
    for profile in profiles {
        if !seen.insert(profile.name.clone()) {
            anyhow::bail!("duplicate profile name '{}'", profile.name);
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: &str = "1111111111111111111111111111111111111111111111111111111111111111";

    fn sample_qeli(profile: &str, port: u16) -> String {
        format!(
            "[qeli]\nserver = vpn.example.com:{port}\nproto = tcp\nuser = alice\npass = secret\nkey = {KEY}\nmode = fake-tls\nname = {profile}\n"
        )
    }

    #[test]
    fn single_ini_profile_defaults_to_default_name() {
        let text = sample_qeli("ignored", 443);
        let profiles = split_client_profiles(&text).unwrap();
        assert_eq!(profiles.len(), 1);
        assert_eq!(profiles[0].name, "ignored");
    }

    #[test]
    fn splits_panel_bundle_with_profile_markers() {
        let text = format!(
            "[logging]\nlevel = info\n\n# Profile: reality-tls\n{}\n\n# Profile: tcp\n{}",
            sample_qeli("reality-tls", 443),
            sample_qeli("tcp", 8443),
        );
        let profiles = split_client_profiles(&text).unwrap();
        assert_eq!(profiles.len(), 2);
        assert_eq!(profiles[0].name, "reality-tls");
        assert_eq!(profiles[1].name, "tcp");
        assert!(profiles[0].body.contains("[logging]"));
        assert!(profiles[0].body.contains("server = vpn.example.com:443"));
        assert!(profiles[1].body.contains("server = vpn.example.com:8443"));
    }

    #[test]
    fn resolves_named_profile_from_bundle() {
        let text = format!(
            "# Profile: reality-tls\n{}\n\n# Profile: tcp\n{}",
            sample_qeli("reality-tls", 443),
            sample_qeli("tcp", 8443),
        );
        let (name, body) = resolve_client_profile(&text, Some("tcp")).unwrap();
        assert_eq!(name, "tcp");
        assert!(body.contains(":8443"));
    }

    #[test]
    fn multi_profile_requires_selector() {
        let text = format!(
            "# Profile: a\n{}\n\n# Profile: b\n{}",
            sample_qeli("a", 443),
            sample_qeli("b", 8443),
        );
        let err = resolve_client_profile(&text, None).unwrap_err();
        assert!(err.to_string().contains("--profile"));
    }

    #[test]
    fn splits_multiple_qeli_links() {
        let uri_a = format!("qeli://alice:secret@vpn.example.com:443?proto=tcp&mode=fake-tls&key={KEY}#reality");
        let uri_b = format!("qeli://alice:secret@vpn.example.com:8443?proto=tcp&mode=fake-tls&key={KEY}#tcp");
        let text = format!("{uri_a}\n{uri_b}\n");
        let profiles = split_client_profiles(&text).unwrap();
        assert_eq!(profiles.len(), 2);
        assert_eq!(profiles[0].name, "reality");
        assert_eq!(profiles[1].name, "tcp");
    }
}
