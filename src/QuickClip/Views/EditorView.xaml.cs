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

namespace QuickClip.Views;

public partial class EditorView : UserControl
{
    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    private const string PlayGlyph = "\uE768";
    private const string PauseGlyph = "\uE769";
    private const int CropOverlayId = 1;
    private const int MessageOverlayId = 2;

    private string? _pendingFile;
    private readonly AppSettings _settings = AppSettings.Current;
    private readonly TimelineState _timeline = new();
    private readonly CropController _crop = new();
    private readonly ObservableCollection<AudioTrack> _tracks = [];
    private readonly DispatcherTimer _childWindowTimer;
    private readonly DispatcherTimer _audioSyncTimer;
    private readonly DispatcherTimer _soundPreviewTimer;
    private List<AudioTrack> _previewTracks = [];   // the tracks mpv is playing, in its track order
    private readonly List<string> _stalePreviews = [];
    private CancellationTokenSource? _soundCts;

    // Added sounds are previewed from copies already placed on the clip's timeline.
    private static readonly string SoundPreviewDir = Path.Combine(Path.GetTempPath(), "QuickClip", "sound previews");
    private static readonly HashSet<string> AudioExtensions = new(
        [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".ac3", ".eac3", ".dts",
         ".mka", ".amr", ".ape", ".wv", ".caf", ".alac", ".mp2", ".m4b"], StringComparer.OrdinalIgnoreCase);

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
    private bool _closed;
    private (string Clip, DateTime RecycledUtc)? _undo; // the open clip was just saved over; its original is in the Recycle Bin
    private bool _settingMerge;
    private int _timePosQueued;
    private double _mpvTimePos;

    /// <summary>File name of the open clip (for the window title), or null.</summary>
    public string? FileTitle { get; private set; }
    public event Action? TitleChanged;
    public string? CurrentFile => _media?.Path;

    private Window OwnerWindow => Window.GetWindow(this) ?? Application.Current.MainWindow;

    /// <summary>Opens a file now, or as soon as the player is ready.</summary>
    public async Task OpenAsync(string path)
    {
        if (!_uiReady)
        {
            _pendingFile = path;
            return;
        }
        if (CanOpenAnother()) await OpenFileAsync(path);
    }

    /// <summary>Gives keyboard focus to the editor so its shortcuts work.</summary>
    public void FocusEditor() => Root.Focus();

    public EditorView()
    {
        InitializeComponent();

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
        // A moved sound is placed again for the preview once it has stayed put for a moment.
        _soundPreviewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(350), DispatcherPriority.Background,
            (_, _) => RenderSoundPreviews(), Dispatcher) { IsEnabled = false };

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        Drop += OnDrop;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
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
        await Video.Created;
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
            MessageBox.Show(OwnerWindow, "The video preview could not be started:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _childWindowTimer.Start();
        ShowMessage("Open a video or drop it here");
        _ = Task.Run(() => DeletePreviews(olderThan: TimeSpan.FromDays(1)));

        LoopToggle.IsChecked = _settings.Loop;
        // Merging always starts off (it used to be remembered, which quietly merged every later export).
        _settings.MergeAudio = false;
        _uiReady = true;

        if (_pendingFile != null)
        {
            var file = _pendingFile;
            _pendingFile = null;
            await OpenFileAsync(file);
        }

        _encoders = await VideoEncoders.GetAvailableAsync();
        RefreshEncoderList();
    }

