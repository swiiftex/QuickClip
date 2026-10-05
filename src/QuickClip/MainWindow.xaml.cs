using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Controls;
using QuickClip.Media;
using QuickClip.Mpv;
using QuickClip.Shell;
using Container = QuickClip.Media.Container;

namespace QuickClip;

public partial class MainWindow : Window
{
    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";
    private const int CropOverlayId = 1;
    private const int MessageOverlayId = 2;

    private readonly string? _initialFile;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly TimelineState _timeline = new();
    private readonly CropController _crop = new();
    private readonly ObservableCollection<AudioTrack> _tracks = [];
    private readonly DispatcherTimer _childWindowTimer;
    private readonly DispatcherTimer _audioSyncTimer;

    private MpvPlayer? _player;
    private PreviewAudio? _previewAudio;
    private MediaInfo? _media;
    private HashSet<string> _encoders = ["libx264", "libx265", "libsvtav1", "libvpx-vp9", "libopus", "libmp3lame"];
    private CancellationTokenSource? _waveCts;
    private CancellationTokenSource? _exportCts;
    private string? _exportPath;
    private string? _lastExport;

    private bool _paused = true;
    private bool _eof;
    private bool _idle = true;
    private bool _scrubbing;
    private bool _loading;
    private bool _uiReady;
    private bool _refreshingEncoders;
    private bool _closingDone;
    private int _timePosQueued;
    private double _mpvTimePos;

