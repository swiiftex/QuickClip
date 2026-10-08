using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Mpv;
using QuickClip.Shell;

namespace QuickClip.Views;

/// <summary>One clip of the open project, as listed (in play order) beside the preview.</summary>
public sealed class ProjectClip(string path) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private int _number;
    private double _duration;
    private bool _current, _dragged;

    public string Path { get; } = path;
    public string Title => System.IO.Path.GetFileNameWithoutExtension(Path);
    internal MediaInfo? Info { get; set; }

    public double Duration
    {
        get => _duration;
        set { _duration = value; OnChanged(); OnChanged(nameof(DurationText)); }
    }

    public string DurationText => Fmt.Time(Duration, millis: false);
    public int Number { get => _number; set { _number = value; OnChanged(); } }
    public ImageSource? Thumbnail { get => _thumbnail; set { _thumbnail = value; OnChanged(); } }
    /// <summary>The clip the preview is on.</summary>
    public bool IsCurrent { get => _current; set { _current = value; OnChanged(); } }
    public bool IsDragged { get => _dragged; set { _dragged = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A montage project: its clips in play order (drag to reorder), a music track, a preview that plays the clips one
/// after another with the music, and an export that joins them into one video for the editor.
/// </summary>
public partial class ProjectView : UserControl
{
    private const string PlayGlyph = "\uE768", PauseGlyph = "\uE769";

    // Probes are slow-ish (an ffprobe each); keep them for files that haven't changed.
    private static readonly Dictionary<string, MediaInfo> ProbeCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<ProjectClip> _clips = [];
    private readonly DispatcherTimer _childWindowTimer;
    private Project? _project;
    private MediaInfo? _music;
    private MontageTimeline _timeline = new([]);
    private int _loadToken;
    private CancellationTokenSource? _exportCts;

    private MpvPlayer? _player;
    private int _current = -1;           // clip loaded in the preview
    private bool _wantPlay;              // keeps playing from clip to clip until paused
    private bool _eof, _seekDragging, _showingPosition;
    private double _mpvTimePos;
    private int _timePosQueued;

    private Point _dragStart;
    private ProjectClip? _pressed;

    /// <summary>Open this file in the editor (the exported montage).</summary>
    public event Action<string>? EditRequested;

    /// <summary>The project's clips or name changed (the gallery's project list shows the count).</summary>
    public event Action? ProjectChanged;

    public string? ProjectFolder => _project?.Folder;

    public ProjectView()
    {
        InitializeComponent();
        ClipList.ItemsSource = _clips;
        _childWindowTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) => Video.DisableChildWindows(), Dispatcher) { IsEnabled = false };
        Video.VideoMouseDown += (_, _) => { Root.Focus(); TogglePlay(); };
        SeekBar.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _seekDragging = true));
        SeekBar.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seekDragging = false;
            SeekTo(SeekBar.Value, exact: true);
        }));
        PreviewKeyDown += OnPreviewKeyDown;
        IsVisibleChanged += (_, e) => { if (e.NewValue is false) Pause(); };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_player != null) return;
        // The project page starts hidden inside the gallery; its video window appears when a project is first opened.
        await Video.Created;
        if (_player != null) return;
        try
        {
            _player = new MpvPlayer(Video.Hwnd);
            _player.PropertyChanged += OnMpvProperty;
            _player.EventReceived += (id, _) =>
            {
                if (id is MpvEventId.FileLoaded or MpvEventId.VideoReconfig) Dispatcher.BeginInvoke(Video.DisableChildWindows);
            };
        }
        catch (Exception ex)
        {
            Log.Write("mpv init failed: " + ex);
            return;
        }
        _childWindowTimer.Start();
        if (_clips.Count > 0) LoadClip(Math.Max(_current, 0), 0, play: false);
    }

    // ------------------------------------------------------------------------------------------
    // Opening a project
    // ------------------------------------------------------------------------------------------

    public async Task ShowAsync(Project project)
    {
        int token = ++_loadToken;
        Unload();
        _project = project;
        NameText.Text = project.Name;
        _clips.Clear();
        foreach (var path in project.Clips)
            _clips.Add(new ProjectClip(path) { Duration = Mp4Info.TryReadDuration(path) ?? 0 });
        Renumber();
        _music = null;
        ShowMusic();
        ShowInfo();

        // Durations and audio tracks for the preview and the export, and the music's length.
        using (var gate = new SemaphoreSlim(4))
            await Task.WhenAll(_clips.Select(async clip =>
            {
                await gate.WaitAsync();
                try { clip.Info = await ProbeAsync(clip.Path); }
                finally { gate.Release(); }
            }));
        if (project.Music != null) _music = await ProbeAsync(project.Music);
        if (token != _loadToken) return;
        foreach (var clip in _clips)
            if (clip.Info is { Duration: > 0 } info) clip.Duration = info.Duration;
        RebuildTimeline();
        ShowMusic();
        ShowInfo();
        LoadClip(0, 0, play: false);
        foreach (var clip in _clips) _ = LoadThumbnailAsync(clip);
    }

    /// <summary>Stops the preview and lets go of the files (leaving the project, or before moving or deleting it).</summary>
    public void Unload()
    {
        _wantPlay = false;
        _current = -1;
        _player?.Command("stop");
        UpdatePlayButton();
    }

    public void Close()
    {
        _loadToken++;
        Unload();
        _project = null;
        _clips.Clear();
    }

    public async Task CloseAsync()
    {
        _exportCts?.Cancel();
        _childWindowTimer.Stop();
        var player = _player;
        _player = null;
        // mpv tears down its child window during shutdown, which needs this thread to keep pumping.
        if (player != null) await Task.Run(player.Dispose);
    }

    private static async Task<MediaInfo?> ProbeAsync(string path)
    {
        try
        {
            var file = new FileInfo(path);
            string key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            lock (ProbeCache)
                if (ProbeCache.TryGetValue(key, out var cached)) return cached;
            var info = await MediaInfo.ProbeAsync(path);
            lock (ProbeCache) ProbeCache[key] = info;
            return info;
        }
        catch (Exception ex)
        {
            Log.Write($"Probing {path} failed: {ex.Message}");
            return null;
        }
    }

    private async Task LoadThumbnailAsync(ProjectClip clip)
    {
        try
        {
            string? thumb = await ThumbnailCache.GetAsync(new FileInfo(clip.Path), clip.Duration);
            if (thumb != null) clip.Thumbnail = await Task.Run(() => ThumbnailCache.LoadImage(thumb, 176));
        }
        catch (Exception ex)
        {
            Log.Write("Loading thumbnail failed: " + ex.Message);
        }
    }

    private void Renumber()
    {
        for (int i = 0; i < _clips.Count; i++) _clips[i].Number = i + 1;
    }

    private void RebuildTimeline() => _timeline = new MontageTimeline(_clips.Select(c => c.Duration).ToList());

    private void ShowInfo()
    {
        bool empty = _clips.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        Workspace.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        ExportButton.IsEnabled = !empty && _exportCts == null;
        var first = _clips.Select(c => c.Info?.Video).FirstOrDefault(v => v != null);
        InfoText.Text = empty ? "No clips yet"
            : $"{_clips.Count} clip{(_clips.Count == 1 ? "" : "s")}  ·  {Fmt.Time(_timeline.Total, millis: false)}" +
              (first != null ? $"  ·  exports at {first.Width}×{first.Height}, {first.Fps:0.##} fps (the first clip's size)" : "");
        DurationText.Text = " / " + Fmt.Time(_timeline.Total, millis: false);
    }

    private void ShowMusic()
    {
        bool has = _project?.Music != null;
        MusicText.Text = !has ? "No music. Add a song to play under the montage; it gets its own track in the editor."
            : $"{Path.GetFileName(_project!.Music)}" + (_music != null ? $"  ·  {Fmt.Time(_music.Duration, millis: false)}" +
                (_music.Duration > _timeline.Total + 0.5 && _timeline.Total > 0 ? "  ·  fades out at the end of the montage" : "") : "");
        MusicButton.Content = has ? "Change…" : "Add music…";
        RemoveMusicButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------------------------------
    // Preview: one clip at a time, with its slice of the music, moving on to the next at the end
    // ------------------------------------------------------------------------------------------

    private void LoadClip(int index, double offset, bool play)
    {
        if (_player == null || _project == null || index < 0 || index >= _clips.Count) return;
        var clip = _clips[index];
        _current = index;
        _eof = false;
        var slice = _project.Music != null && _music != null ? _timeline.MusicSlice(index, _music.Duration) : null;
        var (lavfi, aid) = Montage.PreviewMix(clip.Info?.Audio.Count ?? 1, slice != null);
        _player.SetProperty("audio-files", slice is { } s ? Montage.EdlSlice(_project.Music!, s.Start, s.Length) : "");
        _player.SetProperty("lavfi-complex", lavfi);
        _player.SetProperty("aid", aid);
        _player.SetProperty("start", offset > 0.01 ? Fmt.Num(offset) : "none");
        _player.SetProperty("pause", !play);
        _player.CommandAsync("loadfile", clip.Path, "replace");
        _mpvTimePos = offset;
        ShowPosition();
        for (int i = 0; i < _clips.Count; i++) _clips[i].IsCurrent = i == index;
    }

    /// <summary>Jumps to montage time <paramref name="t"/>.</summary>
    private void SeekTo(double t, bool exact)
    {
        if (_player == null || _clips.Count == 0) return;
        var (index, offset) = _timeline.Locate(t);
        if (index == _current)
            _player.CommandAsync("seek", Fmt.Num(offset, "0.######"), exact ? "absolute+exact" : "absolute+keyframes");
        else
            LoadClip(index, offset, _wantPlay);
    }

    private void TogglePlay()
    {
        if (_player == null || _clips.Count == 0) return;
        if (_wantPlay)
        {
            Pause();
            return;
        }
        _wantPlay = true;
        bool atEnd = _eof || _mpvTimePos >= _timeline.Duration(_current) - 0.05;
        if (_current < 0 || (atEnd && _current == _clips.Count - 1)) LoadClip(0, 0, play: true);
        else if (atEnd) LoadClip(_current + 1, 0, play: true);
        else _player.SetProperty("pause", false);
        UpdatePlayButton();
    }

    private void Pause()
    {
        _wantPlay = false;
        _player?.SetProperty("pause", true);
        UpdatePlayButton();
    }

    private void UpdatePlayButton() => PlayButton.Content = _wantPlay ? PauseGlyph : PlayGlyph;

    private void OnMpvProperty(string name, object? value)
    {
        if (name == "time-pos")
        {
            // Coalesce: at most one pending UI update for the (frequent) position changes.
            _mpvTimePos = value is double d ? d : 0;
            if (Interlocked.Exchange(ref _timePosQueued, 1) == 0)
                Dispatcher.BeginInvoke(() =>
                {
                    _timePosQueued = 0;
                    if (!_seekDragging) ShowPosition();
                }, DispatcherPriority.Input);
            return;
        }
        if (name == "eof-reached")
            Dispatcher.BeginInvoke(() =>
            {
                _eof = value is true;
                if (!_eof) return;
                // On to the next clip; after the last one, stop there.
                if (_wantPlay && _current + 1 < _clips.Count) LoadClip(_current + 1, 0, play: true);
                else if (_wantPlay) Pause();
            });
    }

    private void ShowPosition()
    {
        double t = _current >= 0 ? _timeline.Start(_current) + _mpvTimePos : 0;
        _showingPosition = true;
        SeekBar.Maximum = Math.Max(_timeline.Total, 0.001);
        SeekBar.Value = Math.Clamp(t, 0, SeekBar.Maximum);
        _showingPosition = false;
        TimeText.Text = Fmt.Time(t, millis: false);
    }

    private void SeekBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_showingPosition) return;
        TimeText.Text = Fmt.Time(e.NewValue, millis: false);
        // Keyframes only while dragging, so the picture keeps up; exact once let go (and for clicks).
        SeekTo(e.NewValue, exact: !_seekDragging);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox) return;
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: SeekTo(SeekBar.Value - 5, exact: true); break;
            case Key.Right: SeekTo(SeekBar.Value + 5, exact: true); break;
            case Key.Home: SeekTo(0, exact: true); break;
            case Key.M: Volume.ToggleMute(); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Puts the preview back where it was after the order or music changed (the clips' music slices move).</summary>
    private void ReloadPreview(ProjectClip? keep, double offset)
    {
        RebuildTimeline();
        ShowInfo();
        ShowMusic();
        int index = keep != null ? _clips.IndexOf(keep) : -1;
        if (index >= 0) LoadClip(index, offset, _wantPlay);
        else if (_clips.Count > 0) LoadClip(0, 0, play: false);
        else Unload();
    }

    // ------------------------------------------------------------------------------------------
    // Reordering (drag) and removing clips
    // ------------------------------------------------------------------------------------------

    private static ProjectClip? ClipAt(object source) => (source as DependencyObject) is { } d ? FindData(d) : null;

    private static ProjectClip? FindData(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement { DataContext: ProjectClip clip }) return clip;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static bool InButton(object source)
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    private void ClipList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Root.Focus();
        _pressed = InButton(e.OriginalSource) ? null : ClipAt(e.OriginalSource);
        _dragStart = e.GetPosition(ClipList);
    }

    private void ClipList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(ClipList) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance && Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance)
            return;
        var dragged = _pressed;
        _pressed = null;
        var before = _clips.ToList();
        dragged.IsDragged = true;
        try
        {
            DragDrop.DoDragDrop(ClipList, new DataObject(typeof(ProjectClip), dragged), DragDropEffects.Move);
        }
        finally
        {
            dragged.IsDragged = false;
        }
        if (!before.SequenceEqual(_clips)) CommitOrder();
    }

    private void ClipList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A click without a drag: jump the preview to that clip.
        if (_pressed is { } clip && _clips.IndexOf(clip) is var index and >= 0) LoadClip(index, 0, _wantPlay);
        _pressed = null;
    }

    private void ClipList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(typeof(ProjectClip)) is ProjectClip dragged)
        {
            e.Effects = DragDropEffects.Move;
            // Move the clip as it's dragged, so the list always shows where it would land.
            if (ClipAt(e.OriginalSource) is { } over && over != dragged)
                _clips.Move(_clips.IndexOf(dragged), _clips.IndexOf(over));
        }
        e.Handled = true;
    }

    private void ClipList_Drop(object sender, DragEventArgs e) => e.Handled = true;

    private void CommitOrder()
    {
        if (_project == null) return;
        var playing = _current >= 0 && _current < _clips.Count ? _clips.FirstOrDefault(c => c.IsCurrent) : null;
        double offset = _mpvTimePos;
        _project.Clips.Clear();
        _project.Clips.AddRange(_clips.Select(c => c.Path));
        TrySave();
        Renumber();
        ReloadPreview(playing, offset);
    }

    private async void RemoveClip_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || (sender as FrameworkElement)?.DataContext is not ProjectClip clip) return;
        var playing = _clips.FirstOrDefault(c => c.IsCurrent);
        double offset = _mpvTimePos;
        // The preview may have the file open; let go of it first.
        if (clip == playing) Unload();
        bool removed = false;
        for (int attempt = 0; attempt < 20 && !(removed = ProjectLibrary.RemoveClip(_project, clip.Path)); attempt++)
            await Task.Delay(100);
        if (!removed)
        {
            MessageBox.Show(Window.GetWindow(this), $"Couldn't remove \"{clip.Title}\" from the project.", "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _clips.Remove(clip);
        Renumber();
        ReloadPreview(clip == playing ? null : playing, offset);
        ProjectChanged?.Invoke();
    }

    // ------------------------------------------------------------------------------------------
    // Music
    // ------------------------------------------------------------------------------------------

    private async void ChooseMusic_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose music for the montage",
            Filter = "Audio files|*.mp3;*.m4a;*.aac;*.wav;*.flac;*.ogg;*.opus;*.wma|All files|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var info = await ProbeAsync(dialog.FileName);
        if (info == null || info.Audio.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), $"\"{Path.GetFileName(dialog.FileName)}\" has no audio QuickClip can play.", "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await SetMusicAsync(dialog.FileName);
    }

    private async void RemoveMusic_Click(object sender, RoutedEventArgs e) => await SetMusicAsync(null);

    private async Task SetMusicAsync(string? file)
    {
        if (_project == null) return;
        var playing = _clips.FirstOrDefault(c => c.IsCurrent);
        double offset = _mpvTimePos;
        // The preview reads the current music file; let go of it before it's replaced.
        Unload();
        try
        {
            await ProjectLibrary.SetMusicAsync(_project, file);
        }
        catch (Exception ex)
        {
            Log.Write("Setting the music failed: " + ex);
            MessageBox.Show(Window.GetWindow(this), "Couldn't add the music:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _music = _project.Music != null ? await ProbeAsync(_project.Music) : null;
        ReloadPreview(playing, offset);
    }

    // ------------------------------------------------------------------------------------------
    // Export
    // ------------------------------------------------------------------------------------------

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_project == null || _clips.Count == 0 || _exportCts != null) return;
        var missing = _clips.FirstOrDefault(c => c.Info == null);
        if (missing != null)
        {
            MessageBox.Show(Window.GetWindow(this), $"\"{missing.Title}\" couldn't be read, so the montage can't be made. Remove it from the project and try again.",
                "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Pause();
        var project = _project;
        var clips = _clips.Select(c => c.Info!).ToList();
        var music = project.Music != null ? _music : null;
        _exportCts = new CancellationTokenSource();
        ExportPanel.Visibility = Visibility.Visible;
        ExportButton.IsEnabled = false;
        ExportProgress.Value = 0;
        ExportText.Text = $"Joining {clips.Count} clips…";
        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p;
            ExportText.Text = $"Joining {clips.Count} clips… {p:P0}";
        });
        try
        {
            var encoder = Montage.ChooseEncoder(await VideoEncoders.GetAvailableAsync());
            Directory.CreateDirectory(project.ExportsFolder);
            string output = Path.Combine(project.ExportsFolder, $"{project.Name} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            await Montage.ExportAsync(clips, music, encoder, output, progress, _exportCts.Token);
            EditRequested?.Invoke(output);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("Montage export failed: " + ex);
            MessageBox.Show(Window.GetWindow(this), "The montage couldn't be made:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _exportCts.Dispose();
            _exportCts = null;
            ExportPanel.Visibility = Visibility.Collapsed;
            ExportButton.IsEnabled = _clips.Count > 0;
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_project != null) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_project.Folder}\"") { UseShellExecute = true });
    }

    private void TrySave()
    {
        try
        {
            if (_project != null) ProjectLibrary.Save(_project);
        }
        catch (Exception ex)
        {
            Log.Write("Saving the project failed: " + ex.Message);
        }
    }

#if DEBUG
    internal string DebugState => $"project={_project?.Name} clips={_clips.Count} total={_timeline.Total:0.00} current={_current} " +
                                  $"pos={_mpvTimePos:0.00} playing={_wantPlay} music={_music?.Duration:0.0}";
    internal void PlayForRender() => TogglePlay();
    internal void SeekForRender(double t) => SeekTo(t, exact: true);
    internal Task SetMusicForRender(string file) => SetMusicAsync(file);
    internal void SaveVideoForRender(string path) => _player?.Command("screenshot-to-file", path, "window");
    internal double FirstClipDuration => _timeline.Duration(0);
    internal void MoveForRender(int from, int to)
    {
        _clips.Move(from, to);
        CommitOrder();
    }
    internal void ExportForRender() => Export_Click(this, new RoutedEventArgs());
    internal bool Exporting => _exportCts != null;
#endif
}
