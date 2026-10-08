using System.IO;
using System.Text.Json;
using System.Windows;

namespace Kilim;

public class RugState
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    // Height always follows from the width at 3:2, the shape of the pattern photos.
    public double Width { get; set; } = 420;
    public string Pattern { get; set; } = "sade";

    // Stored item name -> where it was before going under the rug.
    public Dictionary<string, string> OriginalPaths { get; set; } = new();

    // Stored item name -> where it lies under the rug, as [x, y] shares of the rug's
    // width and height, so it stays in place when the rug is resized.
    public Dictionary<string, double[]> Spots { get; set; } = new();
}

/// <summary>
/// Files under the rug live in a hidden folder in the user's profile.
/// Nothing is ever deleted: if the app breaks, everything is still there.
/// </summary>
public class Storage
{
    public static readonly string Root =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kilim");

    public static readonly string ItemsDir = Path.Combine(Root, "items");
    static readonly string StateFile = Path.Combine(Root, "state.json");
    // A rug that was never moved has no position yet (NaN); plain JSON cannot hold NaN.
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public RugState State { get; private set; } = new();

    public Storage()
    {
        // A folder redirected elsewhere by a junction or link would send the user's files, and
        // the settings written next to them, somewhere the user never chose. Checked before
        // anything is created inside it.
        if (IsRedirected(Root)) throw new FolderRedirectedException();
        Directory.CreateDirectory(Root);
        if (IsRedirected(ItemsDir)) throw new FolderRedirectedException();
        Directory.CreateDirectory(ItemsDir);
        var root = new DirectoryInfo(Root);
        root.Attributes |= FileAttributes.Hidden;

        if (File.Exists(StateFile))
        {
            try { State = JsonSerializer.Deserialize<RugState>(File.ReadAllText(StateFile), JsonOptions) ?? new(); }
            catch (JsonException) { State = new(); }
        }
        // Valid JSON can still say "null" or carry odd values; nothing below may trip over that.
        State.OriginalPaths ??= new();
        State.Spots ??= new();
        foreach (var (name, spot) in State.Spots.ToList())
            if (spot is not { Length: 2 } || !double.IsFinite(spot[0]) || !double.IsFinite(spot[1]))
                State.Spots.Remove(name);
        State.Pattern ??= "sade";
        if (!double.IsFinite(State.Width)) State.Width = 420;
    }

    static bool IsRedirected(string dir) =>
        Directory.Exists(dir) && new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>True for paths inside the rug's own folder.</summary>
    public static bool IsInside(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// What may go under the rug: real files and folders on a local drive, but never a whole drive,
    /// the rug's own folder or a folder that contains it, or anything in the Recycle Bin. Checked
    /// both as written and where the path really leads, so a junction or link cannot get round it.
    /// Network locations are refused before anything touches them.
    /// </summary>
    public static bool CanHide(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(Path.GetFileName(full)) || !OnLocalDrive(full)) return false;
        if (LeadsToNetwork(full, 0) || !Exists(full)) return false;
        // Where it really leads must be known and pass the same tests; if Windows cannot say, no.
        if (Native.FinalPath(full) is not { } real) return false;
        real = Path.TrimEndingDirectorySeparator(real);
        if (real.StartsWith(@"\\", StringComparison.Ordinal) || string.IsNullOrEmpty(Path.GetFileName(real)) || !OnLocalDrive(real))
            return false;
        var realRoot = Native.FinalPath(Root) ?? Root;
        foreach (var p in new[] { full, real })
        {
            if (InRecycleBin(p)) return false;
            foreach (var r in new[] { Root, realRoot })
                if (SamePath(p, r) || IsInside(r, p) || IsInside(p, r)) return false;
        }
        return true;
    }

    /// <summary>
    /// Follows junctions and symbolic links along the path by reading their targets from the
    /// local disk, so a link that leads to a network share is caught before Windows contacts it.
    /// </summary>
    static bool LeadsToNetwork(string full, int depth)
    {
        if (depth > 8) return true;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return true;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (!info.Exists) return false;
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not { } target) continue;
            if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
                return true;
            var next = Path.IsPathFullyQualified(target) ? target : Path.Combine(Path.GetDirectoryName(current) ?? root, target);
            if (next.StartsWith(@"\??\", StringComparison.Ordinal)) next = next[4..];
            if (LeadsToNetwork(Path.GetFullPath(next), depth + 1)) return true;
        }
        return false;
    }

    static bool OnLocalDrive(string full)
    {
        var root = Path.GetPathRoot(full);
        if (root is not { Length: >= 3 } || root[1] != ':') return false;
        try { return new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable; }
        catch (ArgumentException) { return false; }
    }

