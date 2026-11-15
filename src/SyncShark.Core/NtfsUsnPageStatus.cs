namespace Massdex.SyncShark;

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
