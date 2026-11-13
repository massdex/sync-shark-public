namespace Massdex.SyncShark.Engine;

/// <summary>One affected subtree in a live effect footprint: its side, root path and whether descendants are
/// locked. ExactEvidence narrows acquisition checks for a move without narrowing its locks or held-issue checks.
/// FolderPlan admits descendant evidence already covered under a non-plain folder deletion's saved folder, only so
/// that deferred folder can enter its survivor plan (B-153); later evidence still refuses.
/// Subtrees are represented by roots, not materialized descendant lists.</summary>
public sealed record LiveEffectScope(Side Side, string NativePath, bool IncludesDescendants, bool ExactEvidence = false,
    bool FolderPlan = false)
{
    /// <summary>A footprint carries native paths for locking; work, holds and pending are keyed pair-relative. A scope
    /// outside the pair's root covers nothing the pair owns, so it refuses.</summary>
    internal string KeyIn(PairConfig pair) => PathNames.KeyFor(Side, RelativeIn(pair) ??
        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged));

    internal string? RelativeIn(PairConfig pair) => Side == Side.Local
        ? PathNames.RelativeUnderLocalRoot(pair.LocalRoot, NativePath)
        : PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(pair.DropboxRoot), NativePath);
}
