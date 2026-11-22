using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Dropbox;

/// <summary>Contract checks for Connections. Each case is reported separately (one accumulated failure line) so a red
/// run shows every failing case: one canonical array list; the predecessor's typed records/slots are validated before
/// acceptance; a slot-only source is authoritative without append bytes; in-place Verify and the next Read agree (the
/// required id is present on both sides of a divergence); distinct targets keep BC8/R47; stage names are unique.</summary>
public static class ConnectionsChecks
{
    private const int MaximumStateBytes = 1024 * 1024;

    public static Task RunAsync()
    {
        var failures = new List<string>();
        var first = Id("first");
        var second = Id("second");
        var third = Id("third");
        var scratch = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var connections = new Connections();

            Case(failures, "typed time after a valid set", () =>
            {
                var root = Root(scratch, "typedTimeAppend");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", TypedTimeBinding(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A wrong-typed time field replaced the valid set");
            });
            Case(failures, "typed time in a higher slot", () =>
            {
                var root = Root(scratch, "typedTimeSlot");
                Slot(root, 'a', 1, present: true, new[] { first });
                SlotWithBinding(root, 'b', 2, TypedTimeBinding(second));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A wrong-typed higher slot generation was selected");
            });
            Case(failures, "valid framework date after a valid set", () =>
            {
                var root = Root(scratch, "validDateAppend");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", DatedBinding(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A valid framework date did not replace the earlier set");
            });
            Case(failures, "valid framework date in a higher slot", () =>
            {
                var root = Root(scratch, "validDateSlot");
                Slot(root, 'a', 1, present: true, new[] { first });
                SlotWithBinding(root, 'b', 2, DatedBinding(second));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A valid framework date in a higher slot was not selected");
            });
            Case(failures, "set with a blank recovery evidence applies", () =>
            {
                var root = Root(scratch, "setBlankEvidence");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", BindingJson(second, "acct-b"), RecoveryJson("")));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A set supplying a blank-evidence recovery was rejected");
            });
            Case(failures, "set with null ancillary recovery fields applies", () =>
            {
                var root = Root(scratch, "setNullAncillary");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root),
                    LegacySet(first) + LegacyRecord(1, "set", BindingJson(second, "acct-b"), RecoveryJson("e.json", nullAncillary: true)));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A set supplying null ancillary recovery fields was rejected");
            });
            Case(failures, "retained record with an unknown capability applies", () =>
            {
                var root = Root(scratch, "retainedUnknownCapability");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", RetainedUnknownCapability(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "An unknown capability field invalidated the retained record");
            });
            Case(failures, "retained record with a wrong-typed known capability is rejected", () =>
            {
                var root = Root(scratch, "retainedWrongCapability");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", RetainedWrongKnownCapability(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A wrong-typed known capability was accepted");
            });
            Case(failures, "omitted slot Present selects no membership", () =>
            {
                var root = Root(scratch, "slotOmittedPresent");
                Slot(root, 'a', 1, present: true, new[] { first });
                WriteSlot(root, 'b', 2, new Dictionary<string, object?>(), hash: null);
                Expect(connections.Read(root).Count == 0, "An omitted slot Present did not select the higher slot with no membership");
            });
            Case(failures, "slot present with a null value selects no membership", () =>
            {
                var root = Root(scratch, "slotNullValue");
                Slot(root, 'a', 1, present: true, new[] { first });
                WriteSlot(root, 'b', 2, new Dictionary<string, object?> { ["Present"] = true, ["Value"] = null }, hash: null);
                Expect(connections.Read(root).Count == 0, "Present:true with a null slot value exposed the lower slot");
            });
            Case(failures, "scopes with a null entry decodes", () =>
            {
                var root = Root(scratch, "scopesNullEntry");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", ScopesWithNull(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "Scopes:[null] stopped the prefix");
            });
            Case(failures, "a superseded set without ids does not stop the scan", () =>
            {
                var root = Root(scratch, "supersededNoIds");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySetWithBinding(BindingJson(null, "")) + LegacySet(second));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A superseded id-less set stopped the scan");
            });
            Case(failures, "a superseded set with a null additional entry does not stop the scan", () =>
            {
                var root = Root(scratch, "supersededNullAdditional");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacyRecord(1, "set", NullAdditional(first), null) + LegacySet(second));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A superseded null additional entry stopped the scan");
            });
            Case(failures, "a winning set with a null additional entry refuses", () =>
            {
                var root = Root(scratch, "winnerNullAdditional");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacyRecord(1, "set", NullAdditional(first), null));
                Expect(Refuses(() => connections.Read(root)), "A winning set with a null additional entry was projected");
            });
            Case(failures, "a set with a null retained entry decodes", () =>
            {
                var root = Root(scratch, "nullRetainedEntry");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacyRecord(1, "set", NullRetained(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A null retained entry stopped the typed read");
            });
            Case(failures, "wrong-typed retained record", () =>
            {
                var root = Root(scratch, "typedRetainedAppend");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", TypedRetainedBinding(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A wrong-typed retained record replaced the valid set");
            });
            Case(failures, "numeric account id after a valid set", () =>
            {
                var root = Root(scratch, "numericAppend");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "set", NumericAccountBinding(second), null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A numeric account id replaced the valid set");
            });
            Case(failures, "numeric account id in a higher slot", () =>
            {
                var root = Root(scratch, "numericSlot");
                Slot(root, 'a', 1, present: true, new[] { first });
                SlotWithBinding(root, 'b', 2, NumericAccountBinding(second));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A wrong-typed higher slot generation was selected");
            });
            Case(failures, "tombstone with a wrong-typed supplied value", () =>
            {
                var root = Root(scratch, "tombstoneValue");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyDelete(TypedTimeBinding(second)));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A tombstone with a wrong-typed supplied value was applied");
            });
            Case(failures, "recovery without evidence stops the prefix", () =>
            {
                var root = Root(scratch, "recoveryNoEvidence");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecovery(null) + LegacySet(second));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A recovery record without evidence did not stop the scan");
            });
            Case(failures, "recovery with a string byte count stops the prefix", () =>
            {
                var root = Root(scratch, "recoveryStringCount");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root),
                    LegacySet(first) + LegacyRecord(1, "recovery", null, RecoveryJson("evidence.json", wrongByteCount: true)) + LegacySet(second));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A recovery with a wrong-typed byte count advanced the scan");
            });
            Case(failures, "valid recovery then a later set", () =>
            {
                var root = Root(scratch, "recoveryThenSet");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root),
                    LegacySet(first) + LegacyRecord(1, "recovery", null, RecoveryJson("evidence.json")) + LegacySet(second));
                Expect(connections.Read(root).SequenceEqual(new[] { second }), "A valid recovery record stopped the scan");
            });
            Case(failures, "slot tombstone with a wrong-typed supplied value", () =>
            {
                var root = Root(scratch, "slotTombstoneValue");
                Slot(root, 'a', 1, present: true, new[] { first });
                SlotTombstoneWithBinding(root, 'b', 2, TypedTimeBinding(second));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A wrong-typed slot tombstone hid the lower valid slot");
            });

            // R47: a slot-only source is authoritative; a same-set append target is accepted without append bytes.
            Case(failures, "slot-only same-set append target accepted and preserved", () =>
            {
                var source = Root(scratch, "slotAuthority");
                Slot(source, 'a', 1, present: true, new[] { second });
                var target = Root(scratch, "appendSameSet");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(target))!);
                File.WriteAllText(ListFile(target), LegacySet(second));
                var bytes = File.ReadAllBytes(ListFile(target));
                var accepted = false;
                var detail = string.Empty;
                try { accepted = connections.Verify(source, target, new[] { second }) is null; }
                catch (Exception error) { detail = " (" + error.Message + ")"; }
                Expect(accepted, "A slot-only source refused a same-set append target for lack of append bytes" + detail);
                Expect(File.ReadAllBytes(ListFile(target)).AsSpan().SequenceEqual(bytes),
                    "A slot-only verification overwrote the accepted target");
            });
            Case(failures, "slot-only different set refuses with the required id present", () =>
            {
                var source = Root(scratch, "slotAuthorityDifferent");
                Slot(source, 'a', 1, present: true, new[] { second });
                var target = Root(scratch, "appendExtraSet");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(target))!);
                File.WriteAllText(ListFile(target), LegacySet(second, third)); // every required id present, set differs
                var bytes = File.ReadAllBytes(ListFile(target));
                Expect(Refuses(() => connections.Verify(source, target, new[] { second })),
                    "A slot-only source accepted a target whose set differs (required id was present)");
                Expect(File.ReadAllBytes(ListFile(target)).AsSpan().SequenceEqual(bytes),
                    "A slot-only refusal overwrote the conflicting target");
            });

            // In place: the value Read exposes must equal the authoritative set; the required id is on both sides.
            Case(failures, "in-place canonical/slot divergence refuses and preserves the file", () =>
            {
                var root = Root(scratch, "inPlaceConflict");
                Slot(root, 'a', 1, present: true, new[] { first, second });
                connections.Save(root, new[] { first });
                var bytes = File.ReadAllBytes(ListFile(root));
                Expect(Refuses(() => connections.Verify(root, root, new[] { first })),
                    "An in-place canonical list that hides a slot connection was accepted (required id was present)");
                Expect(File.ReadAllBytes(ListFile(root)).AsSpan().SequenceEqual(bytes), "An in-place refusal changed the occupied file");
            });
            Case(failures, "in-place equal canonical and slot accepted", () =>
            {
                var root = Root(scratch, "inPlaceEqual");
                Slot(root, 'a', 1, present: true, new[] { first });
                connections.Save(root, new[] { first });
                Expect(connections.Verify(root, root, new[] { first }) is null && connections.Read(root).SequenceEqual(new[] { first }),
                    "An equal in-place canonical list and slot were refused or disagreed with Read");
            });

            Case(failures, "blank legacy line keeps the authoritative slot and empty save wins", () =>
            {
                var root = Root(scratch, "blankLegacy");
                Slot(root, 'a', 1, present: true, new[] { first });
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), "\n");
                Expect(connections.Verify(root, root, new[] { first }) is null && connections.Read(root).SequenceEqual(new[] { first }),
                    "A blank legacy line was treated as our empty list instead of the authoritative slot");
                connections.Save(root, Array.Empty<string>());
                Expect(connections.Read(root).Count == 0, "Saving an empty list did not override the old slots");
            });

            // BC8/R47: a distinct target must carry the authoritative set; an append target its exact bytes.
            Case(failures, "distinct target that drops an authoritative connection refuses", () =>
            {
                var source = Root(scratch, "slotSource");
                Slot(source, 'a', 2, present: true, new[] { first, second });
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(source))!);
                File.WriteAllText(ListFile(source), LegacySet(first)); // stale append beside the authoritative slot
                var target = Root(scratch, "staleCopy");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(target))!);
                File.Copy(ListFile(source), ListFile(target));
                Expect(Refuses(() => connections.Verify(source, target, new[] { first })),
                    "A distinct target that dropped an authoritative connection was accepted (required id was present)");
            });
            Case(failures, "byte-identical distinct append target accepted", () =>
            {
                var source = Root(scratch, "appendSource");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(source))!);
                File.WriteAllText(ListFile(source), LegacySet(first));
                var target = Root(scratch, "identical");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(target))!);
                File.Copy(ListFile(source), ListFile(target));
                Expect(connections.Verify(source, target, new[] { first }) is null,
                    "A byte-identical distinct append target with the same set was refused");
            });
            Case(failures, "distinct append target with different bytes refuses", () =>
            {
                var source = Root(scratch, "appendSourceBytes");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(source))!);
                File.WriteAllText(ListFile(source), LegacySet(first));
                var target = Root(scratch, "sameIdsOtherBytes");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(target))!);
                File.WriteAllText(ListFile(target), LegacyRecovery("evidence.json") + LegacySet(first));
                Expect(Refuses(() => connections.Verify(source, target, new[] { first })),
                    "A distinct append target with different bytes was accepted");
            });
            Case(failures, "append source with a same-set canonical target keeps both sides", () =>
            {
                var source = Root(scratch, "r47Source");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(source))!);
                File.WriteAllText(ListFile(source), LegacyRecovery("evidence.json") + LegacySet(first));
                var target = Root(scratch, "r47Target");
                connections.Save(target, new[] { first });
                var sourceBytes = File.ReadAllBytes(ListFile(source));
                var targetBytes = File.ReadAllBytes(ListFile(target));
                Expect(connections.Verify(source, target, new[] { first }) is null,
                    "A same-set canonical target with different bytes was refused instead of kept under R47");
                Expect(File.ReadAllBytes(ListFile(source)).AsSpan().SequenceEqual(sourceBytes) &&
                       File.ReadAllBytes(ListFile(target)).AsSpan().SequenceEqual(targetBytes),
                    "R47 verification overwrote one side");
            });

            Case(failures, "absent distinct target copies the authoritative append bytes", () =>
            {
                var source = Root(scratch, "copySource");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(source))!);
                File.WriteAllText(ListFile(source), LegacyRecovery("evidence.json") + LegacySet(first));
                var target = Root(scratch, "copyTarget");
                Expect(connections.Verify(source, target, new[] { first }) is not null &&
                       File.ReadAllBytes(ListFile(target)).AsSpan().SequenceEqual(File.ReadAllBytes(ListFile(source))),
                    "An absent distinct target was not byte-copied from the authoritative append log");
                Expect(connections.Read(target).SequenceEqual(new[] { first }), "The byte-copied list did not read back");
            });
            Case(failures, "in-place stale log keeps the newer slot", () =>
            {
                var root = Root(scratch, "inPlace");
                Slot(root, 'a', 2, present: true, new[] { first });
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                var stale = LegacySet(second);
                File.WriteAllText(ListFile(root), stale);
                Expect(connections.Verify(root, root, new[] { first }) is null && File.ReadAllText(ListFile(root)) == stale &&
                       connections.Read(root).SequenceEqual(new[] { first }),
                    "An in-place upgrade refused the slot, overwrote the log or disagreed with Read");
            });
            Case(failures, "in-place zero-byte legacy keeps the slot", () =>
            {
                var root = Root(scratch, "zeroLegacy");
                Slot(root, 'a', 1, present: true, new[] { first });
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllBytes(ListFile(root), Array.Empty<byte>());
                Expect(connections.Verify(root, root, new[] { first }) is null && connections.Read(root).SequenceEqual(new[] { first }),
                    "A zero-byte legacy log with a valid slot was refused or disagreed with Read");
            });
            Case(failures, "in-place slot-only root", () =>
            {
                var root = Root(scratch, "slotOnly");
                Slot(root, 'a', 1, present: true, new[] { first, second });
                Expect(connections.Verify(root, root, new[] { first, second }) is null &&
                       connections.Read(root).SequenceEqual(Sorted(first, second)),
                    "An in-place slot-only root did not expose its authoritative set");
            });
            Case(failures, "in-place legacy-only root", () =>
            {
                var root = Root(scratch, "legacyOnly");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first));
                Expect(connections.Verify(root, root, new[] { first }) is null && connections.Read(root).SequenceEqual(new[] { first }),
                    "An in-place legacy-only root was refused or republished");
            });
            Case(failures, "distinct canonical target set equality", () =>
            {
                var equalSource = Root(scratch, "equalSlotSource");
                Slot(equalSource, 'a', 1, present: true, new[] { first, second });
                var target = Root(scratch, "distinctTarget");
                connections.Save(target, new[] { first, second });
                Expect(connections.Verify(equalSource, target, new[] { first }) is null,
                    "An equal canonical distinct target was refused");
                var differentSource = Root(scratch, "differentSlotSource");
                Slot(differentSource, 'a', 1, present: true, new[] { first });
                Expect(Refuses(() => connections.Verify(differentSource, target, new[] { first })),
                    "A different canonical distinct target was accepted (required id was present)");
            });
            Case(failures, "newer slot deletion overrides the legacy log", () =>
            {
                var root = Root(scratch, "tomb");
                Slot(root, 'a', 3, present: false, Array.Empty<string>());
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySet(first));
                Expect(Refuses(() => connections.Verify(root, Root(scratch, "tombTarget"), new[] { first })),
                    "A newer slot deletion was overridden by the stale legacy log");
            });
            Case(failures, "slot generation selection", () =>
            {
                var root = Root(scratch, "generations");
                Slot(root, 'a', 1, present: true, new[] { second });
                Slot(root, 'b', 2, present: true, new[] { first });
                Expect(connections.Verify(root, Root(scratch, "generationTarget"), new[] { first }) is not null,
                    "The higher-generation slot was not selected");
                var invalid = Root(scratch, "invalidSlots");
                Slot(invalid, 'a', 1, present: true, new[] { first }, hash: new string('0', 64));
                Slot(invalid, 'b', 2, present: true, new[] { second }, hash: new string('0', 64));
                Expect(Refuses(() => connections.Verify(invalid, Root(scratch, "invalidTarget"), Array.Empty<string>())),
                    "Invalid successor slots were accepted");
            });
            Case(failures, "oversized state refuses before use", () =>
            {
                var slot = Root(scratch, "oversizedSlot");
                Directory.CreateDirectory(Path.Combine(slot, "state"));
                File.WriteAllText(Path.Combine(slot, "state", "dropbox-connection-a.json"), new string('x', MaximumStateBytes + 1));
                Expect(Refuses(() => connections.Read(slot)), "An oversized successor slot was read");
                var append = Root(scratch, "oversizedAppend");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(append))!);
                File.WriteAllText(ListFile(append), new string('x', MaximumStateBytes + 1));
                Expect(Refuses(() => connections.Read(append)), "An oversized append log was read");
            });
            Case(failures, "stage id is unique against an abandoned same-second stage", () =>
            {
                var source = Root(scratch, "stageSource");
                Slot(source, 'a', 1, present: true, new[] { first });
                var target = Root(scratch, "stageTarget");
                var state = Path.Combine(target, "state");
                Directory.CreateDirectory(state);
                var abandoned = new List<string>();
                var now = DateTime.UtcNow;
                var abandonedBytes = Encoding.UTF8.GetBytes("abandoned");
                for (var offset = -1; offset <= 3; offset++)
                {
                    var stamp = now.AddSeconds(offset).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                    var path = ListFile(target) + "." + stamp + ".new";
                    File.WriteAllBytes(path, abandonedBytes);
                    abandoned.Add(path);
                }
                var published = false;
                var collision = string.Empty;
                try { published = connections.Verify(source, target, new[] { first }) is not null; }
                catch (Exception error) { collision = " (" + error.Message + ")"; }
                Expect(failures, published, "stage: An abandoned same-second stage blocked the next attempt" + collision);
                Expect(failures, connections.Read(target).SequenceEqual(new[] { first }), "stage: The published target membership is missing");
                Expect(failures, abandoned.All(file => File.Exists(file) &&
                        File.ReadAllBytes(file).AsSpan().SequenceEqual(abandonedBytes)),
                    "stage: An abandoned prior attempt's stage was removed or changed");
            });
            Case(failures, "predecessor append validation and valid prefix", () =>
            {
                var root = Root(scratch, "legacy");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                File.WriteAllText(ListFile(root), LegacySetWithBinding(BindingJson(null, "acct-derived")));
                Expect(connections.Read(root).SequenceEqual(new[] { Connections.CreateConnectionId("acct-derived") }),
                    "A blank connection id with an account id was not normalized to its derived id");
                File.WriteAllText(ListFile(root), LegacySetWithBinding(BindingJson("  " + first + "  ", "")));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "An explicit connection id was not trimmed");
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1.5, "set", BindingJson(null, "acct-other"), null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A non-Int32 schema threw instead of keeping the prefix");
                File.WriteAllText(ListFile(root), LegacySet(first) + "\r\n" + LegacySet(second));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "An empty-after-CR record was skipped instead of stopping");
                File.WriteAllText(ListFile(root), "not-json\n" + LegacySet(second));
                Expect(connections.Read(root).Count == 0, "A malformed first record did not stop with an empty prefix");
                File.WriteAllText(ListFile(root), LegacySet(first).TrimEnd('\n'));
                Expect(connections.Read(root).Count == 0, "An unterminated append record was accepted");
                File.WriteAllText(ListFile(root), LegacySet(first) + LegacyRecord(1, "append", null, null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "An invalid operation did not stop the append scan");
            });
            Case(failures, "append record ceiling counts only applied records", () =>
            {
                var root = Root(scratch, "ceiling");
                Directory.CreateDirectory(Path.GetDirectoryName(ListFile(root))!);
                var hundred = new StringBuilder();
                for (var index = 0; index < 100; index++) hundred.Append(LegacySet(first));
                File.WriteAllText(ListFile(root), hundred + LegacyRecord(1, "append", null, null));
                Expect(connections.Read(root).SequenceEqual(new[] { first }), "A bad record after the applied ceiling did not stop cleanly");
                var over = new StringBuilder();
                for (var index = 0; index < 101; index++) over.Append(LegacySet(first));
                File.WriteAllText(ListFile(root), over.ToString());
                Expect(Refuses(() => connections.Read(root)), "An append log over its applied-record ceiling was accepted");
            });
            Case(failures, "absent lists and required connections", () =>
            {
                var empty = Root(scratch, "empty");
                Expect(connections.Verify(Root(scratch, "none"), empty, Array.Empty<string>()) is null && !File.Exists(ListFile(empty)),
                    "Both lists absent created a list");
                Expect(Refuses(() => connections.Verify(Root(scratch, "none"), Root(scratch, "required"), new[] { first })),
                    "A required connection with both lists absent was accepted");
            });
            Case(failures, "canonical save and read", () =>
            {
                var root = Root(scratch, "canonical");
                connections.Save(root, new[] { second, first, second });
                Expect(connections.Read(root).SequenceEqual(Sorted(first, second)), "A saved canonical list was not read back");
                connections.Save(root, Array.Empty<string>());
                Expect(connections.Read(root).Count == 0, "An empty canonical list was not read back");
                Expect(Refuses(() => connections.Save(root, new[] { "not-a-connection-id" })), "An invalid id was saved");
            });
            Case(failures, "stable connection id", () =>
            {
                const string account = "acct-fixture";
                var created = Connections.CreateConnectionId(account);
                var expected = "connection-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dropbox\n" + account))).ToLowerInvariant()[..24];
                Expect(created == expected && Connections.CreateConnectionId(account) == created, "Connection id is not the stable account hash");
                Expect(Connections.CreateConnectionId("acct-other") != created, "Different accounts share a connection id");
            });
        }
        finally { Directory.Delete(scratch, true); }
        if (failures.Count > 0) throw new InvalidDataException(string.Join(" | ", failures));
        return Task.CompletedTask;
    }

    private static void Case(List<string> failures, string name, Action run)
    {
        try { run(); }
        catch (Exception error) { failures.Add(name + ": " + error.Message); }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    // Records the failure without aborting the case, so independent postconditions each report their own outcome.
    private static void Expect(List<string> failures, bool condition, string message)
    {
        if (!condition) failures.Add(message);
    }

    private static bool Refuses(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
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
            ["ConnectedUtc"] = "01 May 2024",
            ["AdditionalConnections"] = Array.Empty<object>()
        });

    private static string ScopesWithNull(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-log",
            ["Scopes"] = new object?[] { null },
            ["AdditionalConnections"] = Array.Empty<object>()
        });

    private static string NullAdditional(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-log",
            ["AdditionalConnections"] = new object?[] { null }
        });

    private static string NullRetained(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-log",
            ["RetainedConnections"] = new object?[] { null }
        });

    private static readonly string ValidDate = JsonSerializer.SerializeToElement(DateTimeOffset.UnixEpoch).GetString()!;

    private static string DatedBinding(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-dated",
            ["ConnectedUtc"] = ValidDate,
            ["AdditionalConnections"] = Array.Empty<object>()
        });

    private static string TypedRetainedBinding(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-typed",
            ["RetainedConnections"] = new[] { new Dictionary<string, object?> { ["ConnectionId"] = 123 } }
        });

    private static string RetainedUnknownCapability(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-typed",
            ["RetainedConnections"] = new[]
            {
                ProviderRecord(new Dictionary<string, object?>
                {
                    ["ContentRead"] = true,
                    ["FutureCapability"] = "ignored"
                })
            }
        });

    private static string RetainedWrongKnownCapability(string explicitId) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ConnectionId"] = explicitId,
            ["AccountId"] = "acct-typed",
            ["RetainedConnections"] = new[] { ProviderRecord(new Dictionary<string, object?> { ["ContentRead"] = "yes" }) }
        });

    private static Dictionary<string, object?> ProviderRecord(Dictionary<string, object?> capabilities) => new()
    {
        ["ConnectionId"] = "retained-fixture",
        ["Provider"] = "dropbox",
        ["AccountId"] = "retained-account",
        ["RootNamespaceId"] = "retained-root",
        ["HomeNamespaceId"] = "retained-home",
        ["DisplayName"] = "Retained",
        ["Email"] = "retained@example.invalid",
        ["AccountType"] = "basic",
        ["CredentialReference"] = "retained-target",
        ["IsActive"] = false,
        ["Capabilities"] = capabilities
    };

    /// <summary>Writes one 0.3.95 alternating slot with the predecessor's record shape and payload hash.</summary>
    private static string Slot(string root, char slot, long generation, bool present, string[] ids, string? hash = null) =>
        SlotWithBinding(root, slot, generation, present ? BindingJson(ids[0], "acct-slot", ids.Skip(1).ToArray()) : null,
            present: present, hash: hash);

    private static string SlotWithBinding(string root, char slot, long generation, string? bindingJson,
        bool present = true, string? hash = null) =>
        WriteSlot(root, slot, generation, new Dictionary<string, object?>
        {
            ["Present"] = present,
            ["Value"] = present ? JsonSerializer.Deserialize<JsonElement>(bindingJson!) : null
        }, hash);

    private static string SlotTombstoneWithBinding(string root, char slot, long generation, string bindingJson) =>
        WriteSlot(root, slot, generation, new Dictionary<string, object?>
        {
            ["Present"] = false,
            ["Value"] = JsonSerializer.Deserialize<JsonElement>(bindingJson)
        }, hash: null);

    private static string WriteSlot(string root, char slot, long generation, Dictionary<string, object?> value, string? hash)
    {
        var state = Path.Combine(root, "state");
        Directory.CreateDirectory(state);
        var element = JsonSerializer.SerializeToElement(value);
        var path = Path.Combine(state, "dropbox-connection-" + slot + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SchemaVersion"] = 1,
            ["Generation"] = generation,
            ["Value"] = element,
            ["PayloadSha256"] = hash ?? Hex(SHA256.HashData(Encoding.UTF8.GetBytes(element.GetRawText())))
        }));
        return path;
    }

    private static string LegacySet(params string[] ids) =>
        LegacySetWithBinding(BindingJson(ids[0], "acct-log", ids.Skip(1).ToArray()));

    private static string LegacySetWithBinding(string bindingJson) => LegacyRecord(1, "set", bindingJson, null);

    private static string LegacyDelete(string? suppliedValueJson = null) =>
        LegacyRecord(1, "delete", suppliedValueJson, null, payload: "delete");

    private static string LegacyRecovery(string? evidenceFile) =>
        LegacyRecord(1, "recovery", null, evidenceFile is null ? "{}" : RecoveryJson(evidenceFile));

    private static string RecoveryJson(string evidenceFile, bool wrongByteCount = false, bool nullAncillary = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["EvidenceFile"] = evidenceFile,
            ["OriginalSha256"] = nullAncillary ? null : new string('0', 64),
            ["ValidPrefixByteLength"] = 0,
            ["BadByteLength"] = wrongByteCount ? "12" : 0,
            ["BadSha256"] = nullAncillary ? null : new string('0', 64),
            ["Outcome"] = nullAncillary ? null : "tombstone"
        });

    private static string LegacyRecord(object schema, string operation, string? valueJson, string? recoveryJson,
        string? payload = null)
    {
        // The predecessor hashes the operation payload: a set's Value, a recovery's Recovery, the word for a delete.
        var hashed = payload
            ?? (operation == "set" ? valueJson : operation == "recovery" ? recoveryJson : null)
            ?? "delete";
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
