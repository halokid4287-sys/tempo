using Tempo.Core.Models;

namespace Tempo.Core.Games;

/// <summary>Preset intent for the performance/quality slider.</summary>
public enum Preset { MaxPerformance = 0, Balanced = 1, MaxQuality = 2 }

/// <summary>
/// Blends a setting between its "all performance" and "all quality" values so the slider
/// can sit anywhere in between, rather than only on three fixed stops. Ordered option
/// lists blend by position in the list; numbers blend numerically.
/// </summary>
internal static class Blend
{
    /// <param name="t">0 = fully performance, 1 = fully quality.</param>
    public static string? Between(GameSetting s, string? perf, string? qual, double t)
    {
        if (perf == null && qual == null) return null;
        if (perf == null) return t >= 0.5 ? qual : null;
        if (qual == null) return t <= 0.5 ? perf : null;
        if (perf == qual) return perf;

        // A list of choices is ordered worst->best, so walk along it.
        if (s.Options.Count > 1)
        {
            int i = s.Options.FindIndex(o => o.Value == perf);
            int j = s.Options.FindIndex(o => o.Value == qual);
            if (i >= 0 && j >= 0)
            {
                int k = (int)Math.Round(i + (j - i) * t);
                return s.Options[Math.Clamp(k, Math.Min(i, j), Math.Max(i, j))].Value;
            }
        }

        if (double.TryParse(perf, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
            double.TryParse(qual, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var b))
        {
            var v = a + (b - a) * t;
            bool whole = !perf.Contains('.') && !qual.Contains('.');
            return whole
                ? ((long)Math.Round(v)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        // values with no order we can reason about (e.g. True/False) flip at the midpoint
        return t >= 0.5 ? qual : perf;
    }
}

/// <summary>
/// Reads and writes one game's own settings file. Every adapter here was written against
/// a real config file inspected on disk — no invented key names. An adapter only claims a
/// game when its config actually exists.
/// </summary>
public interface IGameSettingsAdapter
{
    string Name { get; }
    bool TryGetConfig(DetectedGame game, out string configPath);
    List<GameSetting> Read(string configPath, SystemInfo sys);
    string? Recommend(GameSetting setting, Preset preset, SystemInfo sys);
    void Write(string configPath, Dictionary<string, string> values);
    /// <summary>Warning shown before applying, e.g. "close the game first".</summary>
    string ApplyWarning { get; }
}

// ===================================================================================
//  Rainbow Six Siege — Documents\My Games\Rainbow Six - Siege\<profile>\GameSettings.ini
//  Keys and value meanings are documented by inline comments in the file itself.
// ===================================================================================
public class RainbowSixAdapter : IGameSettingsAdapter
{
    public string Name => "Rainbow Six Siege";
    public string ApplyWarning => "Close Rainbow Six Siege before applying — it rewrites this file when it exits, which would undo the changes.";

    public bool TryGetConfig(DetectedGame game, out string configPath)
    {
        configPath = "";
        if (!game.Name.Contains("Rainbow Six", StringComparison.OrdinalIgnoreCase)) return false;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                                "My Games", "Rainbow Six - Siege");
        if (!Directory.Exists(root)) return false;
        // one folder per Ubisoft profile id; newest wins
        var hit = Directory.GetDirectories(root)
            .Select(d => Path.Combine(d, "GameSettings.ini"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (hit == null) return false;
        configPath = hit;
        return true;
    }

    private static readonly SettingOption[] Scale04 =
    {
        new() { Value = "0", Label = "Low" }, new() { Value = "1", Label = "Medium" },
        new() { Value = "2", Label = "High" }, new() { Value = "3", Label = "Very High" },
        new() { Value = "4", Label = "Ultra" },
    };

    public List<GameSetting> Read(string cfg, SystemInfo sys)
    {
        var ini = new IniFile(cfg);
        var list = new List<GameSetting>();

        void Add(string key, string section, string label, string desc, string group,
                 SettingOption[]? opts, string impact, string kind = "choice")
        {
            var cur = ini.Get(section, key);
            if (cur == null) return;
            list.Add(new GameSetting
            {
                Key = section + "/" + key, Label = label, Description = desc, Group = group,
                Kind = kind, Current = cur, Impact = impact,
                Options = opts?.ToList() ?? new List<SettingOption>(),
            });
        }

        Add("VSync", "DISPLAY_SETTINGS", "V-Sync",
            "Locks frame output to your monitor to stop tearing, at the cost of input latency. Competitive players usually turn it off.",
            "Display", new SettingOption[]
            { new() { Value = "0", Label = "Off" }, new() { Value = "1", Label = "On (1 frame)" }, new() { Value = "2", Label = "On (2 frames)" } },
            "medium");

        Add("WindowMode", "DISPLAY_SETTINGS", "Display mode",
            "Fullscreen gives the lowest latency. Borderless makes alt-tabbing instant but adds a little overhead.",
            "Display", new SettingOption[]
            { new() { Value = "0", Label = "Fullscreen" }, new() { Value = "1", Label = "Windowed" }, new() { Value = "2", Label = "Borderless" } },
            "low");

        Add("FPSLimit", "DISPLAY", "Frame rate limit",
            "0 means unlimited. A cap slightly below your refresh rate keeps frame times steady and reduces latency on VRR displays.",
            "Display", null, "medium", "number");

        Add("NVReflex", "DISPLAY", "NVIDIA Reflex",
            "Cuts input latency by keeping the CPU from running ahead of the GPU. Needs an NVIDIA GPU; Boost also raises clocks when GPU-bound.",
            "Display", new SettingOption[]
            { new() { Value = "0", Label = "Off" }, new() { Value = "1", Label = "On" }, new() { Value = "2", Label = "On + Boost" } },
            "high");

        Add("Texture", "CUSTOM_QUALITY", "Texture quality",
            "Sharpness of surfaces. Costs video memory rather than GPU speed — safe to keep high if you have VRAM spare.",
            "Quality", Scale04, "low");

        Add("TextureFiltering", "CUSTOM_QUALITY", "Texture filtering",
            "Keeps textures sharp when viewed at an angle. Very cheap on modern GPUs.",
            "Quality", Scale04, "low");

        Add("Shadow", "CUSTOM_QUALITY", "Shadow quality",
            "One of the most expensive settings in the game. Lowering it is usually the biggest single FPS win.",
            "Quality", Scale04, "high");

        Add("Lighting", "CUSTOM_QUALITY", "Lighting quality",
            "Quality of lighting and reflections. Moderately expensive.",
            "Quality", Scale04, "medium");

        Add("AntiAliasing", "CUSTOM_QUALITY", "Anti-aliasing",
            "Smooths jagged edges. Higher settings cost performance; some players prefer it low for a clearer image.",
            "Quality", Scale04, "medium");

        Add("TextureStreaming", "CUSTOM_QUALITY", "Texture streaming",
            "How aggressively textures are loaded in. Lower reduces stutter on slower drives.",
            "Quality", Scale04, "low");

        // ---- Upscaling (FSR / DLSS) ----
        // Siege documents FPSLimit, NVReflex, WindowMode and VSync with inline comments but
        // says nothing about how its upscaler values are numbered. Writing a guessed value
        // could leave the game in a graphics mode it can't render, so these are surfaced
        // read-only: people can see what they're running without Tempo gambling on it.
        void Info(string key, string section, string label, string desc)
        {
            var cur = ini.Get(section, key);
            if (cur == null) return;
            list.Add(new GameSetting
            {
                Key = section + "/" + key, Label = label, Description = desc, Group = "Upscaling",
                Kind = "choice", Current = cur, Impact = "high", ReadOnly = true,
                ReadOnlyReason = "Siege doesn't document how it numbers this setting, and a wrong value could break how the game renders. Change it in the game's Display menu — Tempo won't guess.",
            });
        }

        Info("Upscaler", "CUSTOM_QUALITY", "Upscaler in use",
            "Which upscaler Siege is rendering with. Upscaling renders the game below your screen resolution and reconstructs it, which is usually the single biggest FPS gain available.");
        Info("FSRPerfQual", "CUSTOM_QUALITY", "AMD FSR 1 quality",
            "Quality level for AMD FidelityFX Super Resolution 1. FSR works on any graphics card, not just AMD.");
        Info("FSR2PerfQual", "CUSTOM_QUALITY", "AMD FSR 2 quality",
            "Quality level for AMD FidelityFX Super Resolution 2 — sharper and more stable in motion than FSR 1, and still vendor-neutral.");
        Info("DLSSPerfQual", "CUSTOM_QUALITY", "NVIDIA DLSS quality",
            "Quality level for NVIDIA DLSS. Requires an RTX card; AMD and Intel users should use FSR instead.");
        Info("RenderScalingFactor", "CUSTOM_QUALITY", "Render scaling factor",
            "Siege's own resolution scaling. 0 leaves it to the upscaler setting above.");

        return list;
    }

    public string? Recommend(GameSetting s, Preset p, SystemInfo sys)
    {
        bool nvidia = sys.Gpus.Any(g => g.Vendor == "NVIDIA");
        return s.Key switch
        {
            "DISPLAY_SETTINGS/VSync" => "0",
            "DISPLAY_SETTINGS/WindowMode" => "0",
            "DISPLAY/NVReflex" => nvidia ? (p == Preset.MaxQuality ? "1" : "2") : null,
            "CUSTOM_QUALITY/Shadow" => p switch { Preset.MaxPerformance => "0", Preset.Balanced => "2", _ => "4" },
            "CUSTOM_QUALITY/Lighting" => p switch { Preset.MaxPerformance => "0", Preset.Balanced => "1", _ => "3" },
            "CUSTOM_QUALITY/AntiAliasing" => p switch { Preset.MaxPerformance => "1", Preset.Balanced => "3", _ => "4" },
            "CUSTOM_QUALITY/Texture" => p switch { Preset.MaxPerformance => "2", _ => "4" },
            "CUSTOM_QUALITY/TextureFiltering" => p == Preset.MaxPerformance ? "2" : "4",
            "CUSTOM_QUALITY/TextureStreaming" => p == Preset.MaxPerformance ? "1" : "3",
            _ => null,
        };
    }

    public void Write(string cfg, Dictionary<string, string> values)
    {
        var ini = new IniFile(cfg);
        foreach (var (k, v) in values)
        {
            var parts = k.Split('/', 2);
            if (parts.Length == 2) ini.Set(parts[0], parts[1], v);
        }
        ini.Save();
    }
}

// ===================================================================================
//  Unreal Engine 4/5 — GameUserSettings.ini with [ScalabilityGroups] sg.* keys.
//  This is engine-standard, so it covers many Unreal titles, not just Fortnite.
// ===================================================================================
public class UnrealEngineAdapter : IGameSettingsAdapter
{
    public string Name => "Unreal Engine";
    public string ApplyWarning => "Close the game first — Unreal rewrites this file on exit and would overwrite the changes.";

    public bool TryGetConfig(DetectedGame game, out string configPath)
    {
        configPath = "";
        foreach (var p in CandidatePaths(game))
        {
            if (!File.Exists(p)) continue;
            // only claim it if it really is an Unreal scalability config
            var ini = new IniFile(p);
            if (ini.HasSection("ScalabilityGroups")) { configPath = p; return true; }
        }
        return false;
    }

    /// <summary>
    /// Unreal keeps its config under a folder named after the PROJECT, which is not always the
    /// store name (Fortnite -> FortniteGame). The folder must therefore be correlated with this
    /// specific game: an earlier version simply took the first GameUserSettings.ini it found,
    /// which pointed every game on the PC at Fortnite's config file.
    /// </summary>
    private static IEnumerable<string> CandidatePaths(DetectedGame game)
    {
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(local)) roots.Add(local);
        var myGames = Path.Combine(docs, "My Games");
        if (Directory.Exists(myGames)) roots.Add(myGames);

        var key = Norm(game.Name);
        if (key.Length < 3) yield break;

        foreach (var root in roots)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var dir in dirs)
            {
                var folder = Norm(Path.GetFileName(dir));
                // require a real name correlation between folder and game
                if (!(folder.Contains(key) || key.Contains(folder)) || folder.Length < 3) continue;
                foreach (var variant in new[] { "WindowsClient", "Windows", "WindowsNoEditor" })
                {
                    var p = Path.Combine(dir, "Saved", "Config", variant, "GameUserSettings.ini");
                    if (File.Exists(p)) yield return p;
                }
            }
        }
    }

    private static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())
            .Replace("game", "").Replace("launcher", "");

