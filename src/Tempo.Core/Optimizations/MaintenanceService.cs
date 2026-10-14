using System.Management;
using System.Runtime.InteropServices;
using Tempo.Core.Hardware;

namespace Tempo.Core.Optimizations;

/// <summary>
/// One-shot maintenance actions: measure first (read-only), act only on request.
/// Deletions skip anything locked/in use and never touch system files.
/// </summary>
public class MaintenanceService
{
    // ---------- temp files ----------

    public static string[] TempDirs() => new[]
    {
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
    }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string[] ShaderCacheDirs()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new[]
        {
            Path.Combine(local, "D3DSCache"),
            Path.Combine(local, "NVIDIA", "DXCache"),
            Path.Combine(local, "NVIDIA", "GLCache"),
            Path.Combine(local, "AMD", "DxCache"),
            Path.Combine(local, "AMD", "DxcCache"),
            Path.Combine(local, "AMD", "GLCache"),
            Path.Combine(local, "Intel", "ShaderCache"),
        };
    }

    /// <summary>
    /// Resilient recursive file walk: one inaccessible subfolder must never abort the rest
    /// (EnumerateFiles with AllDirectories throws mid-iteration on access-denied).
    /// </summary>
    private static IEnumerable<string> SafeWalkFiles(string root, List<string>? dirsOut = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files = Array.Empty<string>(), subs = Array.Empty<string>();
            try { files = Directory.GetFiles(dir); } catch { }
            try { subs = Directory.GetDirectories(dir); } catch { }
            foreach (var f in files) yield return f;
            foreach (var s in subs) { dirsOut?.Add(s); stack.Push(s); }
        }
    }

    public static long MeasureDirs(IEnumerable<string> dirs, int minAgeHours = 0)
    {
        long size = 0;
        var cutoff = DateTime.Now.AddHours(-minAgeHours);
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in SafeWalkFiles(dir))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (minAgeHours == 0 || fi.LastWriteTime < cutoff) size += fi.Length;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return size;
    }

    /// <summary>Delete files in the given dirs (older than minAgeHours). Locked files are skipped silently.</summary>
    public static (long freed, int deleted, int skipped) CleanDirs(IEnumerable<string> dirs, int minAgeHours, Action<string>? progress = null)
    {
        long freed = 0; int deleted = 0, skipped = 0;
        var cutoff = DateTime.Now.AddHours(-minAgeHours);
        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                progress?.Invoke("Cleaning " + dir + "…");
                var subDirs = new List<string>();
                int sinceReport = 0;
                foreach (var f in SafeWalkFiles(dir, subDirs))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (minAgeHours > 0 && fi.LastWriteTime >= cutoff) { skipped++; continue; }
                        long len = fi.Length;
                        fi.Attributes = FileAttributes.Normal;
                        fi.Delete();
                        freed += len; deleted++;
                        if (++sinceReport >= 400) { sinceReport = 0; progress?.Invoke($"Cleaning {dir}… {deleted} files, {freed / 1048576} MB so far"); }
                    }
                    catch { skipped++; }
                }
                // prune now-empty subdirectories (deepest first, best-effort)
                foreach (var d in subDirs.OrderByDescending(x => x.Length))
                {
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
                }
            }
            catch { }
        }
        return (freed, deleted, skipped);
    }

    // ---------- recycle bin ----------

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll")]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    public static (long size, long items) RecycleBinInfo()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        try { if (SHQueryRecycleBin(null, ref info) == 0) return (info.i64Size, info.i64NumItems); } catch { }
        return (0, 0);
    }

    /// <summary>Permanently empties the Recycle Bin. Caller must have confirmed with the user.</summary>
    public static string? EmptyRecycleBin()
    {
        const uint SHERB_NOCONFIRMATION = 1, SHERB_NOPROGRESSUI = 2, SHERB_NOSOUND = 4;
        try
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr is 0 or unchecked((int)0x8000FFFF) ? null : "Shell returned 0x" + hr.ToString("X8");
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------- network ----------

    public static string FlushDns() => HardwareService.RunCapture("ipconfig", "/flushdns", 8000).Trim();

    public static string WinsockReset() => HardwareService.RunCapture("netsh", "winsock reset", 10000).Trim();

    public static string AutotuningLevel()
    {
        var o = HardwareService.RunCapture("netsh", "int tcp show global", 8000);
        var m = System.Text.RegularExpressions.Regex.Match(o, @"Auto-Tuning Level\s*:\s*(\w+)");
        return m.Success ? m.Groups[1].Value : "unknown";
    }

    public static string SetAutotuningNormal()
        => HardwareService.RunCapture("netsh", "int tcp set global autotuninglevel=normal", 8000).Trim();

    // ---------- drives ----------

    /// <summary>Run TRIM/optimize on the system drive (SSD). Safe; Windows does this on a schedule anyway.</summary>
    public static string TrimDrive(string letter, Action<string>? progress = null)
    {
        progress?.Invoke("Running TRIM on " + letter + " (this can take a minute)…");
        var o = HardwareService.RunCapture("defrag", letter + " /O", 300000);
        var tail = o.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(4);
        return string.Join(" · ", tail);
    }

    /// <summary>System File Checker — long (5–20 min).</summary>
    public static string SfcScan(Action<string>? progress = null)
    {
        progress?.Invoke("Running System File Checker — this takes 5–20 minutes, leave the app open…");
        var raw = HardwareService.RunCapture("sfc", "/scannow", 1500000);
        // sfc output is UTF-16-ish with interleaved NULs when captured; strip them
        var clean = new string(raw.Where(c => c != '\0' && c != '\r').ToArray());
        var lines = clean.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 3).ToList();
        var verdict = lines.LastOrDefault(l => l.Contains("integrity violations") || l.Contains("corrupt") || l.Contains("repair"))
                      ?? lines.LastOrDefault() ?? "SFC finished (no output captured).";
        return verdict;
    }

    // ---------- restore point ----------

    /// <summary>Create a System Restore point. Returns error text or null on success.</summary>
    public static string? CreateRestorePoint(string description)
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\default");
            scope.Connect();
            using var cls = new ManagementClass(scope, new ManagementPath("SystemRestore"), new ObjectGetOptions());
            var p = cls.GetMethodParameters("CreateRestorePoint");
            p["Description"] = description;
            p["RestorePointType"] = 12;   // MODIFY_SETTINGS
            p["EventType"] = 100;         // BEGIN_SYSTEM_CHANGE
            var r = cls.InvokeMethod("CreateRestorePoint", p, null);
            var rv = Convert.ToInt32(r["ReturnValue"]);
            if (rv == 0) return null;
            if (rv == 1058) return "System Restore is disabled on this PC (Windows setting). Enable 'System protection' for C: first.";
            return "Windows returned code " + rv + ". Note: Windows skips creation if a restore point was made in the last 24 h.";
        }
        catch (Exception ex)
        {
            return "Could not create restore point: " + ex.Message;
        }
    }
}
