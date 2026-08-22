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
        ? (Mbps is double m ? $"{m:F2} Mbit/s"
            : Bytes >= SniSpeedProbe.MinBodyForMbps ? "ok"
            : "tls ok")
        : (Error ?? "fail");
}

/// <summary>
/// LibreSpeed-style probe against an SNI / TLS front host: TCP connect + TLS 1.2/1.3
/// handshake (with the candidate as SNI) + HTTPS download for at least
/// <see cref="DefaultMinDurationMs"/> (or until the byte cap).
/// </summary>
public static class SniSpeedProbe
{
    /// <summary>Max bytes to read per host (Range request cap).</summary>
    public const int DefaultBytes = 2 * 1024 * 1024;

    /// <summary>Keep downloading at least this long before stopping (stable Mbps).</summary>
    public const int DefaultMinDurationMs = 3_000;

    /// <summary>Body smaller than this does not produce an Mbps figure (avoids 0.01 on 403 pages).</summary>
    public const int MinBodyForMbps = 256 * 1024;

    public static async Task<SniSpeedRow> ProbeAsync(
        string host,
        int bytes = DefaultBytes,
        int minDurationMs = DefaultMinDurationMs,
        int timeoutMs = 0,
        CancellationToken ct = default)
    {
        var normalized = SniCatalog.Normalize(host);
        if (normalized == null)
            return new SniSpeedRow { Host = host, Ok = false, Error = "invalid host" };

        bytes = Math.Clamp(bytes, 256 * 1024, 8 * 1024 * 1024);
        minDurationMs = Math.Clamp(minDurationMs, 500, 60_000);
        if (timeoutMs <= 0)
            timeoutMs = Math.Clamp(minDurationMs + 12_000 + bytes / (256 * 1024) * 1000, 15_000, 120_000);

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
            var req = $"GET {path} HTTP/1.1\r\nHost: {normalized}\r\nConnection: close\r\n" +
                      $"User-Agent: qeli-sni-speed/0.7\r\nAccept: */*\r\n" +
                      $"Range: bytes=0-{bytes - 1}\r\n\r\n";
            var reqBytes = Encoding.ASCII.GetBytes(req);
            var swDl = Stopwatch.StartNew();
            await ssl.WriteAsync(reqBytes, cts.Token).ConfigureAwait(false);
            await ssl.FlushAsync(cts.Token).ConfigureAwait(false);

            var buf = new byte[32 * 1024];
            var reader = new HttpBodyReader();
            while (true)
            {
                var n = await ssl.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token).ConfigureAwait(false);
                if (n <= 0) break;
                reader.Add(buf.AsSpan(0, n));

                var elapsed = swDl.ElapsedMilliseconds;
                if (reader.BodyBytes >= bytes) break;
                if (elapsed >= minDurationMs && reader.BodyBytes >= MinBodyForMbps) break;
            }
            swDl.Stop();

            var total = reader.BodyBytes;
            var secs = Math.Max(0.001, swDl.Elapsed.TotalSeconds);
            double? mbps = total >= MinBodyForMbps
                ? total * 8.0 / secs / 1_000_000.0
                : null;
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
        int minDurationMs = DefaultMinDurationMs,
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
                var row = await ProbeAsync(list[i], bytes, minDurationMs, ct: ct).ConfigureAwait(false);
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
                var row = await ProbeAsync(host, bytes, minDurationMs, ct: ct).ConfigureAwait(false);
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

    /// <summary>Accumulates HTTP response bytes; strips headers even when split across reads.</summary>
    private sealed class HttpBodyReader
    {
        private readonly byte[] _pending = new byte[16 * 1024];
        private int _pendingLen;
        private bool _headerDone;
        public long BodyBytes { get; private set; }

        public void Add(ReadOnlySpan<byte> chunk)
        {
            if (_headerDone)
            {
                BodyBytes += chunk.Length;
                return;
            }

            var offset = 0;
            while (offset < chunk.Length)
            {
                if (_headerDone)
                {
                    BodyBytes += chunk.Length - offset;
                    return;
                }

                var take = Math.Min(chunk.Length - offset, _pending.Length - _pendingLen);
                chunk.Slice(offset, take).CopyTo(_pending.AsSpan(_pendingLen));
                _pendingLen += take;
                offset += take;

                var sep = IndexOfHeaderEnd(_pending.AsSpan(0, _pendingLen));
                if (sep < 0)
                {
                    if (_pendingLen >= _pending.Length)
                    {
                        // Headers never terminated — treat remainder as body to avoid stalling.
                        _headerDone = true;
                        BodyBytes += _pendingLen;
                        _pendingLen = 0;
                    }
                    continue;
                }

                _headerDone = true;
                BodyBytes += _pendingLen - (sep + 4);
                _pendingLen = 0;
            }
        }

        private static int IndexOfHeaderEnd(ReadOnlySpan<byte> data)
        {
            for (var i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' &&
                    data[i + 2] == '\r' && data[i + 3] == '\n')
                    return i;
            }
            return -1;
        }
    }
}
