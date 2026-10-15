using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QuickClip.Controls;
using QuickClip.Media;

namespace QuickClip;

/// <summary>
/// One audio stream of the open file, or a sound added to it (another file's audio starting anywhere on the clip's
/// timeline), with its export/preview settings.
/// </summary>
public sealed class AudioTrack : INotifyPropertyChanged
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x4C, 0xC2, 0xFF), Color.FromRgb(0x6C, 0xCB, 0x5F), Color.FromRgb(0xFF, 0x8C, 0x42),
        Color.FromRgb(0xC5, 0x86, 0xF7), Color.FromRgb(0xF7, 0xD0, 0x46), Color.FromRgb(0xFF, 0x6B, 0x9A),
    ];

    private string _name;
    private bool _include = true;
    private double _volume = 1;
    private bool _solo;
    private double _offset;

    public AudioTrack(AudioStreamInfo info, TimelineState timeline, double duration) : this(info, timeline, duration, null, 0) { }

    private AudioTrack(AudioStreamInfo info, TimelineState timeline, double duration, string? soundPath, double offset)
    {
        Info = info;
        Timeline = timeline;
        _name = info.DisplayName;
        Color = Palette[info.Ordinal % Palette.Length];
        Brush = new SolidColorBrush(Color);
        Brush.Freeze();
        Waveform = new WaveformData(duration);
        SoundPath = soundPath;
        SoundDuration = duration;
        if (soundPath != null) MoveTo(offset);
    }

    /// <summary>A sound added to the clip: <paramref name="info"/> is its audio stream in <paramref name="path"/>.</summary>
    public static AudioTrack ForSound(string path, AudioStreamInfo info, double duration, TimelineState timeline, double start) =>
        new(info, timeline, duration, path, start);

    public AudioStreamInfo Info { get; }
    public TimelineState Timeline { get; }
    public WaveformData Waveform { get; }
    public Color Color { get; }
    public SolidColorBrush Brush { get; }
    public string Description => SoundPath == null ? Info.Description
        : $"{System.IO.Path.GetFileName(SoundPath)} · {Info.Description} · " +
          (_offset >= 0 ? $"starts at {Fmt.Time(_offset)}" : $"the first {Fmt.Time(-_offset)} is cut off");

    /// <summary>An added sound's file, or null for one of the clip's own tracks.</summary>
    public string? SoundPath { get; }
    public bool IsSound => SoundPath != null;
    /// <summary>How long an added sound is.</summary>
    public double SoundDuration { get; }

    /// <summary>Where an added sound starts on the clip's timeline, in seconds (negative: its beginning is cut off).</summary>
    public double Offset => _offset;

    /// <summary>Moves an added sound to start at <paramref name="start"/>, keeping at least a little of it in the clip.</summary>
    public void MoveTo(double start)
    {
        const double keep = 0.05;
        double earliest = -Math.Max(0, SoundDuration - keep);
        start = Math.Clamp(start, earliest, Math.Max(earliest, Timeline.Duration - keep));
        if (Set(ref _offset, start, nameof(Offset)))
        {
            OnPropertyChanged(nameof(Description));
            Moved?.Invoke(this);
        }
    }

    /// <summary>An added sound moved.</summary>
    public event Action<AudioTrack>? Moved;

    /// <summary>The user asked to take this added sound off the clip.</summary>
    public event Action<AudioTrack>? RemoveRequested;
    public void Remove() => RemoveRequested?.Invoke(this);

    // The editor's preview plays an added sound from a copy already placed on the clip's timeline.
    internal string? PreviewFile { get; set; }
    internal double PreviewOffset { get; set; } = double.NaN;
    public int Number => Info.Ordinal + 1;

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Changed?.Invoke(this); }
    }

    public bool Include
    {
        get => _include;
        set { if (Set(ref _include, value)) Changed?.Invoke(this); }
    }

    /// <summary>Linear gain, 0..2.</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(Math.Round(value, 2), 0, 2);
            if (Set(ref _volume, value))
            {
                OnPropertyChanged(nameof(VolumePercent));
                OnPropertyChanged(nameof(VolumeText));
                Changed?.Invoke(this);
            }
        }
    }

    /// <summary>Slider binding, 0..200.</summary>
    public double VolumePercent
    {
        get => Math.Round(_volume * 100);
        set => Volume = value / 100.0;
    }

    public string VolumeText
    {
        get
        {
            if (_volume <= 0.0001) return "muted";
            double db = 20 * Math.Log10(_volume);
            return $"{_volume * 100:0}%  ({(db >= 0 ? "+" : "")}{db:0.0} dB)";
        }
    }

    /// <summary>Preview only: listen to just this track.</summary>
    public bool Solo
    {
        get => _solo;
        set { if (Set(ref _solo, value)) Changed?.Invoke(this); }
    }

    /// <summary>Raised when anything affecting preview, export or the waveform changes.</summary>
    public event Action<AudioTrack>? Changed;

    /// <summary>Waveform data grew; lanes should redraw.</summary>
    public event Action? WaveformUpdated;
    public void NotifyWaveformUpdated() => WaveformUpdated?.Invoke();

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