    private static readonly SettingOption[] Scale03 =
    {
        new() { Value = "0", Label = "Low" }, new() { Value = "1", Label = "Medium" },
        new() { Value = "2", Label = "High" }, new() { Value = "3", Label = "Epic" },
    };

    public List<GameSetting> Read(string cfg, SystemInfo sys)
    {
        var ini = new IniFile(cfg);
        var list = new List<GameSetting>();

        void Sg(string key, string label, string desc, string impact)
        {
            var cur = ini.Get("ScalabilityGroups", key);
            if (cur == null) return;
            list.Add(new GameSetting
            {
                Key = "ScalabilityGroups/" + key, Label = label, Description = desc,
                Group = "Quality", Kind = "choice", Current = cur, Impact = impact,
                Options = Scale03.ToList(),
            });
        }

        Sg("sg.ShadowQuality", "Shadow quality", "Usually the single most expensive setting. Dropping it is the biggest easy FPS gain.", "high");
        Sg("sg.EffectsQuality", "Effects quality", "Particles, smoke and explosions. Heavy in busy fights.", "high");
        Sg("sg.PostProcessQuality", "Post-processing", "Motion blur, bloom and depth of field. Lowering it also makes the image clearer.", "medium");
        Sg("sg.TextureQuality", "Texture quality", "Surface detail. Uses video memory more than GPU time.", "low");
        Sg("sg.ViewDistanceQuality", "View distance", "How far away objects keep rendering. Costs CPU as much as GPU.", "medium");
        Sg("sg.AntiAliasingQuality", "Anti-aliasing", "Edge smoothing. Low settings look sharper but jaggier.", "medium");
        Sg("sg.FoliageQuality", "Foliage quality", "Grass and trees. Lowering it can also improve visibility.", "medium");
        Sg("sg.ReflectionQuality", "Reflection quality", "Quality of reflective surfaces.", "medium");
        Sg("sg.GlobalIlluminationQuality", "Global illumination", "Indirect bounced light. Expensive on older GPUs.", "high");
        Sg("sg.ShadingQuality", "Shading quality", "Material and lighting detail.", "medium");

        var rq = ini.Get("ScalabilityGroups", "sg.ResolutionQuality");
        if (rq != null)
            list.Add(new GameSetting
            {
                Key = "ScalabilityGroups/sg.ResolutionQuality", Label = "Render scale (upscaling)",
                Description = "Renders the 3D world at a percentage of your screen resolution and upscales it — the same thing FSR and DLSS do underneath, and it works on any graphics card. 100 is native; 80 is roughly a 'quality' preset. Usually the single biggest FPS gain available.",
                Group = "Upscaling", Kind = "number", Current = rq, Min = 50, Max = 100, Impact = "high",
            });

        var section = FindGameSection(cfg);

        // Which upscaler the game is using. Values are the game's own strings, so this is
        // shown rather than set — writing an unrecognised one could stop it rendering.
        foreach (var (key, label, desc) in new[]
        {
            ("FortAntiAliasingMethod", "Upscaler / anti-aliasing method",
             "Which upscaler the game is running: TSR and FSR work on any card, DLSS needs an NVIDIA RTX card, XeSS is Intel's."),
            ("bEnableDLSSFrameGeneration", "DLSS frame generation",
             "Generates extra frames. Needs an NVIDIA RTX 40-series or newer; AMD's equivalent is FSR 3 frame generation, set in-game."),
        })
        {
            var v = ini.Get(section, key);
            if (v == null) continue;
            list.Add(new GameSetting
            {
                Key = section + "/" + key, Label = label, Description = desc, Group = "Upscaling",
                Kind = "choice", Current = v, Impact = "high", ReadOnly = true,
                ReadOnlyReason = "This game names its upscaler modes itself and doesn't document them, so Tempo won't guess. Pick it in the game's video settings — the render scale above is the part that's safe to set from here.",
            });
        }

        void Game(string key, string label, string desc, string kind, SettingOption[]? opts, string impact, double? min = null, double? max = null)
        {
            var cur = ini.Get(section, key);
            if (cur == null) return;
            list.Add(new GameSetting
            {
                Key = section + "/" + key, Label = label, Description = desc, Group = "Display",
                Kind = kind, Current = cur, Impact = impact, Min = min, Max = max,
                Options = opts?.ToList() ?? new List<SettingOption>(),
            });
        }

        Game("bUseVSync", "V-Sync", "Stops screen tearing but adds input latency. Competitive players normally leave it off.",
            "toggle", new SettingOption[] { new() { Value = "False", Label = "Off" }, new() { Value = "True", Label = "On" } }, "medium");
        Game("FrameRateLimit", "Frame rate limit", "0 means unlimited. A cap keeps frame times consistent and reduces heat and coil whine.",
            "number", null, "medium", 0, 480);
        Game("PreferredFullscreenMode", "Display mode", "0 is exclusive fullscreen (lowest latency), 1 is borderless windowed.",
            "choice", new SettingOption[] { new() { Value = "0", Label = "Fullscreen" }, new() { Value = "1", Label = "Borderless" } }, "low");

        return list;
    }

