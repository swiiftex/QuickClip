using Microsoft.Win32;
using QuickClip.Media;
using QuickClip.Shell;

namespace QuickClip.Recording;

public sealed record SavedClip(string Path, string Folder, int Seconds);

/// <summary>What the recorder is currently doing, for the UI.</summary>
public sealed record RecorderState(bool Running, string? Error, string Summary, IReadOnlyList<string> ChatApps, IReadOnlyList<string> MusicApps);

/// <summary>Instant-replay service: keeps the capture engine running per the settings and saves clips on demand.</summary>
internal sealed class Recorder
{
    public static Recorder Instance { get; } = new();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly GameDetector _games = new();
    private Timer? _poll;
    private IReadOnlyList<string> _chatApps = [], _musicApps = [];
    private string _summary = "";
    private string? _routingError;
    private string? _routing;
    private readonly Dictionary<uint, string> _audioProcesses = [];  // running processes that have had audio
    private string _family = "h264";
    private IntPtr _captureWindow;                       // the game window being recorded, or zero for the monitor
    private (int Width, int Height) _captureWindowSize;

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public int VideoKbps { get; private set; }
    public int AudioTracks { get; private set; }

    /// <summary>Raised (on a worker thread) when recording starts, stops, fails or chat or music apps change.</summary>
    public event Action<RecorderState>? StateChanged;
    public event Action<SavedClip>? ClipSaved;
    public event Action<string>? SaveFailed;

    public RecorderState State => new(IsRunning, Error, _summary, _chatApps, _musicApps);

