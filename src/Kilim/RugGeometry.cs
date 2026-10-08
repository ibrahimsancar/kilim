using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Kilim;

/// <summary>
/// The shape of the rug at one moment: where a corner is pulled to, how far the parts
/// away from the hand trail behind while carried, and how big the lump of hidden things is.
/// </summary>
public struct RugPose
{
    public Point PeelCorner;   // corner being lifted
    public Point PeelTip;      // where that corner is now
    public Vector Sway;        // how far the far edge trails behind the hand, px
    public Point SwayAnchor;   // where the rug is held
    public double LumpHeight;  // px
}

/// <summary>
/// Page-curl fold: the corner side of a fold line rolls around a cylinder of radius R
/// and lies flipped over the rest of the rug, so the corner lands exactly on the tip.
/// </summary>
public readonly struct Fold
{
    public readonly Point Corner;
    public readonly Vector Dir;   // from the corner toward the tip
    public readonly double Line;  // fold line, measured from the corner along Dir
    public readonly double R;
    public readonly bool Active;

    public Fold(Point corner, Point tip, double radius)
    {
        Corner = corner;
        var d = tip - corner;
        var len = d.Length;
        // A barely lifted corner has a tighter roll. With the full radius a corner a pixel
        // off the floor would still curl a wide strip, in whatever direction that pixel points.
        R = Math.Min(radius, len / Math.PI);
        Active = len > 0.3;
        Dir = Active ? d / len : new Vector(1, 0);
        Line = (len + Math.PI * R) / 2;
    }

    /// <summary>Distance past the fold line toward the corner; positive means lifted.</summary>
    public double Lift(Point p) => Active ? Line - Vector.Multiply(p - Corner, Dir) : -1e9;

    /// <summary>Floor that nothing covers any more, not even the rolled part.</summary>
    public bool Uncovers(Point p) => Lift(p) > R;

    /// <summary>Whether p, on screen, lies on the lifted part (its roll or its flipped back).</summary>
    public bool OnFlap(Point p, Rect body)
    {
        if (!Active) return false;
        var u = Vector.Multiply(p - Corner, Dir);
        if (u < Line - R) return false;
        if (u <= Line) return body.Contains(p);
        // Unfold the flat flipped part back to where that piece of rug came from.
        return body.Contains(p - Dir * (2 * (u - Line) + Math.PI * R));
    }

    public (Point, double height) Map(Point p)
    {
        var t = Lift(p);
        if (t <= 0) return (p, 0);
        double along, h;
        if (t < Math.PI * R)
        {
            var a = t / R;
            along = -R * Math.Sin(a);
            h = R * (1 - Math.Cos(a));
        }
        else
        {
            along = t - Math.PI * R;
            h = 2 * R;
        }
        // Positive "along" moves away from the corner, over the rest of the rug.
        var u = Line + along;
        var u0 = Line - t;
        return (p + Dir * (u - u0), h);
    }
}

/// <summary>Builds the rug and fringe meshes, split into the flat part and the lifted flap.</summary>
public class RugGeometry
{
    const int NX = 64, NY = 42;
    public const double FringeLength = 15;

    // Light comes low from the upper left, like a window: bumps and folds read clearly.
    public static readonly Vector3D LightDir = new(0.6, 0.7, 0.62);

    public readonly MeshGeometry3D BaseMesh = new(), FlapMesh = new();
    public readonly MeshGeometry3D BaseFringe = new(), FlapFringe = new();

    static readonly Point3DCollection NoPoints = new();
    static RugGeometry() => NoPoints.Freeze();

    readonly Point3D[] pos =new Point3D[(NX + 1) * (NY + 1)];
    readonly double[] lift = new double[(NX + 1) * (NY + 1)];
    readonly PointCollection uv, fringeUv;
    Rect body;

    public RugGeometry()
    {
        uv = new PointCollection((NX + 1) * (NY + 1));
        for (var j = 0; j <= NY; j++)
            for (var i = 0; i <= NX; i++)
                uv.Add(new Point((double)i / NX, (double)j / NY));
        uv.Freeze();

        fringeUv = new PointCollection(4 * (NY + 1));
        for (var side = 0; side < 2; side++)
            for (var j = 0; j <= NY; j++)
            {
                fringeUv.Add(new Point(0, (double)j / NY));
                fringeUv.Add(new Point(1, (double)j / NY));
            }
        fringeUv.Freeze();
    }

