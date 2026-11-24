using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
using Codec = Massdex.SyncShark.Engine.Storage.Encoding;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Round trips compact v16 fields and rejects incomplete stored facts.</summary>
public static class EncodingChecks
{
    /// <summary>Exercises canonical and opaque values, malformed JSON, and checked compressed blocks.</summary>
    public static Task RunAsync()
    {
        PersistedEnumNumbersFrozen();
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
        try { Reject(() => Codec.DecodeSubject("""{"Items":[],"Incoming":null,"FirstRunDeletioN":1}""")); } catch (InvalidDataException e) { failures.Add("Numeric header discarded unknown deletion field: " + e.Message); }
        var numericLegacy=Codec.DecodeSubject("""{"Items":[{"Side":0,"Path":"a","Id":null}],"Incoming":null,"FirstRunDeletion":1,"LocalHash":{"Size":1,"ModifiedTicks":2,"Hash":"opaque"},"Folders":null}""");
        if (numericLegacy.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.Item || numericLegacy.Items.Single().Id!=string.Empty) failures.Add("Supported numeric header lost predecessor facts");
        var bound=pair with {Roots=new(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,2,null),new("account","root","home","folder"))};
        var image=JsonSerializer.SerializeToNode(bound)!.AsObject();
        if(Codec.DecodePair(image.ToJsonString()).Roots!=bound.Roots) failures.Add("Valid root binding rejected");
        image["Roots"]!["Local"]!.AsObject().Remove("FileIdHigh");
        try { Codec.DecodePair(image.ToJsonString()); failures.Add("Malformed saved root accepted"); }
        catch(InvalidDataException e) { if(e.Message!=Text.Get(TextKey.StateRootBindingInvalid,(TextArg.Pair,pair.PairId))) failures.Add("Malformed saved root lost the preserve-state diagnostic"); }
        image.Remove("Roots");
        if(Codec.DecodePair(image.ToJsonString()).Roots!=null || Codec.DecodePair(json).Roots!=null) failures.Add("Unbound pair no longer opens");
        if (failures.Count != 0) throw new InvalidDataException(string.Join("; ",failures));
        Reject(() => Codec.DecodeMemberSubject("""{"Version":1,"Value":{"Items":[{"Side":"local","Path":"a","Id":"i"}],"Incoming":null}}"""));
        Reject(() => Codec.DecodeMemberSubject("""{"Version":2,"Value":{"Items":[],"Incoming":null}}"""));
        foreach (var invalid in new[] {
            """{"Version":1,"Value":{"Items":[],"Incoming":null,"FirstRunDeletioN":"Item"}}""",
            """{"Version":1,"Value":{"Items":[{"Side":"Local","side":"Dropbox","Path":"a","Id":"i"}],"Incoming":null}}""",
            """{"Version":1,"Value":{"Items":[],"Incoming":null},"Other":true}""" })
            Reject(() => Codec.DecodeMemberSubject(invalid));
        var priorSubject=Codec.DecodeMemberSubject("""{"Version":1,"Value":{"Items":[],"Incoming":null,"FirstRunDeletion":"Item","LocalHash":{"Size":1,"ModifiedTicks":2,"Hash":"opaque"},"Folders":null}}""");
        if (priorSubject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.Item) throw new InvalidDataException("Lost predecessor deletion evidence");
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

    private static void PersistedEnumNumbersFrozen()
    {
        var engine = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory, "src", "SyncShark.Engine");
        Verify<Side>(["Local", "Dropbox"]);
        Verify<NoticeKind>(["Failure", "Tie", "NameClash", "Info", "Skipped", "DriveChange", "RootIdentity", "PutBack"]);
        Verify<IssueSubject.FirstRunDeletionKind>(["None", "Item", "UnscannedSubtree", "PreserveWithHistoryNotice"]);

        void Verify<T>(string[] wireOrder) where T : struct, Enum
        {
            var type = typeof(T);
            var name = type.Name;
            var file = Path.Combine(engine, (type.DeclaringType?.Name ?? name) + ".cs");
            if (type.DeclaringType == typeof(IssueSubject) && !File.Exists(file)) file = Path.Combine(engine, "Notice.cs");
            var declaration = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes()
                .OfType<EnumDeclarationSyntax>().Single(e => e.Identifier.ValueText == name);
            Check.Require(Enum.GetNames<T>().SequenceEqual(wireOrder) && Enum.GetValues<T>().Select(v => Convert.ToInt32(v)).SequenceEqual(Enumerable.Range(0, wireOrder.Length)) &&
                declaration.Members.All(m => m.EqualsValue is not null), "Persisted enum numbers must remain explicit and unchanged: " + name);
        }
    }

    private static void Reject(Action read)
    {
        try { read(); } catch (InvalidDataException) { return; }
        throw new InvalidDataException("Accepted malformed stored data.");
    }
}
