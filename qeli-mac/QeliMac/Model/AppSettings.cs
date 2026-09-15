using System.IO;
using System.Text.Json;

namespace QeliMac.Model;

/// <summary>App-wide settings persisted to ~/Library/Application Support/Qeli/settings.json.</summary>
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
    public bool ProbeReachability { get; set; } = false; // privacy-safe opt-in; manual reachability checks remain available.
    public int ProbeIntervalSecs { get; set; } = 30;    // auto-poll period (only when ProbeReachability is on); clamped 10..3600
    public bool AutoStart { get; set; }                 // run GUI at login (LaunchAgent)
    public bool AutoConnect { get; set; }               // connect on app start
    public string? AutoConnectProfile { get; set; }     // profile name to auto-connect
    public bool StartMinimized { get; set; }            // start hidden in the menu bar
    public bool ServiceEnabled { get; set; }            // desired: run as a launchd daemon
    public string? ServiceProfile { get; set; }         // profile the launchd daemon runs
    public string? LastProfile { get; set; }             // Id of the last-selected profile (restored on next launch, 5.1)
    // Local SOCKS/HTTP proxy routing (v2rayN-like). See Qeli.Shared.Geo.ProxyRoutePreset.
    public string ProxyRoutePreset { get; set; } = "proxy-all";

    private static readonly string Dir = Paths.UserDir;
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

    private static AppSettings Read(string path) =>
        JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
        ?? throw new JsonException("settings root is null");

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
