namespace Massdex.SyncShark.Engine;

/// <summary>Configured roots, first-sync policy and outstanding mapped comparisons for one pair.</summary>
public sealed record PairConfig(string PairId, string Name, string LocalRoot, string DropboxRoot,
    string ConnectionId, IReadOnlyList<string> IgnoreRules, IReadOnlyList<Exclusion> Exclusions,
    FirstRunPolicy FirstRun, bool Enabled, DriveEvidence? Drive, RootIdentity? Roots = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("owed_scopes")] IReadOnlyList<Scope>? OwedScopes = null);

/// <summary>The two endpoint footprints compared together after a scope change.</summary>
public sealed record Scope(string LocalPath, string DropboxPath);
