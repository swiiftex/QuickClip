using System.Diagnostics;

namespace QuickClip.Tests;

public sealed class LogTests
{
    [Fact]
    public void Writing_never_waits_for_the_file()
    {
        string marker = Guid.NewGuid().ToString("N");
        var watch = Stopwatch.StartNew();
        // Hold the log file so nothing else can write it for a moment (as a virus scan or a stuck writer might).
        using (new FileStream(Log.FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Parallel.For(0, 200, i => Log.Write($"log test {marker} {i}"));
            Assert.True(watch.ElapsedMilliseconds < 500, $"writing 200 lines took {watch.ElapsedMilliseconds} ms");
            Thread.Sleep(400);
        }

        // Once the file is free again, every line arrives.
        Log.Flush(5000);
        using var reader = new StreamReader(new FileStream(Log.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        Assert.Equal(200, reader.ReadToEnd().Split('\n').Count(line => line.Contains(marker)));
    }
}
