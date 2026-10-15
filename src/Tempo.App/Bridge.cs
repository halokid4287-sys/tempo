using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Hardware;
using Tempo.Core.Models;
using Tempo.Core.Optimizations;
using Tempo.Core.Games;
using Tempo.Core.Report;
using Tempo.Core.Updates;

namespace Tempo.App;

/// <summary>
/// JSON-RPC style bridge between the HTML UI and the C# services.
/// UI sends {id, method, params}; bridge replies {id, ok, result|error}
/// and pushes events as {evt, data}.
/// </summary>
public class Bridge : IDisposable
{
    private readonly Control _ui;
    private readonly CoreWebView2 _core;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HardwareService _hw = new();
    private readonly BackupService _backup = new();
    private readonly OptimizationEngine _opt;
    private readonly StartupService _startup = new();
    private readonly BloatwareService _bloat = new();
    private readonly ReportService _report = new();
    private readonly DiagnosticsService _diag = new();
    private readonly UpdateService _update = new();
    private readonly GameDetectionService _games = new();
    private readonly GameSettingsService _gameSettings;
    private List<DetectedGame> _gameCache = new();

    private SystemInfo? _sysInfo;
    private LiveMetricsService? _metrics;
    private System.Threading.Timer? _metricsTimer;
    private volatile bool _sampling;

    public Bridge(Control ui, CoreWebView2 core)
    {
        _ui = ui;
        _core = core;
        _opt = new OptimizationEngine(_backup);
        _gameSettings = new GameSettingsService(_backup);
    }

    public async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string id = "";
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetString() ?? "";
            var method = root.GetProperty("method").GetString() ?? "";
            var @params = root.TryGetProperty("params", out var p) ? p.Clone() : default;

