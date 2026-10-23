using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

// Google Drive provider identity, configuration and capability surface.
//
// Deliberately mirrors the Dropbox provider split so both providers can coexist without a shared
// mutable active token or a shared cursor. Only the registered public client identity is bound here;
// the client secret and account grants stay in protected Windows custody. This client is currently
// in Google's External/Testing audience; binding it is not a claim of public app verification.
public static class GoogleDriveProviderConfiguration
{
    public const string PendingClientId = "PENDING_GOOGLE_DRIVE_REGISTRATION";
    public const string UnregisteredCredentialReference =
        "massdex/account-access/v1/google-drive/sync-shark/unregistered";

    // Restricted scope. The consent requirement and the restricted-scope verification obligation are
    // recorded in code rather than hidden in prose.
    public const string DriveScope = "https://www.googleapis.com/auth/drive";
    public const string DriveFileScope = "https://www.googleapis.com/auth/drive.file";
    public const string DriveMetadataReadonlyScope = "https://www.googleapis.com/auth/drive.metadata.readonly";

    // Folder-wide synchronisation of pre-existing content requires the restricted drive scope.
    // drive.file grants per-file access only to content this app created or the user opened with it.
    public static readonly IReadOnlyList<string> RequiredScopes = [DriveScope];

    public static readonly string RestrictedScopeDisclosure =
        "Google Drive folder synchronisation requests the restricted drive scope. That scope requires " +
        "restricted-scope OAuth app verification, and a security assessment when restricted data is " +
        "stored or transmitted. Narrower per-file access does not inventory a pre-existing folder and " +
        "is not presented as an equivalent capability.";

    // Public Desktop client identity verified against Custodian's 2026-09-17 replacement receipt.
    // The test-only seam preserves explicit unbound and controlled-endpoint coverage.
    public const string ProductionClientId =
        "274352988258-ri267556sjtkhiv59nicslu0ol2n3rt5.apps.googleusercontent.com";
    internal static string? RegisteredClientIdForTest { get; set; }

    public static string RegisteredClientId => RegisteredClientIdForTest ?? ProductionClientId;

    public static bool IsLiveProviderBound => !RegisteredClientId.Equals(PendingClientId, StringComparison.Ordinal);

    public static bool HasRequiredGrant(IReadOnlyList<string>? scopes) => scopes is not null &&
        scopes.Any(scope => string.Equals(scope, DriveScope, StringComparison.Ordinal));

    public static void ValidateConnection(GoogleDriveConnectionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (!IsLiveProviderBound)
            throw new InvalidOperationException(
                "No Google Cloud OAuth client is registered for this build. Google Drive connection is " +
                "unavailable until the owning task registers the client and records its identity.");
        if (!string.Equals(binding.ClientId, RegisteredClientId, StringComparison.Ordinal))
            throw new InvalidOperationException("The Google Drive OAuth client does not match this build.");
        if (!HasRequiredGrant(binding.Scopes))
            throw new InvalidOperationException(
                "The Google Drive grant does not include the required drive scope. " +
                RestrictedScopeDisclosure);
        if (string.IsNullOrWhiteSpace(binding.AccountKey) || string.IsNullOrWhiteSpace(binding.PermissionId))
            throw new InvalidOperationException("The Google Drive account identity is incomplete.");
        if (string.IsNullOrWhiteSpace(binding.CredentialReference))
            throw new InvalidOperationException("The Google Drive connection has no credential reference.");
    }
}

// One Google account, one credential set, one identity. There is no namespace pair and no global token.
public sealed record GoogleDriveConnectionBinding(
    string ClientId,
    string AccountKey,
    string PermissionId,
    string Email,
    string DisplayName,
    IReadOnlyList<string> Scopes,
    string CredentialReference = "",
    string ConnectionId = "",
    DateTimeOffset? ConnectedUtc = null,
    DateTimeOffset? LastVerifiedUtc = null,
    string StorageQuotaJson = "",
    IReadOnlyList<ProviderConnectionRecord>? RetainedConnections = null,
    IReadOnlyList<GoogleDriveConnectionBinding>? AdditionalConnections = null,
    bool SharedDrivesEnabled = false)
{
    public static string CreateAccountKey(string permissionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("google-drive|" + permissionId)))
            .ToLowerInvariant()[..24];
    }

    public static string CreateConnectionId(string accountKey) => "gdrive-connection-" + accountKey;

    public GoogleDriveConnectionBinding Normalize()
    {
        var key = string.IsNullOrWhiteSpace(AccountKey) ? CreateAccountKey(PermissionId) : AccountKey.Trim();
        return this with
        {
            ClientId = ClientId?.Trim() ?? string.Empty,
            AccountKey = key,
            PermissionId = PermissionId?.Trim() ?? string.Empty,
            Email = Email?.Trim() ?? string.Empty,
            DisplayName = DisplayName?.Trim() ?? string.Empty,
            Scopes = Scopes?.Order(StringComparer.Ordinal).ToArray() ?? [],
            CredentialReference = string.IsNullOrWhiteSpace(CredentialReference)
                ? GoogleDriveProviderConfiguration.UnregisteredCredentialReference
                : CredentialReference.Trim(),
            ConnectionId = string.IsNullOrWhiteSpace(ConnectionId) ? CreateConnectionId(key) : ConnectionId.Trim(),
            StorageQuotaJson = StorageQuotaJson ?? string.Empty
        };
    }

    public ProviderConnectionRecord ToProviderRecord(bool active = true)
    {
        var normalized = Normalize();
        return new ProviderConnectionRecord(normalized.ConnectionId, SyncProviders.GoogleDrive,
            normalized.AccountKey, normalized.PermissionId, normalized.PermissionId,
            normalized.DisplayName, normalized.Email, "google", normalized.CredentialReference, active,
            GoogleDriveProviderProfiles.CapabilityFlags(normalized));
    }
}

