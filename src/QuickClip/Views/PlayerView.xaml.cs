using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Mpv;
using QuickClip.Shell;

namespace QuickClip.Views;

/// <summary>
/// Watches clips from the gallery: the whole file with all its audio tracks, previous/next through the folder,
/// full screen with controls that get out of the way while playing.
/// </summary>
public partial class PlayerView : UserControl
{
    private const string PlayGlyph = "\uE768", PauseGlyph = "\uE769", FullScreenGlyph = "\uE740", ExitFullScreenGlyph = "\uE73F";
    private const int MessageOverlayId = 1;

    private readonly DispatcherTimer _childWindowTimer;
    private readonly DispatcherTimer _hideTimer;
    private MpvPlayer? _player;
    private PreviewAudio? _audio;
    private List<ClipItem> _clips = [];
    private int _index = -1;
    private int _openToken;
    private bool _openWhenLoaded;
    private double _duration, _mpvTimePos;
    private int _timePosQueued;
    private bool _paused = true, _eof, _dragging, _showingPosition, _fullScreen, _controlsHidden;
    private long _openedAt, _lastClickAt;
    private (int X, int Y) _lastMouse = (-1, -1);

    public event Action? BackRequested;
    public event Action<string>? EditRequested;
    /// <summary>The window should enter (true) or leave full screen.</summary>
    public event Action<bool>? FullScreenRequested;
    public event Action? TitleChanged;

    public string? ClipTitle => Current?.Title;

    private ClipItem? Current => _index >= 0 && _index < _clips.Count ? _clips[_index] : null;

