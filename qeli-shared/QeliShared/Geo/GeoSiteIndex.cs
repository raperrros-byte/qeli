namespace Qeli.Shared.Geo;

/// <summary>v2ray Domain.Type</summary>
public enum GeoDomainType : byte
{
    Plain = 0,
    Regex = 1,
    RootDomain = 2,
    Full = 3,
}

public readonly record struct GeoDomainRule(GeoDomainType Type, string Value);

/// <summary>Loaded subset of geosite.dat for selected tags (case-insensitive).</summary>
public sealed class GeoSiteIndex
{
    private readonly Dictionary<string, List<GeoDomainRule>> _byTag =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Tags => _byTag.Keys;

    public static GeoSiteIndex Load(string path, IEnumerable<string> tags)
    {
        var want = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        var bytes = File.ReadAllBytes(path);
        var index = new GeoSiteIndex();
        var reader = new ProtoReader(bytes);
        while (reader.HasMore)
        {
            if (!reader.TryReadTag(out int field, out int wt)) break;
            if (field != 1 || wt != 2) { reader.Skip(wt); continue; }
            ParseGeoSite(reader.ReadBytes(), want, index);
        }
        return index;
    }

    private static void ParseGeoSite(ReadOnlySpan<byte> msg, HashSet<string> want, GeoSiteIndex index)
    {
        string? code = null;
        var domains = new List<GeoDomainRule>();
        var r = new ProtoReader(msg);
        while (r.HasMore)
        {
            if (!r.TryReadTag(out int field, out int wt)) break;
            switch (field)
            {
                case 1 when wt == 2:
                    code = r.ReadString();
                    break;
                case 2 when wt == 2:
                    domains.Add(ParseDomain(r.ReadBytes()));
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }
        if (code == null || !want.Contains(code)) return;
        if (!index._byTag.TryGetValue(code, out var list))
        {
            list = new List<GeoDomainRule>();
            index._byTag[code] = list;
        }
        list.AddRange(domains);
    }

    private static GeoDomainRule ParseDomain(ReadOnlySpan<byte> msg)
    {
        GeoDomainType type = GeoDomainType.Plain;
        string value = "";
        var r = new ProtoReader(msg);
        while (r.HasMore)
        {
            if (!r.TryReadTag(out int field, out int wt)) break;
            switch (field)
            {
                case 1 when wt == 0:
                    type = (GeoDomainType)(int)r.ReadVarint();
                    break;
                case 2 when wt == 2:
                    value = r.ReadString();
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }
        return new GeoDomainRule(type, value);
    }

    public bool MatchAny(string host, params string[] tags)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var tag in tags)
        {
            if (!_byTag.TryGetValue(tag, out var rules)) continue;
            foreach (var rule in rules)
            {
                if (MatchOne(host, rule)) return true;
            }
        }
        return false;
    }

    private static bool MatchOne(string host, GeoDomainRule rule)
    {
        var v = rule.Value.Trim().ToLowerInvariant();
        if (v.Length == 0) return false;
        return rule.Type switch
        {
            GeoDomainType.Full => host == v,
            GeoDomainType.RootDomain => host == v || host.EndsWith("." + v, StringComparison.Ordinal),
            GeoDomainType.Plain => host.Contains(v, StringComparison.Ordinal),
            GeoDomainType.Regex => SafeRegex(host, v),
            _ => false,
        };
    }

    private static bool SafeRegex(string host, string pattern)
    {
        try { return System.Text.RegularExpressions.Regex.IsMatch(host, pattern); }
        catch { return false; }
    }
}