    static int Idx(int i, int j) => j * (NX + 1) + i;

    public static double CurlRadius(Rect body) => Math.Clamp(Math.Min(body.Width, body.Height) * 0.035, 6, 13);

    public Fold FoldFor(Rect body, RugPose pose) => new(pose.PeelCorner, pose.PeelTip, CurlRadius(body));

    public void Update(Rect bodyRect, RugPose pose)
    {
        body = bodyRect;
        var fold = FoldFor(body, pose);
        var center = new Point(body.X + body.Width / 2, body.Y + body.Height / 2);
        var reach = Math.Max(1, Corners.Max(c => (c - pose.SwayAnchor).Length));

        for (var j = 0; j <= NY; j++)
            for (var i = 0; i <= NX; i++)
            {
                var p = new Point(body.X + body.Width * i / NX, body.Y + body.Height * j / NY);
                var k = Idx(i, j);
                var t = fold.Lift(p);
                lift[k] = t;

                // Bumps flatten out near the fold so the lifted part meets the floor cleanly.
                var flat = t >= 0 ? 0 : SmoothStep(0, 3 * fold.R, -t);
                var h = flat * (Lump(p, center, pose.LumpHeight) + Settle(p));

                var (q, fh) = fold.Map(p);

                // Held at one point, the rest hangs back with its weight and lifts a little in the air.
                var hang = Math.Pow(Math.Min((p - pose.SwayAnchor).Length / reach, 1), 1.2);
                q += pose.Sway * hang;
                h += 0.5 * pose.Sway.Length * hang;

                // Screen y grows downward; the viewer looks along +z, so height is -z.
                pos[k] = new Point3D(q.X, q.Y, -(h + fh));
            }

        var positions = new Point3DCollection(pos);
        positions.Freeze();
        var normals = Normals();

        var baseIdx = new Int32Collection(NX * NY * 6);
        var flapIdx = new Int32Collection();
        for (var j = 0; j < NY; j++)
            for (var i = 0; i < NX; i++)
            {
                int a = Idx(i, j), b = Idx(i + 1, j), c = Idx(i, j + 1), d = Idx(i + 1, j + 1);
                var target = lift[a] > 0 || lift[b] > 0 || lift[c] > 0 || lift[d] > 0 ? flapIdx : baseIdx;
                target.Add(a); target.Add(c); target.Add(b);
                target.Add(b); target.Add(c); target.Add(d);
            }
        baseIdx.Freeze();
        flapIdx.Freeze();

        Set(BaseMesh, positions, normals, uv, baseIdx);
        Set(FlapMesh, positions, normals, uv, flapIdx);
        UpdateFringes(normals);
    }

    static void Set(MeshGeometry3D mesh, Point3DCollection p, Vector3DCollection n, PointCollection t, Int32Collection idx)
    {
        // With no indices WPF reads the positions as a triangle list and draws stray slivers.
        if (idx.Count == 0) p = NoPoints;
        mesh.Positions = p;
        mesh.Normals = n;
        mesh.TextureCoordinates = t;
        mesh.TriangleIndices = idx;
    }

    Vector3DCollection Normals()
    {
        var normals = new Vector3DCollection(pos.Length);
        for (var j = 0; j <= NY; j++)
            for (var i = 0; i <= NX; i++)
            {
                var du = pos[Idx(Math.Min(i + 1, NX), j)] - pos[Idx(Math.Max(i - 1, 0), j)];
                var dv = pos[Idx(i, Math.Min(j + 1, NY))] - pos[Idx(i, Math.Max(j - 1, 0))];
                var n = Vector3D.CrossProduct(dv, du);
                // Where the fold squeezes neighbours together the cross product can vanish;
                // a NaN normal makes WPF drop the whole mesh for that frame.
                if (n.Length < 1e-9 || double.IsNaN(n.Length)) n = new Vector3D(0, 0, -1);
                else n.Normalize();
                normals.Add(n);
            }
        normals.Freeze();
        return normals;
    }

