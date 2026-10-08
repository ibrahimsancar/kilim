using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Kilim;

public partial class MainWindow : Window
{
    // Room around the rug for fringes, its shadow and the edges trailing while carried.
    const double Gutter = 48;
    const double RugMinWidth = 240, RugMaxWidth = 960, Aspect = 1.5;
    // Thin band along the rug's edge that resizes; the rest moves or folds.
    const double EdgeIn = 7, EdgeOut = 6;

    enum Mode { Idle, Pressing, Carrying, Peeling, Resizing }
    enum Edge { None, Left, Top, Right, Bottom }

    readonly Storage storage = new();
    readonly RugGeometry geometry = new();
    readonly UnderRug under;
    // A corner lifts only when the cursor rests on it, not when it merely passes over.
    readonly DispatcherTimer hoverTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };

    Rect body;
    RugPose pose;
    Point tipTarget;
    Vector tipVelocity;
    bool peelOpen;
    Point? hoverCorner;

    Mode mode;
    Point pressedAt;
    Vector grabOffset;
    (Point Cursor, Point Window, double Width) dragFrom;
    Edge resizeEdge;

    Point lastLeftTop;
    Vector velocity, swayVelocity;
    double carryLift, lumpTarget;
    TimeSpan lastFrame;
    bool rendering;

    public MainWindow()
    {
        InitializeComponent();
        under = new UnderRug(Tiles, storage);
        under.Changed += RefreshItems;
        hoverTimer.Tick += (_, _) =>
        {
            hoverTimer.Stop();
            if (hoverCorner is { } c && mode == Mode.Idle && !peelOpen) Peek(c, 12);
        };

        // Older or hand-edited settings may name a pattern that no longer exists.
        storage.State.Pattern = Patterns.Find(storage.State.Pattern).Id;
        storage.State.Width = Math.Clamp(storage.State.Width, RugMinWidth, RugMaxWidth);

        BuildScenes();
        ApplySize(storage.State.Width);
        ApplyPattern(Patterns.Find(storage.State.Pattern));
        BuildMenu();

        var s = storage.State;
        if (double.IsNaN(s.Left) || !FitsOnScreen(s.Left, s.Top))
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        else
            (Left, Top) = (s.Left, s.Top);

        // Owned by the desktop so "show desktop" (Win+D) does not hide the rug.
        new WindowInteropHelper(this).Owner = Native.GetShellWindow();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd).AddHook(Native.StayAtBottom);
            ListenForFiles(hwnd);
        };

        RefreshItems();
        pose.LumpHeight = lumpTarget;
        Redraw();
    }

    // ---------- scene ----------

    readonly GeometryModel3D baseRug = new(), flapRug = new(), baseFringe = new(), flapFringe = new();

    void BuildScenes()
    {
        baseRug.Geometry = geometry.BaseMesh;
        flapRug.Geometry = geometry.FlapMesh;
        baseFringe.Geometry = geometry.BaseFringe;
        flapFringe.Geometry = geometry.FlapFringe;
        BaseView.Children.Add(Scene(baseRug, baseFringe));
        FlapView.Children.Add(Scene(flapRug, flapFringe));
    }

    static ModelVisual3D Scene(GeometryModel3D rug, GeometryModel3D fringe) => new()
    {
        Content = new Model3DGroup
        {
            Children =
            {
                new AmbientLight(Color.FromRgb(0x73, 0x73, 0x73)),
                new DirectionalLight(Color.FromRgb(0xF0, 0xF0, 0xF0), RugGeometry.LightDir),
                rug,
                fringe,
            }
        }
    };

    void ApplyPattern(Pattern pattern)
    {
        var rugBrush = TextureBrush(Patterns.Texture(pattern));
        // The back of a hand-knotted rug shows the same pattern, flatter and darker.
        var front = new DiffuseMaterial(rugBrush);
        var back = new DiffuseMaterial(rugBrush) { Color = Color.FromRgb(0xB4, 0xAA, 0xA0) };
        baseRug.Material = flapRug.Material = front;
        baseRug.BackMaterial = flapRug.BackMaterial = back;
        ApplyFringe(pattern);
    }

    // Fringe strands are drawn to the rug's height, so they are redrawn after a resize.
    void ApplyFringe(Pattern pattern)
    {
        var fringe = new DiffuseMaterial(TextureBrush(Patterns.FringeTexture(pattern.Fringe, body.Height)));
        baseFringe.Material = flapFringe.Material = fringe;
        baseFringe.BackMaterial = flapFringe.BackMaterial = fringe;
    }

    static ImageBrush TextureBrush(System.Windows.Media.Imaging.BitmapSource image)
    {
        var brush = new ImageBrush(image) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
        brush.Freeze();
        return brush;
    }

    void ApplySize(double rugWidth)
    {
        var size = new Size(rugWidth, Math.Round(rugWidth / Aspect));
        double w = size.Width + 2 * Gutter, h = size.Height + 2 * Gutter;
        (Width, Height) = (w, h);
        body = new Rect(Gutter, Gutter, size.Width, size.Height);

        foreach (var view in new[] { BaseView, FlapView })
            view.Camera = new OrthographicCamera
            {
                // Looking along +z with y down, one world unit is one pixel of the window.
                Position = new Point3D(w / 2, h / 2, -1000), LookDirection = new Vector3D(0, 0, 1),
                UpDirection = new Vector3D(0, -1, 0), Width = w, NearPlaneDistance = 1, FarPlaneDistance = 3000,
            };

        Floor.Margin = new Thickness(Gutter, Gutter, 0, 0);
        (Floor.Width, Floor.Height) = (size.Width, size.Height);
        PlaceShadow(AmbientShadow, size, 16);
        PlaceShadow(ContactShadow, size, 2.5);

        pose.PeelCorner = pose.PeelTip = tipTarget = body.TopLeft;
        tipVelocity = default;
        peelOpen = false;
    }

    static void PlaceShadow(Image image, Size size, double radius)
    {
        image.Source = Patterns.SoftShadow(size.Width, size.Height, radius);
        var pad = Math.Ceiling(radius * 2);
        image.Margin = new Thickness(Gutter - pad, Gutter - pad, 0, 0);
    }

    void BuildMenu()
    {
        var patterns = new MenuItem { Header = Text.Pattern };
        foreach (var p in Patterns.All)
        {
            var item = new MenuItem { Header = Text.PatternName(p.Id), IsCheckable = true, IsChecked = p.Id == storage.State.Pattern };
            item.Click += (_, _) =>
            {
                storage.State.Pattern = p.Id;
                storage.Save();
                foreach (MenuItem other in patterns.Items) other.IsChecked = other == item;
                ApplyPattern(p);
                Redraw();
            };
            patterns.Items.Add(item);
        }

        var folder = new MenuItem { Header = Text.ShowFolder };
        folder.Click += (_, _) => UnderRug.ShowFolder();
        var close = new MenuItem { Header = Text.Close };
        close.Click += (_, _) => Application.Current.Shutdown();

        ContextMenu = new ContextMenu { Items = { patterns, new Separator(), folder, close } };
    }

    void RefreshItems()
    {
        var count = storage.Items().Count;
        lumpTarget = count == 0 ? 0 : 6 + 12 * Math.Min(Math.Log2(count + 1), 6) / 6;
        under.Layout(body);
        Animate();
    }

    // ---------- animation ----------

    void Animate()
    {
        if (rendering) return;
        rendering = true;
        lastFrame = TimeSpan.Zero;
        lastLeftTop = new Point(Left, Top);
        CompositionTarget.Rendering += OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        var dt = lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - lastFrame).TotalSeconds, 1 / 30.0);
        if (dt <= 0) return;
        lastFrame = now;

        // The corner is a spring: firm under the hand, softer and slightly springy when let go.
        var (k, zeta) = mode == Mode.Peeling ? (700.0, 0.8) : (240.0, 0.62);
        var accel = (tipTarget - pose.PeelTip) * k - tipVelocity * (2 * zeta * Math.Sqrt(k));
        tipVelocity += accel * dt;
        pose.PeelTip += tipVelocity * dt;
        if ((tipTarget - pose.PeelTip).Length < 0.15 && tipVelocity.Length < 3)
        {
            pose.PeelTip = tipTarget;
            tipVelocity = default;
        }
        // A springy corner coming home must not swing past the edge: the fold would turn
        // inside out and the rug would flip over for a moment.
        if (Vector.Multiply(pose.PeelTip - pose.PeelCorner, Inward(pose.PeelCorner)) < 0)
        {
            pose.PeelTip = pose.PeelCorner;
            tipVelocity = default;
        }

        Sway(dt);

        pose.LumpHeight += (lumpTarget - pose.LumpHeight) * (1 - Math.Exp(-dt * 8));
        carryLift += ((mode == Mode.Carrying ? 1 : 0) - carryLift) * (1 - Math.Exp(-dt * 10));

        Redraw();

        var settled = pose.PeelTip == tipTarget && pose.Sway.Length < 0.05 && swayVelocity.Length < 0.5 && velocity.Length < 1
                      && Math.Abs(lumpTarget - pose.LumpHeight) < 0.02 && mode != Mode.Carrying && carryLift < 0.01;
        if (settled)
        {
            if (pose.PeelTip == pose.PeelCorner) peelOpen = false;
            pose.Sway = swayVelocity = default;
            CompositionTarget.Rendering -= OnFrame;
            rendering = false;
            Redraw();
        }
    }

    /// <summary>
    /// The rug has weight: while carried, the parts away from the hand trail behind in
    /// proportion to the hand's speed; when the hand stops they swing back and settle.
    /// Speed is smoothed because mouse moves arrive unevenly between frames.
    /// </summary>
    void Sway(double dt)
    {
        var at = new Point(Left, Top);
        var raw = mode == Mode.Carrying ? (at - lastLeftTop) / dt : default;
        lastLeftTop = at;
        velocity += (raw - velocity) * (1 - Math.Exp(-dt / 0.08));

        var trail = -velocity * 0.015;
        const double max = 16;
        if (trail.Length > max) trail *= max / trail.Length;

        const double k = 170, zeta = 0.55;
        var accel = (trail - pose.Sway) * k - swayVelocity * (2 * zeta * Math.Sqrt(k));
        swayVelocity += accel * dt;
        pose.Sway += swayVelocity * dt;
    }
    void Redraw()
    {
        geometry.Update(body, pose);
        FlapShadow.Data = geometry.FlapShadow(pose);
        var fold = geometry.FoldFor(body, pose);
        under.Reveal(fold);

        FadeShadow(fold);

        // Carried, the rug rises: its shadow spreads and falls further away.
        AmbientShadow.Opacity = 0.30 - 0.06 * carryLift;
        AmbientScale.ScaleX = AmbientScale.ScaleY = 1 + 0.025 * carryLift;
        AmbientOffset.Y = 5 + 7 * carryLift;
        ContactShadow.Opacity = 0.45 * (1 - carryLift);
    }

    /// <summary>
    /// Where the corner is up there is no rug left to cast a shadow on the desk. The shadow
    /// fades out across the fold line, so the uncovered desk looks like the rest of the desk.
    /// </summary>
    void FadeShadow(Fold fold)
    {
        if (!fold.Active || fold.R <= 0)
        {
            Shadows.OpacityMask = null;
            return;
        }
        var onLine = fold.Corner + fold.Dir * fold.Line;
        var uncovered = onLine - fold.Dir * Math.Max(fold.R * 2.5, 12);
        Shadows.OpacityMask = new LinearGradientBrush(Colors.Black, Colors.Transparent, onLine, uncovered)
        {
            MappingMode = BrushMappingMode.Absolute,
        };
    }
    // ---------- corners and edges ----------

    Point[] Corners => new[] { body.TopLeft, body.TopRight, body.BottomRight, body.BottomLeft };

    double CornerZone => Math.Clamp(Math.Min(body.Width, body.Height) * 0.2, 32, 72);

    Point? CornerAt(Point p)
    {
        if (!Rect.Inflate(body, RugGeometry.FringeLength, 6).Contains(p)) return null;
        foreach (var c in Corners)
            if (Math.Abs(p.X - c.X) < CornerZone && Math.Abs(p.Y - c.Y) < CornerZone) return c;
        return null;
    }

    Edge EdgeAt(Point p)
    {
        if (CornerAt(p) is not null) return Edge.None;
        bool alongX = p.X > body.Left && p.X < body.Right, alongY = p.Y > body.Top && p.Y < body.Bottom;
        // The short sides carry the fringe, so their band reaches over it.
        if (alongY && p.X > body.Left - RugGeometry.FringeLength && p.X < body.Left + EdgeIn) return Edge.Left;
        if (alongY && p.X < body.Right + RugGeometry.FringeLength && p.X > body.Right - EdgeIn) return Edge.Right;
        if (alongX && p.Y > body.Top - EdgeOut && p.Y < body.Top + EdgeIn) return Edge.Top;
        if (alongX && p.Y < body.Bottom + EdgeOut && p.Y > body.Bottom - EdgeIn) return Edge.Bottom;
        return Edge.None;
    }

    Point NearestCorner(Point p) => Corners.OrderBy(c => (c - p).Length).First();

    Vector Inward(Point corner)
    {
        var v = new Point(body.X + body.Width / 2, body.Y + body.Height / 2) - corner;
        v.Normalize();
        return v;
    }

    void StartPeel(Point corner)
    {
        if (pose.PeelCorner != corner)
        {
            pose.PeelCorner = pose.PeelTip = corner;
            tipVelocity = default;
        }
    }

    /// <summary>
    /// Keeps the pull short of folding the rug in half, and the corner on the rug itself:
    /// a corner pulled off the rug would fold part of it out of the window, where it is cut off.
    /// </summary>
    Point ClampTip(Point corner, Point wanted)
    {
        var v = wanted - corner;
        var inward = Inward(corner);
        if (Vector.Multiply(v, inward) < 0) return corner;
        var max = Math.Min(body.Width, body.Height) * 0.95;
        if (v.Length > max) v *= max / v.Length;
        var inside = Rect.Inflate(body, -6, -6);
        var tip = corner + v;
        return new Point(Math.Clamp(tip.X, inside.Left, inside.Right), Math.Clamp(tip.Y, inside.Top, inside.Bottom));
    }

    bool Flat => (pose.PeelTip - pose.PeelCorner).Length < 0.5;

    /// <summary>
    /// Lifts a corner slightly. Another corner that is still up is first laid down;
    /// switching straight away would make it snap flat.
    /// </summary>
    void Peek(Point corner, double amount)
    {
        if (corner != pose.PeelCorner && !Flat)
        {
            tipTarget = pose.PeelCorner;
            Animate();
            return;
        }
        StartPeel(corner);
        tipTarget = corner + Inward(corner) * amount;
        Animate();
    }

    void ClosePeel()
    {
        peelOpen = false;
        tipTarget = pose.PeelCorner;
        under.ClearSelection();
        Animate();
    }

    // ---------- mouse ----------

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        var p = e.GetPosition(this);
        hoverTimer.Stop();
        CaptureMouse();

        if (peelOpen)
        {
            // A folded rug stays folded: grab the folded part to fold it further or lay it back.
            var fold = geometry.FoldFor(body, pose);
            if ((pose.PeelTip - p).Length < CornerZone || fold.OnFlap(p, body))
            {
                mode = Mode.Peeling;
                grabOffset = pose.PeelTip - p;
            }
            else if (body.Contains(p) && fold.Uncovers(p))
            {
                under.ClearSelection();
                ReleaseMouseCapture();
            }
            else StartPress(p);
            return;
        }

        if (EdgeAt(p) is var edge and not Edge.None)
        {
            mode = Mode.Resizing;
            resizeEdge = edge;
            dragFrom = (Native.CursorPos(), new Point(Left, Top), body.Width);
            return;
        }

        if (CornerAt(p) is { } corner)
        {
            StartPeel(corner);
            mode = Mode.Peeling;
            grabOffset = corner - p;
            return;
        }

        StartPress(p);
    }

    void StartPress(Point p)
    {
        mode = Mode.Pressing;
        pressedAt = p;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        switch (mode)
        {
            case Mode.Peeling:
                tipTarget = ClampTip(pose.PeelCorner, p + grabOffset);
                Animate();
                return;

            case Mode.Pressing:
                var moved = p - pressedAt;
                if (Math.Abs(moved.X) < 4 && Math.Abs(moved.Y) < 4) return;
                mode = Mode.Carrying;
                dragFrom = (Native.CursorPos(), new Point(Left, Top), body.Width);
                pose.SwayAnchor = pressedAt;
                goto case Mode.Carrying;

            case Mode.Carrying:
                // Screen coordinates: the window moves under the cursor, so positions
                // relative to it would feed back into themselves.
                var shift = CursorShift();
                Left = dragFrom.Window.X + shift.X;
                Top = dragFrom.Window.Y + shift.Y;
                Animate();
                return;

            case Mode.Resizing:
                Resize(CursorShift());
                return;
        }

        UpdateHover(p);
    }

    Vector CursorShift()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = Native.CursorPos();
        return new Vector((cursor.X - dragFrom.Cursor.X) / dpi.DpiScaleX, (cursor.Y - dragFrom.Cursor.Y) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Grows or shrinks from the dragged edge while the opposite edge stays put,
    /// keeping the rug's 3:2 shape so its pattern is never stretched.
    /// </summary>
    void Resize(Vector shift)
    {
        var startW = dragFrom.Width;
        var wanted = resizeEdge switch
        {
            Edge.Right => startW + shift.X,
            Edge.Left => startW - shift.X,
            Edge.Bottom => startW + shift.Y * Aspect,
            _ => startW - shift.Y * Aspect,
        };
        var newW = Math.Round(Math.Clamp(wanted, RugMinWidth, RugMaxWidth));
        if (Math.Abs(newW - body.Width) < 1) return;

        double startH = Math.Round(startW / Aspect), newH = Math.Round(newW / Aspect);
        var (x, y) = (dragFrom.Window.X, dragFrom.Window.Y);
        switch (resizeEdge)
        {
            case Edge.Left: x += startW - newW; y += (startH - newH) / 2; break;
            case Edge.Right: y += (startH - newH) / 2; break;
            case Edge.Top: y += startH - newH; x += (startW - newW) / 2; break;
            case Edge.Bottom: x += (startW - newW) / 2; break;
        }
        ApplySize(newW);
        (Left, Top) = (x, y);
        under.Layout(body);
        Redraw();
    }

    void UpdateHover(Point p)
    {
        if (peelOpen)
        {
            // On the uncovered desk the cursor is the desktop's own; the hand is only for the folded part.
            var fold = geometry.FoldFor(body, pose);
            var onDesk = body.Contains(p) && fold.Uncovers(p);
            Cursor = !onDesk && ((pose.PeelTip - p).Length < CornerZone || fold.OnFlap(p, body)) ? Cursors.Hand : null;
            return;
        }

        var edge = EdgeAt(p);
        if (edge != Edge.None)
        {
            Cursor = edge is Edge.Left or Edge.Right ? Cursors.SizeWE : Cursors.SizeNS;
            if (hoverCorner is not null) LowerHoverCorner();
            return;
        }

        // Resting on a corner lifts it a little: that corner can be picked up.
        var hover = CornerAt(p);
        Cursor = hover is not null ? Cursors.Hand : null;
        if (hover == hoverCorner) return;
        if (hover is { } c)
        {
            hoverCorner = c;
            hoverTimer.Stop();
            hoverTimer.Start();
        }
        else LowerHoverCorner();
    }

    void LowerHoverCorner()
    {
        hoverCorner = null;
        hoverTimer.Stop();
        if (peelOpen) return;
        tipTarget = pose.PeelCorner;
        Animate();
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        var was = mode;
        mode = Mode.Idle;
        hoverCorner = null;
        hoverTimer.Stop();

        switch (was)
        {
            case Mode.Peeling:
                // Pulled far enough, the corner stays up until it is laid back down.
                if ((tipTarget - pose.PeelCorner).Length > Math.Min(body.Width, body.Height) * 0.22) peelOpen = true;
                else ClosePeel();
                break;

            case Mode.Carrying:
                SaveBounds();
                Animate();
                break;

            case Mode.Resizing:
                storage.State.Width = body.Width;
                SaveBounds();
                ApplyFringe(Patterns.Find(storage.State.Pattern));
                RefreshItems();
                break;
        }
        // Last: releasing the capture sends a mouse move that must see the final state.
        ReleaseMouseCapture();
    }

    void OnLeave(object sender, MouseEventArgs e)
    {
        if (mode != Mode.Idle || peelOpen || hoverCorner is null) return;
        LowerHoverCorner();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && peelOpen) ClosePeel();
    }

    // ---------- dropping things under the rug ----------

    DropTarget? dropTarget;
    FileSystemWatcher? watcher;
    readonly DispatcherTimer watchDelay = new() { Interval = TimeSpan.FromMilliseconds(60) };

    void ListenForFiles(IntPtr hwnd)
    {
        dropTarget = new DropTarget(hwnd)
        {
            Over = FilesOver,
            Left = FilesLeft,
            Dropped = FilesDropped,
            FromScreen = PointFromScreen,
        };

        // Whatever moves files in or out (a drop, a drag to the desktop finished by the shell
        // in the background, Explorer, Ctrl+Z) shows on the rug straight away.
        watchDelay.Tick += (_, _) =>
        {
            watchDelay.Stop();
            RefreshItems();
        };
        watcher = new FileSystemWatcher(Storage.ItemsDir)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(() =>
        {
            watchDelay.Stop();
            watchDelay.Start();
        });
        watcher.Created += changed;
        watcher.Deleted += changed;
        watcher.Renamed += (s, e) => changed(s, e);
        Closed += (_, _) =>
        {
            dropTarget.Unregister();
            watcher.Dispose();
        };
    }

    static bool Ours(string[] paths) => paths.All(p => Storage.IsInside(p, Storage.ItemsDir));

    bool FilesOver(string[] paths, Point p)
    {
        if (Ours(paths)) return body.Contains(p);
        if (!paths.Any(Storage.CanHide)) return false;
        // Something from outside: the nearest corner lifts to let it in.
        if (!peelOpen) Peek(NearestCorner(p), 34);
        return true;
    }

    void FilesLeft()
    {
        if (!under.DraggingOut && !peelOpen) ClosePeel();
    }

    bool FilesDropped(string[] paths, Point at)
    {
        if (Ours(paths))
        {
            // Moved around under the rug: it now lies where it was dropped.
            under.PutAt(paths, at);
            RefreshItems();
            return true;
        }

        var (hidden, failed) = storage.Hide(paths);
        under.PutAt(hidden, at);
        if (!peelOpen) ClosePeel();
        RefreshItems();
        return failed.Count == 0 && hidden.Count > 0;
    }
    // ---------- position ----------

    void SaveBounds()
    {
        (storage.State.Left, storage.State.Top) = (Left, Top);
        storage.Save();
    }

    static bool FitsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 &&
        top >= SystemParameters.VirtualScreenTop - 50 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 50 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 50;
}
