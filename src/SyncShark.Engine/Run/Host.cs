namespace Massdex.SyncShark.Engine.Run;

/// <summary>Host operations for the run module.</summary>
public sealed class Host
{
    /// <summary>Completes after one finite pass over work available at start, including one check per known token; does not drain later arrivals. Returns the participating volume roots and types observed by their local adapters, once per volume, for startup trim.</summary>
    public Task<IReadOnlyList<(string VolumeRoot, DriveType Type)>> StartAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Stop operation on the supplied facts.</summary>
    public Task StopAsync(CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Pause operation on the supplied facts.</summary>
    public Task PauseAsync(string? pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Resume operation on the supplied facts.</summary>
    public Task ResumeAsync(string? pairId, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Whether this pair may start work under the global and per-pair pause settings.</summary>
    public bool IsPaused(string pairId) => throw new NotImplementedException();

    /// <summary>The store factory opens an existing image without creating one unless first pairs add explicitly requests creation. Host supplies its current pair set to adapter construction. Intake, planning and the PC side share the pair scope built from that set and the same observed PC drive type.</summary>
    public Host(Func<bool,CancellationToken,Task<IStore?>> openStore, Func<PairConfig,IReadOnlyList<PairConfig>,(ISide Local,ISide Dropbox)> sides, EngineSettings settings, ILogFiles logs, TimeProvider clock, Func<CancellationToken,Task<string>> connect, Func<string,CancellationToken,Task> disconnect) => throw new NotImplementedException();

    public IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task AddPairAsync(PairConfig pair,CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> RemovePairAsync(string pairId,CancellationToken ct) => throw new NotImplementedException();
    public Task BindAsync(string pairId,CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Preview includes the recovery place for the selected removal side, using its existing observation: managed root-bin path, Windows bin or Dropbox history. No removal means no recovery place.</summary>
    public Task<(bool Applied,long CopyFiles,long CopyBytes,long RemoveFiles,long RemoveBytes,string? RecoveryPlace,TextKey? Cause)> SetExclusionAsync(string pairId,Exclusion exclusion,bool include,bool confirmed,CancellationToken ct) => throw new NotImplementedException();
    public Task SetIgnoresAsync(string pairId,IReadOnlyList<string> rules,CancellationToken ct) => throw new NotImplementedException();
    public Task RepairAsync(string pairId,CancellationToken ct) => throw new NotImplementedException();
    public Task<string> StatusAsync(string? pairId,CancellationToken ct) => throw new NotImplementedException();
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId,CancellationToken ct) => throw new NotImplementedException();
    public Task ResolveAsync(string pairId,long id,string reviewed,ResolutionChoice choice,string? name,CancellationToken ct) => throw new NotImplementedException();
    public Task<string> ConnectAsync(CancellationToken ct) => throw new NotImplementedException();
    public Task DisconnectAsync(string connectionId,CancellationToken ct) => throw new NotImplementedException();
}