// Provider identifiers. The Dropbox literal is unchanged so existing stored state stays valid.
public static class SyncProviders
{
    public const string Local = "local";
    public const string Dropbox = "dropbox";
    public const string GoogleDrive = "google-drive";

    // Provider IDs are storage/protocol identities. User-facing names live here,
    // so UI and CLI consumers never infer a provider from translated/display text.
    public static string DisplayName(string? provider) =>
        Normalize(provider) == Local ? SyncEndpointProviders.Get(SyncEndpointKind.Local).DisplayName :
        SyncEndpointProviders.TryGet(Normalize(provider), out var definition)
            ? definition!.DisplayName : "Cloud storage";

    public static bool IsKnown(string? provider) => SyncEndpointProviders.All.Any(definition =>
        definition.ProviderId is not null && string.Equals(provider, definition.ProviderId, StringComparison.Ordinal));

    public static string Normalize(string? provider) => string.IsNullOrWhiteSpace(provider)
        ? Dropbox
        : provider.Trim().ToLowerInvariant();
}

public interface IGoogleDriveAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class GoogleDriveApiException(HttpStatusCode statusCode, string safeReason, string? providerReason = null)
    : HttpRequestException(safeReason, null, statusCode)
{
    public string SafeReason { get; } = safeReason;
    // F4: one optional, finite provider cause. Anything outside the closed allowlist below,
    // including any provider-authored text, is discarded here so no raw string travels further.
    public string? ProviderReason { get; } = GoogleDriveSharingReason.Normalize(providerReason);
}

// F4/F5: the closed set of Google sharing failure reasons this product recognises. An unrecognised
// value is never retained, classified, logged or shown, so no provider string can reach the CLI or
// the UI. Rate-limited reasons classify as rate-limited even when Google answers with HTTP 403.
internal static class GoogleDriveSharingReason
{
    private const int MaximumReasonEntries = 64;
    private static readonly string[] ReasonArrays = ["errors", "details"];

    private static readonly HashSet<string> Recognised = new(StringComparer.Ordinal)
    {
        "insufficientFilePermissions", "appNotAuthorizedToFile", "domainPolicy", "rateLimitExceeded",
        "userRateLimitExceeded", "dailyLimitExceeded", "sharingRateLimitExceeded", "forbidden",
        "insufficientPermissions", "ACCESS_TOKEN_SCOPE_INSUFFICIENT"
    };

    // Google quota and rate vocabulary: these stay rate-limited rather than access-restricted.
    internal static readonly HashSet<string> RateLimited = new(StringComparer.Ordinal)
    { "rateLimitExceeded", "userRateLimitExceeded", "dailyLimitExceeded", "sharingRateLimitExceeded" };

    // The one recognised reason meaning this account may not read the item sharing details.
    internal const string SharingDetailsUnavailable = "insufficientFilePermissions";

    internal static string? Normalize(string? value) => value is not null && Recognised.Contains(value) ? value : null;

