namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Encoding operations for the storage module.</summary>
public static class Encoding
{
    /// <summary>DecodePair operation on the supplied facts.</summary>
    public static PairConfig DecodePair(string json) => throw new NotImplementedException();
    /// <summary>DecodeNotice operation on the supplied facts.</summary>
    public static Notice DecodeNotice(string json) => throw new NotImplementedException();

    /// <summary>LocalId for the selected Encoding operation.</summary>
    public static object? LocalId(string? value) => throw new NotImplementedException();
    /// <summary>IdFor for the selected Encoding operation.</summary>
    public static object? IdFor(Side side, string? id) => throw new NotImplementedException();
    /// <summary>Hash for the selected Encoding operation.</summary>
    public static object? Hash(string? value) => throw new NotImplementedException();
    /// <summary>ReadHash for the selected Encoding operation.</summary>
    public static string ReadHash(Microsoft.Data.Sqlite.SqliteDataReader reader, int column) => throw new NotImplementedException();
    /// <summary>ReadBool for the selected Encoding operation.</summary>
    public static bool ReadBool(Microsoft.Data.Sqlite.SqliteDataReader reader, int column) => throw new NotImplementedException();
    /// <summary>ReadTime for the selected Encoding operation.</summary>
    public static DateTimeOffset ReadTime(Microsoft.Data.Sqlite.SqliteDataReader reader, int column) => throw new NotImplementedException();
    /// <summary>ReadSynced for the selected Encoding operation.</summary>
    public static SyncedEntry ReadSynced(Microsoft.Data.Sqlite.SqliteDataReader reader, string localKey, int firstColumn) => throw new NotImplementedException();
    /// <summary>ReadEntry for the selected Encoding operation.</summary>
    public static Entry ReadEntry(Microsoft.Data.Sqlite.SqliteDataReader reader, int firstColumn, bool localProjection) => throw new NotImplementedException();
    /// <summary>EncodeDeletedNames for the selected Encoding operation.</summary>
    public static byte[] EncodeDeletedNames(IReadOnlyList<string> keys) => throw new NotImplementedException();
    /// <summary>DecodeDeletedNames for the selected Encoding operation.</summary>
    public static IEnumerable<string> DecodeDeletedNames(Stream input, int count, string lastKey, string? after) => throw new NotImplementedException();
    /// <summary>Encode a versioned value for storage.</summary>
    public static string Envelope<T>(T value) => throw new NotImplementedException();
}
