namespace Massdex.SyncShark.Engine.Local;

/// <summary>The local side, file-system effects and interrupted-removal recovery.</summary>
public sealed class LocalSide : ISide
{
    /// <inheritdoc/>
    public (TimeSpan Offset, TimeSpan Uncertainty)? ClockOffset => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task<DeletionEvidence> DeletedAsync(string path, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Side Side => throw new NotImplementedException();
    /// <summary>ReadAll operation on the supplied facts.</summary>
    public IAsyncEnumerable<Listed> ReadAllAsync(string under, ListOptions options, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadOne operation on the supplied facts.</summary>
    public Task<Entry?> ReadOneAsync(string path, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>OpenRead operation on the supplied facts.</summary>
    public Task<Stream> OpenReadAsync(Entry entry, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ContentHash operation on the supplied facts.</summary>
    public Task<string> ContentHashAsync(Entry entry, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Stage operation on the supplied facts.</summary>
    public Task<IStaged> StageAsync(string path, Stream content, Entry source, Entry? selectedTarget, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Arm the exact note through this pair’s store before recycle; revalidate a selected placeholder on its held handle without hydration. Never publish over a substitute.</summary>
    public Task<RemovalResult> PublishAsync(IStaged staged, Entry? expected, RemovalSelection? selection, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>EnsureFolder operation on the supplied facts.</summary>
    public Task<RemovalResult> EnsureFolderAsync(string path, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Delete operation on the supplied facts.</summary>
    public Task<RemovalResult> DeleteAsync(Entry expected, RemovalSelection? selection, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Move operation on the supplied facts.</summary>
    public Task<RemovalResult> MoveAsync(Entry expected, string newPath, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadById operation on the supplied facts.</summary>
    public Task<(IdentityLocation Location, bool InScope)?> ReadByIdAsync(string id, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>CreateFolderExclusive operation on the supplied facts.</summary>
    public Task<RemovalResult> CreateFolderExclusiveAsync(string path, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadChanges operation on the supplied facts.</summary>
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Recover operation on the supplied facts.</summary>
    public Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadJob operation on the supplied facts.</summary>
    public Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();

    public LocalSide(PairConfig pair, Func<DriveType,PathScope> scopeFromVolume, EngineSettings settings, IStore store) => throw new NotImplementedException();


    /// <inheritdoc/>
    public Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct) => throw new NotImplementedException();
    public Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct) => throw new NotImplementedException();
}
