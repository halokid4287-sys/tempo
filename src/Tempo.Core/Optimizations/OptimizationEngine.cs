using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tempo.Core.Backup;
using Tempo.Core.Hardware;
using Tempo.Core.Models;

namespace Tempo.Core.Optimizations;

/// <summary>
/// Defines every optimization Tempo knows (see OptimizationCatalog*.cs), checks current
/// machine state, and applies selected items with full backup records.
/// Honesty policy: impact ratings are conservative; anecdotal tweaks are labeled as such;
/// checks that only inspect are "diagnostic", one-shot tasks are "action".
/// </summary>
public partial class OptimizationEngine
{
    private readonly BackupService _backup;
    public OptimizationEngine(BackupService backup) => _backup = backup;

    internal record RegVal(RegistryHive Hive, string Key, string Name, object Value, RegistryValueKind Kind);
    internal record PcfgVal(string Sub, string Setting, uint Value, bool Hidden = false);

    internal class TweakDef
    {
        public OptimizationItem Meta = new();
        public List<RegVal> Registry = new();                  // declarative registry writes
        public List<PcfgVal> PowerCfg = new();                 // powercfg sub-setting writes
        public string? Special;                                // named handler, see ApplyOne
        public Func<SystemInfo, bool>? Relevant;               // hardware/OS gate
        public Func<SystemInfo, string?>? NoteFor;             // machine-specific note
        public Func<SystemInfo, string?>? StateFor;            // current system value, human readable
        public Func<SystemInfo, bool?>? AppliedCheck;          // override applied detection (null = fall through)
        public Func<BackupSession, Action<string>?, string>? Execute; // for Kind=action: do it, return summary
    }

    public const string ULTIMATE_GUID = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    public const string HIGHPERF_GUID = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // ---------- Scan ----------

    public List<OptimizationItem> Scan(SystemInfo si)
    {
        var list = new List<OptimizationItem>();
        foreach (var def in Defs())
        {
            var item = def.Meta;
            try
            {
                item.Available = def.Relevant?.Invoke(si) ?? true;
                if (!item.Available) { list.Add(item); continue; }
                item.Note = def.NoteFor?.Invoke(si);
                item.State = def.StateFor?.Invoke(si);
                if (item.Kind == "setting")
                {
                    item.Applied = def.AppliedCheck?.Invoke(si) ?? IsApplied(def, si);
                    if (item.Applied) item.Recommended = false;
                }
                // never preselect medium/high risk, non-reversible, or non-setting items
                if (item.Risk != "low" || !item.Reversible || item.Kind != "setting") item.Recommended = false;
            }
            catch (Exception ex)
            {
                item.State = "scan error: " + ex.Message;
                item.Recommended = false;
            }
            list.Add(item);
        }
        return list;
    }

