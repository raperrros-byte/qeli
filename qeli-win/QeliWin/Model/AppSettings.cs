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
    public bool ProbeReachability { get; set; } = true; // poll each profile's server for the reachability dot/latency AUTOMATICALLY (opt-out: sends a PQ ClientHello per profile). When off, only a manual "check reachability" probes.
    public int ProbeIntervalSecs { get; set; } = 30;    // auto-poll period (only when ProbeReachability is on); clamped 10..3600
    public bool AutoStart { get; set; }                 // run GUI at Windows logon (scheduled task)
    public bool AutoConnect { get; set; }               // connect on app start
    public string? AutoConnectProfile { get; set; }     // profile name to auto-connect
    public bool StartMinimized { get; set; }            // start hidden in the tray
    public bool ServiceEnabled { get; set; }            // desired: run as a Windows service
    public string? ServiceProfile { get; set; }         // profile the Windows service runs
    // Global traffic mode for ALL profiles (main window): "tunnel" = full-tunnel, no local
    // proxy; "proxy" = split-tunnel + local SOCKS/HTTP. Changing it rewrites every profile.
    public string TrafficMode { get; set; } = "tunnel"; // "tunnel" | "proxy"
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
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QeliWin");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static AppSettings? _current;
    public static AppSettings Current => _current ??= Load();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        _current = this;
    }
}
