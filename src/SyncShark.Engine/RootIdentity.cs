using System.Globalization;

namespace Massdex.SyncShark.Engine;

/// <summary>One pair's persisted binding to its exact configured local and Dropbox roots, with both root identities.</summary>
public sealed record RootIdentity(
    string LocalRoot,
    string DropboxRoot,
    string ConnectionId,
    DirectoryIdentity Local,
    DropboxRootIdentity Dropbox)
{
    /// <summary>The Local journal domain for this pair: the volume serial as the adapters report it. A method, so the
    /// persisted binding keeps exactly its constructor members.</summary>
    internal string LocalVolumeKey() => Local.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);

    /// <summary>Checks the supported identity representation without observing or approving a root.</summary>
    public bool IsValid() => Local is not null && Dropbox is not null &&
        (Local.FileIdHigh is not null || Local.VolumeSerialNumber <= uint.MaxValue) &&
        !string.IsNullOrEmpty(Dropbox.AccountId) && !string.IsNullOrEmpty(Dropbox.RootNamespaceId) &&
        !string.IsNullOrEmpty(Dropbox.HomeNamespaceId) && !string.IsNullOrEmpty(Dropbox.FolderId);
}

/// <summary>Stable identity of one local directory: volume serial number, file id and the optional high half of a wide file id.
/// A null high half is basic format (volume serial within UInt32); a high half of zero is still a wide identity.</summary>
public sealed record DirectoryIdentity(ulong VolumeSerialNumber, ulong FileId, ulong? FileIdHigh);

/// <summary>Dropbox root identity: account, root and home namespaces, and the configured folder's ID.</summary>
public sealed record DropboxRootIdentity(string AccountId, string RootNamespaceId,
    string HomeNamespaceId, string FolderId);

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
