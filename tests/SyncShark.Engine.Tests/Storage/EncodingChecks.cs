using System.Text.Json;
using Microsoft.Data.Sqlite;
using Codec = Massdex.SyncShark.Engine.Storage.Encoding;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Round trips compact v16 fields and rejects incomplete stored facts.</summary>
public static class EncodingChecks
{
    /// <summary>Exercises canonical and opaque values, malformed JSON, and checked compressed blocks.</summary>
    public static Task RunAsync()
    {
        if (Codec.IdFor(Side.Local,"42") is not 42L || Codec.LocalId("0042") is not "0042" ||
            Codec.IdFor(Side.Dropbox,"42") is not "42" || Codec.Hash(new string('a',64)) is not byte[] { Length: 32 } ||
            Codec.Hash(new string('A',64)) is not string || Codec.Hash("") is not byte[] { Length: 0 })
            throw new InvalidDataException("Compact and opaque values lost their representation.");
        var pair = new PairConfig("pair", "Example", "left", "/right", "connection", [], [], FirstRunPolicy.NewestWins, true, null);
        var json = JsonSerializer.Serialize(pair);
        if (Codec.DecodePair(json).PairId != pair.PairId) throw new InvalidDataException("Pair did not round trip.");
        Reject(() => Codec.DecodePair(json.Replace("\"Enabled\":true,", "", StringComparison.Ordinal)));
        Reject(() => Codec.DecodePair(json.Replace("\"Enabled\":true", "\"Enabled\":true,\"Enabled\":false", StringComparison.Ordinal)));
        Reject(() => Codec.DecodeNotice("{}"));
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var sql = db.CreateCommand();
        sql.CommandText = "SELECT 'left',NULL,'left',0,42,5,9,'remote',x'ab',-1,7";
        using (var reader = sql.ExecuteReader())
        {
            reader.Read();
            var row = Codec.ReadSynced(reader,"LEFT",0);
            if (row.LocalId != "42" || row.DropboxKey != "LEFT" || row.DropboxContentHash != "ab" ||
                row.LocalPublished != new JournalPosition(ulong.MaxValue,7)) throw new InvalidDataException("Compact projection mismatch.");
        }
        sql.CommandText = "SELECT 'left',NULL,'left',0,42,5,9,'remote',x'ab',NULL,7";
        using (var reader = sql.ExecuteReader())
        {
            reader.Read();
            Reject(() => Codec.ReadSynced(reader,"LEFT",0));
        }
        string[] names = ["A", "B/FILE"];
        var block = Codec.EncodeDeletedNames(names);
        if (!Codec.DecodeDeletedNames(new MemoryStream(block), names.Length, names[^1], null).SequenceEqual(names))
            throw new InvalidDataException("Deleted names changed in the stored block.");
        block[^1] ^= 1;
        Reject(() => Codec.DecodeDeletedNames(new MemoryStream(block), names.Length, names[^1], null).ToArray());
        return Task.CompletedTask;
    }

    private static void Reject(Action read)
    {
        try { read(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("Accepted malformed stored data.");
    }
}
