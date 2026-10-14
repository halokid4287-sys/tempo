using Microsoft.Win32;
using Tempo.Core.Models;

namespace Tempo.Core.Optimizations;

/// <summary>
/// Lists and toggles startup apps using the same mechanism as Task Manager
/// (StartupApproved registry flags) — fully reversible, nothing is deleted.
/// </summary>
public class StartupService
{
    private record Source(RegistryHive Hive, string RunKey, string ApprovedKey, string Scope, string Pretty);

    private static readonly Source[] Sources =
    {
        new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "user", "HKCU Run"),
        new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", "machine", "HKLM Run"),
        new(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", "machine", "HKLM Run (32-bit)"),
    };

    // Known names -> short hints so users know what they're looking at
    private static readonly Dictionary<string, string> Hints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OneDrive"] = "Microsoft cloud file sync. Safe to disable if you don't use OneDrive.",
        ["Discord"] = "Chat app. You can launch it manually when you need it.",
        ["Steam"] = "Game launcher. Starting it manually saves RAM at boot.",
        ["EpicGamesLauncher"] = "Game launcher. Not needed at startup.",
        ["Spotify"] = "Music app. Not needed at startup.",
        ["CORSAIR iCUE"] = "RGB/peripheral software. Needed at startup only if you rely on its profiles.",
        ["RazerCortex"] = "Razer 'booster' — generally unnecessary.",
        ["NvBackend"] = "Part of NVIDIA GeForce Experience.",
        ["SecurityHealth"] = "Windows Security icon. KEEP enabled.",
        ["RtkAudUService"] = "Realtek audio helper. Usually keep.",
        ["Wallpaper Engine"] = "Animated wallpapers; uses GPU cycles on the desktop (pauses in fullscreen games).",
        ["Teams"] = "Microsoft Teams. Disable if you don't use it for work.",
        ["com.squirrel.Teams.Teams"] = "Microsoft Teams auto-start.",
        ["MicrosoftEdgeAutoLaunch"] = "Preloads Edge at boot. Safe to disable.",
        ["iTunesHelper"] = "Apple device detection. Safe to disable.",
        ["CCleaner"] = "Cleaner utility auto-start. Safe to disable.",
    };

    public List<StartupApp> List()
    {
        var apps = new List<StartupApp>();
        foreach (var src in Sources)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(src.Hive, RegistryView.Default);
                using var run = baseKey.OpenSubKey(src.RunKey);
                if (run == null) continue;
                using var approved = baseKey.OpenSubKey(src.ApprovedKey);
                foreach (var name in run.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var cmd = run.GetValue(name)?.ToString() ?? "";
                    bool enabled = true;
                    var flag = approved?.GetValue(name) as byte[];
                    if (flag is { Length: > 0 } && (flag[0] & 0x01) == 0x01) enabled = false; // odd first byte = disabled
                    var hint = Hints.FirstOrDefault(h => name.Contains(h.Key, StringComparison.OrdinalIgnoreCase)
                        || cmd.Contains(h.Key, StringComparison.OrdinalIgnoreCase)).Value;
                    apps.Add(new StartupApp
                    {
                        Name = name, Command = cmd, Scope = src.Scope,
                        Location = src.Pretty, Source = "run-key", Enabled = enabled, Hint = hint
                    });
                }
            }
            catch { }
        }
        return apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Enable/disable a startup entry exactly like Task Manager does. Returns error or null.</summary>
    public string? SetEnabled(string name, string scope, string location, bool enable, BackupSession session)
    {
        var src = Sources.FirstOrDefault(s => s.Scope == scope && s.Pretty == location);
        if (src == null) return "Unknown startup source.";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(src.Hive, RegistryView.Default);
            using var approved = baseKey.CreateSubKey(src.ApprovedKey, true);
            if (approved == null) return "Cannot open StartupApproved key.";

            var old = approved.GetValue(name) as byte[];
            var rec = new ChangeRecord
            {
                TweakId = "startup-" + (enable ? "enable" : "disable"),
                Title = (enable ? "Enabled" : "Disabled") + " startup app: " + name,
                Type = "registry",
                Target = src.Hive + @"\" + src.ApprovedKey,
                ValueName = name,
                OldValue = old != null ? Convert.ToBase64String(old) : null,
                OldKind = old != null ? "Binary" : null,
            };

            byte[] bytes = new byte[12];
            bytes[0] = (byte)(enable ? 0x02 : 0x03);
            if (!enable)
            {
                var ft = DateTime.UtcNow.ToFileTimeUtc();
                BitConverter.GetBytes(ft).CopyTo(bytes, 4);
            }
            approved.SetValue(name, bytes, RegistryValueKind.Binary);
            rec.NewValue = Convert.ToBase64String(bytes);
            session.Changes.Add(rec);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }
}
