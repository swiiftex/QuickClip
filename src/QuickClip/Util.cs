using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QuickClip;

/// <summary>Locates the bundled FFmpeg/FFprobe/libmpv binaries.</summary>
internal static class Deps
{
    public static string AppDir { get; } = AppContext.BaseDirectory;
    public static string? FFmpeg { get; } = Find("ffmpeg.exe");
    public static string? FFprobe { get; } = Find("ffprobe.exe");
    public static bool HasLibMpv => File.Exists(Path.Combine(AppDir, "libmpv-2.dll"));

    private static string? Find(string exe)
    {
        foreach (var dir in new[] { Path.Combine(AppDir, "ffmpeg"), AppDir })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}

/// <summary>
/// The log file (%LOCALAPPDATA%\QuickClip\quickclip.log). Write only queues the line; a background thread writes it,
/// so a slow or locked file can never hold up the caller (the recorder's threads log too).
/// </summary>
internal static class Log
{
    private static readonly BlockingCollection<string> Pending = new(boundedCapacity: 10_000);
    private static readonly ManualResetEventSlim Idle = new(true);
    private static bool _failureReported;

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickClip", "quickclip.log");

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 2_000_000) File.Move(FilePath, FilePath + ".old", overwrite: true);
        }
        catch { }
        new Thread(WriteLoop) { IsBackground = true, Name = "Log writer" }.Start();
    }

    public static void Write(string message)
    {
        Debug.WriteLine(message);
        Idle.Reset();
        if (!Pending.TryAdd($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {message}"))
            Idle.Set(); // full: something is flooding the log
    }

    /// <summary>Waits (briefly) for queued lines to reach the file, e.g. before exiting.</summary>
    public static void Flush(int timeoutMs = 1000) => Idle.Wait(timeoutMs);

    private static void WriteLoop()
    {
        var batch = new StringBuilder();
        foreach (var line in Pending.GetConsumingEnumerable())
        {
            batch.Clear().AppendLine(line);
            while (batch.Length < 64 * 1024 && Pending.TryTake(out var more)) batch.AppendLine(more);
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    // Shared for writing too, so the app, its tests and a second copy can all append.
                    using var file = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    file.Write(Encoding.UTF8.GetBytes(batch.ToString()));
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(200); // briefly locked (a virus scan, say)
                }
                catch (Exception ex)
                {
                    ReportFailure(ex);
                    break;
                }
            }
            if (Pending.Count == 0) Idle.Set();
        }
    }

    // A log that can't be written hides every other problem; leave a note about why somewhere else, once.
    private static void ReportFailure(Exception ex)
    {
        if (_failureReported) return;
        _failureReported = true;
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "QuickClip log error.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} QuickClip couldn't write {FilePath}:{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch { }
    }
}

internal static class Proc
{
    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = Process.Start(StartInfo(exe, args)) ?? throw new InvalidOperationException($"Could not start {exe}");
        using var reg = ct.Register(() => KillQuietly(p));
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, await stdout, await stderr);
    }

    public static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public static string Quote(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Length == 0 || a.Any(c => c is ' ' or '"' or ';' or '|') ? "\"" + a.Replace("\"", "\\\"") + "\"" : a));
}

internal static class Fmt
{
    public static string Num(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>1:02:03.456 / 2:03.456</summary>
    public static string Time(double seconds, bool millis = true)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        string ms = millis ? "." + t.Milliseconds.ToString("000") : "";
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}{ms}"
            : $"{t.Minutes}:{t.Seconds:00}{ms}";
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Parses "1:23.5", "83.5", "1:02:03" into seconds.</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is 0 or > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}
