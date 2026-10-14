using System.Diagnostics;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Tempo.Core.Models;

namespace Tempo.Core.Optimizations;

/// <summary>
/// Read-only diagnostic checks. These NEVER change anything — they inspect and report,
/// so users can see what's actually worth fixing instead of applying blind tweaks.
/// </summary>
public class DiagnosticsService
{
    public DiagnosticResult Run(string id, SystemInfo si)
    {
        try
        {
            return id switch
            {
                "diag-driver" => DriverAge(si),
                "diag-display" => DisplayModes(si),
                "diag-vbs" => VbsStatus(),
                "diag-gpu-msi" => GpuMsi(si),
                "diag-updates" => UpdateStatus(),
                "diag-top-memory" => TopMemory(),
                "diag-memory-pressure" => MemoryPressure(),
                "diag-space" => SpaceUsage(si),
                "diag-drive-wear" => DriveWear(),
                "diag-net-latency" => NetLatency(),
                "diag-dns-speed" => DnsSpeed(),
                "diag-services" => ThirdPartyServices(),
                "diag-net-config" => NetConfig(),
                _ => new DiagnosticResult { Id = id, Ok = false, Summary = "Unknown diagnostic." }
            };
        }
        catch (Exception ex)
        {
            return new DiagnosticResult { Id = id, Ok = false, Summary = "Check failed: " + ex.Message };
        }
    }

    private static string Gb(long b) => (b / 1073741824.0).ToString("0.0") + " GB";
    private static string Mb(long b) => b >= 1073741824 ? Gb(b) : (b / 1048576) + " MB";

    // ---------- GPU ----------

    private static DiagnosticResult DriverAge(SystemInfo si)
    {
        var r = new DiagnosticResult { Id = "diag-driver" };
        var g = si.Gpus.FirstOrDefault();
        if (g == null) { r.Summary = "No GPU detected."; return r; }
        foreach (var gpu in si.Gpus)
            r.Lines.Add($"{gpu.Name} — driver {gpu.DriverVersion}" + (gpu.DriverAgeDays >= 0 ? $" ({gpu.DriverAgeDays} days old, {gpu.DriverDate})" : ""));
        var old = si.Gpus.Where(x => x.DriverAgeDays > 120).ToList();
        if (old.Count > 0)
        {
            r.Ok = false;
            r.Summary = $"Driver is {old[0].DriverAgeDays} days old — updating is the single most reliable performance fix.";
            var v = old[0].Vendor;
            r.Lines.Add(v == "NVIDIA" ? "Update via NVIDIA App or nvidia.com/drivers"
                       : v == "AMD" ? "Update via AMD Software or amd.com/support"
                       : "Update via intel.com/download-center");
        }
        else r.Summary = "GPU driver is reasonably current. New drivers still bring per-game fixes — check monthly.";
        return r;
    }

    private static DiagnosticResult DisplayModes(SystemInfo si)
    {
        var r = new DiagnosticResult { Id = "diag-display" };
        var (w, h, hz) = DisplayUtil.Current();
        if (w == 0) { r.Summary = "Could not read display mode."; r.Ok = false; return r; }
        int max = DisplayUtil.MaxRefreshAtCurrentResolution();
        r.Lines.Add($"Current mode: {w}x{h} @ {hz} Hz");
        r.Lines.Add($"Highest supported at this resolution: {max} Hz");
        if (max > hz + 1)
        {
            r.Ok = false;
            r.Summary = $"Your display supports {max} Hz but runs at {hz} Hz — free smoothness is being left on the table. Use the 'Set maximum refresh rate' option to fix it.";
        }
        else r.Summary = $"Display already runs at its maximum refresh rate ({hz} Hz). ✔";
        return r;
    }

