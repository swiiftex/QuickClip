using System.Runtime.InteropServices;
using System.Text.Json;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Gallery;

/// <summary>A montage project: a folder of clips in the order they play, plus an optional music track.</summary>
public sealed class Project
{
    internal Project(string folder) => Folder = folder;

    public string Folder { get; }
    public string Name => Path.GetFileName(Folder);
    /// <summary>Clip files, in play order.</summary>
    public List<string> Clips { get; } = [];
    /// <summary>The music file (kept in the project's Music folder), or null.</summary>
    public string? Music { get; set; }

    public string MusicFolder => Path.Combine(Folder, "Music");
    public string ExportsFolder => Path.Combine(Folder, "Exports");
}

internal sealed record ProjectSummary(string Name, string Folder, int ClipCount);

/// <summary>
/// Projects live in "Projects" inside the clips folder, one folder each, with the order and music in project.json.
/// Clips are hard links to the gallery's files when they're on the same drive (no extra space, and deleting one
/// name leaves the other), otherwise copies.
/// </summary>
internal static class ProjectLibrary
{
    public const string FolderName = "Projects";
    private const string FileName = "project.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // File names, relative to the project folder (and its Music folder).
    private sealed record Saved(List<string>? Clips, string? Music);

    public static string Root(string clipsFolder) => Path.Combine(clipsFolder, FolderName);

    public static List<ProjectSummary> List(string clipsFolder)
    {
        string root = Root(clipsFolder);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(d => new ProjectSummary(Path.GetFileName(d), d, ClipFiles(d).Count()))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static Project Load(string folder)
    {
        var project = new Project(folder);
        Saved? saved = null;
        try
        {
            string file = Path.Combine(folder, FileName);
            if (File.Exists(file)) saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Json);
        }
        catch (Exception ex)
        {
            Log.Write($"Reading {folder}\\{FileName} failed: {ex.Message}");
        }

        // The saved order first; clips put in the folder some other way go after it, oldest first.
        var files = ClipFiles(folder).ToList();
        foreach (var name in saved?.Clips ?? [])
        {
            var match = files.FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
            if (match == null) continue;
            project.Clips.Add(match);
            files.Remove(match);
        }
        project.Clips.AddRange(files.OrderBy(File.GetCreationTime));

        if (saved?.Music is { } music && File.Exists(Path.Combine(project.MusicFolder, music)))
            project.Music = Path.Combine(project.MusicFolder, music);
        return project;
    }

    public static void Save(Project project)
    {
        var saved = new Saved([.. project.Clips.Select(Path.GetFileName).OfType<string>()], project.Music is { } m ? Path.GetFileName(m) : null);
        File.WriteAllText(Path.Combine(project.Folder, FileName), JsonSerializer.Serialize(saved, Json));
    }

    public static Project Create(string clipsFolder, string name)
    {
        string folder = UniquePath(Path.Combine(Root(clipsFolder), SafeName(name)), "");
        Directory.CreateDirectory(folder);
        var project = new Project(folder);
        Save(project);
        return project;
    }

    /// <summary>Renames the project's folder; returns the project at its new place.</summary>
    public static Project Rename(Project project, string name)
    {
        string safe = SafeName(name);
        if (string.Equals(safe, project.Name, StringComparison.Ordinal)) return project;
        string target = string.Equals(safe, project.Name, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetDirectoryName(project.Folder)!, safe) // only the case changes
            : UniquePath(Path.Combine(Path.GetDirectoryName(project.Folder)!, safe), "");
        Directory.Move(project.Folder, target);
        return Load(target);
    }

    /// <summary>Moves the whole project folder to the Recycle Bin.</summary>
    public static bool Delete(Project project) => RecycleBin.TrySend(project.Folder);

    /// <summary>Adds clips to the end of the project, skipping any already in it. Returns how many were added.</summary>
    public static async Task<int> AddClipsAsync(Project project, IReadOnlyList<string> files, IProgress<int>? progress = null)
    {
        int added = 0;
        for (int i = 0; i < files.Count; i++)
        {
            string source = files[i];
            string target = Path.Combine(project.Folder, Path.GetFileName(source));
            bool already = File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length;
            if (!already)
            {
                target = UniquePath(Path.Combine(project.Folder, Path.GetFileNameWithoutExtension(source)), Path.GetExtension(source));
                await Task.Run(() => LinkOrCopy(source, target));
                project.Clips.Add(target);
                added++;
            }
            progress?.Report(i + 1);
        }
        Save(project);
        return added;
    }

    /// <summary>Takes the clip out of the project (to the Recycle Bin; the gallery's clip stays).</summary>
    public static bool RemoveClip(Project project, string clip)
    {
        if (File.Exists(clip) && !RecycleBin.TrySend(clip)) return false;
        project.Clips.Remove(clip);
        Save(project);
        return true;
    }

    /// <summary>Copies a music file into the project (replacing the previous one), or clears it with null.</summary>
    public static async Task SetMusicAsync(Project project, string? file)
    {
        string? old = project.Music;
        if (file != null)
        {
            Directory.CreateDirectory(project.MusicFolder);
            string target = Path.Combine(project.MusicFolder, Path.GetFileName(file));
            if (!string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(target)) target = UniquePath(Path.Combine(project.MusicFolder, Path.GetFileNameWithoutExtension(file)), Path.GetExtension(file));
                await Task.Run(() => File.Copy(file, target));
            }
            project.Music = target;
        }
        else project.Music = null;
        Save(project);
        if (old != null && !string.Equals(old, project.Music, StringComparison.OrdinalIgnoreCase) && File.Exists(old))
            RecycleBin.TrySend(old);
    }

    /// <summary>Plain copies (to a folder outside QuickClip). Existing files of the same name are kept; the copy gets a new name.</summary>
    public static async Task CopyToFolderAsync(IReadOnlyList<string> files, string folder, IProgress<int>? progress = null)
    {
        for (int i = 0; i < files.Count; i++)
        {
            string source = files[i];
            string target = UniquePath(Path.Combine(folder, Path.GetFileNameWithoutExtension(source)), Path.GetExtension(source));
            await Task.Run(() => File.Copy(source, target));
            progress?.Report(i + 1);
        }
    }

    /// <summary>A hard link when the target is on the same drive, otherwise a copy.</summary>
    internal static void LinkOrCopy(string source, string target)
    {
        if (!CreateHardLink(target, source, IntPtr.Zero)) File.Copy(source, target);
    }

    private static IEnumerable<string> ClipFiles(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Where(ClipLibrary.IsClipFile) : [];

    private static string SafeName(string name)
    {
        string safe = GameDetector.SanitizeFolderName(name);
        return safe.Length > 0 ? safe : "Project";
    }

    private static string UniquePath(string stem, string extension)
    {
        string path = stem + extension;
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++) path = $"{stem} ({i}){extension}";
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr security);
}
