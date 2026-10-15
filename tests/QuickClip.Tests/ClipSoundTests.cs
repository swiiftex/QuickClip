using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class ClipSoundTests
{
    [Fact]
    public void The_clip_sound_is_a_short_wav_windows_can_play()
    {
        byte[] wav = ClipSound.Load();
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20));  // PCM, which PlaySound plays without a codec
        Assert.Equal(16, BitConverter.ToInt16(wav, 34)); // bits per sample
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));

        int bytesPerSecond = BitConverter.ToInt32(wav, 28);
        double seconds = BitConverter.ToInt32(wav, 40) / (double)bytesPerSecond;
        Assert.InRange(seconds, 0.1, 1.0);
    }
}
