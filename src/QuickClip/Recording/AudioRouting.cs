namespace QuickClip.Recording;

/// <summary>
/// Decides which apps each audio track records. Windows records an app together with every app it started (a
/// process tree), and can leave one such tree out of a capture. Chat and Music get only the chat and music apps' own
/// processes: an app one of them started, like a browser opened from a link in Discord, is on Desktop. With a single
/// chat or music app, Desktop is everything but that app. With more than one, Desktop is everything but the first
/// (a chat app, whose voices are least wanted there), with the others' sound subtracted from it sample for sample;
/// only if one of them was started by the first does Desktop instead record every other app with audio, one by one.
/// </summary>
internal static class AudioRouting
{
    /// <summary>
    /// Desktop records everything except <paramref name="Exclude"/> (nothing is left out when that is 0) plus
    /// <paramref name="Desktop"/>; Chat and Music record theirs. Each process comes with the ones it started.
    /// </summary>
    public sealed record Plan(uint Exclude, IReadOnlyList<uint> Desktop, IReadOnlyList<uint> Chat, IReadOnlyList<uint> Music)
    {
        /// <summary>Chat and Music processes whose sound is subtracted from Desktop.</summary>
        public IReadOnlyList<uint> Subtract { get; init; } = [];

        /// <summary>Apps with audio that Desktop can't record (app by app): they started a chat or music app.</summary>
        public IReadOnlyList<uint> Missing { get; init; } = [];
    }

    /// <param name="chat">Root processes of the chat apps recorded on the Chat track.</param>
    /// <param name="music">Root processes of the music apps recorded on the Music track.</param>
    /// <param name="sessionProcesses">Processes with audio; only asked for when it's needed.</param>
    /// <param name="exePath">Where a process's program is, to tell an app's helper processes from apps it started.</param>
    /// <param name="self">QuickClip, whose previews shouldn't be recorded.</param>
    public static Plan Make(IReadOnlyList<ProcessScan.Entry> processes, IReadOnlyList<uint> chat, IReadOnlyList<uint> music,
        Func<IReadOnlySet<uint>> sessionProcesses, Func<uint, string?> exePath, uint self)
    {
        uint[] separated = [.. chat, .. music];
        if (separated.Length == 0) return new(self, [], [], []);

        var byPid = new Dictionary<uint, ProcessScan.Entry>();
        var children = new Dictionary<uint, List<uint>>();
        foreach (var p in processes)
        {
            byPid[p.Pid] = p;
            if (p.ParentPid == 0 || p.ParentPid == p.Pid) continue;
            if (!children.TryGetValue(p.ParentPid, out var list)) children[p.ParentPid] = list = [];
            list.Add(p.Pid);
        }
        // Nearest first. A parent's id can be reused after it exits, so stop at a loop.
        IEnumerable<uint> Ancestors(uint pid)
        {
            var seen = new HashSet<uint> { pid };
            while (byPid.TryGetValue(pid, out var p) && p.ParentPid != 0 && seen.Add(p.ParentPid))
            {
                yield return p.ParentPid;
                pid = p.ParentPid;
            }
        }
        IEnumerable<uint> Descendants(uint pid)
        {
            var seen = new HashSet<uint> { pid };
            var stack = new Stack<uint>([pid]);
            while (stack.Count > 0)
                foreach (uint c in children.GetValueOrDefault(stack.Pop(), []))
                    if (seen.Add(c))
                    {
                        yield return c;
                        stack.Push(c);
                    }
        }
        IReadOnlySet<uint>? sessions = null;
        IReadOnlySet<uint> Sessions() => sessions ??= sessionProcesses();

        // The chat and music apps' own processes, and the first process of each other app they started.
        var own = new HashSet<uint>();
        var ownOf = new Dictionary<uint, HashSet<uint>>();
        var started = new Dictionary<uint, List<uint>>();
        foreach (uint root in separated)
        {
            ownOf[root] = Own(root, byPid, children, exePath, out var others);
            own.UnionWith(ownOf[root]);
            started[root] = others;
        }

        // A track records its apps' processes, each with what it started. In place of one that started another app,
        // its app's processes under it are recorded; it's still recorded itself, bringing the other app along as
        // that can't be helped, when it plays sound or has none of its app's processes under it.
        var foreign = started.Values.SelectMany(s => s).ToHashSet();
        List<uint> Track(IReadOnlyList<uint> roots)
        {
            var result = new List<uint>();
            var seen = new HashSet<uint>();
            foreach (uint root in roots)
            {
                var stack = new Stack<uint>([root]);
                while (stack.Count > 0)
                {
                    uint pid = stack.Pop();
                    if (!seen.Add(pid)) continue;
                    var ownChildren = children.GetValueOrDefault(pid, []).Where(ownOf[root].Contains).ToList();
                    if (!Descendants(pid).Any(foreign.Contains) || ownChildren.Count == 0 || Sessions().Contains(pid)) result.Add(pid);
                    else
                        for (int i = ownChildren.Count - 1; i >= 0; i--) stack.Push(ownChildren[i]);
                }
            }
            return result;
        }
        var chatTrack = Track(chat);
        var musicTrack = Track(music);

        // One app: everything but it, plus the apps it started.
        if (separated.Length == 1)
            return new(separated[0], [.. started[separated[0]].Order()], chatTrack, musicTrack);

        // More: everything but the first, less the others' sound, which works when Windows doesn't leave them out
        // with the first already (as it would one the first started).
        uint first = separated[0];
        var firstTree = Descendants(first).ToHashSet();
        if (!separated.Skip(1).Any(firstTree.Contains))
            return new(first, [.. started[first].Order()], chatTrack, musicTrack) { Subtract = Track([.. separated.Skip(1)]) };

        // App by app: every process with audio but the chat and music apps' own and QuickClip's.
        var candidates = Sessions()
            .Where(pid => !own.Contains(pid) && pid != self && !Ancestors(pid).Contains(self))
            .ToHashSet();
        // Recording an app also records its children, so an app that launched a left-out one (Explorer, a launcher)
        // can't be recorded; its own sounds are lost, but its other children with audio are recorded separately.
        var missing = candidates.Where(separated.SelectMany(Ancestors).Contains).Order().ToList();
        candidates.ExceptWith(separated.Append(self).SelectMany(Ancestors));
        // An app whose parent (or grandparent...) is recorded comes with it. Two apps that are each other's
        // "ancestor" only look related through reused ids, so both are recorded.
        var include = candidates
            .Where(pid => !Ancestors(pid).Any(a => candidates.Contains(a) && !Ancestors(a).Contains(pid)))
            .Order().ToList();
        return new(0, include, chatTrack, musicTrack) { Missing = missing };
    }

