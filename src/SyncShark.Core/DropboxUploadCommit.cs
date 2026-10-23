namespace Massdex.SyncShark;

/// <summary>A closed session awaiting one conditional commit, with its originating durable and measurement contexts.</summary>
/// <remarks>
/// The commit's provider path is built at batch time from the root's verified metadata path, never here: a
/// configured path can be stale after a rename, while the entry itself only carries the pair-relative path.
/// </remarks>
internal sealed record DropboxUploadCommit(string RelativePath, string? ExpectedRevision, string SessionId,
    string ExpectedContentHash, long Size, DateTimeOffset? ClientModifiedUtc = null)
{
    internal V2.ProviderEffectContext? Context { get; } = V2.ProviderEffectContext.Current.Value;
    internal V2.RunInstrumentation? Instrumentation { get; } = V2.RunInstrumentation.Current;
    internal TaskCompletionSource<PilotUploadResult> Result { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task RecordAsync(string kind) => Context?.RecordAsync(kind, SessionId) ?? Task.CompletedTask;
}
