using System.Runtime.InteropServices;
using Tempo.Core.Logging;
using Tempo.Core.Models;

namespace Tempo.Core.Games;

/// <summary>
/// Per-game NVIDIA driver settings through NVAPI's Driver Settings Repository (DRS) —
/// the same documented interface the NVIDIA control panel and Profile Inspector use.
///
/// Setting IDs below come from NVIDIA's published NvApiDriverSettings.h. Anything not
/// listed here is deliberately left alone. If the struct layout ever disagrees with the
/// installed driver, NVAPI rejects the call and we report that instead of guessing.
/// </summary>
public unsafe class NvidiaDriverSettings
{
    // --- official DRS setting ids ---
    private const uint PREFERRED_PSTATE = 0x1057EB71;   // Power management mode
    private const uint PRERENDERLIMIT    = 0x007BA09E;  // Low latency (max pre-rendered frames)
    private const uint VSYNCMODE         = 0x1194F158;  // Vertical sync
    private const uint FRL_FPS           = 0x10835989;  // Max frame rate
    private const uint ANISO_MODE_LEVEL  = 0x6946FF9D;  // Anisotropic filtering level
    private const uint QUALITY_ENHANCE   = 0x00CE2691;  // Texture filtering quality

    private const uint VSYNC_FORCE_OFF = 0x08416747;
    private const uint VSYNC_FORCE_ON  = 0x47814940;
    private const uint VSYNC_APP       = 0x60925348;

    public bool Available { get; private set; }
    public string UnavailableReason { get; private set; } = "";

    public NvidiaDriverSettings()
    {
        try
        {
            if (LoadLibrary("nvapi64.dll") == IntPtr.Zero)
            { UnavailableReason = "nvapi64.dll isn't present — this PC has no NVIDIA driver installed."; return; }
            if (Init() != 0)
            { UnavailableReason = "The NVIDIA driver refused to initialise its settings interface."; return; }
            Available = true;
        }
        catch (Exception ex) { UnavailableReason = "NVIDIA interface unavailable: " + ex.Message; }
    }

    // ---------------- public API ----------------

    /// <param name="balance">0 = all performance, 100 = all quality.</param>
    public List<GameSetting> ReadForGame(DetectedGame game, int balance)
    {
        double t = Math.Clamp(balance, 0, 100) / 100.0;
        var list = new List<GameSetting>();
        var values = new Dictionary<uint, uint?>();
        WithSession(session =>
        {
            var profile = FindProfile(session, game);
            foreach (var id in new[] { PREFERRED_PSTATE, PRERENDERLIMIT, VSYNCMODE, FRL_FPS, ANISO_MODE_LEVEL, QUALITY_ENHANCE })
                values[id] = profile == IntPtr.Zero ? null : GetSetting(session, profile, id);
        });

        string Cur(uint id, string fallback) =>
            values.TryGetValue(id, out var v) && v.HasValue ? v.Value.ToString() : fallback;

        list.Add(new GameSetting
        {
            Key = "drv:" + PREFERRED_PSTATE, Label = "Power management mode", Source = "driver", Group = "Driver",
            Description = "Keeps the GPU at high clocks instead of dropping them between frames. Helps frame-time consistency; raises idle power and heat.",
            Kind = "choice", Current = Cur(PREFERRED_PSTATE, "0"), Impact = "medium",
            Options = new() {
                new() { Value = "0", Label = "Normal (driver decides)" },
                new() { Value = "1", Label = "Prefer maximum performance" },
                new() { Value = "3", Label = "Optimal power" },
            },
            Recommended = t >= 0.80 ? "0" : "1",   // highest clocks until the quality end
        });

        list.Add(new GameSetting
        {
            Key = "drv:" + PRERENDERLIMIT, Label = "Low latency mode", Source = "driver", Group = "Driver",
            Description = "Limits how many frames the CPU queues ahead of the GPU. Lower means less input lag, and slightly lower FPS when GPU-bound.",
            Kind = "choice", Current = Cur(PRERENDERLIMIT, "0"), Impact = "high",
            Options = new() {
                new() { Value = "0", Label = "Off (use the game's setting)" },
                new() { Value = "1", Label = "On (1 frame queued)" },
                new() { Value = "2", Label = "2 frames queued" },
            },
            Recommended = t >= 0.60 ? "0" : "1",   // give the game back control sooner
        });

        list.Add(new GameSetting
        {
            Key = "drv:" + VSYNCMODE, Label = "Vertical sync (driver)", Source = "driver", Group = "Driver",
            Description = "Forces V-Sync on or off regardless of the game's own setting. Forcing it off reduces latency but can cause tearing.",
            Kind = "choice", Current = Cur(VSYNCMODE, VSYNC_APP.ToString()), Impact = "medium",
            Options = new() {
                new() { Value = VSYNC_APP.ToString(), Label = "Use the game's setting" },
                new() { Value = VSYNC_FORCE_OFF.ToString(), Label = "Force off" },
                new() { Value = VSYNC_FORCE_ON.ToString(), Label = "Force on" },
            },
            Recommended = t >= 0.90 ? VSYNC_APP.ToString() : VSYNC_FORCE_OFF.ToString(),
        });

        // Verified against this driver: it rejects writes to this id through DRS, so it is
        // shown as read-only rather than as a control that silently does nothing.
        list.Add(new GameSetting
        {
            Key = "drv:" + FRL_FPS, Label = "Max frame rate (driver)", Source = "driver", Group = "Driver",
            Description = "Caps frames at the driver level. Your in-game frame rate limit does the same job — use that instead where the game offers one.",
            Kind = "number", Current = Cur(FRL_FPS, "0"), Min = 0, Max = 480, Impact = "medium",
            ReadOnly = true,
            ReadOnlyReason = "Your NVIDIA driver won't let other programs set this one. Use the NVIDIA app, or the game's own frame rate limit.",
        });

        list.Add(new GameSetting
        {
            Key = "drv:" + QUALITY_ENHANCE, Label = "Texture filtering quality", Source = "driver", Group = "Driver",
            Description = "Trades a little texture sharpness for speed. The difference is hard to see in motion.",
            Kind = "choice", Current = Cur(QUALITY_ENHANCE, "1"), Impact = "low",
            Options = new() {
                new() { Value = "0", Label = "High performance" },
                new() { Value = "1", Label = "Quality" },
                new() { Value = "2", Label = "High quality" },
            },
            Recommended = t <= 0.35 ? "0" : "1",   // only trade sharpness toward the performance end
        });

        list.Add(new GameSetting
        {
            Key = "drv:" + ANISO_MODE_LEVEL, Label = "Anisotropic filtering", Source = "driver", Group = "Driver",
            Description = "Keeps textures sharp at steep angles. Nearly free on modern GPUs.",
            Kind = "choice", Current = Cur(ANISO_MODE_LEVEL, "0"), Impact = "low",
            Options = new() {
                new() { Value = "0", Label = "Use the game's setting" },
                new() { Value = "4", Label = "4x" }, new() { Value = "8", Label = "8x" }, new() { Value = "16", Label = "16x" },
            },
            ReadOnly = true,
            ReadOnlyReason = "This driver only accepts this setting alongside a mode selector it doesn't expose to other programs. Set it in the NVIDIA app, or in the game — most games have their own texture filtering option.",
        });

        return list;
    }

