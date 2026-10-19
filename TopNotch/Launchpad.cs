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
    /// <summary>
    /// One tile of the Launchpad: an app the user put there, or nothing at all.
    ///
    /// A slot is a value. Filling one builds a new one and puts it in the grid's place, so the
    /// list on screen and the list on disk are never two things that have to be kept in step. The
    /// one piece of state that changes after the fact is whether a dragged file is being held over
    /// the tile, which is a fact about the moment rather than about the app - and is the only
    /// thing here that announces itself.
    /// </summary>
    internal sealed class LaunchpadSlot : INotifyPropertyChanged
    {
        private bool _isDropTarget;

        /// <summary>Where the app is. Empty on an empty tile, which is the only thing that says a
        /// tile is empty - there is no second flag to fall out of step with it.</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>What the tile is called, for the pointer's tooltip. Read off the file when the
        /// app was dropped here, so the grid can be drawn from the file alone.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>The app's own icon, or null when its file has none to give. Kept out of the
        /// file: it is derived from <see cref="Path"/>, it would be the bulk of everything a slot
        /// knows, and it would be the stale part the moment the app updated itself.</summary>
        [JsonIgnore]
        public ImageSource? Icon { get; init; }

        [JsonIgnore]
        public bool IsEmpty => Path.Length == 0;

        [JsonIgnore]
        public bool HasIcon => Icon is not null;

        /// <summary>The letter a filled tile shows when its file had no icon to give.</summary>
        [JsonIgnore]
        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : string.Empty;

        /// <summary>True while a file is held over this tile, so that the tile can say so.</summary>
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

        /// <summary>Reads the app at a path and makes a tile out of what it finds there. A shortcut
        /// is looked through first, so that a tile is the app itself rather than a link to it - and
        /// where there is nothing to look through to, because the app has no file of its own, the
        /// shell is asked what to call it and what it looks like, which is what spares its tile the
        /// arrow a shortcut file is drawn with.</summary>
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

        // The name the rest of Windows shows for the file. Its description comes first because it
        // is the one written for people ("Notepad", "Google Chrome"), where the product name is
        // often the suite the program belongs to - notepad.exe calls itself, in full, "Microsoft®
        // Windows® Operating System". A shortcut carries no version resource of its own, so it
        // falls through to its file name, which is what it was named after anyway.
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

        // The shell's own icon for the file. The bitmap is copied off the icon handle before the
        // handle is disposed and then frozen, so that a tile can bind to it without holding on to
        // anything of the app's.
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

    /// <summary>
    /// The apps in the Launchpad, and the one place that knows where they are kept:
    /// %LOCALAPPDATA%\TopNotch\launchpad.json, beside the settings and written the same way -
    /// plain, indented, and by hand if you would rather. The file is the list itself rather than
    /// an object wrapping it, because the list is all there is to it.
    /// </summary>
    internal sealed class Launchpad
    {
        /// <summary>Two rows of eight, which is what the panel holds without crowding it. The grid
        /// derives its columns from the number of tiles it is given, so this is the only place the
        /// size of the panel is written down.</summary>
        public const int SlotCount = 2 * 8;

        private static readonly JsonSerializerOptions Format = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
        };

        public static Launchpad Current { get; } = Load();

        public static string FilePath => Path.Combine(Settings.Folder, "launchpad.json");

        /// <summary>The tiles, in the order they are laid out. An empty tile is a slot with nothing
        /// in it rather than a missing entry, so every binding in the grid has something to read.</summary>
        public ObservableCollection<LaunchpadSlot> Slots { get; } = new();

        /// <summary>Puts an app in a tile, replacing whatever was there, and writes the file.</summary>
        public bool Place(int index, string path)
        {
            if (index < 0 || index >= SlotCount) return false;

            Slots[index] = LaunchpadSlot.From(path);
            return Save();
        }

        /// <summary>Takes the app out of a tile and writes the file.</summary>
        public bool Clear(int index)
        {
            if (index < 0 || index >= SlotCount) return false;

            Slots[index] = new LaunchpadSlot();
            return Save();
        }

        // Whatever the file holds, the panel gets exactly the tiles it has room for: it may have
        // been a different size when the file was written, and an entry that is not a slot at all
        // is an empty tile rather than a reason to give up on the rest of them.
        //
        // Each stored app is read again from its path rather than taken as it was written, which is
        // what brings its icon back - icons are not kept in the file - and what notices an app that
        // has been renamed since. An app whose file has gone leaves an empty tile, which is both
        // honest about it and the tidier outcome: the alternative is a tile that cannot be opened.
        //
        // Reading them again is also what looks a stored shortcut through to its app. A file written
        // before that happened says .lnk where the tile now holds the app itself, so it is written
        // again afterwards if anything came out different: the file should say what the grid holds.
        //
        // Which is one of two reasons to write the file on the way out. The other is that there was
        // none to read: a fresh install leaves one behind - sixteen empty tiles - so that what the
        // grid holds is a file you can open and fill in by hand instead of one that appears only
        // once something has been dropped.
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

        /// <summary>Writes the file, creating %LOCALAPPDATA%\TopNotch on the way. False if it could
        /// not be written, and quietly: the change is live either way, and there is nothing the
        /// user could do about a file that will not save.</summary>
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);

                // Empty tiles are written as null, which is the shortest thing that can be said
                // about one and the most readable thing to find in the file.
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

    /// <summary>
    /// What a Windows shortcut opens.
    ///
    /// A shortcut is a file about another file, and the shell draws its icon with an arrow on it to
    /// say so. A Launchpad tile is meant to be the app itself - its own icon, its own name - so a
    /// shortcut is read here and the tile is made of what it points at. There is no managed way to
    /// read one: it takes the shell's own IShellLink, which is the only thing this class is for.
    ///
    /// A Store app is the case that has nothing to read: Windows installs it as a package rather
    /// than as a file, so there is no path to make a tile of, and the shortcut is left to be the
    /// tile it would have been - except that the shell is asked about the app the shortcut names,
    /// which is where the tile's name and its own icon then come from.
    /// </summary>
    internal static class Shortcut
    {
        internal static string? Target(string path)
        {
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            object? link = null;
            try
            {
                link = new ShellLink();
                ((IPersistFile)link).Load(path, 0);   // STGM_READ

                var target = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);

                // Where there is nothing to look through - a link into the Store rather than to a
                // program - nothing comes back, and the shortcut is kept as it is. So it is when
                // the target has been moved or deleted since: an arrowed icon is better than a
                // tile that opens nothing.
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

        /// <summary>What a Store app is called and looks like, as the shell has it: the two things
        /// a tile cannot be made of without, since all the shortcut file itself offers is a picture
        /// of the app with an arrow on it. Either half can be missing: the shell may know an icon
        /// for something it has no name for.</summary>
        internal sealed record StoreApp(string? Name, ImageSource? Icon);

        /// <summary>Reads the Store app a shortcut opens, or null when it opens anything else - a
        /// file, which is the tile's own business, or a shell folder or a launch protocol, which is
        /// no kind of app at all. Only a shortcut that names no file is asked after, since a Store
        /// app has no file of its own to be kept: Windows installs it as a package.</summary>
        internal static StoreApp? Store(string path)
        {
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

            object? link = null;
            object? shellItem = null;
            try
            {
                link = new ShellLink();
                ((IPersistFile)link).Load(path, 0);   // STGM_READ

                // A shortcut that names a file is not this, even when the file has gone: the
                // shortcut's own icon is then the best there is, arrow and all. Only a shortcut that
                // names nothing at all is asked after, and only a Store app has anything to answer
                // with: a shell folder or a launch protocol is left as it was.
                var target = new StringBuilder(1024);
                ((IShellLinkW)link).GetPath(target, target.Capacity, IntPtr.Zero, 0);
                if (target.Length > 0) return null;

                // What such a shortcut records is not a path but the name Windows installed the
                // app's package under - "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App" - and the shell
                // is asked for the app under that name, which is also the question of whether there
                // is an app there to ask about.
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

        // What the shortcut records as what it opens: the shell item's identifier list, written out
        // the way the shell writes it. A Store app's is the package and entry above; the other
        // kinds are a URL or a {GUID}. Empty when the shortcut carries no list at all.
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

        // What the shell calls a shell item: "Apple Music" rather than the package name it is filed
        // under, and nothing when it has no such name to give.
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

        // The shell's own picture of the item, at the size a tile shows it. It comes back as a
        // bitmap rather than as an icon because an app is drawn from art bigger than an icon ever
        // is - and it is the app's own art, so there is no arrow on it.
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

        // The shell's shortcut object, and the two faces of it that matter here: IPersistFile to
        // load a .lnk from disk, IShellLinkW to ask where it goes. Declared in the order the
        // interfaces declare them, because that is what the vtable behind them is.
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

        // The shell item a Store app's shortcut names, and the two things it is asked of it: what
        // it is called, and its picture. Asked for through shell:AppsFolder, which is where the
        // shell keeps the apps Windows installed as packages - the ones with no file of their own.
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
