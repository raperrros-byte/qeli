using System.Reflection;

namespace Qeli.Shared;

/// <summary>Built-in + user SNI / TLS front-host catalog.</summary>
public static class SniCatalog
{
    private static IReadOnlyList<string>? _builtin;

    /// <summary>Hosts shipped with the app (community Reality SNI lists + CDN fronts).</summary>
    public static IReadOnlyList<string> Builtin
    {
        get
        {
            if (_builtin != null) return _builtin;
            var asm = typeof(SniCatalog).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("sni_hosts.txt", StringComparison.OrdinalIgnoreCase));
            if (name == null)
            {
                _builtin = TransportAuto.SniPresets;
                return _builtin;
            }
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var list = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                var host = Normalize(line);
                if (host != null) list.Add(host);
            }
            _builtin = list.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(h => h).ToList();
            return _builtin;
        }
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var h = raw.Trim().ToLowerInvariant();
        var hash = h.IndexOf('#');
        if (hash >= 0) h = h[..hash].Trim();
        if (h.Length == 0 || h.StartsWith(';')) return null;
        if (h.Contains("://", StringComparison.Ordinal))
            h = h.Split(new[] { "://" }, 2, StringSplitOptions.None)[1];
        h = h.Split('/')[0];
        var colon = h.LastIndexOf(':');
        if (colon > 0 && h[(colon + 1)..].All(char.IsDigit))
            h = h[..colon];
        h = h.Trim('.');
        if (h.Length is < 3 or > 80) return null;
        if (!h.Contains('.')) return null;
        return h;
    }

    public static IReadOnlyList<string> Merge(IEnumerable<string>? custom)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in Builtin)
            set.Add(h);
        if (custom != null)
        {
            foreach (var c in custom)
            {
                var n = Normalize(c);
                if (n != null) set.Add(n);
            }
        }
        return set.ToList();
    }
}
