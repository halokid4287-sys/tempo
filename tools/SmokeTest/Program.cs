using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Games;
using Tempo.Core.Hardware;
using Tempo.Core.Models;
using Tempo.Core.Optimizations;

// Consolidated pre-release gate. Everything here is read-only.
int fail = 0;
void Check(bool ok, string w) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {w}"); if (!ok) fail++; }

var sys = new HardwareService().GetSystemInfo();
var backup = new BackupService();
var gs = new GameSettingsService(backup);
var games = new GameDetectionService().Detect();

Console.WriteLine("--- no setting may ever claim to do something it cannot ---");
foreach (var g in games)
{
    var p = gs.BuildProfile(g, sys, Preset.MaxPerformance);
    foreach (var s in p.Settings)
    {
        Check(!s.ReadOnly || s.Recommended == null, $"{g.Name}/{s.Label}: locked -> no suggestion");
        Check(!s.ReadOnly || !string.IsNullOrWhiteSpace(s.ReadOnlyReason), $"{g.Name}/{s.Label}: locked -> has a reason");
        Check(s.Label.Length > 0 && s.Description.Length > 20, $"{g.Name}/{s.Label}: labelled and explained");
        Check(s.Kind != "choice" || s.ReadOnly || s.Options.Count > 0, $"{g.Name}/{s.Label}: editable choice has options");
        // an editable setting must have a key we could actually write back
        Check(s.ReadOnly || s.Key.Length > 0, $"{g.Name}/{s.Label}: editable has a write key");
    }
}

Console.WriteLine();
Console.WriteLine("--- vendor neutrality ---");
foreach (var (vendor, gpu) in new[] { ("AMD", "AMD Radeon RX 7900 XT"), ("Intel", "Intel Arc B580"), ("NVIDIA", "NVIDIA GeForce RTX 4080") })
{
    var fake = new SystemInfo { Cpu = new CpuInfo { Name = "Test", Vendor = "AMD", Cores = 8, Threads = 16 }, Ram = sys.Ram, Drives = sys.Drives, Os = sys.Os };
    fake.Gpus.Add(new GpuInfo { Name = gpu, Vendor = vendor, DriverVersion = "1.0", IsPrimary = true, Resolution = "2560x1440" });
    var g = games.First(x => gs.AdapterFor(x, out _) != null);
    var p = gs.BuildProfile(g, fake, Preset.MaxPerformance);
    var drv = p.Settings.Count(s => s.Source == "driver");
    Check(vendor == "NVIDIA" ? drv > 0 : drv == 0, $"{vendor}: driver controls shown only where they work ({drv})");
    Check(vendor == "NVIDIA" || !string.IsNullOrWhiteSpace(p.DriverUnavailableReason), $"{vendor}: explains why not");

    // Upscaling comes from the game's own config, so a game that has it must expose
    // exactly the same options no matter whose GPU is installed.
    var withUpscaling = games.FirstOrDefault(x => gs.BuildProfile(x, sys, Preset.MaxPerformance).Settings.Any(s => s.Group == "Upscaling"));
    if (withUpscaling != null)
    {
        var up = gs.BuildProfile(withUpscaling, fake, Preset.MaxPerformance).Settings.Where(s => s.Group == "Upscaling").ToList();
        Check(up.Count > 0, $"{vendor}: {withUpscaling.Name} still offers upscaling ({up.Count} options)");
        Check(up.Any(s => !s.ReadOnly), $"{vendor}: at least one upscaling control is actually settable");
    }
}

Console.WriteLine();
Console.WriteLine("--- core invariants ---");
var items = new OptimizationEngine(backup).Scan(sys);
Check(items.Count(i => i.Available) > 60, $"optimization catalog intact ({items.Count(i => i.Available)})");
Check(!items.Any(i => i.Recommended && (i.Risk != "low" || !i.Reversible)), "only low-risk reversible items recommended");
var bloat = new BloatwareService().Scan();
Check(bloat.Apps.Count > 0 && !bloat.Apps.Any(a => a.Preselect && a.Safety != "safe"), "bloatware guards intact");
Check(new StartupService().List().Count > 0, "startup list works");
Check(backup.LoadHistory().Count > 0, "history readable");
Check(games.Count > 0 && games.Select(x => x.Id).Distinct().Count() == games.Count, "game detection sane");

Console.WriteLine();
Console.WriteLine(fail == 0 ? "PRE-RELEASE GATE PASSED" : fail + " CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
