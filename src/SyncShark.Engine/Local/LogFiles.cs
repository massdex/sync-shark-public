namespace Massdex.SyncShark.Engine.Local;

/// <summary>The only file I/O for run logs</summary>
public sealed class LogFiles : ILogFiles
{
    /// <summary>Open operation on the supplied facts.</summary>
    public TextWriter Open(string path, int bufferCharacters) => throw new NotImplementedException();
    /// <summary>Resolve operation on the supplied facts.</summary>
    public string? Resolve(string folder, string pairId, string runId) => throw new NotImplementedException();
    /// <summary>Read operation on the supplied facts.</summary>
    public ILogFiles.ReadStatus Read(string path, Action<string,bool> record, int bufferBytes, CancellationToken ct, out bool unfinishedTrailingRecord) => throw new NotImplementedException();
    /// <summary>Prune operation on the supplied facts.</summary>
    public void Prune(string folder, TimeSpan olderThan, DateTimeOffset now) => throw new NotImplementedException();
}
