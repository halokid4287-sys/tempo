using Tempo.Core.Backup;
using Tempo.Core.Bloatware;
using Tempo.Core.Hardware;
using Tempo.Core.Optimizations;

// READ-ONLY verification after the rename.
var si = new HardwareService().GetSystemInfo();
Console.WriteLine($"Hardware OK: {si.Cpu.Name} | {si.Gpus.FirstOrDefault()?.Name} | plan {si.ActivePowerPlan}");

var items = new OptimizationEngine(new BackupService()).Scan(si);
var avail = items.Where(i => i.Available).ToList();
Console.WriteLine($"Catalog OK: {avail.Count} available ({avail.Count(i => i.Kind == "setting")} settings, {avail.Count(i => i.Kind == "action")} actions, {avail.Count(i => i.Kind == "diagnostic")} diagnostics), {avail.Count(i => i.Applied)} applied");

var d = new DiagnosticsService().Run("diag-display", si);
Console.WriteLine($"Diagnostic OK: {d.Summary}");

var res = new BloatwareService().Scan();
Console.WriteLine($"Bloatware OK: {res.Apps.Count} apps, {res.Summary.RunningNow} running, {res.Summary.StartupEnabled} at startup");

Console.WriteLine($"DataDir: {BackupService.DataDir}");
var hist = new BackupService().LoadHistory();
Console.WriteLine($"History migrated: {hist.Count} session(s)");
foreach (var s in hist) Console.WriteLine($"   '{s.Label}' ({s.Changes.Count} change(s)) — restorable: {s.Changes.Count(c => c.Restorable && !c.Restored)}");
Console.WriteLine("\nRENAME VERIFIED — nothing was modified.");
