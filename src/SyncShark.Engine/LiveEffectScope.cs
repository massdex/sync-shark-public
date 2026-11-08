namespace Massdex.SyncShark.Engine;

/// <summary>One affected subtree in a live effect footprint: its side, root path and whether descendants are
/// locked. ExactEvidence narrows acquisition checks for a move without narrowing its locks or held-issue checks.
/// Subtrees are represented by roots, not materialized descendant lists.</summary>
public sealed record LiveEffectScope(Side Side, string NativePath, bool IncludesDescendants, bool ExactEvidence = false);