    private static string FindGameSection(string cfg)
    {
        foreach (var line in File.ReadLines(cfg))
        {
            var t = line.Trim();
            if (t.StartsWith('[') && t.Contains("GameUserSettings", StringComparison.OrdinalIgnoreCase))
                return t.Trim('[', ']');
        }
        return "/Script/Engine.GameUserSettings";
    }

    public string? Recommend(GameSetting s, Preset p, SystemInfo sys)
    {
        string Tier(string low, string mid, string high) =>
            p == Preset.MaxPerformance ? low : p == Preset.Balanced ? mid : high;

        if (s.Key.EndsWith("sg.ResolutionQuality")) return Tier("80", "100", "100");
        if (s.Key.EndsWith("sg.ShadowQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("sg.EffectsQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("sg.PostProcessQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("sg.TextureQuality")) return Tier("1", "2", "3");
        if (s.Key.EndsWith("sg.ViewDistanceQuality")) return Tier("2", "2", "3");
        if (s.Key.EndsWith("sg.AntiAliasingQuality")) return Tier("0", "2", "3");
        if (s.Key.EndsWith("sg.FoliageQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("sg.ReflectionQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("sg.GlobalIlluminationQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("sg.ShadingQuality")) return Tier("0", "1", "3");
        if (s.Key.EndsWith("bUseVSync")) return "False";
        if (s.Key.EndsWith("PreferredFullscreenMode")) return "0";
        return null;
    }

    public void Write(string cfg, Dictionary<string, string> values)
    {
        var ini = new IniFile(cfg);
        foreach (var (k, v) in values)
        {
            var parts = k.Split('/');
            // section names contain slashes (/Script/FortniteGame.X) so split from the right
            var key = parts[^1];
            var section = k[..(k.Length - key.Length - 1)];
            ini.Set(section, key, v);
        }
        ini.Save();
    }
}

// ===================================================================================
//  Minecraft Java — %AppData%\.minecraft\options.txt  (key:value, one per line)
// ===================================================================================
public class MinecraftAdapter : IGameSettingsAdapter
{
    public string Name => "Minecraft Java";
    public string ApplyWarning => "Close Minecraft before applying — it saves this file on exit and would overwrite the changes.";

    public bool TryGetConfig(DetectedGame game, out string configPath)
    {
        configPath = "";
        if (!game.Name.Contains("Minecraft", StringComparison.OrdinalIgnoreCase)) return false;
        // options.txt belongs to the JAVA edition only. The Microsoft Store "Minecraft for
        // Windows" is Bedrock and stores its settings elsewhere, so claiming it here would
        // edit the wrong game's settings entirely.
        if (!game.Name.Contains("Java", StringComparison.OrdinalIgnoreCase)) return false;
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "options.txt");
        if (!File.Exists(p)) return false;
        configPath = p;
        return true;
    }

    private static Dictionary<string, string> Parse(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Split(':', 2))
            .Where(a => a.Length == 2)
            .GroupBy(a => a[0]).Select(g => g.First())
            .ToDictionary(a => a[0], a => a[1]);

    public List<GameSetting> Read(string cfg, SystemInfo sys)
    {
        var kv = Parse(cfg);
        var list = new List<GameSetting>();
        void Add(string key, string label, string desc, string kind, SettingOption[]? opts, string impact, double? min = null, double? max = null)
        {
            if (!kv.TryGetValue(key, out var cur)) return;
            list.Add(new GameSetting
            {
                Key = key, Label = label, Description = desc, Group = "Quality", Kind = kind,
                Current = cur, Impact = impact, Min = min, Max = max,
                Options = opts?.ToList() ?? new List<SettingOption>(),
            });
        }

        Add("renderDistance", "Render distance (chunks)", "How far the world is drawn. By far the biggest performance setting in Minecraft, and heavily CPU-bound.", "number", null, "high", 2, 32);
        Add("simulationDistance", "Simulation distance", "How far entities and blocks keep updating. Costs CPU.", "number", null, "high", 5, 32);
        Add("graphicsMode", "Graphics mode", "Fancy adds transparency and connected visuals; Fast is noticeably quicker.", "choice",
            new SettingOption[] { new() { Value = "0", Label = "Fast" }, new() { Value = "1", Label = "Fancy" }, new() { Value = "2", Label = "Fabulous" } }, "medium");
        Add("enableVsync", "V-Sync", "Caps frames to your monitor. Turning it off can raise FPS but may tear.", "toggle",
            new SettingOption[] { new() { Value = "false", Label = "Off" }, new() { Value = "true", Label = "On" } }, "medium");
        Add("maxFps", "Max FPS", "Frame cap. 260 behaves as unlimited in Minecraft.", "number", null, "medium", 30, 260);
        Add("particles", "Particles", "Amount of particle effects drawn.", "choice",
            new SettingOption[] { new() { Value = "0", Label = "All" }, new() { Value = "1", Label = "Decreased" }, new() { Value = "2", Label = "Minimal" } }, "medium");
        Add("ao", "Smooth lighting", "Soft shading between blocks. Moderate cost.", "toggle",
            new SettingOption[] { new() { Value = "false", Label = "Off" }, new() { Value = "true", Label = "On" } }, "medium");
        Add("entityShadows", "Entity shadows", "Shadows under mobs and players. Cheap to disable.", "toggle",
            new SettingOption[] { new() { Value = "false", Label = "Off" }, new() { Value = "true", Label = "On" } }, "low");
        return list;
    }

    public string? Recommend(GameSetting s, Preset p, SystemInfo sys) => s.Key switch
    {
        "renderDistance" => p switch { Preset.MaxPerformance => "8", Preset.Balanced => "12", _ => "16" },
        "simulationDistance" => p switch { Preset.MaxPerformance => "6", Preset.Balanced => "8", _ => "12" },
        "graphicsMode" => p == Preset.MaxPerformance ? "0" : p == Preset.Balanced ? "1" : "2",
        "enableVsync" => "false",
        "particles" => p == Preset.MaxPerformance ? "2" : p == Preset.Balanced ? "1" : "0",
        "ao" => p == Preset.MaxPerformance ? "false" : "true",
        "entityShadows" => p == Preset.MaxPerformance ? "false" : "true",
        _ => null,
    };

    public void Write(string cfg, Dictionary<string, string> values)
    {
        // preserve the file's own line endings and trailing newline — Minecraft reads this back
        var original = File.ReadAllText(cfg);
        var nl = original.Contains("\r\n") ? "\r\n" : "\n";
        var trailing = original.EndsWith('\n');
        var lines = original.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();

        foreach (var (k, v) in values)
        {
            bool found = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var a = lines[i].Split(':', 2);
                if (a.Length == 2 && a[0] == k) { lines[i] = k + ":" + v; found = true; break; }
            }
            if (!found) lines.Add(k + ":" + v);
        }
        File.WriteAllText(cfg, string.Join(nl, lines) + (trailing ? nl : ""), new System.Text.UTF8Encoding(false));
    }
}
