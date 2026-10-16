namespace Tempo.Core.Games;

/// <summary>A game found on this PC, from any launcher.</summary>
public class DetectedGame
{
    public string Id { get; set; } = "";          // stable key, e.g. "steam:359550"
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";      // Steam | Epic | Ubisoft | GOG | Battle.net | Xbox | EA | Manual
    public string InstallDir { get; set; } = "";
    public string Exe { get; set; } = "";         // best-guess main executable (may be empty)
    public string? IconDataUri { get; set; }      // extracted locally from the exe; never downloaded
    public long SizeBytes { get; set; }
    public string LaunchUri { get; set; } = "";   // e.g. steam://rungameid/359550

    // settings support, filled in by the settings layer
    public bool HasGameSettings { get; set; }
    public string SettingsAdapter { get; set; } = "";   // human-readable, e.g. "Unreal Engine"
    public string SettingsNote { get; set; } = "";      // why not, when unsupported
    public bool HasDriverProfile { get; set; }          // a GPU driver profile can be targeted
}

/// <summary>One adjustable setting exposed for a game.</summary>
public class GameSetting
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Description { get; set; } = "";   // what it does, in plain language
    public string Group { get; set; } = "";         // Quality | Display | Effects | Driver
    public string Kind { get; set; } = "choice";    // choice | number | toggle
    public string Current { get; set; } = "";
    public string? Recommended { get; set; }        // for the selected preset
    public List<SettingOption> Options { get; set; } = new();
    public double? Min { get; set; }
    public double? Max { get; set; }
    public bool ReadOnly { get; set; }              // readable but not safely writable
    public string? ReadOnlyReason { get; set; }
    public string Impact { get; set; } = "medium";  // high | medium | low — effect on performance
    public string Source { get; set; } = "game";    // game | driver
}

public class SettingOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>Everything the UI needs for one game's page.</summary>
public class GameProfile
{
    public DetectedGame Game { get; set; } = new();
    public List<GameSetting> Settings { get; set; } = new();
    public List<string> Notes { get; set; } = new();       // honest limitations for this game
    public string ConfigPath { get; set; } = "";
    public bool DriverSettingsAvailable { get; set; }
    public string DriverVendor { get; set; } = "";
    public string? DriverUnavailableReason { get; set; }
    /// <summary>Driver profile these settings land in, and how many programs share it.</summary>
    public string DriverProfileName { get; set; } = "";
    public int DriverProfileApps { get; set; }
}

public class ApplyGameResult
{
    public bool Success { get; set; }
    public int Changed { get; set; }
    public List<string> Applied { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
    public string? Error { get; set; }
    public string? BackupPath { get; set; }
}
