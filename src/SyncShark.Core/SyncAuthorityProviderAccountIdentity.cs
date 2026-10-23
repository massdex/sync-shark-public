using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark;

// The one shared place that derives a provider-account authority identity and maps an explicit
// endpoint namespace onto the retained authority account row. Host, UI, CLI and planner code never
// branch on a provider to decide an identity or a namespace; they call this helper.
//
// Two legacy derivations are frozen at their recorded bytes because rows already written with them
// must stay valid and are never rewritten, prefixed or recomputed. A future registered cloud provider
// uses its own versioned provider-qualified tuple here rather than a caller-side conditional.
internal static class SyncAuthorityProviderAccountIdentity
{
    // The grandfathered role literal shared by both recorded legacy derivations.
    private const string LegacyAccountRole = "provider-account";

    // A registered cloud provider with no recorded legacy derivation uses this versioned role, so
    // its tuple can never collide with a legacy row or with another provider's tuple.
    private const string ProviderQualifiedAccountRole = "provider-account-v1";

    /// <summary>
    /// The exact retained authority-account id for a registered provider and its real identity:
    /// the provider account, the account id and the root/home namespace the row actually carries.
    /// An unregistered provider is refused rather than given a derived id.
    /// </summary>
    internal static string DeriveId(string provider, string accountId, string rootNamespaceId,
        string homeNamespaceId)
    {
        var definition = RequireRegisteredCloudProvider(provider);
        RequireValue(accountId, nameof(accountId));
        RequireValue(rootNamespaceId, nameof(rootNamespaceId));
        RequireValue(homeNamespaceId, nameof(homeNamespaceId));
        // Dropbox: provider-account(account, root, home). This is the tuple every retained Dropbox
        // authority row already carries and its bytes must not change.
        if (definition.Kind == SyncEndpointKind.Dropbox)
            return Legacy(LegacyAccountRole, accountId, rootNamespaceId, homeNamespaceId);
        // Google Drive: provider-account(google-drive, permissionId, accountKey). The account column
        // holds the native permission id and the root/home columns hold the account key, so the
        // provider literal is part of the tuple exactly as the existing rows were derived.
        if (definition.Kind == SyncEndpointKind.GoogleDrive)
            return Legacy(LegacyAccountRole, SyncProviders.GoogleDrive, accountId, rootNamespaceId);
        return Legacy(ProviderQualifiedAccountRole, definition.ProviderId!, accountId, rootNamespaceId,
            homeNamespaceId);
    }

    /// <summary>
    /// The exact namespace an explicit endpoint must present for this retained authority row.
    /// A Dropbox row is separated by its retained root namespace. A Google Drive row is separated by
    /// its native permission (account) id while that row's retained root namespace stays the account
    /// key. A registered provider with no settled mapping is refused rather than guessed.
    /// </summary>
    internal static string RetainedEndpointNamespace(string provider, string accountId,
        string rootNamespaceId, string homeNamespaceId)
    {
        var definition = RequireRegisteredCloudProvider(provider);
        RequireValue(accountId, nameof(accountId));
        RequireValue(rootNamespaceId, nameof(rootNamespaceId));
        RequireValue(homeNamespaceId, nameof(homeNamespaceId));
        if (definition.Kind == SyncEndpointKind.Dropbox) return rootNamespaceId;
        if (definition.Kind == SyncEndpointKind.GoogleDrive) return accountId;
        throw new NotSupportedException(
            "This registered provider has no retained endpoint-namespace mapping, so the endpoint is refused.");
    }

    /// <summary>
    /// True only when the presented endpoint namespace is exactly the retained one. There is no
    /// label, e-mail, path, substring or wildcard alias that can satisfy this comparison.
    /// </summary>
    internal static bool MatchesEndpointNamespace(string provider, string accountId, string rootNamespaceId,
        string homeNamespaceId, string? endpointNamespaceId) =>
        !string.IsNullOrWhiteSpace(endpointNamespaceId) &&
        string.Equals(RetainedEndpointNamespace(provider, accountId, rootNamespaceId, homeNamespaceId),
            endpointNamespaceId.Trim(), StringComparison.Ordinal);

