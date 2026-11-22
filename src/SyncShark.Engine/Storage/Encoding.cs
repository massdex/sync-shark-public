namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Encoding operations for the storage module.</summary>
public static class Encoding
{
    /// <summary>DecodePair operation on the supplied facts.</summary>
    public static PairConfig DecodePair(string json) => throw new NotImplementedException();
    /// <summary>DecodeNotice operation on the supplied facts.</summary>
    public static Notice DecodeNotice(string json) => throw new NotImplementedException();
    /// <summary>IdFor for the selected Encoding operation.</summary>
    public static object? IdFor(Side side, string? id) => throw new NotImplementedException();
    /// <summary>ReadSynced for the selected Encoding operation.</summary>
    public static SyncedEntry ReadSynced(Microsoft.Data.Sqlite.SqliteDataReader reader, string localKey, int firstColumn) => throw new NotImplementedException();
    /// <summary>Encode a versioned value for storage.</summary>
    public static string Envelope<T>(T value) => throw new NotImplementedException();
}
