using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace TopNotch;

public class SystemMonitor : IDisposable
{
    private const int PollIntervalMs = 1000;

    private Timer? _timer;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _primed;

    public event Action<double, double>? StatsChanged;

    public double CpuPercent { get; private set; }
    public double MemoryPercent { get; private set; }
    public ulong TotalPhysicalBytes { get; private set; }
    public ulong AvailablePhysicalBytes { get; private set; }

    public void Init()
    {
        ReadCpu();
        _timer = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    }

    private void Poll()
    {
        try
        {
            double cpu = ReadCpu();
            if (cpu < 0) return;

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
        }
    }

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
        ulong totalDelta = (kernel - _prevKernel) + (user - _prevUser);

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        if (totalDelta == 0) return -1;

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

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
