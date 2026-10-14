using Microsoft.Win32;
using Tempo.Core.Models;

namespace Tempo.Core.Optimizations;

public partial class OptimizationEngine
{
    private static string Mb(long b) => b >= 1073741824 ? (b / 1073741824.0).ToString("0.0") + " GB" : (b / 1048576) + " MB";

    private void AddSystem(List<TweakDef> l)
    {
        // ======================= STORAGE =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "hibernate-off", Category = "storage", Title = "Disable hibernation (frees disk space)",
                What = "Turns hibernation off and deletes hiberfil.sys from the system drive.",
                Why = "Frees roughly 40% of your RAM size in disk space. No FPS change — a storage win for cramped SSDs.",
                Drawbacks = "Disables Hibernate AND Fast Startup; boots may be seconds slower. Laptops lose hibernate-on-low-battery.",
                Benefit = "storage", Impact = "situational", Risk = "medium"
            },
            Special = "hibernate-off",
            StateFor = _ => HiberfilExists() ? "hiberfil.sys present" : "Hibernation already off",
            NoteFor = si =>
            {
                var est = (long)(si.Ram.TotalBytes * 0.4);
                return HiberfilExists() ? "Estimated space freed: ~" + (est / 1073741824) + " GB" : null;
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "storage-sense-on", Category = "storage", Title = "Enable Storage Sense auto-cleanup",
                What = "Turns on Windows' built-in automatic temp-file cleanup.",
                Why = "Keeps temp files from accumulating without third-party cleaner apps. Set-and-forget.",
                Drawbacks = "Recycle Bin items older than 30 days get cleaned automatically (configurable in Settings).",
                Benefit = "storage", Impact = "low", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 1u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-clean-temp", Category = "storage", Kind = "action", Title = "Clean temporary files now",
                What = "Deletes files older than 24 h from the user and Windows temp folders. Locked/in-use files are skipped.",
                Why = "Reclaims disk space safely. Does not improve FPS — a full SSD (under ~10–15% free) is the only storage situation that hurts gaming.",
                Drawbacks = "Deleted temp files are gone (they are disposable by definition, but this cannot be undone).",
                Benefit = "storage", Impact = "low", Risk = "low", Reversible = false
            },
            StateFor = _ => "~" + Mb(MaintenanceService.MeasureDirs(MaintenanceService.TempDirs(), 24)) + " reclaimable now",
            Execute = (session, progress) =>
            {
                var (freed, deleted, skipped) = MaintenanceService.CleanDirs(MaintenanceService.TempDirs(), 24, progress);
                session.Changes.Add(new Models.ChangeRecord
                {
                    TweakId = "act-clean-temp", Title = "Cleaned temp files (" + Mb(freed) + ")", Type = "cleanup",
                    Target = "temp folders", OldValue = null, NewValue = Mb(freed) + " freed, " + deleted + " files", Restorable = false
                });
                return "Freed " + Mb(freed) + " (" + deleted + " files deleted, " + skipped + " in use/recent skipped).";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-empty-recycle", Category = "storage", Kind = "action", Title = "Empty the Recycle Bin",
                What = "Permanently deletes everything currently in the Recycle Bin.",
                Why = "Reclaims the space shown below. Storage only — no performance change.",
                Drawbacks = "PERMANENT. Make sure nothing in the bin is something you want back.",
                Benefit = "storage", Impact = "low", Risk = "medium", Reversible = false
            },
            StateFor = _ =>
            {
                var (size, items) = MaintenanceService.RecycleBinInfo();
                return items == 0 ? "Recycle Bin is empty" : Mb(size) + " in " + items + " item(s)";
            },
            Execute = (session, progress) =>
            {
                var (size, items) = MaintenanceService.RecycleBinInfo();
                if (items == 0) return "Recycle Bin was already empty.";
                var err = MaintenanceService.EmptyRecycleBin();
                if (err != null) throw new InvalidOperationException(err);
                session.Changes.Add(new Models.ChangeRecord
                {
                    TweakId = "act-empty-recycle", Title = "Emptied Recycle Bin (" + Mb(size) + ")", Type = "cleanup",
                    Target = "recycle bin", NewValue = Mb(size) + " freed", Restorable = false
                });
                return "Freed " + Mb(size) + " (" + items + " items).";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-clean-shader", Category = "storage", Kind = "action", Title = "Clear GPU shader caches (troubleshooting)",
                What = "Deletes DirectX/NVIDIA/AMD shader cache folders. They rebuild automatically as you play.",
                Why = "Fixes stutter caused by a CORRUPTED shader cache after driver updates. Honest note: don't run this routinely — rebuilding causes brief stutter the first time each game loads.",
                Drawbacks = "First launch of each game recompiles shaders (temporary stutter/longer load).",
                Benefit = "stability", Impact = "situational", Risk = "low", Reversible = false
            },
            StateFor = _ => Mb(MaintenanceService.MeasureDirs(MaintenanceService.ShaderCacheDirs())) + " in shader caches",
            Execute = (session, progress) =>
            {
                var (freed, deleted, _) = MaintenanceService.CleanDirs(MaintenanceService.ShaderCacheDirs(), 0, progress);
                session.Changes.Add(new Models.ChangeRecord
                {
                    TweakId = "act-clean-shader", Title = "Cleared shader caches (" + Mb(freed) + ")", Type = "cleanup",
                    Target = "shader caches", NewValue = Mb(freed) + " freed", Restorable = false
                });
                return "Cleared " + Mb(freed) + ". Games will rebuild caches on next launch.";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-trim", Category = "storage", Kind = "action", Title = "Run SSD TRIM now",
                What = "Runs Windows' drive optimizer (retrim) on the system SSD.",
                Why = "Keeps SSD write performance healthy. Honest note: Windows already does this weekly on a schedule — manual runs are for peace of mind after huge deletes.",
                Drawbacks = "None on SSDs; takes a minute.",
                Benefit = "stability", Impact = "low", Risk = "low", Reversible = false
            },
            Relevant = si => si.Drives.Any(d => d.IsSystemDrive && d.MediaType.Contains("SSD")),
            Execute = (session, progress) =>
            {
                var letter = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
                var result = MaintenanceService.TrimDrive(letter, progress);
                return "TRIM finished on " + letter + ". " + result;
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-space", Category = "storage", Kind = "diagnostic", Title = "Check: what's using disk space?",
                What = "Read-only breakdown of temp files, caches, Recycle Bin, hibernation/page files and leftovers.",
                Why = "See what's actually reclaimable before cleaning anything.",
                Drawbacks = "None — measures only.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-drive-wear", Category = "storage", Kind = "diagnostic", Title = "Check: drive health & wear",
                What = "Read-only SMART-based health, wear percentage and temperature of your drives (where the drive reports them).",
                Why = "A failing or worn drive causes stutter and load hitches no tweak can fix. Rule it out.",
                Drawbacks = "None — reads health counters only.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });

        // ======================= NETWORK =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "network-throttling", Category = "network", Title = "Disable multimedia network throttling",
                What = "Removes Windows' legacy cap (~10 packets/ms) during media playback and lowers background CPU reservation (SystemResponsiveness 20→10).",
                Why = "Honest note: designed for 2008-era PCs. Can marginally help packet pacing in fast online games; most players won't notice.",
                Drawbacks = "None known on modern hardware.",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 0xFFFFFFFFu, RegistryValueKind.DWord),
                new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness", 10u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "delivery-opt-off", Category = "network", Title = "Stop uploading Windows updates to other PCs",
                What = "Limits Delivery Optimization to direct downloads only (no peer-to-peer upload of updates).",
                Why = "Stops Windows from quietly using your upload bandwidth to seed updates to strangers — upload saturation is a real cause of in-game lag spikes.",
                Drawbacks = "None for you; updates download normally.",
                Benefit = "responsiveness", Impact = "low", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "autotuning-fix", Category = "network", Title = "Repair: TCP window auto-tuning to normal",
                What = "Sets TCP receive window auto-tuning back to 'normal' if an old optimizer disabled it.",
                Why = "Disabled auto-tuning CRIPPLES download speed on modern connections — a classic harmful tweak. This repairs it; does nothing if already normal.",
                Drawbacks = "None. 'Normal' is the Windows default.",
                Benefit = "stability", Impact = "situational", Risk = "low", Recommended = true
            },
            Special = "autotuning-fix",
            StateFor = _ => "Auto-tuning level: " + MaintenanceService.AutotuningLevel()
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "nagle-off", Category = "network", Title = "Disable Nagle's algorithm (legacy)",
                What = "Sets TcpAckFrequency=1 and TCPNoDelay=1 on all network interfaces.",
                Why = "Honest note: 95% myth today. Modern games use UDP or set TCP_NODELAY themselves. Might help one specific old TCP-based game. Included because it's harmless and famous.",
                Drawbacks = "Slightly more TCP overhead; no effect on most games.",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            // registry writes are generated at apply time (one per interface) via Special
            Special = "nagle-off",
            AppliedCheck = _ => NagleDisabledEverywhere(),
            StateFor = _ => NagleDisabledEverywhere() ? "Currently: disabled on all interfaces" : "Currently: Windows default (enabled)"
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-flush-dns", Category = "network", Kind = "action", Title = "Flush the DNS cache",
                What = "Clears Windows' DNS resolver cache (ipconfig /flushdns).",
                Why = "Fixes 'server not found' and stale-address issues after network changes. Troubleshooting, not a speed boost.",
                Drawbacks = "None; cache refills automatically.",
                Benefit = "stability", Impact = "low", Risk = "low", Reversible = false
            },
            Execute = (session, progress) => { MaintenanceService.FlushDns(); return "DNS cache flushed."; }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-net-latency", Category = "network", Kind = "diagnostic", Title = "Check: internet latency & packet loss",
                What = "Read-only ping test (6 pings each to Cloudflare and Google) measuring latency, jitter and loss.",
                Why = "Separates 'my internet is the problem' from 'the game server is the problem' with data.",
                Drawbacks = "Sends a few pings — takes ~10 seconds.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-dns-speed", Category = "network", Kind = "diagnostic", Title = "Check: DNS speed comparison",
                What = "Read-only benchmark of your current DNS server vs Cloudflare (1.1.1.1) and Google (8.8.8.8).",
                Why = "Shows whether switching DNS would actually help you — instead of blindly changing it. DNS affects connection START times, never in-match ping.",
                Drawbacks = "None — sends a few lookups.",
                Benefit = "diagnostic", Impact = "low", Risk = "low"
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-net-config", Category = "network", Kind = "diagnostic", Title = "Check: network configuration",
                What = "Read-only check: Ethernet vs Wi-Fi, link speed, and TCP auto-tuning state.",
                Why = "Wi-Fi vs cable matters more than every software tweak combined — this makes it visible.",
                Drawbacks = "None.",
                Benefit = "diagnostic", Impact = "high", Risk = "low"
            }
        });

        // ======================= INPUT =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "mouse-accel-off", Category = "input", Title = "Disable mouse acceleration (Enhance Pointer Precision)",
                What = "Turns off Windows' pointer acceleration so the same hand movement always moves the cursor the same distance.",
                Why = "Consistent aim muscle memory — standard practice for shooter players. Applies instantly.",
                Drawbacks = "Changes mouse feel everywhere; your aim may feel off for a few days if you're used to acceleration.",
                Benefit = "responsiveness", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", "0", RegistryValueKind.String),
                new(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", "0", RegistryValueKind.String),
                new(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", "0", RegistryValueKind.String),
            },
            Special = "mouse-accel"
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "keyboard-fast", Category = "input", Title = "Fastest keyboard repeat rate",
                What = "Sets key repeat delay to shortest and repeat rate to fastest (like Control Panel → Keyboard, maxed).",
                Why = "Snappier text editing and menu navigation. No effect in games (they read keys directly).",
                Drawbacks = "Held keys repeat very fast in text fields — some people find it twitchy.",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardDelay", "0", RegistryValueKind.String),
                new(RegistryHive.CurrentUser, @"Control Panel\Keyboard", "KeyboardSpeed", "31", RegistryValueKind.String),
            },
            Special = "keyboard-speed"
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "access-hotkeys-off", Category = "input", Title = "Disable Sticky/Filter Keys hotkeys",
                What = "Stops the Sticky Keys popup (Shift ×5) and Filter Keys shortcut (Shift held 8 s) from triggering.",
                Why = "Pressing Shift five times mid-firefight will never again yank you out of the game. Classic gamer fix.",
                Drawbacks = "If you USE these accessibility features, keep them on — this only disables the accidental hotkey activation.",
                Benefit = "quality of life", Impact = "low", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", "506", RegistryValueKind.String),
                new(RegistryHive.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags", "122", RegistryValueKind.String),
                new(RegistryHive.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", "58", RegistryValueKind.String),
            }
        });

        // ======================= VISUALS =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "reduce-animations", Category = "visuals", Title = "Reduce Windows animations",
                What = "Turns off window minimize/maximize and taskbar animations.",
                Why = "Desktop feels snappier; tiny GPU savings when alt-tabbing. Does not change in-game FPS.",
                Drawbacks = "Windows looks less fluid — cosmetic preference.",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", RegistryValueKind.String),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "transparency-off", Category = "visuals", Title = "Disable transparency effects",
                What = "Turns off the acrylic/blur transparency in the taskbar, Start menu and windows.",
                Why = "Transparency costs a little GPU on the desktop; off = marginally snappier UI, especially on weaker GPUs.",
                Drawbacks = "Flatter look.",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "menu-instant", Category = "visuals", Title = "Instant context menus",
                What = "Removes the artificial 400 ms delay before menus open (MenuShowDelay = 0).",
                Why = "Desktop responsiveness only — menus pop instantly. Zero effect on games.",
                Drawbacks = "Cascading menus can open 'eagerly' when the cursor brushes them.",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "0", RegistryValueKind.String),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "visualfx-perf", Category = "visuals", Title = "Visual effects: best performance preset",
                What = "Sets Windows' 'Adjust for best performance' preset (disables shadows, animations, smooth scrolling — everything).",
                Why = "The nuclear option for weak/older PCs or iGPU systems. On a modern gaming PC the gain is negligible — prefer the lighter options above.",
                Drawbacks = "Windows looks noticeably dated. Font smoothing may need re-enabling manually for comfort.",
                Benefit = "responsiveness", Impact = "situational", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2u, RegistryValueKind.DWord),
            },
            NoteFor = si => si.Gpus.Any(g => g.Vendor is "NVIDIA" or "AMD" && g.VramBytes > 4L * 1073741824)
                ? "You have a capable GPU — this preset won't gain you anything measurable." : null
        });

        // ======================= BACKGROUND & PRIVACY =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "content-suggestions-off", Category = "background", Title = "Stop suggested apps & content",
                What = "Stops Windows from silently installing suggested Store apps and showing ads in Start/Settings.",
                Why = "Prevents sponsored bloatware from reappearing after cleanup; removes background content delivery.",
                Drawbacks = "You lose Microsoft's app suggestions (most consider that a win).",
                Benefit = "background", Impact = "low", Risk = "low", Recommended = true
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SilentInstalledAppsEnabled", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SystemPaneSuggestionsEnabled", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338388Enabled", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-338389Enabled", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", "SubscribedContent-310093Enabled", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "background-apps-off", Category = "background", Title = "Stop Store apps running in the background",
                What = "Globally prevents Microsoft Store apps from running background tasks (Win11 per-user policy).",
                Why = "Fewer background wakeups from apps you never asked to run. Small but real on busy systems.",
                Drawbacks = "Store apps won't update live tiles/notifications until opened (mail apps, clocks). Exceptions can't be set per-app while the global switch is on.",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "widgets-off", Category = "background", Title = "Hide Widgets (news feed) from the taskbar",
                What = "Removes the Widgets button so the news/weather feed process stays dormant.",
                Why = "The widgets board pulls news content in the background once poked. Hiding it keeps that quiet.",
                Drawbacks = "No weather/news at a glance. Re-enable anytime in taskbar settings.",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 0u, RegistryValueKind.DWord),
            },
            Relevant = si => int.TryParse(si.Os.Build, out var b) && b >= 22000
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "copilot-button-off", Category = "background", Title = "Hide the Copilot taskbar button",
                What = "Removes the Copilot button from the taskbar.",
                Why = "Declutters; prevents accidental launches. The app itself stays installed (remove it in the Bloatware tab if unwanted).",
                Drawbacks = "None — Win+C and the Store app still work.",
                Benefit = "quality of life", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowCopilotButton", 0u, RegistryValueKind.DWord),
            },
            Relevant = si => int.TryParse(si.Os.Build, out var b) && b >= 22621
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "search-web-off", Category = "background", Title = "Local-only Start menu search",
                What = "Stops Start menu search from querying Bing — results come from your PC only.",
                Why = "Search gets faster and stops sending keystrokes to the web. Clear responsiveness win for app launching.",
                Drawbacks = "No web answers inside Start search (your browser does that better anyway).",
                Benefit = "responsiveness", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "search-highlights-off", Category = "background", Title = "Disable search highlights",
                What = "Turns off the rotating Bing content/illustrations in the search box.",
                Why = "Less background content fetching, cleaner search.",
                Drawbacks = "None.",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "advertising-id-off", Category = "background", Title = "Disable advertising ID",
                What = "Turns off the per-user advertising identifier apps use to profile you.",
                Why = "Privacy win; trivially less background activity. No performance change.",
                Drawbacks = "Ads become less 'personalized' (arguably a feature).",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "activity-history-off", Category = "background", Title = "Stop activity history collection",
                What = "Stops Windows from recording your app/document timeline activity.",
                Why = "Less background bookkeeping and sync chatter.",
                Drawbacks = "No cross-device timeline (feature was retired anyway).",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "feedback-off", Category = "background", Title = "Stop feedback requests",
                What = "Stops Windows from periodically asking for feedback.",
                Why = "One less background scheduled nag.",
                Drawbacks = "None.",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0u, RegistryValueKind.DWord),
                new(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Siuf\Rules", "PeriodInNanoSeconds", 0u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "telemetry-min", Category = "background", Title = "Set diagnostic data to minimum",
                What = "Sets Windows diagnostic data collection to the lowest level your edition allows (Required only).",
                Why = "Reduces background data collection/upload. Small performance effect; mostly a privacy/background win.",
                Drawbacks = "Windows Insider features and some troubleshooting diagnostics rely on optional data.",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 1u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-services", Category = "background", Kind = "diagnostic", Title = "Check: third-party auto-start services",
                What = "Read-only list of non-Windows services that start automatically with the PC.",
                Why = "Shows what's living in the background. Manage via each app's own settings or the Bloatware tab — not by force-disabling services.",
                Drawbacks = "None.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });

        // ======================= MAINTENANCE =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "fast-startup-off", Category = "maintenance", Title = "Disable Fast Startup",
                What = "Makes 'Shut down' perform a true full shutdown instead of a partial hibernate.",
                Why = "Fast Startup can carry driver glitches across 'reboots' — the reason 'turn it off and on again' sometimes doesn't fix things. True shutdowns start fresh.",
                Drawbacks = "Boot takes a few seconds longer on HDDs; barely noticeable on SSDs.",
                Benefit = "stability", Impact = "low", Risk = "low"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled", 0u, RegistryValueKind.DWord),
            },
            AppliedCheck = _ => !HiberfilExists() ? (bool?)true : null,
            NoteFor = _ => !HiberfilExists() ? "Hibernation is off on this PC, so Fast Startup is already inactive." : null
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "fax-off", Category = "maintenance", Title = "Disable the Fax service",
                What = "Disables the Windows Fax service (if present).",
                Why = "It's 2026. If you fax, you know who you are.",
                Drawbacks = "None unless you fax from this PC.",
                Benefit = "background", Impact = "low", Risk = "low"
            },
            Special = "fax-off",
            Relevant = _ => ServiceExists("Fax"),
            StateFor = _ => ServiceDisabled("Fax") ? "Service: disabled" : "Service: present"
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-restore-point", Category = "maintenance", Kind = "action", Title = "Create a System Restore point",
                What = "Creates a Windows restore point capturing system files, registry and drivers (not personal files).",
                Why = "A safety net before bigger changes. Honest note: restore points don't undo app uninstalls reliably — they protect Windows itself.",
                Drawbacks = "Needs System Protection enabled; Windows skips creation if one was made in the last 24 h.",
                Benefit = "stability", Impact = "low", Risk = "low", Reversible = false
            },
            Execute = (session, progress) =>
            {
                progress?.Invoke("Creating restore point (can take ~30 s)…");
                var err = MaintenanceService.CreateRestorePoint("Tempo — manual restore point");
                if (err != null) throw new InvalidOperationException(err);
                return "Restore point created.";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-sfc", Category = "maintenance", Kind = "action", Title = "Scan & repair system files (SFC)",
                What = "Runs Windows' System File Checker to find and repair corrupted system files.",
                Why = "Corrupted system files cause crashes and weird stutter no tweak can fix. Run when the system misbehaves.",
                Drawbacks = "Takes 5–20 minutes. Leave Tempo open while it runs.",
                Benefit = "stability", Impact = "situational", Risk = "low", Reversible = false
            },
            Execute = (session, progress) => MaintenanceService.SfcScan(progress)
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-updates", Category = "maintenance", Kind = "diagnostic", Title = "Check: Windows Update & pending restart",
                What = "Read-only check for pending-restart state and last successful update.",
                Why = "A half-installed update waiting for restart is a classic hidden cause of stutter.",
                Drawbacks = "None.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });

        // ======================= ADVANCED =======================
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "mpo-off", Category = "advanced", Title = "Disable Multi-Plane Overlay (MPO)",
                What = "Disables the display engine's overlay plane feature via the documented override key.",
                Why = "Fixes flicker/stutter on SOME monitor+GPU combos (NVIDIA once officially recommended it). Only worth trying if you SEE desktop flicker or game stutter you can't explain.",
                Drawbacks = "Can increase power draw and disable some efficiency paths. Restore it if nothing improves.",
                Benefit = "stability", Impact = "situational", Risk = "medium", NeedsReboot = true
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5u, RegistryValueKind.DWord),
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "win32-priority", Category = "advanced", Title = "Foreground CPU priority boost (Win32PrioritySeparation)",
                What = "Sets the scheduler to give longer CPU slices to the foreground app (0x26).",
                Why = "Honest note: heavily debated, evidence anecdotal. Some competitive players swear by it; benchmarks rarely reproduce gains. Harmless and fully reversible.",
                Drawbacks = "Background tasks (downloads, encodes) get less CPU while gaming.",
                Benefit = "smoothness", Impact = "low", Risk = "medium"
            },
            Registry =
            {
                new(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26u, RegistryValueKind.DWord),
            },
            StateFor = _ =>
            {
                try
                {
                    using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\PriorityControl");
                    var v = k?.GetValue("Win32PrioritySeparation") as int?;
                    return v == null ? null : "Current value: 0x" + v.Value.ToString("X") + (v == 2 ? " (Windows default)" : "");
                }
                catch { return null; }
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "act-winsock-reset", Category = "advanced", Kind = "action", Title = "Reset the network stack (Winsock)",
                What = "Runs 'netsh winsock reset' — returns the network stack to a clean state.",
                Why = "Last-resort repair for broken networking caused by VPNs, old 'optimizers' or malware. Don't run it when everything works.",
                Drawbacks = "Needs a restart. Third-party VPN/firewall layers may need reinstalling. Not reversible.",
                Benefit = "stability", Impact = "situational", Risk = "high", Reversible = false, NeedsReboot = true
            },
            Execute = (session, progress) =>
            {
                var o = MaintenanceService.WinsockReset();
                session.Changes.Add(new Models.ChangeRecord
                {
                    TweakId = "act-winsock-reset", Title = "Winsock reset executed", Type = "netsh",
                    Target = "winsock", NewValue = "reset", Restorable = false
                });
                return o.Length > 0 ? o : "Winsock reset. Restart the PC to finish.";
            }
        });
        l.Add(new TweakDef
        {
            Meta = new OptimizationItem
            {
                Id = "diag-vbs", Category = "advanced", Kind = "diagnostic", Title = "Check: Memory Integrity (VBS) FPS tradeoff",
                What = "Read-only check whether Virtualization-Based Security / Memory Integrity is active.",
                Why = "VBS costs ~3–8% FPS in some games but is a real security feature. Tempo reports it and lets YOU decide in Windows Security — no toggle here by design.",
                Drawbacks = "None — reads status only.",
                Benefit = "diagnostic", Impact = "medium", Risk = "low"
            }
        });
    }

    // ---- helpers used by catalog ----

    internal static bool NagleDisabledEverywhere()
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
            if (root == null) return false;
            bool anyIp = false;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;
                bool hasIp = (k.GetValue("DhcpIPAddress") as string ?? (k.GetValue("IPAddress") as string[])?.FirstOrDefault() ?? "") is { Length: > 0 } ip && ip != "0.0.0.0";
                if (!hasIp) continue;
                anyIp = true;
                if ((k.GetValue("TcpAckFrequency") as int?) != 1 || (k.GetValue("TCPNoDelay") as int?) != 1) return false;
            }
            return anyIp;
        }
        catch { return false; }
    }
}
