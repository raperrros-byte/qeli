using System.Net;
using System.Net.Sockets;
using System.Text;
using Qeli.Shared.Geo;

namespace Qeli.Shared.Vpn;

/// <summary>
/// Local SOCKS5 / HTTP CONNECT proxy: apps connect here, outbound TCP is bound to the
/// tunnel client IP so traffic exits via the VPN in split-tunnel mode.
/// </summary>
public sealed class LocalProxyService : IDisposable
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private IPAddress _tunnelIp = IPAddress.None;
    private ProxyMode _mode = ProxyMode.Mixed;
    private Action<string>? _log;
    private bool _warnedMissingGeo;

    private enum ProxyMode { Socks5, Http, Mixed }

    public void Start(string tunnelClientIp, string listen, string mode, Action<string>? log = null)
    {
        Stop();
        _log = log;
        _warnedMissingGeo = false;
        _tunnelIp = IPAddress.Parse(tunnelClientIp);
        _mode = ParseMode(mode);
        var endpoint = ParseListen(listen);
        _listener = new TcpListener(endpoint);
        _listener.Start();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _acceptTask = Task.Run(() => AcceptLoop(ct), ct);
        Log($"Local proxy on {listen} ({mode}) — outbound via tunnel IP {tunnelClientIp}; "
            + "SOCKS/HTTP CONNECT destinations are logged here (point apps at this address)");
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
            try
            {
                var stream = client.GetStream();
                var lead = new byte[1];
                if (await stream.ReadAsync(lead, ct) != 1) return;
                int peek = lead[0];
                if (peek == 0x05 && _mode != ProxyMode.Http)
                    await Socks5Relay(stream, ct);
                else if (_mode != ProxyMode.Socks5)
                    await HttpRelay(stream, peek, ct);
                else
                    Log("Local proxy: expected SOCKS5");
            }
            catch (Exception e)
            {
                Log($"Local proxy session: {e.Message}");
            }
        }
    }

    private async Task Socks5Relay(NetworkStream client, CancellationToken ct)
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
        using var upstream = await DialViaTunnel(host, port, "socks5", ct);
        if (upstream == null)
        {
            await client.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
            return;
        }
        await client.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, ct);
        await Relay(client, upstream.GetStream(), ct);
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

    private async Task HttpRelay(NetworkStream client, int firstByte, CancellationToken ct)
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
            Log($"Local proxy HTTP reject method={parts.ElementAtOrDefault(0) ?? "?"} target={parts.ElementAtOrDefault(1) ?? "?"} (CONNECT only)");
            var deny = Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\nConnection: close\r\n\r\n");
            await client.WriteAsync(deny, ct);
            return;
        }
        var (host, port) = ParseHttpHostPort(parts[1]);
        using var upstream = await DialViaTunnel(host, port, "http-connect", ct);
        if (upstream == null) return;
        await client.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
        if (headerEnd < buf.Length)
        {
            var body = buf.ToArray()[(headerEnd)..];
            if (body.Length > 0) await upstream.GetStream().WriteAsync(body, ct);
        }
        await Relay(client, upstream.GetStream(), ct);
    }

    private async Task<TcpClient?> DialViaTunnel(string host, int port, string via, CancellationToken ct)
    {
        try
        {
            IPAddress targetIp;
            if (IPAddress.TryParse(host, out var parsed))
                targetIp = parsed;
            else
            {
                var addrs = await Dns.GetHostAddressesAsync(host, ct);
                targetIp = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? throw new InvalidOperationException($"no IPv4 address for {host}");
            }

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
                        Log("Local proxy: geo files missing — falling back to proxy-all (download in Settings)");
                    }
                    useProxy = true;
                }
                else
                    useProxy = ProxyRoutePreset.ShouldProxy(preset, host, targetIp, site, ip);
            }

            var path = useProxy ? "proxy" : "direct";
            Log($"Local proxy → {host}:{port} ({targetIp}) via {via}/{path} [{preset}]");
            var client = new TcpClient(AddressFamily.InterNetwork);
            if (useProxy)
                client.Client.Bind(new IPEndPoint(_tunnelIp, 0));
            await client.ConnectAsync(new IPEndPoint(targetIp, port), ct);
            return client;
        }
        catch (Exception e)
        {
            Log($"Local proxy FAIL {host}:{port}: {e.Message}");
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