    /// <summary>Writes the chosen driver settings into this game's own driver profile.</summary>
    public List<string> ApplyForGame(DetectedGame game, Dictionary<string, string> values, BackupSession session)
    {
        var applied = new List<string>();
        var failures = new List<string>();
        if (game.Exe.Length == 0)
            throw new InvalidOperationException("Tempo couldn't work out this game's executable, which the driver profile has to be attached to.");

        WithSession(s =>
        {
            var profile = FindProfile(s, game);
            if (profile == IntPtr.Zero) profile = CreateProfileFor(s, game);
            if (profile == IntPtr.Zero) throw new InvalidOperationException("Could not create a driver profile for this game.");

            foreach (var (key, raw) in values)
            {
                if (!key.StartsWith("drv:")) continue;
                if (!uint.TryParse(key[4..], out var id)) continue;
                if (!uint.TryParse(raw, out var val)) continue;
                if (id is FRL_FPS or ANISO_MODE_LEVEL)
                { failures.Add($"0x{id:X8}: this driver doesn't accept it from other programs"); continue; }

                var before = GetSetting(s, profile, id);
                var rc = SetSettingU32(s, profile, id, val);
                if (rc != 0)
                {
                    var why = Explain(rc);
                    Log.Write("nvapi", $"set 0x{id:X8} failed: {rc} ({why})");
                    failures.Add($"0x{id:X8}: {why}");
                    continue;
                }

                session.Changes.Add(new ChangeRecord
                {
                    TweakId = "nv-drs", Title = $"{game.Name} — driver setting 0x{id:X8}",
                    Type = "nvidia-drs", Target = game.Exe + "|" + id,
                    OldValue = before?.ToString(),     // null = was driver default
                    NewValue = val.ToString(), Restorable = true,
                });
                applied.Add($"driver 0x{id:X8} = {val}");
            }

            var saveRc = Save(s);
            if (saveRc != 0) throw new InvalidOperationException("The driver rejected saving the profile: " + Explain(saveRc));

            // Read every value back. Never tell someone a setting was applied without
            // confirming the driver actually kept it.
            foreach (var (key, raw) in values)
            {
                if (!key.StartsWith("drv:") || !uint.TryParse(key[4..], out var id)) continue;
                if (!uint.TryParse(raw, out var want)) continue;
                if (applied.All(a => !a.Contains($"0x{id:X8}"))) continue;
                var got = GetSetting(s, profile, id);
                if (got != want)
                {
                    applied.RemoveAll(a => a.Contains($"0x{id:X8}"));
                    failures.Add($"0x{id:X8}: the driver did not keep the new value");
                    Log.Write("nvapi", $"verify 0x{id:X8} failed: wanted {want}, read back {(got?.ToString() ?? "nothing")}");
                }
            }
        });
        // Never report success for settings the driver refused.
        if (failures.Count > 0 && applied.Count == 0)
            throw new InvalidOperationException("The NVIDIA driver refused these changes — " + string.Join("; ", failures));
        if (failures.Count > 0)
            applied.Add("(not applied: " + string.Join("; ", failures) + ")");
        return applied;
    }

