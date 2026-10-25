using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TopNotch
{
    internal sealed class PinnedFile : INotifyPropertyChanged
    {
        private double _x;
        private double _y;
        private bool _isDragging;

        public string Path { get; init; } = string.Empty;

        public double X
        {
            get => _x;
            set
            {
                if (_x == value) return;
                _x = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(X)));
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                if (_y == value) return;
                _y = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Y)));
            }
        }

        [JsonIgnore]
        public string Name => System.IO.Path.GetFileName(Path);

        [JsonIgnore]
        public ImageSource? Preview { get; init; }

        [JsonIgnore]
        public bool HasPreview => Preview is not null;

        [JsonIgnore]
        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : string.Empty;

        [JsonIgnore]
        public bool IsDragging
        {
            get => _isDragging;
            set
            {
                if (_isDragging == value) return;
                _isDragging = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragging)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static PinnedFile From(string path) => new() { Path = path, Preview = PreviewFor(path) };

        private static ImageSource? PreviewFor(string path)
        {
            try
            {
                return Picture(path, SIIGBF.SIIGBF_THUMBNAILONLY)
                    ?? Picture(path, SIIGBF.SIIGBF_ICONONLY);
            }
            catch (Exception e) when (e is COMException or InvalidCastException or IOException
                                        or UnauthorizedAccessException or ArgumentException)
            {
                return null;
            }
        }

        private static ImageSource? Picture(string path, SIIGBF kind)
        {
            object? item = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                var iid = IID_IShellItemImageFactory;
                if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item) != 0
                    || item is not IShellItemImageFactory factory)
                    return null;

                var size = new SIZE { cx = PictureSize, cy = PictureSize };
                if (factory.GetImage(size, kind | SIIGBF.SIIGBF_RESIZETOFIT, out bitmap) != 0
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
                if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            }
        }

        private const int PictureSize = 96;

        private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        [Flags]
        private enum SIIGBF : uint
        {
            SIIGBF_THUMBNAILONLY = 0x08,
            SIIGBF_ICONONLY = 0x04,
            SIIGBF_RESIZETOFIT = 0x20,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
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

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr bitmap);
    }

    internal sealed class Pinboard
    {
        public const double FileSize = 44;

        private const double Cascade = 9;

        private static readonly JsonSerializerOptions Format = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static Pinboard Current { get; } = Load();

        public static string FilePath => Path.Combine(Settings.Folder, "clipboard.json");

        public ObservableCollection<PinnedFile> Files { get; } = new();

        private double _width;
        private double _height;

        public void Resize(double width, double height)
        {
            if (width <= 0 || height <= 0) return;

            _width = width;
            _height = height;
        }

        public bool HoldAll()
        {
            bool moved = false;
            foreach (var file in Files) moved |= Hold(file);

            return moved && Save();
        }

        public bool Add(IReadOnlyList<string> paths, double x, double y)
        {
            int before = Files.Count;

            for (int i = 0; i < paths.Count; i++)
            {
                if (!File.Exists(paths[i])) continue;

                if (Has(paths[i])) continue;

                var file = PinnedFile.From(paths[i]);
                file.X = Hold(_width, Math.Round(x + i * Cascade - FileSize / 2));
                file.Y = Hold(_height, Math.Round(y + i * Cascade - FileSize / 2));
                Files.Add(file);
            }

            if (Files.Count == before) return false;
            return Save();
        }

        public bool Has(string path) =>
            Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));

        public void Move(PinnedFile file, double x, double y)
        {
            file.X = Hold(_width, Math.Round(x));
            file.Y = Hold(_height, Math.Round(y));
        }

        public bool Remove(PinnedFile file)
        {
            if (!Files.Remove(file)) return false;
            return Save();
        }

        private bool Hold(PinnedFile file)
        {
            double x = Hold(_width, file.X);
            double y = Hold(_height, file.Y);
            if (x == file.X && y == file.Y) return false;

            file.X = x;
            file.Y = y;
            return true;
        }

        private static double Hold(double extent, double place)
        {
            if (extent <= 0) return place;
            return Math.Clamp(place, 0, Math.Max(0, extent - FileSize));
        }

        private static Pinboard Load()
        {
            var board = new Pinboard();
            List<PinnedFile>? stored = null;
            bool found = File.Exists(FilePath);

            try
            {
                if (found)
                    stored = JsonSerializer.Deserialize<List<PinnedFile>>(File.ReadAllText(FilePath), Format);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
            }

            foreach (var held in stored ?? [])
            {
                if (held is not { Path.Length: > 0 } || !File.Exists(held.Path)) continue;

                var file = PinnedFile.From(held.Path);
                file.X = held.X;
                file.Y = held.Y;
                board.Files.Add(file);
            }

            if (!found) board.Save();
            return board;
        }

        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Files, Format));
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
