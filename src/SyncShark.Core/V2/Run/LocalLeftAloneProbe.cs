using System.ComponentModel;

namespace Massdex.SyncShark.V2;

/// <summary>
/// A targeted check of one local root, used right before a reviewed first sync is applied. Each level of a path is
/// read from its parent folder's listing only (attributes and reparse tag); nothing is opened or followed. A level is
/// confirmed absent only on a not-found answer; any other failure means the path could not be inspected.
/// </summary>
internal sealed class LocalLeftAloneProbe(string rootPath, bool stubs)
{
    /// <summary>What the local side holds at a path now.</summary>
    internal enum State
    {
        /// <summary>The path or an ancestor is confirmed absent: nothing is there.</summary>
        Absent,
        /// <summary>The path exists and no level is a link or (when stubs count) a stub.</summary>
        Present,
        /// <summary>The path or an ancestor is a left-alone stub (decision 57).</summary>
        Protected,
        /// <summary>The path or an ancestor is a link (decision 59); the walk stops there and never follows it.</summary>
        Linked,
        /// <summary>A level could not be inspected (access denied, I/O error): treat as unsafe.</summary>
        Unknown
    }

    private enum Level { Absent, Plain, Protected, Link, Failed }

    private readonly Dictionary<string, Level> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inspects the path and each ancestor, shortest first.</summary>
    public State Inspect(string relative)
    {
        var current = string.Empty;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? segment : current + "/" + segment;
            if (!_cache.TryGetValue(current, out var level))
                _cache[current] = level = Probe(current);
            switch (level)
            {
                case Level.Absent: return State.Absent;
                case Level.Protected: return State.Protected;
                case Level.Link: return State.Linked;
                case Level.Failed: return State.Unknown;
            }
        }
        return current.Length == 0 ? State.Absent : State.Present;
    }

    private Level Probe(string relative)
    {
        DirectoryEntryInfo listed;
        try
        {
            listed = PilotPath.ListedEntry(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception error) when (IsNotFound(error))
        {
            return Level.Absent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Level.Failed;
        }
        if (PilotPath.IsLink(listed.Attributes, listed.ReparseTag)) return Level.Link;
        return stubs && PilotPath.IsOnlineOnlyStub(listed.Attributes, listed.ReparseTag)
            ? Level.Protected
            : Level.Plain;
    }

    // Only a not-found answer is confirmed absence: the name is not listed, or its parent folder does not exist.
    private static bool IsNotFound(Exception error) =>
        error is FileNotFoundException or DirectoryNotFoundException ||
        error is Win32Exception { NativeErrorCode: 2 or 3 };
}
