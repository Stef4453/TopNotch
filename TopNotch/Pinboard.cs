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
    /// <summary>
    /// A file parked on the Clipboard tab, and the place on the board it was left.
    ///
    /// The board is free-form: a file sits where it was dropped and is dragged about from there, so
    /// a file here is a place as much as it is a thing. Its name and its picture are not kept
    /// beside it. Both are read back off the file, which is what keeps them current when it is
    /// renamed or edited, and what keeps the file on disk a list of places rather than of pictures.
    /// </summary>
    internal sealed class PinnedFile : INotifyPropertyChanged
    {
        private double _x;
        private double _y;
        private bool _isDragging;

        /// <summary>Where the file is. There is no empty space on the board - a file with no path
        /// is not on the board at all - so the path is always the whole of what one holds.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>Where on the board it was left, in the board's own pixels, measured from the
        /// file's top left corner.</summary>
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

        /// <summary>The name the tooltip shows: the file's own, the way the rest of Windows says
        /// it. Read off the path rather than kept, so that renaming a file renames its tile.</summary>
        [JsonIgnore]
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>The shell's picture of the file - its thumbnail where it has one, its icon
        /// where it has not - or null when it had neither to give.</summary>
        [JsonIgnore]
        public ImageSource? Preview { get; init; }

        [JsonIgnore]
        public bool HasPreview => Preview is not null;

        /// <summary>The letter a file shows when it had no picture to give.</summary>
        [JsonIgnore]
        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : string.Empty;

        /// <summary>True while this is the file being dragged, so that it can say so.</summary>
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

        /// <summary>Reads the file at a path and makes a board tile out of it.</summary>
        public static PinnedFile From(string path) => new() { Path = path, Preview = PreviewFor(path) };

        // The shell's picture of the file: its thumbnail where the shell has one - a photo, a frame
        // of a film, an album's cover - and its icon where it has not, since a text file or a
        // program has no picture of its own to give. Asked for at more than twice the size the
        // board draws it at, so that it stays sharp on the way down.
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

        // One pass of the above. The picture comes back as a bitmap rather than as an icon because
        // a thumbnail is not an icon: it is the file's own content, drawn small.
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

        // ---- Interop ----

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

    /// <summary>
    /// The files on the Clipboard tab, and the one place that knows where they are kept:
    /// %LOCALAPPDATA%\TopNotch\clipboard.json, beside the settings and the Launchpad and written
    /// the same way. The file is the list itself - a path and a place for each of them - because
    /// the list is all there is to it.
    ///
    /// The tab is called a clipboard and this is not one: nothing here is ever put on the system
    /// clipboard, and Windows has a class of that name already. What it is is a board to pin files
    /// to, which is the name it is kept under.
    /// </summary>
    internal sealed class Pinboard
    {
        /// <summary>How big a file is drawn. The board's one fixed measurement: a file is this
        /// square wherever it is put, which is what the board's edges are a file's width inside.</summary>
        public const double FileSize = 44;

        /// <summary>How far each file of a drop of several lands from the one before it, so that
        /// they come down in a small fan rather than in one pile.</summary>
        private const double Cascade = 9;

        private static readonly JsonSerializerOptions Format = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static Pinboard Current { get; } = Load();

        public static string FilePath => Path.Combine(Settings.Folder, "clipboard.json");

        /// <summary>The files on the board, in the order they were put there, which is also the
        /// order they are drawn in until one of them is dragged, which brings it to the front.</summary>
        public ObservableCollection<PinnedFile> Files { get; } = new();

        private double _width;
        private double _height;

        /// <summary>Tells the board how big it is. The panel is measured at every size on the way
        /// open and shut, so this only records the size: holding files against a measurement the
        /// panel is passing through would drag them up into the corner, and drag marks like that
        /// would be written down. Files are held when the board is really on show - see
        /// <see cref="HoldAll"/>.</summary>
        public void Resize(double width, double height)
        {
            if (width <= 0 || height <= 0) return;

            _width = width;
            _height = height;
        }

        /// <summary>Holds every file inside the board as it is now, and writes the board if any of
        /// them had to move. Called when the board comes into view, which is the one moment its
        /// measurement is the size it is really going to be - and the moment a place written down
        /// outside the board, or left beyond the edge of a smaller one, is brought back inside
        /// it.</summary>
        public bool HoldAll()
        {
            bool moved = false;
            foreach (var file in Files) moved |= Hold(file);

            return moved && Save();
        }

        /// <summary>Puts files on the board where they were let go, each a little off the last. The
        /// place given is where the pointer was, which is the middle of the file that appears
        /// there.</summary>
        public bool Add(IReadOnlyList<string> paths, double x, double y)
        {
            int before = Files.Count;

            for (int i = 0; i < paths.Count; i++)
            {
                if (!File.Exists(paths[i])) continue;

                // A file is on the board once, however it got there. The same file dropped again,
                // or a file carried out of the island and brought back to it, is already where it
                // belongs: a second tile of it would be a second place for it to be, and only one
                // of them could be right.
                if (Has(paths[i])) continue;

                var file = PinnedFile.From(paths[i]);
                file.X = Hold(_width, Math.Round(x + i * Cascade - FileSize / 2));
                file.Y = Hold(_height, Math.Round(y + i * Cascade - FileSize / 2));
                Files.Add(file);
            }

            if (Files.Count == before) return false;
            return Save();
        }

        /// <summary>Whether this file is already on the board.</summary>
        public bool Has(string path) =>
            Files.Any(file => string.Equals(file.Path, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>Puts a dragged file at a place on the board, held inside it. The place is the
        /// file's top left corner, worked out by the caller from where it was picked up.</summary>
        public void Move(PinnedFile file, double x, double y)
        {
            file.X = Hold(_width, Math.Round(x));
            file.Y = Hold(_height, Math.Round(y));
        }

        /// <summary>Takes a file off the board and writes the file.</summary>
        public bool Remove(PinnedFile file)
        {
            if (!Files.Remove(file)) return false;
            return Save();
        }

        // Held inside the board, which is what makes the board's edge the end of a drag rather than
        // a way to lose a file. Says whether the file had to be moved to be held.
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
            // Nothing has been measured yet, so nothing is outside anything.
            if (extent <= 0) return place;
            return Math.Clamp(place, 0, Math.Max(0, extent - FileSize));
        }

        // Whatever the file holds, the board gets files that are still there and nothing else: a
        // file that has gone leaves no tile, which is the tidier outcome - the alternative is a
        // tile that cannot be opened.
        //
        // Each stored file is read again from its path rather than taken as it was written, which
        // is what brings its picture back, since pictures are not kept in the file.
        //
        // A file that was not there at all is written on the way out, so that a fresh install
        // leaves one behind - an empty board - rather than one that appears only once something has
        // been dropped on it.
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

        /// <summary>Writes the file, creating %LOCALAPPDATA%\TopNotch on the way. False if it could
        /// not be written, and quietly: the board holds what it holds either way, and there is
        /// nothing the user could do about a file that will not save.</summary>
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
