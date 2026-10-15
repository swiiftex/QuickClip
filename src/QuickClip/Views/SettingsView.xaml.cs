using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Media;
using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Updates;

namespace QuickClip.Views;

public partial class SettingsView : UserControl
{
    private sealed record Choice(string Label, object Value)
    {
        public override string ToString() => Label;
    }

    private readonly DispatcherTimer _restartTimer;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private IReadOnlyList<CaptureEngine.AudioDevice> _mics = [];
    private readonly List<(ComboBox Combo, Button Remove)> _micRows = [];
    private IReadOnlySet<string> _encoders = new HashSet<string>();
    private bool _loading;
    private bool _capturingHotkey;

    public SettingsView()
    {
        InitializeComponent();
        // Recording settings restart the recorder; wait until the user stops fiddling.
        _restartTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            _restartTimer!.Stop();
            _ = Recorder.Instance.ApplySettingsAsync();
        }, Dispatcher) { IsEnabled = false };
        Unloaded += (_, _) =>
        {
            Recorder.Instance.StateChanged -= OnRecorderState;
            Updater.Changed -= OnUpdaterChanged;
        };
        Loaded += (_, _) =>
        {
            Recorder.Instance.StateChanged += OnRecorderState;
            Updater.Changed += OnUpdaterChanged;
        };
    }

    // ---- updates -------------------------------------------------------------------------------

    private bool _installing;

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(ShowUpdateState);

    private void ShowUpdateState()
    {
        VersionText.Text = $"You have QuickClip {AppVersion.Current.Display}" + (AppVersion.Current.IsPrerelease ? " (a nightly build)" : "") +
                           (Updater.LastChecked is DateTime t ? $" · last checked {t:t}" : "");
        CheckUpdatesButton.IsEnabled = !Updater.IsChecking && !_installing;
        var release = Updater.Available;
        UpdateActions.Visibility = release != null && !_installing ? Visibility.Visible : Visibility.Collapsed;
        if (_installing) return;
        UpdateStatusText.Text =
            Updater.IsChecking ? "Checking for updates…"
            : release != null ? $"QuickClip {release.Version.Display} is available." +
                                (Updater.CanInstall(release) ? "" : " Download it from the release page to install it.")
            : Updater.LastError != null ? $"Couldn't check for updates: {Updater.LastError}."
            : Updater.LastChecked != null ? "You're on the latest version."
            : "";
        InstallUpdateButton.IsEnabled = release != null && Updater.CanInstall(release);
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updater.CheckAsync();

    private async void ThisVersionNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) await window.ShowWhatsNewAsync(after: null);
    }

    private async void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ChannelCombo.SelectedItem is not Choice { Value: string channel } || channel == Updater.Channel) return;
        Updater.Channel = channel;
        ShowChannelHint();
        await Updater.CheckAsync(); // what's on offer depends on the channel
    }

    private void ShowChannelHint() => ChannelHint.Text = Updater.Channel == Updater.Nightly
        ? "New features as they're made, before they're released. They may have bugs."
        : "Tested releases. A nightly build you're on now stays until a newer stable release comes out.";

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Updater.Available?.PageUrl ?? Updater.ReleasesPage) { UseShellExecute = true });

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is not { } release || _installing) return;
        _installing = true;
        UpdateActions.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = $"Downloading QuickClip {release.Version.Display}…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateStatusText.Text = $"Downloading QuickClip {release.Version.Display}… {p:P0}";
            });
            bool windowHidden = !(Window.GetWindow(this)?.IsVisible ?? false);
            var startSwap = await Updater.PrepareInstallAsync(release, restartInTray: windowHidden, progress, CancellationToken.None);
            UpdateStatusText.Text = "Restarting to finish the update…";
            if (!await ((App)Application.Current).QuitAsync(startSwap))
                throw new InvalidOperationException("QuickClip has to close to install the update.");
        }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex);
            _installing = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            ShowUpdateState();
            UpdateStatusText.Text = "The update didn't install: " + ex.Message;
        }
    }

    /// <summary>Re-reads settings and devices; called whenever the page is shown.</summary>
    public async void Refresh()
    {
        _loading = true;
        try
        {
            var s = AppSettings.Current;
            RecordingCheck.IsChecked = s.RecordingEnabled;

            _monitors = await Task.Run(Monitors.List);
            MonitorCombo.ItemsSource = _monitors.Select(m => new Choice(m.Label, m.DeviceName)).ToList();
            Select(MonitorCombo, (_monitors.FirstOrDefault(m => string.Equals(m.DeviceName, s.RecordMonitor, StringComparison.OrdinalIgnoreCase))
                                  ?? _monitors.FirstOrDefault(m => m.IsPrimary))?.DeviceName);
            RefreshAreas(s.RecordAspect);

            int stepIndex = Array.FindIndex(RecordingPresets.BufferSteps, v => v >= s.BufferSeconds);
            BufferSlider.Value = stepIndex < 0 ? RecordingPresets.BufferSteps.Length - 1 : stepIndex;

            QualityCombo.ItemsSource = Enum.GetValues<RecordingQuality>().Reverse().Select(q => new Choice(RecordingPresets.Label(q), q)).ToList();
            Select(QualityCombo, s.RecordQuality);
            FpsCombo.ItemsSource = RecordingPresets.FrameRates.Select(f => new Choice($"{f} fps", f)).ToList();
            Select(FpsCombo, RecordingPresets.FrameRates.OrderBy(f => Math.Abs(f - s.RecordFps)).First());
            RefreshResolutions(s.RecordResolution);

            _encoders = await VideoEncoders.GetAvailableAsync();
            var auto = RecordingPresets.DefaultEncoder(_encoders);
            var encoders = new List<Choice> { new(auto != null ? $"Automatic — {auto.Label}" : "Automatic (no GPU encoder found)", "") };
            encoders.AddRange(RecordingPresets.RecordingEncoderOrder.Where(_encoders.Contains)
                .Select(id => new Choice(VideoEncoders.Find(id)?.Label ?? id, id)));
            EncoderCombo.ItemsSource = encoders;
            Select(EncoderCombo, s.RecordEncoder);
            CursorCheck.IsChecked = s.CaptureCursor;
            GameWindowCheck.IsChecked = s.PreferGameWindow;
            ShowGameWindowState();

            SplitChatCheck.IsChecked = s.SplitChatAudio;
            SplitMusicCheck.IsChecked = s.SplitMusicAudio;
            MicCheck.IsChecked = s.RecordMic;
            _mics = CaptureEngine.IsAvailable ? await Task.Run(() => { CaptureEngine.Initialize(); return CaptureEngine.EnumMicrophones(); }) : [];
            ShowMics(s.MicDeviceIds.Count > 0 ? s.MicDeviceIds : [""]);
            MicTracksCombo.ItemsSource = new List<Choice> { new("All on one Mic track", false), new("A track for each microphone", true) };
            Select(MicTracksCombo, s.SeparateMicTracks);

            ShowHotkey();
            StartupCheck.IsChecked = Startup.IsEnabled;
            MinTrayCheck.IsChecked = s.MinimizeToTray;
            CloseTrayCheck.IsChecked = s.CloseToTray;
            SoundCheck.IsChecked = s.ClipSound;
            MotionBlurCheck.IsChecked = s.ShareMotionBlur;
            ShowShareSize();
            ShellCheck.IsChecked = SafeIsRegistered();
            if (ShellIntegration.IsMenuPackageInstalled())
            {
                ShellCheck.IsEnabled = false;
                ShellCheck.ToolTip = "On the Windows 11 right-click menu (added with scripts\\add-win11-menu.ps1; run it with -Remove to undo).";
            }
            FolderBox.Text = s.ClipsFolder;
            AutoUpdateCheck.IsChecked = s.CheckForUpdates;
            ChannelCombo.ItemsSource = new[]
            {
                new Choice("Stable", Updater.Stable),
                new Choice("Nightly (development builds)", Updater.Nightly),
            };
            Select(ChannelCombo, Updater.Channel);
            ShowChannelHint();
        }
        finally
        {
            _loading = false;
        }
        ShowUpdateState();
        UpdateEstimates();
        OnRecorderState(Recorder.Instance.State);
    }

    private static void Select(ComboBox combo, object? value)
    {
        foreach (var item in combo.Items)
            if (item is Choice c && Equals(c.Value, value)) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private MonitorInfo? SelectedMonitor =>
        _monitors.FirstOrDefault(m => Equals(m.DeviceName, (MonitorCombo.SelectedItem as Choice)?.Value)) ?? _monitors.FirstOrDefault();

    private string SelectedAspect => (AreaCombo.SelectedItem as Choice)?.Value as string ?? "";

    /// <summary>The part of the selected monitor that gets recorded.</summary>
    private RecordingArea? SelectedArea =>
        SelectedMonitor is { } m ? RecordingPresets.Area(m.Width, m.Height, SelectedAspect) : null;

#if DEBUG
    internal void SelectAspectForRender(string aspect) => Select(AreaCombo, aspect);
    internal void SetGameWindowForRender(bool on) => GameWindowCheck.IsChecked = on;

    /// <summary>Adds a second microphone row and scrolls to the microphones.</summary>
    internal void AddMicForRender()
    {
        AddMic_Click(AddMicButton, new RoutedEventArgs());
        MicTracksRow.BringIntoView();
    }
#endif

    private void RefreshAreas(string value)
    {
        var m = SelectedMonitor;
        var items = RecordingPresets.Aspects
            .Select(a => new Choice(m != null ? RecordingPresets.AreaLabel(a, RecordingPresets.Area(m.Width, m.Height, a), m.Width, m.Height)
                : a == "" ? "Whole monitor" : a, a))
            .ToList();
        bool was = _loading;
        _loading = true;
        AreaCombo.ItemsSource = items;
        Select(AreaCombo, value);
        _loading = was;
    }

    private void RefreshResolutions(int value)
    {
        var area = SelectedArea;
        var items = new List<Choice> { new(area is { } n ? $"Native ({n.Width}×{n.Height})" : "Native", 0) };
        if (area is { } a)
            foreach (int r in RecordingPresets.Resolutions.Where(r => r > 0 && r < Math.Min(a.Width, a.Height)))
            {
                var (w, h) = RecordingPresets.OutputSize(a.Width, a.Height, r);
                items.Add(new Choice($"{r}p ({w}×{h})", r));
            }
        bool was = _loading;
        _loading = true;
        ResolutionCombo.ItemsSource = items;
        Select(ResolutionCombo, value);
        _loading = was;
    }

    /// <summary>The crop doesn't apply to a game's window: greyed out while recording that is preferred.</summary>
    private void ShowGameWindowState()
    {
        bool gameWindow = GameWindowCheck.IsChecked == true;
        AreaRow.IsEnabled = !gameWindow;
        AreaRow.Opacity = gameWindow ? 0.45 : 1;
        AreaRow.ToolTip = gameWindow ? "Off while Prefer game window capture is on: a game's window is recorded as it is." : null;
    }

    // ---- microphones ---------------------------------------------------------------------------

    private List<string> SelectedMics => _micRows.Select(r => (r.Combo.SelectedItem as Choice)?.Value as string ?? "").ToList();

    /// <summary>A row per chosen microphone. One that isn't plugged in keeps its place until it's changed or removed.</summary>
    private void ShowMics(IReadOnlyList<string> ids)
    {
        bool was = _loading;
        _loading = true;
        MicList.Children.Clear();
        _micRows.Clear();
        string defaultMic = _mics.FirstOrDefault(m => m.IsDefault != 0).Name ?? "";
        foreach (string id in ids)
        {
            var choices = new List<Choice> { new(defaultMic.Length > 0 ? $"Windows default ({defaultMic})" : "Windows default", "") };
            choices.AddRange(_mics.Select(m => new Choice(m.Name, m.Id)));
            if (!choices.Any(c => Equals(c.Value, id))) choices.Add(new Choice("A microphone that isn't connected", id));
            var combo = new ComboBox { ItemsSource = choices };
            Select(combo, id);
            combo.SelectionChanged += Changed;

            int index = _micRows.Count;
            var remove = new Button
            {
                Content = "Remove", Style = (Style)FindResource("TextButton"), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Stop recording this microphone",
            };
            remove.Click += (_, e) =>
            {
                var remaining = SelectedMics;
                remaining.RemoveAt(index);
                ShowMics(remaining);
                Changed(remove, e);
            };

            var row = new Grid { Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(remove, 1);
            row.Children.Add(combo);
            row.Children.Add(remove);
            MicList.Children.Add(row);
            _micRows.Add((combo, remove));
        }
        _loading = was;
        UpdateMicControls();
    }

    /// <summary>The first connected microphone that isn't chosen yet (the default counts as whichever it is now).</summary>
    private string? NextMic(IReadOnlyList<string> chosen)
    {
        string? defaultId = _mics.FirstOrDefault(m => m.IsDefault != 0).Id;
        var taken = chosen.Select(id => id == "" ? defaultId : id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _mics.Select(m => m.Id).FirstOrDefault(id => !taken.Contains(id));
    }

    private void AddMic_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedMics;
        if (NextMic(ids) is not { } next) return;
        ids.Add(next);
        ShowMics(ids);
        Changed(AddMicButton, e);
    }

    private void UpdateMicControls()
    {
        bool on = MicCheck.IsChecked == true, several = _micRows.Count > 1;
        foreach (var (combo, remove) in _micRows)
        {
            combo.IsEnabled = remove.IsEnabled = on;
            remove.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        }
        AddMicButton.Visibility = NextMic(SelectedMics) != null ? Visibility.Visible : Visibility.Collapsed;
        AddMicButton.IsEnabled = on;
        MicTracksRow.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        MicTracksCombo.IsEnabled = on;
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var s = AppSettings.Current;
        if (sender == MonitorCombo) RefreshAreas(SelectedAspect);
        if (sender == MonitorCombo || sender == AreaCombo) RefreshResolutions((ResolutionCombo.SelectedItem as Choice)?.Value as int? ?? 0);

        s.RecordingEnabled = RecordingCheck.IsChecked == true;
        s.RecordMonitor = (MonitorCombo.SelectedItem as Choice)?.Value as string ?? "";
        s.RecordAspect = SelectedAspect;
        s.BufferSeconds = RecordingPresets.BufferSteps[(int)Math.Round(BufferSlider.Value)];
        if (QualityCombo.SelectedItem is Choice { Value: RecordingQuality q }) s.RecordQuality = q;
        if (FpsCombo.SelectedItem is Choice { Value: int fps }) s.RecordFps = fps;
        if (ResolutionCombo.SelectedItem is Choice { Value: int res }) s.RecordResolution = res;
        s.RecordEncoder = (EncoderCombo.SelectedItem as Choice)?.Value as string ?? "";
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.PreferGameWindow = GameWindowCheck.IsChecked == true;
        ShowGameWindowState();
        s.SplitChatAudio = SplitChatCheck.IsChecked == true;
        s.SplitMusicAudio = SplitMusicCheck.IsChecked == true;
        s.RecordMic = MicCheck.IsChecked == true;
        s.MicDeviceIds = SelectedMics;
        s.SeparateMicTracks = MicTracksCombo.SelectedItem is Choice { Value: true };
        UpdateMicControls();
        s.MinimizeToTray = MinTrayCheck.IsChecked == true;
        s.CloseToTray = CloseTrayCheck.IsChecked == true;
        s.ClipSound = SoundCheck.IsChecked == true;
        s.ShareMotionBlur = MotionBlurCheck.IsChecked == true;
        s.Save();

        if (sender == StartupCheck)
        {
            try { Startup.Set(StartupCheck.IsChecked == true); }
            catch (Exception ex) { Log.Write("Changing startup failed: " + ex.Message); }
        }

        UpdateEstimates();
        bool recordingSetting = sender != StartupCheck && sender != MinTrayCheck && sender != CloseTrayCheck && sender != SoundCheck
                                && sender != MotionBlurCheck;
        if (recordingSetting)
        {
            _restartTimer.Stop();
            _restartTimer.Start();
        }
    }

    private void ShowShareSize() =>
        ShareSizeBox.Text = AppSettings.Current.ShareSizeMB.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    private void ShareSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) ShowShareSize();
        if (e.Key is Key.Enter or Key.Escape) Keyboard.ClearFocus();
    }

    /// <summary>The share size limit, from 1 MB up; anything that isn't a number goes back to what it was.</summary>
    private void ShareSize_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        var s = AppSettings.Current;
        if (double.TryParse(ShareSizeBox.Text.Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mb) && mb > 0)
        {
            s.ShareSizeMB = Math.Clamp(Math.Round(mb, 1), 1, 4000);
            s.Save();
        }
        ShowShareSize();
    }

    /// <summary>Bitrate, memory and clip size for the current choices.</summary>
    private void UpdateEstimates()
    {
        var s = AppSettings.Current;
        BufferText.Text = RecordingPresets.DurationLabel(s.BufferSeconds);
        if (SelectedArea is not { } area)
        {
            RamText.Text = BitrateText.Text = "";
            return;
        }
        var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, s.RecordResolution);
        string encoderId = s.RecordEncoder.Length > 0 ? s.RecordEncoder : RecordingPresets.DefaultEncoder(_encoders)?.Id ?? "h264_amf";
        string family = VideoEncoders.Find(encoderId)?.Family ?? "h264";
        int kbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, family);
        int tracks = RecordingPresets.AudioTrackCount(s);
        long ram = RecordingPresets.EstimateRamBytes(kbps, tracks, s.BufferSeconds);
        long clip = (long)((kbps + tracks * RecordingPresets.AudioKbps) * 1000.0 / 8 * s.BufferSeconds);
        BitrateText.Text = $"≈ {kbps / 1000.0:0.#} Mbps";
        RamText.Text = $"The replay buffer needs about {Fmt.Size(ram)} of memory. A full-length clip is about {Fmt.Size(clip)} on disk.";
    }

    private void OnRecorderState(RecorderState state) => Dispatcher.BeginInvoke(() =>
    {
        ChatText.Text = state.ChatApps.Count > 0
            ? $"Detected now: {string.Join(", ", state.ChatApps)}"
            : "No voice chat app is running right now.";
        MusicText.Text = (state.MusicApps.Count > 0
            ? $"Detected now: {string.Join(", ", state.MusicApps)}."
            : "No music app is running right now.") + " Music played in a web browser stays on the Desktop track.";
        RecorderStatus.Text = state.Running ? $"Recording · {state.Summary}"
            : state.Error != null ? "Not recording: " + state.Error
            : AppSettings.Current.RecordingEnabled ? "Starting…" : "Recording is off.";
    });

    // ---- hotkey --------------------------------------------------------------------------------

    private void ShowHotkey()
    {
        var s = AppSettings.Current;
        HotkeyButton.Content = new Hotkey(s.HotkeyModifiers, s.HotkeyKey).ToString();
        HotkeyStatus.Text = App.Host?.HotkeyActive == false && s.HotkeyKey != 0
            ? "Another app is already using this shortcut — pick a different one."
            : "Works everywhere, including in games.";
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        App.Host?.SuspendHotkey();
        HotkeyButton.Content = "Press the keys…";
        HotkeyStatus.Text = "Esc cancels, Backspace clears.";
        HotkeyButton.Focus();
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndHotkeyCapture();
            return;
        }
        if (Hotkey.IsModifierKey(key)) return;

        var s = AppSettings.Current;
        var hotkey = key is Key.Back or Key.Delete ? new Hotkey(0, 0) : Hotkey.FromWpf(Keyboard.Modifiers, key);
        s.HotkeyModifiers = hotkey.Modifiers;
        s.HotkeyKey = hotkey.VirtualKey;
        s.Save();
        EndHotkeyCapture();
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturingHotkey) EndHotkeyCapture();
    }

    private void EndHotkeyCapture()
    {
        _capturingHotkey = false;
        App.Host?.RegisterHotkey();
        ShowHotkey();
    }

    // ---- general -------------------------------------------------------------------------------

    private static bool SafeIsRegistered()
    {
        try { return ShellIntegration.IsRegistered(); }
        catch { return false; }
    }

    private void ShellCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (ShellCheck.IsChecked == true) ShellIntegration.Register();
            else ShellIntegration.Unregister();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "Couldn't update the Explorer menu:\n\n" + ex.Message, "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlayClipSound_Click(object sender, RoutedEventArgs e) => ClipSound.Play();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should clips be saved?", InitialDirectory = AppSettings.Current.ClipsFolder };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        AppSettings.Current.ClipsFolder = dlg.FolderName;
        AppSettings.Current.Save();
        FolderBox.Text = dlg.FolderName;
    }
}
