namespace Massdex.SyncShark.Engine;

/// <summary>A proven native location of an identity: the native domain and path it occupies and the
/// entry observed there. Returned intact for a location outside the current pair, so shared scope
/// ownership can resolve it.</summary>
public sealed record IdentityLocation(string Domain, string NativePath, Entry Entry);
