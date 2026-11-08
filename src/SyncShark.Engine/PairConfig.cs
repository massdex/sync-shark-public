namespace Massdex.SyncShark.Engine;

/// <summary>One local-Dropbox pair. Drive is the retained local drive evidence used to detect a drive change or rollback. Roots is the optional saved binding to both configured roots; IgnoredFolderKeepRules are the optional canonical rules for ignored contents kept in a deleted folder.</summary>
public sealed record PairConfig(string PairId, string Name, string LocalRoot, string DropboxRoot,
    string ConnectionId, IReadOnlyList<string> IgnoreRules, IReadOnlyList<Exclusion> Exclusions,
    FirstRunPolicy FirstRun, bool Enabled, DriveEvidence? Drive, RootBinding? Roots = null,
    IReadOnlyList<string>? IgnoredFolderKeepRules = null);
