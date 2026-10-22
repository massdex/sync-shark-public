namespace Massdex.SyncShark.V2;

/// <summary>The v2 state database was written by an older schema version and needs a fresh database (decision 51).</summary>
/// <remarks>Not damage: v2 databases have no upgrade path, so the old file is set aside and a fresh one created.</remarks>
public sealed class StateNeedsFreshDatabaseException : Exception
{
    /// <summary>Creates the exception for a database stamped with <paramref name="foundVersion"/>.</summary>
    public StateNeedsFreshDatabaseException(long foundVersion, long requiredVersion)
        : base($"v2 state has schema version {foundVersion}; this build needs version {requiredVersion}. " +
            "It needs a fresh database (v2 databases have no upgrade path).")
    {
        FoundVersion = foundVersion;
        RequiredVersion = requiredVersion;
    }

    /// <summary>The schema version stamped in the database.</summary>
    public long FoundVersion { get; }

    /// <summary>The schema version this build requires.</summary>
    public long RequiredVersion { get; }
}
