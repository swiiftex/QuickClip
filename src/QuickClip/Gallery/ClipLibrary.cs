using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace QuickClip.Gallery;

/// <summary>One clip file shown in the gallery.</summary>
public sealed class ClipItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private string _ago = "";
    private bool _selected;

    public ClipItem(FileInfo file, string folder, double? duration)
    {
        Path = file.FullName;
        Folder = folder;
        Title = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        Created = file.CreationTime;
        Size = file.Length;
        Duration = duration;
        File = file;
        RefreshAgo();
    }

    public string Path { get; }
    public string Folder { get; }
    public string Title { get; }
    public DateTime Created { get; }
    public long Size { get; }
    public double? Duration { get; }
    internal FileInfo File { get; }
    internal bool ThumbnailRequested { get; set; }

    public string DurationText => Duration is double d ? Fmt.Time(d, millis: false) : "";
    public string SizeText => Fmt.Size(Size);

    public string Ago
    {
        get => _ago;
        private set { _ago = value; OnChanged(); }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnChanged(); }
    }

    /// <summary>Ticked in the gallery, for adding to a project or copying out.</summary>
    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnChanged();
            SelectionChanged?.Invoke(this);
        }
    }

    public event Action<ClipItem>? SelectionChanged;

    public void RefreshAgo() => Ago = TimeAgo.Format(Created, DateTime.Now);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class TimeAgo
{
    public static string Format(DateTime then, DateTime now)
    {
        var d = now - then;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return Plural((int)d.TotalMinutes, "minute");
        if (d.TotalHours < 24) return Plural((int)d.TotalHours, "hour");
        if (d.TotalDays < 2) return "yesterday";
        if (d.TotalDays < 7) return Plural((int)d.TotalDays, "day");
        if (d.TotalDays < 31) return Plural((int)(d.TotalDays / 7), "week");
        if (d.TotalDays < 365) return Plural(Math.Max(1, (int)(d.TotalDays / 30.4)), "month");
        return Plural((int)(d.TotalDays / 365.25), "year");
    }

    private static string Plural(int n, string unit) => n == 1 ? $"1 {unit} ago" : $"{n} {unit}s ago";
}

/// <summary>
/// Finds clips in the clips folder: each subfolder is a game, files directly inside count as "Desktop".
/// The Projects folder holds montage projects, not more clips.
/// </summary>
internal static class ClipLibrary
{
    public const string AllClips = "All clips";
    private static readonly string[] Extensions = [".mp4", ".mkv", ".mov", ".webm", ".m4v"];

    public static bool IsClipFile(string path) =>
        Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && !path.Contains(".quickclip-tmp", StringComparison.OrdinalIgnoreCase);

    public static List<ClipItem> Scan(string root)
    {
        var clips = new List<ClipItem>();
        if (!Directory.Exists(root)) return clips;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };
        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            if (!IsClipFile(path)) continue;
            var file = new FileInfo(path);
            string relative = System.IO.Path.GetRelativePath(root, file.DirectoryName!);
            string folder = relative == "." ? "Desktop" : relative.Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (string.Equals(folder, ProjectLibrary.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
            clips.Add(new ClipItem(file, folder, Mp4Info.TryReadDuration(path)));
        }
        return clips.OrderByDescending(c => c.Created).ToList();
    }
}

/// <summary>Reads an MP4/MOV duration from its 'mvhd' box without decoding anything.</summary>
internal static class Mp4Info
{
    public static double? TryReadDuration(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            return FindMvhd(fs, 0, fs.Length);
        }
        catch
        {
            return null;
        }
    }

    private static double? FindMvhd(FileStream fs, long start, long end)
    {
        Span<byte> header = stackalloc byte[16];
        long pos = start;
        while (pos + 8 <= end)
        {
            fs.Position = pos;
            if (fs.Read(header[..8]) < 8) return null;
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerSize = 8;
            if (size == 1)
            {
                if (fs.Read(header[8..16]) < 8) return null;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..16]);
                headerSize = 16;
            }
            else if (size == 0) size = end - pos;
            if (size < headerSize) return null;

            if (type == "moov") return FindMvhd(fs, pos + headerSize, pos + size);
            if (type == "mvhd")
            {
                Span<byte> body = stackalloc byte[32];
                if (fs.Read(body) < 32) return null;
                bool v1 = body[0] == 1;
                uint timescale = BinaryPrimitives.ReadUInt32BigEndian(v1 ? body.Slice(20, 4) : body.Slice(12, 4));
                ulong duration = v1 ? BinaryPrimitives.ReadUInt64BigEndian(body.Slice(24, 8)) : BinaryPrimitives.ReadUInt32BigEndian(body.Slice(16, 4));
                return timescale > 0 ? duration / (double)timescale : null;
            }
            pos += size;
        }
        return null;
    }
}

/// <summary>Small JPEG previews of clips, made once with FFmpeg and cached in %LOCALAPPDATA%\QuickClip\thumbs.</summary>
internal static class ThumbnailCache
{
    private static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickClip", "thumbs");
    private static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>Reads a cached thumbnail, scaled down to <paramref name="width"/> pixels (call off the UI thread; the image is frozen).</summary>
    public static ImageSource LoadImage(string path, int width)
    {
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = width;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }

    public static string PathFor(FileInfo file)
    {
        var key = $"{file.FullName.ToLowerInvariant()}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return System.IO.Path.Combine(Dir, hash + ".jpg");
    }

    public static async Task<string?> GetAsync(FileInfo file, double? duration)
    {
        string thumb = PathFor(file);
        if (File.Exists(thumb)) return thumb;
        if (Deps.FFmpeg == null) return null;
        await Gate.WaitAsync();
        try
        {
            if (File.Exists(thumb)) return thumb;
            Directory.CreateDirectory(Dir);
            // A third of the way in tends to show the action rather than the lead-up.
            double at = duration is > 0.5 ? duration.Value * 0.33 : 0;
            foreach (double seek in at > 0 ? new[] { at, 0 } : [0])
            {
                using var p = Process.Start(Proc.StartInfo(Deps.FFmpeg,
                [
                    "-hide_banner", "-nostdin", "-v", "error", "-ss", Fmt.Num(seek), "-i", file.FullName,
                    "-frames:v", "1", "-vf", "scale=480:-2", "-q:v", "5", "-y", thumb,
                ]));
                if (p == null) return null;
                try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                var drain = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                await drain;
                if (p.ExitCode == 0 && File.Exists(thumb)) return thumb;
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Write("Thumbnail failed for " + file.FullName + ": " + ex.Message);
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }
}