    private Recorder()
    {
        // A display change (a resolution change, a game switching modes) or a sleep can leave the screen capture
        // stale, sending black frames or none; start it again. The replay buffer is kept.
        SystemEvents.DisplaySettingsChanged += (_, _) => RestartCapture("the display settings changed");
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) RestartCapture("the PC woke from sleep");
        };
    }

    private void RestartCapture(string why)
    {
        if (!IsRunning) return;
        Log.Write($"Starting screen capture again: {why}");
        CaptureEngine.RestartVideo();
    }

    /// <summary>(Re)starts recording from the current settings, or stops it if recording is turned off.</summary>
    public Task ApplySettingsAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            StopCore();
            if (AppSettings.Current.RecordingEnabled) StartCore();
        }
        StateChanged?.Invoke(State);
    });

    public Task StopAsync() => Task.Run(() =>
    {
        lock (_gate) StopCore();
        StateChanged?.Invoke(State);
    });

    private void StartCore()
    {
        var s = AppSettings.Current;
        Error = null;
        if (!CaptureEngine.IsAvailable)
        {
            Error = "The recording engine (QuickClipCapture.dll) is missing.";
            return;
        }
        try
        {
            CaptureEngine.Initialize();
            var monitors = Monitors.List();
            var monitor = monitors.FirstOrDefault(m => string.Equals(m.DeviceName, s.RecordMonitor, StringComparison.OrdinalIgnoreCase))
                ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (monitor == null)
            {
                Error = "No monitor to record was found.";
                return;
            }

            var available = VideoEncoders.GetAvailableAsync().GetAwaiter().GetResult();
            string? encoder = RecordingPresets.RecordingEncoderOrder.Contains(s.RecordEncoder) && available.Contains(s.RecordEncoder)
                ? s.RecordEncoder
                : RecordingPresets.DefaultEncoder(available)?.Id;
            if (encoder == null)
            {
                Error = "No graphics card video encoder works on this PC (NVIDIA, AMD or Intel). Updating the graphics driver may help.";
                return;
            }

            // The aspect ratio is for the monitor: a game recorded by its window is taken as it is.
            string aspect = s.RecordAspect;
            var area = RecordingPresets.Area(monitor.Width, monitor.Height, aspect);
            bool cropped = aspect != "" && (area.Width != monitor.Width || area.Height != monitor.Height);
            var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, s.RecordResolution);
            bool scaled = w != area.Width || h != area.Height;
            string family = VideoEncoders.Find(encoder)?.Family ?? "h264";
            VideoKbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, family);
            var mics = s.RecordMic ? Microphones.Plan(s.MicDeviceIds, CaptureEngine.EnumMicrophones()) : [];
            bool separateMics = s.SeparateMicTracks && mics.Count > 1;
            AudioTracks = 1 + (s.SplitChatAudio ? 1 : 0) + (s.SplitMusicAudio ? 1 : 0) + (separateMics ? mics.Count : Math.Min(mics.Count, 1));

            Error = CaptureEngine.Start(monitor.DeviceName, aspect, s.RecordFps, scaled ? w : 0, scaled ? h : 0, encoder, VideoKbps,
                RecordingPresets.AudioKbps, s.BufferSeconds, s.CaptureCursor, s.SplitChatAudio, s.SplitMusicAudio, mics, separateMics);
            if (Error != null) return;
            if (mics.Count > 0)
                Log.Write($"Recording {mics.Count} microphone{(mics.Count == 1 ? "" : "s")}{(separateMics ? " on separate tracks" : "")}: " +
                          string.Join(", ", mics.Select(m => m.DeviceId == "" ? $"{m.Title} (Windows default)" : m.Title)));

            IsRunning = true;
            _family = family;
            _captureWindow = IntPtr.Zero;
            _captureWindowSize = default;
            _routing = null;
            lock (_audioProcesses) _audioProcesses.Clear();
            _summary = $"{h}p{s.RecordFps}{(cropped ? $" {aspect}" : "")} · {family.ToUpperInvariant()} · {RecordingPresets.Label(s.RecordQuality)}" +
                       (s.PreferGameWindow ? " · game window" : "");
            Log.Write($"Recording {monitor.DeviceName}{(cropped ? $" ({area.Width}x{area.Height} at {area.X},{area.Y})" : "")} at {w}x{h}@{s.RecordFps} " +
                      $"with {encoder}, {VideoKbps} kbps, {s.BufferSeconds}s buffer");
            _poll = new Timer(_ => Poll(), null, 0, 2000);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Write("Starting the recorder failed: " + ex);
        }
    }

    private void StopCore()
    {
        _poll?.Dispose();
        _poll = null;
        if (IsRunning) CaptureEngine.Stop();
        IsRunning = false;
        _summary = "";
    }

    private void Poll()
    {
        try
        {
            var s = AppSettings.Current;
            var processes = ProcessScan.Snapshot();
            var chat = ChatApps.Find(processes);
            var music = MusicApps.Find(processes);
            List<uint> chatPids = s.SplitChatAudio ? [.. chat.Select(c => c.Pid)] : [];
            List<uint> musicPids = s.SplitMusicAudio ? [.. music.Select(m => m.Pid)] : [];
            var plan = PlanAudio(processes, chatPids, musicPids);
            CaptureEngine.SetAudioProcesses(plan);
            LogRouting(processes, plan);
            _games.Poll();
            if (s.PreferGameWindow) UpdateCaptureWindow(s);

            var chatNames = chat.Select(c => c.Name).Distinct().ToList();
            var musicNames = music.Select(m => m.Name).Distinct().ToList();
            if (!chatNames.SequenceEqual(_chatApps) || !musicNames.SequenceEqual(_musicApps))
            {
                (_chatApps, _musicApps) = (chatNames, musicNames);
                StateChanged?.Invoke(State);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Recorder poll failed: " + ex.Message);
        }
    }

    /// <summary>Notes in the log which apps each audio track records, whenever that changes.</summary>
    private void LogRouting(IReadOnlyList<ProcessScan.Entry> processes, AudioRouting.Plan plan)
    {
        var names = new Dictionary<uint, string>();
        foreach (var p in processes) names.TryAdd(p.Pid, Path.GetFileNameWithoutExtension(p.ExeName));
        string Names(IEnumerable<uint> pids) =>
            pids.Any() ? string.Join(", ", pids.Select(p => names.GetValueOrDefault(p, $"process {p}")).Distinct()) : "nothing";
        string desktop = plan.Exclude == 0 ? $"Desktop only {Names(plan.Desktop)}"
            : $"Desktop everything except {Names([plan.Exclude])}" + (plan.Desktop.Count > 0 ? $" but with {Names(plan.Desktop)}, which it started" : "");
        string routing = desktop + $"; Chat {Names(plan.Chat)}; Music {Names(plan.Music)}" +
                         (plan.Missing.Count > 0 ? $"; Desktop can't record {Names(plan.Missing)}, which started the chat or music app" : "");
        if (routing == _routing) return;
        _routing = routing;
        Log.Write("Audio tracks: " + routing);
    }

    /// <summary>
    /// "Prefer game window capture": the game in the foreground is recorded by its window, still when you tab out of
    /// it, until it closes; the monitor otherwise. Only starting or leaving a game switches, so the replay buffer
    /// isn't cut short by tabbing out.
    /// </summary>
    private void UpdateCaptureWindow(AppSettings s)
    {
        IntPtr window = _games.GameWindow;
        if (window == IntPtr.Zero && GameDetector.WindowExists(_captureWindow)) window = _captureWindow;
        var size = window != IntPtr.Zero ? GameDetector.WindowSize(window) : default;
        if (window != IntPtr.Zero && (size.Width < 64 || size.Height < 64)) return; // minimized: carry on as it was
        if (window == _captureWindow && size == _captureWindowSize) return;
        _captureWindow = window;
        _captureWindowSize = size;
        if (window == IntPtr.Zero)
        {
            CaptureEngine.SetCaptureWindow(IntPtr.Zero, 0, 0, 0);
            Log.Write("Recording the monitor again: the game's window closed");
            return;
        }
        var (w, h) = RecordingPresets.OutputSize(size.Width & ~7, size.Height & ~1, s.RecordResolution);
        int kbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, _family);
        CaptureEngine.SetCaptureWindow(window, w, h, kbps);
        Log.Write($"Recording the game window \"{GameDetector.WindowTitle(window, 80)}\" ({size.Width}x{size.Height}) at {w}x{h}, {kbps} kbps");
    }

    private AudioRouting.Plan PlanAudio(IReadOnlyList<ProcessScan.Entry> processes, List<uint> chat, List<uint> music)
    {
        uint self = (uint)Environment.ProcessId;
        try
        {
            lock (_audioProcesses)
            {
                // Without chat or music apps, Desktop is simply everything else.
                if (chat.Count + music.Count > 0) AudioRouting.Remember(_audioProcesses, processes, CoreAudio.SessionProcessIds());
                else _audioProcesses.Clear();
                var plan = AudioRouting.Make(processes, chat, music, () => _audioProcesses.Keys.ToHashSet(), ProcessScan.ExePath, self);
                _routingError = null;
                return plan;
            }
        }
        catch (Exception ex)
        {
            // Can't list the apps with audio: leave out just the first separated app, as with only one.
            if (ex.Message != _routingError) Log.Write("Listing the apps with audio failed: " + ex.Message);
            _routingError = ex.Message;
            return new AudioRouting.Plan(chat.Concat(music).DefaultIfEmpty(self).First(), [], chat, music);
        }
    }

    public CaptureEngine.Stats Stats() => IsRunning ? CaptureEngine.GetStats() : default;

    /// <summary>Saves the buffer to the clips folder, under the game being played.</summary>
    public async Task<SavedClip?> SaveClipAsync()
    {
        if (!IsRunning)
        {
            SaveFailed?.Invoke(Error ?? "Recording is turned off.");
            return null;
        }
        if (!await _saveGate.WaitAsync(0)) return null; // a save is already in progress
        try
        {
            var s = AppSettings.Current;
            int seconds = s.BufferSeconds;
            string folder = _games.GameForClip(seconds) ?? "Desktop";
            string dir = Path.Combine(s.ClipsFolder, folder);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{folder} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, $"{folder} {DateTime.Now:yyyy-MM-dd HH-mm-ss} ({i}).mp4");

            string? error = await Task.Run(() => CaptureEngine.Save(path, seconds, folder));
            if (error != null)
            {
                Log.Write("Saving a clip failed: " + error);
                SaveFailed?.Invoke(error);
                return null;
            }
            var clip = new SavedClip(path, folder, seconds);
            Log.Write($"Saved clip {path}");
            ClipSaved?.Invoke(clip);
            return clip;
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
