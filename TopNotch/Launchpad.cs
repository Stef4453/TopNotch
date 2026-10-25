using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TopNotch
{
    internal sealed class LaunchpadSlot : INotifyPropertyChanged
    {
        private bool _isDropTarget;

        public string Path { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        [JsonIgnore]
        public ImageSource? Icon { get; init; }

        [JsonIgnore]
        public bool IsEmpty => Path.Length == 0;

        [JsonIgnore]
        public bool HasIcon => Icon is not null;

        [JsonIgnore]
        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : string.Empty;

        [JsonIgnore]
        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                if (_isDropTarget == value) return;
                _isDropTarget = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static LaunchpadSlot From(string path)
        {
            if (Shortcut.Target(path) is { } target)
            {
                return new()
                {
                    Path = target,
                    Name = NameFor(target),
                    Icon = IconFor(target),
                };
            }

            var store = Shortcut.Store(path);

            return new()
            {
                Path = path,
                Name = store?.Name ?? NameFor(path),
                Icon = store?.Icon ?? IconFor(path),
            };
        }

        private static string NameFor(string path)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                if (version.FileDescription is { Length: > 0 } description) return description;
                if (version.ProductName is { Length: > 0 } product) return product;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
            }

            return System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private static ImageSource? IconFor(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is null) return null;

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }
    }

    internal sealed class Launchpad
    {
        public const int SlotCount = 2 * 8;

        private static readonly JsonSerializerOptions Format = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static Launchpad Current { get; } = Load();

        public static string FilePath => Path.Combine(Settings.Folder, "launchpad.json");

        public ObservableCollection<LaunchpadSlot> Slots { get; } = new();

        public bool Place(int index, string path)
        {
            if (index < 0 || index >= SlotCount) return false;

            Slots[index] = LaunchpadSlot.From(path);
            return Save();
        }

        public bool Clear(int index)
        {
            if (index < 0 || index >= SlotCount) return false;

            Slots[index] = new LaunchpadSlot();
            return Save();
        }

        private static Launchpad Load()
        {
            var launchpad = new Launchpad();
            LaunchpadSlot?[]? stored = null;
            bool found = File.Exists(FilePath);

            try
            {
                if (found)
                    stored = JsonSerializer.Deserialize<LaunchpadSlot?[]>(File.ReadAllText(FilePath), Format);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            bool caughtUp = false;

            for (int i = 0; i < SlotCount; i++)
            {
                LaunchpadSlot? slot = stored is not null && i < stored.Length ? stored[i] : null;
                if (slot is not { Path.Length: > 0 } || !File.Exists(slot.Path))
                {
                    launchpad.Slots.Add(new LaunchpadSlot());
                    continue;
                }

                var fresh = LaunchpadSlot.From(slot.Path);
                caughtUp |= !string.Equals(fresh.Path, slot.Path, StringComparison.OrdinalIgnoreCase);

                launchpad.Slots.Add(fresh);
            }

            if (!found || caughtUp) launchpad.Save();
            return launchpad;
        }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);

                LaunchpadSlot?[] stored = Slots
                    .Select(slot => slot.IsEmpty ? null : slot)
                    .ToArray();

                File.WriteAllText(FilePath, JsonSerializer.Serialize(stored, Format));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    internal static class Shortcut
    {
        internal static string? Target(string path)
        {
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            object? link = null;
            try
            {
                link = new ShellLink();
                ((IPersistFile)link).Load(path, 0);

                var target = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);

                string resolved = target.ToString();
                return File.Exists(resolved) ? resolved : null;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or IOException
                                        or UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            }
        }

        internal sealed record StoreApp(string? Name, ImageSource? Icon);

        internal static StoreApp? Store(string path)
        {
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            object? link = null;
            object? shellItem = null;
            try
            {
                link = new ShellLink();
                ((IPersistFile)link).Load(path, 0);

                var target = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (target.Length > 0) return null;

                string app = IdList(link);
                var iid = IID_IShellItem;
                if (app.Length == 0
                    || SHCreateItemFromParsingName($@"shell:AppsFolder\{app}", IntPtr.Zero, ref iid,
                                                   out shellItem) != 0
                    || shellItem is not IShellItem item)
                    return null;

                string? name = DisplayName(item);
                ImageSource? icon = Picture(item);

                return name is null && icon is null ? null : new StoreApp(name, icon);
            }
            catch (Exception e) when (e is COMException or InvalidCastException or IOException
                                        or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
            finally
            {
                if (shellItem is not null && Marshal.IsComObject(shellItem)) Marshal.ReleaseComObject(shellItem);
                if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            }
        }

        private static string IdList(object link)
        {
            IntPtr list = IntPtr.Zero;
            IntPtr text = IntPtr.Zero;
            try
            {
                ((IShellLinkW)link).GetIDList(out list);
                if (SHGetNameFromIDList(list, SIGDN_PARENTRELATIVEPARSING, out text) != 0 || text == IntPtr.Zero)
                    return string.Empty;

                return Marshal.PtrToStringUni(text) ?? string.Empty;
            }
            finally
            {
                if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
                if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list);
            }
        }

        private static string? DisplayName(IShellItem item)
        {
            IntPtr text = IntPtr.Zero;
            try
            {
                item.GetDisplayName(SIGDN_NORMALDISPLAY, out text);
                string? name = Marshal.PtrToStringUni(text);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            finally
            {
                if (text != IntPtr.Zero) Marshal.FreeCoTaskMem(text);
            }
        }

        private static ImageSource? Picture(IShellItem item)
        {
            if (item is not IShellItemImageFactory factory) return null;

            IntPtr bitmap = IntPtr.Zero;
            try
            {
                var size = new SIZE { cx = 48, cy = 48 };
                if (factory.GetImage(size, SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK,
                                     out bitmap) != 0
                    || bitmap == IntPtr.Zero)
                    return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            }
        }

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int length,
                         IntPtr findData, uint flags);

            void GetIDList(out IntPtr idList);
            void SetIDList(IntPtr idList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int length);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int length);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int length);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCmd);
            void SetShowCmd(int showCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath,
                                 int length, out int icon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010B-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid classID);

            [PreserveSig]
            int IsDirty();

            void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string? file, bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
        }

        private const int SIGDN_NORMALDISPLAY = 0;
        private const int SIGDN_PARENTRELATIVEPARSING = unchecked((int)0x80018001);

        private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        [Flags]
        private enum SIIGBF : uint
        {
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_ICONONLY = 0x04,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        private interface IShellItem
        {
            void BindToHandler(IntPtr context, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(int kind, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr bitmap);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context, ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out object result);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetNameFromIDList(IntPtr list, int kind, out IntPtr name);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr bitmap);
    }
}
