using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TopNotch;

// Samples overall CPU load and physical memory usage for the Home tab.
//
// CPU comes from GetSystemTimes, which hands back cumulative idle/kernel/user time since
// boot: the load is the busy share of the time that elapsed between two samples, so the
// first reading only primes the counters. Memory comes from GlobalMemoryStatusEx, which
// already reports a percentage.
public class SystemMonitor : IDisposable
{
    private const int PollIntervalMs = 1000;

    private Timer? _timer;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _primed;

    public event Action<double, double>? StatsChanged;   // cpuPercent, memoryPercent

    public double CpuPercent { get; private set; }
    public double MemoryPercent { get; private set; }
    public ulong TotalPhysicalBytes { get; private set; }
    public ulong AvailablePhysicalBytes { get; private set; }

    public void Init()
    {
        ReadCpu();   // prime the deltas; the first tick is what actually gets published
        _timer = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    }

    private void Poll()
    {
        try
        {
            double cpu = ReadCpu();
            if (cpu < 0) return;   // counters not ready yet

            CpuPercent = cpu;

            if (ReadMemory(out double mem, out ulong total, out ulong available))
            {
                MemoryPercent = mem;
                TotalPhysicalBytes = total;
                AvailablePhysicalBytes = available;
            }

            StatsChanged?.Invoke(CpuPercent, MemoryPercent);
        }
        catch
        {
            // Transient; the next tick will try again.
        }
    }

    // -1 when there's no usable delta yet.
    private double ReadCpu()
    {
        if (!GetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return -1;

        if (!_primed)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _primed = true;
            return -1;
        }

        ulong idleDelta = idle - _prevIdle;
        // Kernel time already includes the idle time, so total elapsed is kernel + user.
        ulong totalDelta = (kernel - _prevKernel) + (user - _prevUser);

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        if (totalDelta == 0) return -1;   // no time passed; nothing meaningful to report

        double busy = 1.0 - (double)idleDelta / totalDelta;
        return Math.Clamp(busy * 100.0, 0.0, 100.0);
    }

    private static bool ReadMemory(out double percent, out ulong total, out ulong available)
    {
        percent = 0;
        total = 0;
        available = 0;

        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status)) return false;

        percent = status.dwMemoryLoad;
        total = status.ullTotalPhys;
        available = status.ullAvailPhys;
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // ---- Win32 ----

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    // dwLength must be set before the call, so it is initialised inline.
    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
