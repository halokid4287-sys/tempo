using System.Text.Json.Serialization;

namespace Tempo.Core.Models;

// ---------- Hardware ----------
public class SystemInfo
{
    public CpuInfo Cpu { get; set; } = new();
    public List<GpuInfo> Gpus { get; set; } = new();
    public RamInfo Ram { get; set; } = new();
    public List<DriveInfo2> Drives { get; set; } = new();
    public OsInfo Os { get; set; } = new();
    public string Motherboard { get; set; } = "";
    public string ActivePowerPlan { get; set; } = "";
    public string ActivePowerPlanGuid { get; set; } = "";
    public bool IsLaptop { get; set; }
    public string MachineName { get; set; } = "";
    public DateTime ScannedAt { get; set; } = DateTime.Now;
}

public class CpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";      // Intel / AMD / other
    public int Cores { get; set; }
    public int Threads { get; set; }
    public int MaxClockMhz { get; set; }
}

public class GpuInfo
{
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "";      // NVIDIA / AMD / Intel / other
    public string DriverVersion { get; set; } = "";
    public string DriverDate { get; set; } = "";  // yyyy-MM-dd
    public int DriverAgeDays { get; set; } = -1;
    public long VramBytes { get; set; }
    public string Resolution { get; set; } = "";
    public int RefreshRate { get; set; }
    public bool IsPrimary { get; set; }
}

public class RamInfo
{
    public long TotalBytes { get; set; }
    public int Sticks { get; set; }
    public int SpeedMhz { get; set; }
    public string Channels { get; set; } = "";    // "Dual", "Single", etc. best-effort
}

public class DriveInfo2
{
    public string Letter { get; set; } = "";
    public string Label { get; set; } = "";
    public string MediaType { get; set; } = "";   // NVMe SSD / SATA SSD / HDD / Unknown
    public long TotalBytes { get; set; }
    public long FreeBytes { get; set; }
    public string Health { get; set; } = "";      // Healthy / Warning / Unknown
    public bool IsSystemDrive { get; set; }
}

public class OsInfo
{
    public string Name { get; set; } = "";
    public string DisplayVersion { get; set; } = ""; // e.g. 24H2
    public string Build { get; set; } = "";
    public string Edition { get; set; } = "";
    public int UptimeHours { get; set; }
}

// ---------- Live metrics ----------
public class LiveMetrics
{
    public double CpuPercent { get; set; }
    public double GpuPercent { get; set; }
    public double RamPercent { get; set; }
    public long RamUsedBytes { get; set; }
    public long RamTotalBytes { get; set; }
    public long VramUsedBytes { get; set; }
    public long VramTotalBytes { get; set; }
    public int ProcessCount { get; set; }
}

// ---------- Optimizations ----------
public class OptimizationItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";       // gaming | fps | power | cpu | gpu | memory | storage | network | input | visuals | background | maintenance | advanced
    public string Kind { get; set; } = "setting";    // setting (toggleable change) | action (one-shot task) | diagnostic (read-only check)
    public string Title { get; set; } = "";
    public string What { get; set; } = "";           // what it changes / checks
    public string Why { get; set; } = "";            // honest expected benefit
    public string Drawbacks { get; set; } = "";      // honest downsides
    public string Benefit { get; set; } = "";        // FPS | smoothness | responsiveness | storage | background | diagnostic | stability | quality of life
    public string Impact { get; set; } = "low";      // high | medium | low | situational
    public string Risk { get; set; } = "low";        // low | medium | high
    public bool Reversible { get; set; } = true;
    public bool NeedsReboot { get; set; }
    public bool Recommended { get; set; }            // preselect in UI (never for medium/high risk)
    public bool Applied { get; set; }                // current state matches tweak (settings only)
    public bool Available { get; set; } = true;      // relevant on this machine
    public string? Note { get; set; }                // machine-specific note
    public string? State { get; set; }               // current system value, human readable
}