    private bool IsApplied(TweakDef def, SystemInfo si)
    {
        switch (def.Special)
        {
            case "powerplan":
                return si.ActivePowerPlanGuid.Equals(HIGHPERF_GUID, StringComparison.OrdinalIgnoreCase)
                    || si.ActivePowerPlan.Contains("High performance", StringComparison.OrdinalIgnoreCase)
                    || si.ActivePowerPlan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);
            case "ultimate-plan":
                return si.ActivePowerPlan.Contains("Ultimate", StringComparison.OrdinalIgnoreCase);
            case "hibernate-off":
                return !HiberfilExists();
            case "sysmain-off": return ServiceDisabled("SysMain");
            case "fax-off": return ServiceDisabled("Fax");
            case "display-max-hz":
            {
                var (_, _, hz) = DisplayUtil.Current();
                return hz + 1 >= DisplayUtil.MaxRefreshAtCurrentResolution();
            }
            case "autotuning-fix":
                return MaintenanceService.AutotuningLevel().Equals("normal", StringComparison.OrdinalIgnoreCase);
        }
        bool any = false;
        foreach (var r in def.Registry)
        {
            any = true;
            if (!BackupService.RegistryEquals(r.Hive, r.Key, r.Name, r.Value, r.Kind)) return false;
        }
        foreach (var p in def.PowerCfg)
        {
            any = true;
            if (EffectiveAc(p.Sub, p.Setting) != p.Value) return false;
        }
        return any;
    }

    internal static bool HiberfilExists()
        => File.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", "hiberfil.sys"));

    internal static bool ServiceDisabled(string name)
        => HardwareService.RunCapture("sc.exe", "qc " + name, 4000).Contains("DISABLED", StringComparison.OrdinalIgnoreCase);

    internal static bool ServiceExists(string name)
    {
        var o = HardwareService.RunCapture("sc.exe", "qc " + name, 4000);
        return o.Contains("SERVICE_NAME", StringComparison.OrdinalIgnoreCase) || o.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Current AC index of a powercfg setting — reads the scheme's registry first (locale-proof), falls back to powercfg /q.</summary>
    internal static uint? EffectiveAc(string sub, string setting)
    {
        try
        {
            var scheme = PowerCfg.ActiveSchemeGuid();
            if (scheme != null)
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\" + scheme + @"\" + sub + @"\" + setting);
                if (k?.GetValue("ACSettingIndex") is int v) return unchecked((uint)v);
            }
        }
        catch { }
        return PowerCfg.Query(sub, setting).ac;
    }

    // ---------- Apply ----------

    public List<ApplyResult> Apply(IEnumerable<string> ids, SystemInfo si, BackupSession session, Action<string>? progress = null)
    {
        var results = new List<ApplyResult>();
        var defs = Defs().ToDictionary(d => d.Meta.Id);
        foreach (var id in ids)
        {
            if (!defs.TryGetValue(id, out var def))
            {
                results.Add(new ApplyResult { Id = id, Success = false, Error = "Unknown optimization." });
                continue;
            }
            if (def.Meta.Kind == "diagnostic")
            {
                results.Add(new ApplyResult { Id = id, Success = false, Error = "Diagnostics are run individually, not applied." });
                continue;
            }
            progress?.Invoke((def.Meta.Kind == "action" ? "Running: " : "Applying: ") + def.Meta.Title);
            try
            {
                if (def.Meta.Kind == "action")
                {
                    var summary = def.Execute!(session, progress);
                    results.Add(new ApplyResult { Id = id, Success = true, Error = summary }); // Error field doubles as info text for actions
                    continue;
                }
                ApplyOne(def, si, session, progress);
                results.Add(new ApplyResult { Id = id, Success = true });
            }
            catch (Exception ex)
            {
                results.Add(new ApplyResult { Id = id, Success = false, Error = ex.Message });
            }
        }
        return results;
    }

    private void ApplyOne(TweakDef def, SystemInfo si, BackupSession session, Action<string>? progress)
    {
        // Registry portion — snapshot first, then write
        foreach (var r in def.Registry)
        {
            var rec = BackupService.SnapshotRegistry(def.Meta.Id, def.Meta.Title, r.Hive, r.Key, r.Name, r.Value, r.Kind);
            BackupService.WriteRegistry(r.Hive, r.Key, r.Name, r.Value, r.Kind);
            session.Changes.Add(rec);
        }
        // powercfg portion
        foreach (var p in def.PowerCfg)
        {
            if (p.Hidden) PowerCfg.Unhide(p.Sub, p.Setting);
            var (ac, dc) = PowerCfg.Query(p.Sub, p.Setting);
            ac ??= EffectiveAc(p.Sub, p.Setting);
            var rec = new ChangeRecord
            {
                TweakId = def.Meta.Id, Title = def.Meta.Title, Type = "powercfg-sub",
                Target = p.Sub + "|" + p.Setting,
                OldValue = ac?.ToString() ?? "", OldKind = dc?.ToString(),
                NewValue = p.Value.ToString(),
                Restorable = ac.HasValue
            };
            PowerCfg.Set(p.Sub, p.Setting, p.Value);
            session.Changes.Add(rec);
        }
        switch (def.Special)
        {
            case "powerplan": SwitchPlan(si, session, def.Meta, HIGHPERF_GUID, "High performance"); break;
            case "ultimate-plan": SwitchPlan(si, session, def.Meta, ULTIMATE_GUID, "Ultimate Performance"); break;
            case "hibernate-off": ApplyHibernateOff(session, def.Meta); break;
            case "sysmain-off": DisableService("SysMain", session, def.Meta); break;
            case "fax-off": DisableService("Fax", session, def.Meta); break;
            case "mouse-accel": SystemParams.ApplyMouseFromRegistry(); break;
            case "keyboard-speed": SystemParams.ApplyKeyboardFromRegistry(); break;
            case "display-max-hz": ApplyMaxRefresh(session, def.Meta); break;
            case "autotuning-fix": ApplyAutotuning(session, def.Meta); break;
            case "nagle-off": ApplyNagleOff(session, def.Meta); break;
        }
    }

    /// <summary>Per-interface registry writes, generated at apply time for every interface with an IP.</summary>
    private void ApplyNagleOff(BackupSession session, OptimizationItem meta)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
        if (root == null) throw new InvalidOperationException("TCP interface list not found.");
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            if (k == null) continue;
            var ip = k.GetValue("DhcpIPAddress") as string ?? (k.GetValue("IPAddress") as string[])?.FirstOrDefault() ?? "";
            if (ip.Length == 0 || ip == "0.0.0.0") continue;
            var keyPath = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + sub;
            foreach (var (name, val) in new[] { ("TcpAckFrequency", 1u), ("TCPNoDelay", 1u) })
            {
                var rec = BackupService.SnapshotRegistry(meta.Id, meta.Title, RegistryHive.LocalMachine, keyPath, name, val, RegistryValueKind.DWord);
                BackupService.WriteRegistry(RegistryHive.LocalMachine, keyPath, name, val, RegistryValueKind.DWord);
                session.Changes.Add(rec);
            }
        }
    }

    private void SwitchPlan(SystemInfo si, BackupSession session, OptimizationItem meta, string templateGuid, string nameContains)
    {
        var current = HardwareService.RunCapture("powercfg", "/getactivescheme", 5000);
        var m = Regex.Match(current, @"GUID:\s*([0-9a-fA-F\-]{36})");
        var oldGuid = m.Success ? m.Groups[1].Value : "";

        var listing = HardwareService.RunCapture("powercfg", "/list", 5000);
        string target = "";
        foreach (Match mm in Regex.Matches(listing, @"GUID:\s*([0-9a-fA-F\-]{36})\s*\((.*?)\)"))
        {
            if (mm.Groups[1].Value.Equals(templateGuid, StringComparison.OrdinalIgnoreCase) ||
                mm.Groups[2].Value.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            { target = mm.Groups[1].Value; break; }
        }
        if (target == "")
        {
            var dup = HardwareService.RunCapture("powercfg", "/duplicatescheme " + templateGuid, 5000);
            var dm = Regex.Match(dup, @"GUID:\s*([0-9a-fA-F\-]{36})");
            if (dm.Success) target = dm.Groups[1].Value;
        }
        if (target == "") throw new InvalidOperationException("Could not find or create the power plan.");

        HardwareService.RunCapture("powercfg", "/setactive " + target, 5000);
        session.Changes.Add(new ChangeRecord
        {
            TweakId = meta.Id, Title = meta.Title, Type = "powerplan",
            Target = target, OldValue = oldGuid, NewValue = target, Restorable = oldGuid.Length > 0
        });
    }

    private void ApplyHibernateOff(BackupSession session, OptimizationItem meta)
    {
        bool wasOn = HiberfilExists();
        HardwareService.RunCapture("powercfg", "/hibernate off", 6000);
        session.Changes.Add(new ChangeRecord
        {
            TweakId = meta.Id, Title = meta.Title, Type = "powercfg-hibernate",
            Target = "hibernate", OldValue = wasOn ? "on" : "off", NewValue = "off"
        });
    }

    private void DisableService(string name, BackupSession session, OptimizationItem meta)
    {
        var qc = HardwareService.RunCapture("sc.exe", "qc " + name, 5000);
        string old = qc.Contains("AUTO_START (DELAYED)", StringComparison.OrdinalIgnoreCase) ? "delayed-auto"
            : qc.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase) ? "auto"
            : qc.Contains("DEMAND_START", StringComparison.OrdinalIgnoreCase) ? "demand"
            : qc.Contains("DISABLED", StringComparison.OrdinalIgnoreCase) ? "disabled" : "auto";
        HardwareService.RunCapture("sc.exe", "stop " + name, 8000);
        HardwareService.RunCapture("sc.exe", "config " + name + " start= disabled", 5000);
        session.Changes.Add(new ChangeRecord
        {
            TweakId = meta.Id, Title = meta.Title, Type = "service",
            Target = name, OldValue = old, NewValue = "disabled"
        });
    }

    private void ApplyMaxRefresh(BackupSession session, OptimizationItem meta)
    {
        var (_, _, cur) = DisplayUtil.Current();
        int max = DisplayUtil.MaxRefreshAtCurrentResolution();
        if (max <= cur + 1) return;
        var err = DisplayUtil.SetRefresh(max);
        if (err != null) throw new InvalidOperationException(err);
        session.Changes.Add(new ChangeRecord
        {
            TweakId = meta.Id, Title = meta.Title, Type = "display-mode",
            Target = "primary display", OldValue = cur.ToString(), NewValue = max.ToString()
        });
    }

    private void ApplyAutotuning(BackupSession session, OptimizationItem meta)
    {
        var old = MaintenanceService.AutotuningLevel();
        if (old.Equals("normal", StringComparison.OrdinalIgnoreCase)) return;
        MaintenanceService.SetAutotuningNormal();
        session.Changes.Add(new ChangeRecord
        {
            TweakId = meta.Id, Title = meta.Title, Type = "netsh-autotuning",
            Target = "tcp global", OldValue = old == "unknown" ? "normal" : old, NewValue = "normal",
            Restorable = old != "unknown"
        });
    }
}

/// <summary>Applies Control Panel-style settings immediately without logoff.</summary>
internal static class SystemParams
{
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPI_SETKEYBOARDDELAY = 0x0017;
    private const uint SPI_SETKEYBOARDSPEED = 0x000B;
    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[]? pvParam, uint fWinIni);

    public static void ApplyMouseFromRegistry()
    {
        try { SystemParametersInfo(SPI_SETMOUSE, 0, new[] { 0, 0, 0 }, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE); } catch { }
    }

    public static void ApplyKeyboardFromRegistry()
    {
        try
        {
            SystemParametersInfo(SPI_SETKEYBOARDDELAY, 0, null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);  // shortest delay
            SystemParametersInfo(SPI_SETKEYBOARDSPEED, 31, null, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE); // fastest repeat
        }
        catch { }
    }
}
