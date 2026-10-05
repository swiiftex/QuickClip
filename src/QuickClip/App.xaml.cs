using System.Windows;
using System.Windows.Threading;
using QuickClip.Shell;

namespace QuickClip;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var args = e.Args;
        if (args.Length > 0 && args[0] is "--register" or "--unregister")
        {
            try
            {
                if (args[0] == "--register") ShellIntegration.Register();
                else ShellIntegration.Unregister();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                Log.Write($"{args[0]} failed: {ex}");
                Shutdown(1);
            }
            return;
        }

        var missing = new List<string>();
        if (!Deps.HasLibMpv) missing.Add("libmpv-2.dll (video preview)");
        if (Deps.FFmpeg == null) missing.Add("ffmpeg.exe (export)");
        if (Deps.FFprobe == null) missing.Add("ffprobe.exe (reading files)");
        if (missing.Count > 0)
        {
            MessageBox.Show(
                "QuickClip is missing some of its components:\n\n  • " + string.Join("\n  • ", missing) +
                "\n\nRun scripts\\get-deps.ps1 from the QuickClip source folder and rebuild, or reinstall QuickClip.",
                "QuickClip", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        string? file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        var window = new MainWindow(file);
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "QuickClip – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
