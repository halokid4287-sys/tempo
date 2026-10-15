using Microsoft.Win32;
using Tempo.Core.Models;

namespace Tempo.Core.Games;

// ===================================================================================
//  UNITY — settings live in the registry under HKCU\Software\<Company>\<Product>
//  as PlayerPrefs. Key names carry a per-name hash suffix ("..._h182942802"), but the
//  prefix is stable, so this works for any Unity game rather than a specific title.
// ===================================================================================
public class UnityAdapter : IGameSettingsAdapter
{
    public string Name => "Unity";
    public string ApplyWarning => "Close the game first — Unity writes these values back when it exits.";

    private const string ResW = "Screenmanager Resolution Width";
    private const string ResH = "Screenmanager Resolution Height";
    private const string Mode = "Screenmanager Fullscreen mode";
    private const string UseNative = "Screenmanager Resolution Use Native";
    private const string Quality = "UnityGraphicsQuality";

    public bool TryGetConfig(DetectedGame game, out string configPath)
    {
        configPath = "";
        var key = FindKey(game);
        if (key == null) return false;
        configPath = key;   // registry path acts as the "config path"
        return true;
    }

    /// <summary>Locates the game's PlayerPrefs key by name, confirming it is really Unity's.</summary>
    private static string? FindKey(DetectedGame game)
    {
        var target = Norm(game.Name);
        if (target.Length < 3) return null;
        try
        {
            using var sw = Registry.CurrentUser.OpenSubKey("SOFTWARE");
            if (sw == null) return null;
            foreach (var company in sw.GetSubKeyNames())
            {
                RegistryKey? ck = null;
                try { ck = sw.OpenSubKey(company); } catch { }
                if (ck == null) continue;
                using (ck)
                {
                    foreach (var product in ck.GetSubKeyNames())
                    {
                        if (!Norm(product).Equals(target, StringComparison.Ordinal)) continue;
                        using var pk = ck.OpenSubKey(product);
                        if (pk == null) continue;
                        // only claim it when Unity's own markers are present
                        if (pk.GetValueNames().Any(v => v.StartsWith(ResW, StringComparison.Ordinal)))
                            return @"HKCU\SOFTWARE\" + company + @"\" + product;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static RegistryKey? Open(string path, bool write) =>
        Registry.CurrentUser.OpenSubKey(path["HKCU\\".Length..], write);

    /// <summary>Finds the real value name, which carries Unity's hash suffix.</summary>
    private static string? Resolve(RegistryKey key, string prefix) =>
        key.GetValueNames().FirstOrDefault(v => v.StartsWith(prefix + "_h", StringComparison.Ordinal));

    public List<GameSetting> Read(string path, SystemInfo sys)
    {
        var list = new List<GameSetting>();
        using var key = Open(path, false);
        if (key == null) return list;

        int? Val(string prefix)
        {
            var n = Resolve(key, prefix);
            return n == null ? null : key.GetValue(n) as int?;
        }

        var w = Val(ResW); var h = Val(ResH);
        if (w.HasValue && h.HasValue)
        {
            var native = sys.Gpus.FirstOrDefault()?.Resolution ?? "";
            list.Add(new GameSetting
            {
                Key = ResW, Label = "Resolution width", Group = "Display", Kind = "number",
                Description = "Lowering resolution is the single biggest performance gain available in any game." +
                              (native.Length > 0 ? " Your display runs at " + native + "." : ""),
                Current = w.Value.ToString(), Min = 640, Max = 7680, Impact = "high",
            });
            list.Add(new GameSetting
            {
                Key = ResH, Label = "Resolution height", Group = "Display", Kind = "number",
                Description = "Should match the aspect ratio of the width above.",
                Current = h.Value.ToString(), Min = 480, Max = 4320, Impact = "high",
            });
        }

        var mode = Val(Mode);
        if (mode.HasValue)
            list.Add(new GameSetting
            {
                Key = Mode, Label = "Display mode", Group = "Display", Kind = "choice",
                Description = "Exclusive fullscreen usually has the lowest input latency; borderless alt-tabs instantly.",
                Current = mode.Value.ToString(), Impact = "low",
                Options = new() {
                    new() { Value = "0", Label = "Exclusive fullscreen" },
                    new() { Value = "1", Label = "Borderless window" },
                    new() { Value = "2", Label = "Maximized window" },
                    new() { Value = "3", Label = "Windowed" },
                },
            });

        var q = Val(Quality);
        if (q.HasValue)
            list.Add(new GameSetting
            {
                Key = Quality, Label = "Graphics quality level", Group = "Quality", Kind = "number",
                Description = "Unity's overall quality tier. 0 is the lowest; what each level changes is up to the game.",
                Current = q.Value.ToString(), Min = 0, Max = 5, Impact = "high",
            });

        var nat = Val(UseNative);
        if (nat.HasValue)
            list.Add(new GameSetting
            {
                Key = UseNative, Label = "Use native resolution", Group = "Display", Kind = "choice",
                Description = "When on, the game ignores the width and height above and uses your desktop resolution. Turn it off to set a lower resolution for more FPS.",
                Current = nat.Value.ToString(), Impact = "medium",
                Options = new() { new() { Value = "0", Label = "Off" }, new() { Value = "1", Label = "On" } },
            });

        return list;
    }

    public string? Recommend(GameSetting s, Preset p, SystemInfo sys) => s.Key switch
    {
        Quality => p switch { Preset.MaxPerformance => "0", Preset.Balanced => "2", _ => "5" },
        Mode => "0",
        _ => null,   // never auto-change someone's resolution
    };

    public void Write(string path, Dictionary<string, string> values)
    {
        using var key = Open(path, true) ?? throw new InvalidOperationException("Could not open this game's settings key.");
        foreach (var (prefix, raw) in values)
        {
            var name = Resolve(key, prefix);
            if (name == null || !int.TryParse(raw, out var v)) continue;
            key.SetValue(name, v, RegistryValueKind.DWord);
        }
    }
}

// ===================================================================================
//  SOURCE ENGINE — cvars. config.cfg is rewritten by the game on exit, so changes go
//  into autoexec.cfg, which Source runs at startup and never overwrites. That is also
//  what the community has always done, so it won't surprise anyone.
// ===================================================================================
public class SourceEngineAdapter : IGameSettingsAdapter
{
    public string Name => "Source Engine";
    public string ApplyWarning => "Settings are written to autoexec.cfg, which the game reads at startup — restart the game for them to take effect.";

    private record Cvar(string Key, string Label, string Desc, string Impact, SettingOption[]? Opts, double? Min = null, double? Max = null);

    private static readonly Cvar[] Cvars =
    {
        new("fps_max", "Frame rate limit", "0 means unlimited. A cap keeps frame times even and reduces heat.", "medium", null, 0, 1000),
        new("mat_queue_mode", "Multicore rendering", "Spreads rendering across CPU cores. -1 lets the game decide; 2 forces it on and is usually the fastest.", "high",
            new SettingOption[] { new() { Value = "-1", Label = "Auto" }, new() { Value = "0", Label = "Off (single core)" }, new() { Value = "2", Label = "On (multi-core)" } }),
        new("mat_vsync", "V-Sync", "Stops tearing at the cost of input latency.", "medium",
            new SettingOption[] { new() { Value = "0", Label = "Off" }, new() { Value = "1", Label = "On" } }),
        new("r_shadows", "Shadows", "Turning shadows off is a reliable FPS gain in Source games.", "high",
            new SettingOption[] { new() { Value = "0", Label = "Off" }, new() { Value = "1", Label = "On" } }),
        new("mat_antialias", "Anti-aliasing", "Edge smoothing. 0 is off; higher values cost performance.", "medium",
            new SettingOption[] { new() { Value = "0", Label = "Off" }, new() { Value = "2", Label = "2x" }, new() { Value = "4", Label = "4x" }, new() { Value = "8", Label = "8x" } }),
        new("mat_forceaniso", "Anisotropic filtering", "Keeps angled textures sharp. Very cheap on modern GPUs.", "low",
            new SettingOption[] { new() { Value = "1", Label = "Off" }, new() { Value = "4", Label = "4x" }, new() { Value = "8", Label = "8x" }, new() { Value = "16", Label = "16x" } }),
        new("r_waterforceexpensive", "High quality water", "Reflective water. Noticeably expensive on older hardware.", "medium",
            new SettingOption[] { new() { Value = "0", Label = "Off" }, new() { Value = "1", Label = "On" } }),
    };

    public bool TryGetConfig(DetectedGame game, out string configPath)
    {
        configPath = "";
        if (game.InstallDir.Length == 0 || !Directory.Exists(game.InstallDir)) return false;
        // a Source game has a <mod>\cfg\config.cfg next to its root
        foreach (var dir in SafeDirs(game.InstallDir))
        {
            var cfg = Path.Combine(dir, "cfg", "config.cfg");
            if (File.Exists(cfg)) { configPath = Path.Combine(dir, "cfg"); return true; }
        }
        return false;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        string[] d;
        try { d = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var x in d) yield return x;
    }

    private static Dictionary<string, string> ReadCvars(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file)) return map;
        foreach (var line in File.ReadAllLines(file))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("//")) continue;
            var sp = t.Split(' ', 2);
            if (sp.Length != 2) continue;
            map[sp[0].Trim()] = sp[1].Trim().Trim('"');
        }
        return map;
    }

    public List<GameSetting> Read(string cfgDir, SystemInfo sys)
    {
        // autoexec runs last, so anything there is what actually applies
        var cfg = ReadCvars(Path.Combine(cfgDir, "config.cfg"));
        var auto = ReadCvars(Path.Combine(cfgDir, "autoexec.cfg"));

        var list = new List<GameSetting>();
        foreach (var c in Cvars)
        {
            var cur = auto.TryGetValue(c.Key, out var a) ? a
                    : cfg.TryGetValue(c.Key, out var b) ? b
                    : null;
            list.Add(new GameSetting
            {
                Key = c.Key, Label = c.Label, Description = c.Desc,
                Group = c.Key is "fps_max" or "mat_vsync" ? "Display" : "Quality",
                Kind = c.Opts == null ? "number" : "choice",
                Current = cur ?? "(game default)", Impact = c.Impact,
                Min = c.Min, Max = c.Max,
                Options = c.Opts?.ToList() ?? new List<SettingOption>(),
            });
        }
        return list;
    }

    public string? Recommend(GameSetting s, Preset p, SystemInfo sys) => s.Key switch
    {
        "mat_queue_mode" => "2",
        "mat_vsync" => "0",
        "r_shadows" => p == Preset.MaxPerformance ? "0" : "1",
        "mat_antialias" => p == Preset.MaxPerformance ? "0" : p == Preset.Balanced ? "2" : "8",
        "mat_forceaniso" => p == Preset.MaxPerformance ? "1" : "16",
        "r_waterforceexpensive" => p == Preset.MaxPerformance ? "0" : "1",
        _ => null,
    };

    public void Write(string cfgDir, Dictionary<string, string> values)
    {
        var file = Path.Combine(cfgDir, "autoexec.cfg");
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        foreach (var (k, v) in values)
        {
            var line = $"{k} \"{v}\"";
            int at = lines.FindIndex(l => l.TrimStart().StartsWith(k + " ", StringComparison.OrdinalIgnoreCase));
            if (at >= 0) lines[at] = line; else lines.Add(line);
        }
        if (!lines.Any(l => l.Contains("Tempo", StringComparison.OrdinalIgnoreCase)))
            lines.Insert(0, "// settings below were written by Tempo");
        File.WriteAllLines(file, lines);
    }
}

// ===================================================================================
//  UNREAL ENGINE 3 — [SystemSettings] in an .ini under My Games. Rocket League and
//  everything else of that generation. Values are True/False or small integers.
// ===================================================================================
public class UnrealEngine3Adapter : IGameSettingsAdapter
{
    public string Name => "Unreal Engine 3";
    public string ApplyWarning => "Close the game first — it rewrites this file on exit.";

