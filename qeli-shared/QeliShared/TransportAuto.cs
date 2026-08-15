namespace Qeli.Shared;

/// <summary>Auto transport ranking — mirrors Rust <c>config::auto_transport</c>.</summary>
public static class TransportAuto
{
    public static readonly string[] DefaultOrder =
    {
        "reality-tls", "fake-tls", "obfs", "plain", "quic", "udp-quic", "udp-obfs", "udp"
    };

    /// <summary>Short default picker list (main-screen combo). Full catalog: <see cref="SniCatalog.Builtin"/>.</summary>
    public static readonly string[] SniPresets =
    {
        "www.cloudflare.com",
        "www.google.com",
        "www.microsoft.com",
        "www.apple.com",
        "www.amazon.com",
        "www.speedtest.net",
        "github.com",
        "cdn.jsdelivr.net",
        "www.bing.com",
        "login.live.com",
        "www.nvidia.com",
        "www.wikipedia.org",
    };

    public static string TransportLabel(string protocol, string mode, bool quic)
    {
        var proto = (protocol ?? "").Trim().ToLowerInvariant();
        var m = (mode ?? "").Trim().ToLowerInvariant();
        if (proto == "udp")
        {
            if (quic || m == "udp-quic") return "quic";
            if (m is "obfs" or "udp-obfs") return "udp-obfs";
            return "udp";
        }
        return m switch
        {
            "reality-tls" or "reality" => "reality-tls",
            "obfs" => "obfs",
            "plain" => "plain",
            "fake-tls" or "tls" or "" => "fake-tls",
            _ => m,
        };
    }

    public static int PreferenceRank(string label, IReadOnlyList<string>? order = null)
    {
        order ??= DefaultOrder;
        for (var i = 0; i < order.Count; i++)
            if (string.Equals(order[i], label, StringComparison.OrdinalIgnoreCase))
                return i;
        return 900 + label.Length;
    }

    public readonly record struct Ranked(int Index, long? RttMs, int Preference);

    public static List<Ranked> Rank(
        IReadOnlyList<string> labels,
        IReadOnlyList<bool> ok,
        IReadOnlyList<long?> rttMs,
        IReadOnlyList<string>? order = null)
    {
        order ??= DefaultOrder;
        var picks = new List<Ranked>();
        for (var i = 0; i < labels.Count; i++)
        {
            if (!ok[i]) continue;
            picks.Add(new Ranked(i, rttMs[i], PreferenceRank(labels[i], order)));
        }
        picks.Sort((a, b) =>
        {
            var ba = (a.RttMs ?? long.MaxValue) / 40;
            var bb = (b.RttMs ?? long.MaxValue) / 40;
            var c = ba.CompareTo(bb);
            if (c != 0) return c;
            c = a.Preference.CompareTo(b.Preference);
            if (c != 0) return c;
            c = (a.RttMs ?? long.MaxValue).CompareTo(b.RttMs ?? long.MaxValue);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
        return picks;
    }

    public static string ApplySniToIni(string ini, string sni)
    {
        var host = (sni ?? "").Trim();
        if (host.Length == 0) throw new ArgumentException("sni must not be empty");
        var lines = ini.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("sni", StringComparison.OrdinalIgnoreCase) && t.Contains('='))
            {
                lines[i] = "sni = " + host;
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            var insertAt = lines.FindIndex(l => l.Trim().Equals("[qeli]", StringComparison.OrdinalIgnoreCase));
            insertAt = insertAt >= 0 ? insertAt + 1 : lines.Count;
            lines.Insert(insertAt, "sni = " + host);
        }
        return string.Join("\n", lines);
    }
}
