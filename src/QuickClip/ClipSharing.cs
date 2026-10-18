using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using QuickClip.Media;
using QuickClip.Shell;

namespace QuickClip;

internal enum ShareStage { Working, Done, Failed, Cancelled }

/// <summary>What instant share is doing, for the status bar.</summary>
internal sealed record ShareStatus(ShareStage Stage, string ClipName, double Progress, string Text, string? Output = null);

/// <summary>
/// Instant share: makes a copy of a clip small enough to post anywhere (<see cref="ShareCopy"/>) and puts it on the
/// clipboard, ready to paste into Discord, a chat or a folder. One clip at a time.
/// </summary>
internal static class ClipSharing
{
    /// <summary>
    /// Where the copies go: in Videos but outside the clips folder, so they don't show up in the gallery. Not in
    /// AppData: apps treat files pasted from there with suspicion (Discord took one as an empty file).
    /// </summary>
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "QuickClip Shared");

    private static CancellationTokenSource? _cts;

    public static bool IsBusy => _cts != null;
    public static ShareStatus? Status { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Status"/> changes.</summary>
    public static event Action? Changed;

    public static async Task ShareAsync(string clip)
    {
        if (_cts != null) return; // the status bar shows the one already going
        var cts = new CancellationTokenSource();
        _cts = cts;
        string name = Path.GetFileNameWithoutExtension(clip);
        try
        {
            Report(new ShareStatus(ShareStage.Working, name, -1, "Sharing…"));
            await Task.Run(DeleteOldCopies);
            var info = await MediaInfo.ProbeAsync(clip, cts.Token);
            string encoder = ShareCopy.Encoder(await VideoEncoders.GetAvailableAsync());
            string dir = Path.Combine(Folder, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss-fff"));
            Directory.CreateDirectory(dir);
            string output = Path.Combine(dir, name + ".mp4");

            var progress = new Progress<(int Attempt, double Progress)>(p => Report(new ShareStatus(ShareStage.Working, name, p.Progress,
                $"{(p.Attempt == 1 ? "Sharing" : "Compressing a little more")}  ·  {p.Progress:P0}")));
            long size = await ShareCopy.CreateAsync(info, output, encoder, progress, cts.Token, motionBlur: AppSettings.Current.ShareMotionBlur);

            await CopyToClipboardAsync(output);
            Log.Write($"Shared {clip} as {output} ({Fmt.Size(size)}), copied to the clipboard");
            Report(new ShareStatus(ShareStage.Done, name, 1, $"Copied to the clipboard ({Fmt.Size(size)}): paste it anywhere with Ctrl+V", output));
            if (Application.Current?.MainWindow is not { IsActive: true })
                App.Host?.Notify("Clip ready to share", $"{name} ({Fmt.Size(size)}) is on the clipboard: paste it anywhere with Ctrl+V");
        }
        catch (OperationCanceledException)
        {
            Report(new ShareStatus(ShareStage.Cancelled, name, 0, "Sharing stopped."));
        }
        catch (Exception ex)
        {
            Log.Write($"Sharing {clip} failed: {ex}");
            Report(new ShareStatus(ShareStage.Failed, name, 0, $"Couldn't share: {FirstLine(ex.Message)}"));
        }
        finally
        {
            _cts = null;
            cts.Dispose();
        }
    }

    public static void Cancel() => _cts?.Cancel();

    private static void Report(ShareStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    /// <summary>Puts the file on the clipboard the way Explorer's Copy does, so pasting into an app attaches it.</summary>
    private static async Task CopyToClipboardAsync(string path)
    {
        try
        {
            // Windows' own data object for the file: exactly what Explorer puts on the clipboard.
            ShellClipboard.CopyFile(path);
            return;
        }
        catch (Exception ex)
        {
            Log.Write("Copying the file the shell's way failed, using a plain file list: " + ex.Message);
        }

        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (ExternalException) when (attempt < 10)
            {
                await Task.Delay(100); // another app has the clipboard open
            }
        }
    }

    /// <summary>Copies from earlier days; they've been pasted by now.</summary>
    private static void DeleteOldCopies()
    {
        try
        {
            if (!Directory.Exists(Folder)) return;
            foreach (var dir in Directory.GetDirectories(Folder))
                if (DateTime.Now - Directory.GetCreationTime(dir) > TimeSpan.FromDays(2))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
        catch { }
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
