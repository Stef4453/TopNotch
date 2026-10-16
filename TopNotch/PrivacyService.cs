using System;
using System.Threading;
using Microsoft.Win32;

namespace TopNotch;

// Reports whether the microphone and/or camera are currently in use by any app,
// by reading the Windows capability-access consent store. A device counts as "in
// use" when its most recent LastUsedTimeStart is set but LastUsedTimeStop is still 0.
public class PrivacyService : IDisposable
{
    private const string ConsentStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    // How often to re-check the consent store (ms).
    private const int PollIntervalMs = 500;

    private Timer? _timer;

    public event Action<bool, bool>? StateChanged;   // micActive, cameraActive

    public bool MicActive { get; private set; }
    public bool CameraActive { get; private set; }

    public void Init()
    {
        Poll();   // prime the current state before the timer starts
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
            // The consent store can be transiently locked while an app opens/closes;
            // ignore and try again on the next tick.
        }
    }

    private static bool IsCategoryInUse(string category)
    {
        using var root = Registry.CurrentUser.OpenSubKey($@"{ConsentStorePath}\{category}");
        return root != null && AnySubKeyInUse(root);
    }

    // Layout is category -> app (or "NonPackaged") -> exe entry, so recurse a few levels.
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
        // Both values are REG_QWORD. Never used => both zero => not active.
        if (key.GetValue("LastUsedTimeStart") is not long start || start == 0) return false;
        return key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
