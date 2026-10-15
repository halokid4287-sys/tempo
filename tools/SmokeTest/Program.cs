using Tempo.Core.Backup;
using Tempo.Core.Games;
using Tempo.Core.Hardware;

// READ-ONLY: which adapter claims each game now, and does it find the RIGHT config?
var sys = new HardwareService().GetSystemInfo();
var games = new GameDetectionService().Detect();
var svc = new GameSettingsService(new BackupService());

int covered = 0;
foreach (var g in games)
{
    var p = svc.BuildProfile(g, sys, Preset.Balanced);
    var inGame = p.Settings.Where(s => s.Source == "game").ToList();
    var drv = p.Settings.Count(s => s.Source == "driver");
    if (inGame.Count > 0) covered++;
    Console.WriteLine($"{g.Name,-36} {(g.HasGameSettings ? g.SettingsAdapter : "— none —"),-18} in-game={inGame.Count,-3} driver={drv}");
    if (p.ConfigPath.Length > 0) Console.WriteLine($"{"",-36} {p.ConfigPath}");
    foreach (var s in inGame.Take(3))
        Console.WriteLine($"{"",-36}   {s.Label,-26} now={s.Current,-14} rec={s.Recommended ?? "(keep)"}");
}
Console.WriteLine($"\n{covered} of {games.Count} games have in-game settings support");
