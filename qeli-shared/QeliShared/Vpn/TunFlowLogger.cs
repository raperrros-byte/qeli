using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Qeli.Shared.Vpn;

/// <summary>
/// v2rayN-style connection log lines from TUN uplink (full-tunnel / any IP via Wintun).
/// </summary>
public static class TunFlowLogger
{
    private static readonly ConcurrentDictionary<string, long> Recent = new(StringComparer.Ordinal);
    private static long _windowStartMs;
    private static int _windowCount;
    private const int MaxPerSecond = 80;
    private const int DedupeMs = 8_000;
    private const int MaxRecent = 8_000;

    public static void ObserveUplink(byte[] pkt, Action<string> log)
    {
        if (pkt.Length < 20 || (pkt[0] >> 4) != 4) return;
        int ihl = (pkt[0] & 0x0F) * 4;
        if (ihl < 20 || pkt.Length < ihl + 4) return;

        byte proto = pkt[9];
        var dst = new IPAddress(pkt.AsSpan(16, 4));
        if (Geo.GeoIpIndex.IsPrivate(dst)) return;

        if (proto == 6) // TCP
        {
            if (pkt.Length < ihl + 14) return;
            int dport = (pkt[ihl + 2] << 8) | pkt[ihl + 3];
            byte flags = pkt[ihl + 13];
            bool syn = (flags & 0x02) != 0;
            bool ack = (flags & 0x10) != 0;
            if (!syn || ack) return;
            Emit(log, $"from tun accepted tcp:{dst}:{dport} [tunnel -> proxy]", $"tcp:{dst}:{dport}");
            return;
        }

        if (proto == 17) // UDP
        {
            if (pkt.Length < ihl + 8) return;
            int dport = (pkt[ihl + 2] << 8) | pkt[ihl + 3];
            if (dport == 53)
            {
                if (!TryParseDnsQueryName(pkt.AsSpan(ihl + 8), out var name) || name.Length == 0)
                    name = dst.ToString();
                Emit(log, $"from tun accepted udp:{dst}:53 ({name}) [tunnel -> proxy]", "dns:" + name);
            }
            else
            {
                // First datagram of a flow (deduped) — covers QUIC / games / etc.
                Emit(log, $"from tun accepted udp:{dst}:{dport} [tunnel -> proxy]", $"udp:{dst}:{dport}");
            }
        }
    }

    private static void Emit(Action<string> log, string line, string key)
    {
        long now = Environment.TickCount64;
        if (Recent.TryGetValue(key, out var prev) && now - prev < DedupeMs)
            return;

        long win = Interlocked.Read(ref _windowStartMs);
        if (now - win >= 1000)
        {
            Interlocked.Exchange(ref _windowStartMs, now);
            Interlocked.Exchange(ref _windowCount, 0);
        }
        if (Interlocked.Increment(ref _windowCount) > MaxPerSecond)
            return;

        Recent[key] = now;
        if (Recent.Count > MaxRecent)
            Prune(now);

        try { log(line); } catch { }
    }

    private static void Prune(long now)
    {
        foreach (var kv in Recent)
        {
            if (now - kv.Value > DedupeMs)
                Recent.TryRemove(kv.Key, out _);
        }
    }

    private static bool TryParseDnsQueryName(ReadOnlySpan<byte> dns, out string name)
    {
        name = "";
        if (dns.Length < 12) return false;
        int qd = (dns[4] << 8) | dns[5];
        if (qd < 1) return false;
        int i = 12;
        var sb = new StringBuilder(64);
        while (i < dns.Length)
        {
            int lab = dns[i++];
            if (lab == 0) break;
            if ((lab & 0xC0) != 0) return false;
            if (i + lab > dns.Length) return false;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.ASCII.GetString(dns.Slice(i, lab)));
            i += lab;
            if (sb.Length > 253) return false;
        }
        name = sb.ToString();
        return name.Length > 0;
    }
}
