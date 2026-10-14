using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Tempo.Core.Hardware;

namespace Tempo.Core.Optimizations;

/// <summary>powercfg helpers for per-plan sub-settings (with readable current values for backup).</summary>
public static class PowerCfg
{
    // Common GUIDs
    public const string SUB_PROCESSOR = "54533251-82be-4824-96c1-47b60b740d00";
    public const string PROC_MIN = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    public const string CORE_PARKING_MIN = "0cc5b647-c1df-4637-891a-dec35c318583";
    public const string SUB_USB = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string USB_SELECTIVE_SUSPEND = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    public const string SUB_PCIE = "501a4d13-42af-4429-9fd1-a8218c268e20";
    public const string PCIE_ASPM = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    public const string SUB_SLEEP = "238c9fa8-0aad-41ed-83f4-97be242c8f20";
    public const string STANDBY_IDLE = "29f6c1db-86da-48c5-9fdb-f2b67b1f44da";

    public static string Run(string args) => HardwareService.RunCapture("powercfg", args, 10000);

    public static string? ActiveSchemeGuid()
    {
        var m = Regex.Match(Run("/getactivescheme"), @"GUID:\s*([0-9a-fA-F\-]{36})");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Read current AC+DC indices of a setting on the active scheme. Null when unreadable.</summary>
    public static (uint? ac, uint? dc) Query(string subGuid, string settingGuid)
    {
        var o = Run("/q SCHEME_CURRENT " + subGuid + " " + settingGuid);
        var ac = Regex.Match(o, @"AC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        var dc = Regex.Match(o, @"DC Power Setting Index:\s*0x([0-9a-fA-F]+)");
        return (ac.Success ? Convert.ToUInt32(ac.Groups[1].Value, 16) : null,
                dc.Success ? Convert.ToUInt32(dc.Groups[1].Value, 16) : null);
    }

    public static void Set(string subGuid, string settingGuid, uint acValue, uint? dcValue = null)
    {
        Run($"/setacvalueindex SCHEME_CURRENT {subGuid} {settingGuid} {acValue}");
        Run($"/setdcvalueindex SCHEME_CURRENT {subGuid} {settingGuid} {dcValue ?? acValue}");
        Run("/setactive SCHEME_CURRENT");
    }

    /// <summary>Make a hidden attribute (like core parking) visible so it can be set/queried.</summary>
    public static void Unhide(string subGuid, string settingGuid)
        => Run($"-attributes {subGuid} {settingGuid} -ATTRIB_HIDE");
}

/// <summary>Display mode enumeration + refresh rate switching (reversible).</summary>
public static class DisplayUtil
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettings(ref DEVMODE devMode, uint dwFlags);

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x01;
    private const uint DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

    public static (int w, int h, int hz) Current()
    {
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            return ((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
        return (0, 0, 0);
    }

    /// <summary>Highest refresh rate the primary display supports at its CURRENT resolution.</summary>
    public static int MaxRefreshAtCurrentResolution()
    {
        var (w, h, _) = Current();
        if (w == 0) return 0;
        int max = 0;
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        for (int i = 0; EnumDisplaySettings(null, i, ref dm); i++)
            if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && dm.dmDisplayFrequency > max)
                max = (int)dm.dmDisplayFrequency;
        return max;
    }

    /// <summary>Switch the primary display's refresh rate (same resolution). Returns error text or null.</summary>
    public static string? SetRefresh(int hz)
    {
        var (w, h, cur) = Current();
        if (w == 0) return "Could not read current display mode.";
        if (hz == cur) return null;
        var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm)) return "Could not read current display mode.";
        dm.dmDisplayFrequency = (uint)hz;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettings(ref dm, CDS_UPDATEREGISTRY);
        return r == 0 ? null : "Display rejected the mode (code " + r + ") — no change was made.";
    }
}
