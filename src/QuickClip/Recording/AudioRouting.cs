namespace QuickClip.Recording;

/// <summary>
/// Decides what the Desktop track records. Windows can leave one app (a process and its children) out of a capture,
/// so with a single chat or music app running, Desktop is everything but that app. With more than one, Desktop
/// instead records every other app that has audio, one by one.
/// </summary>
internal static class AudioRouting
{
    /// <summary>Everything except <paramref name="Exclude"/>'s apps, or when that is 0, only <paramref name="Include"/>'s.</summary>
    public sealed record Desktop(uint Exclude, IReadOnlyList<uint> Include);

    /// <param name="separated">Root processes of the apps recorded on their own tracks (chat, music).</param>
    /// <param name="sessionProcesses">Processes with audio; only asked for when Desktop is recorded app by app.</param>
    /// <param name="self">QuickClip, whose previews shouldn't be recorded.</param>
    public static Desktop Plan(IReadOnlyList<ProcessScan.Entry> processes, IReadOnlyList<uint> separated,
        Func<IReadOnlySet<uint>> sessionProcesses, uint self)
    {
        if (separated.Count == 0) return new(self, []);
        if (separated.Count == 1) return new(separated[0], []);

        var parents = new Dictionary<uint, uint>();
        foreach (var p in processes) parents[p.Pid] = p.ParentPid;
        // Nearest first. A parent's id can be reused after it exits, so stop at a loop.
        IEnumerable<uint> Ancestors(uint pid)
        {
            var seen = new HashSet<uint> { pid };
            while (parents.TryGetValue(pid, out uint parent) && parent != 0 && seen.Add(parent))
            {
                yield return parent;
                pid = parent;
            }
        }

        var leftOut = new HashSet<uint>(separated) { self };
        var candidates = sessionProcesses()
            .Where(pid => !leftOut.Contains(pid) && !Ancestors(pid).Any(leftOut.Contains))
            .ToHashSet();
        // Recording an app also records its children, so an app that launched a left-out one (Explorer, a launcher)
        // can't be recorded; its own sounds are lost, but its other children with audio are recorded separately.
        candidates.ExceptWith(leftOut.SelectMany(Ancestors));
        // An app whose parent (or grandparent...) is recorded comes with it. Two apps that are each other's
        // "ancestor" only look related through reused ids, so both are recorded.
        var include = candidates
            .Where(pid => !Ancestors(pid).Any(a => candidates.Contains(a) && !Ancestors(a).Contains(pid)))
            .Order().ToList();
        return new(0, include);
    }
}
