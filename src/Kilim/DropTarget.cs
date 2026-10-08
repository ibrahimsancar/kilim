using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Kilim;

/// <summary>
/// The rug as a drop target for the shell, registered directly with OLE instead of through
/// WPF. It tells the shell's drag image manager where the drag is, so the dragged icon stays
/// visible over the rug as it does over the desktop, and it reports an optimized move so the
/// source never deletes or copies the files itself.
/// </summary>
[ComVisible(true)]
public sealed class DropTarget : DropTarget.IDropTarget
{
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDropTarget
    {
        [PreserveSig] int DragEnter(ComDataObject data, int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragOver(int keys, POINTL pt, ref int effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(ComDataObject data, int keys, POINTL pt, ref int effect);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int X, Y; }

    [ComImport, Guid("4657278B-411B-11d2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropTargetHelper
    {
        void DragEnter(IntPtr hwndTarget, ComDataObject data, ref Native.POINT pt, int effect);
        void DragLeave();
        void DragOver(ref Native.POINT pt, int effect);
        void Drop(ComDataObject data, ref Native.POINT pt, int effect);
        void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    }

    [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);

    const int DROPEFFECT_NONE = 0, DROPEFFECT_MOVE = 2;
    static readonly Guid CLSID_DragDropHelper = new("4657278A-411B-11d2-839A-00C04FD918D0");

    readonly IntPtr hwnd;
    readonly IDropTargetHelper? helper;
    string[]? paths;

    /// <summary>Files are over the rug at this point (window coordinates).</summary>
    public Func<string[], Point, bool>? Over;
    public Action? Left;
    /// <summary>Files were dropped here; returns whether they all ended up under the rug.</summary>
    public Func<string[], Point, bool>? Dropped;
    public Func<Point, Point>? FromScreen;

    public DropTarget(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        var type = Type.GetTypeFromCLSID(CLSID_DragDropHelper);
        if (type is not null) helper = Activator.CreateInstance(type) as IDropTargetHelper;
        // WPF registers its own target when AllowDrop is set; this one replaces it.
        RevokeDragDrop(hwnd);
        RegisterDragDrop(hwnd, this);
    }

    public void Unregister() => RevokeDragDrop(hwnd);

    int IDropTarget.DragEnter(ComDataObject data, int keys, POINTL pt, ref int effect)
    {
        // Data from another program can be anything; if it cannot be read, nothing is accepted.
        try { paths = new System.Windows.DataObject(data).GetData(DataFormats.FileDrop) as string[]; }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { paths = null; }
        effect = Decide(pt, effect);
        var p = new Native.POINT { X = pt.X, Y = pt.Y };
        try { helper?.DragEnter(hwnd, data, ref p, effect); } catch (COMException) { }
        return 0;
    }

    int IDropTarget.DragOver(int keys, POINTL pt, ref int effect)
    {
        effect = Decide(pt, effect);
        var p = new Native.POINT { X = pt.X, Y = pt.Y };
        try { helper?.DragOver(ref p, effect); } catch (COMException) { }
        return 0;
    }

    int IDropTarget.DragLeave()
    {
        try { helper?.DragLeave(); } catch (COMException) { }
        paths = null;
        Left?.Invoke();
        return 0;
    }

    int IDropTarget.Drop(ComDataObject data, int keys, POINTL pt, ref int effect)
    {
        var shown = Decide(pt, effect);
        var p = new Native.POINT { X = pt.X, Y = pt.Y };
        try { helper?.Drop(data, ref p, shown); } catch (COMException) { }

        var moved = false;
        try
        {
            moved = shown != DROPEFFECT_NONE && paths is not null && Dropped is not null
                    && Dropped(paths, ToWindow(pt));
        }
        // Never let an error travel back into the shell's drag loop; the files are wherever
        // the shell put them, and the rug shows its folder as it is.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException) { }
        paths = null;
        // Optimized move: we did the move, the source must neither delete nor copy anything.
        if (moved) SetPerformedEffect(data, DROPEFFECT_NONE);
        effect = DROPEFFECT_NONE;
        return 0;
    }

    int Decide(POINTL pt, int allowed)
    {
        if (paths is null || paths.Length == 0 || (allowed & DROPEFFECT_MOVE) == 0) return DROPEFFECT_NONE;
        try { return Over?.Invoke(paths, ToWindow(pt)) == false ? DROPEFFECT_NONE : DROPEFFECT_MOVE; }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return DROPEFFECT_NONE;
        }
    }

    Point ToWindow(POINTL pt) => FromScreen?.Invoke(new Point(pt.X, pt.Y)) ?? new Point(pt.X, pt.Y);

    static void SetPerformedEffect(ComDataObject data, int value)
    {
        try
        {
            var format = new FORMATETC
            {
                cfFormat = (short)RegisterClipboardFormat("Performed DropEffect"),
                dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
            };
            // GPTR: fixed memory, so the handle is the address and can be written directly.
            var memory = GlobalAlloc(0x0040, (UIntPtr)4);
            if (memory == IntPtr.Zero) return;
            Marshal.WriteInt32(memory, value);
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory };
            // On success the data object owns the memory; on failure it is still ours.
            try { data.SetData(ref format, ref medium, true); }
            catch (COMException) { GlobalFree(memory); }
        }
        catch (COMException) { }
    }
}
