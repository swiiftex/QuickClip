using System.Diagnostics;

namespace QuickClip.Media;

/// <summary>Peak envelope of one audio stream, filled in progressively by <see cref="Waveform.ExtractAsync"/>.</summary>
public sealed class WaveformData
{
    public const int PeaksPerSecond = 400;
    private const int SampleRate = 16000;
    public const int SamplesPerPeak = SampleRate / PeaksPerSecond;
    public static int DecodeSampleRate => SampleRate;

    private volatile float[] _peaks;
    private volatile int _count;

    public WaveformData(double durationSeconds)
    {
        _peaks = new float[(int)Math.Ceiling((durationSeconds + 1) * PeaksPerSecond)];
    }

    public bool IsComplete { get; set; }

    /// <summary>Peaks (0..1, max absolute sample) and how many are valid. Safe to call from any thread.</summary>
    public (float[] Peaks, int Count) Snapshot()
    {
        int n = _count;
        var p = _peaks;
        return (p, Math.Min(n, p.Length));
    }

    internal void Append(float peak)
    {
        var p = _peaks;
        if (_count >= p.Length)
        {
            var grown = new float[p.Length * 2 + PeaksPerSecond];
            Array.Copy(p, grown, p.Length);
            _peaks = p = grown;
        }
        p[_count] = peak;
        _count++;
    }
}

internal static class Waveform
{
    /// <summary>Decodes one audio stream to 16 kHz mono PCM through FFmpeg and records its peaks.</summary>
    public static async Task ExtractAsync(string file, int streamIndex, WaveformData data, Action progress, CancellationToken ct)
    {
        if (Deps.FFmpeg == null) return;
        var args = new[]
        {
            "-hide_banner", "-nostdin", "-v", "error", "-i", file, "-map", $"0:{streamIndex}",
            "-ac", "1", "-ar", WaveformData.DecodeSampleRate.ToString(), "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1",
        };
        using var p = Process.Start(Proc.StartInfo(Deps.FFmpeg, args));
        if (p == null) return;
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var reg = ct.Register(() => Proc.KillQuietly(p));
        var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);

        var stream = p.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        int carry = -1;           // dangling low byte from the previous read
        int inPeak = 0;
        int peak = 0;
        long lastReport = Environment.TickCount64;

        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(buffer, ct); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (read <= 0) break;

            int i = 0;
            if (carry >= 0)
            {
                Accumulate((short)(carry | (buffer[0] << 8)));
                carry = -1;
                i = 1;
            }
            for (; i + 1 < read; i += 2)
                Accumulate((short)(buffer[i] | (buffer[i + 1] << 8)));
            if (i < read) carry = buffer[i];

            if (Environment.TickCount64 - lastReport > 250)
            {
                lastReport = Environment.TickCount64;
                progress();
            }
        }

        if (!ct.IsCancellationRequested)
        {
            if (inPeak > 0) data.Append(peak / 32768f);
            await p.WaitForExitAsync(CancellationToken.None);
            if (p.ExitCode != 0) Log.Write($"Waveform for stream {streamIndex} failed: {(await errors).Trim()}");
            data.IsComplete = true;
            progress();
        }

        void Accumulate(short sample)
        {
            int a = sample == short.MinValue ? 32767 : Math.Abs((int)sample);
            if (a > peak) peak = a;
            if (++inPeak == WaveformData.SamplesPerPeak)
            {
                data.Append(peak / 32768f);
                peak = 0;
                inPeak = 0;
            }
        }
    }
}
