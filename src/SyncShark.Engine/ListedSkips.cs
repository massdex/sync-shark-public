namespace Massdex.SyncShark.Engine;

/// <summary>The skipped observations acquired by the two listings, without source or execution authority.</summary>
public sealed record ListedSkips(
    IReadOnlyList<(string Path, TextKey Cause, IssueSubject Subject)> Local,
    IReadOnlyList<(string Path, TextKey Cause, IssueSubject Subject)> Dropbox,
    IReadOnlyList<(Side Side, string Key)> LocalFolders,
    IReadOnlyList<(Side Side, string Key)> DropboxFolders);
