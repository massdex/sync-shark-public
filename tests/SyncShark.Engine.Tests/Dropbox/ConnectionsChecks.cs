using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Connections: an in-place 0.3.95 upgrade accepts the root's authoritative value without
/// overwriting its own evidence, distinct roots keep BC8's byte-identity rule, the predecessor's append validation and
/// valid prefix are retained, and the store-move copy is create-only.</summary>
public static class ConnectionsChecks
{
    private const int MaximumStateBytes = 1024 * 1024;

    public static Task RunAsync()
    {
        var first = Id("first");
        var second = Id("second");
        var third = Id("third");
        var scratch = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var connections = new Connections();

            // In place: a newer slot with a stale legacy log is the predecessor's own evidence, not a competing target.
            var inPlace = Root(scratch, "inPlace");
            Slot(inPlace, 'a', 2, present: true, new[] { first });
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(inPlace))!);
            var stale = LegacySet(second);
            File.WriteAllText(ListFile(inPlace), stale);
            Check.Require(connections.Verify(inPlace, inPlace, new[] { first }) is null,
                "An in-place upgrade refused the newer slot because its stale legacy log looked like a target");
            Check.Require(File.ReadAllText(ListFile(inPlace)) == stale, "An in-place upgrade overwrote the predecessor's log");
            Check.Require(connections.Read(inPlace).SequenceEqual(new[] { first }), "An in-place root did not resolve its authoritative slot");

            // In place with only slots: a create-only list is published and cleanup removes it.
            var slotOnly = Root(scratch, "slotOnly");
            Slot(slotOnly, 'a', 1, present: true, new[] { first, second });
            var cleanup = connections.Verify(slotOnly, slotOnly, new[] { first, second });
            Check.Require(cleanup is not null && connections.Read(slotOnly).SequenceEqual(Sorted(first, second)),
                "An in-place slot-only root did not publish its authoritative set");
            cleanup!();

            // In place with only the legacy log: nothing is published.
            var legacyOnly = Root(scratch, "legacyOnly");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(legacyOnly))!);
            File.WriteAllText(ListFile(legacyOnly), LegacySet(first));
            Check.Require(connections.Verify(legacyOnly, legacyOnly, new[] { first }) is null && connections.Read(legacyOnly).SequenceEqual(new[] { first }),
                "An in-place legacy-only root was refused or republished");

            // Distinct roots: a distinct append target is accepted only byte-identically (BC8), not merely by equal ids.
            var source = Root(scratch, "source");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(source))!);
            File.WriteAllText(ListFile(source), LegacySet(first));
            var sameIdsOtherBytes = Root(scratch, "sameIdsOtherBytes");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(sameIdsOtherBytes))!);
            File.WriteAllText(ListFile(sameIdsOtherBytes), LegacyRecovery("evidence.json") + LegacySet(first));
            var refused = false;
            try { connections.Verify(source, sameIdsOtherBytes, new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A distinct append target with different bytes was accepted because its ids matched");
            var identical = Root(scratch, "identical");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(identical))!);
            File.Copy(ListFile(source), ListFile(identical));
            Check.Require(connections.Verify(source, identical, new[] { first }) is null, "A byte-identical distinct append target was refused");

            // Distinct roots, source slots only, and a legacy target: a competing target refuses.
            var slotSource = Root(scratch, "slotSource");
            Slot(slotSource, 'a', 1, present: true, new[] { second });
            refused = false;
            try { connections.Verify(slotSource, identical, new[] { second }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A distinct legacy target was accepted for a slot-only source");

            // Distinct roots, canonical target: equal set accepted, different set refused.
            var equalSlotSource = Root(scratch, "equalSlotSource");
            Slot(equalSlotSource, 'a', 1, present: true, new[] { first, second });
            var distinctTarget = Root(scratch, "distinctTarget");
            connections.Save(distinctTarget, new[] { first, second });
            Check.Require(connections.Verify(equalSlotSource, distinctTarget, new[] { first }) is null,
                "An equal canonical distinct target was refused");
            refused = false;
            try { connections.Verify(source, distinctTarget, new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A different canonical distinct target was accepted");

            // A newer slot deletion overrides the legacy log.
            var tomb = Root(scratch, "tomb");
            Slot(tomb, 'a', 3, present: false, Array.Empty<string>());
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(tomb))!);
            File.WriteAllText(ListFile(tomb), LegacySet(first));
            refused = false;
            try { connections.Verify(tomb, Root(scratch, "tombTarget"), new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A newer slot deletion was overridden by the stale legacy log");

            // Higher valid generation wins; two invalid slots refuse.
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

            // Oversized state refuses before use.
            var oversizedSlot = Root(scratch, "oversizedSlot");
            Directory.CreateDirectory(Path.Combine(oversizedSlot, "state"));
            File.WriteAllText(Path.Combine(oversizedSlot, "state", "dropbox-connection-a.json"),
                new string('x', MaximumStateBytes + 1));
            refused = false;
            try { connections.Read(oversizedSlot); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "An oversized successor slot was read");
            var oversizedAppend = Root(scratch, "oversizedAppend");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(oversizedAppend))!);
            File.WriteAllText(ListFile(oversizedAppend), new string('x', MaximumStateBytes + 1));
            refused = false;
            try { connections.Read(oversizedAppend); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "An oversized append log was read");

            // The predecessor's append validation and valid prefix: derived id, recovery evidence, non-Int32 schema, first bad line.
            var legacy = Root(scratch, "legacy");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(legacy))!);
            File.WriteAllText(ListFile(legacy), LegacySetWithBinding(BindingJson(null, "acct-derived")));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { Connections.CreateConnectionId("acct-derived") }),
                "A blank connection id with an account id was not normalized to its derived id");
            File.WriteAllText(ListFile(legacy), LegacyRecovery(null));
            Check.Require(connections.Read(legacy).Count == 0, "A recovery record without its evidence file was accepted");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecovery("evidence.json"));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A valid recovery record changed the current value or stopped the scan");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1.5, "set", BindingJson(null, "acct-other"), null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A non-Int32 record schema threw instead of keeping the valid prefix");
            File.WriteAllText(ListFile(legacy), "not-json\n" + LegacySet(second));
            Check.Require(connections.Read(legacy).Count == 0, "A malformed first record did not stop with an empty prefix");
            File.WriteAllText(ListFile(legacy), LegacySet(first).TrimEnd('\n'));
            Check.Require(connections.Read(legacy).Count == 0, "An unterminated append record was accepted");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1, "append", null, null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }), "An invalid operation did not stop the append scan");
            var many = new StringBuilder();
            for (var index = 0; index < 101; index++) many.Append(LegacySet(first));
            File.WriteAllText(ListFile(legacy), many.ToString());
            refused = false;
            try { connections.Read(legacy); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "An append log over its record ceiling was accepted");

            // Absent lists and canonical save.
            var empty = Root(scratch, "empty");
            Check.Require(connections.Verify(Root(scratch, "none"), empty, Array.Empty<string>()) is null &&
                          !File.Exists(ListFile(empty)), "Both lists absent created a list");
            refused = false;
            try { connections.Verify(Root(scratch, "none"), Root(scratch, "required"), new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A required connection with both lists absent was accepted");
            connections.Save(empty, new[] { second, first, second });
            Check.Require(connections.Read(empty).SequenceEqual(Sorted(first, second)), "A saved canonical list was not read back");
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

    private static string BindingJson(string? connectionId, string accountId, params string[] additionalIds) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = connectionId,
            ["AccountId"] = accountId,
            ["AdditionalConnections"] = additionalIds.Select(id => new Dictionary<string, object?>
            {
                ["ConnectionId"] = id,
                ["AccountId"] = ""
            }).ToArray()
        });

    /// <summary>Writes one 0.3.95 alternating slot with the predecessor's record shape and payload hash.</summary>
    private static string Slot(string root, char slot, long generation, bool present, string[] ids, string? hash = null)
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        var value = present
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["Present"] = true,
                ["Value"] = JsonSerializer.Deserialize<JsonElement>(BindingJson(ids[0], "acct-slot", ids.Skip(1).ToArray()))
            })
            : JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["Present"] = false, ["Value"] = null });
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

    private static string LegacySet(params string[] ids) =>
        LegacySetWithBinding(BindingJson(ids[0], "acct-log", ids.Skip(1).ToArray()));

    private static string LegacySetWithBinding(string bindingJson) =>
        LegacyRecord(1, "set", bindingJson, null);

    private static string LegacyRecovery(string? evidenceFile) =>
        LegacyRecord(1, "recovery", null,
            evidenceFile is null ? "{}" : JsonSerializer.Serialize(new Dictionary<string, object?> { ["EvidenceFile"] = evidenceFile }));

    private static string LegacyRecord(object schema, string operation, string? valueJson, string? recoveryJson)
    {
        var payload = recoveryJson ?? valueJson ?? "delete";
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = schema,
            ["Operation"] = operation,
            ["Value"] = valueJson is null ? null : JsonSerializer.Deserialize<JsonElement>(valueJson),
            ["PayloadSha256"] = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            ["Recovery"] = recoveryJson is null ? null : JsonSerializer.Deserialize<JsonElement>(recoveryJson)
        }) + "\n";
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
