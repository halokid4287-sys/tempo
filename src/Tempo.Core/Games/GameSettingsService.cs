using System.Diagnostics;
using Tempo.Core.Backup;
using Tempo.Core.Logging;
using Tempo.Core.Models;

namespace Tempo.Core.Games;

/// <summary>
/// Builds a per-game settings page: in-game settings via an adapter (vendor-neutral,
/// works on any GPU) plus GPU driver settings where the vendor exposes a supported API.
/// Nothing is written without an explicit apply, and the original file is backed up first.
/// </summary>
public class GameSettingsService
{
    private readonly BackupService _backup;
    private readonly List<IGameSettingsAdapter> _adapters = new()
    {
        new RainbowSixAdapter(),
        new UnrealEngineAdapter(),
        new MinecraftAdapter(),
    };

    public GameSettingsService(BackupService backup) => _backup = backup;

    private static string BackupDir => Path.Combine(BackupService.DataDir, "game-backups");

    public IGameSettingsAdapter? AdapterFor(DetectedGame game, out string configPath)
    {
        configPath = "";
        foreach (var a in _adapters)
        {
            try { if (a.TryGetConfig(game, out var p)) { configPath = p; return a; } }
            catch (Exception ex) { Log.Write("games", a.Name + " probe failed: " + ex.Message); }
        }
        return null;
    }

    /// <summary>Everything the UI needs for one game. Read-only.</summary>
    public GameProfile BuildProfile(DetectedGame game, SystemInfo sys, Preset preset)
    {
        var profile = new GameProfile { Game = game };
        var adapter = AdapterFor(game, out var cfg);

        if (adapter != null)
        {
            profile.ConfigPath = cfg;
            game.HasGameSettings = true;
            game.SettingsAdapter = adapter.Name;
            try
            {
                profile.Settings.AddRange(adapter.Read(cfg, sys));
                foreach (var s in profile.Settings)
                    s.Recommended = adapter.Recommend(s, preset, sys);
            }
            catch (Exception ex)
            {
                Log.Error("games", ex);
                profile.Notes.Add("Could not read this game's settings file: " + ex.Message);
            }
            profile.Notes.Add(adapter.ApplyWarning);
        }
        else
        {
            game.SettingsNote =
                "Tempo doesn't have a settings adapter for this game yet, so its in-game options have to be changed in the game itself. " +
                "Driver-level settings below still apply to it.";
            profile.Notes.Add(game.SettingsNote);
        }

        // ---- GPU driver settings (vendor-dependent) ----
        var gpu = sys.Gpus.FirstOrDefault();
        profile.DriverVendor = gpu?.Vendor ?? "Unknown";
        switch (profile.DriverVendor)
        {
            case "NVIDIA":
                try
                {
                    var drv = new NvidiaDriverSettings();
                    if (drv.Available)
                    {
                        profile.DriverSettingsAvailable = true;
                        profile.Settings.AddRange(drv.ReadForGame(game, preset));
                        game.HasDriverProfile = true;
                    }
                    else profile.DriverUnavailableReason = drv.UnavailableReason;
                }
                catch (Exception ex)
                {
                    profile.DriverUnavailableReason = "NVIDIA driver interface failed: " + ex.Message;
                    Log.Error("games", ex);
                }
                break;

            case "AMD":
                profile.DriverUnavailableReason =
                    "AMD keeps per-game driver settings inside AMD Software's own database, and publishes no supported way for other " +
                    "programs to change them. Rather than show switches that quietly do nothing, Tempo leaves these to AMD Software — " +
                    "the in-game settings above work exactly the same on your card.";
                break;

            case "Intel":
                profile.DriverUnavailableReason =
                    "Intel's graphics software doesn't expose a supported way for other programs to set per-game driver options, " +
                    "so Tempo won't pretend to. Use Intel Graphics Software for those; the in-game settings above work normally.";
                break;

            default:
                profile.DriverUnavailableReason = "No supported GPU driver interface was detected on this PC.";
                break;
        }

        return profile;
    }

    /// <summary>Applies chosen values. Backs the config up first and records it in History.</summary>
    public ApplyGameResult Apply(DetectedGame game, SystemInfo sys, Dictionary<string, string> values, Action<string>? progress = null)
    {
        var result = new ApplyGameResult();
        var session = new BackupSession { Label = "Game settings: " + game.Name };

        var gameValues = values.Where(kv => !kv.Key.StartsWith("drv:")).ToDictionary(k => k.Key, v => v.Value);
        var drvValues = values.Where(kv => kv.Key.StartsWith("drv:")).ToDictionary(k => k.Key, v => v.Value);

        try
        {
            if (gameValues.Count > 0)
            {
                var adapter = AdapterFor(game, out var cfg);
                if (adapter == null)
                {
                    result.Skipped.Add("In-game settings: no adapter for this game.");
                }
                else
                {
                    if (IsGameRunning(game))
                        throw new InvalidOperationException(
                            game.Name + " is running. Close it first — it rewrites its settings file on exit, which would undo these changes.");

                    progress?.Invoke("Backing up " + Path.GetFileName(cfg) + "…");
                    var backup = BackupConfig(cfg, game);
                    result.BackupPath = backup;

                    progress?.Invoke("Writing " + gameValues.Count + " setting(s)…");
                    adapter.Write(cfg, gameValues);
                    result.Changed += gameValues.Count;
                    result.Applied.AddRange(gameValues.Select(kv => kv.Key + " = " + kv.Value));

                    session.Changes.Add(new ChangeRecord
                    {
                        TweakId = "game-settings", Title = game.Name + " — in-game settings (" + gameValues.Count + ")",
                        Type = "game-config", Target = cfg,
                        OldValue = backup, NewValue = string.Join(", ", gameValues.Select(kv => kv.Key + "=" + kv.Value)),
                        Restorable = true,
                    });
                }
            }

            if (drvValues.Count > 0)
            {
                progress?.Invoke("Applying GPU driver settings…");
                var drv = new NvidiaDriverSettings();
                if (!drv.Available) result.Skipped.Add("Driver settings: " + drv.UnavailableReason);
                else
                {
                    var applied = drv.ApplyForGame(game, drvValues, session);
                    result.Changed += applied.Count;
                    result.Applied.AddRange(applied);
                }
            }

            if (session.Changes.Count > 0) _backup.SaveSession(session);
            result.Success = true;
        }
        catch (Exception ex)
        {
            Log.Error("games", ex);
            result.Success = false;
            result.Error = ex.Message;
            if (session.Changes.Count > 0) _backup.SaveSession(session);
        }
        return result;
    }

    private static string BackupConfig(string cfg, DetectedGame game)
    {
        Directory.CreateDirectory(BackupDir);
        var safe = string.Concat(game.Name.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var dest = Path.Combine(BackupDir, $"{safe}_{DateTime.Now:yyyyMMdd-HHmmss}_{Path.GetFileName(cfg)}");
        File.Copy(cfg, dest, true);
        return dest;
    }

    /// <summary>Restores a config file saved by a previous apply.</summary>
    public static string? RestoreConfig(string backupPath, string originalPath)
    {
        try
        {
            if (!File.Exists(backupPath)) return "The saved copy of this file is no longer on disk.";
            File.Copy(backupPath, originalPath, true);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static bool IsGameRunning(DetectedGame game)
    {
        if (game.Exe.Length == 0) return false;
        var name = Path.GetFileNameWithoutExtension(game.Exe);
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }
}
