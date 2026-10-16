using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Games;
using Tempo.Core.Hardware;
using Tempo.Core.Models;
using Tempo.Core.Optimizations;

int fail = 0;
void Check(bool ok, string w) { if (!ok) { Console.WriteLine("FAIL  " + w); fail++; } }

var sys = new HardwareService().GetSystemInfo();
var backup = new BackupService();
var gs = new GameSettingsService(backup);
var games = new GameDetectionService().Detect();

Console.WriteLine("--- one game's changes must never touch another ---");
var targets = new Dictionary<string, string>();
foreach (var g in games)
{
    if (gs.AdapterFor(g, out var cfg) == null) continue;
    Check(!targets.ContainsValue(cfg), $"{g.Name} shares a config file with another game: {cfg}");
    targets[g.Name] = cfg;
}
Console.WriteLine($"  {targets.Count} games with adapters, all writing to separate files");

Console.WriteLine("\n--- settings valid at every slider position ---");
foreach (var g in games)
    foreach (var bal in new[] { 0, 20, 40, 50, 60, 80, 100 })
    {
        var p = gs.BuildProfile(g, sys, bal);
        foreach (var s in p.Settings)
        {
            Check(!s.ReadOnly || s.Recommended == null, $"{g.Name}@{bal}/{s.Label}: locked but suggests");
            Check(s.Label.Length > 0 && s.Description.Length > 20, $"{g.Name}@{bal}/{s.Label}: missing text");
            if (s.Recommended != null && s.Options.Count > 0)
                Check(s.Options.Any(o => o.Value == s.Recommended), $"{g.Name}@{bal}/{s.Label}: '{s.Recommended}' not an option");
            if (s.Recommended != null && s.Kind == "number" &&
                double.TryParse(s.Recommended, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                if (s.Min.HasValue) Check(n >= s.Min.Value, $"{g.Name}@{bal}/{s.Label}: {n} < {s.Min}");
                if (s.Max.HasValue) Check(n <= s.Max.Value, $"{g.Name}@{bal}/{s.Label}: {n} > {s.Max}");
            }
        }
    }
Console.WriteLine($"  {games.Count} games x 7 slider positions all valid");

Console.WriteLine("\n--- driver profile transparency ---");
foreach (var g in games)
{
    var p = gs.BuildProfile(g, sys, 50);
    if (!p.DriverSettingsAvailable) continue;
    Check(p.DriverProfileName.Length > 0, $"{g.Name}: driver profile not named");
    Console.WriteLine($"  {g.Name,-34} '{p.DriverProfileName}' covering {p.DriverProfileApps} program(s)");
}

Console.WriteLine("\n--- vendor neutrality + edge cases ---");
foreach (var (vendor, name) in new[] { ("AMD", "AMD Radeon RX 7900 XT"), ("Intel", "Intel Arc B580") })
{
    var fake = new SystemInfo { Cpu = new CpuInfo { Name = "T", Vendor = "AMD", Cores = 8, Threads = 16 }, Ram = sys.Ram, Drives = sys.Drives, Os = sys.Os };
    fake.Gpus.Add(new GpuInfo { Name = name, Vendor = vendor, DriverVersion = "1", IsPrimary = true, Resolution = "2560x1440" });
    var p = gs.BuildProfile(games.First(x => gs.AdapterFor(x, out _) != null), fake, 50);
    Check(p.Settings.Count(s => s.Source == "driver") == 0, $"{vendor}: driver controls shown where unsupported");
    Check(!string.IsNullOrWhiteSpace(p.DriverUnavailableReason), $"{vendor}: no explanation");
}
foreach (var (what, act) in new (string, Action)[]
{
    ("bogus game", () => gs.BuildProfile(new DetectedGame { Id = "x", Name = "Ghost", InstallDir = @"Z:\nope" }, sys, 50)),
    ("balance 9999", () => gs.BuildProfile(games[0], sys, 9999)),
    ("balance -40", () => gs.BuildProfile(games[0], sys, -40)),
})
{ try { act(); } catch (Exception ex) { Check(false, $"{what} threw {ex.GetType().Name}"); } }

var items = new OptimizationEngine(backup).Scan(sys);
Check(items.Count(i => i.Available) > 60, "catalog shrank");
Check(!items.Any(i => i.Recommended && (i.Risk != "low" || !i.Reversible)), "risky item recommended");
var bloat = new BloatwareService().Scan();
Check(!bloat.Apps.Any(a => a.Preselect && a.Safety != "safe"), "unsafe app pre-ticked");
foreach (var prot in new[] { "NVIDIA", "Realtek", "Defender", "Visual C++" })
    Check(!bloat.Apps.Any(a => a.Name.Contains(prot, StringComparison.OrdinalIgnoreCase)), $"protected listed: {prot}");
Console.WriteLine($"  {items.Count(i => i.Available)} options · {bloat.Apps.Count} apps · guards intact");

Console.WriteLine();
Console.WriteLine(fail == 0 ? "PRE-RELEASE GATE PASSED" : fail + " CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
