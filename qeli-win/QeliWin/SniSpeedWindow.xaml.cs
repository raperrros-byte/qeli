using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using Qeli.Shared;
using Qeli.Shared.Model;
using QeliWin.Model;

namespace QeliWin;

public partial class SniSpeedWindow : Window
{
    public const int MaxCombos = 400;

    private readonly ObservableCollection<ModeVm> _modes = new();
    private readonly ObservableCollection<HostVm> _hosts = new();
    private readonly ObservableCollection<ComboVm> _rows = new();
    private CancellationTokenSource? _cts;
    private readonly Action<VpnConfig, string>? _applyBest;
    private readonly IReadOnlyList<VpnConfig> _profiles;

    public record AppliedPick(VpnConfig Profile, string Sni);

    public AppliedPick? LastBest { get; private set; }

    public SniSpeedWindow(IReadOnlyList<VpnConfig> profiles, Action<VpnConfig, string>? applyBest = null)
    {
        InitializeComponent();
        _profiles = profiles;
        _applyBest = applyBest;
        ModesList.ItemsSource = _modes;
        HostsList.ItemsSource = _hosts;
        Grid.ItemsSource = _rows;
        DelayBox.Text = Math.Clamp(AppSettings.Current.SniProbeDelayMs, 0, 60_000).ToString();
        MeasureBox.Text = (Math.Clamp(AppSettings.Current.SniProbeMinDurationMs, 500, 60_000) / 1000.0)
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        MaxMbBox.Text = (Math.Clamp(AppSettings.Current.SniProbeBytes, 256 * 1024, 8 * 1024 * 1024)
            / (1024.0 * 1024.0))
            .ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        Reload();
    }

    private void Reload()
    {
        var s = AppSettings.Current;
        _modes.Clear();
        foreach (var p in _profiles)
        {
            var label = TransportAuto.TransportLabel(p.Protocol, p.WireMode, p.QuicEnabled);
            _modes.Add(new ModeVm
            {
                Profile = p,
                Label = $"{label} · {p.DisplayName}",
                Selected = true,
            });
        }

        var all = SniCatalog.Merge(s.CustomSniHosts);
        var selected = new HashSet<string>(s.SniSpeedSelection ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0)
        {
            foreach (var h in TransportAuto.SniPresets.Take(8))
                selected.Add(h);
        }
        _hosts.Clear();
        foreach (var h in all)
            _hosts.Add(new HostVm { Host = h, Selected = selected.Contains(h) });
        RefreshStatusCount();
    }

    private void RefreshStatusCount()
    {
        var catalog = _hosts.Count;
        var sni = _hosts.Count(h => h.Selected);
        var modes = _modes.Count(m => m.Selected);
        StatusLine.Text = Loc.F("SniCatalogCount", catalog, sni, modes, sni * modes);
    }

