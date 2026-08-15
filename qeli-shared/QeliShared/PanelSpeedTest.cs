using System.Net;
using System.Net.Http;
using System.Text;

namespace Qeli.Shared;

/// <summary>Authenticated download against panel <c>/api/speedtest</c> with lab-friendly TLS.</summary>
public static class PanelSpeedTest
{
    public static double Mbps(
        string preferredBaseUrl,
        string user,
        string password,
        int bytes = 1_048_576,
        string? vpnServerHost = null)
    {
        bytes = Math.Clamp(bytes, 64 * 1024, 8 * 1024 * 1024);
        var candidates = BuildCandidates(preferredBaseUrl, vpnServerHost);
        Exception? last = null;
        foreach (var baseUrl in candidates)
        {
            try
            {
                return RunOnce(baseUrl, user, password, bytes);
            }
            catch (Exception ex)
            {
                last = Root(ex);
            }
        }
        throw last ?? new InvalidOperationException("speed test: no panel URL candidates");
    }

    public static IReadOnlyList<string> BuildCandidates(string? preferredBaseUrl, string? vpnServerHost)
    {
        var list = new List<string>();
        void add(string? u)
        {
            u = (u ?? "").Trim().TrimEnd('/');
            if (u.Length == 0) return;
            if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                u = "https://" + u;
            if (!list.Any(x => string.Equals(x, u, StringComparison.OrdinalIgnoreCase)))
                list.Add(u);
        }

        add(preferredBaseUrl);

        var host = (vpnServerHost ?? "").Trim().Trim('[', ']');
        if (host.Length > 0)
        {
            var isIp = IPAddress.TryParse(host, out _);
            if (isIp)
            {
                add($"http://{host}:8080");
                add($"https://{host}:8080");
            }
            else
            {
                add($"https://{host}");
                add($"http://{host}:8080");
                add($"https://{host}:8080");
                add($"http://{host}");
            }
        }

        if (list.Count == 0)
            add("http://127.0.0.1:8080");

        return list;
    }

    private static double RunOnce(string baseUrl, string user, string password, int bytes)
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            // Lab panels often use nginx with private / mismatched certs.
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
            CheckCertificateRevocationList = false,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(45) };
        if (!string.IsNullOrEmpty(password))
        {
            var loginBody = new StringContent(
                $"{{\"username\":\"{user}\",\"password\":\"{password}\"}}",
                Encoding.UTF8, "application/json");
            using var loginResp = http.PostAsync($"{baseUrl}/api/login", loginBody).GetAwaiter().GetResult();
            if (!loginResp.IsSuccessStatusCode)
                throw new InvalidOperationException($"panel login HTTP {(int)loginResp.StatusCode} @ {baseUrl}");
            _ = loginResp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = http.GetAsync($"{baseUrl}/api/speedtest?bytes={bytes}").GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"speedtest HTTP {(int)resp.StatusCode} @ {baseUrl}");
        var payload = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        sw.Stop();
        if (payload.Length < bytes / 4)
            throw new InvalidOperationException($"speedtest short body {payload.Length}B @ {baseUrl}");
        var secs = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        return payload.Length * 8.0 / secs / 1_000_000.0;
    }

    private static Exception Root(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: > 0 } agg)
            ex = agg.InnerExceptions[0];
        while (ex.InnerException != null)
            ex = ex.InnerException;
        return ex;
    }
}
