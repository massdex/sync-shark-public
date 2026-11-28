namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The durable store and the disposable tables attached during Repair.</summary>
public static class Schema
{
    /// <summary>Version installed only after the schema transaction succeeds.</summary>
    public const int Version = 18;
    /// <summary>Connection configuration for the durable store.</summary>
    public static string Pragmas => Sql.Get(SqlKey.SchemaPragmas);
    internal static string KeepIndex => Sql.Get(SqlKey.SchemaKeepIndex);
    /// <summary>Creates the durable tables, indexes and root-change trigger on a fresh image only; never run on an existing store.</summary>
    // The name_count CHECK uses DeletedNamesCodec.DeletedBlockRows, fixed by the v16 disk format.
    public static string Create => Sql.Get(SqlKey.SchemaCreate);
    /// <summary>Creates only the listing and deleted-name buffers on the attached scan store.</summary>
    public static string CreateScan => Sql.Get(SqlKey.SchemaCreateScan);
}
