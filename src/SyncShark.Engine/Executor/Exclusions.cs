namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Exclusions operations for the executor module.</summary>
public sealed class Exclusions
{
    /// <summary>For folder copies and kept-side conflict labels, await the population callback from Run; no second child walk.</summary>
    public Task<ExclusionOutcome> ApplyAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Exclusion exclusion, ISide local, ISide dropbox, IStore store, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>PreviewAsync for the selected Exclusions operation.</summary>
    public Task<ExcludePreview> PreviewAsync(PairConfig pair, Exclusion exclusion, ISide local, ISide dropbox, IStore store, PathScope paths, CancellationToken ct) => throw new NotImplementedException();
}

/// <summary>Expected effects of applying the exclusion.</summary>
public sealed record ExcludePreview(Side Side, string Path, int CopyFiles, long CopyBytes, int RemoveFiles,
    long RemoveBytes, TextKey RecoveryPlace, bool BlockedByProtected);
/// <summary>Whether the exclusion settled.</summary>
public enum ExclusionOutcome { Applied, Pending }
