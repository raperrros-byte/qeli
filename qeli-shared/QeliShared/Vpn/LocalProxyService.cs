using System.Net;
using System.Net.Sockets;
using System.Text;
using Qeli.Shared.Geo;

namespace Qeli.Shared.Vpn;

/// <summary>
/// Original TCP destinations for WinDivert DNAT into the local proxy (app intercept mode).
/// Keyed by the intercepted client's apparent source endpoint after rewrite to 127.0.0.1:proxy.
/// </summary>
public static class TransparentDestRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        (string ip, ushort port), (IPAddress dest, ushort destPort)> Map = new();

    public static void Remember(IPAddress clientIp, ushort clientPort, IPAddress dest, ushort destPort) =>
        Map[(clientIp.ToString(), clientPort)] = (dest, destPort);

    public static bool TryGet(IPAddress clientIp, ushort clientPort, out IPAddress dest, out ushort destPort)
    {
        if (Map.TryGetValue((clientIp.ToString(), clientPort), out var v))
        {
            dest = v.dest;
            destPort = v.destPort;
            return true;
        }
        dest = IPAddress.None;
        destPort = 0;
        return false;
    }

    public static void Forget(IPAddress clientIp, ushort clientPort) =>
        Map.TryRemove((clientIp.ToString(), clientPort), out _);
}

