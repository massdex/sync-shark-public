namespace Massdex.SyncShark.V2;

/// <summary>
/// Turns an internal scan diagnostic into text a user can act on.
/// The raw diagnostic is kept in the durable retry's <c>LastError</c> for support and for the next
/// run's forensics; a notice shows this explanation instead. Every explanation states a cause and a
/// fix. Reasons that are already written for the user (a Defender or quarantine description, a name
/// refusal, or the online-only observation message whose exact prefix closes its notices) are passed
/// through; unrecognised ones get the generic fix appended unless they already name a remedy.
/// </summary>
internal static class ScanFailureText
{
    // No diagnostic was reported, so state only what is certainly true.
    internal const string Unknown =
        "This folder could not be completely read. Check that it still exists and that your account can read it.";

    // Appended to a diagnostic that is already written for the user but names no remedy. Kept neutral:
    // it deliberately does not guess at the cause, unlike the targeted texts above.
    internal const string Fix = " Resolve the cause above, then choose Retry.";

    /// <summary>The plain-language explanation for a raw scan diagnostic (never empty, always with a fix).</summary>
    internal static string Describe(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return Unknown;
        // The online-only observation message is matched by this exact prefix when its notices are
        // made due or closed, so it must stay unchanged. It already states a cause and a fix.
        if (diagnostic == OwnedPointerProof.ObservationMessage) return diagnostic;
        // The folder itself could not be opened, listed or enumerated: the targeted re-read path, the
        // scanner's blocked-folder decision, and the journal name resolver's parent listing.
        if (Has(diagnostic, "targeted re-read") || Has(diagnostic, "targeted refresh") ||
            Has(diagnostic, "guarded refresh") || Has(diagnostic, "root lease") ||
            Has(diagnostic, "could not be opened") || Has(diagnostic, "could not be enumerated") ||
            Has(diagnostic, "listing failed"))
            return "Sync Shark could not open this folder to check what changed. " +
                "Make sure it still exists and that your account can read it. " +
                "Restore read access to this folder (or exclude it), then choose Retry.";
        // One physical file under several hard-linked names: the journal cannot name the changed one.
        if (Has(diagnostic, "hard-link") || Has(diagnostic, "hard link"))
            return "A file here has more than one hard-linked name, so Sync Shark could not tell which name changed. " +
                "Remove the extra hard link, or exclude the name you do not need.";
        // The NTFS change journal could not be trusted for this range. Capacity enlargement is offered
        // by its own change-log notice, not by this sync-retry notice.
        if (Has(diagnostic, "journal") || Has(diagnostic, "cursor") || Has(diagnostic, "USN"))
            return "Windows' change journal was unavailable or was overwritten, so this folder needs a fresh check. " +
                "Make sure the change journal is enabled and large enough. If it is too small, the separate " +
                "change-log capacity notice offers to enlarge it.";
        // A bounded pass could not resolve every name.
        if (Has(diagnostic, "path limit") || Has(diagnostic, "too many"))
            return "Too many changes happened at once to check this folder in one pass. " +
                "Exclude a folder you do not need to sync, or retry later.";
        // Most other reasons are already phrased for the user (quarantine, name, placeholder rules).
        // Append the generic remedy so no notice is left with a cause but no fix -- but only when the
        // reason names no remedy of its own. A reason that already says "then choose Retry" (or
        // "Retry") must not gain a second, competing remedy. [review-gapscope-2 A1]
        if (Has(diagnostic, "then choose Retry") || Has(diagnostic, "Retry"))
            return diagnostic;
        return diagnostic + Fix;
    }

    private static bool Has(string text, string phrase) =>
        text.Contains(phrase, StringComparison.OrdinalIgnoreCase);
}
