using System.IO;
using System.Text.Json;

namespace QeliWin.Model;

/// <summary>App-wide settings persisted to %APPDATA%\QeliWin\settings.json.</summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "en";       // "en" | "ru" (default English)
    public string Theme { get; set; } = "system";      // "system" | "light" | "dark"
    // Timestamp shape in the log view. Same values as the server's
    // [logging] time_format; anything unknown renders as "datetime".
    public string LogTimeFormat { get; set; } = "datetime";  // "datetime" | "rfc3339" | "time" | "epoch" | "none"
    public string LogLevel { get; set; } = "info";           // "info" (compact) | "debug" (detailed)
    public bool ToastsEnabled { get; set; } = true;
    public bool CheckForUpdates { get; set; }           // opt-in: check GitHub for a newer version (default OFF)
    public bool ProbeReachability { get; set; } = false; // privacy-safe opt-in: automatic UDP probes emit a distinctive PQ first flight. Manual checks always remain available.
    public int ProbeIntervalSecs { get; set; } = 30;    // auto-poll period (only when ProbeReachability is on); clamped 10..3600
    public bool AutoStart { get; set; }                 // run GUI at Windows logon (scheduled task)
    public bool AutoConnect { get; set; }               // connect on app start
    public string? AutoConnectProfile { get; set; }     // profile name to auto-connect
    public bool StartMinimized { get; set; }            // start hidden in the tray
    public bool ServiceEnabled { get; set; }            // desired: run as a Windows service
    public string? ServiceProfile { get; set; }         // profile the Windows service runs
    // Global traffic defaults for ALL profiles (main window). PreferFullTunnel sets
    // gateway/routing; EnableLocalProxy can be on together with per-app filters.
    public bool PreferFullTunnel { get; set; } = true;
    public bool EnableLocalProxy { get; set; } = false;
    // Legacy: "tunnel" | "proxy". Migrated into PreferFullTunnel + EnableLocalProxy on load.
    public string TrafficMode { get; set; } = "tunnel";
    public int ProxyPort { get; set; } = 1080;
    public string ProxyMode { get; set; } = "mixed"; // socks5 | http | mixed
    // Local SOCKS/HTTP proxy routing (v2rayN-like). See Qeli.Shared.Geo.ProxyRoutePreset.
    public string ProxyRoutePreset { get; set; } = "proxy-all";
    // Server panel metrics (CPU/RAM charts). Empty URL = http://{connected-server}:8080
    public string ServerPanelUrl { get; set; } = "";
    public string ServerPanelUser { get; set; } = "admin";
    public string ServerPanelPassword { get; set; } = "";
    /// <summary>Probe all profiles and pick the best masking mode; enable failover on cut path.</summary>
    public bool AutoTransport { get; set; } = false;
    /// <summary>Next to Connect: on connect, probe modes×SNI and pick the best combo automatically.</summary>
    public bool AutoPickOnConnect { get; set; } = false;
    /// <summary>Shadowrocket-like: on give-up, try the next profile.</summary>
    public bool ProfileFailover { get; set; } = false;
    /// <summary>User-added SNI / TLS front hosts (merged with the built-in catalog).</summary>
    public List<string> CustomSniHosts { get; set; } = new();
    /// <summary>Hosts selected for the last SNI speed-test run.</summary>
    public List<string> SniSpeedSelection { get; set; } = new();
    /// <summary>Pause between sequential SNI / matrix probes (ms).</summary>
    public int SniProbeDelayMs { get; set; } = 750;
    /// <summary>Minimum HTTPS download window per SNI host (ms) — stable Mbps.</summary>
    public int SniProbeMinDurationMs { get; set; } = 3_000;
    /// <summary>Max bytes to download per SNI host (Range cap).</summary>
    public int SniProbeBytes { get; set; } = 2 * 1024 * 1024;
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QeliWin");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return ReadBackupOrDefault();
        try
        {
            return Read(FilePath);
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"AppSettings: settings.json unreadable ({error.Message})");
            try
            {
                File.Move(
                    FilePath,
                    FilePath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            }
            catch { /* preserve best-effort; never overwrite it here */ }
            return ReadBackupOrDefault();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var temp = FilePath + $".tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 16 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath))
                File.Replace(temp, FilePath, FilePath + ".bak");
            else
                File.Move(temp, FilePath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
        _current = this;
    }

    private static AppSettings Read(string path)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
            ?? throw new JsonException("settings root is null");
        settings.MigrateTrafficMode();
        return settings;
    }

    /// <summary>Map legacy TrafficMode XOR into PreferFullTunnel + EnableLocalProxy.
    /// New settings persist both flags; only upgrade when EnableLocalProxy is still off
    /// so a saved PreferFullTunnel=true + proxy ("both") is not rewritten to split.</summary>
    public void MigrateTrafficMode()
    {
        if (!EnableLocalProxy
            && TrafficMode.Equals("proxy", StringComparison.OrdinalIgnoreCase))
        {
            PreferFullTunnel = false;
            EnableLocalProxy = true;
        }
        else if (!EnableLocalProxy
                 && TrafficMode.Equals("both", StringComparison.OrdinalIgnoreCase))
        {
            PreferFullTunnel = false;
            EnableLocalProxy = true;
        }
        else if (!EnableLocalProxy
                 && TrafficMode.Equals("split", StringComparison.OrdinalIgnoreCase))
        {
            PreferFullTunnel = false;
        }
        // Keep TrafficMode in sync for older tools that still read it.
        TrafficMode = EnableLocalProxy
            ? (PreferFullTunnel ? "both" : "proxy")
            : (PreferFullTunnel ? "tunnel" : "split");
    }

    private static AppSettings ReadBackupOrDefault()
    {
        try
        {
            var backup = FilePath + ".bak";
            if (!File.Exists(backup)) return new AppSettings();
            var settings = Read(backup);
            System.Diagnostics.Debug.WriteLine("AppSettings: recovered settings.json from .bak");
            return settings;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"AppSettings: .bak recovery failed ({error.Message})");
            return new AppSettings();
        }
    }
}