    public MainWindow(string? file)
    {
        _initialFile = file;
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width * 0.95);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height * 0.95);

        TrackList.ItemsSource = _tracks;
        Timeline.State = _timeline;
        Timeline.SeekRequested += OnSeekRequested;
        WaveformLane.SeekRequested += OnSeekRequested;
        _timeline.SelectionChanged += OnSelectionChanged;
        _crop.Changed += OnCropChanged;

        Video.CursorQuery = (x, y) => _media?.Video == null ? VideoHost.IDC_ARROW : _crop.CursorAt(x, y);
        Video.VideoMouseDown += OnVideoMouseDown;
        Video.VideoMouseMove += (x, y) => { if (_crop.Enabled) _crop.MouseMove(x, y); };
        Video.VideoMouseUp += (_, _) => { if (_crop.Enabled) _crop.MouseUp(); };

        // mpv creates (and may recreate) its child window asynchronously; keep it disabled so clicks reach us.
        _childWindowTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => Video.DisableChildWindows(), Dispatcher);
        _audioSyncTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
            (_, _) => SyncPreviewAudio(), Dispatcher) { IsEnabled = false };

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Drop += OnDrop;
        DragOver += OnDragOver;
        Loaded += OnLoaded;

        foreach (var combo in new[] { AspectCombo, FormatCombo, VideoModeCombo, EncoderCombo, QualityCombo, ResolutionCombo, FpsCombo, AudioBitrateCombo })
            combo.DropDownClosed += ComboBox_DropDownClosed;

        InitOptionLists();
        UpdateUiState();
    }

    // ------------------------------------------------------------------------------------------
    // Startup / shutdown
    // ------------------------------------------------------------------------------------------

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            _player = new MpvPlayer(Video.Hwnd);
            _previewAudio = new PreviewAudio(_player);
            _player.PropertyChanged += OnMpvProperty;
            _player.EventReceived += OnMpvEvent;
        }
        catch (Exception ex)
        {
            Log.Write("mpv init failed: " + ex);
            MessageBox.Show(this, "The video preview could not be started:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _childWindowTimer.Start();
        ShowMessage("Open a video or drop it here");

        ShellCheck.IsChecked = SafeIsRegistered();
        if (ShellIntegration.IsMenuPackageInstalled())
        {
            // The installer's Windows 11 menu package owns the entry; it's removed with uninstall.ps1.
            ShellCheck.IsEnabled = false;
            ShellCheck.ToolTip = "Installed on the Windows 11 right-click menu. Run scripts\\uninstall.ps1 to remove it.";
        }
        LoopToggle.IsChecked = _settings.Loop;
        MergeCheck.IsChecked = _settings.MergeAudio;
        _uiReady = true;

        if (_initialFile != null) await OpenFileAsync(_initialFile);

        _encoders = await VideoEncoders.GetAvailableAsync();
        RefreshEncoderList();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingDone)
        {
            base.OnClosing(e);
            return;
        }
        if (_exportCts != null)
        {
            var answer = MessageBox.Show(this, "An export is still running. Cancel it and close?", "QuickClip",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _exportCts.Cancel();
        }

        e.Cancel = true;
        SaveSettings();
        _waveCts?.Cancel();
        _childWindowTimer.Stop();
        _audioSyncTimer.Stop();
        Hide();

        // mpv tears down its child window during shutdown, which needs this thread to keep pumping.
        var player = _player;
        _player = null;
        _previewAudio = null;
        if (player != null) await Task.Run(player.Dispose);
        if (_exportPath != null) TryDelete(_exportPath);

        _closingDone = true;
        Close();
    }

    private void SaveSettings()
    {
        _settings.Loop = LoopToggle.IsChecked == true;
        _settings.MergeAudio = MergeCheck.IsChecked == true;
        if (FormatCombo.SelectedItem is Choice f) _settings.Format = (string)f.Value;
        if (VideoModeCombo.SelectedItem is Choice m) _settings.VideoMode = (string)m.Value;
        if (QualityCombo.SelectedItem is Choice q) _settings.Quality = (string)q.Value;
        if (ResolutionCombo.SelectedItem is Choice r) _settings.Resolution = (int)r.Value;
        if (FpsCombo.SelectedItem is Choice fps) _settings.Fps = (double)fps.Value;
        if (AudioBitrateCombo.SelectedItem is Choice ab) _settings.AudioBitrate = (int)ab.Value;
        if (double.TryParse(TargetSizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var mb) && mb > 0)
            _settings.TargetSizeMB = mb;
        _settings.Save();
    }

    // ------------------------------------------------------------------------------------------
    // Opening files
    // ------------------------------------------------------------------------------------------

    private async void Open_Click(object sender, RoutedEventArgs e) => await PromptOpenAsync();

    private bool CanOpenAnother()
    {
        if (_exportCts == null) return true;
        MessageBox.Show(this, "Please wait for the export to finish first.", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private async Task PromptOpenAsync()
    {
        if (!CanOpenAnother()) return;
        var exts = string.Join(";", ShellIntegration.Extensions.Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Title = "Open a video or audio file",
            Filter = $"Media files|{exts}|All files|*.*",
            InitialDirectory = _media != null ? Path.GetDirectoryName(_media.Path) : _settings.LastFolder,
        };
        if (dlg.ShowDialog(this) == true)
            await OpenFileAsync(dlg.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && CanOpenAnother())
            await OpenFileAsync(files[0]);
    }

    private async Task OpenFileAsync(string path)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            path = Path.GetFullPath(path);
            MediaInfo info;
            try
            {
                Mouse.OverrideCursor = Cursors.AppStarting;
                info = await MediaInfo.ProbeAsync(path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open \"{Path.GetFileName(path)}\":\n\n{ex.Message}", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            _waveCts?.Cancel();
            _media = info;
            _settings.LastFolder = Path.GetDirectoryName(path);
            _lastExport = null;
            RevealButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "";

            Title = $"{Path.GetFileName(path)} – QuickClip";
            FileNameText.Text = Path.GetFileName(path);
            FileNameText.ToolTip = path;
            FileInfoText.Text = info.Summary;

            foreach (var t in _tracks) t.Changed -= OnTrackChanged;
            _tracks.Clear();
            foreach (var a in info.Audio)
            {
                var track = new AudioTrack(a, _timeline, info.Duration);
                track.Changed += OnTrackChanged;
                _tracks.Add(track);
            }
            AudioHeader.Text = _tracks.Count switch
            {
                0 => "NO AUDIO TRACKS",
                1 => "AUDIO TRACK",
                _ => $"AUDIO TRACKS ({_tracks.Count})",
            };

            _eof = false;
            _timeline.Reset(info.Duration);
            Timeline.HasMedia = true;

            _crop.Reset(info.Video?.Width ?? 0, info.Video?.Height ?? 0);
            CropCheck.IsChecked = false;
            AspectCombo.SelectedIndex = 0;

            if (_player != null)
            {
                _player.Command("stop");
                ConfigurePreviewAudio();
                _player.SetProperty("pause", true);
                _player.CommandAsync("loadfile", path, "replace");
            }
            ShowMessage(info.Video == null ? "♪  Audio only" : null);

            StartWaveforms();
            RefreshFormatList();
            UpdateUiState();
            Root.Focus();
        }
        finally
        {
            _loading = false;
        }
    }

    private void StartWaveforms()
    {
        if (_media == null) return;
        var cts = new CancellationTokenSource();
        _waveCts = cts;
        string file = _media.Path;
        foreach (var track in _tracks)
        {
            var t = track;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Waveform.ExtractAsync(file, t.Info.Index, t.Waveform,
                        () => Dispatcher.BeginInvoke(t.NotifyWaveformUpdated, DispatcherPriority.Background), cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Write($"Waveform for track {t.Number} failed: {ex}");
                }
            });
        }
    }

    // ------------------------------------------------------------------------------------------
    // Preview audio: every track is decoded and mixed live with its own volume.
    // ------------------------------------------------------------------------------------------

    private void ConfigurePreviewAudio()
    {
        if (_media == null) return;
        _audioSyncTimer.Stop();
        _previewAudio?.Configure(_tracks.Count, i => PreviewVolume(_tracks[i]));
    }

    /// <summary>Makes the current volumes stick across mpv rebuilding its filters (it does on every seek).</summary>
    private void SyncPreviewAudio()
    {
        _audioSyncTimer.Stop();
        if (_media != null) _previewAudio?.Sync(_tracks.Count, i => PreviewVolume(_tracks[i]));
    }

    private double PreviewVolume(AudioTrack t)
    {
        bool anySolo = _tracks.Any(x => x.Solo);
        if (anySolo) return t.Solo ? t.Volume : 0;
        return t.Include ? t.Volume : 0;
    }

    private void ApplyPreviewVolumes()
    {
        if (_previewAudio == null) return;
        _previewAudio.SetLive(_tracks.Count, i => PreviewVolume(_tracks[i]));
        _audioSyncTimer.Stop();
        _audioSyncTimer.Start();
    }

    private void OnTrackChanged(AudioTrack track)
    {
        ApplyPreviewVolumes();
        UpdateSummary();
    }

    private void VolumeSlider_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrack t }) t.Volume = 1;
    }

    private void Merge_Changed(object sender, RoutedEventArgs e) => UpdateSummary();

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
        if (_media == null || _scrubbing) return;
        double t = _mpvTimePos;
        _timeline.SetPosition(t);
        if (!_paused) _timeline.Follow(t);
        UpdateTimeText();
    }

    private void HandleMpvProperty(string name, object? value)
    {
        switch (name)
        {
            case "pause":
                _paused = value is true;
                PlayButton.Content = _paused ? PlayGlyph : PauseGlyph;
                break;
            case "eof-reached":
                bool wasEof = _eof;
                _eof = value is true;
                // Loop a selection that runs to the end of the file (mpv's A-B loop can't cross EOF).
                if (_eof && !wasEof && LoopToggle.IsChecked == true && _media != null && _timeline.Out >= _timeline.Duration - 0.05)
                {
                    Seek(_timeline.In);
                    _player?.SetProperty("pause", false);
                }
                break;
            case "idle-active":
                _idle = value is true;
                break;
            case "duration":
                if (value is double d && _media != null && _media.Duration <= 0)
                {
                    _media.Duration = d;
                    _timeline.UpdateDuration(d);
                }
                break;
            case "osd-dimensions":
            case "video-out-params":
                UpdateOsdGeometry();
                break;
        }
    }

    private void OnMpvEvent(MpvEventId id, int error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (id)
            {
                case MpvEventId.FileLoaded:
                    Video.DisableChildWindows();
                    ApplyPreviewVolumes();
                    ApplyLoop();
                    UpdateOsdGeometry();
                    break;
                case MpvEventId.VideoReconfig:
                    Video.DisableChildWindows();
                    UpdateOsdGeometry();
                    break;
                case MpvEventId.EndFile when error < 0 && _media != null:
                    ShowMessage("Preview not available for this file\n(export may still work)");
                    Log.Write("mpv end-file error: " + MpvNative.ErrorString(error));
                    break;
            }
        });
    }

    private void UpdateOsdGeometry()
    {
        if (_player == null) return;
        double w = _player.GetDouble("osd-dimensions/w") ?? 0, h = _player.GetDouble("osd-dimensions/h") ?? 0;
        if (w <= 0 || h <= 0) return;
        _crop.SetOsd(w, h,
            _player.GetDouble("osd-dimensions/ml") ?? 0, _player.GetDouble("osd-dimensions/mt") ?? 0,
            _player.GetDouble("osd-dimensions/mr") ?? 0, _player.GetDouble("osd-dimensions/mb") ?? 0);
    }

    /// <summary>Centered text drawn by mpv over the video area (null clears it).</summary>
    private void ShowMessage(string? text)
    {
        if (_player == null) return;
        if (string.IsNullOrEmpty(text))
            _player.Command("osd-overlay", MessageOverlayId.ToString(), "none", "");
        else
            _player.Command("osd-overlay", MessageOverlayId.ToString(), "ass-events",
                @"{\an5\fnSegoe UI\fs28\bord0\shad0\1c&H909090&}" + text.Replace("\n", @"\N"), "0", "720");
    }

    // ------------------------------------------------------------------------------------------
    // Transport
    // ------------------------------------------------------------------------------------------

    private void Seek(double t, bool exact = true)
    {
        if (_player == null || _media == null) return;
        if (_audioSyncTimer.IsEnabled) SyncPreviewAudio();
        t = Math.Clamp(t, 0, _timeline.Duration);
        _timeline.SetPosition(t);
        UpdateTimeText();
        _player.CommandAsync("seek", Fmt.Num(t, "0.######"), exact ? "absolute+exact" : "absolute+keyframes");
    }

    private void OnSeekRequested(double t, bool final)
    {
        _scrubbing = !final;
        Seek(t);
    }

    private void TogglePlay()
    {
        if (_player == null || _media == null) return;
        if (_paused)
        {
            double pos = _timeline.Position;
            bool loop = LoopToggle.IsChecked == true;
            if (loop && (pos < _timeline.In - 0.01 || pos >= _timeline.Out - 0.02))
                Seek(_timeline.In);
            else if (_eof || pos >= _timeline.Duration - 0.05)
                Seek(loop ? _timeline.In : 0);
            _player.SetProperty("pause", false);
        }
        else
        {
            _player.SetProperty("pause", true);
        }
    }

    private void StepFrame(bool forward)
    {
        if (_player == null || _media == null) return;
        if (_audioSyncTimer.IsEnabled) SyncPreviewAudio();
        _player.CommandAsync(forward ? "frame-step" : "frame-back-step");
    }

    private void SeekRelative(double seconds) => Seek(_timeline.Position + seconds);

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void FrameBack_Click(object sender, RoutedEventArgs e) => StepFrame(false);
    private void FrameForward_Click(object sender, RoutedEventArgs e) => StepFrame(true);
    private void GoStart_Click(object sender, RoutedEventArgs e) => Seek(0);
    private void GoEnd_Click(object sender, RoutedEventArgs e) => Seek(_timeline.Duration);
    private void SetIn_Click(object sender, RoutedEventArgs e) => _timeline.SetIn(_timeline.Position);
    private void SetOut_Click(object sender, RoutedEventArgs e) => _timeline.SetOut(_timeline.Position);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _timeline.ZoomAt(_timeline.Position, 0.5);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _timeline.ZoomAt(_timeline.Position, 2);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) => _timeline.Fit();
    private void Loop_Changed(object sender, RoutedEventArgs e) => ApplyLoop();

    private void ApplyLoop()
    {
        if (_player == null) return;
        bool loop = LoopToggle.IsChecked == true && _media != null;
        bool toEnd = _timeline.Out >= _timeline.Duration - 0.05;
        if (loop && !toEnd)
        {
            _player.SetProperty("ab-loop-a", _timeline.In);
            _player.SetProperty("ab-loop-b", _timeline.Out);
        }
        else
        {
            _player.SetProperty("ab-loop-a", "no");
            _player.SetProperty("ab-loop-b", "no");
        }
    }

    private void OnSelectionChanged()
    {
        if (!InBox.IsKeyboardFocused) InBox.Text = Fmt.Time(_timeline.In);
        if (!OutBox.IsKeyboardFocused) OutBox.Text = Fmt.Time(_timeline.Out);
        SelectionText.Text = $"Clip length {Fmt.Time(_timeline.Out - _timeline.In)}";
        ApplyLoop();
        UpdateSummary();
    }

    private void UpdateTimeText()
    {
        TimeText.Text = Fmt.Time(_timeline.Position);
        DurationText.Text = " / " + Fmt.Time(_timeline.Duration);
    }

    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Escape)
            {
                // Revert before focus moves, since losing focus commits.
                InBox.Text = Fmt.Time(_timeline.In);
                OutBox.Text = Fmt.Time(_timeline.Out);
            }
            Root.Focus();
            e.Handled = true;
        }
    }

    private void InBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_media != null && Fmt.TryParseTime(InBox.Text, out var t)) _timeline.SetIn(t);
        else OnSelectionChanged();
    }

    private void OutBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_media != null && Fmt.TryParseTime(OutBox.Text, out var t)) _timeline.SetOut(t);
        else OnSelectionChanged();
    }

    // ------------------------------------------------------------------------------------------
    // Keyboard / mouse
    // ------------------------------------------------------------------------------------------

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking anywhere but a text box returns keyboard focus to the window so shortcuts work.
        if (e.OriginalSource is DependencyObject d && FindParent<TextBox>(d) == null && Keyboard.FocusedElement is TextBox)
            Root.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            // Enter finishes editing (the boxes commit when they lose focus); Escape is left to each box.
            if (e.Key == Key.Enter)
            {
                Root.Focus();
                e.Handled = true;
            }
            return;
        }
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        bool handled = true;
        switch (e.Key)
        {
            case Key.O when ctrl: _ = PromptOpenAsync(); break;
            case Key.E when ctrl: ExportNew_Click(this, e); break;
            case Key.S when ctrl: SaveOver_Click(this, e); break;
            case Key.Space: TogglePlay(); break;
            case Key.Left when ctrl: SeekRelative(-5); break;
            case Key.Right when ctrl: SeekRelative(5); break;
            case Key.Left when shift: SeekRelative(-1); break;
            case Key.Right when shift: SeekRelative(1); break;
            case Key.Left: StepFrame(false); break;
            case Key.Right: StepFrame(true); break;
            case Key.I when shift: Seek(_timeline.In); break;
            case Key.O when shift: Seek(_timeline.Out); break;
            case Key.I when _media != null: _timeline.SetIn(_timeline.Position); break;
            case Key.O when _media != null: _timeline.SetOut(_timeline.Position); break;
            case Key.Home: Seek(0); break;
            case Key.End: Seek(_timeline.Duration); break;
            case Key.L: LoopToggle.IsChecked = LoopToggle.IsChecked != true; break;
            case Key.C when CropCheck.IsEnabled: CropCheck.IsChecked = CropCheck.IsChecked != true; break;
            case Key.OemPlus or Key.Add: _timeline.ZoomAt(_timeline.Position, 0.5); break;
            case Key.OemMinus or Key.Subtract: _timeline.ZoomAt(_timeline.Position, 2); break;
            case Key.D0 or Key.NumPad0: _timeline.Fit(); break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    private void OnVideoMouseDown(int x, int y)
    {
        Root.Focus();
        if (_crop.Enabled) _crop.MouseDown(x, y);
        else TogglePlay();
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------
    // Crop
    // ------------------------------------------------------------------------------------------

    private void Crop_Changed(object sender, RoutedEventArgs e)
    {
        _crop.SetEnabled(CropCheck.IsChecked == true);
        CropPanel.IsEnabled = _crop.Enabled;
        UpdateUiState();
    }

    private void Aspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AspectCombo.SelectedItem is not Choice c || _media?.Video == null) return;
        double? ratio = c.Value switch
        {
            "free" => null,
            "source" => _crop.SourceWidth / Math.Max(1, _crop.SourceHeight),
            double r => r,
            _ => null,
        };
        _crop.SetAspect(ratio);
    }

    private void CropReset_Click(object sender, RoutedEventArgs e)
    {
        if (_media?.Video == null) return;
        AspectCombo.SelectedIndex = 0;
        _crop.SetRect(0, 0, _crop.SourceWidth, _crop.SourceHeight);
    }

    private void CropBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Escape) UpdateCropFields(force: true);
            Root.Focus();
            e.Handled = true;
        }
    }

    private void CropBox_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!int.TryParse(CropX.Text, out var x) || !int.TryParse(CropY.Text, out var y) ||
            !int.TryParse(CropW.Text, out var w) || !int.TryParse(CropH.Text, out var h))
        {
            UpdateCropFields(force: true);
            return;
        }
        var r = _crop.Rect;
        if (x == (int)Math.Round(r.X) && y == (int)Math.Round(r.Y) && w == (int)Math.Round(r.Width) && h == (int)Math.Round(r.Height))
            return;
        AspectCombo.SelectedIndex = 0; // typed sizes override the aspect lock
        _crop.SetRect(x, y, w, h);
    }

    private void OnCropChanged()
    {
        if (_player != null)
        {
            var overlay = _crop.BuildOverlay();
            if (overlay is { } o)
                _player.Command("osd-overlay", CropOverlayId.ToString(), "ass-events", o.Ass, o.ResX.ToString(), o.ResY.ToString());
            else
                _player.Command("osd-overlay", CropOverlayId.ToString(), "none", "");
        }
        UpdateCropFields(force: false);
        UpdateSummary();
    }

    private void UpdateCropFields(bool force)
    {
        var r = _crop.Rect;
        void Set(TextBox box, double v)
        {
            if (force || !box.IsKeyboardFocused) box.Text = ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture);
        }
        Set(CropX, r.X);
        Set(CropY, r.Y);
        Set(CropW, r.Width);
        Set(CropH, r.Height);
    }

    // ------------------------------------------------------------------------------------------
    // Export options
    // ------------------------------------------------------------------------------------------

    private void InitOptionLists()
    {
        AspectCombo.ItemsSource = new[]
        {
            new Choice("Free", "free"), new Choice("Same as video", "source"),
            new Choice("16:9 (landscape)", 16 / 9.0), new Choice("9:16 (vertical / Shorts / TikTok)", 9 / 16.0),
            new Choice("1:1 (square)", 1.0), new Choice("4:3", 4 / 3.0), new Choice("4:5 (portrait)", 4 / 5.0),
            new Choice("21:9 (ultrawide)", 21 / 9.0),
        };
        AspectCombo.SelectedIndex = 0;

        VideoModeCombo.ItemsSource = new[]
        {
            new Choice("Re-encode (frame-accurate, allows crop)", "encode"),
            new Choice("Stream copy (instant, lossless, cuts on keyframes)", "copy"),
        };
        Select(VideoModeCombo, _settings.VideoMode);

        QualityCombo.ItemsSource = new[]
        {
            new Choice("Best (large file)", "best"), new Choice("High", "high"), new Choice("Good", "good"),
            new Choice("Small", "small"), new Choice("Target file size…", "size"),
        };
        Select(QualityCombo, _settings.Quality);
        TargetSizeBox.Text = _settings.TargetSizeMB.ToString("0.##", CultureInfo.CurrentCulture);

        ResolutionCombo.ItemsSource = new[] { 0, 2160, 1440, 1080, 720, 480, 360 }
            .Select(r => new Choice(r == 0 ? "Original" : $"{r}p", r)).ToArray();
        Select(ResolutionCombo, _settings.Resolution);

        FpsCombo.ItemsSource = new[] { 0.0, 60, 30, 24, 15 }
            .Select(f => new Choice(f == 0 ? "Original" : $"{f:0} fps", f)).ToArray();
        Select(FpsCombo, _settings.Fps);

        AudioBitrateCombo.ItemsSource = new[] { 96, 128, 160, 192, 256, 320 }
            .Select(b => new Choice($"{b} kbps", b)).ToArray();
        Select(AudioBitrateCombo, _settings.AudioBitrate);

        RefreshFormatList();
        RefreshEncoderList();
    }

    private static void Select(ComboBox combo, object value)
    {
        foreach (var item in combo.Items)
            if (item is Choice c && c.Value.Equals(value)) { combo.SelectedItem = item; return; }
        if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void RefreshFormatList()
    {
        object? current = (FormatCombo.SelectedItem as Choice)?.Value ?? _settings.Format;
        string srcExt = _media != null ? Path.GetExtension(_media.Path).ToLowerInvariant() : "";
        var items = new List<Choice> { new(srcExt.Length > 0 ? $"Same as source ({srcExt})" : "Same as source", "source") };
        items.AddRange(Containers.Choices.Select(c => new Choice(c.Label, c.Key)));
        FormatCombo.ItemsSource = items;
        Select(FormatCombo, current);
    }

    private void RefreshEncoderList()
    {
        var container = CurrentContainer(forOverwrite: false);
        var list = VideoEncoders.All
            .Where(e => _encoders.Contains(e.Id) && container.VideoFamilies.Contains(e.Family))
            .Select(e => new Choice(e.Label, e.Id))
            .ToList();
        if (list.Count == 0)
            list.Add(new Choice(container.FallbackVideoEncoder != null ? $"Automatic ({container.FallbackVideoEncoder})" : "Automatic", ""));

        _refreshingEncoders = true;
        try
        {
            EncoderCombo.ItemsSource = list;
            // The user's last pick if this format allows it, else a fast GPU H.264 encoder, then x264, then anything.
            var preferred = new[] { _settings.Encoder, "h264_nvenc", "h264_amf", "h264_qsv", "libx264" }
                .FirstOrDefault(id => list.Any(c => c.Value.Equals(id)));
            if (preferred != null) Select(EncoderCombo, preferred);
            else EncoderCombo.SelectedIndex = 0;
        }
        finally
        {
            _refreshingEncoders = false;
        }
    }

    private Container CurrentContainer(bool forOverwrite)
    {
        bool hasVideo = _media?.Video != null;
        string srcExt = _media != null ? Path.GetExtension(_media.Path) : ".mp4";
        string key = forOverwrite ? "source" : (FormatCombo.SelectedItem as Choice)?.Value as string ?? "source";
        if (key == "source") return Containers.ForExtension(srcExt, hasVideo);
        return Containers.Choices.FirstOrDefault(c => c.Key == key) ?? Containers.Mp4;
    }

    private void ExportOption_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        if (sender == FormatCombo) RefreshEncoderList();
        if (sender == EncoderCombo && !_refreshingEncoders && EncoderCombo.SelectedItem is Choice { Value: string id } && id.Length > 0)
            _settings.Encoder = id;
        UpdateUiState();
    }

    private void TargetSize_Changed(object sender, TextChangedEventArgs e) => UpdateSummary();

    // Hand keyboard focus back so Space/arrows control playback rather than the dropdown.
    private void ComboBox_DropDownClosed(object? sender, EventArgs e) => Root.Focus();

    private void UpdateUiState()
    {
        if (!IsInitialized) return;
        var container = CurrentContainer(forOverwrite: false);
        bool hasVideo = _media?.Video != null;
        bool busy = _exportCts != null;
        bool videoOut = (container.HasVideo || container.IsGif) && (hasVideo || _media == null);

        VideoOptions.Visibility = videoOut ? Visibility.Visible : Visibility.Collapsed;
        VideoModeCombo.IsEnabled = !container.IsGif;
        bool copy = (VideoModeCombo.SelectedItem as Choice)?.Value as string == "copy" && !container.IsGif;
        EncodeOptions.Visibility = copy ? Visibility.Collapsed : Visibility.Visible;
        EncoderCombo.IsEnabled = !container.IsGif && EncoderCombo.Items.Count > 1;
        bool quality = videoOut && !copy && !container.IsGif;
        QualityOptions.Visibility = quality ? Visibility.Visible : Visibility.Collapsed;
        TargetSizePanel.Visibility = quality && (QualityCombo.SelectedItem as Choice)?.Value as string == "size"
            ? Visibility.Visible : Visibility.Collapsed;
        AudioOptions.Visibility = container.IsGif ? Visibility.Collapsed : Visibility.Visible;

        Grid.SetColumn(AudioOptions, quality ? 2 : 0);
        Grid.SetColumnSpan(AudioOptions, quality ? 1 : 3);

        CropSection.Visibility = hasVideo || _media == null ? Visibility.Visible : Visibility.Collapsed;
        CropCheck.IsEnabled = hasVideo && !busy;
        CropPanel.IsEnabled = _crop.Enabled && !busy;
        ExportButton.IsEnabled = _media != null && !busy;
        SaveOverButton.IsEnabled = _media != null && !busy;
        MergeCheck.IsEnabled = _tracks.Count > 1;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (!IsInitialized || _media == null)
        {
            if (IsInitialized) SummaryText.Text = "";
            return;
        }
        var c = CurrentContainer(forOverwrite: false);
        var parts = new List<string> { $"Clip: {Fmt.Time(_timeline.Out - _timeline.In)}" };
        if ((c.HasVideo || c.IsGif) && _media.Video != null)
        {
            var crop = _crop.ExportRect();
            int w = crop?.W ?? _media.Video.Width, h = crop?.H ?? _media.Video.Height;
            int maxShort = (ResolutionCombo.SelectedItem as Choice)?.Value is int r ? r : 0;
            if (c.IsGif && maxShort == 0) maxShort = 480;
            bool copy = (VideoModeCombo.SelectedItem as Choice)?.Value as string == "copy" && !c.IsGif;
            if (!copy && maxShort > 0 && Math.Min(w, h) > maxShort)
            {
                double s = maxShort / (double)Math.Min(w, h);
                (w, h) = ((int)Math.Round(w * s / 2) * 2, (int)Math.Round(h * s / 2) * 2);
            }
            parts.Add($"{w}×{h}" + (crop != null ? " (cropped)" : ""));
            if (copy) parts.Add("video copied");
        }
        int audio = c.IsGif ? 0 : _tracks.Count(t => t.Include);
        if (audio == 0) parts.Add("no audio");
        else if (audio == 1) parts.Add("1 audio track");
        else if (MergeCheck.IsChecked == true || c.SingleAudioTrack) parts.Add($"{audio} audio tracks merged into 1");
        else parts.Add($"{audio} audio tracks");
        SummaryText.Text = string.Join("  ·  ", parts);
    }

    // ------------------------------------------------------------------------------------------
    // Export
    // ------------------------------------------------------------------------------------------

    private ExportRequest BuildRequest(string outputPath, Container container)
    {
        double.TryParse(TargetSizeBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var mb);
        return new ExportRequest
        {
            Media = _media!,
            OutputPath = outputPath,
            Container = container,
            Start = _timeline.In,
            End = _timeline.Out,
            Crop = _crop.ExportRect(),
            Tracks = _tracks.Where(t => t.Include)
                .Select(t => new TrackExport(t.Info.Index, t.Info.Codec, t.Info.Channels, t.Info.BitRate, t.Name.Trim(), t.Volume))
                .ToList(),
            MergeAudio = MergeCheck.IsChecked == true,
            VideoMode = (VideoModeCombo.SelectedItem as Choice)?.Value as string == "copy" ? VideoMode.Copy : VideoMode.Encode,
            EncoderId = (EncoderCombo.SelectedItem as Choice)?.Value as string ?? "libx264",
            Quality = (QualityCombo.SelectedItem as Choice)?.Value as string ?? "high",
            TargetSizeMB = mb > 0 ? mb : 25,
            MaxShortSide = (ResolutionCombo.SelectedItem as Choice)?.Value is int r ? r : 0,
            Fps = (FpsCombo.SelectedItem as Choice)?.Value is double f ? f : 0,
            AudioBitrateK = (AudioBitrateCombo.SelectedItem as Choice)?.Value is int b ? b : 192,
        };
    }

    private async void ExportNew_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null || _exportCts != null) return;
        var container = CurrentContainer(forOverwrite: false);
        string dir = Path.GetDirectoryName(_media.Path)!;
        string name = Path.GetFileNameWithoutExtension(_media.Path);

        var dlg = new SaveFileDialog
        {
            Title = "Export clip",
            InitialDirectory = dir,
            FileName = Path.GetFileName(UniquePath(dir, $"{name} (clip)", container.Extension)),
            Filter = $"{container.Label}|*{container.Extension}",
            DefaultExt = container.Extension,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        if (string.Equals(Path.GetFullPath(dlg.FileName), _media.Path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "That's the file you're editing. Use \"Save over original\" to replace it.", "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await RunExportAsync(dlg.FileName, container, overwrite: false);
    }

    private async void SaveOver_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null || _exportCts != null) return;
        var container = CurrentContainer(forOverwrite: true);
        string fileName = Path.GetFileName(_media.Path);
        var answer = MessageBox.Show(this,
            $"Replace \"{fileName}\" with the edited clip?\n\nThe original will be moved to the Recycle Bin.",
            "Save over original", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        string dir = Path.GetDirectoryName(_media.Path)!;
        string temp = UniquePath(dir, Path.GetFileNameWithoutExtension(_media.Path) + ".quickclip-tmp", Path.GetExtension(_media.Path));
        await RunExportAsync(temp, container, overwrite: true);
    }

    private async Task RunExportAsync(string outputPath, Container container, bool overwrite)
    {
        var request = BuildRequest(outputPath, container);
        List<string> args;
        try
        {
            args = ExportBuilder.BuildArgs(request);
        }
        catch (ExportException ex)
        {
            MessageBox.Show(this, ex.Message, "Can't export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _exportPath = outputPath;
        RevealButton.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ExportProgress.Value = 0;
        StatusText.Text = "Exporting…";
        UpdateUiState();

        var sw = Stopwatch.StartNew();
        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p * 100;
            StatusText.Text = $"Exporting… {p:P0}";
        });

        try
        {
            await ExportRunner.RunAsync(args, outputPath, request.Duration, progress, cts.Token);
            string final = outputPath;
            if (overwrite)
            {
                StatusText.Text = "Replacing the original…";
                final = await ReplaceOriginalAsync(outputPath);
            }
            _lastExport = final;
            RevealButton.Visibility = Visibility.Visible;
            StatusText.Text = $"Saved {Path.GetFileName(final)} ({Fmt.Size(new FileInfo(final).Length)}) in {sw.Elapsed.TotalSeconds:0.#} s";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Export cancelled.";
        }
        catch (Exception ex)
        {
            Log.Write("Export failed: " + ex);
            StatusText.Text = "Export failed.";
            MessageBox.Show(this, "The export failed:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCts = null;
            _exportPath = null;
            cts.Dispose();
            ProgressPanel.Visibility = Visibility.Collapsed;
            UpdateUiState();
        }
    }

    /// <summary>Swaps the exported temp file in for the original (which goes to the Recycle Bin), then reloads it.</summary>
    private async Task<string> ReplaceOriginalAsync(string tempPath)
    {
        string original = _media!.Path;
        DateTime created = File.GetCreationTime(original);

        // Let go of the file: stop playback and waveform readers.
        _waveCts?.Cancel();
        if (_player != null)
        {
            _player.Command("stop");
            for (int i = 0; i < 100 && !_idle; i++) await Task.Delay(30);
        }
        await Task.Delay(150);

        bool recycled = await RetryAsync(() => RecycleBin.TrySend(original));
        if (!recycled && File.Exists(original))
        {
            var answer = MessageBox.Show(this,
                "The original couldn't be moved to the Recycle Bin.\n\nDelete it permanently and replace it with the edited clip?\n" +
                "(Choose No to keep both files.)", "QuickClip", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                string dir = Path.GetDirectoryName(original)!;
                string keep = UniquePath(dir, Path.GetFileNameWithoutExtension(original) + " (edited)", Path.GetExtension(original));
                File.Move(tempPath, keep);
                await OpenFileAsync(original);
                return keep;
            }
            await RetryAsync(() => { File.Delete(original); return true; });
        }

        await RetryAsync(() => { File.Move(tempPath, original); return true; });
        try { File.SetCreationTime(original, created); } catch { }
        await OpenFileAsync(original);
        return original;
    }

    private static async Task<bool> RetryAsync(Func<bool> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (action()) return true;
                if (attempt >= 5) return false;
            }
            catch (IOException) when (attempt < 10) { }
            catch (UnauthorizedAccessException) when (attempt < 10) { }
            await Task.Delay(200);
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExport != null && File.Exists(_lastExport))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastExport}\"") { UseShellExecute = true });
    }

    private static string UniquePath(string dir, string baseName, string ext)
    {
        string path = Path.Combine(dir, baseName + ext);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName} {i}{ext}");
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ------------------------------------------------------------------------------------------
    // Explorer integration
    // ------------------------------------------------------------------------------------------

    private static bool SafeIsRegistered()
    {
        try { return ShellIntegration.IsRegistered(); }
        catch { return false; }
    }

    private void Shell_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        try
        {
            if (ShellCheck.IsChecked == true) ShellIntegration.Register();
            else ShellIntegration.Unregister();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't update the Explorer menu:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
