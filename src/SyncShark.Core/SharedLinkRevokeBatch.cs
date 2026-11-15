using System.Text.Json;

namespace Massdex.SyncShark;

public sealed record SharedLinkRevokeTarget(string AccountId, string NamespaceId, string Url,
    string? PermissionId = null, string? RevisionFingerprint = null);
public sealed record SharedLinkRevokeResult(SharedLinkRevokeTarget Target, string Status, string Message)
{
    public bool Retryable => Status is "rejected" or "not-attempted";
}

// One bounded selection shared by UI and CLI. Never expands selection or retries an effect.
public static class SharedLinkRevokeBatch
{
    public const int MaximumTargets = 100;
    private enum Outcome { Revoked, NotAttempted, EffectUncertain, Rejected, Unknown }

    // CLI admission measures these same receipts before any provider effect. Keep descriptions and
    // retryability in one place so a future provider cannot silently invalidate that upper bound.
    private static SharedLinkRevokeResult Receipt(SharedLinkRevokeTarget target, Outcome outcome) => outcome switch
    {
        Outcome.Revoked => new(target, "revoked", "The provider confirmed revocation."),
        Outcome.NotAttempted => new(target, "not-attempted", "Cancelled before this link was attempted."),
        Outcome.EffectUncertain => new(target, "uncertain", "Not confirmed. Refresh and check this link before any further action."),
        Outcome.Rejected => new(target, "rejected", "The link could not be revoked. Refresh the link and account permissions before retrying."),
        _ => new(target, "uncertain", "The result is unknown. Check this exact link before any further action.")
    };

    internal static IReadOnlyList<SharedLinkRevokeResult> PossibleReceipts(SharedLinkRevokeTarget target) =>
        Enum.GetValues<Outcome>().Select(outcome => Receipt(target, outcome)).ToArray();

    internal static SharedLinkRevokeResult MaximumReceipt(SharedLinkRevokeTarget target) =>
        PossibleReceipts(target).MaxBy(result => JsonSerializer.Serialize(result).Length)!;

    public static IReadOnlyList<SharedLinkRevokeTarget> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array ||
            document.RootElement.EnumerateObject().Any(p => p.Name != "links"))
            throw new InvalidDataException("Supply settings with an exact links array.");
        var result = new List<SharedLinkRevokeTarget>();
        foreach (var item in links.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(p => p.Name is not ("accountId" or "namespaceId" or "url" or "permissionId" or "revisionFingerprint")))
                throw new InvalidDataException("Each link requires accountId, namespaceId and url, with optional permissionId and revisionFingerprint.");
            string Read(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()! : throw new InvalidDataException("Each link requires accountId, namespaceId and url.");
            result.Add(new(Read("accountId"), Read("namespaceId"), Read("url"),
                item.TryGetProperty("permissionId", out _) ? Read("permissionId") : null,
                item.TryGetProperty("revisionFingerprint", out _) ? Read("revisionFingerprint") : null));
        }
        Validate(result);
        return result;
    }

    public static void Validate(IReadOnlyList<SharedLinkRevokeTarget> targets)
    {
        if (targets.Count is < 1 or > MaximumTargets) throw new InvalidDataException("Select between 1 and 100 exact links.");
        var seen = new HashSet<(string, string, string, string?)>();
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target.AccountId) || string.IsNullOrWhiteSpace(target.NamespaceId) ||
                target.AccountId.Length > 200 || target.NamespaceId.Length > 200 ||
                !seen.Add((target.AccountId, target.NamespaceId, target.Url, target.PermissionId)))
                throw new InvalidDataException("Every selected link must have a unique exact account, namespace and URL.");
            AccountSharingLookup.ValidateUrl(target.Url);
            if (target.PermissionId is { } id && (id.Length is < 1 or > 512 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
                throw new InvalidDataException("The exact permission identity is invalid.");
            if (target.RevisionFingerprint is { } revision && (revision.Length != 64 || revision.Any(c => !Uri.IsHexDigit(c))))
                throw new InvalidDataException("The observed permission revision is invalid.");
        }
    }

    public static async Task<IReadOnlyList<SharedLinkRevokeResult>> ExecuteAsync(
        IReadOnlyList<SharedLinkRevokeTarget> targets, Func<SharedLinkRevokeTarget, CancellationToken, Task> revoke,
        CancellationToken cancellationToken = default)
    {
        var selection = targets.ToArray();
        Validate(selection); // Reject the entire malformed selection before the first effect.
        var results = new List<SharedLinkRevokeResult>(selection.Length);
        foreach (var target in selection)
        {
            if (cancellationToken.IsCancellationRequested)
            { results.Add(Receipt(target, Outcome.NotAttempted)); continue; }
            try { await revoke(target, cancellationToken); results.Add(Receipt(target, Outcome.Revoked)); }
            catch (Exception error) when (error is DropboxEffectUncertainException or GoogleDriveEffectUncertainException)
            { results.Add(Receipt(target, Outcome.EffectUncertain)); }
            catch (Exception error) when (error is DropboxApiException or GoogleDriveApiException or InvalidOperationException or InvalidDataException or HttpRequestException)
            { results.Add(Receipt(target, Outcome.Rejected)); }
            catch (Exception)
            { results.Add(Receipt(target, Outcome.Unknown)); }
        }
        return results;
    }
}

public sealed partial class SyncAuthorityRuntimeHost
{
    public Task<IReadOnlyList<SharedLinkRevokeResult>> RevokeSharedLinksAsync(
        IReadOnlyList<SharedLinkRevokeTarget> targets, CancellationToken cancellationToken = default) =>
        SharedLinkRevokeBatch.ExecuteAsync(targets, async (target, token) =>
        {
            var client = await CreateAccountSharingClientAsync(target.AccountId, token);
            var current = await AccountSharingLookup.ExactAsync(client, target.NamespaceId, target.Url, target.PermissionId, token);
            if (target.RevisionFingerprint is not null && target.RevisionFingerprint != current.RevisionFingerprint)
                throw new InvalidOperationException("This permission changed after selection. Refresh before revoking it.");
            await client.RevokeLinkAsync(current, token);
        }, cancellationToken);
}
