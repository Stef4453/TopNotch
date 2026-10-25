using System;
using System.Threading;
using Microsoft.Win32;

namespace TopNotch;

public class PrivacyService : IDisposable
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private const int PollIntervalMs = 500;

    private Timer? _timer;

    public event Action<bool, bool>? StateChanged;

    public bool MicActive { get; private set; }
    public bool CameraActive { get; private set; }

    public void Init()
    {
        Poll();
        _timer = new Timer(_ => Poll(), null, PollIntervalMs, PollIntervalMs);
    }

    private void Poll()
    {
        try
        {
            bool mic = IsCategoryInUse("microphone");
            bool camera = IsCategoryInUse("webcam");

            if (mic == MicActive && camera == CameraActive) return;

            MicActive = mic;
            CameraActive = camera;
            StateChanged?.Invoke(mic, camera);
        }
        catch
        {
        }
    }

    private static bool IsCategoryInUse(string category)
    {
        using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStorePath}\{category}");
        return root != null && AnySubKeyInUse(root);
    }

    private static bool AnySubKeyInUse(RegistryKey parent, int depth = 0)
    {
        if (depth > 3) return false;

        foreach (var name in parent.GetSubKeyNames())
        {
            using var child = parent.OpenSubKey(name);
            if (child == null) continue;

            if (IsInUse(child) || AnySubKeyInUse(child, depth + 1))
                return true;
        }
        return false;
    }

    private static bool IsInUse(RegistryKey key)
    {
        if (key.GetValue("LastUsedTimeStart") is not long start || start == 0) return false;
        return key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
