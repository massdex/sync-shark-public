namespace Massdex.SyncShark;

public static class SyncModePolicy
{
    public static bool Allows(SyncMode direction, SyncAuthorityOperationKind kind)
    {
        if (!Enum.IsDefined(direction)) throw new InvalidDataException("Unknown sync direction.");
        return direction switch
        {
            SyncMode.TwoWay => true,
            SyncMode.UploadOnly => kind is SyncAuthorityOperationKind.CreateRemote or
                SyncAuthorityOperationKind.UpdateRemote or SyncAuthorityOperationKind.DeleteRemote or SyncAuthorityOperationKind.MoveRemote,
            SyncMode.DownloadOnly => kind is SyncAuthorityOperationKind.CreateLocal or
                SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.MoveLocal,
            _ => false
        };
    }
    public static string Label(SyncMode direction) => direction switch
    {
        SyncMode.TwoWay => "Two-Way", SyncMode.UploadOnly => "Upload Only",
        SyncMode.DownloadOnly => "Download Only", _ => throw new InvalidDataException("Unknown sync direction.")
    };
}

public enum SyncDirectoryStorage { CloudOnly, AlwaysAvailable }
public sealed record SyncDirectoryStorageRule(string FolderPath, SyncDirectoryStorage Storage,
    ulong? LocalDirectoryId = null, string? RemoteDirectoryId = null);
public sealed record SyncStorageDirectoryIdentity(string RelativePath, ulong? LocalId, string? RemoteId);
public sealed record SyncNativeDirectoryChoice(string RelativePath, ulong LocalId, string? RemoteId, bool KeepOffline);
public sealed record SyncFileStorageRule(string RelativePath, SyncDirectoryStorage Storage, ulong LocalFileId, string? RemoteFileId);

// Participation and storage are deliberately independent: an online-only directory still synchronizes.
public static class SyncDirectoryStoragePolicy
{
    public static SyncPair AdoptExplorerChoices(SyncPair pair, IReadOnlyList<SyncNativeDirectoryChoice> choices)
    {
        if (!pair.FilesOnDemandEnabled) return pair;
        var rules = Normalize(pair.DirectoryStorageRules).ToList();
        var changed = new List<string>();
        foreach (var choice in choices.OrderBy(item => item.RelativePath.Count(character => character == '/')))
        {
            var path = PilotPath.NormalizeRelative(choice.RelativePath);
            if ((pair.DirectoryStorageApplyPaths ?? []).Any(pending => Contains(pending, path))) continue;
            var storage = choice.KeepOffline ? SyncDirectoryStorage.AlwaysAvailable : SyncDirectoryStorage.CloudOnly;
            if (Resolve(pair with { DirectoryStorageRules = rules }, path) == storage) continue;
            rules.RemoveAll(rule => string.Equals(rule.FolderPath, path, StringComparison.OrdinalIgnoreCase));
            rules.Add(new(path, storage, choice.LocalId, choice.RemoteId)); changed.Add(path);
        }
        return changed.Count == 0 ? pair : pair with { DirectoryStorageRules = Normalize(rules),
            DirectoryStorageApplyPaths = (pair.DirectoryStorageApplyPaths ?? []).Concat(changed).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            PolicyRevision = checked(pair.PolicyRevision + 1) };
    }

    public static IReadOnlyList<SyncDirectoryStorageRule> Normalize(IReadOnlyList<SyncDirectoryStorageRule>? rules)
    {
        var normalized = new Dictionary<string, SyncDirectoryStorageRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules ?? [])
        {
            if (!Enum.IsDefined(rule.Storage)) throw new InvalidDataException("Unknown directory storage policy.");
            var path = PilotPath.NormalizeRelative(rule.FolderPath);
            normalized[path] = rule with { FolderPath = path };
        }
        return normalized.Values.OrderBy(rule => rule.FolderPath.Count(c => c == '/'))
            .ThenBy(rule => rule.FolderPath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static SyncDirectoryStorage Resolve(SyncPair pair, string relativePath)
        => Compile(pair)(relativePath);

    public static SyncDirectoryStorage Default(SyncPair pair) => pair.DefaultStorage ??
        (pair.FilesOnDemandEnabled ? SyncDirectoryStorage.CloudOnly : SyncDirectoryStorage.AlwaysAvailable);

    public static Func<string, SyncDirectoryStorage> Compile(SyncPair pair)
    {
        var rules = Normalize(pair.DirectoryStorageRules).OrderByDescending(rule => rule.FolderPath.Length).ToArray();
        var files = NormalizeFiles(pair.FileStorageRules);
        var byLocal = files.Where(rule => rule.LocalFileId != 0).ToDictionary(rule => rule.LocalFileId);
        var byRemote = files.Where(rule => rule.RemoteFileId is not null).ToDictionary(rule => rule.RemoteFileId!, StringComparer.Ordinal);
        var filePaths = files.Select(rule => rule.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallback = Default(pair);
        return relativePath =>
        {
            var path = PilotPath.NormalizeRelative(relativePath);
            if (files.Count > 0)
            {
                // Pinning belongs to the actual file, not to a subsequently reused file name.
                // Attribute-only handles do not download online-only contents.
                try
                {
                    var identity = WindowsCloudFilesProvider.ReadStorageFileIdentity(PilotPath.ResolveContainedPath(pair.LocalRoot, path), pair.Id, pair.Provider);
                    if (identity.LocalId != 0 && byLocal.TryGetValue(identity.LocalId, out var local)) return local.Storage;
                    if (identity.RemoteId is not null && byRemote.TryGetValue(identity.RemoteId, out var remote)) return remote.Storage;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    // An unrelated new download must still inherit its folder policy. A previously
                    // pinned path whose identity cannot yet be rebound is retained locally instead.
                    if (filePaths.Contains(path)) return SyncDirectoryStorage.AlwaysAvailable;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { return SyncDirectoryStorage.AlwaysAvailable; }
            }
            return rules.FirstOrDefault(rule => Contains(rule.FolderPath, path))?.Storage ?? fallback;
        };
    }

    public static IReadOnlyList<SyncFileStorageRule> NormalizeFiles(IReadOnlyList<SyncFileStorageRule>? rules)
    {
        if (rules is null) return [];
        if (rules.Count > 10_000) throw new InvalidDataException("Use a folder preference for large selections.");
        var result = new List<SyncFileStorageRule>(rules.Count);
        var local = new HashSet<ulong>(); var remote = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in rules)
        {
            if (!Enum.IsDefined(rule.Storage) || rule.LocalFileId == 0 && rule.RemoteFileId is null ||
                rule.RemoteFileId is not null && (string.IsNullOrWhiteSpace(rule.RemoteFileId) ||
                    rule.RemoteFileId.Length > 1024 || rule.RemoteFileId.Any(char.IsControl)))
                throw new InvalidDataException("A file storage preference has no valid file identity.");
            var path = PilotPath.NormalizeRelative(rule.RelativePath);
            if (!paths.Add(path) || rule.LocalFileId != 0 && !local.Add(rule.LocalFileId) ||
                rule.RemoteFileId is not null && !remote.Add(rule.RemoteFileId))
                throw new InvalidDataException("Two file storage preferences refer to the same file.");
            result.Add(rule with { RelativePath = path });
        }
        return result.OrderBy(rule => rule.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool Contains(string folder, string path) =>
        string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
}