    /// <summary>Stops playback and any export. Returns false if the user chose to keep an export running.</summary>
    public async Task<bool> CloseAsync()
    {
        if (_closed) return true;
        if (_exportCts != null)
        {
            var answer = MessageBox.Show(OwnerWindow, "An export is still running. Cancel it and close?", "QuickClip",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return false;
            _exportCts.Cancel();
        }

        _closed = true;
        SaveSettings();
        _waveCts?.Cancel();
        _soundCts?.Cancel();
        _childWindowTimer.Stop();
        _audioSyncTimer.Stop();
        _soundPreviewTimer.Stop();

        // mpv tears down its child window during shutdown, which needs this thread to keep pumping.
        var player = _player;
        _player = null;
        _previewAudio = null;
        if (player != null) await Task.Run(player.Dispose);
        if (_exportPath != null) TryDelete(_exportPath);
        foreach (var file in _stalePreviews.Concat(_tracks.Select(t => t.PreviewFile).OfType<string>())) TryDelete(file);
        return true;
    }

    private void SaveSettings()
    {
        _settings.Loop = LoopToggle.IsChecked == true;
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
        MessageBox.Show(OwnerWindow, "Please wait for the export to finish first.", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (dlg.ShowDialog(OwnerWindow) == true)
            await OpenFileAsync(dlg.FileName);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        bool sounds = files && DropAddsSounds(e);
        SoundDropHint.Visibility = sounds ? Visibility.Visible : Visibility.Collapsed;
        if (sounds) SoundDropText.Text = DropOverLanes(e, out double t) ? $"Drop to add as a sound starting at {Fmt.Time(t)}" : "Drop to add as a sound at the playhead";
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) SoundDropHint.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        SoundDropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        if (DropAddsSounds(e))
        {
            await AddSoundsAsync(files, DropOverLanes(e, out double t) ? t : SoundStart());
            return;
        }
        if (CanOpenAnother()) await OpenFileAsync(files[0]);
    }

    /// <summary>With a clip open, audio files dropped anywhere, and any file dropped on the tracks, become added sounds.</summary>
    private bool DropAddsSounds(DragEventArgs e) =>
        _media != null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files
        && (IsOver(TracksArea, e) || files.All(f => AudioExtensions.Contains(Path.GetExtension(f))));

    /// <summary>Whether the drop is over the timeline or the waveforms, and the time there.</summary>
    private bool DropOverLanes(DragEventArgs e, out double time)
    {
        double x = e.GetPosition(Timeline).X;
        time = _timeline.XToTime(x, Timeline.ActualWidth);
        return IsOver(TracksArea, e) && x >= 0 && x <= Timeline.ActualWidth;
    }

    private static bool IsOver(FrameworkElement element, DragEventArgs e)
    {
        var p = e.GetPosition(element);
        return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
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
                MessageBox.Show(OwnerWindow, $"Couldn't open \"{Path.GetFileName(path)}\":\n\n{ex.Message}", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Error);
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
            _undo = null;
            RevealButton.Visibility = Visibility.Collapsed;
            UndoButton.Visibility = Visibility.Collapsed;
            StatusText.Text = "";

            FileTitle = Path.GetFileName(path);
            TitleChanged?.Invoke();
            FileNameText.Text = Path.GetFileName(path);
            FileNameText.ToolTip = path;
            FileInfoText.Text = info.Summary;

            _soundCts?.Cancel();
            _soundPreviewTimer.Stop();
            foreach (var t in _tracks)
            {
                t.Changed -= OnTrackChanged;
                t.Moved -= OnSoundMoved;
                t.RemoveRequested -= RemoveSound;
                if (t.PreviewFile != null) _stalePreviews.Add(t.PreviewFile);
            }
            _tracks.Clear();
            foreach (var a in info.Audio)
            {
                var track = new AudioTrack(a, _timeline, info.Duration);
                track.Changed += OnTrackChanged;
                _tracks.Add(track);
            }
            UpdateTrackHeader();

            _eof = false;
            _timeline.Reset(info.Duration);
            Timeline.HasMedia = true;

            _crop.Reset(info.Video?.Width ?? 0, info.Video?.Height ?? 0);
            CropCheck.IsChecked = false;
            AspectCombo.SelectedIndex = 0;

            if (_player != null)
            {
                _player.Command("stop");
                _player.SetList("audio-files", []);
                _player.SetProperty("start", "none");
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
        _waveCts = new CancellationTokenSource();
        foreach (var track in _tracks) StartWaveform(track, _media.Path);
    }

    private void StartWaveform(AudioTrack t, string file)
    {
        var token = _waveCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                await Waveform.ExtractAsync(file, t.Info.Index, t.Waveform,
                    () => Dispatcher.BeginInvoke(t.NotifyWaveformUpdated, DispatcherPriority.Background), token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write($"Waveform for track {t.Number} failed: {ex}");
            }
        });
    }

    private void UpdateTrackHeader() => AudioHeader.Text = _tracks.Count switch
    {
        0 => "NO AUDIO TRACKS",
        1 => "AUDIO TRACK",
        _ => $"AUDIO TRACKS ({_tracks.Count})",
    };

    // ------------------------------------------------------------------------------------------
    // Added sounds: other files' audio as more tracks, each starting anywhere on the clip's timeline
    // ------------------------------------------------------------------------------------------