    private static DiagnosticResult VbsStatus()
    {
        var r = new DiagnosticResult { Id = "diag-vbs" };
        int status = 0;
        var running = new List<int>();
        try
        {
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\DeviceGuard");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("select VirtualizationBasedSecurityStatus, SecurityServicesRunning from Win32_DeviceGuard"));
            foreach (ManagementObject mo in s.Get())
            {
                status = Convert.ToInt32(mo["VirtualizationBasedSecurityStatus"]);
                if (mo["SecurityServicesRunning"] is int[] arr) running = arr.ToList();
                else if (mo["SecurityServicesRunning"] is uint[] uarr) running = uarr.Select(x => (int)x).ToList();
            }
        }
        catch { r.Summary = "Could not query VBS status."; return r; }
        bool vbsOn = status == 2;
        bool hvciOn = running.Contains(2);
        r.Lines.Add("Virtualization-Based Security: " + (vbsOn ? "running" : "off"));
        r.Lines.Add("Memory Integrity (HVCI): " + (hvciOn ? "on" : "off"));
        if (vbsOn || hvciOn)
        {
            r.Summary = "Memory Integrity / VBS is ON. Reviews measure a ~3–8% FPS cost in some games — but it is a real security feature, so Tempo will not toggle it.";
            r.Lines.Add("Your call: Settings → Privacy & security → Windows Security → Device security → Core isolation.");
            r.Lines.Add("If you game on a dedicated rig and accept the tradeoff, turn 'Memory integrity' off there, then restart.");
        }
        else r.Summary = "Memory Integrity / VBS is off — no FPS overhead from this feature.";
        return r;
    }