    static bool InRecycleBin(string path) =>
        path.Split(Path.DirectorySeparatorChar).Any(part => part.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase));

    static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                      StringComparison.OrdinalIgnoreCase);
    public void Save()
    {
        // Forget records whose item is no longer under the rug.
        foreach (var name in State.OriginalPaths.Keys.ToList())
            if (!Exists(Path.Combine(ItemsDir, name)))
                State.OriginalPaths.Remove(name);
        foreach (var name in State.Spots.Keys.ToList())
            if (!Exists(Path.Combine(ItemsDir, name)))
                State.Spots.Remove(name);

        // Written to a brand-new file beside it and then swapped in: a power cut never leaves half a
        // file, and a link planted under a predictable temp name cannot redirect the write.
        var temp = Path.Combine(Root, $"state.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, State, JsonOptions);
            File.Move(temp, StateFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public List<string> Items() =>
        Directory.EnumerateFileSystemEntries(ItemsDir)
            .OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Moves the given paths under the rug. Returns where they now are and the names
    /// that could not be moved (the shell has already told the user why).
    /// </summary>
    public (List<string> Hidden, List<string> Failed) Hide(IEnumerable<string> paths)
    {
        var sources = paths.Where(CanHide).Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToList();
        var targets = new List<string>();
        foreach (var source in sources)
        {
            // Names already given to earlier items of this drop count as taken.
            targets.Add(UniquePath(ItemsDir, Path.GetFileName(source), targets));
        }
        Shell.Move(sources, targets);

        var hidden = new List<string>();
        var failed = new List<string>();
        for (var i = 0; i < sources.Count; i++)
        {
            // A folder move cut short leaves part of it here: still remember where it came from.
            if (Exists(targets[i])) State.OriginalPaths[Path.GetFileName(targets[i])] = sources[i];
            if (Exists(targets[i]) && !Exists(sources[i])) hidden.Add(targets[i]);
            else failed.Add(Path.GetFileName(sources[i]));
        }
        Save();
        return (hidden, failed);
    }

    public Point? SpotOf(string storedPath) =>
        State.Spots.TryGetValue(Path.GetFileName(storedPath), out var s) && s.Length == 2 ? new Point(s[0], s[1]) : null;

    public void SetSpot(string storedPath, Point share) =>
        State.Spots[Path.GetFileName(storedPath)] = new[] { Math.Clamp(share.X, 0, 1), Math.Clamp(share.Y, 0, 1) };

    /// <summary>Puts an item back where it came from, or on the desktop if that spot is gone.</summary>
    public void Restore(string storedPath)
    {
        var name = Path.GetFileName(storedPath);
        string target;
        // The settings file is only a hint: a missing, odd or relative path sends the item to the desktop.
        if (State.OriginalPaths.TryGetValue(name, out var original)
            && IsSafeReturn(original)
            && Directory.Exists(Path.GetDirectoryName(original))
            && !Exists(original))
            target = original;
        else
        {
            var originalName = Path.GetFileName(original);
            target = UniquePath(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                string.IsNullOrEmpty(originalName) ? name : originalName);
        }

        Shell.Move(new[] { storedPath }, new[] { target });
        if (Exists(storedPath)) throw new IOException("not moved");
        Save();
    }

    static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Where an item may be put back: an ordinary folder on a local drive. Never a network share,
    /// a device path, or a startup folder, where an edited settings file could make a file run.
    /// </summary>
    public static bool IsSafeReturn(string? path)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var full = Path.GetFullPath(path);
        if (!OnLocalDrive(full) || Path.GetDirectoryName(full) is not { } parent) return false;
        // Judge the folder by where it really leads: a mapped drive or a junction can dress up a
        // share or the startup folder as an ordinary path.
        if (Native.FinalPath(parent) is not { } realParent || realParent.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var real = Path.Combine(realParent, Path.GetFileName(full));
        foreach (var startup in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var dir = Environment.GetFolderPath(startup);
            if (dir.Length == 0) continue;
            foreach (var d in new[] { dir, Native.FinalPath(dir) ?? dir })
                foreach (var p in new[] { full, real })
                    if (IsInside(p, d) || SamePath(p, d)) return false;
        }
        return true;
    }

    static string UniquePath(string dir, string name, ICollection<string>? reserved = null)
    {
        bool Taken(string p) => Exists(p) || reserved?.Contains(p, StringComparer.OrdinalIgnoreCase) == true;
        var path = Path.Combine(dir, name);
        if (!Taken(path)) return path;

        var isDir = !Path.HasExtension(name);
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Taken(path)) return path;
        }
    }
}

/// <summary>The rug's folder has been redirected elsewhere; Kilim will not use it.</summary>
public sealed class FolderRedirectedException : IOException
{
    public FolderRedirectedException() : base("The Kilim folder is redirected to another location.") { }
}