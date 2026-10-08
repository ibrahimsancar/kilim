// Safety checks for everything Kilim does with user files.
// Uses the real Kilim folder of the current user, which must be empty when the test starts.
using System.IO;
using Kilim;

var fails = 0;
void Check(string what, bool ok)
{
    Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}");
    if (!ok) fails++;
}

var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
Check("drive root is refused", !Storage.CanHide(@"C:\"));
Check("bare drive is refused", !Storage.CanHide(@"C:"));
Check("Kilim folder is refused", !Storage.CanHide(Storage.Root));
Check("items folder is refused", !Storage.CanHide(Storage.ItemsDir));
Check("a folder containing Kilim is refused", !Storage.CanHide(profile));
Check("Recycle Bin items are refused", !Storage.CanHide(@"C:\$Recycle.Bin\S-1-5-21\$R123.txt"));
Check("relative path is refused", !Storage.CanHide("notes.txt"));
Check("a look-alike folder is not ours", !Storage.IsInside(Path.Combine(Storage.Root, "items2", "a.txt"), Storage.ItemsDir));
Check("a path inside items is ours", Storage.IsInside(Path.Combine(Storage.ItemsDir, "a.txt"), Storage.ItemsDir));

Check("startup folder is no place to restore to",
      !Storage.IsSafeReturn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "a.exe")));
Check("network share is no place to restore to", !Storage.IsSafeReturn(@"\\server\share\a.txt"));
Check("device path is no place to restore to", !Storage.IsSafeReturn(@"\\?\C:\a.txt"));
Check("relative path is no place to restore to", !Storage.IsSafeReturn(@"..\a.txt"));
Check("an ordinary folder is fine to restore to", Storage.IsSafeReturn(Path.Combine(profile, "Documents", "a.txt")));
Check("a network file is refused before it is touched", !Storage.CanHide(@"\\server\share\a.txt"));
Check("the Recycle Bin itself is refused", !Storage.CanHide(@"C:\$Recycle.Bin"));

// Junctions dress one folder up as another; the checks must see where they really lead.
var links = Path.Combine(Path.GetTempPath(), "kilim-links-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(links);
bool Junction(string at, string to)
{
    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{at}\" \"{to}\"")
    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
    p!.WaitForExit();
    return Directory.Exists(at);
}
var toKilim = Path.Combine(links, "looks-harmless");
var toStartup = Path.Combine(links, "also-harmless");
var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
Directory.CreateDirectory(Storage.ItemsDir);
if (Junction(toKilim, Storage.Root))
{
    Check("a junction to Kilim's folder is refused", !Storage.CanHide(toKilim));
    Check("a path through a junction into items is refused", !Storage.CanHide(Path.Combine(toKilim, "items")));
}
else Check("could create a test junction", false);
if (Junction(toStartup, startupDir))
    Check("a junction to the startup folder is no place to restore to", !Storage.IsSafeReturn(Path.Combine(toStartup, "a.exe")));
else Check("could create a test junction", false);
var toRecycle = Path.Combine(links, "bin-in-disguise");
if (Junction(toRecycle, @"C:\$Recycle.Bin"))
    Check("a junction to the Recycle Bin is refused", !Storage.CanHide(toRecycle));
var otherDrive = DriveInfo.GetDrives().FirstOrDefault(d => d.IsReady && d.DriveType == DriveType.Fixed &&
                                                            !string.Equals(d.Name, Path.GetPathRoot(links), StringComparison.OrdinalIgnoreCase));
var toDrive = Path.Combine(links, "whole-drive");
if (otherDrive is not null && Junction(toDrive, otherDrive.Name))
    Check("a junction to another drive's root is refused", !Storage.CanHide(toDrive));
foreach (var j in new[] { toKilim, toStartup, toRecycle, toDrive })
    if (Directory.Exists(j)) Directory.Delete(j); // removes the link only, never what it points to
Directory.Delete(links);

var storage = new Storage();
if (storage.Items().Count > 0)
{
    Console.WriteLine("The rug is not empty; take everything out first.");
    return 2;
}

// A rug that has never been moved has no position yet; saving must still work.
var (left, top) = (storage.State.Left, storage.State.Top);
storage.State.Left = storage.State.Top = double.NaN;
var savedFresh = true;
try { storage.Save(); } catch (Exception) { savedFresh = false; }
Check("a fresh rug with no position can be saved", savedFresh && double.IsNaN(new Storage().State.Left));
(storage.State.Left, storage.State.Top) = (left, top);
storage.Save();

// A settings file edited into odd but valid JSON must not break anything.
var stateFile = Path.Combine(Storage.Root, "state.json");
var keep = File.ReadAllText(stateFile);
File.WriteAllText(stateFile, """{ "Width": 400, "Pattern": null, "OriginalPaths": null, "Spots": { "x.txt": null, "y.txt": [1] } }""");
var odd = new Storage();
Check("odd settings are tidied on load", odd.State.OriginalPaths is not null && odd.State.Spots.Count == 0 && odd.State.Pattern == "sade");
File.WriteAllText(stateFile, keep);
storage = new Storage();

// A real round trip with a name clash: nothing may be overwritten.
var work = Path.Combine(Path.GetTempPath(), "kilim-safety-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(Path.Combine(work, "a"));
Directory.CreateDirectory(Path.Combine(work, "b"));
File.WriteAllText(Path.Combine(work, "a", "same.txt"), "first");
File.WriteAllText(Path.Combine(work, "b", "same.txt"), "second");
Check("an ordinary file is accepted", Storage.CanHide(Path.Combine(work, "a", "same.txt")));

var (hidden, failed) = storage.Hide(new[] { Path.Combine(work, "a", "same.txt"), Path.Combine(work, "b", "same.txt") });
Check("both files hidden", hidden.Count == 2 && failed.Count == 0);
Check("nothing overwritten: both contents kept",
      hidden.Select(File.ReadAllText).OrderBy(x => x).SequenceEqual(new[] { "first", "second" }));
Check("sources moved away", !File.Exists(Path.Combine(work, "a", "same.txt")) && !File.Exists(Path.Combine(work, "b", "same.txt")));

foreach (var h in hidden) storage.Restore(h);
Check("restored with the right contents",
      File.ReadAllText(Path.Combine(work, "a", "same.txt")) == "first" && File.ReadAllText(Path.Combine(work, "b", "same.txt")) == "second");
Check("nothing left under the rug", storage.Items().Count == 0);
Check("state file is valid JSON", System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Storage.Root, "state.json"))) is not null);
Check("no half-written state file left", Directory.GetFiles(Storage.Root, "*.tmp").Length == 0);

Directory.Delete(work, true);
Console.WriteLine(fails == 0 ? "All checks passed" : $"{fails} checks failed");
return fails == 0 ? 0 : 1;
