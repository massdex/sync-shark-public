namespace Massdex.SyncShark.V2;

/// <summary>Why a pair layout is refused: each stable key and its wording live here and nowhere else.</summary>
public static class PairAdmissionReasons
{
    /// <summary>A root is exactly a root of another pair: the same path on one store, or the same object.</summary>
    public static readonly NoticeReason SameRoot = new("pair-same-root",
        "Another folder pair already uses this exact folder. Choose a different folder.");

    /// <summary>The pair contains another pair on one side and lies inside that pair on another side.</summary>
    public static readonly NoticeReason CrossedNesting = new("pair-crossed-nesting",
        "This pair's folders overlap another pair's folders in opposite directions: on one side this pair " +
        "contains the other pair, on the other side it lies inside it. Choose folders that nest the same way " +
        "on both sides, or that do not overlap.");

    /// <summary>A compared root has no usable path, so the relation cannot be decided.</summary>
    public static readonly NoticeReason Unresolved = new("pair-unresolved",
        "A folder of this pair or of another pair has no full, plain path, so Sync Shark cannot tell whether " +
        "the two pairs overlap.");

    /// <summary>A root path given for a new pair is not a full, plain path.</summary>
    public static readonly NoticeReason UnsafePath = new("pair-root-path",
        "The folder must be a full path without '.' or '..' parts.");
}