    // Fringes hang off the two short sides and follow the edge when it curls.
    void UpdateFringes(Vector3DCollection rugNormals)
    {
        var p = new Point3DCollection(4 * (NY + 1));
        var n = new Vector3DCollection(4 * (NY + 1));
        var lifted = new bool[2 * (NY + 1)];
        for (var side = 0; side < 2; side++)
        {
            int edge = side == 0 ? 0 : NX, inner = side == 0 ? 1 : NX - 1;
            for (var j = 0; j <= NY; j++)
            {
                var e = pos[Idx(edge, j)];
                var outward = e - pos[Idx(inner, j)];
                if (outward.Length < 1e-9) outward = new Vector3D(side == 0 ? -1 : 1, 0, 0);
                else outward.Normalize();
                // A little droop: fringes on a flat rug lie on the floor, slightly fanned.
                var wobble = Math.Sin(j * 2.3) * 0.08;
                var tip = e + (outward + new Vector3D(0, wobble, 0)) * FringeLength;
                p.Add(e);
                p.Add(tip);
                n.Add(rugNormals[Idx(edge, j)]);
                n.Add(rugNormals[Idx(edge, j)]);
                lifted[side * (NY + 1) + j] = lift[Idx(edge, j)] > 0;
            }
        }
        p.Freeze();
        n.Freeze();

        var baseIdx = new Int32Collection();
        var flapIdx = new Int32Collection();
        for (var side = 0; side < 2; side++)
            for (var j = 0; j < NY; j++)
            {
                var r = side * (NY + 1) + j;
                int a = 2 * r, b = a + 1, c = a + 2, d = a + 3;
                var target = lifted[r] || lifted[r + 1] ? flapIdx : baseIdx;
                // Winding differs per side so both strips face the viewer.
                if (side == 0) { target.Add(a); target.Add(b); target.Add(c); target.Add(c); target.Add(b); target.Add(d); }
                else { target.Add(a); target.Add(c); target.Add(b); target.Add(b); target.Add(c); target.Add(d); }
            }
        baseIdx.Freeze();
        flapIdx.Freeze();
        Set(BaseFringe, p, n, fringeUv, baseIdx);
        Set(FlapFringe, p, n, fringeUv, flapIdx);
    }

    Point[] Corners => new[] { body.TopLeft, body.TopRight, body.BottomRight, body.BottomLeft };

    /// <summary>The part of a rectangle where f is not negative.</summary>
    static List<Point> Clip(Rect r, Func<Point, double> f)
    {
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
        var result = new List<Point>();
        for (var i = 0; i < 4; i++)
        {
            Point a = corners[i], b = corners[(i + 1) % 4];
            double fa = f(a), fb = f(b);
            if (fa >= 0) result.Add(a);
            if ((fa >= 0) != (fb >= 0)) result.Add(a + (b - a) * (fa / (fa - fb)));
        }
        return result;
    }

    /// <summary>Outline of the lifted flap's shadow on whatever is below it.</summary>
    public Geometry? FlapShadow(RugPose pose)
    {
        var fold = FoldFor(body, pose);
        if (!fold.Active) return null;
        var clipped = Clip(body, fold.Lift);
        if (clipped.Count < 3) return null;

        var light = new Vector(LightDir.X, LightDir.Y) / LightDir.Z;
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        var first = true;
        for (var i = 0; i < clipped.Count; i++)
        {
            Point a = clipped[i], b = clipped[(i + 1) % clipped.Count];
            for (var s = 0; s < 24; s++)
            {
                var (q, h) = fold.Map(a + (b - a) * (s / 24.0));
                var shadowPoint = q + light * h * 0.5;
                if (first) { figure.StartPoint = shadowPoint; first = false; }
                else figure.Segments.Add(new LineSegment(shadowPoint, false));
            }
        }
        var geometry = new PathGeometry(new[] { figure }) { FillRule = FillRule.Nonzero };
        geometry.Freeze();
        return geometry;
    }

    static double Lump(Point p, Point center, double height)
    {
        if (height <= 0) return 0;
        var dx = (p.X - center.X) / (height * 2.5 + 34);
        var dy = (p.Y - center.Y) / (height * 1.6 + 24);
        return height * Math.Exp(-(dx * dx + dy * dy));
    }

    // A real rug never lies perfectly flat; these soft swells catch the light.
    static double Settle(Point p) =>
        0.8 * Math.Sin(p.X / 83 + 0.7) * Math.Sin(p.Y / 61 + 1.3) + 0.5 * Math.Sin((p.X + p.Y) / 127) + 1.3;

    static double SmoothStep(double a, double b, double x)
    {
        var t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
