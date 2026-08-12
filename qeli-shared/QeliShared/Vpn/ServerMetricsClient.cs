using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Qeli.Shared.Vpn;

/// <summary>One sample from the server panel <c>/api/system</c> + local tunnel rates.</summary>
public readonly record struct ServerMetricsSample(
    double CpuPct,
    double MemPct,
    double UpMbps,
    double DownMbps,
    int Clients,
    long MemUsed,
    long MemTotal,
    int Cores,
    double Load1);

/// <summary>Polls the qeli web panel metrics API (cookie session after /api/login).</summary>
public sealed class ServerMetricsClient : IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };
    private string? _cookie;
    private string _base = "";
    private string _user = "";
    private string _pass = "";

    public void Configure(string baseUrl, string user, string password)
    {
        _base = (baseUrl ?? "").TrimEnd('/');
        _user = user ?? "";
        _pass = password ?? "";
        _cookie = null;
    }

    public bool IsConfigured =>
        _base.Length > 0 && _user.Length > 0 && _pass.Length > 0;

    public async Task<ServerMetricsSample?> FetchAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return null;
        try
        {
            if (_cookie == null && !await LoginAsync(ct).ConfigureAwait(false))
                return null;
            using var req = new HttpRequestMessage(HttpMethod.Get, _base + "/api/system");
            req.Headers.TryAddWithoutValidation("Cookie", _cookie);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                _cookie = null;
                if (!await LoginAsync(ct).ConfigureAwait(false)) return null;
                using var req2 = new HttpRequestMessage(HttpMethod.Get, _base + "/api/system");
                req2.Headers.TryAddWithoutValidation("Cookie", _cookie);
                using var resp2 = await Http.SendAsync(req2, ct).ConfigureAwait(false);
                resp2.EnsureSuccessStatusCode();
                return Parse(await resp2.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            resp.EnsureSuccessStatusCode();
            return Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> LoginAsync(CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { username = _user, password = _pass });
            using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/login")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var c in cookies)
                {
                    var part = c.Split(';')[0].Trim();
                    if (part.StartsWith("qeli_session=", StringComparison.OrdinalIgnoreCase))
                    {
                        _cookie = part;
                        return true;
                    }
                }
            }
            return false;
        }
        catch { return false; }
    }

    private static ServerMetricsSample? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            return null;
        double load1 = 0;
        if (r.TryGetProperty("load", out var load) && load.ValueKind == JsonValueKind.Array
            && load.GetArrayLength() > 0)
            load1 = load[0].GetDouble();
        return new ServerMetricsSample(
            CpuPct: r.TryGetProperty("cpu_pct", out var c) ? c.GetDouble() : 0,
            MemPct: r.TryGetProperty("mem_pct", out var m) ? m.GetDouble() : 0,
            UpMbps: r.TryGetProperty("up_mbps", out var u) ? u.GetDouble() : 0,
            DownMbps: r.TryGetProperty("down_mbps", out var d) ? d.GetDouble() : 0,
            Clients: r.TryGetProperty("clients", out var cl) ? cl.GetInt32() : 0,
            MemUsed: r.TryGetProperty("mem_used", out var mu) ? mu.GetInt64() : 0,
            MemTotal: r.TryGetProperty("mem_total", out var mt) ? mt.GetInt64() : 0,
            Cores: r.TryGetProperty("cores", out var cores) ? cores.GetInt32() : 0,
            Load1: load1);
    }

    public void Dispose() { }
}
