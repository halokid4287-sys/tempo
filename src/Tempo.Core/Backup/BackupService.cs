using System.Text.Json;
using Microsoft.Win32;
using Tempo.Core.Models;

namespace Tempo.Core.Backup;

/// <summary>
/// Records every change Tempo makes (old value + new value) into
/// %ProgramData%\Tempo\history.json and can roll changes back.
/// </summary>
public class BackupService
{
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tempo");
    private static string HistoryPath => Path.Combine(DataDir, "history.json");
    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static BackupService() => MigrateLegacyData();

    /// <summary>
    /// One-time carry-over from the app's former name, so change history (and therefore
    /// Restore) survives the rename. No-op on a fresh install.
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Velocity");
            if (!Directory.Exists(legacy)) return;
            Directory.CreateDirectory(DataDir);
            bool allCopied = true;
            foreach (var src in Directory.GetFiles(legacy))
            {
                var dest = Path.Combine(DataDir, Path.GetFileName(src));
                if (File.Exists(dest)) continue;      // never clobber newer data
                try { File.Copy(src, dest); } catch { allCopied = false; }
            }
            // tidy up the old folder once its contents are safely carried over
            if (allCopied) { try { Directory.Delete(legacy, true); } catch { } }
        }
        catch { }
    }

    public List<BackupSession> LoadHistory()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(HistoryPath)) return new();
                return JsonSerializer.Deserialize<List<BackupSession>>(File.ReadAllText(HistoryPath)) ?? new();
            }
            catch { return new(); }
        }
    }

    public void SaveSession(BackupSession session) => UpsertSession(session);

    /// <summary>
    /// Insert or update a session by Id. Call after EVERY change in a long batch so that
    /// a crash / force-close mid-batch never loses the record of what was already done.
    /// </summary>
    public void UpsertSession(BackupSession session)
    {
        if (session.Changes.Count == 0) return;
        lock (_lock)
        {
            var all = LoadHistoryUnsafe();
            var idx = all.FindIndex(s => s.Id == session.Id);
            if (idx >= 0) all[idx] = session; else all.Insert(0, session);
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all, JsonOpts));
        }
    }

    private List<BackupSession> LoadHistoryUnsafe()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new();
            return JsonSerializer.Deserialize<List<BackupSession>>(File.ReadAllText(HistoryPath)) ?? new();
        }
        catch { return new(); }
    }

    // ---------- Recording helpers (call BEFORE changing anything) ----------

    /// <summary>Snapshot a registry value into a ChangeRecord before it is modified.</summary>
    public static ChangeRecord SnapshotRegistry(string tweakId, string title, RegistryHive hive, string keyPath, string valueName, object newValue, RegistryValueKind newKind)
    {
        var rec = new ChangeRecord
        {
            TweakId = tweakId,
            Title = title,
            Type = "registry",
            Target = hive + @"\" + keyPath,
            ValueName = valueName,
            NewValue = ValueToString(newValue, newKind),
        };
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(keyPath);
            var existing = key?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (existing == null || key == null)
            {
                rec.OldValue = null; // value (or key) did not exist -> restore deletes it
                rec.OldKind = null;
            }
            else
            {
                var kind = key.GetValueKind(valueName);
                rec.OldValue = ValueToString(existing, kind);
                rec.OldKind = kind.ToString();
            }
        }
        catch
        {
            rec.OldValue = null;
            rec.OldKind = null;
        }
        return rec;
    }

    public static string ValueToString(object value, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        RegistryValueKind.MultiString => string.Join("\n", (string[])value),
        _ => value.ToString() ?? ""
    };

    public static object StringToValue(string s, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => unchecked((int)uint.Parse(s)),
        RegistryValueKind.QWord => unchecked((long)ulong.Parse(s)),
        RegistryValueKind.Binary => Convert.FromBase64String(s),
        RegistryValueKind.MultiString => s.Split('\n'),
        _ => s
    };

    // ---------- Apply + restore for registry ----------

    public static void WriteRegistry(RegistryHive hive, string keyPath, string valueName, object value, RegistryValueKind kind)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.CreateSubKey(keyPath, true)
            ?? throw new InvalidOperationException("Cannot open " + keyPath);
        // registry DWORDs come through as uint in our tweak table; Registry API wants int
        if (kind == RegistryValueKind.DWord && value is uint u) value = unchecked((int)u);
        if (kind == RegistryValueKind.QWord && value is ulong ul) value = unchecked((long)ul);
        key.SetValue(valueName, value, kind);
    }

    /// <summary>Returns true if the registry value currently equals the given value.</summary>
    public static bool RegistryEquals(RegistryHive hive, string keyPath, string valueName, object expected, RegistryValueKind kind)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(keyPath);
            var v = key?.GetValue(valueName, null);
            if (v == null) return false;
            return ValueToString(v, kind) == ValueToString(expected, kind);
        }
        catch { return false; }
    }

    /// <summary>Roll back a single change record. Returns an error string or null on success.</summary>
    public string? Restore(ChangeRecord rec)
    {
        try
        {
            switch (rec.Type)
            {
                case "registry":
                {
                    var parts = rec.Target.Split('\\', 2);
                    var hive = Enum.Parse<RegistryHive>(parts[0]);
                    var keyPath = parts[1];
                    using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                    if (rec.OldValue == null)
                    {
                        using var key = baseKey.OpenSubKey(keyPath, true);
                        key?.DeleteValue(rec.ValueName ?? "", false);
                    }
                    else
                    {
                        var kind = Enum.Parse<RegistryValueKind>(rec.OldKind ?? "String");
                        WriteRegistry(hive, keyPath, rec.ValueName ?? "", StringToValue(rec.OldValue, kind), kind);
                    }
                    return null;
                }
                case "powerplan":
                {
                    if (string.IsNullOrWhiteSpace(rec.OldValue)) return "No previous power plan recorded.";
                    var r = Hardware.HardwareService.RunCapture("powercfg", "/setactive " + rec.OldValue, 6000);
                    return null;
                }
                case "powercfg-hibernate":
                {
                    Hardware.HardwareService.RunCapture("powercfg", rec.OldValue == "on" ? "/hibernate on" : "/hibernate off", 6000);
                    return null;
                }
                case "service":
                {
                    // OldValue = previous start type: auto|demand|disabled|delayed-auto
                    if (string.IsNullOrWhiteSpace(rec.OldValue)) return "No previous service state recorded.";
                    Hardware.HardwareService.RunCapture("sc.exe", "config \"" + rec.Target + "\" start= " + rec.OldValue, 6000);
                    if (rec.OldValue is "auto" or "delayed-auto")
                        Hardware.HardwareService.RunCapture("sc.exe", "start \"" + rec.Target + "\"", 6000);
                    return null;
                }
                case "powercfg-sub":
                {
                    // Target = "subGuid|settingGuid", OldValue = previous AC index, OldKind = previous DC index
                    if (string.IsNullOrWhiteSpace(rec.OldValue)) return "No previous power setting value recorded.";
                    var g = rec.Target.Split('|');
                    if (g.Length != 2) return "Malformed power setting record.";
                    uint ac = uint.Parse(rec.OldValue);
                    uint? dc = uint.TryParse(rec.OldKind, out var d) ? d : null;
                    Optimizations.PowerCfg.Set(g[0], g[1], ac, dc);
                    return null;
                }
                case "display-mode":
                {
                    if (!int.TryParse(rec.OldValue, out var hz) || hz <= 0) return "No previous refresh rate recorded.";
                    return Optimizations.DisplayUtil.SetRefresh(hz);
                }
                case "netsh-autotuning":
                {
                    if (string.IsNullOrWhiteSpace(rec.OldValue)) return "No previous auto-tuning level recorded.";
                    Hardware.HardwareService.RunCapture("netsh", "int tcp set global autotuninglevel=" + rec.OldValue.ToLowerInvariant(), 8000);
                    return null;
                }
                default:
                    return "This type of change (" + rec.Type + ") cannot be automatically restored.";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Mark a record restored and persist.</summary>
    public void MarkRestored(string sessionId, string changeId)
    {
        lock (_lock)
        {
            var all = LoadHistoryUnsafe();
            var rec = all.FirstOrDefault(s => s.Id == sessionId)?.Changes.FirstOrDefault(c => c.Id == changeId);
            if (rec != null)
            {
                rec.Restored = true;
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(all, JsonOpts));
            }
        }
    }
}