    private static DiagnosticResult GpuMsi(SystemInfo si)
    {
        var r = new DiagnosticResult { Id = "diag-gpu-msi" };
        var found = false;
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
            using var video = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            // locate GPU PNP ids via Win32_VideoController
            using var s = new ManagementObjectSearcher("select Name,PNPDeviceID from Win32_VideoController");
            foreach (ManagementObject mo in s.Get())
            {
                var pnp = mo["PNPDeviceID"]?.ToString() ?? "";
                var name = mo["Name"]?.ToString() ?? "";
                if (pnp.Length == 0) continue;
                using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + pnp + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");
                if (k != null)
                {
                    found = true;
                    var msi = (k.GetValue("MSISupported") as int?) == 1;
                    r.Lines.Add(name + ": " + (msi ? "MSI mode ✔ (modern, low-latency interrupts)" : "legacy line-based interrupts"));
                    if (!msi) r.Ok = false;
                }
            }
        }
        catch { }
        r.Summary = !found ? "Could not read GPU interrupt configuration."
            : r.Ok ? "GPU already uses Message-Signaled Interrupts — nothing to change (the 'MSI mode' tweak other tools sell is already active)."
            : "GPU uses legacy interrupts. Modern drivers normally enable MSI themselves; a driver reinstall usually fixes this — safer than editing it by hand.";
        return r;
    }

    // ---------- Windows ----------

    private static DiagnosticResult UpdateStatus()
    {
        var r = new DiagnosticResult { Id = "diag-updates" };
        bool reboot = false;
        try { reboot |= Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") != null; } catch { }
        try { reboot |= Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") != null; } catch { }
        r.Lines.Add("Restart pending: " + (reboot ? "YES — Windows is waiting to finish an update" : "no"));
        try
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
            if (t != null)
            {
                dynamic au = Activator.CreateInstance(t)!;
                var last = au.Results.LastInstallationSuccessDate;
                if (last is DateTime d) r.Lines.Add("Last successful update install: " + d.ToLocalTime().ToString("yyyy-MM-dd"));
            }
        }
        catch { }
        r.Ok = !reboot;
        r.Summary = reboot
            ? "A restart is pending. Half-installed updates are a classic cause of stutter and weird slowdowns — restart before judging performance."
            : "No pending restart. Windows Update state looks clean.";
        return r;
    }

    private static DiagnosticResult ThirdPartyServices()
    {
        var r = new DiagnosticResult { Id = "diag-services" };
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<string>();
        using var s = new ManagementObjectSearcher("select Name,DisplayName,PathName,State from Win32_Service where StartMode='Auto'");
        foreach (ManagementObject mo in s.Get())
        {
            var path = mo["PathName"]?.ToString() ?? "";
            if (path.Contains(win, StringComparison.OrdinalIgnoreCase)) continue;
            var disp = mo["DisplayName"]?.ToString() ?? mo["Name"]?.ToString() ?? "";
            var state = mo["State"]?.ToString() ?? "";
            list.Add(disp + (state == "Running" ? " (running)" : " (" + state.ToLower() + ")"));
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        r.Lines.AddRange(list.Take(25));
        if (list.Count > 25) r.Lines.Add("…and " + (list.Count - 25) + " more");
        r.Summary = list.Count + " third-party services start automatically with Windows. Review them — anti-cheat and driver services are needed; updaters for apps you rarely use are not. Manage via the app's own settings or the Bloatware tab, not by force-disabling services.";
        return r;
    }

    // ---------- memory ----------

    private static DiagnosticResult TopMemory()
    {
        var r = new DiagnosticResult { Id = "diag-top-memory" };
        var groups = Process.GetProcesses()
            .GroupBy(p => { try { return p.ProcessName; } catch { return "?"; } })
            .Select(g => new { Name = g.Key, Ram = g.Sum(p => { try { return p.WorkingSet64; } catch { return 0L; } }), N = g.Count() })
            .OrderByDescending(x => x.Ram)
            .Take(10).ToList();
        foreach (var g in groups)
            r.Lines.Add($"{g.Name}{(g.N > 1 ? $" ×{g.N}" : "")} — {Mb(g.Ram)}");
        r.Summary = "Top memory users right now. Browsers and launchers at the top are normal; close them before heavy gaming sessions if RAM is tight.";
        return r;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public int cb; public IntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable,
            SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public int HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION pi, int cb);

    private static DiagnosticResult MemoryPressure()
    {
        var r = new DiagnosticResult { Id = "diag-memory-pressure" };
        if (!GetPerformanceInfo(out var pi, Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        { r.Summary = "Could not read memory info."; r.Ok = false; return r; }
        long page = pi.PageSize.ToInt64();
        long physTotal = pi.PhysicalTotal.ToInt64() * page;
        long physAvail = pi.PhysicalAvailable.ToInt64() * page;
        long commitTotal = pi.CommitTotal.ToInt64() * page;
        long commitLimit = pi.CommitLimit.ToInt64() * page;
        double usedPct = 100.0 * (physTotal - physAvail) / physTotal;
        double commitPct = 100.0 * commitTotal / commitLimit;
        r.Lines.Add($"Physical RAM: {Gb(physTotal - physAvail)} used of {Gb(physTotal)} ({usedPct:0}%)");
        r.Lines.Add($"Commit charge: {Gb(commitTotal)} of {Gb(commitLimit)} limit ({commitPct:0}%)");
        r.Lines.Add($"Processes: {pi.ProcessCount} · Threads: {pi.ThreadCount}");
        if (usedPct > 85 || commitPct > 80)
        {
            r.Ok = false;
            r.Summary = "Memory pressure is HIGH. Expect stutter when games load assets. Close background apps (see 'Top memory users') — and do NOT use RAM-cleaner apps, they make stutter worse.";
        }
        else r.Summary = "Memory pressure is healthy. Windows managing cache aggressively is normal — free RAM is wasted RAM.";
        return r;
    }

    // ---------- storage ----------

    private static DiagnosticResult SpaceUsage(SystemInfo si)
    {
        var r = new DiagnosticResult { Id = "diag-space" };
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var sysRoot = Path.GetPathRoot(winDir) ?? @"C:\";
        long temp = MaintenanceService.MeasureDirs(MaintenanceService.TempDirs());
        long shader = MaintenanceService.MeasureDirs(MaintenanceService.ShaderCacheDirs());
        var (rbSize, rbItems) = MaintenanceService.RecycleBinInfo();
        r.Lines.Add("Temp files: " + Mb(temp));
        r.Lines.Add("Shader caches: " + Mb(shader) + " (rebuilt automatically; clearing causes brief first-launch stutter)");
        r.Lines.Add($"Recycle Bin: {Mb(rbSize)} ({rbItems} items)");
        foreach (var f in new[] { "hiberfil.sys", "pagefile.sys", "swapfile.sys" })
        {
            try { var fi = new FileInfo(Path.Combine(sysRoot, f)); if (fi.Exists) r.Lines.Add(f + ": " + Mb(fi.Length)); } catch { }
        }
        try { if (Directory.Exists(Path.Combine(sysRoot, "Windows.old"))) r.Lines.Add("Windows.old present — old Windows install; Settings → Storage → Cleanup recommendations can remove it (several GB)."); } catch { }
        try
        {
            var dumps = new FileInfo(Path.Combine(winDir, "MEMORY.DMP"));
            long mini = MaintenanceService.MeasureDirs(new[] { Path.Combine(winDir, "Minidump") });
            long total = (dumps.Exists ? dumps.Length : 0) + mini;
            if (total > 100 * 1048576) r.Lines.Add("Crash dumps: " + Mb(total));
        }
        catch { }
        long reclaimable = temp + rbSize;
        r.Summary = $"~{Mb(reclaimable)} immediately reclaimable (temp + Recycle Bin). Use the cleanup actions below to free it.";
        var sys = si.Drives.FirstOrDefault(d => d.IsSystemDrive);
        if (sys != null && sys.TotalBytes > 0 && (double)sys.FreeBytes / sys.TotalBytes < 0.12)
        { r.Ok = false; r.Summary += $" Drive {sys.Letter} is nearly full ({Gb(sys.FreeBytes)} free) — SSDs slow down below ~10–15% free."; }
        return r;
    }

    private static DiagnosticResult DriveWear()
    {
        var r = new DiagnosticResult { Id = "diag-drive-wear" };
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            var disks = new Dictionary<string, string>();
            using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("select DeviceId,FriendlyName,HealthStatus from MSFT_PhysicalDisk")))
                foreach (ManagementObject mo in s.Get())
                    disks[mo["DeviceId"]?.ToString() ?? ""] =
                        (mo["FriendlyName"]?.ToString() ?? "disk") + " — health: " +
                        (Convert.ToInt32(mo["HealthStatus"] ?? 99) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" });
            using (var s2 = new ManagementObjectSearcher(scope, new ObjectQuery("select DeviceId,Wear,Temperature from MSFT_StorageReliabilityCounter")))
                foreach (ManagementObject mo in s2.Get())
                {
                    var id = mo["DeviceId"]?.ToString() ?? "";
                    var baseLine = disks.TryGetValue(id, out var fn) ? fn : "disk " + id;
                    var wear = mo["Wear"];
                    var temp = mo["Temperature"];
                    var extras = new List<string>();
                    if (wear != null && Convert.ToInt32(wear) > 0) extras.Add("wear " + wear + "%");
                    if (temp != null && Convert.ToInt32(temp) > 0) extras.Add(temp + "°C");
                    r.Lines.Add(baseLine + (extras.Count > 0 ? " · " + string.Join(" · ", extras) : ""));
                    disks.Remove(id);
                }
            foreach (var leftover in disks.Values) r.Lines.Add(leftover);
        }
        catch (Exception ex) { r.Lines.Add("Reliability counters unavailable: " + ex.Message); }
        r.Ok = !r.Lines.Any(l => l.Contains("Warning") || l.Contains("Unhealthy"));
        r.Summary = r.Ok ? "Drive health looks good." : "A drive reports degraded health — back up your data and investigate before anything else.";
        return r;
    }

    // ---------- network ----------

    private static DiagnosticResult NetLatency()
    {
        var r = new DiagnosticResult { Id = "diag-net-latency" };
        foreach (var host in new[] { "1.1.1.1", "8.8.8.8" })
        {
            var times = new List<long>();
            int lost = 0;
            using var ping = new Ping();
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    var reply = ping.Send(host, 1500);
                    if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime); else lost++;
                }
                catch { lost++; }
            }
            if (times.Count > 0)
            {
                double avg = times.Average();
                double jitter = times.Count > 1 ? times.Zip(times.Skip(1), (a, b) => Math.Abs(a - b)).Average() : 0;
                r.Lines.Add($"{host}: avg {avg:0} ms · jitter {jitter:0.0} ms · loss {lost}/6");
                if (avg > 60 || jitter > 15 || lost > 0) r.Ok = false;
            }
            else { r.Lines.Add(host + ": unreachable"); r.Ok = false; }
        }
        r.Summary = r.Ok
            ? "Internet latency and jitter look healthy. In-game lag is more likely the game server or Wi-Fi."
            : "Latency, jitter or packet loss detected. If you are on Wi-Fi, try Ethernet — it beats every software tweak. Also check for downloads/streams on your network.";
        return r;
    }

    private static DiagnosticResult DnsSpeed()
    {
        var r = new DiagnosticResult { Id = "diag-dns-speed" };
        // current DNS servers
        var current = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct().Take(2).ToList();
        var targets = new List<(string label, IPAddress ip)>();
        foreach (var c in current) targets.Add(("current (" + c + ")", c));
        targets.Add(("Cloudflare 1.1.1.1", IPAddress.Parse("1.1.1.1")));
        targets.Add(("Google 8.8.8.8", IPAddress.Parse("8.8.8.8")));
        foreach (var (label, ip) in targets)
        {
            var ms = QueryDnsAvg(ip, "www.google.com", 3);
            r.Lines.Add($"{label}: " + (ms >= 0 ? ms.ToString("0") + " ms" : "no answer"));
        }
        r.Summary = "DNS lookup speed only affects how fast connections START (server browsers, site loads) — never in-match ping. If your current DNS is much slower than the alternatives, change it in Settings → Network → your adapter → DNS.";
        return r;
    }

    /// <summary>Minimal raw DNS A-query benchmark (avg ms over n tries, -1 = fail).</summary>
    private static double QueryDnsAvg(IPAddress server, string host, int tries)
    {
        var ok = new List<double>();
        for (int t = 0; t < tries; t++)
        {
            try
            {
                var q = BuildDnsQuery(host, (ushort)(0x1234 + t));
                using var udp = new UdpClient();
                udp.Client.ReceiveTimeout = 1200;
                var sw = Stopwatch.StartNew();
                udp.Send(q, q.Length, new IPEndPoint(server, 53));
                var ep = new IPEndPoint(IPAddress.Any, 0);
                udp.Receive(ref ep);
                sw.Stop();
                ok.Add(sw.Elapsed.TotalMilliseconds);
            }
            catch { }
        }
        return ok.Count > 0 ? ok.Average() : -1;
    }

    private static byte[] BuildDnsQuery(string host, ushort id)
    {
        var ms = new MemoryStream();
        void W16(ushort v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)(v & 0xff)); }
        W16(id); W16(0x0100); W16(1); W16(0); W16(0); W16(0);
        foreach (var part in host.Split('.'))
        {
            var b = System.Text.Encoding.ASCII.GetBytes(part);
            ms.WriteByte((byte)b.Length); ms.Write(b);
        }
        ms.WriteByte(0); W16(1); W16(1); // A, IN
        return ms.ToArray();
    }

    private static DiagnosticResult NetConfig()
    {
        var r = new DiagnosticResult { Id = "diag-net-config" };
        var active = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.GetIPProperties().GatewayAddresses.Count > 0)
            .ToList();
        foreach (var n in active)
        {
            var wifi = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            r.Lines.Add($"{n.Name}: {(wifi ? "Wi-Fi" : "Ethernet")} · link {n.Speed / 1_000_000} Mbps");
            if (wifi) { r.Ok = false; }
        }
        var tuning = MaintenanceService.AutotuningLevel();
        r.Lines.Add("TCP auto-tuning: " + tuning + (tuning.Equals("normal", StringComparison.OrdinalIgnoreCase) ? " ✔" : " — should be 'normal'; some old 'optimizers' break this"));
        if (!tuning.Equals("normal", StringComparison.OrdinalIgnoreCase) && tuning != "unknown") r.Ok = false;
        r.Summary = r.Ok
            ? "Network configuration looks right."
            : active.Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                ? "You're gaming on Wi-Fi. A cable is worth more than every software tweak combined. Also check the TCP note above."
                : "TCP auto-tuning is not 'normal' — use the repair option in the Network category to fix it.";
        return r;
    }
}