    /// <summary>Restores one driver setting (used by History undo).</summary>
    public string? Restore(string target, string? oldValue)
    {
        try
        {
            var parts = target.Split('|');
            if (parts.Length != 2 || !uint.TryParse(parts[1], out var id)) return "Malformed driver change record.";
            var exe = parts[0];
            string? err = null;
            WithSession(s =>
            {
                var profile = FindProfileByExe(s, exe);
                if (profile == IntPtr.Zero) { err = "That game's driver profile no longer exists."; return; }
                if (string.IsNullOrEmpty(oldValue)) DeleteSetting(s, profile, id);   // back to driver default
                else if (uint.TryParse(oldValue, out var v)) SetSettingU32(s, profile, id, v);
                Save(s);
            });
            return err;
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ---------------- NVAPI plumbing ----------------

    private void WithSession(Action<IntPtr> body)
    {
        if (!Available) throw new InvalidOperationException(UnavailableReason);
        var create = Fn<DrsCreateSession>(0x0694D52E);
        var load = Fn<DrsLoadSettings>(0x375DBD6B);
        var destroy = Fn<DrsDestroySession>(0xDAD9CFF8);
        if (create(out var session) != 0) throw new InvalidOperationException("Could not open the NVIDIA settings session.");
        try
        {
            if (load(session) != 0) throw new InvalidOperationException("Could not load NVIDIA driver settings.");
            body(session);
        }
        finally { try { destroy(session); } catch { } }
    }

    private IntPtr FindProfile(IntPtr session, DetectedGame game)
    {
        if (game.Exe.Length > 0)
        {
            var p = FindProfileByExe(session, game.Exe);
            if (p != IntPtr.Zero) return p;
        }
        var byName = Fn<DrsFindProfileByName>(0x7E4A9A0B);
        return byName(session, Uni(game.Name), out var pr) == 0 ? pr : IntPtr.Zero;
    }

    private IntPtr FindProfileByExe(IntPtr session, string exePath)
    {
        try
        {
            var findApp = Fn<DrsFindApplicationByName>(0xEEE566B2);
            // struct below carries the V2 fields, so it must declare V2 — declaring V3
            // (which adds `flags`) made the driver reject every call with a size mismatch
            var app = new NVDRS_APPLICATION { version = AppVer };
            var name = Uni(Path.GetFileName(exePath));
            if (findApp(session, name, out var profile, ref app) == 0) return profile;
        }
        catch { }
        return IntPtr.Zero;
    }

    private IntPtr CreateProfileFor(IntPtr session, DetectedGame game)
    {
        var createProfile = Fn<DrsCreateProfile>(0xCC176068);
        var createApp = Fn<DrsCreateApplication>(0x4347A9DE);

        var info = new NVDRS_PROFILE { version = (uint)(sizeof(NVDRS_PROFILE) | (1 << 16)) };
        SetUni(info.profileName, "Tempo - " + game.Name);
        var prc = createProfile(session, ref info, out var profile);
        if (prc != 0) { Log.Write("nvapi", "CreateProfile failed: " + Explain(prc)); return IntPtr.Zero; }

        var app = new NVDRS_APPLICATION { version = AppVer, isPredefined = 0 };
        SetUni(app.appName, Path.GetFileName(game.Exe));
        SetUni(app.userFriendlyName, game.Name);
        var rc = createApp(session, profile, ref app);
        if (rc != 0)
        {
            // A profile with no application attached applies to nothing. Writing settings
            // into it would look like success while changing nothing at all, so stop here.
            Log.Write("nvapi", "CreateApplication failed: " + Explain(rc));
            throw new InvalidOperationException(
                "Couldn't attach " + Path.GetFileName(game.Exe) + " to a driver profile (" + Explain(rc) +
                "). No driver settings were changed.");
        }
        return profile;
    }

    private uint? GetSetting(IntPtr session, IntPtr profile, uint id)
    {
        try
        {
            var get = Fn<DrsGetSetting>(0x73BF8338);
            var s = new NVDRS_SETTING { version = (uint)(sizeof(NVDRS_SETTING) | (1 << 16)) };
            if (get(session, profile, id, ref s) != 0) return null;
            return s.currentValue;
        }
        catch { return null; }
    }

    private int SetSettingU32(IntPtr session, IntPtr profile, uint id, uint value)
    {
        var set = Fn<DrsSetSetting>(0x577DD202);
        var s = new NVDRS_SETTING
        {
            version = (uint)(sizeof(NVDRS_SETTING) | (1 << 16)),
            settingId = id,
            settingType = 0,              // NVDRS_DWORD_TYPE
            currentValue = value,
        };
        return set(session, profile, ref s);
    }

    private void DeleteSetting(IntPtr session, IntPtr profile, uint id)
    {
        try { Fn<DrsDeleteProfileSetting>(0xE4A26362)(session, profile, id); } catch { }
    }

    private int Save(IntPtr session) => Fn<DrsSaveSettings>(0xFCBC7E14)(session);

    private int Init() => Fn<NvInit>(0x0150E828)();

    /// <summary>Turns an NVAPI status code into something a person can act on.</summary>
    internal static string Explain(int code) => code switch
    {
        -6 => "the driver expects a different version of this request (NVAPI struct mismatch)",
        -1 => "the driver reported a general error",
        -5 => "the driver doesn't support this setting",
        -8 => "invalid argument",
        -11 => "no NVIDIA GPU found for this operation",
        -130 => "administrator rights are required to change driver settings",
        -163 => "that profile does not exist",
        -166 => "that setting is not present in the profile (it's at the driver default)",
        -168 => "the profile or setting name is already in use",
        _ => "NVAPI status " + code,
    };

    private static T Fn<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        if (p == IntPtr.Zero) throw new EntryPointNotFoundException($"This NVIDIA driver doesn't expose function 0x{id:X8}.");
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    /// <summary>NVDRS_APPLICATION as laid out below is the V2 shape.</summary>
    private static uint AppVer => (uint)(sizeof(NVDRS_APPLICATION) | (2 << 16));

    private static ushort[] Uni(string s)
    {
        var a = new ushort[2048];
        for (int i = 0; i < s.Length && i < 2047; i++) a[i] = s[i];
        return a;
    }

    private static void SetUni(ushort* dest, string s)
    {
        for (int i = 0; i < 2047; i++) dest[i] = i < s.Length ? s[i] : (ushort)0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string name);
    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvInit();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsCreateSession(out IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsLoadSettings(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsDestroySession(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsSaveSettings(IntPtr s);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsFindProfileByName(IntPtr s, [MarshalAs(UnmanagedType.LPArray, SizeConst = 2048)] ushort[] name, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsFindApplicationByName(IntPtr s, [MarshalAs(UnmanagedType.LPArray, SizeConst = 2048)] ushort[] name, out IntPtr profile, ref NVDRS_APPLICATION app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsCreateProfile(IntPtr s, ref NVDRS_PROFILE info, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsCreateApplication(IntPtr s, IntPtr profile, ref NVDRS_APPLICATION app);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsGetSetting(IntPtr s, IntPtr profile, uint id, ref NVDRS_SETTING setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsSetSetting(IntPtr s, IntPtr profile, ref NVDRS_SETTING setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DrsDeleteProfileSetting(IntPtr s, IntPtr profile, uint id);

    [StructLayout(LayoutKind.Sequential)]
    private struct NVDRS_PROFILE
    {
        public uint version;
        public fixed ushort profileName[2048];
        public uint gpuSupport;
        public uint isPredefined;
        public uint numOfApps;
        public uint numOfSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NVDRS_APPLICATION
    {
        public uint version;
        public uint isPredefined;
        public fixed ushort appName[2048];
        public fixed ushort userFriendlyName[2048];
        public fixed ushort launcher[2048];
        public fixed ushort fileInFolder[2048];
    }

    // The two trailing members are unions in NVAPI; only the DWORD form is used here and
    // the rest is reserved so the overall size still matches what the driver expects.
    [StructLayout(LayoutKind.Sequential)]
    private struct NVDRS_SETTING
    {
        public uint version;
        public fixed ushort settingName[2048];
        public uint settingId;
        public uint settingType;
        public uint settingLocation;
        public uint isCurrentPredefined;
        public uint isPredefinedValid;
        public uint predefinedValue;
        public fixed byte predefinedPad[4096];
        public uint currentValue;
        public fixed byte currentPad[4096];
    }
}
