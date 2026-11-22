using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Connections: one canonical array representation ([] is the empty list, disjoint from
/// every legacy log); the predecessor's typed payloads are validated before a record replaces the prefix or a slot is
/// selected; slot-only sources are authoritative without append bytes; in-place Verify and the next Read agree; distinct
/// targets keep BC8/R47; in-place evidence is never overwritten.</summary>
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

            // The predecessor's typed payload is validated before a record replaces the prefix or a slot is selected.
            var legacy = Root(scratch, "legacy");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(legacy))!);
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1, "set", TypedTimeBinding(second), null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A wrong-typed time field replaced the earlier valid set");
            var structSlot = Root(scratch, "structSlot");
            Slot(structSlot, 'a', 1, present: true, new[] { first });
            SlotWithBinding(structSlot, 'b', 2, TypedTimeBinding(second));
            Check.Require(connections.Read(structSlot).SequenceEqual(new[] { first }),
                "A wrong-typed higher slot generation was selected");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1, "set", TypedRetainedBinding(second), null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A wrong-typed retained record replaced the earlier valid set");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1, "set", NumericAccountBinding(second), null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A numeric account id replaced the earlier valid set");
            var numericSlot = Root(scratch, "numericSlot");
            Slot(numericSlot, 'a', 1, present: true, new[] { first });
            SlotWithBinding(numericSlot, 'b', 2, NumericAccountBinding(second));
            Check.Require(connections.Read(numericSlot).SequenceEqual(new[] { first }),
                "A wrong-typed higher slot generation was selected");
            // A tombstone must still decode a supplied value the predecessor would reject.
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyDelete(TypedTimeBinding(second)));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A tombstone with a wrong-typed supplied value was applied");

            // R47: a slot-only source is authoritative; a same-set append target is accepted without append bytes.
            var slotAuthority = Root(scratch, "slotAuthority");
            Slot(slotAuthority, 'a', 1, present: true, new[] { second });
            var appendSameSet = Root(scratch, "appendSameSet");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(appendSameSet))!);
            File.WriteAllText(ListFile(appendSameSet), LegacySet(second));
            var appendBytes = File.ReadAllBytes(ListFile(appendSameSet));
            Check.Require(connections.Verify(slotAuthority, appendSameSet, new[] { second }) is null,
                "A slot-only source refused a same-set append target for lack of append bytes");
            Check.Require(File.ReadAllBytes(ListFile(appendSameSet)).AsSpan().SequenceEqual(appendBytes),
                "A slot-only verification overwrote the accepted target");
            var appendOtherSet = Root(scratch, "appendOtherSet");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(appendOtherSet))!);
            File.WriteAllText(ListFile(appendOtherSet), LegacySet(third));
            var refused = false;
            try { connections.Verify(slotAuthority, appendOtherSet, new[] { second }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A slot-only source accepted a target with a different set");

            // In place: an occupied canonical list that diverges from the slots refuses before publication.
            var inPlaceConflict = Root(scratch, "inPlaceConflict");
            Slot(inPlaceConflict, 'a', 1, present: true, new[] { first, second });
            connections.Save(inPlaceConflict, new[] { first });
            refused = false;
            try { connections.Verify(inPlaceConflict, inPlaceConflict, new[] { second }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "An in-place canonical list that hides a slot connection was accepted");
            var inPlaceEqual = Root(scratch, "inPlaceEqual");
            Slot(inPlaceEqual, 'a', 1, present: true, new[] { first });
            connections.Save(inPlaceEqual, new[] { first });
            Check.Require(connections.Verify(inPlaceEqual, inPlaceEqual, new[] { first }) is null &&
                          connections.Read(inPlaceEqual).SequenceEqual(new[] { first }),
                "An equal in-place canonical list and slot were refused or disagreed with Read");

            // A blank legacy line is valid legacy input; the canonical array is what tells an empty list apart.
            var blankLegacy = Root(scratch, "blankLegacy");
            Slot(blankLegacy, 'a', 1, present: true, new[] { first });
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(blankLegacy))!);
            File.WriteAllText(ListFile(blankLegacy), "\n");
            Check.Require(connections.Verify(blankLegacy, blankLegacy, new[] { first }) is null &&
                          connections.Read(blankLegacy).SequenceEqual(new[] { first }),
                "A blank legacy line was treated as our empty list instead of the authoritative slot");
            connections.Save(blankLegacy, Array.Empty<string>());
            Check.Require(connections.Read(blankLegacy).Count == 0,
                "Saving an empty list did not override the old slots");

            // BC8/R47: a distinct target must carry the authoritative set; an append target its exact bytes.
            var slotSource = Root(scratch, "slotSource");
            Slot(slotSource, 'a', 2, present: true, new[] { first, second });
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(slotSource))!);
            File.WriteAllText(ListFile(slotSource), LegacySet(first)); // stale append beside the authoritative slot
            var staleCopy = Root(scratch, "staleCopy");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(staleCopy))!);
            File.Copy(ListFile(slotSource), ListFile(staleCopy));
            refused = false;
            try { connections.Verify(slotSource, staleCopy, new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A distinct target that dropped an authoritative connection was accepted");

            var appendSource = Root(scratch, "appendSource");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(appendSource))!);
            File.WriteAllText(ListFile(appendSource), LegacySet(first));
            var identical = Root(scratch, "identical");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(identical))!);
            File.Copy(ListFile(appendSource), ListFile(identical));
            Check.Require(connections.Verify(appendSource, identical, new[] { first }) is null,
                "A byte-identical distinct append target with the same set was refused");
            var sameIdsOtherBytes = Root(scratch, "sameIdsOtherBytes");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(sameIdsOtherBytes))!);
            File.WriteAllText(ListFile(sameIdsOtherBytes), LegacyRecovery("evidence.json") + LegacySet(first));
            refused = false;
            try { connections.Verify(appendSource, sameIdsOtherBytes, new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A distinct append target with different bytes was accepted");

            // R47: an append-authoritative source and a same-set canonical target with different bytes are both kept.
            var r47Target = Root(scratch, "r47Target");
            connections.Save(r47Target, new[] { first });
            var sourceBytes = File.ReadAllBytes(ListFile(appendSource));
            var targetBytes = File.ReadAllBytes(ListFile(r47Target));
            Check.Require(connections.Verify(appendSource, r47Target, new[] { first }) is null,
                "A same-set canonical target with different bytes was refused instead of kept under R47");
            Check.Require(File.ReadAllBytes(ListFile(appendSource)).AsSpan().SequenceEqual(sourceBytes) &&
                          File.ReadAllBytes(ListFile(r47Target)).AsSpan().SequenceEqual(targetBytes),
                "R47 verification overwrote one side");

            // A distinct absent target copies the authoritative append log's bytes, not a normalized rewrite.
            var copySource = Root(scratch, "copySource");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(copySource))!);
            File.WriteAllText(ListFile(copySource), LegacyRecovery("evidence.json") + LegacySet(first));
            var copyTarget = Root(scratch, "copyTarget");
            Check.Require(connections.Verify(copySource, copyTarget, new[] { first }) is not null &&
                          File.ReadAllBytes(ListFile(copyTarget)).AsSpan().SequenceEqual(File.ReadAllBytes(ListFile(copySource))),
                "An absent distinct target was not byte-copied from the authoritative append log");
            Check.Require(connections.Read(copyTarget).SequenceEqual(new[] { first }), "The byte-copied list did not read back");

            // In place: a newer slot with a stale legacy log is evidence, not a competing target.
            var inPlace = Root(scratch, "inPlace");
            Slot(inPlace, 'a', 2, present: true, new[] { first });
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(inPlace))!);
            var stale = LegacySet(second);
            File.WriteAllText(ListFile(inPlace), stale);
            Check.Require(connections.Verify(inPlace, inPlace, new[] { first }) is null,
                "An in-place upgrade refused the newer slot because its stale legacy log looked like a target");
            Check.Require(File.ReadAllText(ListFile(inPlace)) == stale, "An in-place upgrade overwrote the predecessor's log");
            Check.Require(connections.Read(inPlace).SequenceEqual(new[] { first }), "An in-place root did not resolve its authoritative slot");

            // In place: a zero-byte legacy log with a valid slot resolves the slot on Verify and on the next Read.
            var zeroLegacy = Root(scratch, "zeroLegacy");
            Slot(zeroLegacy, 'a', 1, present: true, new[] { first });
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(zeroLegacy))!);
            File.WriteAllBytes(ListFile(zeroLegacy), Array.Empty<byte>());
            Check.Require(connections.Verify(zeroLegacy, zeroLegacy, new[] { first }) is null,
                "An in-place zero-byte legacy log with a valid slot was refused");
            Check.Require(connections.Read(zeroLegacy).SequenceEqual(new[] { first }),
                "A zero-byte legacy log did not resolve the same slot the Verify used");

            // In place with only slots: the slot set is authoritative and no file is created.
            var slotOnly = Root(scratch, "slotOnly");
            Slot(slotOnly, 'a', 1, present: true, new[] { first, second });
            Check.Require(connections.Verify(slotOnly, slotOnly, new[] { first, second }) is null &&
                          connections.Read(slotOnly).SequenceEqual(Sorted(first, second)),
                "An in-place slot-only root did not expose its authoritative set");

            // In place with only the legacy log: nothing is published.
            var legacyOnly = Root(scratch, "legacyOnly");
            Directory.CreateDirectory(Path.GetDirectoryName(ListFile(legacyOnly))!);
            File.WriteAllText(ListFile(legacyOnly), LegacySet(first));
            Check.Require(connections.Verify(legacyOnly, legacyOnly, new[] { first }) is null &&
                          connections.Read(legacyOnly).SequenceEqual(new[] { first }),
                "An in-place legacy-only root was refused or republished");

            // Distinct roots, canonical target: equal set accepted, different set refused.
            var equalSlotSource = Root(scratch, "equalSlotSource");
            Slot(equalSlotSource, 'a', 1, present: true, new[] { first, second });
            var distinctTarget = Root(scratch, "distinctTarget");
            connections.Save(distinctTarget, new[] { first, second });
            Check.Require(connections.Verify(equalSlotSource, distinctTarget, new[] { first }) is null,
                "An equal canonical distinct target was refused");
            refused = false;
            try { connections.Verify(appendSource, distinctTarget, new[] { first }); }
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
            File.WriteAllText(Path.Combine(oversizedSlot, "state", "dropbox-connection-a.json"), new string('x', MaximumStateBytes + 1));
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

            // The predecessor's append validation and valid prefix.
            File.WriteAllText(ListFile(legacy), LegacySetWithBinding(BindingJson(null, "acct-derived")));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { Connections.CreateConnectionId("acct-derived") }),
                "A blank connection id with an account id was not normalized to its derived id");
            File.WriteAllText(ListFile(legacy), LegacySetWithBinding(BindingJson("  " + first + "  ", "")));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }), "An explicit connection id was not trimmed");
            File.WriteAllText(ListFile(legacy), LegacyRecovery(null));
            Check.Require(connections.Read(legacy).Count == 0, "A recovery record without its evidence file was accepted");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecovery("evidence.json"));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A valid recovery record changed the current value or stopped the scan");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecovery("evidence.json") + LegacySet(second));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { second }),
                "A different valid set after a recovery record was not applied");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1.5, "set", BindingJson(null, "acct-other"), null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "A non-Int32 record schema threw instead of keeping the valid prefix");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + "\r\n" + LegacySet(second));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }),
                "An empty-after-CR record was skipped instead of stopping at the valid prefix");
            File.WriteAllText(ListFile(legacy), "not-json\n" + LegacySet(second));
            Check.Require(connections.Read(legacy).Count == 0, "A malformed first record did not stop with an empty prefix");
            File.WriteAllText(ListFile(legacy), LegacySet(first).TrimEnd('\n'));
            Check.Require(connections.Read(legacy).Count == 0, "An unterminated append record was accepted");
            File.WriteAllText(ListFile(legacy), LegacySet(first) + LegacyRecord(1, "append", null, null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }), "An invalid operation did not stop the append scan");

            // Only applied records count against the ceiling.
            var hundred = new StringBuilder();
            for (var index = 0; index < 100; index++) hundred.Append(LegacySet(first));
            File.WriteAllText(ListFile(legacy), hundred + LegacyRecord(1, "append", null, null));
            Check.Require(connections.Read(legacy).SequenceEqual(new[] { first }), "A bad record after the applied ceiling did not stop cleanly");
            var over = new StringBuilder();
            for (var index = 0; index < 101; index++) over.Append(LegacySet(first));
            File.WriteAllText(ListFile(legacy), over.ToString());
            refused = false;
            try { connections.Read(legacy); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "An append log over its applied-record ceiling was accepted");

            // Empty lists and canonical save.
            var empty = Root(scratch, "empty");
            Check.Require(connections.Verify(Root(scratch, "none"), empty, Array.Empty<string>()) is null &&
                          !File.Exists(ListFile(empty)), "Both lists absent created a list");
            refused = false;
            try { connections.Verify(Root(scratch, "none"), Root(scratch, "required"), new[] { first }); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused, "A required connection with both lists absent was accepted");
            connections.Save(empty, new[] { second, first, second });
            Check.Require(connections.Read(empty).SequenceEqual(Sorted(first, second)), "A saved canonical list was not read back");
            connections.Save(empty, Array.Empty<string>());
            Check.Require(connections.Read(empty).Count == 0, "An empty canonical list was not read back");
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

    private static string NumericAccountBinding(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = 123,
            ["AdditionalConnections"] = Array.Empty<object>()
        });

    private static string TypedTimeBinding(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-typed",
            ["ConnectedUtc"] = "not-a-date",
            ["AdditionalConnections"] = Array.Empty<object>()
        });

    private static string TypedRetainedBinding(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-typed",
            ["RetainedConnections"] = new[] { new Dictionary<string, object?> { ["ConnectionId"] = 123 } }
        });

    /// <summary>Writes one 0.3.95 alternating slot with the predecessor's record shape and payload hash.</summary>
    private static string Slot(string root, char slot, long generation, bool present, string[] ids, string? hash = null) =>
        SlotWithBinding(root, slot, generation, present ? BindingJson(ids[0], "acct-slot", ids.Skip(1).ToArray()) : null,
            present: present, hash: hash);

    private static string SlotWithBinding(string root, char slot, long generation, string? bindingJson,
        bool present = true, string? hash = null)
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        var value = present
            ? JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["Present"] = true,
                ["Value"] = JsonSerializer.Deserialize<JsonElement>(bindingJson!)
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

    private static string LegacySetWithBinding(string bindingJson) => LegacyRecord(1, "set", bindingJson, null);

    private static string LegacyDelete(string? suppliedValueJson = null) =>
        LegacyRecord(1, "delete", suppliedValueJson, null, payload: "delete");

    private static string LegacyRecovery(string? evidenceFile) =>
        LegacyRecord(1, "recovery", null,
            evidenceFile is null ? "{}" : JsonSerializer.Serialize(new Dictionary<string, object?> { ["EvidenceFile"] = evidenceFile }));

    private static string LegacyRecord(object schema, string operation, string? valueJson, string? recoveryJson,
        string? payload = null)
    {
        var hashed = payload ?? recoveryJson ?? valueJson ?? "delete";
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = schema,
            ["Operation"] = operation,
            ["Value"] = valueJson is null ? null : JsonSerializer.Deserialize<JsonElement>(valueJson),
            ["PayloadSha256"] = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(hashed))),
            ["Recovery"] = recoveryJson is null ? null : JsonSerializer.Deserialize<JsonElement>(recoveryJson)
        }) + "\n";
    }

    private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
}