            var result = await Task.Run(() => Dispatch(method, @params));
            Reply(id, true, result, null);
        }
        catch (Exception ex)
        {
            Tempo.Core.Logging.Log.Error("bridge", ex);
            Reply(id, false, null, ex.Message);
        }
    }

    private async Task<object?> Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "init":
            {
                _sysInfo = _hw.GetSystemInfo();
                StartMetrics();
                return new { system = _sysInfo, history = _backup.LoadHistory() };
            }
            case "rescan":
            {
                _sysInfo = _hw.GetSystemInfo();
                return _sysInfo;
            }
            case "scanOptimizations":
            {
                _sysInfo ??= _hw.GetSystemInfo();
                return _opt.Scan(_sysInfo);
            }
            case "applyOptimizations":
            {
                var ids = p.GetProperty("ids").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                _sysInfo ??= _hw.GetSystemInfo();
                var session = new BackupSession { Label = "Optimizations applied" };
                var results = new List<ApplyResult>();
                int n = 0;
                try
                {
                    foreach (var tid in ids)
                    {
                        n++;
                        var pre = "[" + n + "/" + ids.Count + "] ";
                        results.AddRange(_opt.Apply(new[] { tid }, _sysInfo, session, msg => Push("progress", new { msg = pre + msg })));
                        _backup.UpsertSession(session); // persist as we go — a crash never loses records
                    }
                }
                finally { _backup.UpsertSession(session); }
                _sysInfo = _hw.GetSystemInfo(); // refresh power plan state etc.
                return new { results, session = session.Id };
            }
            case "listStartup":
                return _startup.List();
            case "setStartup":
            {
                var name = p.GetProperty("name").GetString() ?? "";
                var scope = p.GetProperty("scope").GetString() ?? "";
                var location = p.GetProperty("location").GetString() ?? "";
                var enable = p.GetProperty("enable").GetBoolean();
                var session = new BackupSession { Label = (enable ? "Enabled" : "Disabled") + " startup: " + name };
                var err = _startup.SetEnabled(name, scope, location, enable, session);
                if (err != null) throw new InvalidOperationException(err);
                _backup.SaveSession(session);
                return _startup.List();
            }
            case "scanBloatware":
                return _bloat.Scan();
            case "runDiagnostic":
            {
                var diagId = p.GetProperty("id").GetString() ?? "";
                _sysInfo ??= _hw.GetSystemInfo();
                return _diag.Run(diagId, _sysInfo);
            }
            case "uninstallApps":
            {
                var ids = p.GetProperty("ids").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                bool restorePoint = p.TryGetProperty("restorePoint", out var rp) && rp.GetBoolean();
                var session = new BackupSession { Label = "Apps removed" };
                string? rpWarning = null;
                if (restorePoint)
                {
                    Push("progress", new { msg = "Creating System Restore point first (can take ~30 s)…" });
                    rpWarning = MaintenanceService.CreateRestorePoint("Tempo — before app removal");
                    Push("progress", new { msg = rpWarning == null ? "Restore point created." : "Restore point: " + rpWarning });
                }
                var results = new List<ApplyResult>();
                int n = 0;
                try
                {
                    foreach (var appId in ids)
                    {
                        n++;
                        var pre = "[" + n + "/" + ids.Count + "] ";
                        results.Add(await _bloat.Uninstall(appId, session, msg => Push("progress", new { msg = pre + msg })));
                        _backup.UpsertSession(session); // persist after each app — survives force-close mid-batch
                    }
                }
                finally { _backup.UpsertSession(session); }
                return new { results, restorePointWarning = rpWarning };
            }
            case "getHistory":
                return _backup.LoadHistory();
            case "restoreChange":
            {
                var sessionId = p.GetProperty("sessionId").GetString() ?? "";
                var changeId = p.GetProperty("changeId").GetString() ?? "";
                var all = _backup.LoadHistory();
                var rec = all.FirstOrDefault(s => s.Id == sessionId)?.Changes.FirstOrDefault(c => c.Id == changeId)
                    ?? throw new InvalidOperationException("Change record not found.");
                if (!rec.Restorable) throw new InvalidOperationException("This change is not automatically restorable.");
                var err = _backup.Restore(rec);
                if (err != null) throw new InvalidOperationException(err);
                _backup.MarkRestored(sessionId, changeId);
                return _backup.LoadHistory();
            }
            case "restoreSession":
            {
                var sessionId = p.GetProperty("sessionId").GetString() ?? "";
                var all = _backup.LoadHistory();
                var session = all.FirstOrDefault(s => s.Id == sessionId)
                    ?? throw new InvalidOperationException("Session not found.");
                var errors = new List<string>();
                foreach (var rec in session.Changes.Where(c => c.Restorable && !c.Restored))
                {
                    Push("progress", new { msg = "Restoring: " + rec.Title });
                    var err = _backup.Restore(rec);
                    if (err == null) _backup.MarkRestored(sessionId, rec.Id);
                    else errors.Add(rec.Title + ": " + err);
                }
                return new { history = _backup.LoadHistory(), errors };
            }
            case "exportReport":
            {
                _sysInfo ??= _hw.GetSystemInfo();
                var opts = _opt.Scan(_sysInfo);
                var html = _report.BuildHtml(_sysInfo, opts, _backup.LoadHistory());
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                var path = Path.Combine(desktop, "Tempo-Report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".html");
                File.WriteAllText(path, html);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return new { path };
            }
            case "openUrl":
            {
                var url = p.GetProperty("url").GetString() ?? "";
                if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return true;
            }
            // ---------------- game optimization ----------------
            case "scanGames":
            {
                var games = _games.Detect();
                foreach (var g in games) g.IconDataUri = ExtractIcon(g.Exe);
                _gameCache = games;
                return games;
            }
            case "gameProfile":
            {
                var id = p.GetProperty("id").GetString() ?? "";
                var preset = (Preset)(p.TryGetProperty("preset", out var pr) ? pr.GetInt32() : 1);
                var game = FindGame(id);
                _sysInfo ??= _hw.GetSystemInfo();
                return _gameSettings.BuildProfile(game, _sysInfo, preset);
            }
            case "applyGameSettings":
            {
                var id = p.GetProperty("id").GetString() ?? "";
                var game = FindGame(id);
                _sysInfo ??= _hw.GetSystemInfo();
                var values = new Dictionary<string, string>();
                foreach (var kv in p.GetProperty("values").EnumerateObject())
                    values[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString();
                return _gameSettings.Apply(game, _sysInfo, values, msg => Push("progress", new { msg }));
            }
            case "addGame":
            {
                var path = p.GetProperty("path").GetString() ?? "";
                _games.AddManual(path);
                return true;
            }
            case "hideGame":
            {
                _games.Hide(p.GetProperty("id").GetString() ?? "");
                return true;
            }
            case "pickGameExe":
            {
                var path = PickExecutable();
                return new { path };
            }
            case "launchGame":
            {
                var game = FindGame(p.GetProperty("id").GetString() ?? "");
                var target = game.LaunchUri.Length > 0 ? game.LaunchUri : game.Exe;
                if (target.Length == 0) throw new InvalidOperationException("Tempo doesn't know how to launch this one.");
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }
            case "openGpuControlPanel":
            {
                var vendor = p.GetProperty("vendor").GetString() ?? "";
                OpenVendorPanel(vendor);
                return true;
            }
            case "appVersion":
                return new { version = UpdateService.CurrentVersion() };
            case "whatsNew":
            {
                // only after an actual version change, never on a first install
                var previous = UpdateService.ConsumeVersionChange();
                if (previous == null) return new { show = false };
                var info = await _update.CheckAsync();
                return new
                {
                    show = true,
                    from = previous,
                    version = UpdateService.CurrentVersion(),
                    notes = info.Error == null ? info.Notes : "",
                };
            }
            case "checkUpdate":
                return await _update.CheckAsync();
            case "applyUpdate":
            {
                var err = await _update.DownloadAndApplyAsync(msg => Push("progress", new { msg }));
                if (err != null) throw new InvalidOperationException(err);

                // Hand the single-instance lock over BEFORE starting the new build, or it
                // would think another copy is running and quit without ever showing a window.
                Program.ReleaseInstanceLock();
                UpdateService.RelaunchAfterUpdate();

                // Shut down for real. Application.Exit() can be refused by a busy message
                // loop or a modal dialog, which previously left the old build running while
                // the new one waited for it, so fall back to a hard exit.
                _ui.BeginInvoke(() =>
                {
                    try { Application.Exit(); } catch { }
                    var t = new System.Windows.Forms.Timer { Interval = 2500 };
                    t.Tick += (_, _) =>
                    {
                        Tempo.Core.Logging.Log.Write("update", "forcing exit so the new build can start");
                        Environment.Exit(0);
                    };
                    t.Start();
                });
                return new { restarted = true };
            }
            default:
                throw new InvalidOperationException("Unknown method: " + method);
        }
    }

    // ---------- live metrics ----------

    private void StartMetrics()
    {
        if (_metrics != null) return;
        var vram = _sysInfo?.Gpus.FirstOrDefault()?.VramBytes ?? 0;
        _metrics = new LiveMetricsService(vram);
        _metricsTimer = new System.Threading.Timer(_ =>
        {
            if (_sampling) return;
            _sampling = true;
            try
            {
                var m = _metrics!.Sample();
                Push("metrics", m);
            }
            catch { }
            finally { _sampling = false; }
        }, null, 500, 1000);
    }

    // ---------- game helpers ----------

    private DetectedGame FindGame(string id)
    {
        var g = _gameCache.FirstOrDefault(x => x.Id == id);
        if (g == null)
        {
            _gameCache = _games.Detect();
            g = _gameCache.FirstOrDefault(x => x.Id == id);
        }
        return g ?? throw new InvalidOperationException("That game is no longer installed. Re-scan to refresh the list.");
    }

    /// <summary>
    /// Pulls the icon straight out of the game's own executable. Entirely local —
    /// Tempo never downloads cover art, so the no-network promise still holds.
    /// </summary>
    private static string? ExtractIcon(string exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) return null;
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(exe);
            if (ico == null) return null;
            using var bmp = ico.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    private string? PickExecutable()
    {
        string? chosen = null;
        _ui.Invoke(() =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Choose the game's main program",
                Filter = "Programs (*.exe)|*.exe",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() == DialogResult.OK) chosen = dlg.FileName;
        });
        return chosen;
    }

    private static void OpenVendorPanel(string vendor)
    {
        // Launch the vendor's own tool rather than pretending Tempo can set these.
        var candidates = vendor switch
        {
            "NVIDIA" => new[] { "nvcplui.exe", @"C:\Program Files\NVIDIA Corporation\NVIDIA app\CEF\NVIDIA app.exe" },
            "AMD" => new[] { @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe", "RadeonSoftware.exe" },
            "Intel" => new[] { "IntelGraphicsSoftware.exe", "IntelArcControl.exe" },
            _ => Array.Empty<string>(),
        };
        foreach (var c in candidates)
        {
            try { Process.Start(new ProcessStartInfo(c) { UseShellExecute = true }); return; }
            catch { }
        }
        // fall back to the Windows graphics settings page
        try { Process.Start(new ProcessStartInfo("ms-settings:display-advancedgraphics") { UseShellExecute = true }); } catch { }
    }

    // ---------- plumbing ----------

    private void Reply(string id, bool ok, object? result, string? error)
        => Post(new { id, ok, result, error });

    private void Push(string evt, object data)
        => Post(new { evt, data });

    private void Post(object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, Json);
            if (_ui.IsHandleCreated && !_ui.IsDisposed)
                _ui.BeginInvoke(() => { try { _core.PostWebMessageAsJson(json); } catch { } });
        }
        catch { }
    }

    public void Dispose()
    {
        _metricsTimer?.Dispose();
        _metrics?.Dispose();
    }
}
