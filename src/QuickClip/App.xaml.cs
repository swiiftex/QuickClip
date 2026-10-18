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

    // The window, tracked here rather than through Application.MainWindow, which WPF can point at another window
    // (and then a second QuickClip window would open beside this one).
    private QuickClip.MainWindow? _main;

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
            _renderOffscreen = true;
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
            _main?.ShowSettings();
        };
        Host.QuitRequested += Quit;
        Host.Start();
        if (!renderTest) SingleInstance.Listen(a => Dispatcher.BeginInvoke(() => HandleArgs(a)));

        bool tray = args.Contains("--tray");
        string? file = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
#if DEBUG
        // Test runs: point clips at a scratch folder without touching the saved settings.
        string? clipsArg = args.FirstOrDefault(a => a.StartsWith("--clips=", StringComparison.Ordinal));
        if (clipsArg != null)
        {
            AppSettings.SaveDisabled = true;
            AppSettings.Current.ClipsFolder = clipsArg["--clips=".Length..];
        }
        if (args.FirstOrDefault(a => a.StartsWith("--render=", StringComparison.Ordinal)) is { } render)
        {
            AppSettings.SaveDisabled = true;
            // Renders make projects and exports, and show the gallery: never in the real clips folder.
            if (clipsArg == null)
            {
                AppSettings.Current.ClipsFolder = Path.Combine(render["--render=".Length..], "clips");
                Directory.CreateDirectory(AppSettings.Current.ClipsFolder);
            }
            ShowMain();

            var window = _main!;
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
            if (_main == null) TrimMemory();
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

    private bool _renderOffscreen;

    /// <summary>Shows (or recreates) the window, optionally opening a file in the editor.</summary>
    internal void ShowMain(string? file = null)
    {
        if (_quitting) return;
        if (_main is not { IsLoaded: true } window)
        {
            window = _main = new QuickClip.MainWindow();
            MainWindow = window;
            if (_renderOffscreen)
            {
                // Page renders don't need to be seen: off the screen, without taking focus from whatever's in front.
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = 0;
            }
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
    internal void OnMainWindowClosed(QuickClip.MainWindow window)
    {
        if (window != _main) return; // not the window QuickClip is using
        _main = null;
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
        if (_main is { IsLoaded: true } window && !await window.RequestCloseAsync())
        {
            _quitting = false; // the user kept the window open
            return false;
        }
        await Recorder.Instance.StopAsync();
        Host?.Dispose();
        AppSettings.Current.Save();
        ShellClipboard.KeepAfterExit();
        Log.Flush();
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
