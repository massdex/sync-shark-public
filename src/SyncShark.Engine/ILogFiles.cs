namespace Massdex.SyncShark.Engine;

/// <summary>Log file operations available both while the host is running and from a detached reader.</summary>
public interface ILogFiles
{
    /// <summary>Opens a writer that remains readable while active.</summary>
    TextWriter Open(string path, int bufferCharacters);
    /// <summary>Finds the requested pair/run log without creating a file.</summary>
    string? Resolve(string folder, string pairId, string runId);
    /// <summary>The callback’s bool indicates a well-formed record; malformed records are still delivered flagged. Streams complete records and reports an unterminated final record separately.</summary>
    ReadStatus Read(string path, Action<string, bool> record, int bufferBytes, CancellationToken ct,
        out bool unfinishedTrailingRecord);
    /// <summary>Removes log files older than the configured retention.</summary>
    void Prune(string folder, TimeSpan olderThan, DateTimeOffset now);
    /// <summary>Distinguishes an absent log from one that cannot be read.</summary>
    public enum ReadStatus { Found, NotFound, Unreadable }
}
