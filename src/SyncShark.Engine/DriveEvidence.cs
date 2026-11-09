namespace Massdex.SyncShark.Engine;

/// <summary>Per-pair volume and journal evidence for detecting drive replacement or restoration.</summary>
public sealed record DriveEvidence(uint? Serial, ulong? JournalId, long? NextUsn)
{
    /// <summary>Identifies drive replacement or restoration from comparable evidence.</summary>
    public static TextKey? Change(DriveEvidence? saved, DriveEvidence current)
    {
        if (saved is not { Serial: { } serial } || current.Serial is not { } currentSerial)
            return null;
        if (serial != currentSerial)
            return TextKey.DriveChanged;
        return WentBack(saved, current) ? TextKey.DriveRestored : null;
    }

    /// <summary>Retains coherent evidence without accepting a detected drive change.</summary>
    public static DriveEvidence? After(DriveEvidence? saved, DriveEvidence finishing)
    {
        if (Change(saved, finishing) is not null)
            return saved;
        if (saved is null or { Serial: null, JournalId: null, NextUsn: null })
            return finishing.JournalId is null || finishing.NextUsn is < 0
                ? finishing with { JournalId = null, NextUsn = null } : finishing;

        // Keep journal tuples intact; without a serial, only the same journal can advance saved evidence.
        if (finishing.JournalId is not { } journal || finishing.NextUsn is not >= 0 ||
            (finishing.Serial is null && saved.JournalId != journal))
            return saved;
        if (WentBack(saved, finishing))
            return saved;
        return finishing.Serial is null && saved.Serial is { } serial
            ? finishing with { Serial = serial } : finishing;
    }

    private static bool WentBack(DriveEvidence saved, DriveEvidence current) =>
        saved.JournalId is { } journal && current.JournalId == journal &&
        saved.NextUsn is >= 0 && current.NextUsn is >= 0 && current.NextUsn < saved.NextUsn;
}
