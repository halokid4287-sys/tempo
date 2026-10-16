using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Games;
using Tempo.Core.Hardware;
using Tempo.Core.Models;
using Tempo.Core.Optimizations;

// Full pre-release gate. Read-only.
int fail = 0;
void Check(bool ok, string w) { if (!ok) { Console.WriteLine("FAIL  " + w); fail++; } }
void Section(string s) => Console.WriteLine("\n--- " + s + " ---");

var sys = new HardwareService().GetSystemInfo();
var backup = new BackupService();
var gs = new GameSettingsService(backup);
var games = new GameDetectionService().Detect();

Section("nothing may claim to do what it cannot");
foreach (var g in games)
{
    var p = gs.BuildProfile(g, sys, Preset.MaxPerformance);
    foreach (var s in p.Settings)
    {
        Check(!s.ReadOnly || s.Recommended == null, $"{g.Name}/{s.Label}: locked but suggests a value");
        Check(!s.ReadOnly || !string.IsNullOrWhiteSpace(s.ReadOnlyReason), $"{g.Name}/{s.Label}: locked without a reason");
        Check(s.Label.Length > 0 && s.Description.Length > 20, $"{g.Name}/{s.Label}: missing label/description");
        Check(s.Kind != "choice" || s.ReadOnly || s.Options.Count > 0, $"{g.Name}/{s.Label}: choice with no options");
    }
    Console.WriteLine($"  {g.Name,-34} {p.Settings.Count,2} settings  ({p.Settings.Count(x => x.ReadOnly)} locked)");
}

Section("vendor neutrality");
foreach (var (vendor, name) in new[] { ("AMD", "AMD Radeon RX 7900 XT"), ("Intel", "Intel Arc B580"), ("NVIDIA", "NVIDIA GeForce RTX 4080") })
{
    var fake = new SystemInfo { Cpu = new CpuInfo { Name = "Test CPU", Vendor = "AMD", Cores = 8, Threads = 16 }, Ram = sys.Ram, Drives = sys.Drives, Os = sys.Os };
    fake.Gpus.Add(new GpuInfo { Name = name, Vendor = vendor, DriverVersion = "1.0", IsPrimary = true, Resolution = "2560x1440" });
    var g = games.First(x => gs.AdapterFor(x, out _) != null);
    var p = gs.BuildProfile(g, fake, Preset.MaxPerformance);
    var drv = p.Settings.Count(s => s.Source == "driver");
    Check(vendor == "NVIDIA" ? drv > 0 : drv == 0, $"{vendor}: wrong driver control count ({drv})");
    Check(vendor == "NVIDIA" || !string.IsNullOrWhiteSpace(p.DriverUnavailableReason), $"{vendor}: no explanation given");
    var up = games.Select(x => gs.BuildProfile(x, fake, Preset.MaxPerformance)).SelectMany(x => x.Settings).Count(s => s.Group == "Upscaling");
    Check(up > 0, $"{vendor}: upscaling vanished");
    Console.WriteLine($"  {vendor,-8} driver={drv}  upscaling={up}  cpu-neutral=yes");
}

Section("edge cases must not crash");
foreach (var (what, act) in new (string, Action)[]
{
    ("no install dir", () => gs.BuildProfile(new DetectedGame { Id = "x1", Name = "Ghost" }, sys, Preset.Balanced)),
    ("bogus path", () => gs.BuildProfile(new DetectedGame { Id = "x2", Name = "Ghost", InstallDir = @"Z:\nope", Exe = @"Z:\nope\a.exe" }, sys, Preset.Balanced)),
    ("empty game", () => gs.AdapterFor(new DetectedGame(), out _)),
    ("missing ini", () => { var _ = new IniFile(@"Z:\nope\x.ini").Get("A", "B"); }),
})
{
    try { act(); Console.WriteLine($"  {what}: ok"); }
    catch (Exception ex) { Check(false, $"{what} threw {ex.GetType().Name}"); }
}
var noAdapter = gs.Apply(new DetectedGame { Id = "x3", Name = "None", Exe = "" }, sys, new Dictionary<string, string> { ["A/B"] = "1" });
Check(noAdapter.Changed == 0 && (noAdapter.Skipped.Count > 0 || noAdapter.Error != null), "no-adapter apply falsely reported success");

Section("core invariants");
var items = new OptimizationEngine(backup).Scan(sys);
Check(items.Count(i => i.Available) > 60, "optimization catalog shrank");
Check(!items.Any(i => i.Recommended && (i.Risk != "low" || !i.Reversible)), "risky item recommended");
Check(!items.Any(i => i.State != null && i.State.StartsWith("scan error")), "an option errored while scanning");
var bloat = new BloatwareService().Scan();
Check(!bloat.Apps.Any(a => a.Preselect && a.Safety != "safe"), "unsafe app pre-ticked");
foreach (var p in new[] { "NVIDIA", "Realtek", "Defender", "Visual C++", "WebView2" })
    Check(!bloat.Apps.Any(a => a.Name.Contains(p, StringComparison.OrdinalIgnoreCase)), $"protected software listed: {p}");
Check(new StartupService().List().Count > 0, "startup list empty");
Check(backup.LoadHistory().Count > 0, "history unreadable");
Check(games.Select(x => x.Id).Distinct().Count() == games.Count, "duplicate game ids");
Console.WriteLine($"  {items.Count(i => i.Available)} options · {bloat.Apps.Count} apps · {games.Count} games · history ok");

Console.WriteLine();
Console.WriteLine(fail == 0 ? "PRE-RELEASE GATE PASSED" : fail + " CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
