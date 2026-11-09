using System.Text.Json;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks the drive-change detector, coherent checkpoint merging and unsigned persistence.</summary>
public static class DriveEvidenceChecks
{
    public static void Run()
    {
        DetectorAndMergeMatrix();
        MergeCases();
        UnsignedJournalRoundTrip();
    }

    private static void DetectorAndMergeMatrix()
    {
        uint?[] serials = [null, 0, 1, uint.MaxValue];
        ulong?[] journals = [null, 0, 1, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue];
        long?[] positions = [null, long.MinValue, -1, 0, 1, long.MaxValue - 1, long.MaxValue];
        var readings = (from serial in serials
                        from journal in journals
                        from position in positions
                        select new DriveEvidence(serial, journal, position)).ToArray();
        foreach (var saved in readings.Prepend<DriveEvidence?>(null))
        {
            foreach (var current in readings)
            {
                TextKey? expected = null;
                if (saved?.Serial is not null && current.Serial is not null)
                {
                    if (saved.Serial != current.Serial) expected = TextKey.DriveChanged;
                    else if (saved.JournalId is not null && saved.JournalId == current.JournalId &&
                        saved.NextUsn is >= 0 && current.NextUsn is >= 0 && current.NextUsn < saved.NextUsn)
                        expected = TextKey.DriveRestored;
                }
                var context = $"saved={saved}, current={current}";
                Check.Require(DriveEvidence.Change(saved, current) == expected, "drive detection: " + context);
                var merged = DriveEvidence.After(saved, current);
                if (expected is not null)
                {
                    Check.Require(merged == saved, "detected changes never reseed the checkpoint: " + context);
                    continue;
                }
                if (merged != saved)
                {
                    Check.Require(merged?.NextUsn is null or >= 0,
                        "new observations never save negative signed USNs: " + context);
                }
                if (merged?.NextUsn is not null)
                {
                    Check.Require(
                        (merged.JournalId == saved?.JournalId && merged.NextUsn == saved?.NextUsn) ||
                        (merged.JournalId == current.JournalId && merged.NextUsn == current.NextUsn),
                        "a saved journal and position come from one reading: " + context);
                }
                if (saved?.JournalId is not null && saved.NextUsn is >= 0 &&
                    merged?.JournalId == saved.JournalId)
                {
                    Check.Require(merged.NextUsn >= saved.NextUsn,
                        "merging never loses progress within the same journal: " + context);
                }
                if (merged?.Serial is not null)
                {
                    Check.Require(merged.Serial == saved?.Serial || merged.Serial == current.Serial,
                        "merging never invents a volume serial: " + context);
                }
                if (current.Serial is null && merged != saved && merged?.JournalId != saved?.JournalId)
                    Check.Require(merged?.Serial is null,
                        "an old serial never joins a different serial-less journal: " + context);
            }
        }
    }

    private static void MergeCases()
    {
        var saved = new DriveEvidence(1, 10, 100);
        foreach (var serial in new uint?[] { null, saved.Serial })
        {
            foreach (var journal in new ulong?[] { null, saved.JournalId })
            {
                foreach (var position in new long?[] { null, saved.NextUsn })
                {
                    var partial = new DriveEvidence(serial, journal, position);
                    Check.Require(DriveEvidence.After(saved, partial) == saved,
                        "missing fields retain the usable coherent checkpoint: " + partial);
                }
            }
        }
        var cases = new (string Name, DriveEvidence? Saved, DriveEvidence Current, DriveEvidence? Expected)[]
        {
            ("no evidence seeds an empty record", null, new(null, null, null), new(null, null, null)),
            ("serial-less new journal cannot replace saved evidence", saved, new(null, 20, 200), saved),
            ("incomplete new journal cannot replace saved evidence", saved, new(1, 20, null), saved),
            ("serial-less same journal advances", saved, new(null, 10, 101), new(1, 10, 101)),
            ("serial-less rollback retains progress", saved, new(null, 10, 99), saved),
            ("negative seed position is unavailable", null, new(1, 10, -1), new(1, null, null)),
            ("empty baseline sanitizes negative seed", new(null, null, null),
                new(null, 10, long.MinValue), new(null, null, null)),
            ("seed without a serial", null, new(null, 10, 100), new(null, 10, 100)),
            ("seed without a journal", null, new(1, null, 100), new(1, null, null)),
            ("partial baseline is not reseeded", new(null, 10, null),
                new(1, null, null), new(null, 10, null)),
            ("invalid saved position is not rewritten by unavailable evidence", new(1, 10, -1),
                new(null, null, null), new(1, 10, -1)),
            ("complete reading replaces invalid saved position", new(1, 10, -1), saved, saved),
            ("seed serial", null, new(1, null, null), new(1, null, null)),
            ("seed complete zero checkpoint", null, new(0, 0, 0), new(0, 0, 0)),
            ("seed unsigned boundaries", null, new(uint.MaxValue, ulong.MaxValue, long.MaxValue),
                new(uint.MaxValue, ulong.MaxValue, long.MaxValue)),
            ("forward checkpoint", saved, new(1, 10, 101), new(1, 10, 101)),
            ("rollback retains checkpoint", saved, new(1, 10, 99), saved),
            ("rollback to zero retains checkpoint", saved, new(1, 10, 0), saved),
            ("negative position retains checkpoint", saved, new(1, 10, -1), saved),
            ("minimum signed position retains checkpoint", saved, new(1, 10, long.MinValue), saved),
            ("replacement retains checkpoint", saved, new(2, 20, 200), saved),
            ("replacement without journal retains checkpoint", saved, new(2, null, null), saved),
            ("replacement takes precedence over rollback", saved, new(2, 10, 0), saved),
            ("recreated journal can start lower", saved, new(1, 20, 0), new(1, 20, 0)),
            ("recreated journal can advance", saved, new(1, 20, 200), new(1, 20, 200)),
            ("journal IDs are identities not ordered counters", new(1, ulong.MaxValue, 100),
                new(1, 0, 0), new(1, 0, 0)),
            ("serial-only baseline gains a checkpoint", new(1, null, null), saved, saved),
            ("empty baseline gains evidence", new(null, null, null), saved, saved),
            ("maximum position is retained", new(1, ulong.MaxValue, long.MaxValue),
                new(1, ulong.MaxValue, long.MaxValue), new(1, ulong.MaxValue, long.MaxValue)),
            ("maximum position rollback is detected", new(1, ulong.MaxValue, long.MaxValue),
                new(1, ulong.MaxValue, long.MaxValue - 1), new(1, ulong.MaxValue, long.MaxValue)),
        };
        foreach (var test in cases)
        {
            Check.Require(DriveEvidence.After(test.Saved, test.Current) == test.Expected,
                "drive evidence merge: " + test.Name);
        }
    }

    private static void UnsignedJournalRoundTrip()
    {
        ulong[] journals = [0, 1, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue];
        foreach (var journal in journals)
        {
            var evidence = new DriveEvidence(uint.MaxValue, journal, long.MaxValue);
            var pair = new PairConfig("pair-a", "Pair A", "/Local", "/Remote", "connection-a",
                [], [], FirstRunPolicy.NewestWins, true, evidence);
            var restored = JsonSerializer.Deserialize<PairConfig>(JsonSerializer.Serialize(pair));
            Check.Require(restored?.Drive == evidence, "pair JSON preserves unsigned journal ID " + journal);
        }
    }
}