    private static readonly SettingOption[] Bool =
        { new() { Value = "False", Label = "Off" }, new() { Value = "True", Label = "On" } };

    public bool TryGetConfig(DetectedGame game, out string configPath)
    {
        configPath = "";
        var myGames = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");
        if (!Directory.Exists(myGames)) return false;
        var key = Norm(game.Name);
        if (key.Length < 3) return false;

        foreach (var dir in Directory.GetDirectories(myGames))
        {
            var folder = Norm(Path.GetFileName(dir));
            if (!(folder.Contains(key) || key.Contains(folder))) continue;
            string[] inis;
            try { inis = Directory.GetFiles(dir, "*SystemSettings.ini", SearchOption.AllDirectories); } catch { continue; }
            foreach (var ini in inis)
            {
                if (new IniFile(ini).HasSection("SystemSettings")) { configPath = ini; return true; }
            }
        }
        return false;
    }

    private static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    public List<GameSetting> Read(string cfg, SystemInfo sys)
    {
        var ini = new IniFile(cfg);
        var list = new List<GameSetting>();

        void Add(string key, string label, string desc, string impact, SettingOption[]? opts, string group = "Quality", double? min = null, double? max = null)
        {
            var cur = ini.Get("SystemSettings", key);
            if (cur == null) return;
            list.Add(new GameSetting
            {
                Key = "SystemSettings/" + key, Label = label, Description = desc, Group = group,
                Kind = opts == null ? "number" : "choice", Current = cur, Impact = impact,
                Min = min, Max = max, Options = opts?.ToList() ?? new List<SettingOption>(),
            });
        }

        Add("DynamicShadows", "Dynamic shadows", "Moving shadows. Usually the most expensive single setting here.", "high", Bool);
        Add("LightEnvironmentShadows", "Character shadows", "Shadows cast by players and objects.", "medium", Bool);
        Add("DepthOfField", "Depth of field", "Background blur. Costs performance and can hurt visibility.", "medium", Bool);
        Add("Bloom", "Bloom", "Glow around bright areas.", "low", Bool);
        Add("MotionBlur", "Motion blur", "Blur while the camera moves. Many players turn it off for clarity.", "medium", Bool);
        Add("UseVsync", "V-Sync", "Stops tearing but adds input latency.", "medium", Bool, "Display");
        Add("Fullscreen", "Fullscreen", "Exclusive fullscreen generally has the lowest latency.", "low", Bool, "Display");
        Add("bAllowTemporalAA", "Temporal anti-aliasing", "Smooths edges across frames; can look slightly soft.", "medium", Bool);
        Add("bAllowPostprocessMLAA", "Post-process anti-aliasing", "Cheaper edge smoothing done after rendering.", "low", Bool);
        Add("MaxShadowResolution", "Shadow resolution", "Sharpness of shadows. Lower is noticeably faster.", "high", null, null, 256, 4096);
        Add("DetailMode", "Detail mode", "Overall level of world detail. 0 is lowest.", "high", new SettingOption[]
            { new() { Value = "0", Label = "Low" }, new() { Value = "1", Label = "Medium" }, new() { Value = "2", Label = "High" } });
        Add("ShadowFilterQualityBias", "Shadow filter quality", "Softness of shadow edges. Negative values are faster.", "medium", null, null, -2, 2);

        return list;
    }

    public string? Recommend(GameSetting s, Preset p, SystemInfo sys)
    {
        bool perf = p == Preset.MaxPerformance;
        bool quality = p == Preset.MaxQuality;
        return s.Key switch
        {
            "SystemSettings/DynamicShadows" => perf ? "False" : "True",
            "SystemSettings/LightEnvironmentShadows" => perf ? "False" : "True",
            "SystemSettings/DepthOfField" => quality ? "True" : "False",
            "SystemSettings/Bloom" => perf ? "False" : "True",
            "SystemSettings/MotionBlur" => quality ? "True" : "False",
            "SystemSettings/UseVsync" => "False",
            "SystemSettings/Fullscreen" => "True",
            "SystemSettings/bAllowTemporalAA" => perf ? "False" : "True",
            "SystemSettings/MaxShadowResolution" => perf ? "512" : quality ? "2048" : "1024",
            "SystemSettings/DetailMode" => perf ? "0" : quality ? "2" : "2",
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