/// <summary>
/// Local SOCKS5 / HTTP CONNECT proxy: apps connect here, outbound TCP is bound to the
/// tunnel client IP so traffic exits via the VPN in split-tunnel mode.
/// Also accepts transparent TCP from WinDivert app-intercept (no SOCKS handshake).
/// Log lines follow v2rayN style: <c>from tcp:peer accepted tcp:host:port [socks -> proxy|direct]</c>.
/// </summary>
public sealed class LocalProxyService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private IPAddress _tunnelIp = IPAddress.None;
    private ProxyMode _mode = ProxyMode.Mixed;
    private Action<string>? _log;
    private Func<IPAddress, IDisposable?>? _pinHostViaTunnel;
    private bool _bindOutboundToTunnelIp = true;
    private bool _warnedMissingGeo;

    private enum ProxyMode { Socks5, Http, Mixed }

    /// <param name="pinHostViaTunnel">
    /// Optional: for split-tunnel on Windows Wintun, bind-to-TUN alone is not enough — the OS
    /// still picks the physical default route. Caller installs a temporary dest/32 via the
    /// TUN for the lease lifetime (refcounted). Null = bind only (Linux/macOS / full-tunnel).
    /// </param>
    /// <param name="bindOutboundToTunnelIp">
    /// When false, dials use the normal NIC (legacy WinDivert combo — prefer Wintun bind+pin).
    /// </param>
    public void Start(
        string tunnelClientIp,
        string listen,
        string mode,
        Action<string>? log = null,
        Func<IPAddress, IDisposable?>? pinHostViaTunnel = null,
        bool bindOutboundToTunnelIp = true)
    {
        Stop();
        _log = log;
        _pinHostViaTunnel = pinHostViaTunnel;
        _bindOutboundToTunnelIp = bindOutboundToTunnelIp;
        _warnedMissingGeo = false;
        _tunnelIp = IPAddress.Parse(tunnelClientIp);
        _mode = ParseMode(mode);
        var endpoint = ParseListen(listen);
        _listener = new TcpListener(endpoint);
        _listener.Start();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _acceptTask = Task.Run(() => AcceptLoop(ct), ct);
        Log(bindOutboundToTunnelIp
            ? $"Local proxy on {listen} ({mode}) — outbound via tunnel IP {tunnelClientIp}"
            : $"Local proxy on {listen} ({mode}) — outbound via process capture");
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts = null;
        _acceptTask = null;
    }

    public void Dispose() => Stop();

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e)
            {
                Log($"Local proxy accept error: {e.Message}");
                continue;
            }
            _ = Task.Run(() => HandleClient(client, ct), ct);
        }
    }

    private async Task HandleClient(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            string peer = "tcp:127.0.0.1:0";
            IPEndPoint? remote = null;
            try
            {
                if (client.Client.RemoteEndPoint is IPEndPoint ep)
                {
                    remote = ep;
                    peer = $"tcp:{ep.Address}:{ep.Port}";
                }

                // WinDivert intercept: client speaks raw TCP to the original server; we already
                // know the destination from the DNAT flow table.
                if (remote != null
                    && TransparentDestRegistry.TryGet(remote.Address, (ushort)remote.Port,
                        out var dest, out var destPort))
                {
                    await TransparentRelay(client, peer, dest, destPort, ct);
                    return;
                }

                var stream = client.GetStream();
                var lead = new byte[1];
                if (await stream.ReadAsync(lead, ct) != 1) return;
                int peek = lead[0];
                if (peek == 0x05 && _mode != ProxyMode.Http)
                    await Socks5Relay(stream, peer, ct);
                else if (_mode != ProxyMode.Socks5)
                    await HttpRelay(stream, peek, peer, ct);
                else
                    Log($"{peer} rejected: expected SOCKS5");
            }
            catch (Exception e)
            {
                Log($"{peer} session: {e.Message}");
            }
            finally
            {
                if (remote != null)
                    TransparentDestRegistry.Forget(remote.Address, (ushort)remote.Port);
            }
        }
    }

    private async Task TransparentRelay(
        TcpClient client, string peer, IPAddress dest, ushort destPort, CancellationToken ct)
    {
        string host = dest.ToString();
        var decision = Decide(host, destPort, out var targetIp);
        Log($"{peer} accepted tcp:{host}:{destPort} [intercept -> {decision.Tag}]");
        if (decision.Tag == "block") return;
        using var upstream = await Dial(host, destPort, targetIp ?? dest, decision.ViaTunnel, ct);
        if (upstream == null) return;
        await Relay(client.GetStream(), upstream.Stream, ct);
    }

    private async Task Socks5Relay(NetworkStream client, string peer, CancellationToken ct)
    {
        var nmethods = await ReadExact(client, 1, ct);
        if (nmethods.Length == 0) return;
        await ReadExact(client, nmethods[0], ct);
        await client.WriteAsync(new byte[] { 0x05, 0x00 }, ct);

        var hdr = await ReadExact(client, 4, ct);
        if (hdr.Length < 4 || hdr[0] != 0x05 || hdr[1] != 0x01)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        var (host, port) = await ReadSocks5Target(client, hdr[3], ct);
        var decision = Decide(host, port, out var targetIp);
        Log($"{peer} accepted tcp:{host}:{port} [socks -> {decision.Tag}]");
        if (decision.Tag == "block")
        {
            await client.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        using var upstream = await Dial(host, port, targetIp, decision.ViaTunnel, ct);
        if (upstream == null)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        await client.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        await Relay(client, upstream.Stream, ct);
    }

    private async Task<(string Host, int Port)> ReadSocks5Target(
        NetworkStream client, byte atyp, CancellationToken ct)
    {
        string host;
        if (atyp == 0x01)
        {
            var ip = await ReadExact(client, 4, ct);
            host = new IPAddress(ip).ToString();
        }
        else if (atyp == 0x03)
        {
            var len = await ReadExact(client, 1, ct);
            var name = await ReadExact(client, len[0], ct);
            host = Encoding.UTF8.GetString(name);
        }
        else throw new InvalidOperationException("SOCKS5 IPv6 not supported");
        var portb = await ReadExact(client, 2, ct);
        int port = (portb[0] << 8) | portb[1];
        return (host, port);
    }

    private async Task HttpRelay(NetworkStream client, int firstByte, string peer, CancellationToken ct)
    {
        var buf = new MemoryStream();
        buf.WriteByte((byte)firstByte);
        var tmp = new byte[1024];
        while (true)
        {
            int n = await client.ReadAsync(tmp, ct);
            if (n <= 0) return;
            buf.Write(tmp, 0, n);
            if (Encoding.ASCII.GetString(buf.GetBuffer(), 0, (int)buf.Length).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
            if (buf.Length > 64 * 1024) return;
        }
        var text = Encoding.UTF8.GetString(buf.ToArray());
        var line = text.Split('\n')[0].Trim();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            Log($"{peer} http reject method={parts.ElementAtOrDefault(0) ?? "?"}");
            var deny = Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\n\r\n");
            await client.WriteAsync(deny, ct);
            return;
        }
        var (host, port) = ParseHttpHostPort(parts[1]);
        var decision = Decide(host, port, out var targetIp);
        Log($"{peer} accepted tcp:{host}:{port} [http -> {decision.Tag}]");
        if (decision.Tag == "block")
        {
            await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n"), ct);
            return;
        }
        using var upstream = await Dial(host, port, targetIp, decision.ViaTunnel, ct);
        if (upstream == null) return;
        await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        if (headerEnd < buf.Length)
        {
            var body = buf.ToArray()[(headerEnd)..];
            if (body.Length > 0) await upstream.Stream.WriteAsync(body, ct);
        }
        await Relay(client, upstream.Stream, ct);
    }

    private readonly record struct RouteDecision(string Tag, bool ViaTunnel);

    private RouteDecision Decide(string host, int port, out IPAddress? resolvedIp)
    {
        resolvedIp = null;
        try
        {
            if (IPAddress.TryParse(host, out var parsed))
                resolvedIp = parsed;
            else
            {
                var addrs = Dns.GetHostAddresses(host);
                resolvedIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            }
        }
        catch { /* leave null */ }

        var preset = ProxyRouteConfig.PresetId;
        bool useProxy = true;
        if (!string.Equals(preset, ProxyRoutePreset.ProxyAll, StringComparison.Ordinal))
        {
            var (site, ip) = GeoAssetStore.GetOrLoad(preset);
            if (site == null || ip == null)
            {
                if (!_warnedMissingGeo)
                {
                    _warnedMissingGeo = true;
                    Log("geo files missing — falling back to proxy-all (download in Settings)");
                }
                useProxy = true;
            }
            else
                useProxy = ProxyRoutePreset.ShouldProxy(preset, host, resolvedIp, site, ip);
        }

        // Ads category in CN whitelist/blacklist presets → block (v2rayN-like).
        if (!useProxy
            && (preset == ProxyRoutePreset.BypassCn || preset == ProxyRoutePreset.GfwBlacklist))
        {
            var (site, _) = GeoAssetStore.GetOrLoad(preset);
            if (site != null && site.MatchAny(host, "category-ads-all"))
                return new RouteDecision("block", false);
        }

        return useProxy
            ? new RouteDecision("proxy", true)
            : new RouteDecision("direct", false);
    }

    /// <summary>Upstream TCP plus optional temporary TUN host-route lease.</summary>
    private sealed class UpstreamConn : IDisposable
    {
        private readonly TcpClient _client;
        private readonly IDisposable? _routeLease;
        public NetworkStream Stream => _client.GetStream();
        public UpstreamConn(TcpClient client, IDisposable? routeLease)
        {
            _client = client;
            _routeLease = routeLease;
        }
        public void Dispose()
        {
            try { _client.Dispose(); } catch { }
            try { _routeLease?.Dispose(); } catch { }
        }
    }

    private async Task<UpstreamConn?> Dial(string host, int port, IPAddress? knownIp, bool viaTunnel, CancellationToken ct)
    {
        IDisposable? routeLease = null;
        try
        {
            IPAddress targetIp = knownIp
                ?? (IPAddress.TryParse(host, out var p) ? p : null)
                ?? (await Dns.GetHostAddressesAsync(host, ct))
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? throw new InvalidOperationException($"no IPv4 address for {host}");

            // Split-tunnel Wintun: without a dest/32 via TUN, Connect from the tunnel IP
            // fails (WSAEACCES) because the default route stays on the physical NIC.
            if (viaTunnel && _bindOutboundToTunnelIp && _pinHostViaTunnel != null)
                routeLease = _pinHostViaTunnel(targetIp);

            var client = new TcpClient(AddressFamily.InterNetwork);
            if (viaTunnel && _bindOutboundToTunnelIp)
                client.Client.Bind(new IPEndPoint(_tunnelIp, 0));
            await client.ConnectAsync(new IPEndPoint(targetIp, port), ct);
            return new UpstreamConn(client, routeLease);
        }
        catch (Exception e)
        {
            try { routeLease?.Dispose(); } catch { }
            Log($"FAIL tcp:{host}:{port}: {e.Message}");
            return null;
        }
    }

    private static async Task Relay(NetworkStream a, NetworkStream b, CancellationToken ct)
    {
        var up = a.CopyToAsync(b, ct);
        var down = b.CopyToAsync(a, ct);
        await Task.WhenAny(up, down);
    }

    private static async Task<byte[]> ReadExact(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(got, count - got), ct);
            if (n == 0) break;
            got += n;
        }
        return got == count ? buf : buf[..got];
    }

    private static (string Host, int Port) ParseHttpHostPort(string target)
    {
        var idx = target.LastIndexOf(':');
        if (idx <= 0) throw new FormatException("HTTP CONNECT target missing port");
        return (target[..idx], int.Parse(target[(idx + 1)..]));
    }

    private static IPEndPoint ParseListen(string listen)
    {
        if (listen.StartsWith('['))
        {
            var rb = listen.IndexOf(']');
            if (rb < 0 || rb + 2 >= listen.Length) throw new FormatException($"invalid proxy_listen '{listen}'");
            return new IPEndPoint(IPAddress.Parse(listen[1..rb]), int.Parse(listen[(rb + 2)..]));
        }
        var colon = listen.LastIndexOf(':');
        if (colon <= 0) throw new FormatException($"invalid proxy_listen '{listen}'");
        return new IPEndPoint(IPAddress.Parse(listen[..colon]), int.Parse(listen[(colon + 1)..]));
    }

    private static ProxyMode ParseMode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "socks5" or "socks" => ProxyMode.Socks5,
        "http" => ProxyMode.Http,
        "mixed" => ProxyMode.Mixed,
        _ => throw new ArgumentException($"unknown proxy_mode '{mode}' — expected socks5, http or mixed"),
    };

    private void Log(string m) => _log?.Invoke(m);
}
