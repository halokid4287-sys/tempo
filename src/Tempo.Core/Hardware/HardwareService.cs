using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Tempo.Core.Models;

namespace Tempo.Core.Hardware;

public class HardwareService
{
    public SystemInfo GetSystemInfo()
    {
        var info = new SystemInfo { MachineName = Environment.MachineName };
        try { ReadCpu(info); } catch { /* leave defaults */ }
        try { ReadGpus(info); } catch { }
        try { ReadRam(info); } catch { }
        try { ReadDrives(info); } catch { }
        try { ReadOs(info); } catch { }
        try { ReadBoard(info); } catch { }
        try { ReadPowerPlan(info); } catch { }
        try { info.IsLaptop = DetectLaptop(); } catch { }
        return info;
    }

    private static void ReadCpu(SystemInfo info)
    {
        using var s = new ManagementObjectSearcher("select Name,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,Manufacturer from Win32_Processor");
        foreach (ManagementObject mo in s.Get())
        {
            info.Cpu.Name = Clean(mo["Name"]);
            info.Cpu.Cores = ToInt(mo["NumberOfCores"]);
            info.Cpu.Threads = ToInt(mo["NumberOfLogicalProcessors"]);
            info.Cpu.MaxClockMhz = ToInt(mo["MaxClockSpeed"]);
            var man = Clean(mo["Manufacturer"]);
            info.Cpu.Vendor = man.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel"
                : man.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD" : man;
            break;
        }
    }

    private static void ReadGpus(SystemInfo info)
    {
        using var s = new ManagementObjectSearcher(
            "select Name,DriverVersion,DriverDate,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,PNPDeviceID from Win32_VideoController");
        foreach (ManagementObject mo in s.Get())
        {
            var g = new GpuInfo { Name = Clean(mo["Name"]) };
            if (g.Name.Length == 0) continue;
            var n = g.Name.ToLowerInvariant();
            g.Vendor = n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx") ? "NVIDIA"
                : n.Contains("amd") || n.Contains("radeon") ? "AMD"
                : n.Contains("intel") ? "Intel" : "Other";
            g.DriverVersion = Clean(mo["DriverVersion"]);
            var dd = Clean(mo["DriverDate"]);
            if (dd.Length >= 8)
            {
                try
                {
                    var d = ManagementDateTimeConverter.ToDateTime(dd);
                    g.DriverDate = d.ToString("yyyy-MM-dd");
                    g.DriverAgeDays = (int)(DateTime.Now - d).TotalDays;
                }
                catch { }
            }
            var w = ToInt(mo["CurrentHorizontalResolution"]);
            var h = ToInt(mo["CurrentVerticalResolution"]);
            if (w > 0 && h > 0) { g.Resolution = w + "x" + h; g.IsPrimary = true; }
            g.RefreshRate = ToInt(mo["CurrentRefreshRate"]);
            g.VramBytes = ReadVramFromRegistry(Clean(mo["PNPDeviceID"]), g.Name);
            info.Gpus.Add(g);
        }
        // Prefer nvidia-smi for the human-readable driver version / exact VRAM if available
        try
        {
            var nv = info.Gpus.FirstOrDefault(g => g.Vendor == "NVIDIA");
            if (nv != null)
            {
                var smi = RunCapture("nvidia-smi", "--query-gpu=driver_version,memory.total --format=csv,noheader,nounits", 4000);
                if (!string.IsNullOrWhiteSpace(smi))
                {
                    var parts = smi.Trim().Split(',');
                    if (parts.Length >= 1 && parts[0].Trim().Length > 0) nv.DriverVersion = parts[0].Trim();
                    if (parts.Length >= 2 && long.TryParse(parts[1].Trim(), out var mb) && mb > 0) nv.VramBytes = mb * 1024L * 1024L;
                }
            }
        }
        catch { }
        info.Gpus = info.Gpus
            .OrderByDescending(g => g.IsPrimary)
            .ThenByDescending(g => g.VramBytes)
            .ToList();
    }