    /// <summary>
    /// <paramref name="root"/> and the processes it started that are part of the same app (the same program, one
    /// from its folder, or a web view), and theirs. <paramref name="others"/> gets the first process of each other
    /// app it started, like a browser opened from a link.
    /// </summary>
    private static HashSet<uint> Own(uint root, Dictionary<uint, ProcessScan.Entry> byPid, Dictionary<uint, List<uint>> children,
        Func<uint, string?> exePath, out List<uint> others)
    {
        others = [];
        var own = new HashSet<uint> { root };
        if (!byPid.TryGetValue(root, out var app)) return own;
        string? folder = null;
        bool folderRead = false;
        var queue = new Queue<uint>([root]);
        while (queue.Count > 0)
        {
            foreach (uint c in children.GetValueOrDefault(queue.Dequeue(), []))
            {
                if (own.Contains(c) || others.Contains(c) || !byPid.TryGetValue(c, out var child)) continue;
                bool part = string.Equals(child.ExeName, app.ExeName, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(child.ExeName, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase);
                if (!part)
                {
                    if (!folderRead)
                    {
                        folder = Path.GetDirectoryName(exePath(root));
                        folderRead = true;
                    }
                    string? path = string.IsNullOrEmpty(folder) ? null : exePath(c);
                    part = path != null && path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }
                if (part)
                {
                    own.Add(c);
                    queue.Enqueue(c);
                }
                else others.Add(c);
            }
        }
        return own;
    }

    /// <summary>
    /// Keeps <paramref name="known"/> as the running processes that have had audio. An app's audio session ends when
    /// it closes its sound output, which some apps (browsers) do between sounds; still recording it means its next
    /// sound isn't cut short while that's noticed.
    /// </summary>
    public static void Remember(Dictionary<uint, string> known, IReadOnlyList<ProcessScan.Entry> processes, IReadOnlySet<uint> sessions)
    {
        var running = new Dictionary<uint, string>();
        foreach (var p in processes) running[p.Pid] = p.ExeName;
        foreach (uint pid in known.Keys.ToList())
            if (!running.TryGetValue(pid, out string? name) || name != known[pid]) known.Remove(pid);
        foreach (uint pid in sessions)
            if (running.TryGetValue(pid, out string? name)) known.TryAdd(pid, name);
    }
}
