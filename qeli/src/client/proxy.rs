//! Local SOCKS5 / HTTP CONNECT proxy for per-app tunneling.
//!
//! Accepts connections on `proxy_listen` (default 127.0.0.1:1080) and dials outbound
//! TCP through the tunnel by binding the socket to the client-assigned TUN IP. Requires
//! split-tunnel mode (`gateway = false`) for selective routing; full-tunnel already sends
//! everything through the VPN.

use anyhow::{anyhow, Context};
use std::net::{Ipv4Addr, SocketAddr};
use std::sync::Arc;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::{TcpListener, TcpSocket, TcpStream};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ProxyMode {
    Socks5,
    Http,
    Mixed,
}

impl ProxyMode {
    fn parse(s: &str) -> anyhow::Result<Self> {
        match s.trim().to_ascii_lowercase().as_str() {
            "socks5" | "socks" => Ok(Self::Socks5),
            "http" => Ok(Self::Http),
            "mixed" => Ok(Self::Mixed),
            other => Err(anyhow!(
                "unknown proxy_mode '{other}' - expected socks5, http or mixed"
            )),
        }
    }
}

pub async fn serve(
    listen: String,
    mode: String,
    bind_ip: Ipv4Addr,
    bind_device: Option<String>,
) -> anyhow::Result<()> {
    let mode = ProxyMode::parse(&mode)?;
    let addr: SocketAddr = listen
        .parse()
        .with_context(|| format!("invalid proxy_listen '{listen}'"))?;
    if !addr.ip().is_loopback() {
        log::warn!(
            "proxy_listen {listen} is not loopback - any local user/process can use the tunnel"
        );
    }
    let listener = TcpListener::bind(addr).await.with_context(|| {
        format!("local proxy cannot bind {listen} (is another proxy already listening?)")
    })?;
    let bind = Arc::new(bind_ip);
    let bind_device = bind_device.map(Arc::new);
    log::info!("Local proxy ready on {listen}");
    loop {
        let (stream, peer) = listener.accept().await?;
        let bind = bind.clone();
        let dev = bind_device.clone();
        tokio::spawn(async move {
            if let Err(e) = handle_client(stream, peer, mode, *bind, dev.as_deref().map(|d| d.as_str())).await {
                log::debug!("Local proxy session from {peer}: {e}");
            }
        });
    }
}

async fn handle_client(
    stream: TcpStream,
    peer: SocketAddr,
    mode: ProxyMode,
    bind_ip: Ipv4Addr,
    bind_device: Option<&str>,
) -> anyhow::Result<()> {
    let mut peek = [0u8; 1];
    let n = stream.peek(&mut peek).await?;
    if n == 0 {
        return Ok(());
    }
    match peek[0] {
        0x05 if mode != ProxyMode::Http => socks5_relay(stream, bind_ip, bind_device).await,
        b'C' | b'G' | b'P' | b'H' | b'D' if mode != ProxyMode::Socks5 => {
            http_relay(stream, bind_ip, bind_device).await
        }
        0x05 => Err(anyhow!("SOCKS5 rejected - proxy_mode = http")),
        _ if mode == ProxyMode::Socks5 => Err(anyhow!("expected SOCKS5 from {peer}")),
        _ => http_relay(stream, bind_ip, bind_device).await,
    }
}

async fn dial_via_tunnel(
    host: &str,
    port: u16,
    bind_ip: Ipv4Addr,
    _bind_device: Option<&str>,
    via: &str,
) -> anyhow::Result<TcpStream> {
    let target = resolve_target(host, port).await?;
    log::info!("Local proxy → {host}:{port} ({target}) via {via}");
    let socket = TcpSocket::new_v4()?;
    // Source IP + policy routing (table 100) steers traffic through the TUN.
    // SO_BINDTODEVICE breaks TCP connect in split-tunnel (SYN never completes).
    socket
        .bind(SocketAddr::new(bind_ip.into(), 0))
        .with_context(|| format!("bind outbound to tunnel IP {bind_ip}"))?;
    match socket.connect(target).await {
        Ok(s) => Ok(s),
        Err(e) => {
            log::warn!("Local proxy FAIL {host}:{port}: {e}");
            Err(e).with_context(|| format!("connect {host}:{port} via tunnel"))
        }
    }
}

async fn resolve_target(host: &str, port: u16) -> anyhow::Result<SocketAddr> {
    if let Ok(ip) = host.parse::<Ipv4Addr>() {
        return Ok(SocketAddr::new(ip.into(), port));
    }
    let lookup = format!("{host}:{port}");
    let mut addrs = tokio::net::lookup_host(&lookup).await?;
    addrs
        .find(|a| a.is_ipv4())
        .ok_or_else(|| anyhow!("no IPv4 address for {host}"))
}

async fn relay_bidirectional(a: TcpStream, b: TcpStream) -> anyhow::Result<()> {
    let (mut ar, mut aw) = a.into_split();
    let (mut br, mut bw) = b.into_split();
    tokio::select! {
        r = tokio::io::copy(&mut ar, &mut bw) => { r?; }
        r = tokio::io::copy(&mut br, &mut aw) => { r?; }
    }
    Ok(())
}

