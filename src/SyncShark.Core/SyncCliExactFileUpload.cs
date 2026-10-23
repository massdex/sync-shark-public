using System.Globalization;
using System.Security.Cryptography;

namespace Massdex.SyncShark;

// The one exact-file Dropbox upload capability. It reuses the existing protected account selection, the
// existing Dropbox provider and the existing CLI progress route, and it creates no sync pair, inventory,
// plan, engine, client, token, scope or per-pair switch.

/// <summary>One validated exact-file upload request, already shape-checked by the registry.</summary>
public sealed record SyncExactFileUploadRequest(string AccountId, string SourcePath, string Destination,
    long ExpectedBytes, string ExpectedSha256);

/// <summary>
/// The compact JSON receipt for one exact-file upload attempt. The outcome vocabulary is exact: success,
/// refused (the command refused before any effect attempt), known-no-effect (transmitted and definitively
/// not created) and unknown (transmitted and the result was not proven).
/// </summary>
public sealed record SyncCliExactFileUploadData(string Command, string AccountId, string AccountReference,
    string NamespaceId, string SourcePath, long SourceBytes, string SourceSha256, string Destination,
    bool EffectAttempted, string Outcome, string? FailureReason, string? SafeNextStep, DateTimeOffset StartedUtc,
    DateTimeOffset? VerifiedUtc, DateTimeOffset? EffectUtc, DateTimeOffset? CompletedUtc, string? CommittedId,
    string? CommittedRevision, long? CommittedSize, string? CommittedContentHash);

internal static class SyncExactFileUploadRules
{
    public const int MaximumPathCharacters = 4096;
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(300);

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    // ns:<namespace-id>//<absolute path>. The namespace is stated explicitly, so a destination in any
    // other namespace can never be silently resolved against the retained account.
    public static bool TryParseDestination(string? value, out string namespaceId, out string exactPath)
    {
        namespaceId = string.Empty;
        exactPath = string.Empty;
        if (value is null || value.Length is 0 or > MaximumPathCharacters ||
            !value.StartsWith("ns:", StringComparison.Ordinal)) return false;
        var separator = value.IndexOf('/', 3);
        if (separator < 4) return false;
        var candidateNamespace = value[3..separator];
        foreach (var character in candidateNamespace)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')) return false;
        var remainder = value[separator..];
        if (!remainder.StartsWith("//", StringComparison.Ordinal)) return false;
        try
        {
            var normalized = PilotPath.NormalizeDropboxRoot(remainder[1..]);
            if (normalized.Length <= 1 || normalized.Length > MaximumPathCharacters) return false;
            namespaceId = candidateNamespace;
            exactPath = normalized;
            return true;
        }
        catch (ArgumentException) { return false; }
    }

    // The exact parent folder and the single validated leaf the create-only commit adds below it.
    public static void SplitExactDestination(string exactPath, out string parentPath, out string leaf)
    {
        var normalized = PilotPath.NormalizeDropboxRoot(exactPath).TrimEnd('/');
        var index = normalized.LastIndexOf('/');
        parentPath = index <= 0 ? "/" : normalized[..index];
        leaf = PilotPath.NormalizeRelative(normalized[(index + 1)..]);
    }

    // One bounded pass over the retained handle for the caller's SHA-256 and the provider's own Dropbox
    // content hash. A read or hash failure is a pre-transmission failure.
    public static async Task<(string Sha256, string DropboxContentHash)> HashSourceAsync(Stream source,
        CancellationToken cancellationToken)
    {
        source.Position = 0;
        using var sha = SHA256.Create();
        var blockHasher = new DropboxContentHash.BlockHasher();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            sha.TransformBlock(buffer, 0, read, null, 0);
            blockHasher.Append(buffer.AsSpan(0, read));
        }
        sha.TransformFinalBlock([], 0, 0);
        source.Position = 0;
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), blockHasher.Finish());
    }
}

