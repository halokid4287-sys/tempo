using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Games;
using Tempo.Core.Hardware;
using Tempo.Core.Models;
using Tempo.Core.Optimizations;
using Tempo.Core.Updates;

int fail = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) fail++; }
void NoCrash(string what, Action a)
{
    try { a(); Check(true, what + " (no crash)"); }
    catch (Exception ex) { Check(false, what + " THREW: " + ex.GetType().Name + " - " + ex.Message); }
}

var sys = new HardwareService().GetSystemInfo();
var backup = new BackupService();
var gs = new GameSettingsService(backup);

Console.WriteLine("=== edge cases: bad / missing input must fail cleanly ===");

NoCrash("profile for a game with no install dir", () =>
    gs.BuildProfile(new DetectedGame { Id = "x:1", Name = "Nonexistent Game", Source = "Manual" }, sys, Preset.Balanced));

NoCrash("profile for a game with a bogus path", () =>
    gs.BuildProfile(new DetectedGame { Id = "x:2", Name = "Ghost", InstallDir = @"Z:\nope\missing", Exe = @"Z:\nope\missing\a.exe" }, sys, Preset.Balanced));

NoCrash("adapter lookup on empty game", () => gs.AdapterFor(new DetectedGame(), out _));

NoCrash("running-check on empty exe", () => GameSettingsService.IsGameRunning(new DetectedGame()));

// applying to a game with no adapter must be reported, not silently 'successful'
var noAdapter = new DetectedGame { Id = "x:3", Name = "No Adapter Game", Exe = "" };
var r = gs.Apply(noAdapter, sys, new Dictionary<string, string> { ["Fake/Key"] = "1" });
Check(r.Skipped.Count > 0 || r.Error != null, "applying with no adapter is reported as skipped, not applied");
Check(r.Changed == 0, "nothing counted as changed when there is no adapter");

// driver settings for a game with no exe must refuse rather than write somewhere random
var noExe = new DetectedGame { Id = "x:4", Name = "No Exe", Exe = "" };
var r2 = gs.Apply(noExe, sys, new Dictionary<string, string> { ["drv:274011249"] = "1" });
Check(!r2.Success || r2.Changed == 0, "driver apply without an exe does not claim success");

NoCrash("ini reader on a missing file", () => { var _ = new IniFile(@"Z:\nope\missing.ini").Get("A", "B"); });

NoCrash("ini reader on a binary file", () =>
{
    var tmp = Path.Combine(Path.GetTempPath(), "tempo-bin-test.ini");
    File.WriteAllBytes(tmp, new byte[] { 0, 1, 2, 3, 255, 254, 0, 0, 7 });
    var _ = new IniFile(tmp).Get("Section", "Key");
    File.Delete(tmp);
});

Console.WriteLine();
Console.WriteLine("=== regression: existing features still work ===");

var items = new OptimizationEngine(backup).Scan(sys);
Check(items.Count(i => i.Available) > 60, $"optimization catalog intact ({items.Count(i => i.Available)} available)");
Check(items.Count(i => i.Kind == "setting" && i.Available) > 40, "settings present");
Check(items.Count(i => i.Kind == "diagnostic" && i.Available) > 10, "diagnostics present");

var diag = new DiagnosticsService();
foreach (var id in new[] { "diag-display", "diag-memory-pressure", "diag-space" })
{
    var d = diag.Run(id, sys);
    Check(d.Summary.Length > 0, $"diagnostic '{id}' returns a verdict");
}

var bloat = new BloatwareService().Scan();
Check(bloat.Apps.Count > 0, $"bloatware scan works ({bloat.Apps.Count} apps)");
Check(bloat.Summary.TotalApps == bloat.Apps.Count, "bloatware summary matches list");

var startup = new StartupService().List();
Check(startup.Count > 0, $"startup list works ({startup.Count} entries)");

var games = new GameDetectionService().Detect();
Check(games.Count > 0, $"game detection works ({games.Count} games)");
Check(games.All(g => g.Name.Length > 0 && g.Source.Length > 0), "every game has a name and source");

Check(BackupService.DataDir.EndsWith("Tempo"), "data folder correct");
Check(new BackupService().LoadHistory().Count > 0, "history readable");
Check(UpdateService.CurrentVersion().Split('.').Length == 3, "version string well-formed");

Console.WriteLine();
Console.WriteLine("=== every game profile builds without error ===");
foreach (var g in games)
{
    try
    {
        var p = gs.BuildProfile(g, sys, Preset.MaxPerformance);
        bool allDescribed = p.Settings.All(s => s.Label.Length > 0 && s.Description.Length > 0);
        bool noBogusRec = p.Settings.All(s => !s.ReadOnly || s.Recommended == null || s.ReadOnlyReason != null);
        Check(allDescribed && noBogusRec, $"{g.Name}: {p.Settings.Count} settings, all labelled and described");
    }
    catch (Exception ex) { Check(false, $"{g.Name} THREW: {ex.Message}"); }
}

Console.WriteLine();
Console.WriteLine(fail == 0 ? "ALL ROBUSTNESS + REGRESSION CHECKS PASSED" : fail + " CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
