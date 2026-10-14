using System.Diagnostics;
using Microsoft.Win32;
using Tempo.Core.Models;
using Windows.Management.Deployment;

namespace Tempo.Core.Bloatware;

/// <summary>
/// Finds removable apps (Microsoft Store packages + classic Win32 programs),
/// classifies them against a curated safety catalog, and uninstalls only via
/// official methods. Essential Windows components, drivers, and runtimes are
/// excluded from the list entirely — they are never offered for removal.
/// </summary>
public class BloatwareService
{
    private record StoreMeta(string Friendly, string Desc, string Why, string Safety, string SafetyNote, bool Preselect);

    // Curated Microsoft Store bloatware catalog. Safety: safe = no system impact,
    // optional = useful to some people, risky = removing affects a real feature.
    private static readonly Dictionary<string, StoreMeta> StoreCatalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.BingNews"] = new("Microsoft News", "News feed app.", "Duplicates any browser news site; runs background content updates.", "safe", "No system impact. ", false),
        ["Microsoft.BingWeather"] = new("MSN Weather", "Weather app.", "Only needed if you use it; widgets have their own weather.", "safe", "No system impact.", false),
        ["Microsoft.BingSearch"] = new("Bing Search", "Web search app integration.", "Duplicates browser search.", "safe", "No system impact.", false),
        ["Microsoft.GetHelp"] = new("Get Help", "Microsoft support chat app.", "Only used when contacting Microsoft support.", "optional", "Microsoft's guided support won't open until reinstalled.", false),
        ["Microsoft.Getstarted"] = new("Tips", "Windows tips/welcome app.", "Tutorial content most users never open.", "safe", "No system impact.", false),
        ["Microsoft.WindowsFeedbackHub"] = new("Feedback Hub", "Sends feedback/bug reports to Microsoft.", "Only needed to file Windows feedback.", "safe", "No system impact.", false),
        ["Microsoft.MicrosoftSolitaireCollection"] = new("Solitaire Collection", "Card games with ads.", "Game with ads; reinstallable anytime.", "safe", "No system impact.", false),
        ["Microsoft.MicrosoftOfficeHub"] = new("Microsoft 365 (Office hub)", "Shortcut/ad app for Microsoft 365.", "It's a storefront for Office, not Office itself.", "safe", "Does not affect installed Office apps.", false),
        ["Microsoft.OutlookForWindows"] = new("Outlook (new)", "New Outlook mail client.", "Unneeded if you use another mail app.", "optional", "Your mail stays on the server; app is reinstallable.", false),
        ["Microsoft.People"] = new("People", "Contacts app.", "Legacy app, rarely used.", "optional", "Contact data lives in your Microsoft account, not this app.", false),
        ["Microsoft.ToDo"] = new("Microsoft To Do", "Task list app.", "Unneeded if you don't use To Do.", "optional", "Tasks stay in your Microsoft account.", false),
        ["Microsoft.WindowsMaps"] = new("Maps", "Offline/online maps.", "Most people use phone or browser maps.", "safe", "No system impact.", false),
        ["Microsoft.WindowsSoundRecorder"] = new("Sound Recorder", "Audio recording app.", "Unneeded if you never record audio with it.", "optional", "No system impact.", false),
        ["Microsoft.ZuneMusic"] = new("Media Player (Zune Music)", "Music player.", "Unneeded if you use Spotify/other players.", "optional", "Local music files are not deleted.", false),
        ["Microsoft.ZuneVideo"] = new("Movies & TV", "Video store/player.", "Unneeded if you use VLC/streaming.", "optional", "Purchased content remains in your Microsoft account.", false),
        ["Microsoft.MicrosoftStickyNotes"] = new("Sticky Notes", "Desktop notes.", "Unneeded if you don't use sticky notes.", "optional", "Notes sync to your Microsoft account; local-only notes are lost.", false),
        ["Microsoft.Todos"] = new("Microsoft To Do", "Task list app.", "Unneeded if you don't use To Do.", "optional", "Tasks stay in your Microsoft account.", false),
        ["MicrosoftTeams"] = new("Teams (personal)", "Consumer Teams chat.", "Separate from work Teams; most gamers never use it.", "safe", "Does not affect Teams for Work/School.", false),
        ["MSTeams"] = new("Microsoft Teams", "Teams chat app.", "Unneeded if you don't use Teams.", "optional", "Reinstallable from the Store.", false),
        ["Microsoft.SkypeApp"] = new("Skype", "Legacy video calls.", "Replaced by Teams; service retired in 2025.", "safe", "No system impact.", false),
        ["Microsoft.Microsoft3DViewer"] = new("3D Viewer", "Views 3D models.", "Niche tool most users never open.", "safe", "No system impact.", false),
        ["Microsoft.MixedReality.Portal"] = new("Mixed Reality Portal", "Windows MR headset portal.", "Only needed for Windows Mixed Reality headsets.", "safe", "Removing breaks WMR headsets (if you had one).", false),
        ["Microsoft.Wallet"] = new("Wallet", "Legacy payment app.", "Discontinued service.", "safe", "No system impact.", false),
        ["Microsoft.YourPhone"] = new("Phone Link", "Pairs your Android/iPhone with the PC.", "Unneeded if you don't link your phone.", "optional", "Phone notifications/messages on PC stop working.", false),
        ["Microsoft.WindowsAlarms"] = new("Clock", "Alarms/timers.", "Unneeded if you never set PC alarms.", "optional", "Focus sessions feature is also removed.", false),
        ["Microsoft.PowerAutomateDesktop"] = new("Power Automate", "Desktop automation tool.", "Business automation tool preinstalled for everyone.", "safe", "Only matters if you build automation flows.", false),
        ["Clipchamp.Clipchamp"] = new("Clipchamp", "Microsoft's video editor.", "Unneeded if you don't edit video (or use another editor).", "optional", "Projects are cloud-saved; app is reinstallable.", false),
        ["Microsoft.Copilot"] = new("Copilot", "Windows AI assistant app.", "Unneeded if you don't use Copilot.", "optional", "The Copilot key/taskbar button stops opening it.", false),
        ["Microsoft.549981C3F5F10"] = new("Cortana (legacy)", "Retired voice assistant.", "Service discontinued by Microsoft.", "safe", "No system impact on current Windows.", false),
        ["king.com.CandyCrushSaga"] = new("Candy Crush Saga", "Advertised game.", "Sponsored content.", "safe", "No system impact.", true),
        ["king.com.CandyCrushSodaSaga"] = new("Candy Crush Soda", "Advertised game.", "Sponsored content.", "safe", "No system impact.", true),
        ["SpotifyAB.SpotifyMusic"] = new("Spotify (Store)", "Music streaming.", "Often preinstalled as a sponsored tile; keep if you actually use Spotify.", "optional", "No system impact.", false),
        ["BytedancePte.Ltd.TikTok"] = new("TikTok", "Short video app.", "Sponsored preinstall on many systems.", "safe", "No system impact.", false),
        ["Facebook.InstagramBeta"] = new("Instagram", "Social app.", "Sponsored preinstall.", "safe", "No system impact.", false),
        ["FACEBOOK.FACEBOOK"] = new("Facebook", "Social app.", "Sponsored preinstall.", "safe", "No system impact.", false),
        ["5A894077.McAfeeSecurity"] = new("McAfee Security (Store)", "Antivirus trial.", "Trial antivirus; Windows Security (Defender) protects you for free.", "optional", "Make sure Windows Security turns back on after removal (it does automatically).", false),
        ["Microsoft.Xbox.TCUI"] = new("Xbox TCUI", "Xbox social/overlay plumbing.", "", "risky", "Needed by Game Pass titles and Xbox sign-in in many games. Keep unless you never touch Xbox/Game Pass.", false),
        ["Microsoft.XboxApp"] = new("Xbox Console Companion (old)", "Legacy Xbox app.", "Replaced by the new Xbox app.", "optional", "The NEW Xbox app and Game Bar are separate and unaffected.", false),
        ["Microsoft.GamingApp"] = new("Xbox app", "Game Pass / Xbox store app.", "", "risky", "Required for PC Game Pass installs. Keep if you use Game Pass.", false),
        ["Microsoft.OneConnect"] = new("Mobile Plans", "Cellular data plans app.", "Only for PCs with SIM slots.", "safe", "No system impact on desktops.", false),
        ["Microsoft.Print3D"] = new("Print 3D", "Legacy 3D printing app.", "Niche legacy tool.", "safe", "No system impact.", false),
        ["Microsoft.Office.OneNote"] = new("OneNote for Windows 10 (legacy)", "Old OneNote app.", "Superseded by desktop OneNote.", "optional", "Notes are in your account; desktop OneNote unaffected.", false),
        ["AmazonVideo.PrimeVideo"] = new("Prime Video", "Streaming app.", "Sponsored preinstall; the website does the same thing.", "safe", "No system impact.", false),
        ["Disney.37853FC22B2CE"] = new("Disney+", "Streaming app.", "Sponsored preinstall.", "safe", "No system impact.", false),
        ["Netflix"] = new("Netflix", "Streaming app.", "Sponsored preinstall; the website does the same thing.", "safe", "No system impact.", false),
        ["LinkedIn"] = new("LinkedIn", "Social app.", "Sponsored preinstall.", "safe", "No system impact.", false),
        ["Microsoft.WidgetsPlatformRuntime"] = new("Widgets Platform", "Taskbar widgets runtime.", "", "risky", "Removing breaks the Widgets board. Prefer hiding Widgets in taskbar settings instead.", false),
    };

    // Packages that must NEVER appear in the removal list (plumbing the OS or games need).
    private static readonly string[] ProtectedStorePrefixes =
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.Windows.", "Microsoft.NET.", "Microsoft.VCLibs", "Microsoft.UI.Xaml",
        "Microsoft.WindowsAppRuntime", "Microsoft.GamingServices", "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay", "Microsoft.XboxSpeechToTextOverlay", "Microsoft.XboxIdentityProvider",
        "Microsoft.SecHealthUI", "Microsoft.Windows.Photos", "Microsoft.WindowsCamera",
        "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.WindowsCalculator",
        "Microsoft.ScreenSketch", "Microsoft.WindowsTerminal", "Microsoft.HEVCVideoExtension",
        "Microsoft.VP9VideoExtensions", "Microsoft.WebMediaExtensions", "Microsoft.WebpImageExtension",
        "Microsoft.HEIFImageExtension", "Microsoft.RawImageExtension", "Microsoft.AV1VideoExtension",
        "Microsoft.MPEG2VideoExtension", "Microsoft.ApplicationCompatibilityEnhancements",
        "MicrosoftWindows.", "NVIDIACorp.", "AdvancedMicroDevicesInc", "AppUp.IntelGraphicsExperience",
        "RealtekSemiconductorCorp", "DolbyLaboratories",
    };

    // Win32 publishers/names that are drivers, runtimes, or security software — never listed.
    private static readonly string[] ProtectedWin32Patterns =
    {
        "NVIDIA", "AMD Software", "AMD Chipset", "Intel(R)", "Realtek", "Microsoft Visual C++",
        "Microsoft .NET", "Windows SDK", "Microsoft Edge", "Microsoft Update", "Windows Software Development",
        "Microsoft GameInput", "DirectX", "Vulkan", "OpenAL", "Microsoft Windows Desktop Runtime",
        "Microsoft ASP.NET", "WebView2", "Windows App Runtime", "PhysX", "UXP WebView Support",
        "Application Compatibility", "Windows Driver Kit",
    };

    private record Win32Meta(string Desc, string Why, string Safety, string SafetyNote);
    private static readonly Dictionary<string, Win32Meta> Win32Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["McAfee"] = new("Antivirus suite (usually a preinstalled trial).", "Trial antivirus that nags and uses resources; Windows Security protects you for free once it's removed.", "optional", "Windows Defender re-enables itself automatically after removal. Don't remove if you PAID for McAfee."),
        ["Norton"] = new("Antivirus suite (usually a preinstalled trial).", "Trial antivirus; Windows Security takes over when removed.", "optional", "Windows Defender re-enables itself automatically. Don't remove if you PAID for Norton."),
        ["Avast"] = new("Antivirus (free tier shows popups).", "If unused, it duplicates Windows Security.", "optional", "Windows Defender re-enables itself automatically."),
        ["AVG "] = new("Antivirus (free tier shows popups).", "If unused, it duplicates Windows Security.", "optional", "Windows Defender re-enables itself automatically."),
        ["WildTangent"] = new("Preinstalled game store/trials.", "Classic OEM bloatware.", "safe", "No system impact."),
        ["ExpressVPN"] = new("VPN trial often preinstalled.", "Remove if you don't use this VPN.", "optional", "No system impact."),
        ["Booking.com"] = new("Travel site shortcut app.", "Pure OEM sponsorship.", "safe", "No system impact."),
        ["Razer Cortex"] = new("'Game booster' utility.", "Boosters like this rarely help and add their own overhead.", "safe", "No system impact."),
        ["Driver Booster"] = new("Driver updater.", "Driver updaters are risky; get drivers from NVIDIA/AMD/your board maker instead.", "safe", "No system impact."),
        ["Advanced SystemCare"] = new("System 'optimizer'.", "Registry cleaners don't improve FPS and can cause issues; Tempo covers the legitimate tweaks.", "safe", "No system impact."),
        ["CCleaner"] = new("Cleaner utility.", "Windows Storage Sense covers most of this; keep only if you actively use it.", "optional", "No system impact."),
        ["Anti-Cheat"] = new("Anti-cheat service for an installed game.", "", "risky", "REQUIRED by one of your games — removing it breaks that game's online play. Only remove together with the game itself."),
        ["Easy Anti-Cheat"] = new("Anti-cheat service used by many games.", "", "risky", "REQUIRED by installed games (Fortnite, Apex, …). Only remove together with the games that use it."),
        ["BattlEye"] = new("Anti-cheat service used by many games.", "", "risky", "REQUIRED by installed games (R6, DayZ, …). Only remove together with the games that use it."),
        ["Riot Vanguard"] = new("Riot's anti-cheat driver.", "", "risky", "REQUIRED by Valorant/League. Only remove if you no longer play Riot games."),
        ["Epic Online Services"] = new("Epic's multiplayer/account plumbing.", "", "risky", "Needed by Epic Games Launcher and some Epic-powered games' online features."),
        ["Microsoft OneDrive"] = new("Cloud file sync.", "If you don't sync files with OneDrive, it idles in the background.", "optional", "Files already synced stay in the cloud; local-only files are untouched. Windows backup-to-OneDrive stops."),
        ["Visual Studio"] = new("Software development environment.", "", "risky", "Development tool — remove only if you don't code. Uninstalling can affect projects and SDKs."),
    };

    // ---------- Scan ----------

    public BloatScanResult Scan()
    {
        var list = new List<BloatApp>();
        try { ScanStore(list); } catch { }
        try { ScanWin32(list); } catch { }
        try { AttachUsage(list); } catch { }
        foreach (var a in list) a.Group = Classify(a);
        var apps = list
            .OrderByDescending(a => a.Preselect)
            .ThenBy(a => a.Safety == "safe" ? 0 : a.Safety == "optional" ? 1 : a.Safety == "unknown" ? 2 : 3)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BloatScanResult { Apps = apps, Summary = BuildSummary(apps) };
    }

    private static string Classify(BloatApp a)
    {
        bool heavy = a.RamBytes > 300L * 1048576 || a.CpuPercent > 3 || (a.RunningProcesses > 0 && a.HasStartup);
        if (a.Safety == "risky") return "keep-unless-sure";
        if (a.Safety == "safe") return "likely-unnecessary";
        if (heavy) return "resource-heavy";
        if (a.Safety == "optional") return "optional";
        return "normal";
    }

    /// <summary>
    /// Best-effort link of each app to its live footprint: running processes (RAM + sampled CPU),
    /// auto-start services, and startup entries. Read-only; ~1.2 s for the CPU sample.
    /// </summary>
    private static void AttachUsage(List<BloatApp> list)
    {
        // --- process snapshot with CPU delta sample ---
        var procs = new List<(string path, long ram, double cpu)>();
        var snapshot = Process.GetProcesses();
        var t0 = new Dictionary<int, TimeSpan>();
        foreach (var p in snapshot) { try { t0[p.Id] = p.TotalProcessorTime; } catch { } }
        Thread.Sleep(1100);
        foreach (var p in snapshot)
        {
            try
            {
                string path = p.MainModule?.FileName ?? "";
                if (path.Length == 0) continue;
                double cpu = 0;
                if (t0.TryGetValue(p.Id, out var start))
                {
                    p.Refresh();
                    cpu = (p.TotalProcessorTime - start).TotalMilliseconds / 1100.0 / Environment.ProcessorCount * 100.0;
                }
                procs.Add((path, p.WorkingSet64, Math.Max(0, cpu)));
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        // --- auto-start services by exe path ---
        var services = new List<string>();
        try
        {
            using var s = new System.Management.ManagementObjectSearcher("select PathName from Win32_Service where StartMode='Auto'");
            foreach (System.Management.ManagementObject mo in s.Get())
            {
                var raw = mo["PathName"]?.ToString() ?? "";
                var (exe, _) = SplitCommand(raw);
                if (exe.Length > 0) services.Add(exe);
            }
        }
        catch { }

        // --- startup entries ---
        var startup = new Optimizations.StartupService().List().Where(x => x.Enabled).ToList();

        foreach (var a in list)
        {
            var loc = a.InstallLocation;
            // require a real directory (avoid "C:\" style prefixes matching everything)
            if (!string.IsNullOrWhiteSpace(loc) && loc.Length > 7 && loc.Contains('\\'))
            {
                var matched = procs.Where(p => p.path.StartsWith(loc, StringComparison.OrdinalIgnoreCase)).ToList();
                a.RunningProcesses = matched.Count;
                a.RamBytes = matched.Sum(p => p.ram);
                a.CpuPercent = Math.Round(matched.Sum(p => p.cpu), 1);
                a.ServiceCount = services.Count(svc => svc.StartsWith(loc, StringComparison.OrdinalIgnoreCase));
            }
            var su = startup.FirstOrDefault(x =>
                (!string.IsNullOrWhiteSpace(loc) && x.Command.Contains(loc, StringComparison.OrdinalIgnoreCase)) ||
                x.Name.Contains(a.Name, StringComparison.OrdinalIgnoreCase) ||
                (a.Name.Length > 4 && a.Name.Contains(x.Name, StringComparison.OrdinalIgnoreCase)));
            if (su != null) { a.HasStartup = true; a.StartupName = su.Name; }
        }
    }

    private static BloatSummary BuildSummary(List<BloatApp> apps)
    {
        var s = new BloatSummary
        {
            TotalApps = apps.Count,
            TotalSizeBytes = apps.Sum(a => a.SizeBytes),
            StartupEnabled = apps.Count(a => a.HasStartup),
            RunningNow = apps.Count(a => a.RunningProcesses > 0),
            RunningRamBytes = apps.Sum(a => a.RamBytes),
        };
        string Fmt(long b) => b >= 1073741824 ? (b / 1073741824.0).ToString("0.0") + " GB" : (b / 1048576) + " MB";
        foreach (var a in apps.Where(a => a.RamBytes > 50 * 1048576).OrderByDescending(a => a.RamBytes).Take(5))
            s.TopRamApps.Add(a.Name + " — " + Fmt(a.RamBytes) + (a.CpuPercent >= 1 ? " · " + a.CpuPercent + "% CPU" : ""));
        foreach (var a in apps.Where(a => a.SizeBytes > 0).OrderByDescending(a => a.SizeBytes).Take(5))
            s.TopDiskApps.Add(a.Name + " — " + Fmt(a.SizeBytes));
        try
        {
            long temp = Optimizations.MaintenanceService.MeasureDirs(Optimizations.MaintenanceService.TempDirs(), 24);
            var (rb, rbItems) = Optimizations.MaintenanceService.RecycleBinInfo();
            s.CleanupBytes = temp + rb;
            if (temp > 0) s.CleanupLines.Add("Temp files (safe to clean) — " + Fmt(temp));
            if (rb > 0) s.CleanupLines.Add("Recycle Bin (" + rbItems + " items) — " + Fmt(rb));
            s.CleanupLines.Add("Clean both from Optimize → Storage.");
        }
        catch { }
        return s;
    }

    private void ScanStore(List<BloatApp> list)
    {
        var pm = new PackageManager();
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        foreach (var pkg in pm.FindPackagesForUser(sid))
        {
            try
            {
                if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                var name = pkg.Id.Name;
                if (ProtectedStorePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;

                StoreCatalog.TryGetValue(name, out var meta);
                // OS-signed packages (Settings, File Explorer, pickers…) are Windows itself — never offer them.
                try { if (meta == null && pkg.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System) continue; } catch { }
                var isMs = pkg.Id.PublisherId is "8wekyb3d8bbwe" or "cw5n1h2txyewy"
                    || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase);
                if (meta == null && isMs) continue; // unknown Microsoft package: too risky to offer — skip

                var app = new BloatApp
                {
                    Id = "store:" + pkg.Id.FullName,
                    Kind = "store",
                    Name = meta?.Friendly ?? (pkg.DisplayName is { Length: > 0 } dn ? dn : name),
                    Publisher = pkg.PublisherDisplayName ?? pkg.Id.Publisher,
                    Version = pkg.Id.Version.Major + "." + pkg.Id.Version.Minor + "." + pkg.Id.Version.Build,
                    Description = meta?.Desc ?? "Microsoft Store app installed for this user.",
                    WhyRemovable = meta?.Why ?? "Not part of Windows — remove it only if you know you don't use it.",
                    Safety = meta?.Safety ?? "unknown",
                    SafetyNote = meta?.SafetyNote ?? "Unknown to Tempo's catalog. It is a Store app, so removal won't break Windows, but make sure you don't use it.",
                    Preselect = meta?.Preselect ?? false,
                    Restorable = true,
                    Restore = "Reinstall free from the Microsoft Store (search its name).",
                    UninstallMethod = "Official Store package removal (per-user)",
                    Silent = true,
                };
                try
                {
                    app.InstallLocation = pkg.InstalledLocation?.Path ?? "";
                    app.SizeBytes = QuickDirSize(app.InstallLocation);
                }
                catch { }
                list.Add(app);
            }
            catch { }
        }
    }

    private static long QuickDirSize(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return 0;
        long size = 0; int files = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { }
                if (++files > 4000) break; // keep the scan fast; big apps just show as "4000+ files measured"
            }
        }
        catch { }
        return size;
    }

    private void ScanWin32(List<BloatApp> list)
    {
        var roots = new (RegistryHive hive, string path, string scope)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "machine"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "machine32"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "user"),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, path, scope) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
                using var root = baseKey.OpenSubKey(path);
                if (root == null) continue;
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        var name = k.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        if ((k.GetValue("SystemComponent") as int?) == 1) continue;
                        if (k.GetValue("ParentKeyName") != null) continue; // updates/patches
                        var uninstall = k.GetValue("UninstallString") as string;
                        var quiet = k.GetValue("QuietUninstallString") as string;
                        if (string.IsNullOrWhiteSpace(uninstall) && string.IsNullOrWhiteSpace(quiet)) continue;
                        if (ProtectedWin32Patterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
                        if (!seen.Add(name)) continue;

                        var meta = Win32Catalog.FirstOrDefault(c => name.Contains(c.Key, StringComparison.OrdinalIgnoreCase)).Value;
                        var sizeKb = k.GetValue("EstimatedSize") as int? ?? 0;
                        var instLoc = (k.GetValue("InstallLocation") as string ?? "").Trim().TrimEnd('\\');
                        if (instLoc.Length == 0)
                        {
                            // fall back to the uninstaller's folder
                            var icon = (k.GetValue("DisplayIcon") as string ?? "").Split(',')[0].Trim('"', ' ');
                            var src = icon.Length > 3 ? icon : SplitCommand(uninstall ?? quiet ?? "").exe;
                            try { instLoc = Path.GetDirectoryName(src) ?? ""; } catch { }
                        }
                        list.Add(new BloatApp
                        {
                            InstallLocation = instLoc,
                            Id = "win32:" + hive + "|" + path + "|" + sub,
                            Kind = "win32",
                            Name = name,
                            Publisher = k.GetValue("Publisher") as string ?? "",
                            Version = k.GetValue("DisplayVersion") as string ?? "",
                            Description = meta?.Desc ?? "Installed desktop program.",
                            WhyRemovable = meta?.Why ?? "Listed so YOU can decide — Tempo doesn't recognize it as bloatware. Remove only software you know you don't use.",
                            Safety = meta?.Safety ?? "unknown",
                            SafetyNote = meta?.SafetyNote ?? "Unknown to Tempo's catalog. If other programs depend on it, they may stop working. Research before removing.",
                            SizeBytes = (long)sizeKb * 1024,
                            Restorable = false,
                            Restore = "Not automatic — you would need to download and reinstall it yourself.",
                            UninstallMethod = string.IsNullOrWhiteSpace(quiet) ? "Official uninstaller (its window will open)" : "Official quiet uninstall",
                            Silent = !string.IsNullOrWhiteSpace(quiet),
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // ---------- Uninstall ----------

    public async Task<ApplyResult> Uninstall(string id, BackupSession session, Action<string>? progress = null)
    {
        try
        {
            if (id.StartsWith("store:")) return await UninstallStore(id, session, progress);
            if (id.StartsWith("win32:")) return await UninstallWin32(id, session, progress);
            return new ApplyResult { Id = id, Success = false, Error = "Unknown app id." };
        }
        catch (Exception ex)
        {
            Logging.Log.Error("uninstall", ex);
            return new ApplyResult { Id = id, Success = false, Error = ex.Message };
        }
    }

    private async Task<ApplyResult> UninstallStore(string id, BackupSession session, Action<string>? progress)
    {
        var fullName = id["store:".Length..];
        progress?.Invoke("Removing Store package " + fullName + "…");
        try
        {
            var pm = new PackageManager();
            var result = await pm.RemovePackageAsync(fullName).AsTask();
            if (!string.IsNullOrEmpty(result.ErrorText))
                return new ApplyResult { Id = id, Success = false, Error = result.ErrorText };
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x80073CFA || ex.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
        {
            // already gone (removed earlier / by another tool) — treat as done
            return new ApplyResult { Id = id, Success = true, Error = "Was already removed." };
        }
        session.Changes.Add(new ChangeRecord
        {
            TweakId = "bloat-store", Title = "Removed Store app: " + fullName, Type = "appx",
            Target = fullName, Restorable = false,
            OldValue = "installed", NewValue = "removed (reinstall free from Microsoft Store)"
        });
        return new ApplyResult { Id = id, Success = true };
    }

    private async Task<ApplyResult> UninstallWin32(string id, BackupSession session, Action<string>? progress)
    {
        var parts = id["win32:".Length..].Split('|', 3);
        var hive = Enum.Parse<RegistryHive>(parts[0]);
        string keyPath = parts[1] + @"\" + parts[2];
        string name; string? cmd; bool quiet;
        using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
        using (var k = baseKey.OpenSubKey(keyPath))
        {
            if (k == null) return new ApplyResult { Id = id, Success = true, Error = "Was already removed." };
            name = k.GetValue("DisplayName") as string ?? parts[2];
            var quietCmd = k.GetValue("QuietUninstallString") as string;
            cmd = string.IsNullOrWhiteSpace(quietCmd) ? k.GetValue("UninstallString") as string : quietCmd;
            quiet = !string.IsNullOrWhiteSpace(quietCmd);
        }
        if (string.IsNullOrWhiteSpace(cmd)) return new ApplyResult { Id = id, Success = false, Error = name + ": no uninstall command registered." };

        var (exe, args) = SplitCommand(cmd);
        bool isMsi = exe.Contains("msiexec", StringComparison.OrdinalIgnoreCase);
        // Many MSI entries register "MsiExec.exe /I{code}" (modify/repair). Force uninstall mode.
        if (isMsi) args = System.Text.RegularExpressions.Regex.Replace(args, @"/I\s*{", "/X{", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        progress?.Invoke((quiet ? "Quiet-uninstalling " : "Opening the official uninstaller for ") + name + "…");
        Logging.Log.Write("uninstall", name + " -> " + exe + " " + args);
        var psi = new ProcessStartInfo(exe, args) { UseShellExecute = true };
        using var p = Process.Start(psi);
        if (p == null) return new ApplyResult { Id = id, Success = false, Error = name + ": uninstaller failed to start." };

        // Interactive uninstallers can open BEHIND this window — bring theirs to the front.
        if (!quiet) _ = Task.Run(async () =>
        {
            for (int i = 0; i < 16; i++)
            {
                try
                {
                    await Task.Delay(500);
                    p.Refresh();
                    if (p.HasExited) return;
                    if (p.MainWindowHandle != IntPtr.Zero) { ShowWindow(p.MainWindowHandle, 9 /*SW_RESTORE*/); SetForegroundWindow(p.MainWindowHandle); return; }
                }
                catch { return; }
            }
        });

        // Wait with LIVE progress so the UI never looks frozen; cap at 10 minutes.
        var started = DateTime.Now;
        int? exitCode = null;
        while (true)
        {
            try { if (p.WaitForExit(5000)) { exitCode = p.ExitCode; break; } }
            catch { break; }
            var secs = (int)(DateTime.Now - started).TotalSeconds;
            if (secs >= 600)
            {
                return new ApplyResult { Id = id, Success = false, Error = name + ": its uninstaller is still open after 10 minutes. Finish or close its window, then re-scan — Tempo did not cancel it." };
            }
            progress?.Invoke(quiet
                ? $"Still uninstalling {name}… ({secs}s)"
                : $"Waiting for {name}'s uninstaller window — check your taskbar if you don't see it ({secs}s)");
        }

        // MSI gives reliable exit codes.
        if (isMsi && exitCode is 1602)
            return new ApplyResult { Id = id, Success = false, Error = name + ": you cancelled its uninstaller — nothing was removed." };

        // Verify by re-checking the uninstall key. NSIS-style uninstallers hand off to a temp copy
        // and exit instantly, so poll for up to 25 s before judging.
        bool gone = false;
        for (int i = 0; i < 10; i++)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default))
            using (var k = baseKey.OpenSubKey(keyPath))
                gone = k == null;
            if (gone) break;
            progress?.Invoke("Verifying removal of " + name + "…");
            await Task.Delay(2500);
        }

        if (gone || (isMsi && exitCode is 0 or 3010))
        {
            session.Changes.Add(new ChangeRecord
            {
                TweakId = "bloat-win32", Title = "Uninstalled program: " + name, Type = "win32",
                Target = name, Restorable = false,
                OldValue = "installed", NewValue = "uninstalled" + (exitCode is 3010 ? " (restart required to finish)" : "")
            });
            return new ApplyResult { Id = id, Success = true, Error = exitCode is 3010 ? name + ": removed — restart to finish." : null };
        }
        return new ApplyResult
        {
            Id = id, Success = false,
            Error = name + ": the uninstaller ran but the program still appears installed — it may have been cancelled or needs more steps. Re-scan to check."
        };
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static (string exe, string args) SplitCommand(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            var end = cmd.IndexOf('"', 1);
            if (end > 0) return (cmd[1..end], cmd[(end + 1)..].Trim());
        }
        // MsiExec and unquoted paths: split on first space that follows ".exe"
        var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (i > 0 && i + 4 < cmd.Length) return (cmd[..(i + 4)], cmd[(i + 4)..].Trim());
        var sp = cmd.IndexOf(' ');
        return sp > 0 ? (cmd[..sp], cmd[(sp + 1)..]) : (cmd, "");
    }
}
