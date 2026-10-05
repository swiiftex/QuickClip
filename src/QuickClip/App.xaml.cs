using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip;

public partial class App : Application
{
    /// <summary>Tray icon, hotkey and recorder; alive for the whole session.</summary>
    internal static AppHost? Host { get; private set; }

    private bool _quitting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

        bool renderTest = false;
#if DEBUG
        // Page renders run beside a normal QuickClip, without recording or touching its settings.
        renderTest = args.Any(a => a.StartsWith("--render=", StringComparison.Ordinal));
        if (renderTest)
        {
            AppSettings.SaveDisabled = true;
            AppSettings.Current.RecordingEnabled = false;
        }
#endif

        // One QuickClip per user: a second launch (e.g. "Edit with QuickClip") hands its file to the first.
        if (!renderTest && !SingleInstance.TryBecomePrimary())
        {
            SingleInstance.SendToPrimary(args);
            Shutdown(0);
            return;
        }
        if (args.Contains("--quit") || args.Contains("--save-clip"))
        {
            Shutdown(0); // nothing running to talk to
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

        Host = new AppHost();
        Host.OpenRequested += () => ShowMain();
        Host.UpdateRequested += () =>
        {
            ShowMain();
            (MainWindow as QuickClip.MainWindow)?.ShowSettings();
        };
        Host.QuitRequested += Quit;
        Host.Start();
        if (!renderTest) SingleInstance.Listen(a => Dispatcher.BeginInvoke(() => HandleArgs(a)));

        bool tray = args.Contains("--tray");
        string? file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
#if DEBUG
        // Test runs: point clips at a scratch folder without touching the saved settings.
        if (args.FirstOrDefault(a => a.StartsWith("--clips=", StringComparison.Ordinal)) is { } clipsArg)
        {
            AppSettings.SaveDisabled = true;
            AppSettings.Current.ClipsFolder = clipsArg["--clips=".Length..];
        }
        if (args.FirstOrDefault(a => a.StartsWith("--render=", StringComparison.Ordinal)) is { } render)
        {
            AppSettings.SaveDisabled = true;
            ShowMain();

            var window = (QuickClip.MainWindow)MainWindow;
            Dispatcher.BeginInvoke(async () =>
            {
                try { await window.RenderPagesAsync(render["--render=".Length..], file); }
                catch (Exception ex) { Log.Write("render failed: " + ex); }
                Quit();
            }, DispatcherPriority.ApplicationIdle);
            return;
        }
#endif
        if (!tray || file != null) ShowMain(file);
        else TrimMemorySoon();
    }

    /// <summary>
    /// Start-up (theme parsing, device probing) leaves a lot of garbage that the GC may not collect for a long
    /// time in a mostly idle tray app; collect it and hand the pages back once things have settled.
    /// </summary>
    private void TrimMemorySoon() =>
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(6000);
            if (MainWindow == null) TrimMemory();
        });

    private static void TrimMemory()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    /// <summary>Arguments from a later launch: a file to edit, or a command for the running QuickClip.</summary>
    private void HandleArgs(string[] args)
    {
        if (args.Contains("--quit"))
        {
            Quit();
            return;
        }
        if (args.Contains("--save-clip"))
        {
            _ = Recorder.Instance.SaveClipAsync();
            return;
        }
        string? file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        if (args.Contains("--tray") && file == null) return;
        ShowMain(file);
    }

    /// <summary>Shows (or recreates) the window, optionally opening a file in the editor.</summary>
    internal void ShowMain(string? file = null)
    {
        if (_quitting) return;
        if (MainWindow is not QuickClip.MainWindow window || !window.IsLoaded)
        {
            window = new QuickClip.MainWindow();
            MainWindow = window;
            window.Show();
        }
        else
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        }
        window.Activate();
        if (file != null) _ = window.OpenInEditorAsync(file);
    }

    /// <summary>Called when the window closes: keep running in the tray, or quit.</summary>
    internal void OnMainWindowClosed()
    {
        MainWindow = null;
        if (_quitting || !AppSettings.Current.CloseToTray)
        {
            Quit();
            return;
        }
        // Give back the memory the window used; the recorder carries on.
        TrimMemorySoon();
    }

    internal async void Quit() => await QuitAsync();

    /// <summary>
    /// Closes the window, stops recording and exits. <paramref name="beforeExit"/> runs only once the exit is certain
    /// (it's how an update gets installed). Returns false if the user kept the window open.
    /// </summary>
    internal async Task<bool> QuitAsync(Action? beforeExit = null)
    {
        if (_quitting) return true;
        _quitting = true;
        if (MainWindow is QuickClip.MainWindow window && window.IsLoaded && !await window.RequestCloseAsync())
        {
            _quitting = false; // the user kept the window open
            return false;
        }
        await Recorder.Instance.StopAsync();
        Host?.Dispose();
        AppSettings.Current.Save();
        beforeExit?.Invoke();
        Shutdown(0);
        return true;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Write("Unhandled: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "QuickClip – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
}
