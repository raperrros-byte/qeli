using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Qeli.Shared;

/// <summary>One row in the SNI speed-test table.</summary>
public sealed class SniSpeedRow
{
    public string Host { get; init; } = "";
    public bool Ok { get; init; }
    public long? TlsMs { get; init; }
    public double? Mbps { get; init; }
    public long Bytes { get; init; }
    public string? Error { get; init; }
    public string Status => Ok
        ? (Mbps is double m ? $"{m:F2} Mbit/s" : "ok")
        : (Error ?? "fail");
}

/// <summary>
/// LibreSpeed-style probe against an SNI / TLS front host: TCP connect + TLS 1.2/1.3
/// handshake (with the candidate as SNI) + short HTTPS download.
/// </summary>
public static class SniSpeedProbe
{
    public const int DefaultBytes = 512 * 1024;

    public static async Task<SniSpeedRow> ProbeAsync(
        string host,
        int bytes = DefaultBytes,
        int timeoutMs = 12_000,
        CancellationToken ct = default)
    {
        var normalized = SniCatalog.Normalize(host);
        if (normalized == null)
            return new SniSpeedRow { Host = host, Ok = false, Error = "invalid host" };

        bytes = Math.Clamp(bytes, 64 * 1024, 4 * 1024 * 1024);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var swTls = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(normalized, 443, cts.Token).ConfigureAwait(false);
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                userCertificateValidationCallback: static (_, _, _, _) => true);
            var opts = new SslClientAuthenticationOptions
            {
                TargetHost = normalized,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            };
            await ssl.AuthenticateAsClientAsync(opts, cts.Token).ConfigureAwait(false);
            swTls.Stop();

            var path = $"/?qeli-sni-probe={bytes}";
            // Prefer hosts that speak HTTP; many CDNs return 403/301 — still counts bytes if any.
            var req = $"GET {path} HTTP/1.1\r\nHost: {normalized}\r\nConnection: close\r\n" +
                      $"User-Agent: qeli-sni-speed/0.7\r\nAccept: */*\r\n" +
                      $"Range: bytes=0-{bytes - 1}\r\n\r\n";
            var reqBytes = Encoding.ASCII.GetBytes(req);
            var swDl = Stopwatch.StartNew();
            await ssl.WriteAsync(reqBytes, cts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(cts.Token).ConfigureAwait(false);

            var buf = new byte[16 * 1024];
            long total = 0;
            var headerDone = false;
            while (total < bytes)
            {
                var n = await ssl.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                if (!headerDone)
                {
                    // Count only body after headers for Mbps; still ok if headers-only.
                    var span = buf.AsSpan(0, n);
                    var sep = span.IndexOf("\r\n\r\n"u8);
                    if (sep >= 0)
                    {
                        headerDone = true;
                        total += n - (sep + 4);
                    }
                }
                else total += n;
            }
            swDl.Stop();

            var secs = Math.Max(0.001, swDl.Elapsed.TotalSeconds);
            var mbps = total > 0 ? total * 8.0 / secs / 1_000_000.0 : (double?)null;
            return new SniSpeedRow
            {
                Host = normalized,
                Ok = true,
                TlsMs = swTls.ElapsedMilliseconds,
                Mbps = mbps,
                Bytes = total,
            };
        }
        catch (Exception ex)
        {
            return new SniSpeedRow
            {
                Host = normalized,
                Ok = false,
                Error = ex.GetBaseException().Message,
            };
        }
    }

    public static async Task<IReadOnlyList<SniSpeedRow>> ProbeManyAsync(
        IEnumerable<string> hosts,
        int bytes = DefaultBytes,
        int parallelism = 4,
        int delayMs = 0,
        IProgress<SniSpeedRow>? progress = null,
        CancellationToken ct = default)
    {
        var list = hosts
            .Select(SniCatalog.Normalize)
            .Where(h => h != null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        if (delayMs > 0 || parallelism <= 1)
        {
            var sequential = new List<SniSpeedRow>(list.Count);
            for (var i = 0; i < list.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (delayMs > 0 && i > 0)
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                var row = await ProbeAsync(list[i], bytes, ct: ct).ConfigureAwait(false);
                sequential.Add(row);
                progress?.Report(row);
            }
            return sequential
                .OrderByDescending(r => r.Ok)
                .ThenBy(r => r.TlsMs ?? long.MaxValue)
                .ThenByDescending(r => r.Mbps ?? 0)
                .ToList();
        }

        var results = new SniSpeedRow[list.Count];
        using var gate = new SemaphoreSlim(Math.Clamp(parallelism, 1, 8));
        var tasks = list.Select(async (host, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var row = await ProbeAsync(host, bytes, ct: ct).ConfigureAwait(false);
                results[i] = row;
                progress?.Report(row);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results
            .Where(r => r != null)
            .OrderByDescending(r => r.Ok)
            .ThenBy(r => r.TlsMs ?? long.MaxValue)
            .ThenByDescending(r => r.Mbps ?? 0)
            .ToList();
    }
}
