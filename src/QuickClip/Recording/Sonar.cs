using System.Text.RegularExpressions;

namespace QuickClip.Recording;

/// <summary>
/// SteelSeries Sonar gives each of its channels (Game, Chat, Media and Aux) an output of its own, "SteelSeries Sonar -
/// Gaming (SteelSeries Sonar Virtual Audio Device)" and so on. Apps play on one of them and Sonar mixes them together for
/// the headset. While Windows plays through Sonar, each channel is recorded on a track of its own in place of Desktop.
/// </summary>
internal static partial class Sonar
{
    /// <param name="DeviceId">Windows' id for the channel's output.</param>
    /// <param name="Title">Its track's name, as Sonar's mixer names the channel.</param>
    public sealed record Channel(string DeviceId, string Title);

    private static readonly string[] Order = ["Game", "Chat", "Media", "Aux"];

    /// <summary>
    /// Sonar's channels among the active <paramref name="outputs"/>, in the order of Sonar's mixer, while Windows'
    /// default output is one of them; none otherwise, as apps then play somewhere else (Sonar is off, or set aside).
    /// Sonar's Microphone and Stream outputs aren't channels apps play on: they carry the microphone and the stream mix
    /// (everything again) that Sonar makes.
    /// </summary>
    public static IReadOnlyList<Channel> Channels(IReadOnlyList<CaptureEngine.AudioDevice> outputs)
    {
        var channels = new List<(Channel Channel, bool IsDefault)>();
        foreach (var output in outputs)
            if (ChannelName(output.Name) is { } name)
                channels.Add((new Channel(output.Id, name), output.IsDefault != 0));
        if (!channels.Any(c => c.IsDefault)) return [];
        return channels.Select(c => c.Channel)
            .GroupBy(c => c.Title, StringComparer.OrdinalIgnoreCase).Select(g => g.First()) // two named alike would be confusing
            .OrderBy(c => Array.IndexOf(Order, c.Title) is var i and >= 0 ? i : Order.Length)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The channel an output of Sonar's is for ("Game" for "SteelSeries Sonar - Gaming (SteelSeries Sonar Virtual Audio
    /// Device)"), or null for any other output and Sonar's Microphone and Stream.
    /// </summary>
    public static string? ChannelName(string? outputName)
    {
        if (string.IsNullOrWhiteSpace(outputName) || OutputName().Match(outputName.Trim()) is not { Success: true } m) return null;
        // Windows lets an output be renamed: then its name is what it was renamed to, without Sonar's prefix.
        string name = m.Groups["prefixed"].Success ? m.Groups["prefixed"].Value.Trim() : m.Groups["name"].Value.Trim();
        if (name.Length == 0 || NotAChannel().IsMatch(name)) return null;
        return name.Equals("Gaming", StringComparison.OrdinalIgnoreCase) ? "Game"
            : Order.FirstOrDefault(o => o.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>Whether a microphone is Sonar's (its microphone with Sonar's effects on it).</summary>
    public static bool IsSonarDevice(string? name) => name != null && OutputName().IsMatch(name.Trim());

    [GeneratedRegex(@"^(?:(?:SteelSeries\s+)?Sonar\s*-\s*(?<prefixed>.+?)|(?<name>.+?))\s*\(\s*SteelSeries Sonar Virtual Audio Device\s*\)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex OutputName();

    [GeneratedRegex(@"^(microphone|mic|stream)$", RegexOptions.IgnoreCase)]
    private static partial Regex NotAChannel();
}
