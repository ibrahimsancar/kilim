using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Kilim;

/// <summary>
/// The Windows shell itself, so that items under the rug look and move exactly like
/// the icons on the desktop: same pictures and thumbnails, same drag image and cursor.
/// </summary>
static class Shell
{
    /// <summary>Icon size the desktop uses, in logical pixels (48 unless the user changed it).</summary>
    public static int DesktopIconSize()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Bags\1\Desktop");
        return key?.GetValue("IconSize") is int size && size is >= 16 and <= 256 ? size : 48;
    }

    // ---------- pictures ----------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [In] ref Guid riid,
                                                   [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr obj, int size, out DIBSECTION section);

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public int dsOffset;
    }

    /// <summary>The picture Explorer shows for a file or folder: a thumbnail for images, otherwise its icon.</summary>
    public static BitmapSource? Image(string path, int pixels)
    {
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            if (factory.GetImage(new SIZE { cx = pixels, cy = pixels }, 0, out var hbitmap) != 0) return null;
            try { return FromDib(hbitmap); }
            finally { DeleteObject(hbitmap); }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or UnauthorizedAccessException) { return null; }
    }

    // CreateBitmapSourceFromHBitmap drops the alpha channel, so the pixels are copied by hand.
    static BitmapSource? FromDib(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0 || dib.bmBits == IntPtr.Zero || dib.bmBitsPixel != 32)
            return null;
        int w = dib.bmWidth, h = dib.bmHeight, stride = dib.bmWidthBytes;
        var px = new byte[stride * h];
        Marshal.Copy(dib.bmBits, px, 0, px.Length);
        if (dib.biHeight > 0)
        {
            // Bottom-up rows.
            var flipped = new byte[px.Length];
            for (var y = 0; y < h; y++) Buffer.BlockCopy(px, y * stride, flipped, (h - 1 - y) * stride, stride);
            px = flipped;
        }
        var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        image.Freeze();
        return image;
    }

    // ---------- dragging ----------

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        void BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                           [In] ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    }

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref SHDRAGIMAGE image, ComDataObject data);
        void InitializeFromWindow(IntPtr hwnd, ref Native.POINT pt, ComDataObject data);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHDRAGIMAGE
    {
        public SIZE sizeDragImage;
        public Native.POINT ptOffset;
        public IntPtr hbmpDragImage;
        public uint crColorKey;
    }

    static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11d2-839A-00C04FD918D0");
    static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHBindToParent(IntPtr pidl, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr lastChild);

    [DllImport("shell32.dll")]
    static extern int SHDoDragDrop(IntPtr hwnd, ComDataObject data, IntPtr dropSource, uint okEffects, out uint effect);

    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    const uint DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2, DROPEFFECT_LINK = 4;

    /// <summary>
    /// Drags files exactly as Explorer does: the shell's own data object, drag image and
    /// cursors, and the shell does the move wherever they are dropped. Returns when dropped.
    /// </summary>
    /// <param name="image">what is shown under the cursor, already rendered</param>
    /// <param name="grab">where in that image the cursor holds it</param>
    public static void DragFiles(IntPtr hwnd, IReadOnlyList<string> paths, BitmapSource image, Point grab)
    {
        var absolute = new List<IntPtr>();
        try
        {
            foreach (var path in paths)
                if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) == 0) absolute.Add(pidl);
            if (absolute.Count == 0) return;

            // All items live in the same folder, so one parent serves every one of them.
            var iidFolder = typeof(IShellFolder).GUID;
            var children = new IntPtr[absolute.Count];
            IShellFolder? parent = null;
            for (var i = 0; i < absolute.Count; i++)
            {
                if (SHBindToParent(absolute[i], ref iidFolder, out var folder, out children[i]) != 0) return;
                parent ??= folder;
            }

            var iidData = IID_IDataObject;
            parent!.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidData, IntPtr.Zero, out var dataPtr);
            var data = (ComDataObject)Marshal.GetObjectForIUnknown(dataPtr);
            Marshal.Release(dataPtr);

            SetDragImage(data, image, grab);
            SHDoDragDrop(hwnd, data, IntPtr.Zero, DROPEFFECT_MOVE | DROPEFFECT_COPY | DROPEFFECT_LINK, out _);
        }
        catch (COMException) { }
        finally
        {
            foreach (var pidl in absolute) CoTaskMemFree(pidl);
        }
    }

    static void SetDragImage(ComDataObject data, BitmapSource image, Point grab)
    {
        var helperType = Type.GetTypeFromCLSID(CLSID_DragDropHelper);
        if (helperType is null || Activator.CreateInstance(helperType) is not IDragSourceHelper helper) return;

        int w = image.PixelWidth, h = image.PixelHeight;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var hbitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (hbitmap == IntPtr.Zero) return;
        var handedOver = false;
        try
        {
            var px = new byte[w * h * 4];
            new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0).CopyPixels(px, w * 4, 0);
            Marshal.Copy(px, 0, bits, px.Length);

            var drag = new SHDRAGIMAGE
            {
                sizeDragImage = new SIZE { cx = w, cy = h },
                ptOffset = new Native.POINT { X = (int)grab.X, Y = (int)grab.Y },
                hbmpDragImage = hbitmap,
                crColorKey = 0xFFFFFFFF,
            };
            helper.InitializeFromBitmap(ref drag, data);
            // The helper owns the bitmap from here on.
            handedOver = true;
        }
        catch (COMException) { }
        finally
        {
            if (!handedOver) DeleteObject(hbitmap);
        }
    }

    // ---------- moving ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom, pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    const uint FO_MOVE = 1;
    const ushort FOF_MULTIDESTFILES = 0x1, FOF_RENAMEONCOLLISION = 0x8, FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMMKDIR = 0x200;

    /// <summary>
    /// Moves each item to its own target the way Explorer does: the desktop and open folders
    /// update at once, other drives work, big moves show progress, and Ctrl+Z on the desktop
    /// undoes it. Problems such as a file being open are reported by the shell's own dialogs.
    /// </summary>
    public static void Move(IReadOnlyList<string> from, IReadOnlyList<string> to)
    {
        if (from.Count == 0) return;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_MOVE,
            pFrom = string.Join("\0", from) + "\0",
            pTo = string.Join("\0", to) + "\0",
            // Never overwrites: a name that is taken by then gets " (2)", as in Explorer.
            fFlags = (ushort)(FOF_MULTIDESTFILES | FOF_RENAMEONCOLLISION | FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR),
        };
        SHFileOperation(ref op);
    }
}