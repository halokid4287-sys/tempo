using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tempo.Core.Logging;

namespace Tempo.Core.Games;

/// <summary>
/// Finds installed games from every launcher we can read reliably. Entirely local and
/// read-only: launcher manifests and registry keys only, no network, no account access.
/// Vendor-neutral — nothing here depends on which GPU or CPU the PC has.
/// </summary>
public class GameDetectionService
{
    private static string ManualListPath =>
        Path.Combine(Backup.BackupService.DataDir, "games-manual.json");
    private static string HiddenListPath =>
        Path.Combine(Backup.BackupService.DataDir, "games-hidden.json");

    public List<DetectedGame> Detect()
    {
        var all = new List<DetectedGame>();
        foreach (var (name, fn) in new (string, Action<List<DetectedGame>>)[]
        {
            ("Steam", Steam), ("Epic", Epic), ("Ubisoft", Ubisoft), ("GOG", Gog),
            ("EA", Ea), ("Battle.net", BattleNet), ("Xbox", Xbox), ("Manual", Manual),
        })
        {
            try { fn(all); }
            catch (Exception ex) { Log.Write("games", name + " scan failed: " + ex.Message); }
        }

        // Same game via two launchers -> keep one entry (prefer the richer source).
        var merged = all
            .GroupBy(g => Normalize(g.Name))
            .Select(grp => grp.OrderByDescending(g => g.InstallDir.Length > 0)
                              .ThenByDescending(g => g.Exe.Length > 0)
                              .First())
            .ToList();

        var hidden = LoadList(HiddenListPath);
        merged = merged.Where(g => !hidden.Contains(g.Id)).ToList();

        foreach (var g in merged)
        {
            if (g.Exe.Length == 0) g.Exe = GuessExe(g.InstallDir, g.Name);
            if (g.SizeBytes == 0) g.SizeBytes = QuickSize(g.InstallDir);
        }
        return merged.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Normalize(string n) => Regex.Replace(n.ToLowerInvariant(), @"[^a-z0-9]", "");

    // ---------------- Steam ----------------
    private static void Steam(List<DetectedGame> outList)
    {
        var steam = (Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("SteamPath") as string)
                 ?? (Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")?.GetValue("InstallPath") as string);
        if (string.IsNullOrWhiteSpace(steam)) return;
        steam = steam.Replace('/', '\\');

        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), @"""path""\s+""(.+?)"""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    var t = File.ReadAllText(acf);
                    var id = Regex.Match(t, @"""appid""\s+""(\d+)""").Groups[1].Value;
                    var nm = Regex.Match(t, @"""name""\s+""(.+?)""").Groups[1].Value;
                    var dir = Regex.Match(t, @"""installdir""\s+""(.+?)""").Groups[1].Value;
                    var size = Regex.Match(t, @"""SizeOnDisk""\s+""(\d+)""").Groups[1].Value;
                    if (id.Length == 0 || nm.Length == 0) continue;
                    if (IsRedistributable(nm)) continue;
                    var install = Path.Combine(apps, "common", dir);
                    outList.Add(new DetectedGame
                    {
                        Id = "steam:" + id,
                        Name = CleanName(nm),
                        Source = "Steam",
                        InstallDir = Directory.Exists(install) ? install : "",
                        SizeBytes = long.TryParse(size, out var sz) ? sz : 0,
                        LaunchUri = "steam://rungameid/" + id,
                    });
                }
                catch { }
            }
        }
    }

    // ---------------- Epic ----------------
    private static void Epic(List<DetectedGame> outList)
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var r = doc.RootElement;
                string Get(string k) => r.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                var name = Get("DisplayName");
                var loc = Get("InstallLocation");
                if (name.Length == 0 || IsRedistributable(name)) continue;
                var exeRel = Get("LaunchExecutable").Replace('/', '\\');
                var exe = loc.Length > 0 && exeRel.Length > 0 ? Path.Combine(loc, exeRel) : "";
                // Epic's LaunchExecutable is often a bootstrapper/launcher stub; the driver
                // profile has to target the real shipped game exe, so prefer a better match.
                var nameOfExe = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (exe.Length == 0 || nameOfExe.Contains("launcher") || nameOfExe.Contains("bootstrap"))
                {
                    var better = GuessExe(loc, name);
                    if (better.Length > 0) exe = better;
                }
                outList.Add(new DetectedGame
                {
                    Id = "epic:" + (Get("AppName").Length > 0 ? Get("AppName") : name),
                    Name = CleanName(name),
                    Source = "Epic",
                    InstallDir = loc,
                    Exe = File.Exists(exe) ? exe : "",
                    LaunchUri = "com.epicgames.launcher://apps/" + Get("AppName") + "?action=launch",
                });
            }
            catch { }
        }
    }

    // ---------------- Ubisoft ----------------
    private static void Ubisoft(List<DetectedGame> outList)
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs", @"SOFTWARE\Ubisoft\Launcher\Installs" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k == null) continue;
            foreach (var id in k.GetSubKeyNames())
            {
                try
                {
                    using var sub = k.OpenSubKey(id);
                    var dir = (sub?.GetValue("InstallDir") as string)?.Replace('/', '\\').TrimEnd('\\');
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    outList.Add(new DetectedGame
                    {
                        Id = "ubisoft:" + id,
                        Name = CleanName(new DirectoryInfo(dir).Name),
                        Source = "Ubisoft",
                        InstallDir = dir,
                        LaunchUri = "uplay://launch/" + id + "/0",
                    });
                }
                catch { }
            }
        }
    }

    // ---------------- GOG ----------------
    private static void Gog(List<DetectedGame> outList)
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k == null) continue;
            foreach (var id in k.GetSubKeyNames())
            {
                try
                {
                    using var sub = k.OpenSubKey(id);
                    var name = sub?.GetValue("gameName") as string;
                    var dir = sub?.GetValue("path") as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    outList.Add(new DetectedGame
                    {
                        Id = "gog:" + id,
                        Name = CleanName(name),
                        Source = "GOG",
                        InstallDir = dir ?? "",
                        Exe = sub?.GetValue("exe") as string ?? "",
                    });
                }
                catch { }
            }
        }
    }

    // ---------------- EA ----------------
    private static void Ea(List<DetectedGame> outList)
    {
        // EA app records installs under its own uninstall-style keys
        foreach (var hive in new[] { RegistryHive.LocalMachine })
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Electronic Arts", @"SOFTWARE\Electronic Arts" })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var k = baseKey.OpenSubKey(root);
            if (k == null) continue;
            foreach (var name in k.GetSubKeyNames())
            {
                try
                {
                    using var sub = k.OpenSubKey(name);
                    var dir = (sub?.GetValue("Install Dir") as string) ?? (sub?.GetValue("InstallDir") as string);
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                    if (IsRedistributable(name)) continue;
                    outList.Add(new DetectedGame
                    {
                        Id = "ea:" + name, Name = CleanName(name), Source = "EA",
                        InstallDir = dir.TrimEnd('\\'),
                    });
                }
                catch { }
            }
        }
    }

    // ---------------- Battle.net ----------------
    private static void BattleNet(List<DetectedGame> outList)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Overwatch"] = "Overwatch", ["Call of Duty"] = "Call of Duty",
            ["Diablo"] = "Diablo", ["Hearthstone"] = "Hearthstone",
            ["StarCraft"] = "StarCraft", ["World of Warcraft"] = "World of Warcraft",
            ["Heroes of the Storm"] = "Heroes of the Storm",
        };
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var k = Registry.LocalMachine.OpenSubKey(root);
            if (k == null) continue;
            foreach (var sk in k.GetSubKeyNames())
            {
                try
                {
                    using var sub = k.OpenSubKey(sk);
                    var pub = sub?.GetValue("Publisher") as string ?? "";
                    if (!pub.Contains("Blizzard", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = sub?.GetValue("DisplayName") as string ?? "";
                    if (name.Length == 0 || name.Contains("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!known.Keys.Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;
                    outList.Add(new DetectedGame
                    {
                        Id = "bnet:" + sk, Name = CleanName(name), Source = "Battle.net",
                        InstallDir = (sub?.GetValue("InstallLocation") as string ?? "").TrimEnd('\\'),
                    });
                }
                catch { }
            }
        }
    }

    // ---------------- Xbox / Game Pass ----------------
    private static void Xbox(List<DetectedGame> outList)
    {
        try
        {
            var pm = new Windows.Management.Deployment.PackageManager();
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
            foreach (var pkg in pm.FindPackagesForUser(sid))
            {
                try
                {
                    if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                    if (pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue;
                    var loc = pkg.InstalledLocation?.Path ?? "";
                    if (loc.Length == 0) continue;
                    if (!loc.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!HasGamingMarker(loc)) continue;
                    outList.Add(new DetectedGame
                    {
                        Id = "xbox:" + pkg.Id.FamilyName,
                        Name = CleanName(pkg.DisplayName is { Length: > 0 } d ? d : pkg.Id.Name),
                        Source = "Xbox",
                        InstallDir = loc,
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// True only for actual games. Store apps and Xbox system components (Game Bar, Store,
    /// Identity Provider, Spotify…) all live in WindowsApps and mention "xbox" in their
    /// manifests, so the only dependable marker is MicrosoftGame.config — the game config
    /// file that shipped Game Pass / Microsoft Store titles carry and ordinary apps do not.
    /// </summary>
    private static bool HasGamingMarker(string dir)
    {
        try { return File.Exists(Path.Combine(dir, "MicrosoftGame.config")); }
        catch { return false; }
    }

    // ---------------- manual entries ----------------
    private static void Manual(List<DetectedGame> outList)
    {
        try
        {
            if (!File.Exists(ManualListPath)) return;
            var list = JsonSerializer.Deserialize<List<DetectedGame>>(File.ReadAllText(ManualListPath));
            if (list != null) outList.AddRange(list);
        }
        catch { }
    }

    public void AddManual(string exePath, string? displayName = null)
    {
        if (!File.Exists(exePath)) throw new FileNotFoundException("That file doesn't exist.", exePath);
        var list = new List<DetectedGame>();
        try { if (File.Exists(ManualListPath)) list = JsonSerializer.Deserialize<List<DetectedGame>>(File.ReadAllText(ManualListPath)) ?? new(); } catch { }
        var dir = Path.GetDirectoryName(exePath) ?? "";
        var game = new DetectedGame
        {
            Id = "manual:" + Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant(),
            Name = displayName is { Length: > 0 } ? displayName : CleanName(Path.GetFileNameWithoutExtension(exePath)),
            Source = "Manual",
            InstallDir = dir,
            Exe = exePath,
        };
        list.RemoveAll(g => g.Id == game.Id);
        list.Add(game);
        Directory.CreateDirectory(Backup.BackupService.DataDir);
        File.WriteAllText(ManualListPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void Hide(string id)
    {
        var hidden = LoadList(HiddenListPath);
        hidden.Add(id);
        Directory.CreateDirectory(Backup.BackupService.DataDir);
        File.WriteAllText(HiddenListPath, JsonSerializer.Serialize(hidden.Distinct().ToList()));
    }

    private static HashSet<string> LoadList(string path)
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))?.ToHashSet() ?? new(); }
        catch { }
        return new HashSet<string>();
    }

    // ---------------- helpers ----------------
    private static bool IsRedistributable(string name) =>
        name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Steamworks", StringComparison.OrdinalIgnoreCase)
        || name.Contains("DirectX", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Proton", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase);

    private static string CleanName(string n) =>
        n.Replace("®", "").Replace("™", "").Replace("©", "")
         .Replace("’", "'").Replace("â€™", "'")
         .Trim();

    /// <summary>Picks the most plausible main executable in an install folder.</summary>
    internal static string GuessExe(string dir, string gameName)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return "";
        try
        {
            var skip = new[] { "unins", "crash", "setup", "install", "launcher", "redist", "vcredist",
                               "dxsetup", "eac", "battleye", "report", "helper", "service", "update",
                               "elementviewer", "hammer", "bsppack", "vtex", "glview", "benchmark",
                               "config", "editor", "server", "dedicated", "cefprocess", "subprocess",
                               // Source-engine SDK tools that ship inside bin\ and would otherwise win on size
                               "studiomdl", "vbsp", "vvis", "vrad", "vpk", "gmad", "gmpublish",
                               "bspzip", "dmxconvert", "hlmv", "motionmapper", "phonemeextractor" };
            var root = Path.GetFullPath(dir).TrimEnd('\\');
            var scored = new List<(FileInfo f, int score)>();
            foreach (var f in SafeExes(dir, 0))
            {
                var n = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                if (skip.Any(s => n.Contains(s))) continue;
                var key = Normalize(gameName);
                var fn = Normalize(n);
                int score = 0;
                // a name resembling the game is the strongest signal
                if (fn.Length > 2 && (key.Contains(fn) || fn.Contains(key))) score += 1000;
                // the real game exe usually sits at the install root, not in bin/ or tools/
                var depth = Path.GetFullPath(f.FullName).TrimEnd('\\')
                                .Substring(root.Length).TrimStart('\\').Count(c => c == '\\');
                score += Math.Max(0, 300 - depth * 80);
                // Win64/Binaries are where engines ship the real exe — a nudge, not enough to
                // outrank a root-level executable (that put Garry's Mod on a modelling tool).
                var p = f.FullName.ToLowerInvariant();
                if (p.Contains(@"\win64\") || p.Contains(@"\binaries\win64\") || p.Contains(@"\bin\win64\")) score += 100;
                scored.Add((f, score));
            }
            if (scored.Count == 0) return "";
            return scored.OrderByDescending(x => x.score).ThenByDescending(x => x.f.Length).First().f.FullName;
        }
        catch { return ""; }
    }

    private static IEnumerable<FileInfo> SafeExes(string dir, int depth)
    {
        if (depth > 3) yield break;
        FileInfo[] files = Array.Empty<FileInfo>();
        string[] subs = Array.Empty<string>();
        try { files = new DirectoryInfo(dir).GetFiles("*.exe"); } catch { }
        try { subs = Directory.GetDirectories(dir); } catch { }
        foreach (var f in files) yield return f;
        foreach (var s in subs)
        {
            var n = Path.GetFileName(s).ToLowerInvariant();
            if (n is "redist" or "_commonredist" or "engine" or "support" or "directx") continue;
            foreach (var f in SafeExes(s, depth + 1)) yield return f;
        }
    }

    /// <summary>Cheap size estimate — stops early so scanning stays fast.</summary>
    private static long QuickSize(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return 0;
        long total = 0; int n = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
                if (++n > 3000) break;
            }
        }
        catch { }
        return total;
    }
}
