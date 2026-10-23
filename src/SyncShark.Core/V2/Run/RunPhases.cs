using Massdex.SyncShark;

namespace Massdex.SyncShark.V2;

/// <summary>The user-facing phase names a run reports through <see cref="RunProgress"/>.</summary>
public static class RunPhases
{
    /// <summary>Resolving effects that an earlier run started but never committed.</summary>
    public const string Reconcile = "Checking earlier changes";

    /// <summary>Comparing the two sides' inventories into one plan.</summary>
    public const string Comparing = "Comparing contents";

    /// <summary>Reading the content hashes of the files a plan will copy (before they are copied).</summary>
    public const string Hashing = "Checking file contents";

    /// <summary>Publishing proofs completed by the independent indexing run, without reading content.</summary>
    public const string Verifying = "Publishing index changes";

    /// <summary>Choosing the effects for this run.</summary>
    public const string Planning = "Planning";

    /// <summary>Applying the planned effects.</summary>
    public const string Applying = "Applying";

    /// <summary>Advancing cursors and pruning after the effects.</summary>
    public const string Finishing = "Finishing";

    /// <summary>The reading phase for one side, named by its provider.</summary>
    public static string Reading(RootDef root) => "Reading " + SyncProviders.DisplayName(root.Provider);
}
