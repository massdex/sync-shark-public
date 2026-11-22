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
        var header = """{"Items":[{"Path":"a","Id":"i"}],"Incoming":null}""";
        var member = """{"Version":1,"Value":{"Items":[{"Side":"Local","Side":"Dropbox","Path":"a","Id":"i"}],"Incoming":null}}""";
        var failures = new List<string>();
        try { Reject(() => Codec.DecodeSubject(header)); } catch (InvalidDataException e) { failures.Add("#1 header: " + e.Message); }
        try { Reject(() => Codec.DecodeMemberSubject(member)); } catch (InvalidDataException e) { failures.Add("#1 envelope: " + e.Message); }
        if (typeof(Codec).GetField("DeletedBlockRows")?.GetRawConstantValue() is not 5000) failures.Add("#7 shared v16 block limit missing");
        if (failures.Count != 0) throw new InvalidDataException(string.Join("; ",failures));
        Reject(() => Codec.DecodeMemberSubject("""{"Version":1,"Value":{"Items":[{"Side":"local","Path":"a","Id":"i"}],"Incoming":null}}"""));
        Reject(() => Codec.DecodeMemberSubject("""{"Version":2,"Value":{"Items":[],"Incoming":null}}"""));
        var legacy=Codec.DecodeMemberSubject("""{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"a","Id":null}],"Incoming":null}}""");
        if (legacy.Items.Single().Id != string.Empty || legacy.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None) throw new InvalidDataException("Lost legacy unknown identity or optional deletion fact.");
        // Construct the fixed v16 wire fixture independently of the candidate encoder: zlib and BinaryWriter length-prefixed UTF-8 strings.
        using var fixture = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(fixture,System.IO.Compression.CompressionLevel.NoCompression,true))
        using (var v16 = new BinaryWriter(z)) { v16.Write("A"); v16.Write("B/FILE"); }
        fixture.Position=0;
        if (!Codec.DecodeDeletedNames(fixture,2,"B/FILE",null).SequenceEqual(new[]{"A","B/FILE"})) throw new InvalidDataException("Unreadable pinned v16 block.");
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
