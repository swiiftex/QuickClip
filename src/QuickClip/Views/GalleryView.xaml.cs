using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Gallery;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Views;

/// <summary>Clips in the clips folder, grouped by game, newest first.</summary>
public partial class GalleryView : UserControl
{
    private sealed record Folder(string Name, int Count, DateTime Latest);

    private const double CardSlot = 264 + 14; // card width + right margin

    private readonly DispatcherTimer _reloadTimer;
    private readonly DispatcherTimer _agoTimer;
    private List<ClipItem> _clips = [];
    private string _folder = ClipLibrary.AllClips;
    private string? _loadedRoot;
    private FileSystemWatcher? _watcher;
    private int _columns = 3;
    private bool _loading, _reloadQueued, _released;

    /// <summary>The user wants to edit a file.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>The user wants to watch a clip; the folder's other clips come along for previous/next.</summary>
    public event Action<IReadOnlyList<ClipItem>, int>? PlayRequested;

    public GalleryView()
    {
        InitializeComponent();
        _reloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
        {
            _reloadTimer!.Stop();
            _ = ReloadAsync();
        }, Dispatcher) { IsEnabled = false };
        _agoTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var clip in _clips) clip.RefreshAgo();
        }, Dispatcher);

        Recorder.Instance.ClipSaved += OnClipSaved;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _loadedRoot != AppSettings.Current.ClipsFolder) _ = ReloadAsync();
        };
        Loaded += (_, _) => _ = ReloadAsync();
    }

    /// <summary>Stops watching the folder and drops the thumbnails (the window is closing).</summary>
    public void Release()
    {
        _released = true;
        Recorder.Instance.ClipSaved -= OnClipSaved;
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer.Stop();
        _agoTimer.Stop();
        Rows.ItemsSource = null;
        _clips = [];
    }

    private void OnClipSaved(SavedClip clip) => Dispatcher.BeginInvoke(QueueReload);

    private void QueueReload()
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    }

    private async Task ReloadAsync()
    {
        if (_released) return;
        if (_loading)
        {
            _reloadQueued = true;
            return;
        }
        _loading = true;
        try
        {
            string root = AppSettings.Current.ClipsFolder;
            if (root != _loadedRoot) Watch(root);
            _loadedRoot = root;
            var previous = _clips.ToDictionary(c => c.Path, StringComparer.OrdinalIgnoreCase);
            _clips = await Task.Run(() => ClipLibrary.Scan(root));
            // Keep thumbnails already on screen so a new clip doesn't make every card flicker.
            foreach (var clip in _clips)
                if (previous.TryGetValue(clip.Path, out var old) && old.Size == clip.Size && old.Thumbnail != null)
                {
                    clip.Thumbnail = old.Thumbnail;
                    clip.ThumbnailRequested = true;
                }
            RebuildFolders();
        }
        catch (Exception ex)
        {
            Log.Write("Loading clips failed: " + ex);
        }
        finally
        {
            _loading = false;
        }
        if (_reloadQueued)
        {
            _reloadQueued = false;
            await ReloadAsync();
        }
    }

    private void Watch(string root)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            Directory.CreateDirectory(root);
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(QueueReload);
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(QueueReload);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Log.Write("Can't watch the clips folder: " + ex.Message);
        }
    }

    private void RebuildFolders()
    {
        var folders = new List<Folder> { new(ClipLibrary.AllClips, _clips.Count, _clips.FirstOrDefault()?.Created ?? DateTime.MinValue) };
        folders.AddRange(_clips.GroupBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Folder(g.First().Folder, g.Count(), g.Max(c => c.Created)))
            .OrderByDescending(f => f.Latest));
        if (folders.All(f => !string.Equals(f.Name, _folder, StringComparison.OrdinalIgnoreCase))) _folder = ClipLibrary.AllClips;
        FolderList.ItemsSource = folders;
        FolderList.SelectedItem = folders.First(f => string.Equals(f.Name, _folder, StringComparison.OrdinalIgnoreCase));
        ShowFolder();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is not Folder f || f.Name == _folder) return;
        _folder = f.Name;
        ShowFolder();
    }

    private IEnumerable<ClipItem> CurrentClips =>
        _folder == ClipLibrary.AllClips ? _clips : _clips.Where(c => string.Equals(c.Folder, _folder, StringComparison.OrdinalIgnoreCase));

    private void ShowFolder()
    {
        var clips = CurrentClips.ToList();
        FolderTitle.Text = _folder;
        FolderInfo.Text = clips.Count == 0 ? "" : $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")}  ·  {Fmt.Size(clips.Sum(c => c.Size))}";
        Rows.ItemsSource = clips.Chunk(_columns).ToList();

        EmptyState.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var s = AppSettings.Current;
        EmptyText.Text = $"Press {new Hotkey(s.HotkeyModifiers, s.HotkeyKey)} while you play to save the last " +
                         $"{RecordingPresets.DurationLabel(s.BufferSeconds)}. Clips are filed by game in {s.ClipsFolder}.";
        _agoTimer.Start();
    }

    private void ClipArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int columns = Math.Max(1, (int)((ClipArea.ActualWidth - 18) / CardSlot));
        if (columns == _columns) return;
        _columns = columns;
        if (_clips.Count > 0) ShowFolder();
    }

    // ---- cards ---------------------------------------------------------------------------------

    private static ClipItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as ClipItem;

    private void Card_Loaded(object sender, RoutedEventArgs e) => RequestThumbnail(ItemOf(sender));

    // Scrolling reuses cards for other clips without raising Loaded again.
    private void Card_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (((FrameworkElement)sender).IsLoaded) RequestThumbnail(ItemOf(sender));
    }

    private async void RequestThumbnail(ClipItem? clip)
    {
        if (clip is not { ThumbnailRequested: false } item) return;
        item.ThumbnailRequested = true;
        string? thumb = await ThumbnailCache.GetAsync(item.File, item.Duration);
        if (thumb == null) return;
        try
        {
            item.Thumbnail = await Task.Run(() => LoadImage(thumb));
        }
        catch (Exception ex)
        {
            Log.Write("Loading thumbnail failed: " + ex.Message);
        }
    }

    private static ImageSource LoadImage(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 480;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => Play(ItemOf(sender));

    private void Play(ClipItem? item)
    {
        if (item == null) return;
        var clips = CurrentClips.ToList();
        int index = clips.IndexOf(item);
        if (index >= 0) PlayRequested?.Invoke(clips, index);
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e) => Play(ItemOf(sender));

#if DEBUG
    internal bool PlayFirstForRender()
    {
        if (CurrentClips.FirstOrDefault() is not { } first) return false;
        Play(first);
        return true;
    }
#endif

    private void OpenClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) OpenRequested?.Invoke(item.Path);
    }

    private void OpenWith_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
    }

    private void RevealClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (!RecycleBin.TrySend(item.Path))
        {
            MessageBox.Show(Window.GetWindow(this), $"Couldn't move \"{Path.GetFileName(item.Path)}\" to the Recycle Bin.", "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _clips.Remove(item);
        RebuildFolders();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string root = AppSettings.Current.ClipsFolder;
        string dir = _folder == ClipLibrary.AllClips ? root : Path.Combine(root, _folder);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var exts = string.Join(";", ShellIntegration.Extensions.Select(x => "*" + x));
        var dlg = new OpenFileDialog
        {
            Title = "Open a video or audio file",
            Filter = $"Media files|{exts}|All files|*.*",
            InitialDirectory = AppSettings.Current.LastFolder,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) OpenRequested?.Invoke(dlg.FileName);
    }
}
