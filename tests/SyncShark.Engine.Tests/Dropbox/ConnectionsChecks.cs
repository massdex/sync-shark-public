using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Connections: the authoritative 0.3.95 value is resolved from the alternating slots
/// before the legacy append log, the predecessor's record validation and ordering are kept, and the store-move copy
/// is create-only from that resolved set.</summary>
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

            // The successor slots are authoritative; a stale legacy log is never consulted.
            var source = Root(scratch, "source");
            Slot(source, 'a', 2, present: true, new[] { first, second });
            File.WriteAllText(ListFile(source), LegacySet(third));
            var product = Root(scratch, "product");
            var cleanup = connections.Verify(source, product, new[] { first, second });
            Check.Require(cleanup is not null && connections.Read(product).SequenceEqual(Sorted(first, second)),
                "The authoritative 0.3.95 slots were not migrated or the stale log won");
            cleanup!();
            Check.Require(!File.Exists(ListFile(product)), "Cleanup kept the list this attempt created");

            // A newer slot deletion overrides the legacy log.
            var tomb = Root(scratch, "tomb");
            Slot(tomb, 'a', 3, present: false, Array.Empty<string>());
            File.WriteAllText(ListFile(tomb), LegacySet(first));
            var refused = false;
            try { connections.Verify(tomb, Root(scratch, "tombTarget"), new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A newer slot deletion was overridden by the stale legacy log");

            // The higher valid generation wins; two invalid slots refuse.
            var generations = Root(scratch, "generations");
            Slot(generations, 'a', 1, present: true, new[] { second });
            Slot(generations, 'b', 2, present: true, new[] { first });
            Check.Require(connections.Verify(generations, Root(scratch, "generationTarget"), new[] { first }) is not null,
                "The higher-generation slot was not selected");
            var invalid = Root(scratch, "invalidSlots");
            Slot(invalid, 'a', 1, present: true, new[] { first }, hash: new string('0', 64));
            Slot(invalid, 'b', 2, present: true, new[] { second }, hash: new string('0', 64));
            refused = false;
            try { connections.Verify(invalid, Root(scratch, "invalidTarget"), Array.Empty<string>()); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "Invalid successor slots were accepted");

            // The predecessor's append scan stops at a bad record; an unterminated tail is not a record.
            var legacy = Root(scratch, "legacy");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(legacy))!);
            File.WriteAllText(ListFile(legacy), LegacySet(first) +
                JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["SchemaVersion"] = 1,
                    ["Operation"] = "set",
                    ["Value"] = JsonSerializer.SerializeToElement(Binding(new[] { third })),
                    ["PayloadSha256"] = new string('0', 64),
                    ["Recovery"] = null
                }) + "\n" + LegacySet(second));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A bad-hash record did not stop the append scan at its valid prefix");
            File.WriteAllText(ListFile(legacy), LegacySet(first).TrimEnd('\n'));
            Check.Require(connections.Read(legacy).Count == 0, "An unterminated append record was accepted");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyDelete());
            Check.Require(connections.Read(legacy).Count == 0, "A validated delete record did not clear the current value");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + "{\"SchemaVersion\":1,\"Operation\":\"append\"}\n" + LegacySet(second));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }), "An invalid operation did not stop the append scan");

            // Our canonical list: save/replace, equal target accepted, different target refused.
            var product2 = Root(scratch, "product2");
            connections.Save(product2, new[] { second, first, second });
            Check.Require(connections.Read(product2).SequenceEqual(Sorted(first, second)),
                "A saved canonical list was not read back distinct and ordered");
            Check.Require(connections.Verify(source, product2, new[] { first }) is null &&
                          File.Exists(ListFile(product2)), "An equal target set was refused or claimed cleanup");
            refused = false;
            try { connections.Verify(generations, product2, new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && connections.Read(product2).SequenceEqual(Sorted(first, second)),
                "A different target set was accepted or the target changed");

            // Absent lists: nothing is created without a source; a required connection then refuses.
            var empty = Root(scratch, "empty");
            Check.Require(connections.Verify(Root(scratch, "none"), empty, Array.Empty<string>()) is null &&
                          !File.Exists(ListFile(empty)), "Both lists absent created a list");
            refused = false;
            try { connections.Verify(Root(scratch, "none"), Root(scratch, "required"), new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A required connection with both lists absent was accepted");
            refused = false;
            try { connections.Save(empty, new[] { "not-a-connection-id" }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "An invalid id was saved");

            var accountId = "acct-fixture";
            var created = Connections.CreateConnectionId(accountId);
            var expected = "connection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dropbox\n" + accountId))).ToLowerInvariant()[..24];
            Check.Require(created == expected && Connections.CreateConnectionId(accountId) == created,
                "Connection id is not the stable account hash");
            Check.Require(Connections.CreateConnectionId("acct-other") != created, "Different accounts share a connection id");
        }
        finally { Directory.Delete(scratch, true); }
        return Task.CompletedTask;
    }

    private static string Root(string scratch, string name) => Path.Combine(scratch, name);
    private static string ListFile(string root) => Path.Combine(root, "state", "dropbox-connection.jsonl");
    private static string[] Sorted(params string[] ids) => ids.Order(StringComparer.Ordinal).ToArray();

    private static string Id(string seed) =>
        "connection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant()[..24];

    private static Dictionary<string, object?> Binding(string[] ids) => new()
    {
        ["ConnectionId"] = ids[0],
        ["AdditionalConnections"] = ids.Skip(1)
            .Select(id => new Dictionary<string, object?> { ["ConnectionId"] = id }).ToArray()
    };

    /// <summary>Writes one 0.3.95 alternating slot with the predecessor's record shape and payload hash.</summary>
    private static string Slot(string root, char slot, long generation, bool present, string[] ids, string? hash = null)
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        var value = present
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["Present"] = true,
                ["Value"] = Binding(ids)
            })
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Present"] = false, ["Value"] = null },
                new JsonSerializerOptions());
        var payloadHash = hash ?? Hex(SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText())));
        var path = Path.Combine(state, "dropbox-connection-" + slot + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Generation"] = generation,
            ["Value"] = value,
            ["PayloadSha256"] = payloadHash
        }));
        return path;
    }

    private static string LegacySet(params string[] ids)
    {
        var binding = Binding(ids);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Operation"] = "set",
            ["Value"] = JsonSerializer.SerializeToElement(binding),
            ["PayloadSha256"] = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(binding)))),
            ["Recovery"] = null
        }) + "\n";
    }

    private static string LegacyDelete() =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Operation"] = "delete",
            ["Value"] = null,
            ["PayloadSha256"] = Hex(SHA256.HashData(Encoding.UTF8.GetBytes("delete"))),
            ["Recovery"] = null
        }) + "\n";

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
