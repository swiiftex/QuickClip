using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Gallery;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Views;

/// <summary>
/// Clips in the clips folder, grouped by game, newest first; clips can be ticked to add them to a montage project
/// or copy them out. Projects are listed under the folders and open in place of the clips.
/// </summary>
public partial class GalleryView : UserControl
{
    private sealed record Folder(string Name, int Count, DateTime Latest);

    private const double CardSlot = 264 + 14; // card width + right margin

    public static readonly DependencyProperty HasSelectionProperty =
        DependencyProperty.Register(nameof(HasSelection), typeof(bool), typeof(GalleryView));

    private readonly DispatcherTimer _reloadTimer;
    private readonly DispatcherTimer _agoTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private List<ClipItem> _clips = [];
    private List<ProjectSummary> _projects = [];
    private string _folder = ClipLibrary.AllClips;
    private string? _openProject;
    private string? _loadedRoot;
    private FileSystemWatcher? _watcher;
    private int _columns = 3;
    private bool _loading, _reloadQueued, _released, _settingLists;

    /// <summary>The user wants to edit a file.</summary>
    public event Action<string>? OpenRequested;

    /// <summary>The user wants to watch a clip; the folder's other clips come along for previous/next.</summary>
    public event Action<IReadOnlyList<ClipItem>, int>? PlayRequested;

