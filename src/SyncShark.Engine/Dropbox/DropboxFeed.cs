namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DropboxFeed operations for the dropbox module.</summary>
public sealed class DropboxFeed
{
    /// <summary>Read operation on the supplied facts.</summary>
    public Task<ChangePage> ReadAsync(DropboxApi api, PairConfig pair, string? cursor, CancellationToken ct) => throw new NotImplementedException();

    /// <summary>ToChanges for the selected DropboxFeed operation.</summary>
    public static IReadOnlyList<Change> ToChanges(IReadOnlyList<Listed> entries) => throw new NotImplementedException();
}

/// <summary>Tracks repeated continuation cursors.</summary>
public sealed class ContinuationCycle
{
    /// <summary>Check for repetition in this sequence.</summary>
    public bool Repeats(string previous, string next) => throw new NotImplementedException();
}