    private void OnAddHost(object sender, RoutedEventArgs e)
    {
        var n = SniCatalog.Normalize(CustomHostBox.Text);
        if (n == null)
        {
            MessageBox.Show(this, Loc.T("SniEmpty"), Loc.T("SniSpeedTitle"));
            return;
        }
        var s = AppSettings.Current;
        if (!s.CustomSniHosts.Any(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase)))
        {
            s.CustomSniHosts.Add(n);
            s.Save();
        }
        CustomHostBox.Text = "";
        Reload();
        var row = _hosts.FirstOrDefault(r => string.Equals(r.Host, n, StringComparison.OrdinalIgnoreCase));
        if (row != null) row.Selected = true;
        RefreshStatusCount();
    }

    private void OnSelectAllHosts(object sender, RoutedEventArgs e)
    {
        foreach (var h in _hosts) h.Selected = true;
        RefreshStatusCount();
    }

    private void OnSelectTop(object sender, RoutedEventArgs e)
    {
        foreach (var h in _hosts) h.Selected = false;
        foreach (var h in _hosts.Take(20)) h.Selected = true;
        RefreshStatusCount();
    }

    private void OnClearHosts(object sender, RoutedEventArgs e)
    {
        foreach (var h in _hosts) h.Selected = false;
        RefreshStatusCount();
    }

    private void OnSelectAllModes(object sender, RoutedEventArgs e)
    {
        foreach (var m in _modes) m.Selected = true;
        RefreshStatusCount();
    }

    private void OnClearModes(object sender, RoutedEventArgs e)
    {
        foreach (var m in _modes) m.Selected = false;
        RefreshStatusCount();
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        var modes = _modes.Where(m => m.Selected).ToList();
        var hosts = _hosts.Where(h => h.Selected).Select(h => h.Host).ToList();
        if (modes.Count == 0 || hosts.Count == 0)
        {
            MessageBox.Show(this, Loc.T("SniSelectSome"), Loc.T("SniSpeedTitle"));
            return;
        }
        var combos = modes.Count * hosts.Count;
        if (combos > MaxCombos)
        {
            MessageBox.Show(this, Loc.F("SniTooMany", combos, MaxCombos), Loc.T("SniSpeedTitle"));
            return;
        }

        if (!int.TryParse(DelayBox.Text?.Trim(), out var delayMs))
            delayMs = 750;
        delayMs = Math.Clamp(delayMs, 0, 60_000);

        if (!double.TryParse(MeasureBox.Text?.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var measureSec)
            && !double.TryParse(MeasureBox.Text?.Trim(), out measureSec))
            measureSec = 3;
        var minDurationMs = Math.Clamp((int)Math.Round(measureSec * 1000), 500, 60_000);

        if (!double.TryParse(MaxMbBox.Text?.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var maxMb)
            && !double.TryParse(MaxMbBox.Text?.Trim(), out maxMb))
            maxMb = 2;
        var probeBytes = Math.Clamp((int)Math.Round(maxMb * 1024 * 1024), 256 * 1024, 8 * 1024 * 1024);

        var s = AppSettings.Current;
        s.SniSpeedSelection = hosts;
        s.SniProbeDelayMs = delayMs;
        s.SniProbeMinDurationMs = minDurationMs;
        s.SniProbeBytes = probeBytes;
        s.Save();

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        RunBtn.IsEnabled = false;
        _rows.Clear();
        StatusLine.Text = Loc.F("SniTesting", combos);

        try
        {
            var modeRttCache = new Dictionary<VpnConfig, long?>();
            var sniCache = new Dictionary<string, SniSpeedRow>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            foreach (var mode in modes)
            {
                if (!modeRttCache.ContainsKey(mode.Profile))
                    modeRttCache[mode.Profile] = await ProbeModeRttAsync(mode.Profile, ct).ConfigureAwait(true);

                foreach (var host in hosts)
                {
                    ct.ThrowIfCancellationRequested();
                    if (done > 0 && delayMs > 0)
                        await Task.Delay(delayMs, ct).ConfigureAwait(true);

                    if (!sniCache.TryGetValue(host, out var sniRow))
                    {
                        sniRow = await SniSpeedProbe.ProbeAsync(
                            host, probeBytes, minDurationMs, ct: ct).ConfigureAwait(true);
                        sniCache[host] = sniRow;
                    }

                    var modeRtt = modeRttCache[mode.Profile];
                    var modeOk = modeRtt != null;
                    var ok = modeOk && sniRow.Ok;
                    var vm = new ComboVm
                    {
                        Profile = mode.Profile,
                        ModeLabel = mode.Label,
                        Host = host,
                        ModeRttMs = modeRtt,
                        TlsMs = sniRow.TlsMs,
                        Mbps = sniRow.Mbps,
                        Ok = ok,
                        Status = !modeOk
                            ? "mode unreachable"
                            : (sniRow.Ok ? sniRow.Status : (sniRow.Error ?? "sni fail")),
                    };
                    _rows.Add(vm);
                    done++;
                    StatusLine.Text = Loc.F("SniTesting", $"{done}/{combos}");
                }
            }

            var ordered = _rows
                .OrderByDescending(r => r.Ok)
                .ThenBy(r => r.TlsMs ?? long.MaxValue)
                .ThenBy(r => r.ModeRttMs ?? long.MaxValue)
                .ThenByDescending(r => r.Mbps ?? 0)
                .ToList();
            _rows.Clear();
            foreach (var r in ordered) _rows.Add(r);

            var best = ordered.FirstOrDefault(r => r.Ok);
            LastBest = best == null ? null : new AppliedPick(best.Profile, best.Host);
            StatusLine.Text = best == null
                ? Loc.T("SniNoneOk")
                : Loc.F("SniBest", best.ModeLabel, best.Host,
                    best.TlsMs?.ToString() ?? "-", best.Mbps?.ToString("F2") ?? "-");
        }
        catch (OperationCanceledException)
        {
            StatusLine.Text = Loc.T("SniCancelled");
        }
        catch (Exception ex)
        {
            StatusLine.Text = ex.GetBaseException().Message;
        }
        finally
        {
            RunBtn.IsEnabled = true;
        }
    }

    private static async Task<long?> ProbeModeRttAsync(VpnConfig profile, CancellationToken ct)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var client = new TcpClient();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(3_000);
            await client.ConnectAsync(profile.ServerAddress, profile.Port, linked.Token).ConfigureAwait(false);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private void OnApplyBest(object sender, RoutedEventArgs e)
    {
        var best = _rows.Where(r => r.Ok)
            .OrderBy(r => r.TlsMs ?? long.MaxValue)
            .ThenBy(r => r.ModeRttMs ?? long.MaxValue)
            .ThenByDescending(r => r.Mbps ?? 0)
            .FirstOrDefault();
        if (best == null)
        {
            MessageBox.Show(this, Loc.T("SniNoneOk"), Loc.T("SniSpeedTitle"));
            return;
        }
        LastBest = new AppliedPick(best.Profile, best.Host);
        _applyBest?.Invoke(best.Profile, best.Host);
        DialogResult = true;
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private sealed class ModeVm : INotifyPropertyChanged
    {
        private bool _selected = true;
        public VpnConfig Profile { get; init; } = null!;
        public string Label { get; init; } = "";
        public bool Selected
        {
            get => _selected;
            set { _selected = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class HostVm : INotifyPropertyChanged
    {
        private bool _selected;
        public string Host { get; init; } = "";
        public bool Selected
        {
            get => _selected;
            set { _selected = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private sealed class ComboVm
    {
        public VpnConfig Profile { get; init; } = null!;
        public string ModeLabel { get; init; } = "";
        public string Host { get; init; } = "";
        public long? ModeRttMs { get; init; }
        public long? TlsMs { get; init; }
        public double? Mbps { get; init; }
        public bool Ok { get; init; }
        public string Status { get; init; } = "";
        public string ModeRttText => ModeRttMs is long ms ? $"{ms} ms" : "—";
        public string TlsText => TlsMs is long ms ? $"{ms} ms" : "—";
        public string MbpsText => Mbps is double m ? $"{m:F2}" : "—";
    }
}