public abstract partial class SyncCliOperationsBase
{
    // Test seam for the single 300-second bound. It changes no other decision and never weakens the
    // ordinary provider, account, destination or confirmation rules.
    internal static TimeSpan ExactFileUploadBudget { get; set; } = SyncExactFileUploadRules.DefaultBudget;

    public async Task<SyncCliExactFileUploadData> UploadExactFileAsync(SyncExactFileUploadRequest request,
        Action<string> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);
        var startedUtc = DateTimeOffset.UtcNow;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ExactFileUploadBudget);
        var token = budget.Token;
        var effectAttempted = false;
        var outcome = "refused";
        string? failureReason = null;
        string? safeNextStep = null;
        string? accountReference = null;
        var namespaceId = "unknown";
        var sourceBytes = request.ExpectedBytes;
        var sourceSha = SyncExactFileUploadRules.IsSha256(request.ExpectedSha256)
            ? request.ExpectedSha256 : string.Empty;
        DateTimeOffset? verifiedUtc = null;
        DateTimeOffset? effectUtc = null;
        string? committedId = null;
        string? committedRevision = null;
        string? committedContentHash = null;
        long? committedSize = null;

        string FullSourcePath()
        {
            try { return Path.GetFullPath(request.SourcePath); }
            catch (Exception) { return request.SourcePath; }
        }

        SyncCliExactFileUploadData Receipt() => new("files upload", request.AccountId,
            accountReference ?? request.AccountId, namespaceId, FullSourcePath(), sourceBytes, sourceSha,
            request.Destination, effectAttempted, outcome, failureReason, safeNextStep, startedUtc, verifiedUtc,
            effectUtc, DateTimeOffset.UtcNow, committedId, committedRevision, committedSize, committedContentHash);

        SyncCliExactFileUploadException Refuse(string code, SyncCliExitCode exitCode, string reason, string next)
        {
            outcome = effectAttempted ? "unknown" : "refused";
            failureReason = effectAttempted
                ? "The create-only upload was attempted, but its result was not proven: " + reason
                : reason;
            safeNextStep = effectAttempted
                ? "Inspect the exact destination before doing anything else; do not retry an unproven upload."
                : next;
            return new SyncCliExactFileUploadException(code, failureReason + " " + safeNextStep, exitCode, Receipt());
        }

        // The refusal text is never null: it is either the provider's own reason or a bounded default.
        string Reason() => failureReason ?? "The exact-file upload did not complete.";
        string Next() => safeNextStep ?? "Resolve the stated condition and run the exact command again.";

        try
        {
            // The exact retained account through the existing protected selection. A missing, inactive or
            // foreign-provider account refuses here, before any provider request.
            var binding = RequireConnection(request.AccountId);
            accountReference = string.IsNullOrWhiteSpace(binding.DisplayName) ? binding.ConnectionId : binding.DisplayName;
            if (!string.Equals(binding.Provider, SyncProviders.Dropbox, StringComparison.Ordinal))
                throw Refuse("unsupported", SyncCliExitCode.NeedsReview,
                    "The exact account '" + request.AccountId + "' is a " + SyncProviders.DisplayName(binding.Provider) +
                    " account, and this command uploads only through the existing Dropbox provider.",
                    "Use the exact Dropbox connection id; run: " + SyncCli.ExecutableName + " --cli accounts list");
            var retainedNamespace = binding.RootNamespaceId;
            if (string.IsNullOrWhiteSpace(retainedNamespace))
                throw Refuse("missing_target", SyncCliExitCode.MissingTarget,
                    "The retained account '" + request.AccountId + "' has no verified Dropbox root namespace.",
                    "Reconnect that exact account, then run the command again.");
            if (!SyncExactFileUploadRules.TryParseDestination(request.Destination, out var requestedNamespace,
                    out var exactPath))
                throw Refuse("usage", SyncCliExitCode.Usage,
                    "--destination must be an exact namespace-qualified Dropbox file path, for example " +
                    "ns:123456//Documents/report.csv.",
                    "Nothing was read from the provider and nothing was uploaded.");
            namespaceId = requestedNamespace;
            if (!string.Equals(requestedNamespace, retainedNamespace, StringComparison.Ordinal))
                throw Refuse("missing_target", SyncCliExitCode.MissingTarget,
                    "The destination names namespace '" + requestedNamespace + "', which is not the retained root " +
                    "namespace '" + retainedNamespace + "' of account '" + request.AccountId + "'.",
                    "Correct the destination namespace; run: " + SyncCli.ExecutableName +
                    " --cli accounts show --account " + request.AccountId);

            // The exact local source: one ordinary file, never a directory, a reparse point or a cloud
            // placeholder. The read handle stays open across the single effect, so no other writer can
            // replace or rewrite the bytes while their identity, length and hash are in use.
            var sourcePath = FullSourcePath();
            FileAttributes attributes;
            try { attributes = File.GetAttributes(sourcePath); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                throw Refuse("missing_target", SyncCliExitCode.MissingTarget,
                    "No file exists at --source '" + sourcePath + "'.",
                    "Check the exact absolute path and run the command again.");
            }
            catch (Exception error) when (error is UnauthorizedAccessException or IOException)
            {
                throw Refuse("operation_failed", SyncCliExitCode.Failed,
                    "--source '" + sourcePath + "' could not be inspected: " + SyncCli.DescribeException(error) + ".",
                    "Choose an ordinary local file this process can read.");
            }
            if ((attributes & FileAttributes.Directory) != 0)
                throw Refuse("missing_target", SyncCliExitCode.MissingTarget,
                    "--source '" + sourcePath + "' is a directory, and this command uploads exactly one ordinary file.",
                    "Pass the exact file path.");
            if (PilotPath.IsUnsafeAttributes(attributes))
                throw Refuse("missing_target", SyncCliExitCode.MissingTarget,
                    "--source '" + sourcePath + "' is a reparse point or a cloud placeholder, so its bytes are not " +
                    "a stable local file.",
                    "Choose a file that is fully present on this device.");

            await using var handle = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.SequentialScan);
            if (handle.Length != request.ExpectedBytes)
                throw Refuse("operation_failed", SyncCliExitCode.Failed,
                    "The source '" + sourcePath + "' is " + handle.Length + " bytes, not the expected " +
                    request.ExpectedBytes + " bytes, so it changed after it was measured.",
                    "Re-measure the exact file and pass its current --bytes and --sha256.");
            progress("Hashing the exact local source " + sourcePath + " (" + handle.Length + " bytes)");
            (sourceSha, var dropboxContentHash) = await SyncExactFileUploadRules.HashSourceAsync(handle, token);
            sourceBytes = handle.Length;
            if (!string.Equals(sourceSha, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw Refuse("operation_failed", SyncCliExitCode.Failed,
                    "The source SHA-256 " + sourceSha + " does not match the expected " + request.ExpectedSha256 +
                    ", so the exact file changed and is not the file this request described.",
                    "Re-measure the exact file and pass its current --bytes and --sha256.");
            verifiedUtc = DateTimeOffset.UtcNow;
            progress("Verified " + sourceBytes + " bytes with SHA-256 " + sourceSha + " and Dropbox content hash " +
                dropboxContentHash + "; no byte has been sent yet");

            progress("Verifying the exact destination and its existing parent under namespace " + namespaceId +
                "; a parent folder is never created");
            var committed = await Host.UploadExactFileAsync(binding.ConnectionId, exactPath, handle, dropboxContentHash,
                () => { effectAttempted = true; effectUtc ??= DateTimeOffset.UtcNow; }, progress, token);
            if (committed.Disposition == SyncExactFileUploadDisposition.KnownNoEffect)
            {
                failureReason = committed.Detail;
                safeNextStep = "Nothing was created at the exact destination. Choose a different destination, or " +
                    "observe the existing object first; this command never replaces, renames or deletes it.";
                outcome = "known-no-effect";
                throw new SyncCliExactFileUploadException("operation_failed",
                    Reason() + " " + Next(), SyncCliExitCode.Failed, Receipt());
            }
            if (committed.Result is not { } upload)
                throw new SyncCliOperationException(
                    "The Dropbox upload returned no committed identity, so the destination state is unproven.");
            if (upload.Size != sourceBytes ||
                !string.Equals(upload.ContentHash, dropboxContentHash, StringComparison.OrdinalIgnoreCase))
            {
                committedId = upload.FileId;
                committedRevision = upload.Revision;
                committedSize = upload.Size;
                committedContentHash = upload.ContentHash;
                outcome = "unknown";
                failureReason = "Dropbox accepted the create-only commit but the returned size or content hash " +
                    "differs from the exact local source, so the created object was not proven identical.";
                safeNextStep = "Inspect the exact destination before doing anything else; this command never " +
                    "retries an upload.";
                throw new SyncCliExactFileUploadException("operation_failed", Reason() + " " + Next(),
                    SyncCliExitCode.Failed, Receipt());
            }
            committedId = upload.FileId;
            committedRevision = upload.Revision;
            committedSize = upload.Size;
            committedContentHash = upload.ContentHash;
            outcome = "success";
            progress("Committed one exact file: id " + upload.FileId + ", revision " + upload.Revision + ", size " +
                upload.Size + ", Dropbox content hash " + upload.ContentHash);
            return Receipt();
        }
        catch (SyncCliExactFileUploadException) { throw; }
        catch (SyncCliException error)
        {
            throw Refuse(error.Code, error.ExitCode, SyncCli.DescribeException(error),
                "Resolve the stated account or source condition; no upload was attempted.");
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            outcome = effectAttempted ? "unknown" : "refused";
            failureReason = (cancellationToken.IsCancellationRequested ? "The command was canceled "
                : "The command exceeded its " + ExactFileUploadBudget.TotalSeconds.ToString("0.###",
                    CultureInfo.InvariantCulture) + "-second bound ") + (effectAttempted
                    ? "after the create-only upload was attempted, so the outcome is UNKNOWN."
                    : "before any upload was transmitted, so no effect was attempted.");
            safeNextStep = effectAttempted
                ? "Inspect the exact destination for a created object before doing anything else; this command " +
                  "never blind-retries an upload."
                : "Re-run the exact command; nothing was uploaded.";
            throw new SyncCliExactFileUploadException(effectAttempted ? "operation_failed" : "timed_out",
                Reason() + " " + Next(),
                effectAttempted ? SyncCliExitCode.Failed : SyncCliExitCode.Canceled, Receipt());
        }
        catch (DropboxEffectUncertainException uncertain)
        {
            effectAttempted = true;
            outcome = "unknown";
            failureReason = "The create-only upload was transmitted and its result was not proven: " +
                SyncCli.DescribeException(uncertain);
            safeNextStep = "The exact destination state is UNKNOWN. Inspect it for a created object before doing " +
                "anything else; this command never retries an upload.";
            throw new SyncCliExactFileUploadException("operation_failed", Reason() + " " + Next(),
                SyncCliExitCode.Failed, Receipt());
        }
        catch (DropboxExactParentAbsentException absent)
        {
            throw Refuse("missing_target", SyncCliExitCode.MissingTarget, SyncCli.DescribeException(absent),
                "Create or choose an existing parent folder yourself; this command never creates one.");
        }
        catch (DropboxApiException api)
        {
            throw Refuse("operation_failed", SyncCliExitCode.Failed,
                "Dropbox refused or could not prove the destination check: " + SyncCli.DescribeException(api),
                "Check the exact account, namespace, destination and its existing parent, then run the command again.");
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidDataException or IOException or
                                          NotSupportedException or InvalidOperationException or FormatException or
                                          HttpRequestException or System.Text.Json.JsonException or
                                          UnauthorizedAccessException or ArgumentException)
        {
            throw Refuse("operation_failed", SyncCliExitCode.Failed,
                "The exact destination or its parent could not be verified: " + SyncCli.DescribeException(error),
                "Resolve the stated condition and run the exact command again; nothing was uploaded.");
        }
    }
}
