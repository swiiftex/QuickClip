using System.Text.RegularExpressions;

namespace QuickClip.Recording;

/// <summary>Works out which microphones to record and what their tracks are called.</summary>
internal static partial class Microphones
{
    /// <param name="DeviceId">Windows' id for the microphone, or "" for the default one.</param>
    /// <param name="Title">Its track's name when each microphone has a track of its own.</param>
    public sealed record Input(string DeviceId, string Title);

    /// <summary>
    /// The chosen microphones, each recorded once (choosing the default microphone and the same one by name doesn't
    /// record it twice), named after the device. "Microphone (Yeti Stereo Microphone)" records as "Mic (Yeti Stereo Microphone)".
    /// </summary>
    public static IReadOnlyList<Input> Plan(IReadOnlyList<string> deviceIds, IReadOnlyList<CaptureEngine.AudioDevice> devices)
    {
        var defaultDevice = devices.FirstOrDefault(d => d.IsDefault != 0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inputs = new List<Input>();
        foreach (string id in deviceIds)
        {
            // The default microphone is followed as it changes, so for spotting repeats it's whichever is default now.
            string device = id == "" && defaultDevice.Id != null ? defaultDevice.Id : id;
            if (!seen.Add(device)) continue;
            string? name = devices.FirstOrDefault(d => string.Equals(d.Id, device, StringComparison.OrdinalIgnoreCase)).Name;
            inputs.Add(new Input(id, Title(name, inputs.Count + 1, inputs.Select(i => i.Title))));
        }
        return inputs;
    }

    /// <summary>How many tracks the microphones are recorded on, one shared Mic track or one each.</summary>
    public static int TrackCount(AppSettings s) =>
        !s.RecordMic || s.MicDeviceIds.Count == 0 ? 0 : s.SeparateMicTracks ? s.MicDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() : 1;

    private static string Title(string? name, int number, IEnumerable<string> taken)
    {
        if (string.IsNullOrWhiteSpace(name)) return $"Mic {number}"; // not connected right now
        // Windows names inputs "input (device)". Usually the input is just "Microphone" or "Line In" and the device
        // tells them apart, but a device with several inputs ("Wave Mic 3 (Elgato Virtual Audio)") names each one.
        string label = name.Trim();
        if (NameParts().Match(label) is { Success: true } m)
            label = GenericInput().IsMatch(m.Groups[1].Value) ? m.Groups[2].Value : m.Groups[1].Value;
        string title = $"Mic ({label})";
        return taken.Contains(title) ? $"Mic {number} ({label})" : title;
    }

    [GeneratedRegex(@"^(.+?)\s*\(([^()]+)\)$")]
    private static partial Regex NameParts();

    [GeneratedRegex(@"^((headset|external|internal|front|rear|digital|stereo|array|microphones?|mic|line|in|input)\s*)+$", RegexOptions.IgnoreCase)]
    private static partial Regex GenericInput();
}