async fn socks5_relay(
    mut client: TcpStream,
    bind_ip: Ipv4Addr,
    bind_device: Option<&str>,
) -> anyhow::Result<()> {
    let mut buf = [0u8; 513];
    let n = read_exact_or_eof(&mut client, &mut buf[..2]).await?;
    if n < 2 || buf[0] != 0x05 {
        return Err(anyhow!("invalid SOCKS5 greeting"));
    }
    let nmethods = buf[1] as usize;
    if nmethods == 0 || read_exact_or_eof(&mut client, &mut buf[..nmethods]).await? < nmethods {
        return Err(anyhow!("truncated SOCKS5 methods"));
    }
    client.write_all(&[0x05, 0x00]).await?; // no auth

    let n = read_exact_or_eof(&mut client, &mut buf[..4]).await?;
    if n < 4 || buf[0] != 0x05 || buf[1] != 0x01 {
        let cmd = if n >= 2 { buf[1] } else { 0 };
        client.write_all(&[0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0]).await?;
        return Err(anyhow!(
            "unsupported SOCKS5 command 0x{cmd:02x} (TCP CONNECT only; no UDP ASSOCIATE)"
        ));
    }
    let atyp = buf[3];
    let (host, port) = read_socks5_target(&mut client, &mut buf, atyp).await?;
    let upstream = match dial_via_tunnel(&host, port, bind_ip, bind_device, "socks5").await {
        Ok(s) => s,
        Err(e) => {
            client.write_all(&[0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0]).await?;
            return Err(e);
        }
    };
    client.write_all(&[0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0]).await?;
    relay_bidirectional(client, upstream).await?;
    Ok(())
}

async fn read_socks5_target(
    client: &mut TcpStream,
    buf: &mut [u8; 513],
    atyp: u8,
) -> anyhow::Result<(String, u16)> {
    let host = match atyp {
        0x01 => {
            let mut ip = [0u8; 4];
            read_exact_or_eof(client, &mut ip).await?;
            Ipv4Addr::from(ip).to_string()
        }
        0x03 => {
            let mut lenb = [0u8; 1];
            read_exact_or_eof(client, &mut lenb).await?;
            let len = lenb[0] as usize;
            if len == 0 || len > 255 {
                return Err(anyhow!("invalid SOCKS5 domain length"));
            }
            read_exact_or_eof(client, &mut buf[..len]).await?;
            String::from_utf8(buf[..len].to_vec()).context("SOCKS5 domain not UTF-8")?
        }
        0x04 => return Err(anyhow!("SOCKS5 IPv6 not supported")),
        other => return Err(anyhow!("unknown SOCKS5 ATYP {other}")),
    };
    let mut portb = [0u8; 2];
    read_exact_or_eof(client, &mut portb).await?;
    let port = u16::from_be_bytes(portb);
    Ok((host, port))
}

async fn http_relay(
    mut client: TcpStream,
    bind_ip: Ipv4Addr,
    bind_device: Option<&str>,
) -> anyhow::Result<()> {
    let mut buf = Vec::with_capacity(4096);
    let mut tmp = [0u8; 1024];
    loop {
        let n = client.read(&mut tmp).await?;
        if n == 0 {
            return Err(anyhow!("truncated HTTP request"));
        }
        buf.extend_from_slice(&tmp[..n]);
        if buf.windows(4).any(|w| w == b"\r\n\r\n") {
            break;
        }
        if buf.len() > 64 * 1024 {
            return Err(anyhow!("HTTP request too large"));
        }
    }
    let header = std::str::from_utf8(&buf).context("HTTP request not UTF-8")?;
    let mut lines = header.lines();
    let request_line = lines.next().ok_or_else(|| anyhow!("empty HTTP request"))?;
    let mut parts = request_line.split_whitespace();
    let method = parts.next().unwrap_or("");
    let target = parts.next().unwrap_or("");
    if !method.eq_ignore_ascii_case("CONNECT") {
        client
            .write_all(b"HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\n\r\n")
            .await?;
        return Err(anyhow!(
            "HTTP proxy supports CONNECT only, got {method} {target}"
        ));
    }
    let (host, port) = parse_http_host_port(target)?;
    let mut upstream = dial_via_tunnel(&host, port, bind_ip, bind_device, "http-connect").await?;
    client
        .write_all(b"HTTP/1.1 200 Connection Established\r\n\r\n")
        .await?;
    let body_start = header.find("\r\n\r\n").unwrap() + 4;
    if body_start < buf.len() {
        upstream.write_all(&buf[body_start..]).await?;
    }
    relay_bidirectional(client, upstream).await?;
    Ok(())
}

fn parse_http_host_port(target: &str) -> anyhow::Result<(String, u16)> {
    let (host, port) = target
        .rsplit_once(':')
        .ok_or_else(|| anyhow!("HTTP CONNECT target missing port"))?;
    let port: u16 = port.parse().context("invalid HTTP CONNECT port")?;
    Ok((host.to_string(), port))
}

async fn read_exact_or_eof(stream: &mut TcpStream, buf: &mut [u8]) -> anyhow::Result<usize> {
    let mut got = 0;
    while got < buf.len() {
        let n = stream.read(&mut buf[got..]).await?;
        if n == 0 {
            break;
        }
        got += n;
    }
    Ok(got)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn proxy_mode_parse() {
        assert_eq!(ProxyMode::parse("mixed").unwrap(), ProxyMode::Mixed);
        assert_eq!(ProxyMode::parse("SOCKS5").unwrap(), ProxyMode::Socks5);
        assert!(ProxyMode::parse("bad").is_err());
    }

    #[test]
    fn http_host_port() {
        assert_eq!(
            parse_http_host_port("example.com:443").unwrap(),
            ("example.com".into(), 443)
        );
    }
}
