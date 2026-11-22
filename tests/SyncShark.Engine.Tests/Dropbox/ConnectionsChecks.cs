using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Connections: the 0.3.95 append list is read, the store-move copy is create-only and
/// byte-identical, and the required connections are enforced.</summary>
public static class ConnectionsChecks
{
    public static Task RunAsync()
    {
        var first = Id("first");
        var second = Id("second");
        var third = Id("third");
        var scratch = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var connections = new Connections();

            var source = Root(scratch, "source");
            var sourceFile = ListFile(source);
            Directory.CreateDirectory(Path.GetDirectoryName(sourceFile)!);
            File.WriteAllText(sourceFile, OldSet(first, second));
            var read = connections.Read(source);
            Check.Require(read.SequenceEqual(Sorted(first, second)), "0.3.95 connection list ids were not read");

            var product = Root(scratch, "product");
            var targetFile = ListFile(product);
            var cleanup = connections.Verify(source, product, new[] { first });
            Check.Require(cleanup is not null && File.Exists(targetFile), "A present source list did not create the target list");
            Check.Require(File.ReadAllBytes(targetFile).AsSpan().SequenceEqual(File.ReadAllBytes(sourceFile)), "The copied list differs from the source");
            cleanup!();
            Check.Require(!File.Exists(targetFile), "Cleanup kept the list this attempt created");

            connections.Verify(source, product, new[] { first });
            Check.Require(connections.Verify(source, product, new[] { first, second }) is null, "A byte-identical target was refused or claimed cleanup");
            var kept = File.ReadAllBytes(targetFile);
            var distinct = Root(scratch, "distinct");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(distinct))!);
            File.WriteAllText(ListFile(distinct), OldSet(third));
            var refused = false;
            try { connections.Verify(distinct, product, new[] { third }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && File.ReadAllBytes(targetFile).AsSpan().SequenceEqual(kept), "Distinct lists were accepted or the target changed");

            var missing = Root(scratch, "missing");
            refused = false;
            try { connections.Verify(source, missing, new[] { third }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && !File.Exists(ListFile(missing)) && NoStage(missing), "A missing required connection was accepted or left files");

            var empty = Root(scratch, "empty");
            Check.Require(connections.Verify(Root(scratch, "none"), empty, Array.Empty<string>()) is null && !File.Exists(ListFile(empty)), "Both lists absent with no required connections created a list");
            refused = false;
            try { connections.Verify(Root(scratch, "none"), Root(scratch, "required"), new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "Both lists absent with a required connection was accepted");

            File.WriteAllText(sourceFile, OldSet(first, second) + OldDelete());
            Check.Require(connections.Read(source).Count == 0, "A deleted 0.3.95 binding was still read");
            File.WriteAllText(sourceFile, "{\"not\":\"a record\"}\n");
            refused = false;
            try { connections.Read(source); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A malformed list was accepted");

            connections.Save(empty, new[] { second, first, second });
            Check.Require(connections.Read(empty).SequenceEqual(Sorted(first, second)), "Saved ids were not read back distinct and ordered");
            connections.Save(empty, new[] { third });
            Check.Require(connections.Read(empty).SequenceEqual(new[] { third }), "A saved list was not replaced");
            var before = File.ReadAllBytes(ListFile(empty));
            refused = false;
            try { connections.Save(empty, new[] { "not-a-connection-id" }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && File.ReadAllBytes(ListFile(empty)).AsSpan().SequenceEqual(before), "An invalid id replaced the list");

            var accountId = "acct-fixture";
            var created = Connections.CreateConnectionId(accountId);
            var expected = "connection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dropbox\n" + accountId))).ToLowerInvariant()[..24];
            Check.Require(created == expected && Connections.CreateConnectionId(accountId) == created, "Connection id is not the stable account hash");
            Check.Require(Connections.CreateConnectionId("acct-other") != created, "Different accounts share a connection id");
        }
        finally { Directory.Delete(scratch, true); }
        return Task.CompletedTask;
    }

    private static string Root(string scratch, string name) => Path.Combine(scratch, name);
    private static string ListFile(string root) => Path.Combine(root, "state", "dropbox-connection.jsonl");
    private static bool NoStage(string root) =>
        !Directory.Exists(Path.Combine(root, "state")) ||
        !Directory.EnumerateFiles(Path.Combine(root, "state"), "*.new").Any();
    private static string[] Sorted(params string[] ids) => ids.Order(StringComparer.Ordinal).ToArray();

    private static string Id(string seed) =>
        "connection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];

    private static string OldSet(params string[] ids)
    {
        var binding = new Dictionary<string, object?>
        {
            ["ConnectionId"] = ids[0],
            ["AdditionalConnections"] = ids.Skip(1)
                .Select(id => new Dictionary<string, object?> { ["ConnectionId"] = id }).ToArray()
        };
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Operation"] = "set",
            ["Value"] = binding,
            ["PayloadSha256"] = new string('0', 64),
            ["Recovery"] = null
        }) + "\n";
    }

    private static string OldDelete() =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Operation"] = "delete",
            ["Value"] = null,
            ["PayloadSha256"] = new string('0', 64),
            ["Recovery"] = null
        }) + "\n";
}
