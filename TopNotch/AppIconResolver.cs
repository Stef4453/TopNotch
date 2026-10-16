using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TopNotch
{
    /// <summary>
    /// Resolves the icon shown next to a notification.
    /// <para>
    /// The notification API only hands back a logo for packaged apps; ordinary Win32 apps report
    /// none. When that happens we work from the app's AUMID instead, the same way the shell does:
    /// an icon the app registered under <c>AppUserModelId</c>, a path-like AUMID, or the entry the
    /// shell lists for it in <c>shell:AppsFolder</c>.
    /// </para>
    /// </summary>
    internal static class AppIconResolver
    {
        /// <summary>Returns PNG bytes for the app's icon, or null if none can be found.</summary>
        public static byte[]? ResolvePng(string? aumid)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(aumid)) return null;

                // 1. An icon the app registered for its AUMID.
                var registered = LoadIconFromPath(ReadRegisteredIconUri(aumid));
                if (registered != null) return registered;

                // 2. An AUMID that is literally an executable path.
                if (Path.IsPathRooted(aumid) && File.Exists(aumid))
                {
                    var exe = LoadIconFromPath(aumid);
                    if (exe != null) return exe;
                }

                // 3. Anything the shell lists as an installed app — this is how the Action Center
                //    itself gets an icon for an unpackaged app.
                return ExtractAppsFolderIconPng(aumid);
            }
            catch
            {
                return null;
            }
        }

        private static string? ReadRegisteredIconUri(string aumid)
        {
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey($@"Software\Classes\AppUserModelId\{aumid}");
                    if (key?.GetValue("IconUri") is string iconUri && !string.IsNullOrWhiteSpace(iconUri))
                        return iconUri;
                }
                catch
                {
                    // Ignore and try the next hive.
                }
            }

            return null;
        }

        private static byte[]? LoadIconFromPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            path = path.Trim().Trim('"');

            // Icon references are often "file,index"; keep the file when it exists.
            int comma = path.LastIndexOf(',');
            if (comma > 2 && File.Exists(path[..comma])) path = path[..comma];

            if (!File.Exists(path)) return null;

            string ext = Path.GetExtension(path).ToLowerInvariant();

            // Image files can be handed to the decoder as-is.
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".ico" or ".gif")
            {
                try { return File.ReadAllBytes(path); } catch { return null; }
            }

            return ExtractShellIconPng(path);
        }

        private static byte[]? ExtractShellIconPng(string path)
        {
            var info = new SHFILEINFO();
            var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                return ToPng(source);
            }
            catch
            {
                return null;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }

        private static byte[]? ExtractAppsFolderIconPng(string aumid)
        {
            object? item = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                var iid = IID_IShellItem;
                if (SHCreateItemFromParsingName($@"shell:AppsFolder\{aumid}", IntPtr.Zero, ref iid, out item) != 0)
                    return null;

                if (item is not IShellItemImageFactory factory) return null;

                var size = new SIZE { cx = 48, cy = 48 };
                if (factory.GetImage(size, SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, out hbm) != 0 || hbm == IntPtr.Zero)
                    return null;

                // Icons come back as a bitmap; on the black island any lost alpha is invisible.
                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                return ToPng(source);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hbm != IntPtr.Zero) DeleteObject(hbm);
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }

        private static byte[]? ToPng(BitmapSource source)
        {
            source.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }

        // ---- Interop ----

        private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        private const uint SHGFI_ICON = 0x000000100;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        [Flags]
        private enum SIIGBF : uint
        {
            SIIGBF_BIGGERSIZEOK = 0x01,
            SIIGBF_ICONONLY = 0x04,
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
    }
}
