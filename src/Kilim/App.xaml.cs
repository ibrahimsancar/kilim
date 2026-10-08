using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Kilim;

public partial class App : Application
{
    // Two copies would draw two rugs over the same hidden folder.
    static Mutex? single;

    protected override void OnStartup(StartupEventArgs e)
    {
        single = new Mutex(true, "Kilim.SingleInstance", out var first);
        if (!first)
        {
            Shutdown();
            return;
        }
        DispatcherUnhandledException += OnCrash;
        try { _ = new Storage(); }
        catch (FolderRedirectedException)
        {
            MessageBox.Show(Text.FolderRedirected, "Kilim", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    // A crash leaves a note next to the hidden items, on this computer only. The items
    // themselves are untouched: they are ordinary files in that folder.
    static void OnCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            // Never written through a link: a planted crash.log must not let Kilim append to another file.
            var log = new FileInfo(Path.Combine(Storage.Root, "crash.log"));
            if (new DirectoryInfo(Storage.Root).Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            if (log.Exists && log.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;
            File.AppendAllText(log.FullName, $"{DateTime.Now:u}\n{e.Exception}\n\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