public class DiagnosticResult
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; } = true;             // false = check found a problem
    public string Summary { get; set; } = "";        // one-line verdict
    public List<string> Lines { get; set; } = new(); // detail lines
}

public class ApplyResult
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}

// ---------- Startup apps ----------
public class StartupApp
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Location { get; set; } = "";       // registry path or folder, human readable
    public string Scope { get; set; } = "";          // user | machine
    public bool Enabled { get; set; } = true;
    public string Source { get; set; } = "";         // run-key | startup-folder
    public string? Hint { get; set; }                // what we know about it
}

// ---------- Bloatware ----------
public class BloatApp
{
    public string Id { get; set; } = "";             // stable key for uninstall call
    public string Kind { get; set; } = "";           // store | win32
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string Description { get; set; } = "";    // what it does
    public string WhyRemovable { get; set; } = "";   // why it might be unnecessary
    public string Safety { get; set; } = "unknown";  // safe | optional | risky | unknown
    public string SafetyNote { get; set; } = "";     // effect on Windows/drivers/features
    public long SizeBytes { get; set; }              // 0 = unknown
    public string Restore { get; set; } = "";        // how to get it back
    public bool Restorable { get; set; }
    public bool Preselect { get; set; }              // never true unless clearly junk
    public string UninstallMethod { get; set; } = "";// "Microsoft Store package removal" / "Official uninstaller"
    public bool Silent { get; set; }                 // quiet uninstall available
    public string InstallLocation { get; set; } = "";
    // --- live usage linkage (filled during scan, best-effort) ---
    public bool HasStartup { get; set; }             // has an enabled startup entry
    public string StartupName { get; set; } = "";
    public int RunningProcesses { get; set; }        // processes running from its install folder right now
    public long RamBytes { get; set; }               // working set of those processes
    public double CpuPercent { get; set; }           // sampled CPU share of those processes
    public int ServiceCount { get; set; }            // auto-start services from its install folder
    public string Group { get; set; } = "";          // likely-unnecessary | optional | resource-heavy | keep-unless-sure | normal
}

public class BloatScanResult
{
    public List<BloatApp> Apps { get; set; } = new();
    public BloatSummary Summary { get; set; } = new();
}

public class BloatSummary
{
    public int TotalApps { get; set; }
    public long TotalSizeBytes { get; set; }
    public int StartupEnabled { get; set; }
    public int RunningNow { get; set; }
    public long RunningRamBytes { get; set; }
    public long CleanupBytes { get; set; }           // temp + caches + recycle bin, reclaimable
    public List<string> TopRamApps { get; set; } = new();   // "Name — 1.2 GB"
    public List<string> TopDiskApps { get; set; } = new();  // "Name — 4.5 GB"
    public List<string> CleanupLines { get; set; } = new(); // "User temp files — 1.8 GB"
}

// ---------- Backup / history ----------
public class ChangeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string TweakId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";           // registry | powerplan | service | startup | appx | win32 | powercfg
    public string Target { get; set; } = "";         // key path / scheme guid / service name / package name
    public string? ValueName { get; set; }
    public string? OldValue { get; set; }            // JSON-ish string; null = didn't exist
    public string? OldKind { get; set; }             // registry kind
    public string? NewValue { get; set; }
    public bool Restorable { get; set; } = true;
    public bool Restored { get; set; }
    public DateTime At { get; set; } = DateTime.Now;
}

public class BackupSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime At { get; set; } = DateTime.Now;
    public string Label { get; set; } = "";
    public List<ChangeRecord> Changes { get; set; } = new();
}

[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(LiveMetrics))]
[JsonSerializable(typeof(List<OptimizationItem>))]
[JsonSerializable(typeof(List<StartupApp>))]
[JsonSerializable(typeof(List<BloatApp>))]
[JsonSerializable(typeof(List<BackupSession>))]
[JsonSerializable(typeof(List<ApplyResult>))]
public partial class VJson : JsonSerializerContext { }
