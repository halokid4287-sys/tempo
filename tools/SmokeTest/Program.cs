using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Games;
using Tempo.Core.Hardware;
using Tempo.Core.Models;
using Tempo.Core.Optimizations;

// Full pre-release gate. Read-only.
int fail = 0;
void Check(bool ok, string w) { if (!ok) { Console.WriteLine("FAIL  " + w); fail++; } }

var sys = new HardwareService().GetSystemInfo();
var backup = new BackupService();
var gs = new GameSettingsService(backup);
var games = new GameDetectionService().Detect();

Console.WriteLine("--- settings integrity across the whole slider range ---");
foreach (var g in games)
{
    foreach (var bal in new[] { 0, 33, 50, 67, 100 })
    {
        var p = gs.BuildProfile(g, sys, bal);
        foreach (var s in p.Settings)
        {
            Check(!s.ReadOnly || s.Recommended == null, $"{g.Name}@{bal}/{s.Label}: locked but suggests a value");
            Check(!s.ReadOnly || !string.IsNullOrWhiteSpace(s.ReadOnlyReason), $"{g.Name}@{bal}/{s.Label}: locked without a reason");
            Check(s.Label.Length > 0 && s.Description.Length > 20, $"{g.Name}@{bal}/{s.Label}: missing text");
            // a suggested value must be one the control can actually take
            if (s.Recommended != null && s.Options.Count > 0)
                Check(s.Options.Any(o => o.Value == s.Recommended), $"{g.Name}@{bal}/{s.Label}: suggests '{s.Recommended}' which is not an option");
            if (s.Recommended != null && s.Kind == "number")
            {
                Check(double.TryParse(s.Recommended, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var nv),
                      $"{g.Name}@{bal}/{s.Label}: non-numeric '{s.Recommended}'");
                if (double.TryParse(s.Recommended, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n2))
                {
                    if (s.Min.HasValue) Check(n2 >= s.Min.Value, $"{g.Name}@{bal}/{s.Label}: {n2} below min {s.Min}");
                    if (s.Max.HasValue) Check(n2 <= s.Max.Value, $"{g.Name}@{bal}/{s.Label}: {n2} above max {s.Max}");
                }
            }
        }
    }
    Console.WriteLine($"  {g.Name,-34} ok across 0/33/50/67/100");
}

Console.WriteLine("\n--- vendor neutrality ---");
foreach (var (vendor, name) in new[] { ("AMD", "AMD Radeon RX 7900 XT"), ("Intel", "Intel Arc B580"), ("NVIDIA", "NVIDIA GeForce RTX 4080") })
{
    var fake = new SystemInfo { Cpu = new CpuInfo { Name = "Test", Vendor = "AMD", Cores = 8, Threads = 16 }, Ram = sys.Ram, Drives = sys.Drives, Os = sys.Os };
    fake.Gpus.Add(new GpuInfo { Name = name, Vendor = vendor, DriverVersion = "1.0", IsPrimary = true, Resolution = "2560x1440" });
    var g = games.First(x => gs.AdapterFor(x, out _) != null);
    var p = gs.BuildProfile(g, fake, 50);
    var drv = p.Settings.Count(s => s.Source == "driver");
    Check(vendor == "NVIDIA" ? drv > 0 : drv == 0, $"{vendor}: wrong driver count ({drv})");
    Check(vendor == "NVIDIA" || !string.IsNullOrWhiteSpace(p.DriverUnavailableReason), $"{vendor}: no explanation");
    Console.WriteLine($"  {vendor,-8} driver={drv}");
}

Console.WriteLine("\n--- edge cases ---");
foreach (var (what, act) in new (string, Action)[]
{
    ("no install dir", () => gs.BuildProfile(new DetectedGame { Id = "x1", Name = "Ghost" }, sys, 50)),
    ("bogus path", () => gs.BuildProfile(new DetectedGame { Id = "x2", Name = "Ghost", InstallDir = @"Z:\nope" }, sys, 0)),
    ("balance out of range", () => gs.BuildProfile(games[0], sys, 9999)),
    ("negative balance", () => gs.BuildProfile(games[0], sys, -40)),
    ("missing ini", () => { var _ = new IniFile(@"Z:\nope\x.ini").Get("A", "B"); }),
})
{
    try { act(); Console.WriteLine($"  {what}: ok"); }
    catch (Exception ex) { Check(false, $"{what} threw {ex.GetType().Name}: {ex.Message}"); }
}
var bad = gs.Apply(new DetectedGame { Id = "x3", Name = "None", Exe = "" }, sys, new Dictionary<string, string> { ["A/B"] = "1" });
Check(bad.Changed == 0 && (bad.Skipped.Count > 0 || bad.Error != null), "no-adapter apply falsely succeeded");

Console.WriteLine("\n--- core invariants ---");
var items = new OptimizationEngine(backup).Scan(sys);
Check(items.Count(i => i.Available) > 60, "optimization catalog shrank");
Check(!items.Any(i => i.Recommended && (i.Risk != "low" || !i.Reversible)), "risky item recommended");
var bloat = new BloatwareService().Scan();
Check(!bloat.Apps.Any(a => a.Preselect && a.Safety != "safe"), "unsafe app pre-ticked");
foreach (var prot in new[] { "NVIDIA", "Realtek", "Defender", "Visual C++", "WebView2" })
    Check(!bloat.Apps.Any(a => a.Name.Contains(prot, StringComparison.OrdinalIgnoreCase)), $"protected software listed: {prot}");
Check(new StartupService().List().Count > 0, "startup list empty");
Check(backup.LoadHistory().Count > 0, "history unreadable");
Console.WriteLine($"  {items.Count(i => i.Available)} options · {bloat.Apps.Count} apps · {games.Count} games");

Console.WriteLine();
Console.WriteLine(fail == 0 ? "PRE-RELEASE GATE PASSED" : fail + " CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
