using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using QuickClip.Controls;
using QuickClip.Media;

namespace QuickClip;

/// <summary>One audio stream of the open file, with its export/preview settings.</summary>
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

    public AudioTrack(AudioStreamInfo info, TimelineState timeline, double duration)
    {
        Info = info;
        Timeline = timeline;
        _name = info.DisplayName;
        Color = Palette[info.Ordinal % Palette.Length];
        Brush = new SolidColorBrush(Color);
        Brush.Freeze();
        Waveform = new WaveformData(duration);
    }

    public AudioStreamInfo Info { get; }
    public TimelineState Timeline { get; }
    public WaveformData Waveform { get; }
    public Color Color { get; }
    public SolidColorBrush Brush { get; }
    public string Description => Info.Description;
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
