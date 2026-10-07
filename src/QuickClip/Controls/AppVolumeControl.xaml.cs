using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickClip.Shell;

namespace QuickClip.Controls;

/// <summary>Mute button and slider for QuickClip's own level in the Windows volume mixer; the two always match.</summary>
public partial class AppVolumeControl : UserControl
{
    private Window? _window;
    private bool _muted;
    private bool _showing;

    public AppVolumeControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppVolume.ChangedElsewhere += OnChangedElsewhere;
            // The level may have been changed in the mixer, or the output device switched, while we were in the background.
            _window = Window.GetWindow(this);
            if (_window != null) _window.Activated += OnWindowActivated;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppVolume.ChangedElsewhere -= OnChangedElsewhere;
            if (_window != null) _window.Activated -= OnWindowActivated;
            _window = null;
        };
        IsVisibleChanged += (_, _) => Refresh();
    }

    public void ToggleMute()
    {
        if (!MuteButton.IsEnabled) return;
        AppVolume.SetMuted(_muted = !_muted);
        UpdateButton();
    }

    /// <summary>Changes the level by <paramref name="percent"/> points.</summary>
    public void Nudge(int percent)
    {
        if (LevelSlider.IsEnabled) LevelSlider.Value = Math.Clamp(LevelSlider.Value + percent, 0, 100);
    }

    private void OnWindowActivated(object? sender, EventArgs e) => Refresh();

    private void OnChangedElsewhere() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        if (!IsVisible) return;
        AppVolume.Refresh();
        var volume = AppVolume.Read();
        _showing = true;
        LevelSlider.IsEnabled = MuteButton.IsEnabled = volume != null;
        if (volume is { } v)
        {
            LevelSlider.Value = Math.Round(v.Level * 100);
            _muted = v.Muted;
        }
        _showing = false;
        UpdateButton();
    }

    private void Level_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_showing)
        {
            AppVolume.SetLevel((float)(e.NewValue / 100));
            // Moving the slider unmutes, as it does in the mixer.
            if (_muted) AppVolume.SetMuted(_muted = false);
        }
        UpdateButton();
    }

    private void Level_Wheel(object sender, MouseWheelEventArgs e)
    {
        Nudge(Math.Sign(e.Delta) * 5);
        e.Handled = true;
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void UpdateButton()
    {
        double level = LevelSlider.Value;
        MuteButton.Content = _muted || level == 0 ? "\uE74F" : level < 34 ? "\uE993" : level < 67 ? "\uE994" : "\uE995";
        MuteButton.ToolTip = _muted ? "Unmute (M)" : "Mute (M)";
        LevelSlider.ToolTip = $"Volume {level:0}%, QuickClip's level in the Windows volume mixer";
    }
}
