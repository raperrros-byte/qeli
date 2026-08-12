namespace Qeli.Shared.Geo;

/// <summary>Download + cache directory for geosite.dat / geoip.dat.</summary>
public static class GeoAssetStore
{
    public const string GeositeFile = "geosite.dat";
    public const string GeoipFile = "geoip.dat";

    // Primary: RU-oriented lists (same source v2rayN RUv1 presets use).
    // Fallback: Loyalsoldier community dat.
    // Prefer CDN/raw first — github.com/releases often hits HTTP/2 REFUSED_STREAM behind proxies.
    public static readonly string[] GeositeUrls =
    {
        "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geosite.dat",
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geosite.dat",
        "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geosite.dat",
        "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat",
    };

    public static readonly string[] GeoipUrls =
    {
        "https://cdn.jsdelivr.net/gh/runetfreedom/russia-v2ray-rules-dat@release/geoip.dat",
        "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/release/geoip.dat",
        "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/geoip.dat",
        "https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geoip.dat",
    };

    private static string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Qeli", "geo");

    private static readonly object Gate = new();
    private static GeoSiteIndex? _site;
    private static GeoIpIndex? _ip;
    private static string _loadedPreset = "";

    public static string DirectoryPath => _dir;
    public static string GeositePath => Path.Combine(_dir, GeositeFile);
    public static string GeoipPath => Path.Combine(_dir, GeoipFile);

    public static bool HasFiles => File.Exists(GeositePath) && File.Exists(GeoipPath);

    public static string StatusText()
    {
        if (!HasFiles) return "not downloaded";
        var gs = new FileInfo(GeositePath);
        var gi = new FileInfo(GeoipPath);
        return $"geosite {gs.Length / 1024} KB, geoip {gi.Length / 1024} KB — {gs.LastWriteTime:yyyy-MM-dd}";
    }

    public static void Configure(string directory)
    {
        _dir = directory;
        lock (Gate)
        {
            _site = null;
            _ip = null;
            _loadedPreset = "";
        }
    }

    public static async Task DownloadAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_dir);
        await DownloadOneAsync(GeositeUrls, GeositePath, "geosite.dat", log, ct).ConfigureAwait(false);
        await DownloadOneAsync(GeoipUrls, GeoipPath, "geoip.dat", log, ct).ConfigureAwait(false);
        lock (Gate)
        {
            _site = null;
            _ip = null;
            _loadedPreset = "";
        }
    }

    private static async Task DownloadOneAsync(
        string[] urls, string dest, string label, Action<string>? log, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                log?.Invoke($"Downloading {label}…");
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("qeli-geo/0.7");
                http.DefaultRequestVersion = System.Net.HttpVersion.Version11;
                http.DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower;
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var tmp = dest + ".tmp";
                await using (var fs = File.Create(tmp))
                {
                    await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                if (new FileInfo(tmp).Length < 1024)
                    throw new InvalidDataException($"{label} too small");
                File.Copy(tmp, dest, overwrite: true);
                File.Delete(tmp);
                log?.Invoke($"{label} OK ({new FileInfo(dest).Length / 1024} KB)");
                return;
            }
            catch (Exception e)
            {
                last = e;
                log?.Invoke($"{label} failed from {url}: {e.Message}");
            }
        }
        throw last ?? new IOException($"could not download {label}");
    }

    public static (GeoSiteIndex? Site, GeoIpIndex? Ip) GetOrLoad(string presetId)
    {
        var need = ProxyRoutePreset.TagsFor(presetId);
        lock (Gate)
        {
            if (_site != null && _ip != null && _loadedPreset == presetId)
                return (_site, _ip);
            if (!HasFiles) return (null, null);
            _site = GeoSiteIndex.Load(GeositePath, need.SiteTags);
            _ip = GeoIpIndex.Load(GeoipPath, need.IpTags);
            _loadedPreset = presetId;
            return (_site, _ip);
        }
    }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _site = null;
            _ip = null;
            _loadedPreset = "";
        }
    }
}