    /// <summary>Some clips are ticked (each card then shows its checkbox, and clicking a card ticks it).</summary>
    public bool HasSelection
    {
        get => (bool)GetValue(HasSelectionProperty);
        private set => SetValue(HasSelectionProperty, value);
    }

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
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(6), DispatcherPriority.Background, (_, _) =>
        {
            _statusTimer!.Stop();
            StatusText.Visibility = Visibility.Collapsed;
        }, Dispatcher) { IsEnabled = false };

        ProjectPane.EditRequested += path => OpenRequested?.Invoke(path);
        ProjectPane.ProjectChanged += QueueReload;
        Recorder.Instance.ClipSaved += OnClipSaved;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _loadedRoot != AppSettings.Current.ClipsFolder) _ = ReloadAsync();
        };
        Loaded += (_, _) => _ = ReloadAsync();
    }

    /// <summary>Stops watching the folder, closes the project preview and drops the thumbnails (the window is closing).</summary>
    public async Task CloseAsync()
    {
        _released = true;
        Recorder.Instance.ClipSaved -= OnClipSaved;
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer.Stop();
        _agoTimer.Stop();
        _statusTimer.Stop();
        Rows.ItemsSource = null;
        _clips = [];
        await ProjectPane.CloseAsync();
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
            (_clips, _projects) = await Task.Run(() => (ClipLibrary.Scan(root), ProjectLibrary.List(root)));
            foreach (var clip in _clips)
            {
                // Keep thumbnails already on screen so a new clip doesn't make every card flicker.
                if (previous.TryGetValue(clip.Path, out var old) && old.Size == clip.Size && old.Thumbnail != null)
                {
                    clip.Thumbnail = old.Thumbnail;
                    clip.ThumbnailRequested = true;
                }
                clip.IsSelected = _selected.Contains(clip.Path);
                clip.SelectionChanged += OnClipSelectionChanged;
            }
            _selected.IntersectWith(_clips.Select(c => c.Path));
            RebuildFolders();
            RebuildProjects();
            UpdateSelection();
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

    // ---- folders ------------------------------------------------------------------------------

    private void RebuildFolders()
    {
        var folders = new List<Folder> { new(ClipLibrary.AllClips, _clips.Count, _clips.FirstOrDefault()?.Created ?? DateTime.MinValue) };
        folders.AddRange(_clips.GroupBy(c => c.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Folder(g.First().Folder, g.Count(), g.Max(c => c.Created)))
            .OrderByDescending(f => f.Latest));
        if (folders.All(f => !string.Equals(f.Name, _folder, StringComparison.OrdinalIgnoreCase))) _folder = ClipLibrary.AllClips;
        _settingLists = true;
        FolderList.ItemsSource = folders;
        FolderList.SelectedItem = _openProject != null ? null : folders.First(f => string.Equals(f.Name, _folder, StringComparison.OrdinalIgnoreCase));
        _settingLists = false;
        ShowFolder();
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLists || FolderList.SelectedItem is not Folder f) return;
        if (_openProject != null) CloseProject();
        if (f.Name == _folder) return;
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

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
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
            item.Thumbnail = await Task.Run(() => ThumbnailCache.LoadImage(thumb, 480));
        }
        catch (Exception ex)
        {
            Log.Write("Loading thumbnail failed: " + ex.Message);
        }
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        // While clips are being picked, a click picks; Ctrl+click always does.
        if (HasSelection || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) item.IsSelected = !item.IsSelected;
        else Play(item);
    }

    private void Play(ClipItem? item)
    {
        if (item == null) return;
        var clips = CurrentClips.ToList();
        int index = clips.IndexOf(item);
        if (index >= 0) PlayRequested?.Invoke(clips, index);
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e) => Play(ItemOf(sender));

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

    private void SelectClip_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsSelected = true;
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
        item.IsSelected = false;
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

    // ---- selection -----------------------------------------------------------------------------

    private void OnClipSelectionChanged(ClipItem clip)
    {
        if (clip.IsSelected) _selected.Add(clip.Path);
        else _selected.Remove(clip.Path);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        HasSelection = _selected.Count > 0;
        SelectionBar.Visibility = HasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!SelectionActions.IsEnabled) return; // showing progress instead
        long size = _clips.Where(c => c.IsSelected).Sum(c => c.Size);
        SelectionText.Text = $"{_selected.Count} clip{(_selected.Count == 1 ? "" : "s")} selected  ·  {Fmt.Size(size)}";
    }

    /// <summary>Selected clips, oldest first (the order they happened in, which is how a montage usually starts).</summary>
    private List<string> SelectedPaths() =>
        _clips.Where(c => c.IsSelected).OrderBy(c => c.Created).Select(c => c.Path).ToList();

    private void ClearSelection()
    {
        foreach (var clip in _clips.Where(c => c.IsSelected).ToList()) clip.IsSelected = false;
        _selected.Clear();
        UpdateSelection();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var clip in CurrentClips) clip.IsSelected = true;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => ClearSelection();

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddToProjectButton, Placement = PlacementMode.Bottom };
        foreach (var project in _projects)
        {
            // A TextBlock header: an underscore in a name isn't an access key.
            var item = new MenuItem { Header = new TextBlock { Text = $"{project.Name}  ({project.ClipCount})" } };
            item.Click += async (_, _) => await AddSelectionToAsync(project.Folder, open: false);
            menu.Items.Add(item);
        }
        if (_projects.Count > 0) menu.Items.Add(new Separator());
        var create = new MenuItem { Header = "New project…" };
        create.Click += async (_, _) =>
        {
            if (CreateProject() is { } folder) await AddSelectionToAsync(folder, open: true);
        };
        menu.Items.Add(create);
        menu.IsOpen = true;
    }

    private async Task AddSelectionToAsync(string folder, bool open)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        try
        {
            var project = ProjectLibrary.Load(folder);
            int added = await WithProgressAsync($"Adding to {project.Name}", paths.Count,
                progress => ProjectLibrary.AddClipsAsync(project, paths, progress));
            ClearSelection();
            ShowStatus(added == paths.Count
                ? $"Added {added} clip{(added == 1 ? "" : "s")} to {project.Name}."
                : $"Added {added} clip{(added == 1 ? "" : "s")} to {project.Name}; {paths.Count - added} {(paths.Count - added == 1 ? "was" : "were")} already in it.");
            await ReloadAsync();
            if (open) OpenProject(project.Folder);
        }
        catch (Exception ex)
        {
            Log.Write("Adding clips to a project failed: " + ex);
            MessageBox.Show(Window.GetWindow(this), "Couldn't add the clips to the project:\n\n" + ex.Message, "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CopyToFolder_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        var dialog = new OpenFolderDialog { Title = $"Copy {paths.Count} clip{(paths.Count == 1 ? "" : "s")} to" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await WithProgressAsync("Copying", paths.Count, async progress =>
            {
                await ProjectLibrary.CopyToFolderAsync(paths, dialog.FolderName, progress);
                return paths.Count;
            });
            ClearSelection();
            ShowStatus($"Copied {paths.Count} clip{(paths.Count == 1 ? "" : "s")} to {dialog.FolderName}.");
        }
        catch (Exception ex)
        {
            Log.Write("Copying clips failed: " + ex);
            MessageBox.Show(Window.GetWindow(this), "Couldn't copy the clips:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Shows "Copying… 2 of 5" in the selection bar (its buttons disabled) while <paramref name="work"/> runs.</summary>
    private async Task<T> WithProgressAsync<T>(string what, int count, Func<IProgress<int>, Task<T>> work)
    {
        SelectionActions.IsEnabled = false;
        SelectionText.Text = $"{what}…";
        try
        {
            return await work(new Progress<int>(done => SelectionText.Text = $"{what}… {done} of {count}"));
        }
        finally
        {
            SelectionActions.IsEnabled = true;
            UpdateSelection();
        }
    }

    // ---- projects ------------------------------------------------------------------------------

    private void RebuildProjects()
    {
        if (_openProject != null && !Directory.Exists(_openProject)) CloseProject();
        _settingLists = true;
        ProjectList.ItemsSource = _projects;
        ProjectList.SelectedItem = _projects.FirstOrDefault(p => string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase));
        _settingLists = false;
        NoProjectsText.Visibility = _projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProjectList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLists || ProjectList.SelectedItem is not ProjectSummary p) return;
        if (!string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase)) OpenProject(p.Folder);
    }

    private async void OpenProject(string folder)
    {
        _openProject = folder;
        _settingLists = true;
        FolderList.SelectedItem = null;
        ProjectList.SelectedItem = _projects.FirstOrDefault(p => string.Equals(p.Folder, folder, StringComparison.OrdinalIgnoreCase));
        _settingLists = false;
        ClipsPane.Visibility = Visibility.Collapsed;
        ProjectPane.Visibility = Visibility.Visible;
        try
        {
            await ProjectPane.ShowAsync(ProjectLibrary.Load(folder));
        }
        catch (Exception ex)
        {
            Log.Write("Opening a project failed: " + ex);
        }
    }

    private void CloseProject()
    {
        _openProject = null;
        ProjectPane.Close();
        ProjectPane.Visibility = Visibility.Collapsed;
        ClipsPane.Visibility = Visibility.Visible;
        _settingLists = true;
        ProjectList.SelectedItem = null;
        FolderList.SelectedItem = (FolderList.ItemsSource as List<Folder>)?.FirstOrDefault(f => f.Name == _folder);
        _settingLists = false;
    }

    /// <summary>Asks for a name and makes the project; returns its folder, or null if cancelled.</summary>
    private string? CreateProject()
    {
        var name = TextPrompt.Ask(Window.GetWindow(this), "New project", "Montage", "Create");
        if (name == null) return null;
        try
        {
            return ProjectLibrary.Create(AppSettings.Current.ClipsFolder, name).Folder;
        }
        catch (Exception ex)
        {
            Log.Write("Creating a project failed: " + ex);
            MessageBox.Show(Window.GetWindow(this), "Couldn't create the project:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (CreateProject() is not { } folder) return;
        await ReloadAsync();
        OpenProject(folder);
    }

    private static ProjectSummary? ProjectOf(object sender) => (sender as FrameworkElement)?.DataContext as ProjectSummary;

    private async void RenameProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOf(sender) is not { } p) return;
        var name = TextPrompt.Ask(Window.GetWindow(this), "Rename project", p.Name, "Rename");
        if (name == null) return;
        bool wasOpen = string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase);
        // The preview has the project's files open; let go of them first.
        if (wasOpen) ProjectPane.Unload();
        Project? renamed = null;
        for (int attempt = 0; attempt < 20 && renamed == null; attempt++)
        {
            try
            {
                renamed = ProjectLibrary.Rename(ProjectLibrary.Load(p.Folder), name);
            }
            catch (IOException) when (attempt < 19)
            {
                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "Couldn't rename the project:\n\n" + ex.Message, "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
        }
        if (wasOpen) _openProject = renamed?.Folder ?? p.Folder;
        await ReloadAsync();
        if (wasOpen) OpenProject(_openProject!);
    }

    private void OpenProjectFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOf(sender) is { } p) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{p.Folder}\"") { UseShellExecute = true });
    }

    private async void DeleteProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectOf(sender) is not { } p) return;
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"Move the project \"{p.Name}\" to the Recycle Bin?\n\nThe clips stay in the gallery; its montages, music and order go with it.",
            "QuickClip", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        if (string.Equals(p.Folder, _openProject, StringComparison.OrdinalIgnoreCase)) CloseProject();
        bool deleted = false;
        for (int attempt = 0; attempt < 20 && !(deleted = ProjectLibrary.Delete(ProjectLibrary.Load(p.Folder))); attempt++)
            await Task.Delay(100);
        if (!deleted)
            MessageBox.Show(Window.GetWindow(this), $"Couldn't move \"{p.Name}\" to the Recycle Bin.", "QuickClip", MessageBoxButton.OK, MessageBoxImage.Warning);
        await ReloadAsync();
    }

#if DEBUG
    internal bool PlayFirstForRender()
    {
        if (CurrentClips.FirstOrDefault() is not { } first) return false;
        Play(first);
        return true;
    }

    internal List<string> SelectForRender(int count)
    {
        foreach (var clip in CurrentClips.Take(count)) clip.IsSelected = true;
        return SelectedPaths();
    }

    internal Task AddSelectionForRender(string folder) => AddSelectionToAsync(folder, open: true);

    internal ProjectView ProjectForRender => ProjectPane;
#endif
}
