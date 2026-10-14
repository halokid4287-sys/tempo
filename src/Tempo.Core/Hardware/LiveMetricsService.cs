using System.Diagnostics;
using System.Runtime.InteropServices;
using Tempo.Core.Models;

namespace Tempo.Core.Hardware;

/// <summary>
/// Lightweight live metrics: CPU, RAM, GPU utilization. Uses performance counters
/// (same sources Task Manager uses). Designed to be polled ~1x/second.
/// </summary>
public class LiveMetricsService : IDisposable
{
    private PerformanceCounter? _cpu;
    private List<PerformanceCounter> _gpuEngines = new();
    private List<PerformanceCounter> _vramCounters = new();
    private DateTime _gpuListRefreshed = DateTime.MinValue;
    private long _vramTotal;

    public LiveMetricsService(long vramTotalBytes)
    {
        _vramTotal = vramTotalBytes;
        try
        {
            // "% Processor Utility" matches Task Manager on modern CPUs; fall back to classic counter.
            _cpu = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
            _cpu.NextValue();
        }
        catch
        {
            try { _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true); _cpu.NextValue(); }
            catch { _cpu = null; }
        }
    }

    public LiveMetrics Sample()
    {
        var m = new LiveMetrics();
        try { m.CpuPercent = Math.Clamp(_cpu?.NextValue() ?? 0, 0, 100); } catch { }

        try
        {
            var mem = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(mem))
            {
                m.RamTotalBytes = (long)mem.ullTotalPhys;
                m.RamUsedBytes = (long)(mem.ullTotalPhys - mem.ullAvailPhys);
                m.RamPercent = mem.dwMemoryLoad;
            }
        }
        catch { }

        try { m.GpuPercent = SampleGpu(); } catch { }
        try
        {
            m.VramTotalBytes = _vramTotal;
            m.VramUsedBytes = SampleVram();
        }
        catch { }
        try
        {
            var ps = Process.GetProcesses();
            m.ProcessCount = ps.Length;
            foreach (var p in ps) p.Dispose(); // avoid handle buildup — this samples every second
        }
        catch { }
        return m;
    }

    private double SampleGpu()
    {
        // GPU Engine instances come and go with processes; refresh the list periodically.
        if ((DateTime.Now - _gpuListRefreshed).TotalSeconds > 10)
        {
            foreach (var c in _gpuEngines) c.Dispose();
            _gpuEngines.Clear();
            try
            {
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var inst in cat.GetInstanceNames())
                {
                    // 3D engine carries game rendering load; include copy/compute for completeness of "busy".
                    if (inst.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
                        inst.EndsWith("engtype_Graphics_1", StringComparison.OrdinalIgnoreCase) ||
                        inst.EndsWith("engtype_Compute_0", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, true);
                            pc.NextValue();
                            _gpuEngines.Add(pc);
                        }
                        catch { }
                    }
                }
            }
            catch { }
            _gpuListRefreshed = DateTime.Now;
            return 0; // first sample after refresh is meaningless
        }
        double sum = 0;
        foreach (var c in _gpuEngines)
        {
            try { sum += c.NextValue(); } catch { }
        }
        return Math.Clamp(sum, 0, 100);
    }

    private long SampleVram()
    {
        if (_vramCounters.Count == 0)
        {
            try
            {
                var cat = new PerformanceCounterCategory("GPU Adapter Memory");
                foreach (var inst in cat.GetInstanceNames())
                {
                    try
                    {
                        var pc = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", inst, true);
                        pc.NextValue();
                        _vramCounters.Add(pc);
                    }
                    catch { }
                }
            }
            catch { }
        }
        long max = 0;
        foreach (var c in _vramCounters)
        {
            try { max = Math.Max(max, (long)c.NextValue()); } catch { }
        }
        return max;
    }

    public void Dispose()
    {
        _cpu?.Dispose();
        foreach (var c in _gpuEngines) c.Dispose();
        foreach (var c in _vramCounters) c.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX() { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
