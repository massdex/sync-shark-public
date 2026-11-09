namespace Massdex.SyncShark.Engine.Executor;

/// <summary>The one settlement decision for a Dropbox move whose effect is unknown, from its single by-ID
/// observation; Retry and Repair both ask it before any other work on that Failure. It makes no provider call: a
/// failed, malformed or cancelled read reaches no decision because the caller's read throws first.</summary>
internal static class UnknownMoveCheck
{
    /// <summary>What the observation proves about the intended move.</summary>
    internal enum Outcome
    {
        /// <summary>The expected identity is at the destination with its final spelling: adopt, never resend.</summary>
        Adopt,
        /// <summary>It is still at the source (including the source spelling of a case-only move): re-plan from
        /// current facts, never replay the old request.</summary>
        ReplanFromSource,
        /// <summary>It is at the recorded case-rename intermediate: plan only the remaining hop.</summary>
        RemainingHop,
        /// <summary>It is elsewhere inside the pair: re-plan from that location.</summary>
        ReplanFromObserved,
        /// <summary>It is outside the pair: keep the responsibility unresolved pending an explicit exit.</summary>
        KeepUnresolved,
        /// <summary>The identity is proven absent: keep the unknown Failure.</summary>
        KeepUnknown
    }

    /// <summary>Ancestors compare by the path key; a case-only distinction compares the last component's exact
    /// spelling, which Dropbox guarantees for path_display.</summary>
    internal static Outcome Decide(IssueSubject.UnknownMove move, IdentityLocation? observed)
    {
        ArgumentNullException.ThrowIfNull(move);
        if (observed is null) return Outcome.KeepUnknown;
        var path = observed.Entry.Path;
        // The by-ID reader keeps the native path as the entry path only when the location is outside the pair.
        if (string.Equals(path, observed.NativePath, StringComparison.Ordinal)) return Outcome.KeepUnresolved;
        if (PathNames.Same(path, move.Destination) && PathNames.SameName(path, move.Destination)) return Outcome.Adopt;
        if (move.Intermediate is { } intermediate && PathNames.Same(path, intermediate)) return Outcome.RemainingHop;
        return PathNames.Same(path, move.Source) ? Outcome.ReplanFromSource : Outcome.ReplanFromObserved;
    }
}
