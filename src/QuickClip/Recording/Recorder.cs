using QuickClip.Media;

namespace QuickClip.Recording;

public sealed record SavedClip(string Path, string Folder, int Seconds);

/// <summary>What the recorder is currently doing, for the UI.</summary>
public sealed record RecorderState(bool Running, string? Error, string Summary, IReadOnlyList<string> ChatApps);

/// <summary>Instant-replay service: keeps the capture engine running per the settings and saves clips on demand.</summary>
internal sealed class Recorder
{
    public static Recorder Instance { get; } = new();

    private readonly object _gate = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly GameDetector _games = new();
    private Timer? _poll;
    private IReadOnlyList<string> _chatApps = [];
    private string _summary = "";

    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }
    public int VideoKbps { get; private set; }
    public int AudioTracks { get; private set; }

    /// <summary>Raised (on a worker thread) when recording starts, stops, fails or chat apps change.</summary>
    public event Action<RecorderState>? StateChanged;
    public event Action<SavedClip>? ClipSaved;
    public event Action<string>? SaveFailed;

    public RecorderState State => new(IsRunning, Error, _summary, _chatApps);

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
                Error = "No graphics card video encoder was found (NVIDIA, AMD or Intel).";
                return;
            }

            var (w, h) = RecordingPresets.OutputSize(monitor.Width, monitor.Height, s.RecordResolution);
            bool scaled = w != monitor.Width || h != monitor.Height;
            string family = VideoEncoders.Find(encoder)?.Family ?? "h264";
            VideoKbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, family);
            AudioTracks = 1 + (s.SplitChatAudio ? 1 : 0) + (s.RecordMic ? 1 : 0);

            Error = CaptureEngine.Start(monitor.DeviceName, s.RecordFps, scaled ? w : 0, scaled ? h : 0, encoder, VideoKbps,
                RecordingPresets.AudioKbps, s.BufferSeconds, s.CaptureCursor, s.SplitChatAudio, s.RecordMic,
                string.IsNullOrEmpty(s.MicDeviceId) ? null : s.MicDeviceId);
            if (Error != null) return;

            IsRunning = true;
            _summary = $"{h}p{s.RecordFps} · {family.ToUpperInvariant()} · {RecordingPresets.Label(s.RecordQuality)}";
            Log.Write($"Recording {monitor.DeviceName} at {w}x{h}@{s.RecordFps} with {encoder}, {VideoKbps} kbps, {s.BufferSeconds}s buffer");
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
            var processes = ProcessScan.Snapshot();
            var chat = ChatApps.Find(processes);
            if (AppSettings.Current.SplitChatAudio) CaptureEngine.SetChatProcesses(chat.Select(c => c.Pid).ToList());
            _games.Poll();

            var names = chat.Select(c => c.Name).Distinct().ToList();
            if (!names.SequenceEqual(_chatApps))
            {
                _chatApps = names;
                StateChanged?.Invoke(State);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Recorder poll failed: " + ex.Message);
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
