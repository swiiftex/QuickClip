using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickClip.Updates;

public sealed record ReleaseAsset(string Url, long Size, string? Sha256);

/// <summary>A GitHub release: <paramref name="Setup"/> updates installed copies, <paramref name="Portable"/> unzipped ones.</summary>
public sealed record ReleaseInfo(AppVersion Version, string Tag, string Name, string PageUrl, ReleaseAsset? Setup, ReleaseAsset? Portable);

/// <summary>Checks the GitHub repository for newer releases and installs them in place.</summary>
internal static class Updater
{
    public const string Repository = "swiiftex/QuickClip";
    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    private static readonly HttpClient Http = CreateClient();

    public static ReleaseInfo? Available { get; private set; }
    public static string? LastError { get; private set; }
    public static DateTime? LastChecked { get; private set; }
    public static bool IsChecking { get; private set; }

    /// <summary>Raised (on any thread) when a check starts or finishes.</summary>
    public static event Action? Changed;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"QuickClip/{AppVersion.Current}");
        return http;
    }

    public static async Task<ReleaseInfo?> CheckAsync()
    {
        if (IsChecking) return Available;
        IsChecking = true;
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases?per_page=30");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(response.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "the release page isn't reachable"
                    : $"GitHub answered {(int)response.StatusCode}");
            Available = PickUpdate(await response.Content.ReadAsStringAsync(cts.Token), AppVersion.Current);
            LastError = null;
            Log.Write(Available != null ? $"Update available: {Available.Version}" : "No update available");
        }
        catch (Exception ex)
        {
            LastError = ex is OperationCanceledException or HttpRequestException ? "couldn't reach GitHub" : ex.Message;
            Log.Write("Update check failed: " + ex.Message);
        }
        finally
        {
            IsChecking = false;
            LastChecked = DateTime.Now;
            Changed?.Invoke();
        }
        return Available;
    }

    /// <summary>
    /// The newest published release above <paramref name="current"/>, from the GitHub releases JSON.
    /// Prereleases are only offered to people already running one.
    /// </summary>
    internal static ReleaseInfo? PickUpdate(string json, AppVersion current)
    {
        using var doc = JsonDocument.Parse(json);
        ReleaseInfo? best = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!AppVersion.TryParse(tag, out var version)) continue;
            bool prerelease = version.IsPrerelease || (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean());
            if (prerelease && !current.IsPrerelease) continue;
            if (!(version > current) || (best != null && !(version > best.Version))) continue;

            ReleaseAsset? setup = null, portable = null;
            if (release.TryGetProperty("assets", out var assets))
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("QuickClip-", StringComparison.OrdinalIgnoreCase)) continue;
                    var found = new ReleaseAsset(
                        asset.GetProperty("browser_download_url").GetString() ?? "",
                        asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                        asset.TryGetProperty("digest", out var d) && d.GetString() is string digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                            ? digest[7..] : null);
                    if (name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)) setup = found;
                    else if (name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) portable = found;
                }
            best = new ReleaseInfo(version, tag, release.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } title ? title : tag,
                release.GetProperty("html_url").GetString() ?? ReleasesPage, setup, portable);
        }
        return best;
    }

    /// <summary>This copy was installed with the setup (rather than unzipped), so updates run the new setup.</summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>True if this copy can update itself to <paramref name="release"/>.</summary>
    public static bool CanInstall(ReleaseInfo release)
    {
        if (IsInstalledCopy) return release.Setup != null;
        if (release.Portable == null) return false;
        try
        {
            string probe = Path.Combine(AppContext.BaseDirectory, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Downloads the update and returns the action that applies it. Run that action as QuickClip exits: it starts
    /// the new installer silently (installed copies) or a file swap that waits for this process to end (portable
    /// copies); either way QuickClip restarts afterwards.
    /// </summary>
    public static async Task<Action> PrepareInstallAsync(ReleaseInfo release, bool restartInTray, IProgress<double> progress, CancellationToken ct)
    {
        string work = Path.Combine(Path.GetTempPath(), "QuickClip-update", release.Tag);
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);

        if (IsInstalledCopy)
        {
            var setup = release.Setup ?? throw new InvalidOperationException("This release has no installer.");
            string exe = Path.Combine(work, "QuickClip-setup.exe");
            await DownloadAsync(setup, exe, progress, ct);
            var installer = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", $"/LAUNCH={(restartInTray ? "tray" : "window")}" })
                installer.ArgumentList.Add(arg);
            return () => Process.Start(installer);
        }

        var portable = release.Portable ?? throw new InvalidOperationException("This release has no portable zip.");
        string zip = Path.Combine(work, "update.zip");
        await DownloadAsync(portable, zip, progress, ct);
        string files = Path.Combine(work, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, files), ct);
        string? source = Directory.EnumerateFiles(files, "QuickClip.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
        if (source == null) throw new InvalidOperationException("The update doesn't contain QuickClip.exe.");

        string script = Path.Combine(work, "apply.ps1");
        File.WriteAllText(script, ApplyScript);
        string target = AppContext.BaseDirectory.TrimEnd('\\');
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-ProcessId", Environment.ProcessId.ToString(), "-Source", source, "-Target", target, "-Restart", restartInTray ? "tray" : "window" })
            psi.ArgumentList.Add(arg);
        return () => Process.Start(psi);
    }

    private static async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<double> progress, CancellationToken ct)
    {
        using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report(Math.Min(1, done / (double)total));
            }
        }

        if (asset.Sha256 != null)
        {
            await using var file = File.OpenRead(path);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The download is damaged (its checksum doesn't match). Try again.");
        }
    }

    /// <summary>Waits for QuickClip to exit, copies the new files over it, then starts it again.</summary>
    internal const string ApplyScript = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Restart = '')
        $log = Join-Path $env:LOCALAPPDATA 'QuickClip\quickclip.log'
        function Note($text) { Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss.fff} update: {1}" -f (Get-Date), $text) -ErrorAction SilentlyContinue }
        Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
        robocopy $Source $Target /E /R:20 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        if ($LASTEXITCODE -lt 8) { Note "installed into $Target" } else { Note "copying failed (robocopy $LASTEXITCODE)" }
        $exe = Join-Path $Target 'QuickClip.exe'
        if ($Restart -eq 'tray') { Start-Process -FilePath $exe -ArgumentList '--tray' }
        elseif ($Restart -eq 'window') { Start-Process -FilePath $exe }
        """;
}
