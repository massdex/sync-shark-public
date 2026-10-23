namespace Massdex.SyncShark;

/// <summary>A launched batch job: complete with entries, or still running with its job id.</summary>
public sealed record DropboxCliBatchData(bool Complete, string? JobId, IReadOnlyList<DropboxCliBatchEntry> Entries)
    : IDropboxCliData;