    /// <summary>Where a sound added without a drop position starts: the playhead, or the beginning when that's at the end.</summary>
    private double SoundStart() => _timeline.Position >= _timeline.Duration - 0.05 ? 0 : _timeline.Position;

    private async void AddSound_Click(object sender, RoutedEventArgs e)
    {
        if (_media == null) return;
        var audio = string.Join(";", AudioExtensions.Select(x => "*" + x));
        var video = string.Join(";", ShellIntegration.Extensions.Where(x => !AudioExtensions.Contains(x)).Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Title = "Add a sound",
            Filter = $"Sounds and music|{audio}|Videos (adds their sound)|{video}|All files|*.*",
            Multiselect = true,
            InitialDirectory = _settings.SoundFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };
        if (dlg.ShowDialog(OwnerWindow) != true) return;
        _settings.SoundFolder = Path.GetDirectoryName(dlg.FileNames[0]);
        await AddSoundsAsync(dlg.FileNames, SoundStart());
    }

    private async Task AddSoundsAsync(IReadOnlyList<string> paths, double start)
    {
        var media = _media;
        if (media == null) return;
        foreach (var path in paths)
        {
            MediaInfo info;
            try
            {
                Mouse.OverrideCursor = Cursors.AppStarting;
                info = await MediaInfo.ProbeAsync(Path.GetFullPath(path));
            }
            catch (Exception ex)
            {
                MessageBox.Show(OwnerWindow, $"Couldn't add \"{Path.GetFileName(path)}\":\n\n{ex.Message}", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
                continue;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
            if (_media != media) return; // another file was opened meanwhile
            if (info.Audio.Count == 0 || info.Duration <= 0)
            {
                MessageBox.Show(OwnerWindow, $"\"{Path.GetFileName(path)}\" has no sound to add.", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Information);
                continue;
            }

            var a = info.Audio[0];
            var stream = new AudioStreamInfo
            {
                Index = a.Index, Ordinal = _tracks.Count, Codec = a.Codec, Channels = a.Channels, ChannelLayout = a.ChannelLayout,
                SampleRate = a.SampleRate, BitRate = a.BitRate, Language = a.Language, Title = Path.GetFileNameWithoutExtension(path),
            };
            var track = AudioTrack.ForSound(info.Path, stream, info.Duration, _timeline, start);
            track.Changed += OnTrackChanged;
            track.Moved += OnSoundMoved;
            track.RemoveRequested += RemoveSound;
            _tracks.Add(track);
            StartWaveform(track, info.Path);
            Log.Write($"Added the sound {info.Path} at {Fmt.Time(track.Offset)}");
        }
        UpdateTrackHeader();
        UpdateUiState();
        RenderSoundPreviews();
    }

    private void RemoveSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioTrack t }) RemoveSound(t);
    }

    private void RemoveSound(AudioTrack track)
    {
        if (!track.IsSound || !_tracks.Remove(track)) return;
        track.Changed -= OnTrackChanged;
        track.Moved -= OnSoundMoved;
        track.RemoveRequested -= RemoveSound;
        if (track.PreviewFile != null) _stalePreviews.Add(track.PreviewFile);
        UpdateTrackHeader();
        UpdateUiState();
        if (_previewTracks.Contains(track)) ReloadPreview();
    }

    private void OnSoundMoved(AudioTrack track)
    {
        UpdateSummary();
        _soundPreviewTimer.Stop();
        _soundPreviewTimer.Start();
    }

    /// <summary>Places every new or moved sound for the preview, then plays them along with the clip.</summary>
    private async void RenderSoundPreviews()
    {
        _soundPreviewTimer.Stop();
        if (_media == null) return;
        var pending = _tracks.Where(t => t.IsSound && (t.PreviewFile == null || t.PreviewOffset != t.Offset)).ToList();
        if (pending.Count == 0) return;
        _soundCts?.Cancel();
        var cts = new CancellationTokenSource();
        _soundCts = cts;
        double duration = _media.Duration;
        try
        {
            Directory.CreateDirectory(SoundPreviewDir);
            var placed = await Task.WhenAll(pending.Select(async t =>
            {
                double offset = t.Offset;
                string file = Path.Combine(SoundPreviewDir, $"{Guid.NewGuid():N}.flac");
                await AddedSound.RenderPreviewAsync(t.SoundPath!, t.Info.Index, offset, duration, file, cts.Token);
                return (Track: t, File: file, Offset: offset);
            }));
            if (cts.IsCancellationRequested)
            {
                _stalePreviews.AddRange(placed.Select(p => p.File));
                return;
            }
            foreach (var (t, file, offset) in placed)
            {
                if (!_tracks.Contains(t))
                {
                    _stalePreviews.Add(file); // removed meanwhile
                    continue;
                }
                if (t.PreviewFile != null) _stalePreviews.Add(t.PreviewFile);
                t.PreviewFile = file;
                t.PreviewOffset = offset;
            }
            ReloadPreview();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Placing a sound for the preview failed: " + ex);
            StatusText.Text = "A sound couldn't be prepared for the preview. Exporting may still work.";
        }
    }

    /// <summary>Loads the clip again, where it was, with the added sounds' placed copies as extra tracks.</summary>
    private void ReloadPreview()
    {
        if (_player == null || _media == null) return;
        var sounds = _tracks.Where(t => t.IsSound && t.PreviewFile != null).Select(t => t.PreviewFile!);
        double position = _timeline.Position;
        _player.Command("stop"); // the new track setup only fits the reloaded file
        _player.SetList("audio-files", sounds);
        ConfigurePreviewAudio();
        _player.SetProperty("start", position > 0.001 ? Fmt.Num(position, "0.######") : "none");
        _player.SetProperty("pause", _paused);
        _eof = false;
        _player.CommandAsync("loadfile", _media.Path, "replace");
    }

    /// <summary>Deletes placed sound copies the preview no longer plays (or, at startup, any left over from before).</summary>
    private void DeleteStalePreviews()
    {
        _stalePreviews.RemoveAll(file =>
        {
            try { File.Delete(file); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { return false; } // still open; try again next time
        });
    }

    private static void DeletePreviews(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(SoundPreviewDir)) return;
            foreach (var file in Directory.GetFiles(SoundPreviewDir, "*.flac"))
                if (DateTime.Now - File.GetLastWriteTime(file) > olderThan)
                    try { File.Delete(file); } catch { }
        }
        catch { }
    }

    // ------------------------------------------------------------------------------------------
    // Preview audio: every track is decoded and mixed live with its own volume.
    // ------------------------------------------------------------------------------------------

    private void ConfigurePreviewAudio()
    {
        if (_media == null) return;
        _audioSyncTimer.Stop();
        // The clip's own tracks, then the added sounds that are ready (mpv numbers external tracks after the file's).
        _previewTracks = [.. _tracks.Where(t => !t.IsSound || t.PreviewFile != null)];
        _previewAudio?.Configure(_previewTracks.Count, i => PreviewVolume(_previewTracks[i]));
    }

    /// <summary>Makes the current volumes stick across mpv rebuilding its filters (it does on every seek).</summary>
    private void SyncPreviewAudio()
    {
        _audioSyncTimer.Stop();
        if (_media != null) _previewAudio?.Sync(_previewTracks.Count, i => PreviewVolume(_previewTracks[i]));
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
        _previewAudio.SetLive(_previewTracks.Count, i => PreviewVolume(_previewTracks[i]));
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

    private void Merge_Changed(object sender, RoutedEventArgs e)
    {
        if (!_settingMerge && MergeCheck.IsChecked == true)
        {
            var answer = MessageBox.Show(OwnerWindow,
                "Merge all audio tracks into one when exporting?\n\nThe exported file will have a single audio track, so its game, " +
                "chat, mic and music can't be turned up or down separately any more.",
                "Merge audio tracks", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
            {
                _settingMerge = true;
                MergeCheck.IsChecked = false;
                _settingMerge = false;
            }
        }
        UpdateSummary();
    }

    private void Transport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Natural widths (a measure against the column width would be clipped to it).
        var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
        PlaybackControls.Measure(unbounded);
        EditControls.Measure(unbounded);
        bool oneLine = PlaybackControls.DesiredSize.Width + EditControls.DesiredSize.Width + 16 <= Transport.ActualWidth;
        Grid.SetRow(EditControls, oneLine ? 0 : 1);
        Grid.SetColumn(EditControls, oneLine ? 1 : 0);
        Grid.SetColumnSpan(EditControls, oneLine ? 1 : 2);
        EditControls.Margin = new Thickness(0, oneLine ? 0 : 8, 0, 0);
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
                    DeleteStalePreviews();
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
            case Key.Z when ctrl && _undo != null: Undo_Click(this, e); break;
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
            case Key.M: Volume.ToggleMute(); break;
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
            .Where(e => e.Export && _encoders.Contains(e.Id) && container.VideoFamilies.Contains(e.Family))
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
        MergeCheck.Visibility = container.IsGif ? Visibility.Collapsed : Visibility.Visible;
        AddSoundButton.IsEnabled = _media != null;
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
        int sounds = c.IsGif ? 0 : _tracks.Count(t => t.Include && t.IsSound);
        string withSounds = sounds switch { 0 => "", 1 => " (1 added sound)", _ => $" ({sounds} added sounds)" };
        if (audio == 0) parts.Add("no audio");
        else if (audio == 1) parts.Add("1 audio track" + withSounds);
        else if (MergeCheck.IsChecked == true || c.SingleAudioTrack) parts.Add($"{audio} audio tracks{withSounds} merged into 1");
        else parts.Add($"{audio} audio tracks{withSounds}");
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
                .Select(t => new TrackExport(t.Info.Index, t.Info.Codec, t.Info.Channels, t.Info.BitRate, t.Name.Trim(), t.Volume)
                {
                    SoundPath = t.SoundPath,
                    Offset = t.Offset,
                })
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
        if (dlg.ShowDialog(OwnerWindow) != true) return;

        if (string.Equals(Path.GetFullPath(dlg.FileName), _media.Path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(OwnerWindow, "That's the file you're editing. Use \"Save over original\" to replace it.", "QuickClip",
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
        int tracks = _tracks.Count(t => t.Include);
        bool merging = MergeCheck.IsChecked == true && tracks > 1 && !container.SingleAudioTrack;
        var answer = merging
            ? MessageBox.Show(OwnerWindow,
                $"Merge all audio tracks is on.\n\nSaving over \"{fileName}\" mixes its {tracks} audio tracks into one, so the clip " +
                "loses its separate game, chat, mic and music tracks. The original will be moved to the Recycle Bin.\n\nSave over it anyway?",
                "Merge tracks and save over original?", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(OwnerWindow,
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
            MessageBox.Show(OwnerWindow, ex.Message, "Can't export", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _exportPath = outputPath;
        RevealButton.Visibility = Visibility.Collapsed;
        UndoButton.Visibility = Visibility.Collapsed;
        _undo = null;
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
                DateTime? recycled;
                (final, recycled) = await ReplaceOriginalAsync(outputPath);
                if (recycled is { } when) _undo = (final, when);
            }
            _lastExport = final;
            RevealButton.Visibility = Visibility.Visible;
            UndoButton.Visibility = _undo != null ? Visibility.Visible : Visibility.Collapsed;
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
            MessageBox.Show(OwnerWindow, "The export failed:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Error);
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

    /// <summary>
    /// Swaps the exported temp file in for the original (which goes to the Recycle Bin), then reloads it. Returns the
    /// edited file and, if the original went to the Recycle Bin, when (so it can be put back).
    /// </summary>
    private async Task<(string Final, DateTime? RecycledUtc)> ReplaceOriginalAsync(string tempPath)
    {
        string original = _media!.Path;
        DateTime created = File.GetCreationTime(original);
        await ReleaseFileAsync();

        DateTime before = DateTime.UtcNow.AddSeconds(-2);
        bool recycled = await RetryAsync(() => RecycleBin.TrySend(original));
        if (!recycled && File.Exists(original))
        {
            var answer = MessageBox.Show(OwnerWindow,
                "The original couldn't be moved to the Recycle Bin.\n\nDelete it permanently and replace it with the edited clip?\n" +
                "(Choose No to keep both files.)", "QuickClip", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                string dir = Path.GetDirectoryName(original)!;
                string keep = UniquePath(dir, Path.GetFileNameWithoutExtension(original) + " (edited)", Path.GetExtension(original));
                File.Move(tempPath, keep);
                await OpenFileAsync(original);
                return (keep, null);
            }
            await RetryAsync(() => { File.Delete(original); return true; });
        }

        await RetryAsync(() => { File.Move(tempPath, original); return true; });
        try { File.SetCreationTime(original, created); } catch { }
        await OpenFileAsync(original);
        return (original, recycled ? before : null);
    }

    /// <summary>Lets go of the open file: stops playback and the waveform and sound readers.</summary>
    private async Task ReleaseFileAsync()
    {
        _waveCts?.Cancel();
        _soundCts?.Cancel();
        if (_player != null)
        {
            _player.Command("stop");
            for (int i = 0; i < 100 && !_idle; i++) await Task.Delay(30);
        }
        await Task.Delay(150);
    }

    /// <summary>
    /// Puts the clip back as it was before it was saved over: the original comes out of the Recycle Bin and the edited
    /// version goes in, so this can be undone the same way.
    /// </summary>
    private async void Undo_Click(object sender, RoutedEventArgs e) => await UndoAsync();

    private async Task UndoAsync()
    {
        if (_undo is not { } undo || _exportCts != null || _loading) return;
        string clip = undo.Clip, dir = Path.GetDirectoryName(clip)!;
        string name = Path.GetFileNameWithoutExtension(clip), ext = Path.GetExtension(clip);
        string? parked = null;
        UndoButton.IsEnabled = false;
        RevealButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "Putting the original back…";
        try
        {
            var original = RecycleBin.FindLatest(clip, undo.RecycledUtc)
                ?? throw new IOException("The original isn't in the Recycle Bin any more.");
            await ReleaseFileAsync();
            // Out of the Recycle Bin beside the clip first, so nothing is lost if the edited version can't be moved.
            parked = UniquePath(dir, name + ".quickclip-restore", ext);
            await RetryAsync(() => { RecycleBin.Restore(original, parked); return true; });
            if (!await RetryAsync(() => RecycleBin.TrySend(clip)))
                throw new IOException("The edited clip couldn't be moved to the Recycle Bin.");
            await RetryAsync(() => { File.Move(parked, clip); return true; });
            parked = null;
            Log.Write($"Undid the edit of {clip}: the original is back, the edited version is in the Recycle Bin");
            await OpenFileAsync(clip);
            StatusText.Text = "The clip is back as it was before the edit. The edited version is in the Recycle Bin.";
        }
        catch (Exception ex)
        {
            Log.Write("Undoing the edit failed: " + ex);
            string message = "The edit couldn't be undone:\n\n" + ex.Message;
            if (parked != null && File.Exists(parked))
            {
                // Keep the original where it can be found rather than back in the bin.
                string keep = UniquePath(dir, name + " (before edit)", ext);
                try { File.Move(parked, keep); parked = keep; } catch { }
                message += $"\n\nThe original is saved as \"{Path.GetFileName(parked)}\" next to the clip.";
            }
            StatusText.Text = "Undo failed.";
            MessageBox.Show(OwnerWindow, message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (File.Exists(clip)) await OpenFileAsync(clip);
        }
        finally
        {
            UndoButton.IsEnabled = true;
            UndoButton.Visibility = _undo != null ? Visibility.Visible : Visibility.Collapsed;
        }
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

#if DEBUG
    internal string DebugState
    {
        get
        {
            int mpvAudio = 0;
            if (_player?.GetDouble("track-list/count") is double n)
                for (int i = 0; i < (int)n; i++)
                    if (_player.GetString($"track-list/{i}/type") == "audio") mpvAudio++;
            var tracks = _tracks.Select(t => t.IsSound ? $"{t.Name}@{t.Offset:0.00}{(t.PreviewOffset == t.Offset ? "" : " (not placed)")}" : t.Name);
            return $"tracks=[{string.Join(", ", tracks)}] previewing={_previewTracks.Count} mpv audio tracks={mpvAudio} " +
                   $"pos={_timeline.Position:0.00} paused={_paused} summary={SummaryText.Text}";
        }
    }
    internal Task AddSoundForRender(string file, double at) => AddSoundsAsync([file], at);
    internal void MoveSoundForRender(double to) => _tracks.Last(t => t.IsSound).MoveTo(to);
    internal void PlayForRender() => TogglePlay();
    internal void SeekForRender(double t) => Seek(t);
    internal bool CanUndo => _undo != null;
    internal string StatusForRender => StatusText.Text;
    internal Task UndoForRender() => UndoAsync();
    internal Task SaveOverForRender(double newIn)
    {
        _timeline.SetIn(newIn);
        string dir = Path.GetDirectoryName(_media!.Path)!;
        return RunExportAsync(UniquePath(dir, Path.GetFileNameWithoutExtension(_media.Path) + ".quickclip-tmp", Path.GetExtension(_media.Path)),
            CurrentContainer(forOverwrite: true), overwrite: true);
    }
#endif
}