    // F4: read a single finite reason from an already bounded, in-memory non-success body. Only
    // error.errors[].reason and error.details[].reason are inspected; error.message and every other
    // provider string are never read. Missing, malformed, oversized, unrecognised or ambiguous
    // input returns null so the caller keeps its existing safe status and message.
    internal static string? FromBody(byte[] body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return null;
            string? found = null;
            foreach (var name in ReasonArrays)
            {
                // An absent array is normal; a present but non-array or oversized one is malformed.
                if (!error.TryGetProperty(name, out var array)) continue;
                if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > MaximumReasonEntries) return null;
                foreach (var entry in array.EnumerateArray())
                {
                    // G2: a non-object element is malformed, and an errors element without a reason
                    // is an unreadable cause in the one array where Google always states a reason.
                    if (entry.ValueKind != JsonValueKind.Object) return null;
                    if (!entry.TryGetProperty("reason", out var reason))
                    {
                        // A typed details object carrying no reason describes unrelated provider
                        // detail, so it is skipped rather than treated as a cause.
                        if (string.Equals(name, "details", StringComparison.Ordinal)) continue;
                        return null;
                    }
                    if (reason.ValueKind != JsonValueKind.String) return null;
                    // G2: a cause is only ever claimed when every reason element in the body is one
                    // of the recognized finite values; one unrecognized or non-string reason makes the
                    // whole body ambiguous and yields no definite classification.
                    if (Normalize(reason.GetString()) is not { } value) return null;
                    if (found is not null && !string.Equals(found, value, StringComparison.Ordinal)) return null;
                    found = value;
                }
            }
            return found;
        }
        catch (JsonException) { return null; }
    }
}

// A refused effect that the product must surface as an actionable blocker rather than resolving it
// silently. Every instance retains the stable ids it refused to choose between.
public sealed class GoogleDriveBlockedException(string safeReason,
    IReadOnlyList<GoogleDrivePathAmbiguity>? ambiguities = null) : IOException(safeReason)
{
    public IReadOnlyList<GoogleDrivePathAmbiguity> Ambiguities { get; } = ambiguities ?? [];
}

public sealed class GoogleDriveEffectUncertainException(string message, Exception? innerException = null)
    : IOException(message, innerException);

// One derived relative path that resolved to more than one stable Drive object id, or to an entry that
// Windows cannot represent. The ids are retained so the caller can present an actionable choice.
public sealed record GoogleDrivePathAmbiguity(
    string RelativePath,
    string Reason,
    IReadOnlyList<string> FileIds,
    IReadOnlyList<string> Names);

public sealed record GoogleDriveCapabilities(
    bool StableObjectIds,
    bool DeltaChanges,
    bool NativeMove,
    bool TrashRecovery,
    bool Revisions,
    bool ContentDigests,
    bool ResumableUpload,
    bool RangeDownload,
    bool SharedDrives,
    bool NativeDocuments,
    bool Shortcuts,
    bool AtomicConditionalWrites,
    bool FileRequests,
    bool AccountRewind)
{
    public static readonly GoogleDriveCapabilities Baseline = new(
        StableObjectIds: true,
        DeltaChanges: true,
        NativeMove: true,
        TrashRecovery: true,
        Revisions: true,
        ContentDigests: true,
        ResumableUpload: true,
        RangeDownload: true,
        SharedDrives: false,
        NativeDocuments: true,
        Shortcuts: true,
        // Actual Google current PUT/PATCH and stale412/no-retry acceptance: 2026-09-17.
        AtomicConditionalWrites: true,
        FileRequests: false,
        AccountRewind: false);

    public GoogleDriveCapabilities WithSharedDrives(bool enabled) => this with { SharedDrives = enabled };

    public IReadOnlyList<string> UnsupportedUserOutcomes()
    {
        var unsupported = new List<string>();
        if (!FileRequests)
            unsupported.Add("File requests are not available for Google Drive accounts.");
        if (!AccountRewind)
            unsupported.Add("Account-wide rewind is not available for Google Drive accounts.");
        if (!AtomicConditionalWrites)
            unsupported.Add(
                "Conditional Google Drive writes have not yet passed live provider verification in this " +
                "build. Existing-item updates and destructive metadata changes are refused.");
        if (!SharedDrives)
            unsupported.Add("Shared drives are not enabled for this connection.");
        return unsupported;
    }
}

public static class GoogleDriveProviderProfiles
{
    public static ProviderCapabilityFlags CapabilityFlags(GoogleDriveConnectionBinding binding,
        bool? sharedDrives = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new ProviderCapabilityFlags(
            ContentRead: true,
            ContentWrite: true,
            MetadataRead: true,
            MetadataWrite: true,
            // Report the product surface, not permissions Google's API could support.
            SharingRead: true,
            SharingWrite: true,
            StableObjectIds: true,
            DeltaCursor: true,
            NativeMove: true,
            Revisions: true,
            DeletedItems: true,
            ProgrammaticRewind: false,
            FileRequestsRead: false,
            FileRequestsWrite: false,
            NativeDocuments: true,
            Shortcuts: true,
            SharedDrives: sharedDrives ?? binding.SharedDrivesEnabled,
            ConditionalWrites: GoogleDriveCapabilities.Baseline.AtomicConditionalWrites);
    }

    public static GoogleDriveCapabilities Capabilities(bool sharedDrives = false) =>
        GoogleDriveCapabilities.Baseline.WithSharedDrives(sharedDrives);
}
