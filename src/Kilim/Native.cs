using System.Runtime.InteropServices;
using System.Windows;

namespace Kilim;

static class Native
{
    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    public struct POINT { public int X, Y; }

    /// <summary>Cursor in physical screen pixels.</summary>
    public static Point CursorPos()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                                      uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path,
                                                uint size, uint flags);

    /// <summary>
    /// Where an existing path really leads once junctions, links and mapped drives are followed:
    /// "C:\..." or "\\server\share\...". Null if it does not exist or cannot be opened.
    /// </summary>
    public static string? FinalPath(string path)
    {
        const uint FILE_SHARE_ALL = 7, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        using var handle = CreateFile(path, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new System.Text.StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            // Long path: ask again with the size Windows reported.
            buffer = new System.Text.StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity) return null;
        var final = buffer.ToString();
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + final[8..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) return final[4..];
        return final;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    // Keeps a window directly above the desktop, under every other window.
    // HWND_BOTTOM would push it behind the desktop itself and hide it.
    const int WM_WINDOWPOSCHANGING = 0x0046;
    const uint SWP_NOZORDER = 0x0004, GW_HWNDPREV = 3;

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    public static IntPtr StayAtBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOZORDER) == 0)
            {
                var aboveDesktop = GetWindow(GetShellWindow(), GW_HWNDPREV);
                if (aboveDesktop == hwnd) pos.flags |= SWP_NOZORDER;
                else pos.hwndInsertAfter = aboveDesktop;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }
        return IntPtr.Zero;
    }
}
