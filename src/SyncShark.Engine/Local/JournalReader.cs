namespace Massdex.SyncShark.Engine.Local;

/// <summary>JournalReader operations for the local module.</summary>
public sealed class JournalReader
{
    /// <summary>Attach membership before any volume read, including paused pairs; a stopped or paused member still holds its oldest unsettled input.</summary>
    public void Attach(PairConfig pair) => throw new NotImplementedException();
    /// <summary>Save only the common contiguous volume position after this pair’s settlement, without reading another page.</summary>
    public Task SettleAsync(PairConfig pair, JournalPosition position, IStore store, CancellationToken ct) => throw new NotImplementedException();
    /// <summary>Attach for the selected JournalReader operation.</summary>
    public void Attach(PairConfig pair,IStore store,Func<PathScope> scope, Func<ulong,bool,CancellationToken,(string? Path,IReadOnlyList<string>? Links)> locate,Func<string,Entry?> entry) => throw new NotImplementedException();
    /// <summary>Membership for the selected JournalReader operation.</summary>
    public Task Membership(PairConfig pair,bool paused,bool removed=false) => throw new NotImplementedException();
    /// <summary>ReadPageAsync for the selected JournalReader operation.</summary>
    public Task<ChangePage> ReadPageAsync(PairConfig pair,JournalPosition? position,JournalPosition? end,EngineSettings settings,CancellationToken ct) => throw new NotImplementedException();
}

/// <summary>The journal observed a change to the bound root itself.</summary>
public sealed class JournalRootChangedException(string root) : ChangedUnderneathException(root);

/// <summary>Classifies the result of one native USN journal page read.</summary>
public enum NtfsUsnPageStatus
{
    /// <summary>The requested range was consumed to its end without consuming a record that crosses its boundary.</summary>
    Complete,

    /// <summary>Records remain beyond this page; the caller continues from the consumed next USN.</summary>
    More,

    /// <summary>Journal state or the native result could not be established; the page's other outputs are not evidence.</summary>
    Unavailable,

    /// <summary>The requested start is below the journal's lowest valid USN, so its records were purged.</summary>
    HistoryLost,

    /// <summary>The volume's journal identity no longer matches the caller's expected journal ID.</summary>
    JournalChanged,

    /// <summary>A record uses a layout this decoder does not support.</summary>
    UnsupportedRecord,

    /// <summary>A record is malformed or truncated.</summary>
    InvalidRecord,

    /// <summary>Native continuation did not advance past the requested start.</summary>
    NoProgress
}
