namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The Dropbox side</summary>
public sealed class DropboxSide : ISide
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
    public async Task<IStaged> StageAsync(string path, Stream input, Entry source, Entry? selectedTarget, CancellationToken ct) =>
        await stage(api,path,input,source,null,ct).ConfigureAwait(false);
    /// <summary>Publish operation on the supplied facts.</summary>
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
    /// <inheritdoc/>
    public Task SettleJournalAsync(JournalPosition position, CancellationToken ct) => throw new NotSupportedException();
    /// <inheritdoc/>
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Recover operation on the supplied facts.</summary>
    public Task<Recovery?> RecoverAsync(IdentityLocation actual, Entry version, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>ReadJob operation on the supplied facts.</summary>
    public Task<JobReply> ReadJobAsync(AcceptedJob job, CancellationToken ct) => throw new NotImplementedException();

    private readonly DropboxApi api;
    private readonly Func<DropboxApi,string,Stream,Entry,Entry?,CancellationToken,Task<DropboxStaged>> stage;
    public DropboxSide(PairConfig pair, Func<PathScope> scope, DropboxApi api, Dispatcher dispatcher) : this(pair,scope,api,dispatcher,new UploadContent().StageAsync) { }
    public DropboxSide(PairConfig pair, Func<PathScope> scope, DropboxApi api, Dispatcher dispatcher,
        Func<DropboxApi,string,Stream,Entry,Entry?,CancellationToken,Task<DropboxStaged>> stage) => (this.api,this.stage)=(api,stage);



    /// <inheritdoc/>
    public Task<IStaged> ReopenStageAsync(RemovalSelection armedVictim, StageProof expected, CancellationToken ct) => throw new NotImplementedException();
    public Task<(DirectoryIdentity? Local, DropboxRootIdentity? Dropbox, DriveEvidence? Drive, DriveType? LocalDriveType)> RootAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task<DeletionEvidence> DeletedAsync(string path, bool folder, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task<ChangePage> ReadChangesAsync(string? cursor, JournalPosition? position, bool catchUp, CancellationToken ct) => throw new NotImplementedException();
    /// <inheritdoc/>
    public Task<(string? Cursor,JournalPosition? Position)> BoundaryAsync(CancellationToken ct) => throw new NotImplementedException();
}
