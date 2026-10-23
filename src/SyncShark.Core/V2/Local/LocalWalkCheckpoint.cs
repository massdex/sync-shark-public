using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark.V2;

// Completed directories are atomic; an abrupt exit can lose only the uncommitted batch.
internal sealed class LocalWalkCheckpoint(ILocalWalkStore store, string root, string rootPath)
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };
    private readonly int _savedCount = store.LocalWalkPageCount(root);
    private readonly List<(int Ordinal, string Payload)> _pending = [];
    private readonly Stopwatch _sinceFlush = Stopwatch.StartNew();
    private int _next, _entries;
    internal LocalWalkPage? Read()
    {
        if (_next >= _savedCount) return null;
        try
        {
            var json = store.LocalWalkPage(root, _next++)
                ?? throw new InvalidDataException("Missing local walk page.");
            var page = JsonSerializer.Deserialize<LocalWalkPage>(json, Json);
            if (page is null || page.Version is not (2 or 3) || page.Directory is null || page.AddedDirectories is null ||
                page.Entries is null || page.Decisions is null || page.Selections is null ||
                page.Entries.Any(e => e is null || string.IsNullOrEmpty(e.RelativePath)) ||
                page.AddedDirectories.Any(d => d is null) || page.Decisions.Any(d => d is null || d.RelativePath is null) ||
                page.Selections.Any(s => s is null || s.Path is null))
                throw new InvalidDataException("Local walk page shape or version changed.");
            if (page.Version == 3 && (page.AddedDirectoryIdentities is null ||
                page.AddedDirectoryIdentities.Length != page.AddedDirectories.Length))
                throw new InvalidDataException("Local walk directory identities do not match the frontier.");
            return page with { Directory = Expand(page.Directory),
                DirectoryIdentity = page.Version == 2 ? null : page.DirectoryIdentity,
                AddedDirectoryIdentities = page.Version == 2 ? new PilotDirectoryIdentity?[page.AddedDirectories.Length] : page.AddedDirectoryIdentities,
                AddedDirectories = page.AddedDirectories.Select(Expand).ToArray(),
                Entries = page.Entries.Select(e => e with { FullPath = Expand(e.RelativePath) }).ToArray() };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            throw new LocalWalkReplayInvalidException(error.Message, error);
        }
    }

    private string Expand(string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Contains(".."))
            throw new InvalidDataException("Local walk page contains a non-relative path.");
        return Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    internal void Save(LocalWalkPage page)
    {
        var compact = page with { Version = 3, Directory = Path.GetRelativePath(rootPath, page.Directory),
            AddedDirectories = page.AddedDirectories.Select(d => Path.GetRelativePath(rootPath, d)).ToArray(),
            Entries = page.Entries.Select(e => e with { FullPath = null! }).ToArray(),
            Selections = page.Selections.Where(s => !s.Include || !s.Traverse).ToArray() };
        _pending.Add((_next++, JsonSerializer.Serialize(compact, Json)));
        _entries += page.Found;
        if (_entries >= 5000 || _pending.Count >= 256 || _sinceFlush.Elapsed >= TimeSpan.FromSeconds(5)) Flush();
    }

    internal void Complete()
    {
        if (_next < _savedCount)
            throw new LocalWalkReplayInvalidException("Local walk checkpoint has pages beyond its frontier.");
        Flush();
    }

    internal void Flush()
    {
        if (_pending.Count == 0) return;
        store.SaveLocalWalkPages(root, _pending);
        _pending.Clear();
        _entries = 0;
        _sinceFlush.Restart();
    }
}
