using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using QeliWin.Model;

namespace QeliWin.Vpn;

/// <summary>
/// Forces selected apps through Qeli's local SOCKS listener without app proxy settings.
/// Uses ProxyBridge CLI (same model as proxy-cursor → InterceptSuite ProxyBridge) instead of
/// fragile WinDivert DNAT to loopback. Bundled under <c>ProxyBridge/</c> next to the exe, or
/// a system install / <see cref="AppSettings.ProxyBridgePath"/>.
/// </summary>
internal sealed class AppProxyRedirector : IDisposable
{
    private readonly IReadOnlyList<string> _apps;
    private readonly ushort _proxyPort;
    private readonly Action<string>? _log;
    private Process? _process;
    private string? _profilePath;
    private bool _disposed;

    public AppProxyRedirector(
        IReadOnlyList<string> apps,
        ushort proxyPort,
        IPAddress carrierIp,
        ushort carrierPort,
        Action<string>? log = null)
    {
        // Carrier is owned by Qeli; we mark our process DIRECT in the profile so ProxyBridge
        // never captures the VPN control channel.
        _ = carrierIp;
        _ = carrierPort;
        _apps = apps;
        _proxyPort = proxyPort;
        _log = log;
    }

    public void Start()
    {
        if (_process != null) return;

        string? cli = ResolveProxyBridgeCli();
        if (cli == null)
        {
            throw new InvalidOperationException(
                "ProxyBridge_CLI.exe not found. Place ProxyBridge next to QeliWin "
                + "(ProxyBridge/ProxyBridge_CLI.exe) or install from "
                + "https://github.com/InterceptSuite/ProxyBridge/releases "
                + "(same approach as proxy-cursor). Mode 3 manual SOCKS still works.");
        }

        var processNames = _apps
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (processNames.Count == 0)
            throw new InvalidOperationException("No application names for proxy intercept");

        string selfName = Path.GetFileName(Environment.ProcessPath ?? "QeliWin.exe");
        string proxyUri = $"socks5://127.0.0.1:{_proxyPort}";

        _profilePath = Path.Combine(
            Path.GetTempPath(), $"qeli-proxybridge-{Environment.ProcessId}.pbprofile");
        File.WriteAllText(
            _profilePath,
            BuildProfileJson(selfName, processNames, _proxyPort),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (!TryStart(cli, new[] { "--profile", _profilePath, "--verbose", "2" }, out string err))
            throw new InvalidOperationException($"ProxyBridge failed to start: {err}");

        _log?.Invoke(
            $"App proxy intercept ACTIVE via ProxyBridge: {string.Join(';', processNames)} → {proxyUri} "
            + $"(cli={cli})");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch { /* best effort */ }
        try { _process?.Dispose(); } catch { }
        _process = null;
        if (_profilePath != null)
        {
            try { File.Delete(_profilePath); } catch { }
            _profilePath = null;
        }
        _log?.Invoke("App proxy intercept stopped (ProxyBridge)");
    }

    private bool TryStart(string cli, string[] args, out string error)
    {
        error = "";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                WorkingDirectory = Path.GetDirectoryName(cli) ?? "",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var bootLog = new StringBuilder();
            void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                bootLog.AppendLine(line);
                _log?.Invoke($"ProxyBridge: {line}");
            }
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) => OnLine(e.Data);

            if (!proc.Start())
            {
                error = "Start() returned false";
                return false;
            }
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (proc.WaitForExit(1000))
            {
                error = $"exited early code={proc.ExitCode}: {bootLog}";
                try { proc.Dispose(); } catch { }
                return false;
            }

            _process = proc;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    private static string BuildProfileJson(
        string selfName, IReadOnlyList<string> processNames, ushort proxyPort)
    {
        var rules = new List<object>
        {
            new
            {
                ProcessName = selfName,
                TargetHosts = "*",
                TargetPorts = "*",
                Protocol = "TCP",
                Action = "DIRECT",
                IsEnabled = true,
            },
            new
            {
                ProcessName = "ProxyBridge_CLI.exe;ProxyBridge.exe",
                TargetHosts = "*",
                TargetPorts = "*",
                Protocol = "TCP",
                Action = "DIRECT",
                IsEnabled = true,
            },
            new
            {
                ProcessName = string.Join(";", processNames),
                TargetHosts = "*",
                TargetPorts = "*",
                Protocol = "TCP",
                Action = "PROXY",
                IsEnabled = true,
                ProxyConfigId = 1,
            },
        };

        var profile = new
        {
            Version = "1.0",
            LocalhostViaProxy = false,
            IsTrafficLoggingEnabled = true,
            ProxyConfigs = new object[]
            {
                new
                {
                    Id = 1,
                    Type = "socks5",
                    Host = "127.0.0.1",
                    Port = proxyPort.ToString(),
                    Username = "",
                    Password = "",
                },
            },
            ProxyRules = rules,
        };
        return JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static string? ResolveProxyBridgeCli()
    {
        var candidates = new List<string>();
        string? env = Environment.GetEnvironmentVariable("QELI_PROXYBRIDGE");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env.Trim());

        string? configured = AppSettings.Current.ProxyBridgePath;
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(configured.Trim());

        string? baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            candidates.Add(Path.Combine(baseDir, "ProxyBridge", "ProxyBridge_CLI.exe"));
            candidates.Add(Path.Combine(baseDir, "ProxyBridge_CLI.exe"));
        }

        // Dev / publish tree: qeli-win/dist/ProxyBridge
        try
        {
            string? asm = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrEmpty(asm))
            {
                candidates.Add(Path.Combine(asm, "ProxyBridge", "ProxyBridge_CLI.exe"));
                var dist = Path.GetFullPath(Path.Combine(asm, "..", "ProxyBridge", "ProxyBridge_CLI.exe"));
                candidates.Add(dist);
            }
        }
        catch { /* ignore */ }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        candidates.Add(Path.Combine(pf, "ProxyBridge", "ProxyBridge_CLI.exe"));
        string? pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrEmpty(pf86))
            candidates.Add(Path.Combine(pf86, "ProxyBridge", "ProxyBridge_CLI.exe"));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Programs", "ProxyBridge", "ProxyBridge_CLI.exe"));

        foreach (string path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path))
                    return Path.GetFullPath(path);
            }
            catch { /* skip */ }
        }
        return null;
    }
}
