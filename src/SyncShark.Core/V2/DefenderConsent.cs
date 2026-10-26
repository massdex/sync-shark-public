namespace Massdex.SyncShark.V2;

/// <summary>The one vocabulary for a deferred deletion's state; the store keeps its integer value.</summary>
public enum DefenderConsent
{
    /// <summary>No evidence and no choice yet: deferred for a bounded time, then offered in a review.</summary>
    Pending = 0,

    /// <summary>The user chose Proceed for its reviewed batch, or released it from Keep held.</summary>
    Proceed = 1,

    /// <summary>The user chose Keep held: listed in the standing notice, never pruned while the entity lives.</summary>
    Keep = 2,

    /// <summary>Released by evidence, not by a user (complete folder history, or inactive Defender after the bound).</summary>
    Released = 3,
}