    public PlayerView()
    {
        InitializeComponent();
        // mpv creates (and may recreate) its child window asynchronously; keep it disabled so clicks reach us.
        _childWindowTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => Video.DisableChildWindows(), Dispatcher) { IsEnabled = false };
        _hideTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.5), DispatcherPriority.Background, (_, _) => HideControls(), Dispatcher)
            { IsEnabled = false };

        Video.VideoMouseDown += OnVideoMouseDown;
        Video.VideoMouseMove += OnVideoMouseMove;
        Video.CursorQuery = (_, _) => _controlsHidden ? -1 : 0;
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _dragging = false;
            Seek(SeekBar.Value);
        }));
        PreviewKeyDown += OnPreviewKeyDown;
        // Hidden with the window (minimized to the tray, say): don't keep playing unseen.
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) _player?.SetProperty("pause", true); };
        Loaded += OnLoaded;
    }

    /// <summary>Plays <paramref name="clips"/>[<paramref name="index"/>]; previous and next move through the list.</summary>
    public void Play(IReadOnlyList<ClipItem> clips, int index)
    {
        _clips = [.. clips];
        _index = index;
        _openedAt = Environment.TickCount64;
        if (_player == null) _openWhenLoaded = true;
        else _ = OpenAsync(index);
    }

    /// <summary>Stops playback and lets go of the file (when leaving the player).</summary>
    public void Unload()
    {
        _openToken++;
        _player?.Command("stop");
        ShowMessage(null);
    }

    /// <summary>Gives keyboard focus to the player so its shortcuts work.</summary>
    public void FocusPlayer() => Root.Focus();

    /// <summary>Set by the window as it enters or leaves full screen.</summary>
    public bool IsFullScreen
    {
        get => _fullScreen;
        set
        {
            _fullScreen = value;
            Header.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            FullScreenButton.Content = value ? ExitFullScreenGlyph : FullScreenGlyph;
            FullScreenButton.ToolTip = value ? "Exit full screen (F or Esc)" : "Full screen (F)";
            ShowControls();
        }
    }

    public async Task CloseAsync()
    {
        _childWindowTimer.Stop();
        _hideTimer.Stop();
        // mpv tears down its child window during shutdown, which needs this thread to keep pumping.
        var player = _player;
        _player = null;
        _audio = null;
        if (player != null) await Task.Run(player.Dispose);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_player != null) return;
        try
        {
            _player = new MpvPlayer(Video.Hwnd);
            _audio = new PreviewAudio(_player);
            _player.PropertyChanged += OnMpvProperty;
            _player.EventReceived += OnMpvEvent;
        }
        catch (Exception ex)
        {
            Log.Write("mpv init failed: " + ex);
            InfoText.Text = "The video player could not be started: " + ex.Message;
            return;
        }
        _childWindowTimer.Start();
        if (_openWhenLoaded)
        {
            _openWhenLoaded = false;
            _ = OpenAsync(_index);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Opening clips
    // ------------------------------------------------------------------------------------------

    private async Task OpenAsync(int index)
    {
        if (_player == null || index < 0 || index >= _clips.Count) return;
        int token = ++_openToken;
        _index = index;
        var clip = _clips[index];
        TitleText.Text = clip.Title;
        InfoText.Text = string.Join("  ·  ", new[] { clip.Folder, clip.Ago, clip.SizeText, $"{index + 1} of {_clips.Count}" }.Where(s => s.Length > 0));
        PreviousButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _clips.Count - 1;
        TitleChanged?.Invoke();
        _duration = clip.Duration ?? 0;
        _mpvTimePos = 0;
        ShowPosition(0);
        ShowMessage(null);

        // Every audio track plays (Desktop, Chat, Music, Mic...), so the track count is needed before loading.
        int tracks = 1;
        try
        {
            var info = await MediaInfo.ProbeAsync(clip.Path);
            tracks = info.Audio.Count;
            if (info.Duration > 0) _duration = info.Duration;
        }
        catch (Exception ex)
        {
            Log.Write($"Probing {clip.Path} failed: {ex.Message}"); // mpv may still manage
        }
        if (token != _openToken || _player == null || _audio == null) return;

        _eof = false;
        _player.Command("stop");
        _audio.Configure(tracks, _ => 1);
        _player.SetProperty("pause", false);
        _player.CommandAsync("loadfile", clip.Path, "replace");
        ShowPosition(0);
    }

    private void Step(int delta)
    {
        int index = _index + delta;
        if (index < 0 || index >= _clips.Count) return;
        _openedAt = 0;
        _ = OpenAsync(index);
    }

    // ------------------------------------------------------------------------------------------
    // mpv events (arrive on mpv's thread)
    // ------------------------------------------------------------------------------------------

    private void OnMpvProperty(string name, object? value)
    {
        if (name == "time-pos")
        {
            // Coalesce: at most one pending UI update for the (frequent) position changes.
            _mpvTimePos = value is double d ? d : 0;
            if (Interlocked.Exchange(ref _timePosQueued, 1) == 0)
                Dispatcher.BeginInvoke(FlushTimePos, DispatcherPriority.Input);
            return;
        }
        Dispatcher.BeginInvoke(() => HandleMpvProperty(name, value));
    }

    private void FlushTimePos()
    {
        _timePosQueued = 0;
        if (!_dragging) ShowPosition(_mpvTimePos);
    }

    private void HandleMpvProperty(string name, object? value)
    {
        switch (name)
        {
            case "pause":
                _paused = value is true;
                PlayButton.Content = _paused ? PlayGlyph : PauseGlyph;
                ShowControls();
                break;
            case "eof-reached":
                _eof = value is true;
                break;
            case "duration":
                if (value is double d && d > 0)
                {
                    _duration = d;
                    ShowPosition(_mpvTimePos);
                }
                break;
        }
    }

    private void OnMpvEvent(MpvEventId id, int error) => Dispatcher.BeginInvoke(() =>
    {
        if (id is MpvEventId.FileLoaded or MpvEventId.VideoReconfig) Video.DisableChildWindows();
        if (id == MpvEventId.EndFile && error < 0 && Current != null)
        {
            ShowMessage("This clip can't be played");
            Log.Write("mpv end-file error: " + MpvNative.ErrorString(error));
        }
    });

    /// <summary>Centered text drawn by mpv over the video area (null clears it).</summary>
    private void ShowMessage(string? text)
    {
        if (_player == null) return;
        if (string.IsNullOrEmpty(text))
            _player.Command("osd-overlay", MessageOverlayId.ToString(), "none", "");
        else
            _player.Command("osd-overlay", MessageOverlayId.ToString(), "ass-events",
                @"{\an5\fnSegoe UI\fs28\bord0\shad0\1c&H909090&}" + text, "0", "720");
    }

    // ------------------------------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------------------------------

    private void ShowPosition(double t)
    {
        _showingPosition = true;
        SeekBar.Maximum = Math.Max(_duration, 0.001);
        SeekBar.Value = Math.Clamp(t, 0, SeekBar.Maximum);
        _showingPosition = false;
        TimeText.Text = Fmt.Time(t, millis: false);
        DurationText.Text = " / " + Fmt.Time(_duration, millis: false);
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_showingPosition) return;
        TimeText.Text = Fmt.Time(e.NewValue, millis: false);
        // Keyframes only while dragging, so the picture keeps up; exact once let go (and for clicks).
        Seek(e.NewValue, exact: !_dragging);
    }

    private void Seek(double t, bool exact = true)
    {
        if (_player == null || Current == null) return;
        t = Math.Clamp(t, 0, Math.Max(_duration, 0));
        _player.CommandAsync("seek", Fmt.Num(t, "0.######"), exact ? "absolute+exact" : "absolute+keyframes");
    }

    private void SeekRelative(double seconds)
    {
        if (_player != null && Current != null) _player.CommandAsync("seek", Fmt.Num(seconds), "relative+exact");
    }

    private void TogglePlay()
    {
        if (_player == null || Current == null) return;
        if (_paused && (_eof || _mpvTimePos >= _duration - 0.05)) Seek(0);
        _player.SetProperty("pause", !_paused);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void FullScreen_Click(object sender, RoutedEventArgs e) => FullScreenRequested?.Invoke(!_fullScreen);

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } clip) EditRequested?.Invoke(clip.Path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Current is { } clip) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{clip.Path}\"") { UseShellExecute = true });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Current is not { } clip || _player == null) return;
        Unload();
        // mpv lets go of the file as it stops, which can take a moment.
        bool deleted = false;
        for (int attempt = 0; attempt < 20 && !(deleted = RecycleBin.TrySend(clip.Path)); attempt++)
            await Task.Delay(100);
        if (!deleted)
        {
            MessageBox.Show(Window.GetWindow(this), $"Couldn't move \"{Path.GetFileName(clip.Path)}\" to the Recycle Bin.", "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            _ = OpenAsync(_index);
            return;
        }
        _clips.RemoveAt(_index);
        if (_clips.Count == 0) BackRequested?.Invoke();
        else _ = OpenAsync(Math.Min(_index, _clips.Count - 1));
    }

    // ------------------------------------------------------------------------------------------
    // Mouse, keyboard and the controls hiding in full screen
    // ------------------------------------------------------------------------------------------

    private void OnVideoMouseDown(int x, int y)
    {
        Root.Focus();
        long now = Environment.TickCount64;
        uint doubleClick = GetDoubleClickTime();
        // The second click of a double-click on a gallery card lands here; it shouldn't pause the new clip.
        if (now - _openedAt < doubleClick) return;
        if (now - _lastClickAt < doubleClick)
        {
            // Double-click: full screen, and undo the pause/play of its first click.
            _lastClickAt = 0;
            TogglePlay();
            FullScreenRequested?.Invoke(!_fullScreen);
            return;
        }
        _lastClickAt = now;
        TogglePlay();
    }

    private void OnVideoMouseMove(int x, int y)
    {
        // Layout changes (like the controls hiding) send a move without the mouse moving.
        if ((x, y) == _lastMouse) return;
        _lastMouse = (x, y);
        ShowControls();
    }

    private void ControlBar_MouseMove(object sender, MouseEventArgs e) => ShowControls();

    private void ShowControls()
    {
        if (_controlsHidden)
        {
            _controlsHidden = false;
            ControlBar.Visibility = Visibility.Visible;
            Video.RefreshCursor();
        }
        _hideTimer.Stop();
        if (_fullScreen && !_paused) _hideTimer.Start();
    }

    private void HideControls()
    {
        _hideTimer.Stop();
        if (!_fullScreen || _paused || ControlBar.IsMouseOver) return;
        _controlsHidden = true;
        ControlBar.Visibility = Visibility.Collapsed;
        Video.RefreshCursor();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        switch (e.Key)
        {
            case Key.Space or Key.K: TogglePlay(); break;
            case Key.Left: SeekRelative(shift ? -1 : -5); break;
            case Key.Right: SeekRelative(shift ? 1 : 5); break;
            case Key.J: SeekRelative(-10); break;
            case Key.L: SeekRelative(10); break;
            case Key.OemComma: _player?.CommandAsync("frame-back-step"); break;
            case Key.OemPeriod: _player?.CommandAsync("frame-step"); break;
            case Key.Home: Seek(0); break;
            case Key.Up: Volume.Nudge(5); break;
            case Key.Down: Volume.Nudge(-5); break;
            case Key.M: Volume.ToggleMute(); break;
            case Key.F or Key.F11: FullScreenRequested?.Invoke(!_fullScreen); break;
            case Key.Escape when _fullScreen: FullScreenRequested?.Invoke(false); break;
            case Key.Escape or Key.Back: BackRequested?.Invoke(); break;
            case Key.PageUp: Step(-1); break;
            case Key.PageDown: Step(1); break;
            case Key.E: Edit_Click(this, e); break;
            case Key.Delete: Delete_Click(this, e); break;
            default: handled = false; break;
        }
        e.Handled = handled;
        if (handled) ShowControls();
    }

    [DllImport("user32.dll")] private static extern uint GetDoubleClickTime();

#if DEBUG
    internal string DebugState => $"clip={Current?.Title} pos={_mpvTimePos:0.00} duration={_duration:0.00} paused={_paused} fullscreen={_fullScreen}";

    /// <summary>What mpv is showing (page renders can't capture the video window).</summary>
    internal void SaveVideoForRender(string path) => _player?.Command("screenshot-to-file", path, "window");
#endif
}
