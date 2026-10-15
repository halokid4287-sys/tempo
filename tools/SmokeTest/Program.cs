using Tempo.Core.Games;
using Tempo.Core.Hardware;

// Confirm the probe left nothing behind on the user's driver profile.
var sys = new HardwareService().GetSystemInfo();
var game = new GameDetectionService().Detect().First(g => g.Name.Contains("Rainbow"));
var nv = new NvidiaDriverSettings();
Console.WriteLine($"{game.Name} driver profile state:");
foreach (var s in nv.ReadForGame(game, Preset.Balanced))
{
    var label = s.Options.FirstOrDefault(o => o.Value == s.Current)?.Label ?? s.Current;
    Console.WriteLine($"   {s.Label,-28} {label}{(s.ReadOnly ? "   [read-only]" : "")}");
}