    /// <summary>The refusing form: returns the retained namespace or throws without a mutation.</summary>
    internal static string RequireEndpointNamespace(string provider, string accountId,
        string rootNamespaceId, string homeNamespaceId, string? endpointNamespaceId)
    {
        if (!MatchesEndpointNamespace(provider, accountId, rootNamespaceId, homeNamespaceId, endpointNamespaceId))
            throw new InvalidOperationException(
                "The endpoint namespace does not match the retained provider-account identity; the endpoint is refused.");
        return RetainedEndpointNamespace(provider, accountId, rootNamespaceId, homeNamespaceId);
    }

    /// <summary>
    /// Validates one complete retained provider account against an explicit endpoint: the retained id
    /// must be exactly this provider's derivation over that row's own account/root/home tuple, and the
    /// endpoint namespace must be exactly the retained one. Nothing here reads a display label,
    /// e-mail, path or any other presentational value.
    /// </summary>
    internal static void RequireRetainedEndpointMatches(string provider, string retainedProviderAccountId,
        string accountId, string rootNamespaceId, string homeNamespaceId, string? endpointNamespaceId)
    {
        RequireValue(retainedProviderAccountId, nameof(retainedProviderAccountId));
        if (!string.Equals(DeriveId(provider, accountId, rootNamespaceId, homeNamespaceId),
                retainedProviderAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The retained provider-account id is not this provider's derivation over its own identity tuple.");
        RequireEndpointNamespace(provider, accountId, rootNamespaceId, homeNamespaceId, endpointNamespaceId);
    }

    /// <summary>True when a retained row id is exactly this provider's derivation over its own tuple.</summary>
    internal static bool RetainedRowMatchesDerivation(string provider, string? retainedProviderAccountId,
        string accountId, string rootNamespaceId, string homeNamespaceId)
    {
        if (string.IsNullOrWhiteSpace(retainedProviderAccountId)) return false;
        try
        {
            return string.Equals(DeriveId(provider, accountId, rootNamespaceId, homeNamespaceId),
                retainedProviderAccountId, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// The legacy Google classification used by the migration. It is true only for a row whose
    /// retained provider-account id is exactly the grandfathered Google derivation over that row's
    /// own account and root, with home equal to root. A display label, e-mail, path, substring or
    /// guess can never classify a row.
    /// </summary>
    internal static bool IsLegacyGoogleProviderAccount(string? providerAccountId, string? accountId,
        string? rootNamespaceId, string? homeNamespaceId) =>
        !string.IsNullOrWhiteSpace(providerAccountId) && !string.IsNullOrWhiteSpace(accountId) &&
        !string.IsNullOrWhiteSpace(rootNamespaceId) && !string.IsNullOrWhiteSpace(homeNamespaceId) &&
        string.Equals(homeNamespaceId, rootNamespaceId, StringComparison.Ordinal) &&
        string.Equals(providerAccountId,
            Legacy(LegacyAccountRole, SyncProviders.GoogleDrive, accountId, rootNamespaceId),
            StringComparison.Ordinal);

    // The recorded role-newline-parts SHA-256 first-32 form. This is the exact primitive the retained
    // rows were derived with; it is deliberately not generalised.
    private static string Legacy(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant()[..32];

    private static SyncEndpointProviderDefinition RequireRegisteredCloudProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException(
                "A provider-account identity requires a provider literal.", nameof(provider));
        if (!SyncEndpointProviders.TryGet(provider, out var definition) || definition!.ProviderId is null)
            throw new ArgumentException(
                "This provider is not a registered cloud provider, so no authority identity is derived for it.",
                nameof(provider));
        return definition;
    }

    private static void RequireValue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A provider-account identity requires a non-empty value.", name);
    }
}
