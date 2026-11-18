namespace Massdex.SyncShark.Engine;

/// <summary>The LegacyUnverified pending note: a recognized old-shape note preserved byte-for-byte for manual handling only.
/// RawToken identifies those exact bytes; Path, StagePath and Source are unverified locators, so no stage ownership is proven
/// and no losing proof, saved binding or phase exists here.</summary>
public sealed record LegacyPending(string RawJson, string RawToken, string Path,
    string StagePath, Entry Source) : PendingRead;
