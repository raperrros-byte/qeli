using System.Net;
using System.Net.Sockets;

namespace Qeli.Shared.Geo;

public readonly record struct Ipv4Cidr(uint Network, byte Prefix)
{
    public bool Contains(uint ip)
    {
        if (Prefix == 0) return true;
        if (Prefix >= 32) return ip == Network;
        uint mask = uint.MaxValue << (32 - Prefix);
        return (ip & mask) == (Network & mask);
    }
}

/// <summary>Loaded subset of geoip.dat for selected country codes.</summary>
public sealed class GeoIpIndex
{
    private readonly Dictionary<string, List<Ipv4Cidr>> _byCode =
        new(StringComparer.OrdinalIgnoreCase);

    public static GeoIpIndex Load(string path, IEnumerable<string> codes)
    {
        var want = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        var bytes = File.ReadAllBytes(path);
        var index = new GeoIpIndex();
        var reader = new ProtoReader(bytes);
        while (reader.HasMore)
        {
            if (!reader.TryReadTag(out int field, out int wt)) break;
            if (field != 1 || wt != 2) { reader.Skip(wt); continue; }
            ParseGeoIp(reader.ReadBytes(), want, index);
        }
        return index;
    }

    private static void ParseGeoIp(ReadOnlySpan<byte> msg, HashSet<string> want, GeoIpIndex index)
    {
        string? code = null;
        var cidrs = new List<Ipv4Cidr>();
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
                    if (TryParseCidr(r.ReadBytes(), out var c)) cidrs.Add(c);
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }
        if (code == null || !want.Contains(code)) return;
        if (!index._byCode.TryGetValue(code, out var list))
        {
            list = new List<Ipv4Cidr>();
            index._byCode[code] = list;
        }
        list.AddRange(cidrs);
    }

    private static bool TryParseCidr(ReadOnlySpan<byte> msg, out Ipv4Cidr cidr)
    {
        cidr = default;
        byte[]? ip = null;
        byte prefix = 0;
        var r = new ProtoReader(msg);
        while (r.HasMore)
        {
            if (!r.TryReadTag(out int field, out int wt)) break;
            switch (field)
            {
                case 1 when wt == 2:
                    ip = r.ReadBytes().ToArray();
                    break;
                case 2 when wt == 0:
                    prefix = (byte)Math.Min(32, (int)r.ReadVarint());
                    break;
                default:
                    r.Skip(wt);
                    break;
            }
        }
        if (ip == null || ip.Length != 4) return false;
        uint network = ((uint)ip[0] << 24) | ((uint)ip[1] << 16) | ((uint)ip[2] << 8) | ip[3];
        cidr = new Ipv4Cidr(network, prefix);
        return true;
    }

    public bool MatchAny(IPAddress? ip, params string[] codes)
    {
        if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        uint v = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        foreach (var code in codes)
        {
            if (!_byCode.TryGetValue(code, out var list)) continue;
            foreach (var c in list)
                if (c.Contains(v)) return true;
        }
        return false;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || b[0] == 127
            || (b[0] == 169 && b[1] == 254);
    }
}
