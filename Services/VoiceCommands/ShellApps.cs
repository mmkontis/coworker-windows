using System.Runtime.InteropServices;

namespace Coworker.Services.VoiceCommands;

/// <param name="Id">The AppUserModelID: what shell:AppsFolder\ addresses. Packaged apps look like
/// "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", classic ones like "{known folder GUID}\dir\app.exe".</param>
/// <param name="Path">The program file for classic apps; empty for packaged apps.</param>
public sealed record InstalledApp(string Id, string Name, string Path)
{
    public string ShellPath => ShellPathFor(Id);

    public static string ShellPathFor(string id) => $@"shell:AppsFolder\{id}";
}

/// <summary>Icon pixels, top-down 32-bit BGRA with premultiplied alpha.</summary>
public sealed record AppIcon(int Width, int Height, byte[] Bgra);

/// <summary>
/// The Start menu app list (Mac: the .app scan behind MacAppPicker): the shell's AppsFolder covers
/// classic and packaged apps in one place. Shell objects are apartment-bound, so every call runs on
/// its own STA thread.
/// </summary>
public static class ShellApps
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly object Gate = new();
    private static Task<IReadOnlyList<InstalledApp>>? _scan;
    private static DateTime _scannedAt;

    // Icons are cheap individually but a long list would otherwise spawn a thread per row at once.
    private static readonly SemaphoreSlim IconSlots = new(3);

    /// Installed apps sorted by name, cached for five minutes.
    public static Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync()
    {
        lock (Gate)
        {
            if (_scan == null || _scan.IsFaulted || DateTime.UtcNow - _scannedAt > CacheLifetime)
            {
                _scan = RunSta<IReadOnlyList<InstalledApp>>(Enumerate);
                _scannedAt = DateTime.UtcNow;
            }
            return _scan;
        }
    }

    /// The icon of <paramref name="parsingName"/> (a file path or a shell:AppsFolder\ path), or null
    /// when the shell has none.
    public static async Task<AppIcon?> LoadIconAsync(string parsingName, int size)
    {
        await IconSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            return await RunSta(() => ExtractIcon(parsingName, size)).ConfigureAwait(false);
        }
        // Failed HRESULTs surface as COMException or as the matching standard exception (not found, bad argument).
        catch (Exception ex) when (ex is COMException or ArgumentException or IOException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            IconSlots.Release();
        }
    }

    private static List<InstalledApp> Enumerate()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        var folder = (IShellItem)CreateItem(@"shell:AppsFolder", IidShellItem);
        var bhid = BhidEnumItems;
        var iid = IidEnumShellItems;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out var enumPtr);
        var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
        Marshal.Release(enumPtr);

        try
        {
            while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1)
            {
                try
                {
                    item.GetDisplayName(Sigdn.NormalDisplay, out var name);
                    item.GetDisplayName(Sigdn.ParentRelativeParsing, out var id);
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;

                    apps.TryAdd(id, new InstalledApp(id, name, ProgramPath(id)));
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(items);
            Marshal.ReleaseComObject(folder);
        }

        return apps.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// The exe behind a classic app's id: either a plain path, or a known-folder GUID plus a relative path.
    private static string ProgramPath(string id)
    {
        string path;
        if (id.Length > 39 && id[0] == '{' && id[37] == '}' && id[38] == '\\' && Guid.TryParse(id[..38], out var folderId))
        {
            if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out var folder) != 0) return "";
            path = Path.Combine(folder, id[39..]);
        }
        else
        {
            path = id;
        }

        return Path.IsPathRooted(path) && File.Exists(path) ? path : "";
    }

    private static AppIcon? ExtractIcon(string parsingName, int size)
    {
        var factory = (IShellItemImageFactory)CreateItem(parsingName, IidShellItemImageFactory);
        var bitmap = IntPtr.Zero;
        try
        {
            factory.GetImage(new Size { Width = size, Height = size }, SiigbfIconOnly, out bitmap);
            if (GetObject(bitmap, Marshal.SizeOf<BitmapInfo>(), out var info) == 0) return null;

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = info.Width,
                Height = -info.Height,   // negative: top-down rows
                Planes = 1,
                BitCount = 32,
            };
            var pixels = new byte[info.Width * info.Height * 4];
            var screen = GetDC(IntPtr.Zero);
            try
            {
                if (GetDIBits(screen, bitmap, 0, (uint)info.Height, pixels, ref header, 0) == 0) return null;
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, screen);
            }

            // The shell hands out straight alpha; XAML bitmaps want it premultiplied.
            for (var i = 0; i < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3];
                if (alpha == 255) continue;
                pixels[i] = (byte)(pixels[i] * alpha / 255);
                pixels[i + 1] = (byte)(pixels[i + 1] * alpha / 255);
                pixels[i + 2] = (byte)(pixels[i + 2] * alpha / 255);
            }
            return new AppIcon(info.Width, info.Height, pixels);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(factory);
        }
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static object CreateItem(string parsingName, Guid iid)
    {
        SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var item);
        return item;
    }

    private static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid IidShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    private const int SiigbfIconOnly = 0x04;

    private enum Sigdn : uint
    {
        NormalDisplay = 0,
        ParentRelativeParsing = 0x80018001,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(Sigdn name, [MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, out IShellItem item, out uint fetched);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid,
        [MarshalAs(UnmanagedType.IUnknown)] out object item);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int size, out BitmapInfo bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint firstLine, uint lines, byte[] bits,
        ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
