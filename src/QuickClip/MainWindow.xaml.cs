using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Updates;
using QuickClip.Views;

namespace QuickClip;

/// <summary>App window: navigation between the gallery (and its player), the editor and settings, plus the recording status bar.</summary>
public partial class MainWindow : Window
{
    private static readonly Brush IdleDot = Freeze(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)));

    private readonly DispatcherTimer _statusTimer;
    private EditorView? _editor;
    private PlayerView? _player;
    private SettingsView? _settings;
    private bool _closeReady;
    private WindowState _stateBeforeFullScreen;

    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width * 0.95);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.95);

        Gallery.OpenRequested += path => _ = OpenInEditorAsync(path);
        Gallery.PlayRequested += (clips, index) =>
        {
            ShowPage(PlayerPage);
            PlayerPage.Play(clips, index);
        };
        Recorder.Instance.StateChanged += OnRecorderState;
        Updater.Changed += OnUpdaterChanged;
        OnUpdaterChanged();
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateStatus(), Dispatcher);
        Loaded += (_, _) =>
        {
            UpdateStatus();
            _statusTimer.Start();
        };
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) return;
            // Coming back from the taskbar or the tray then shows the ordinary window.
            SetFullScreen(false);
            if (AppSettings.Current.MinimizeToTray) Hide();
        };
    }

    public async Task OpenInEditorAsync(string path)
    {
        ShowPage(EditorPage);
        await EditorPage.OpenAsync(path);
    }

    public void ShowSettings() => ShowPage(SettingsPage);

    private EditorView EditorPage
    {
        get
        {
            if (_editor != null) return _editor;
            _editor = new EditorView { Visibility = Visibility.Collapsed };
            _editor.TitleChanged += UpdateTitle;
            Pages.Children.Add(_editor);
            return _editor;
        }
    }

    private PlayerView PlayerPage
    {
        get
        {
            if (_player != null) return _player;
            _player = new PlayerView { Visibility = Visibility.Collapsed };
            _player.BackRequested += () => ShowPage(Gallery);
            _player.EditRequested += path => _ = OpenInEditorAsync(path);
            _player.FullScreenRequested += SetFullScreen;
            _player.TitleChanged += UpdateTitle;
            Pages.Children.Add(_player);
            return _player;
        }
    }

    private SettingsView SettingsPage
    {
        get
        {
            if (_settings != null) return _settings;
            _settings = new SettingsView { Visibility = Visibility.Collapsed };
            Pages.Children.Add(_settings);
            return _settings;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        if (sender == GalleryNav) ShowPage(Gallery);
        else if (sender == EditorNav) ShowPage(EditorPage);
        else if (sender == SettingsNav) ShowPage(SettingsPage);
    }

    // Clicking Gallery while the player (part of the gallery) is open goes back to the clips.
    private void GalleryNav_Click(object sender, RoutedEventArgs e) => ShowPage(Gallery);

    private void ShowPage(UIElement page)
    {
        if (page != _player && _player != null)
        {
            SetFullScreen(false);
            _player.Unload();
        }
        foreach (UIElement child in Pages.Children)
            child.Visibility = child == page ? Visibility.Visible : Visibility.Collapsed;
        GalleryNav.IsChecked = page == Gallery || page == _player;
        EditorNav.IsChecked = page == _editor;
        SettingsNav.IsChecked = page == _settings;
        if (page == _editor) Dispatcher.BeginInvoke(_editor.FocusEditor, DispatcherPriority.Input);
        if (page == _player) Dispatcher.BeginInvoke(_player.FocusPlayer, DispatcherPriority.Input);
        if (page == _settings) _settings.Refresh();
        UpdateTitle();
    }

    private void UpdateTitle() =>
        Title = _player?.IsVisible == true && _player.ClipTitle is string clip ? $"{clip} – QuickClip"
            : EditorNav.IsChecked == true && _editor?.FileTitle is string file ? $"{file} – QuickClip"
            : "QuickClip";

    /// <summary>Video fills the screen: no title bar, taskbar, navigation or status bar.</summary>
    private void SetFullScreen(bool on)
    {
        if (_player == null || on == _player.IsFullScreen) return;
        ApplyFullScreenLayout(on);
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Maximizing again after dropping the border makes the window cover the taskbar too.
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (WindowState != WindowState.Minimized) WindowState = _stateBeforeFullScreen;
        }
    }

    private void ApplyFullScreenLayout(bool on)
    {
        RailColumn.Width = new GridLength(on ? 0 : 80);
        Rail.Visibility = StatusBar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        _player!.IsFullScreen = on;
    }

    private void OnRecorderState(RecorderState state) => Dispatcher.BeginInvoke(UpdateStatus);

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(() =>
    {
        var release = Updater.Available;
        UpdateButton.Visibility = release != null ? Visibility.Visible : Visibility.Collapsed;
        if (release != null) UpdateButtonText.Text = $"Update to {release.Version.Display}";
    });

    private void UpdateButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void UpdateStatus()
    {
        var recorder = Recorder.Instance;
        var s = AppSettings.Current;
        var hotkey = new Hotkey(s.HotkeyModifiers, s.HotkeyKey);
        if (recorder.IsRunning)
        {
            var stats = recorder.Stats();
            RecDot.Fill = (Brush)FindResource("RecordingRed");
            string buffered = RecordingPresets.DurationLabel((int)Math.Round(stats.BufferSeconds));
            string hotkeyText = App.Host?.HotkeyActive == false ? "the hotkey is unavailable (see Settings)" : $"{hotkey} saves a clip";
            RecStatus.Text = $"Recording  ·  {recorder.State.Summary}  ·  {buffered} buffered ({Fmt.Size(stats.BufferBytes)})  ·  {hotkeyText}";
        }
        else
        {
            RecDot.Fill = IdleDot;
            RecStatus.Text = recorder.Error != null ? "Not recording: " + recorder.Error
                : s.RecordingEnabled ? "Starting…" : "Recording is off — turn on instant replay in Settings";
        }
        SaveClipButton.IsEnabled = recorder.IsRunning;
    }

    private async void SaveClip_Click(object sender, RoutedEventArgs e)
    {
        SaveClipButton.IsEnabled = false;
        SaveClipText.Text = "Saving…";
        try
        {
            await Recorder.Instance.SaveClipAsync();
        }
        finally
        {
            SaveClipText.Text = "Save clip";
            UpdateStatus();
        }
    }

    /// <summary>Closes the window; false if the user kept it open (e.g. to let an export finish).</summary>
    public Task<bool> RequestCloseAsync()
    {
        _closeResult = new TaskCompletionSource<bool>();
        Close();
        return _closeResult.Task;
    }

    private TaskCompletionSource<bool>? _closeResult;

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closeReady)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        if (_editor != null && !await _editor.CloseAsync())
        {
            _closeResult?.TrySetResult(false);
            return;
        }
        if (_player != null) await _player.CloseAsync();
        Gallery.Release();
        _closeReady = true;
        // Close for real once this handler has returned; WPF refuses a Close() from inside Closing.
        _ = Dispatcher.BeginInvoke(Close);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeResult?.TrySetResult(true);
        _statusTimer.Stop();
        Recorder.Instance.StateChanged -= OnRecorderState;
        Updater.Changed -= OnUpdaterChanged;
        base.OnClosed(e);
        ((App)Application.Current).OnMainWindowClosed();
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
