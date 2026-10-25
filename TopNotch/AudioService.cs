using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;

namespace TopNotch;

public class AudioService : IDisposable, AudioService.IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private readonly object _lock = new();
    private readonly object _attachLock = new();
    private CancellationTokenSource? _attachCts;

    [Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.U4)] uint newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        void OnDefaultDeviceChanged(DataFlow flow, Role role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, PropertyKey key);
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(DataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IntPtr ppEndpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);
        int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    private IMMDeviceEnumerator? _comEnumerator;

    public event Action<float>? VolumeChanged;
    public event Action<string, bool>? DeviceChanged;

    public string? DefaultDeviceName
    {
        get
        {
            lock (_lock) return _device?.FriendlyName;
        }
    }

    public bool IsCurrentDeviceBluetooth
    {
        get
        {
            lock (_lock) return _device != null && IsBluetoothDevice(_device);
        }
    }

    public static bool IsBluetoothDevice(MMDevice device)
    {
        try
        {
            for (int i = 0; i < device.Properties.Count; i++)
            {
                var key = device.Properties.Get(i);
                if (key.formatId == new Guid("a45c254e-df1c-4efd-8020-67d146a850e0") && key.propertyId == 24)
                {
                    string? val = device.Properties[key].Value?.ToString();
                    if (!string.IsNullOrEmpty(val) && val.StartsWith("BTH", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            for (int i = 0; i < device.Properties.Count; i++)
            {
                try
                {
                    var key = device.Properties.Get(i);
                    string? val = device.Properties[key].Value?.ToString();
                    if (!string.IsNullOrEmpty(val) &&
                        (val.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                         val.Contains("BTHHFENUM", StringComparison.OrdinalIgnoreCase) ||
                         val.Contains("BthA2dp", StringComparison.OrdinalIgnoreCase) ||
                         val.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    public void Init()
    {
        try
        {
            _comEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _comEnumerator.RegisterEndpointNotificationCallback(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to register endpoint callback: {ex}");
        }

        Attach();
    }

    private void ScheduleAttach()
    {
        lock (_attachLock)
        {
            _attachCts?.Cancel();
            _attachCts = new CancellationTokenSource();
            var token = _attachCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(60, token);
                    if (!token.IsCancellationRequested)
                    {
                        Attach();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ScheduleAttach failed: {ex}");
                }
            });
        }
    }

    private void Attach()
    {
        lock (_lock)
        {
            if (_device != null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
                try { _device.Dispose(); } catch { }
                _device = null;
            }

            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var vol = _device.AudioEndpointVolume;
                vol.OnVolumeNotification += OnVolume;
                VolumeChanged?.Invoke(vol.Mute ? 0f : vol.MasterVolumeLevelScalar);
                DeviceChanged?.Invoke(_device.FriendlyName, IsBluetoothDevice(_device));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Attach failed: {ex}");
            }
        }
    }

    private void OnVolume(AudioVolumeNotificationData data)
        => VolumeChanged?.Invoke(data.Muted ? 0f : data.MasterVolume);

    public void SetVolume(float level)
    {
        lock (_lock)
        {
            if (_device == null) return;
            try
            {
                var vol = _device.AudioEndpointVolume;
                level = Math.Clamp(level, 0f, 1f);
                if (level > 0f && vol.Mute) vol.Mute = false;
                vol.MasterVolumeLevelScalar = level;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetVolume failed: {ex}");
            }
        }
    }

    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && (role == Role.Multimedia || role == Role.Console))
        {
            ScheduleAttach();
        }
    }

    public void OnDeviceStateChanged(string deviceId, uint newState)
    {
        ScheduleAttach();
    }

    public void OnDeviceAdded(string deviceId)
    {
        ScheduleAttach();
    }

    public void OnDeviceRemoved(string deviceId)
    {
        ScheduleAttach();
    }

    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        try
        {
            _comEnumerator?.UnregisterEndpointNotificationCallback(this);
        }
        catch { }

        lock (_lock)
        {
            if (_device != null)
            {
                try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume; } catch { }
                try { _device.Dispose(); } catch { }
                _device = null;
            }
        }
        _enumerator.Dispose();
    }
}