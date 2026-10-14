using Microsoft.Win32;
using Tempo.Core.Backup;
using Tempo.Core.Models;

namespace Tempo.Core.Optimizations;

public partial class OptimizationEngine
{
    private List<TweakDef> Defs()
    {
        var l = new List<TweakDef>();
        AddCore(l);    // gaming, fps, power, cpu, gpu, memory
        AddSystem(l);  // storage, network, input, visuals, background, maintenance, advanced
        return l;
    }

    private void AddCore(List<TweakDef> l)
    {
        // ======================= GAMING FEATURES =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "game-mode-on", Category = "gaming", Title = "Ensure Windows Game Mode is on",
                What = "Enables Windows Game Mode (the default, but sometimes turned off by old tweak guides).",
                Why = "Game Mode deprioritizes Windows Update and background activity while a game is focused. Modest but real, especially on 6-core CPUs.",
                Drawbacks = "Essentially none on modern Windows 11.",
                Benefit = "smoothness", Impact = "low", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 1u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AllowAutoGameMode", 1u, RegistryValueKind.DWord),
            },
            StateFor = _ => BackupService.RegistryEquals(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", 1u, RegistryValueKind.DWord) ? "Game Mode: on" : "Game Mode: off or unset"
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "game-dvr-off", Category = "gaming", Title = "Disable Xbox background recording (Game DVR)",
                What = "Turns off the Game Bar's always-on background gameplay recording.",
                Why = "Background recording continuously encodes video while you play — real CPU/GPU/VRAM cost when enabled. One of the few classic tweaks with a measurable effect.",
                Drawbacks = "Win+G instant replay ('record that') stops working; manual recording still works.",
                Benefit = "FPS", Impact = "medium", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "gamebar-nexus-off", Category = "gaming", Title = "Stop Xbox button opening Game Bar",
                What = "Stops the controller's Xbox/Guide button from popping the Game Bar overlay mid-game.",
                Why = "Prevents accidental overlay interruptions with a controller. No FPS change — pure quality of life.",
                Drawbacks = "You lose the controller shortcut to the overlay (Win+G still works).",
                Benefit = "quality of life", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0u, RegistryValueKind.DWord),
            }
        });

        // ======================= FPS & FRAME-TIME =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "windowed-opt", Category = "fps", Title = "Optimizations for windowed games",
                What = "Enables Windows 11's modern presentation path (flip model) for DirectX 10/11 games running windowed or borderless.",
                Why = "Microsoft-documented: lower latency and smoother frame delivery for borderless-windowed games — how most people play today.",
                Drawbacks = "Rare visual glitches in a handful of old titles (can be disabled per-game in Graphics settings).",
                Benefit = "smoothness", Impact = "medium", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "SwapEffectUpgradeEnable=1;", RegistryValueKind.String),
            },
            Relevant = si => int.TryParse(si.Os.Build, out var b) && b >= 22000,
            AppliedCheck = _ =>
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\DirectX\UserGpuPreferences");
                    return (k?.GetValue("DirectXUserGlobalSettings") as string ?? "").Contains("SwapEffectUpgradeEnable=1");
                }
                catch { return false; }
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "fse-opt", Category = "fps", Title = "Disable fullscreen optimizations globally",
                What = "Makes exclusive-fullscreen games bypass Windows' fullscreen-optimization layer.",
                Why = "Honest note: on Windows 11 this mostly matters for OLDER titles that stutter in 'fullscreen'. Modern games use borderless and ignore it. Try it if a specific old game stutters.",
                Drawbacks = "Slower alt-tab in affected games; no benefit for most modern titles.",
                Benefit = "smoothness", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", 2u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", 1u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_EFSEFeatureFlags", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "vrr-windowed", Category = "fps", Title = "Variable refresh rate for windowed games",
                What = "Lets G-Sync / FreeSync work in windowed and borderless games, not just exclusive fullscreen.",
                Why = "If you own a VRR monitor, this removes tearing and judder in borderless games — significant smoothness win.",
                Drawbacks = "Does nothing without a G-Sync/FreeSync display. Needs a restart of the game.",
                Benefit = "smoothness", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;", RegistryValueKind.String),
            },
            Relevant = si => int.TryParse(si.Os.Build, out var b) && b >= 19041,
            NoteFor = _ => "Only helps if your monitor supports G-Sync or FreeSync (check its spec sheet).",
            AppliedCheck = _ =>
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\DirectX\UserGpuPreferences");
                    return (k?.GetValue("DirectXUserGlobalSettings") as string ?? "").Contains("VRROptimizeEnable=1");
                }
                catch { return false; }
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "display-max-hz", Category = "fps", Title = "Set display to its maximum refresh rate",
                What = "Switches your monitor to the highest refresh rate it supports at the current resolution.",
                Why = "Monitors shipped at 60 Hz by default while supporting 144+ Hz is the single most common wasted-performance mistake. Biggest real 'free FPS feel' fix there is.",
                Drawbacks = "If the screen blanks or looks wrong, restore from History (the old rate is saved). Some TVs need specific HDMI ports/cables for high refresh.",
                Benefit = "smoothness", Impact = "high", Risk = "medium", NeedsReboot = false
            },
            Special = "display-max-hz",
            Relevant = _ => DisplayUtil.MaxRefreshAtCurrentResolution() > DisplayUtil.Current().hz + 1 || DisplayUtil.Current().hz > 0,
            StateFor = _ =>
            {
                var (w, h, hz) = DisplayUtil.Current();
                var max = DisplayUtil.MaxRefreshAtCurrentResolution();
                return w == 0 ? null : $"Now: {w}x{h} @ {hz} Hz · max supported: {max} Hz";
            },
            NoteFor = _ =>
            {
                var (_, _, hz) = DisplayUtil.Current();
                var max = DisplayUtil.MaxRefreshAtCurrentResolution();
                return max > hz + 1 ? $"Your display can run {max} Hz but is set to {hz} Hz — strongly recommended."
                                    : "Already at maximum — nothing to gain here.";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "mmcss-games", Category = "fps", Title = "Raise multimedia priority for games (MMCSS)",
                What = "Sets the 'Games' task profile in Windows' Multimedia Class Scheduler to higher GPU and CPU priority.",
                Why = "Honest note: evidence is mostly anecdotal — Windows already prioritizes the focused game. Harmless, and some players report steadier frame times in CPU-bound games.",
                Drawbacks = "Likely no measurable change on most systems.",
                Benefit = "smoothness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority", 8u, RegistryValueKind.DWord),
                new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority", 6u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-display", Category = "fps", Kind = "diagnostic", Title = "Check: is your monitor running at full speed?",
                What = "Read-only check comparing your current refresh rate against the maximum your display supports.",
                Why = "Finds the classic '144 Hz monitor stuck at 60 Hz' problem in one click.",
                Drawbacks = "None — reads settings only.",
                Benefit = "diagnostic", Impact = "high", Risk = "low"
            }
        });

        // ======================= POWER =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "power-plan", Category = "power", Title = "High Performance power plan",
                What = "Switches the active Windows power plan to High Performance (your current plan is saved for restore).",
                Why = "Stops aggressive CPU downclocking between frames. Helps frame-time consistency more than average FPS — matters most coming from Balanced/power-saver.",
                Drawbacks = "Higher idle power draw and heat; on laptops, noticeably less battery.",
                Benefit = "smoothness", Impact = "medium", Risk = "low", Recommended = true
            },
            Special = "powerplan",
            StateFor = si => "Active plan: " + si.ActivePowerPlan,
            NoteFor = si => si.IsLaptop ? "Laptop: expect reduced battery life on this plan." : null
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "ultimate-plan", Category = "power", Title = "Ultimate Performance power plan",
                What = "Activates Microsoft's hidden 'Ultimate Performance' plan (created if not present).",
                Why = "Slightly more aggressive than High Performance (no per-core idling micro-delays). Honest note: on desktops the difference vs High Performance is tiny — pick one of the two, not both.",
                Drawbacks = "Highest idle power use. Overkill for most; pointless on laptops on battery.",
                Benefit = "smoothness", Impact = "low", Risk = "low"
            },
            Special = "ultimate-plan",
            StateFor = si => "Active plan: " + si.ActivePowerPlan,
            NoteFor = si => si.IsLaptop ? "Not recommended on laptops." : "Alternative to High Performance — selecting both just ends on this one."
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "power-throttling-off", Category = "power", Title = "Disable Power Throttling",
                What = "Stops Windows from running background processes in a low-power CPU state.",
                Why = "Mainly benefits laptops where throttled background apps hitch when they wake. Small effect on desktops.",
                Drawbacks = "Slightly higher background power use; less battery on laptops.",
                Benefit = "smoothness", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1u, RegistryValueKind.DWord),
            },
            NoteFor = si => si.IsLaptop ? "Laptop detected: trades battery for consistency." : "Desktop detected: effect is minor."
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "usb-suspend-off", Category = "power", Title = "Disable USB selective suspend",
                What = "Stops Windows from powering down idle USB devices on the current power plan.",
                Why = "Fixes the class of bugs where a mouse/headset/controller lags or disconnects for a moment after idling. Prevention more than FPS.",
                Drawbacks = "Slightly higher power draw; on laptops, a bit less battery.",
                Benefit = "responsiveness", Impact = "situational", Risk = "low"
            },
            PowerCfg = { new(PowerCfg.SUB_USB, PowerCfg.USB_SELECTIVE_SUSPEND, 0) },
            StateFor = _ => EffectiveAc(PowerCfg.SUB_USB, PowerCfg.USB_SELECTIVE_SUSPEND) switch
            { 0 => "Currently: disabled", 1 => "Currently: enabled", _ => null }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "pcie-aspm-off", Category = "power", Title = "Disable PCI Express link power saving (ASPM)",
                What = "Sets PCIe Link State Power Management to Off on the current plan.",
                Why = "ASPM can add micro-latency when the GPU link wakes from low-power states. High Performance plans already set this — relevant if you stay on Balanced.",
                Drawbacks = "Slightly higher idle power. Battery cost on laptops.",
                Benefit = "smoothness", Impact = "low", Risk = "low"
            },
            PowerCfg = { new(PowerCfg.SUB_PCIE, PowerCfg.PCIE_ASPM, 0) },
            StateFor = _ => EffectiveAc(PowerCfg.SUB_PCIE, PowerCfg.PCIE_ASPM) switch
            { 0 => "Currently: off", 1 => "Currently: moderate savings", 2 => "Currently: maximum savings", _ => null }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "never-sleep-ac", Category = "power", Title = "Never sleep while plugged in",
                What = "Sets the sleep timeout to 'never' on AC power (display can still turn off).",
                Why = "Stops Windows from sleeping mid-download or during long AFK sessions. Zero performance change — convenience.",
                Drawbacks = "PC stays on until you sleep it yourself; higher idle energy use.",
                Benefit = "quality of life", Impact = "low", Risk = "low"
            },
            PowerCfg = { new(PowerCfg.SUB_SLEEP, PowerCfg.STANDBY_IDLE, 0) },
            StateFor = _ =>
            {
                var v = EffectiveAc(PowerCfg.SUB_SLEEP, PowerCfg.STANDBY_IDLE);
                return v == null ? null : v == 0 ? "Currently: never sleeps on AC" : "Currently: sleeps after " + (v / 60) + " min";
            }
        });

        // ======================= CPU =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "proc-min-100", Category = "cpu", Title = "Keep CPU at full clocks (min processor state 100%)",
                What = "Sets minimum processor state to 100% on your CURRENT power plan — the main thing High Performance does, without switching plans.",
                Why = "Removes clock ramp-up delay between frames. Use this OR the High Performance plan; doing both is redundant (harmless).",
                Drawbacks = "CPU idles hotter and uses more power all the time.",
                Benefit = "smoothness", Impact = "medium", Risk = "low"
            },
            PowerCfg = { new(PowerCfg.SUB_PROCESSOR, PowerCfg.PROC_MIN, 100) },
            StateFor = _ =>
            {
                var v = EffectiveAc(PowerCfg.SUB_PROCESSOR, PowerCfg.PROC_MIN);
                return v == null ? null : "Current minimum state: " + v + "%";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "core-parking-off", Category = "cpu", Title = "Disable CPU core parking",
                What = "Forces Windows to keep all CPU cores awake (minimum unparked cores = 100%).",
                Why = "Honest note: desktop Windows 10/11 already keeps cores unparked on most plans — this mostly matters on laptops and some OEM power profiles. Harmless to enforce.",
                Drawbacks = "Higher idle power. No change if cores were never parked.",
                Benefit = "smoothness", Impact = "situational", Risk = "low"
            },
            PowerCfg = { new(PowerCfg.SUB_PROCESSOR, PowerCfg.CORE_PARKING_MIN, 100, Hidden: true) },
            StateFor = _ =>
            {
                var v = EffectiveAc(PowerCfg.SUB_PROCESSOR, PowerCfg.CORE_PARKING_MIN);
                return v == null ? "Current: Windows default (usually no parking on desktops)" : "Minimum unparked cores: " + v + "%";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-top-memory", Category = "memory", Kind = "diagnostic", Title = "Check: what's eating RAM right now?",
                What = "Read-only snapshot of the 10 processes using the most memory.",
                Why = "Shows exactly what to close before a gaming session instead of guessing.",
                Drawbacks = "None — reads the process list only.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });

        // ======================= GPU =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "hags", Category = "gpu", Title = "Hardware-Accelerated GPU Scheduling",
                What = "Lets the GPU manage its own memory scheduling instead of the CPU (HAGS).",
                Why = "Slightly lower latency, and REQUIRED for NVIDIA DLSS Frame Generation. Recommended on GTX 10-series+/RX 5000+ with current drivers.",
                Drawbacks = "Needs a restart. A few games/overlays historically misbehaved — restore it if a specific game stutters.",
                Benefit = "smoothness", Impact = "situational", Risk = "low", NeedsReboot = true, Recommended = true
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2u, RegistryValueKind.DWord),
            },
            Relevant = si => si.Gpus.Any(g => g.Vendor is "NVIDIA" or "AMD"),
            StateFor = _ =>
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
                    return (k?.GetValue("HwSchMode") as int?) switch { 2 => "Currently: on", 1 => "Currently: off", _ => "Currently: driver default" };
                }
                catch { return null; }
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "gpu-pref-high", Category = "gpu", Title = "Default all apps to the high-performance GPU",
                What = "Sets the global Windows graphics preference so apps use the discrete GPU instead of integrated graphics.",
                Why = "On dual-GPU systems (laptops, or desktops with iGPU active), stops games accidentally launching on the slow integrated GPU.",
                Drawbacks = "Higher battery drain on laptops; per-app overrides still win.",
                Benefit = "FPS", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences", "GpuPreference", "GpuPreference=2;", RegistryValueKind.String),
            },
            Relevant = si => si.Gpus.Count >= 2,
            NoteFor = si => si.Gpus.Count >= 2 ? "Detected " + si.Gpus.Count + " GPUs: " + string.Join(" + ", si.Gpus.Select(g => g.Name)) : null
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-driver", Category = "gpu", Kind = "diagnostic", Title = "Check: GPU driver freshness",
                What = "Read-only check of your graphics driver version and age, with the official download link.",
                Why = "Driver updates carry real per-game optimizations — the most reliable 'tweak' in existence.",
                Drawbacks = "None — reads driver info only.",
                Benefit = "diagnostic", Impact = "high", Risk = "low"
            },
            Relevant = si => si.Gpus.Count > 0
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-gpu-msi", Category = "gpu", Kind = "diagnostic", Title = "Check: GPU interrupt mode (MSI)",
                What = "Read-only check whether your GPU uses modern Message-Signaled Interrupts.",
                Why = "Debunks/validates the 'MSI mode tool' tweak: modern drivers nearly always enable it already. This proves it instead of guessing.",
                Drawbacks = "None — reads the device configuration only.",
                Benefit = "diagnostic", Impact = "low", Risk = "low"
            },
            Relevant = si => si.Gpus.Count > 0
        });

        // ======================= MEMORY =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "sysmain-off", Category = "memory", Title = "Disable SysMain (Superfetch)",
                What = "Stops and disables the SysMain service, which preloads frequently-used apps into RAM.",
                Why = "On mechanical hard drives SysMain can cause heavy disk activity and stutter. On SSD/NVMe systems it is lightweight and usually worth KEEPING.",
                Drawbacks = "Slightly slower app launches. Not recommended on SSD-only systems.",
                Benefit = "smoothness", Impact = "situational", Risk = "medium"
            },
            Special = "sysmain-off",
            StateFor = _ => ServiceDisabled("SysMain") ? "Service: disabled" : "Service: running (Windows default)",
            NoteFor = si =>
            {
                var sys = si.Drives.FirstOrDefault(d => d.IsSystemDrive);
                return sys?.MediaType == "HDD"
                    ? "HDD system drive detected: this tweak is genuinely worth trying here."
                    : "SSD system drive detected: recommended to LEAVE SysMain on.";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "pagefile-managed", Category = "memory", Title = "Repair: let Windows manage the page file",
                What = "Re-enables system-managed page file sizing if a tweak guide set it tiny or off.",
                Why = "Disabled/tiny page files cause crashes and stutter in big games (even with 32 GB RAM, games commit more than they use). This REPAIRS a harmful tweak; it does nothing if already managed.",
                Drawbacks = "Page file uses some disk space — that's normal and healthy.",
                Benefit = "stability", Impact = "situational", Risk = "low", NeedsReboot = true
            },
            AppliedCheck = _ =>
            {
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("select AutomaticManagedPagefile from Win32_ComputerSystem");
                    foreach (System.Management.ManagementObject mo in s.Get())
                        return mo["AutomaticManagedPagefile"] as bool? ?? true;
                }
                catch { }
                return true;
            },
            StateFor = _ =>
            {
                try
                {
                    using var s = new System.Management.ManagementObjectSearcher("select AutomaticManagedPagefile from Win32_ComputerSystem");
                    foreach (System.Management.ManagementObject mo in s.Get())
                        return (mo["AutomaticManagedPagefile"] as bool? ?? true) ? "Page file: system-managed ✔" : "Page file: MANUALLY configured";
                }
                catch { }
                return null;
            },
            Registry =
            {
                // PagingFiles = "?:\pagefile.sys" (system managed token)
                new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles", @"?:\pagefile.sys", RegistryValueKind.MultiString),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "clearpagefile-off", Category = "memory", Title = "Repair: don't wipe the page file at shutdown",
                What = "Ensures 'clear page file at shutdown' is OFF (some privacy guides enable it).",
                Why = "When enabled, every shutdown takes minutes while Windows overwrites the page file. Repairs a common bad tweak; does nothing if already off.",
                Drawbacks = "None for home gaming PCs.",
                Benefit = "responsiveness", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "ClearPageFileAtShutdown", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-memory-pressure", Category = "memory", Kind = "diagnostic", Title = "Check: memory pressure",
                What = "Read-only check of RAM usage and commit charge against your installed memory.",
                Why = "Tells you whether stutter could be memory-related — and why RAM-cleaner apps would make it worse, not better.",
                Drawbacks = "None — reads counters only.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });
    }
}
