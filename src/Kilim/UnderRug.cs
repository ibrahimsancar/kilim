using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Kilim;

/// <summary>
/// The hidden items, lying on the floor under the rug exactly where they were put.
/// Dragging one inside the rug moves it there; dragging it off the rug takes it out.
/// </summary>
public class UnderRug
{
    const double Pad = 8;
    // Same size and spacing as the desktop's own icons.
    static readonly int IconSize = Shell.DesktopIconSize();
    // Tall enough for the icon and a two-line name, as on the desktop.
    static readonly double CellW = IconSize + 28, CellH = IconSize + 54;

    readonly Canvas canvas;
    readonly Storage storage;
    readonly List<(Border tile, Point center, string path)> tiles = new();
    readonly HashSet<string> selected = new(StringComparer.OrdinalIgnoreCase);
    Rect body;
    Point pressedAt;
    string? pressedPath;

    // Items sit on the real desktop, so they are labelled like desktop icons: white with a dark edge.
    static readonly Brush Ink = Frozen(new SolidColorBrush(Colors.White));
    static readonly Brush InkShadow = Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)));
    // Windows 11 desktop: a faint glass square on hover, a brighter one when selected.
    static readonly Brush HoverFill = Frozen(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
    static readonly Brush HoverEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    static readonly Brush SelectedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    static readonly Brush SelectedEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)));
    Border? hovered;

    public bool DraggingOut { get; private set; }
    public event Action? Changed;

    public UnderRug(Canvas canvas, Storage storage)
    {
        this.canvas = canvas;
        this.storage = storage;
    }

    /// <param name="rug">rug area in window coordinates</param>
    public void Layout(Rect rug)
    {
        body = rug;
        canvas.Children.Clear();
        tiles.Clear();

        var items = storage.Items();
        selected.RemoveWhere(p => !items.Contains(p, StringComparer.OrdinalIgnoreCase));

        var taken = new List<Rect>();
        var placedNew = false;
        // Items with a place first, so newcomers find cells that are really free.
        foreach (var path in items.OrderBy(p => storage.SpotOf(p) is null))
        {
            var center = storage.SpotOf(path) is { } share
                ? new Point(share.X * body.Width, share.Y * body.Height)
                : FreeSpot(taken);
            if (storage.SpotOf(path) is null)
            {
                storage.SetSpot(path, new Point(center.X / body.Width, center.Y / body.Height));
                placedNew = true;
            }
            center = Keep(center);
            taken.Add(CellAround(center));
            var tile = ItemTile(path);
            Canvas.SetLeft(tile, center.X - CellW / 2);
            Canvas.SetTop(tile, center.Y - CellH / 2);
            canvas.Children.Add(tile);
            tiles.Add((tile, new Point(body.X + center.X, body.Y + center.Y), path));
        }
        if (placedNew) storage.Save();
        Repaint();
    }

    /// <summary>Keeps a whole tile on the rug.</summary>
    Point Keep(Point c) => new(
        Math.Clamp(c.X, CellW / 2 + Pad, Math.Max(CellW / 2 + Pad, body.Width - CellW / 2 - Pad)),
        Math.Clamp(c.Y, CellH / 2 + Pad, Math.Max(CellH / 2 + Pad, body.Height - CellH / 2 - Pad)));

    static Rect CellAround(Point c) => new(c.X - CellW / 2, c.Y - CellH / 2, CellW, CellH);

    /// <summary>For items that have no place yet: the first free cell, starting from the middle.</summary>
    Point FreeSpot(List<Rect> taken)
    {
        var cols = Math.Max(1, (int)((body.Width - 2 * Pad) / CellW));
        var rows = Math.Max(1, (int)((body.Height - 2 * Pad) / CellH));
        var x0 = (body.Width - cols * CellW) / 2 + CellW / 2;
        var y0 = (body.Height - rows * CellH) / 2 + CellH / 2;
        var middle = new Point(body.Width / 2, body.Height / 2);
        var cells = Enumerable.Range(0, cols * rows)
            .Select(i => new Point(x0 + i % cols * CellW, y0 + i / cols * CellH))
            .OrderBy(c => (c - middle).Length);
        foreach (var c in cells)
            if (!taken.Any(t => t.IntersectsWith(Rect.Inflate(CellAround(c), -2, -2)))) return c;
        return middle;
    }

    /// <summary>Puts newly hidden or moved items where they were dropped, side by side if several.</summary>
    public void PutAt(IReadOnlyList<string> paths, Point dropInWindow)
    {
        var cols = (int)Math.Ceiling(Math.Sqrt(paths.Count));
        var rows = (int)Math.Ceiling(paths.Count / (double)cols);
        var first = dropInWindow - (Vector)body.TopLeft - new Vector((cols - 1) * CellW / 2, (rows - 1) * CellH / 2);
        for (var k = 0; k < paths.Count; k++)
        {
            var c = Keep(first + new Vector(k % cols * CellW, k / cols * CellH));
            storage.SetSpot(paths[k], new Point(c.X / body.Width, c.Y / body.Height));
        }
        storage.Save();
    }

    /// <summary>Only items the rug no longer covers can be touched.</summary>
    public void Reveal(Fold fold)
    {
        foreach (var (tile, center, _) in tiles)
            tile.IsHitTestVisible = fold.Uncovers(center);
    }

    public void ClearSelection()
    {
        selected.Clear();
        Repaint();
    }

    Border ItemTile(string path)
    {
        var name = Path.GetFileName(path);
        var dpi = VisualTreeHelper.GetDpi(canvas).DpiScaleX;
        var stack = new StackPanel();
        stack.Children.Add(new Image
        {
            Source = Picture(path, (int)Math.Ceiling(IconSize * dpi)), Width = IconSize, Height = IconSize,
            Margin = new Thickness(0, 2, 0, 3),
        });
        stack.Children.Add(Label(name, 12, 36));

        var tile = new Border
        {
            Width = CellW - 4, Height = CellH - 4, Margin = new Thickness(2), Padding = new Thickness(3, 2, 3, 2),
            CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent, Child = stack,
            Tag = path, IsHitTestVisible = false,
        };
        tile.MouseLeftButtonDown += OnTileDown;
        tile.MouseMove += OnTileMove;
        tile.MouseEnter += (_, _) => { hovered = tile; Repaint(); };
        tile.MouseLeave += (_, _) => { if (hovered == tile) hovered = null; Repaint(); };

        var open = new MenuItem { Header = Text.Open };
        open.Click += (_, _) => Open(path);
        var restore = new MenuItem { Header = Text.Restore };
        restore.Click += (_, _) => Restore(selected.Contains(path) ? selected.ToList() : new List<string> { path });
        tile.ContextMenu = new ContextMenu { Items = { open, restore } };
        return tile;
    }

    // Asking the shell for pictures is slow; a rug refresh must not wait for it every time.
    readonly Dictionary<string, (DateTime Changed, BitmapSource? Image)> pictures = new(StringComparer.OrdinalIgnoreCase);

    BitmapSource? Picture(string path, int pixels)
    {
        var changed = Directory.Exists(path) ? Directory.GetLastWriteTimeUtc(path) : File.GetLastWriteTimeUtc(path);
        if (pictures.TryGetValue(path, out var cached) && cached.Changed == changed) return cached.Image;
        var image = Shell.Image(path, pixels);
        pictures[path] = (changed, image);
        return image;
    }

    /// <summary>Text with a one-pixel dark copy behind it, readable on any wallpaper.</summary>
    static Grid Label(string text, double size, double maxHeight)
    {
        TextBlock Make(Brush brush) => new()
        {
            Text = text, Foreground = brush, FontSize = size, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = maxHeight,
        };
        var shadow = Make(InkShadow);
        shadow.RenderTransform = new TranslateTransform(0.8, 1);
        return new Grid { Children = { shadow, Make(Ink) } };
    }

    void OnTileDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var path = (string)((Border)sender).Tag;
        if (e.ClickCount == 2)
        {
            Open(path);
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!selected.Remove(path)) selected.Add(path);
        }
        else if (!selected.Contains(path))
        {
            selected.Clear();
            selected.Add(path);
        }
        Repaint();
        pressedAt = e.GetPosition(canvas);
        pressedPath = path;
    }

    // Dragging is handed to the shell itself, so it looks and behaves exactly as on the desktop:
    // dropped on the desktop or in a folder the shell moves it there; dropped on the rug it stays under it.
    void OnTileMove(object sender, MouseEventArgs e)
    {
        if (pressedPath is null || e.LeftButton != MouseButtonState.Pressed) return;
        var tile = (Border)sender;
        var moved = e.GetPosition(canvas) - pressedAt;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        pressedPath = null;
        var paths = selected.ToList();
        var dpi = VisualTreeHelper.GetDpi(tile);
        var picture = new RenderTargetBitmap((int)Math.Ceiling(tile.ActualWidth * dpi.DpiScaleX),
                                             (int)Math.Ceiling(tile.ActualHeight * dpi.DpiScaleY),
                                             dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        picture.Render(tile);
        var grab = e.GetPosition(tile);
        var hwnd = ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(tile)).Handle;

        DraggingOut = true;
        try { Shell.DragFiles(hwnd, paths, picture, new Point(grab.X * dpi.DpiScaleX, grab.Y * dpi.DpiScaleY)); }
        finally { DraggingOut = false; }

        storage.Save();
        Changed?.Invoke();
    }
    void Repaint()
    {
        foreach (var (tile, _, path) in tiles)
        {
            var isSelected = selected.Contains(path);
            var isHovered = tile == hovered;
            tile.Background = isSelected ? SelectedFill : isHovered ? HoverFill : Brushes.Transparent;
            tile.BorderBrush = isSelected ? SelectedEdge : isHovered ? HoverEdge : Brushes.Transparent;
        }
    }

    // When an item cannot be moved (open in another program, say), the shell has
    // already told the user why; it simply stays under the rug.
    void Restore(List<string> paths)
    {
        foreach (var path in paths)
        {
            try { storage.Restore(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        selected.Clear();
        Changed?.Invoke();
    }

    static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    public static void ShowFolder() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.ItemsDir}\"") { UseShellExecute = true });

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