    // WMI AdapterRAM is a 32-bit value (caps at 4GB). The video class registry key stores the real size.
    private static long ReadVramFromRegistry(string pnpId, string name)
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return 0;
            foreach (var subName in cls.GetSubKeyNames())
            {
                if (!Regex.IsMatch(subName, @"^\d{4}$")) continue;
                using var sub = cls.OpenSubKey(subName);
                if (sub == null) continue;
                var desc = sub.GetValue("DriverDesc") as string ?? "";
                var match = sub.GetValue("MatchingDeviceId") as string ?? "";
                bool matches = desc.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (match.Length > 0 && pnpId.StartsWith(match, StringComparison.OrdinalIgnoreCase));
                if (!matches) continue;
                var qw = sub.GetValue("HardwareInformation.qwMemorySize");
                if (qw is long l && l > 0) return l;
                if (qw is byte[] b && b.Length >= 8) return BitConverter.ToInt64(b, 0);
                var dw = sub.GetValue("HardwareInformation.MemorySize");
                if (dw is int i && i > 0) return (uint)i;
                if (dw is byte[] b2 && b2.Length >= 4) return BitConverter.ToUInt32(b2, 0);
            }
        }
        catch { }
        return 0;
    }

    private static void ReadRam(SystemInfo info)
    {
        long total = 0; int sticks = 0; int speed = 0;
        using var s = new ManagementObjectSearcher("select Capacity,Speed,ConfiguredClockSpeed from Win32_PhysicalMemory");
        foreach (ManagementObject mo in s.Get())
        {
            sticks++;
            total += (long)ToULong(mo["Capacity"]);
            var sp = ToInt(mo["ConfiguredClockSpeed"]);
            if (sp == 0) sp = ToInt(mo["Speed"]);
            speed = Math.Max(speed, sp);
        }
        info.Ram.TotalBytes = total;
        info.Ram.Sticks = sticks;
        info.Ram.SpeedMhz = speed;
        info.Ram.Channels = sticks >= 2 ? "Likely dual-channel" : sticks == 1 ? "Single-channel" : "";
    }

    private static void ReadDrives(SystemInfo info)
    {
        var sysDrive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
        // Physical disks: media type / bus / health, best-effort
        var physical = new List<(string friendly, string media, string bus, string health)>();
        try
        {
            var scope = new ManagementScope(@"\\.\ROOT\Microsoft\Windows\Storage");
            scope.Connect();
            using var s = new ManagementObjectSearcher(scope, new ObjectQuery("select FriendlyName,MediaType,BusType,HealthStatus from MSFT_PhysicalDisk"));
            foreach (ManagementObject mo in s.Get())
            {
                var media = ToInt(mo["MediaType"]) switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" };
                var bus = ToInt(mo["BusType"]) switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 8 => "RAID", _ => "" };
                var health = ToInt(mo["HealthStatus"]) switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" };
                physical.Add((Clean(mo["FriendlyName"]), media, bus, health));
            }
        }
        catch { }

        using var ld = new ManagementObjectSearcher("select DeviceID,VolumeName,Size,FreeSpace from Win32_LogicalDisk where DriveType=3");
        foreach (ManagementObject mo in ld.Get())
        {
            var d = new DriveInfo2
            {
                Letter = Clean(mo["DeviceID"]),
                Label = Clean(mo["VolumeName"]),
                TotalBytes = (long)ToULong(mo["Size"]),
                FreeBytes = (long)ToULong(mo["FreeSpace"]),
            };
            d.IsSystemDrive = d.Letter.Equals(sysDrive, StringComparison.OrdinalIgnoreCase);
            d.MediaType = "Unknown"; d.Health = "Unknown";
            info.Drives.Add(d);
        }
        if (physical.Count == 1)
        {
            foreach (var d in info.Drives) { d.MediaType = LabelOf(physical[0]); d.Health = physical[0].health; }
        }
        else if (physical.Count > 1)
        {
            var best = physical.FirstOrDefault(p => p.bus == "NVMe" || p.media == "SSD");
            var sys = info.Drives.FirstOrDefault(x => x.IsSystemDrive);
            if (sys != null && best.friendly != null) { sys.MediaType = LabelOf(best); sys.Health = best.health; }
        }
    }

    private static string LabelOf((string friendly, string media, string bus, string health) p)
        => p.bus == "NVMe" ? "NVMe SSD" : p.media == "SSD" ? "SATA SSD" : p.media == "HDD" ? "HDD" : "Unknown";

    private static void ReadOs(SystemInfo info)
    {
        using var s = new ManagementObjectSearcher("select Caption,BuildNumber,LastBootUpTime from Win32_OperatingSystem");
        foreach (ManagementObject mo in s.Get())
        {
            info.Os.Name = Clean(mo["Caption"]).Replace("Microsoft ", "");
            info.Os.Build = Clean(mo["BuildNumber"]);
            try
            {
                var boot = ManagementDateTimeConverter.ToDateTime(Clean(mo["LastBootUpTime"]));
                info.Os.UptimeHours = (int)(DateTime.Now - boot).TotalHours;
            }
            catch { }
            break;
        }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            info.Os.DisplayVersion = k?.GetValue("DisplayVersion") as string ?? "";
            info.Os.Edition = k?.GetValue("EditionID") as string ?? "";
        }
        catch { }
    }

    private static void ReadBoard(SystemInfo info)
    {
        using var s = new ManagementObjectSearcher("select Manufacturer,Product from Win32_BaseBoard");
        foreach (ManagementObject mo in s.Get())
        {
            var man = Clean(mo["Manufacturer"]);
            if (man.Contains("Default string", StringComparison.OrdinalIgnoreCase)) man = "";
            info.Motherboard = (man + " " + Clean(mo["Product"])).Trim();
            break;
        }
    }

    private static void ReadPowerPlan(SystemInfo info)
    {
        var outp = RunCapture("powercfg", "/getactivescheme", 4000);
        var m = Regex.Match(outp, @"GUID:\s*([0-9a-fA-F\-]{36})\s*\((.*?)\)");
        if (m.Success) { info.ActivePowerPlanGuid = m.Groups[1].Value; info.ActivePowerPlan = m.Groups[2].Value; }
    }

    private static bool DetectLaptop()
    {
        using var s = new ManagementObjectSearcher("select PCSystemType from Win32_ComputerSystem");
        foreach (ManagementObject mo in s.Get())
            return ToInt(mo["PCSystemType"]) == 2; // 2 = Mobile
        return false;
    }

    internal static string RunCapture(string exe, string args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            string o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } }
            return o;
        }
        catch { return ""; }
    }

    private static string Clean(object? o) => (o?.ToString() ?? "").Trim();
    private static int ToInt(object? o) { try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; } }
    private static ulong ToULong(object? o) { try { return Convert.ToUInt64(o, CultureInfo.InvariantCulture); } catch { return 0; } }
}
