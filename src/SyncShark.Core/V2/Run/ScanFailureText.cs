namespace Massdex.SyncShark.V2;

/// <summary>
/// Turns an internal scan diagnostic into text a user can act on.
/// The raw diagnostic is kept in the durable retry's <c>LastError</c> for support and for the next
/// run's forensics; a notice shows this explanation instead. Reasons that are already written for
/// the user (a Defender or quarantine description, a name refusal) are passed through unchanged.
/// </summary>
internal static class ScanFailureText
{
    // No diagnostic was reported, so state only what is certainly true.
    internal const string Unknown =
        "This folder could not be completely read. Check that it still exists and that your account can read it.";

    /// <summary>The plain-language explanation for a raw scan diagnostic (never empty).</summary>
    internal static string Describe(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return Unknown;
        // The folder itself could not be opened or listed, including the targeted re-read path.
        if (Has(diagnostic, "targeted re-read") || Has(diagnostic, "targeted refresh") ||
            Has(diagnostic, "guarded refresh") || Has(diagnostic, "root lease") ||
            Has(diagnostic, "could not be opened"))
            return "Sync Shark could not open this folder to check what changed. " +
                "Make sure it still exists and that your account can read it.";
        // One physical file under several hard-linked names: the journal cannot name the changed one.
        if (Has(diagnostic, "hard-link") || Has(diagnostic, "hard link"))
            return "A file here has more than one hard-linked name, so Sync Shark could not tell which name changed. " +
                "Remove the extra hard link, or exclude the name you do not need.";
        // The NTFS change journal could not be trusted for this range.
        if (Has(diagnostic, "journal") || Has(diagnostic, "cursor") || Has(diagnostic, "USN"))
            return "Windows' change journal was unavailable or was overwritten, so this folder needs a fresh check. " +
                "Make sure the change journal is enabled and large enough, or let Sync Shark enlarge it.";
        // A bounded pass could not resolve every name.
        if (Has(diagnostic, "path limit") || Has(diagnostic, "too many"))
            return "Too many changes happened at once to check this folder in one pass. " +
                "Exclude a folder you do not need to sync, or retry later.";
        // Most other reasons are already phrased for the user (quarantine, name, placeholder rules).
        return diagnostic;
    }

    private static bool Has(string text, string phrase) =>
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
