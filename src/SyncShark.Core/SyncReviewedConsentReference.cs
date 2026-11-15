using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Massdex.SyncShark;

/// <summary>
/// One compact, immutable reference from a retry command to the original terminal
/// first-sync approval that stays retained exactly once in its root command_request
/// payload. A retry keeps only its own selection filter as a bitmap over the
/// ORIGINAL retained Choices array order, so the reviewed plan is not repeated for
/// every retry while the original consent stays byte-for-byte unchanged. The bitmap
/// is a filter only: it never supplies an action, group, move, root or revision.
/// </summary>
internal sealed record SyncReviewedConsentReference(
    [property: JsonPropertyName("commandId")] string CommandId,
    [property: JsonPropertyName("requestSha256")] string RequestSha256,
    [property: JsonPropertyName("choiceCount")] int ChoiceCount,
    [property: JsonPropertyName("selectionBitsBase64")] string SelectionBitsBase64,
    [property: JsonPropertyName("consentId")] string? ConsentId = null)
{
    internal const string PayloadPropertyName = "reviewedConsent";
    internal const string SkipAction = "Skip for now";

    /// <summary>The normalized original consent identity a consent-backed retry additionally binds.</summary>
    internal const string ConsentIdPropertyName = "consentId";

    /// <summary>The only decoded length a selection of this original size may have: ceil(count/8).</summary>
    internal static int EncodedByteLength(int choiceCount) => choiceCount <= 0 ? 0 : (choiceCount + 7) / 8;

    /// <summary>Lowercase SHA-256 of the exact UTF-8 bytes of the retained original request JSON.</summary>
    internal static string LowercaseRequestSha256(string requestJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson))).ToLowerInvariant();

    /// <summary>
    /// This retry's selection filter, indexed by the ORIGINAL retained Choices array
    /// order with the least-significant bit first inside each byte. Bit 1 means that
    /// exact original choice is selected for this retry (its normalized action is not
    /// "Skip for now"). The original array order decides the bit; display order,
    /// case-folding or a separate enumeration never does.
    /// </summary>
    internal static string EncodeSelection(IReadOnlyList<SyncBootstrapActionChoice> originalChoices,
        IReadOnlyList<SyncBootstrapActionChoice> normalizedChoices)
    {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var choice in normalizedChoices)
            if (!string.Equals(choice.Action, SkipAction, StringComparison.Ordinal))
                selected.Add(PilotPath.NormalizeRelative(choice.RelativePath));
        var bits = new byte[EncodedByteLength(originalChoices.Count)];
        for (var index = 0; index < originalChoices.Count; index++)
            if (selected.Contains(PilotPath.NormalizeRelative(originalChoices[index].RelativePath)))
                bits[index >> 3] |= (byte)(1 << (index & 7));
        return Convert.ToBase64String(bits);
    }

    /// <summary>The exact retained consent reference for one retry command of this original approval.</summary>
    internal static SyncReviewedConsentReference Create(SyncAuthorityRunHistory originalRun,
        SyncBootstrapActionPlan originalPlan, IReadOnlyList<SyncBootstrapActionChoice> normalizedChoices,
        string? consentId = null) =>
        originalPlan.ConsentLayout is null
            ? new(originalRun.Id, LowercaseRequestSha256(originalRun.RequestJson), originalPlan.Choices.Count,
                EncodeSelection(originalPlan.Choices, normalizedChoices), consentId)
            : CreateWithOriginalOrdinals(originalRun, originalPlan, normalizedChoices, consentId);

    /// <summary>
    /// The single production payload builder for an accepted reviewed run command, consumed by the
    /// runtime host and by the checks. The original non-retry approval keeps its complete inline plan;
    /// every retry omits it and retains only the compact reference plus the existing scalar metadata.
    /// Exactly one reviewed representation is ever written.
    /// </summary>
    internal static string BuildAcceptedRunRequestJson(SyncPair pair, SyncBootstrapActionPlan normalizedPlan,
        string trigger, long policyRevision, string? retryOfRunId, SyncReviewedConsentReference? reviewedConsent)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(normalizedPlan);
        var selected = normalizedPlan.Choices.Count(item =>
            !string.Equals(item.Action, SkipAction, StringComparison.Ordinal));
        return reviewedConsent is null
            ? JsonSerializer.Serialize(new
            {
                trigger,
                reviewedPlan = normalizedPlan,
                policyRevision,
                retryOfRunId,
                pairIds = new[] { pair.Id },
                pairNames = new[] { pair.Name },
                connectionId = pair.ConnectionId,
                selected,
                skipped = normalizedPlan.Choices.Count - selected
            })
            : JsonSerializer.Serialize(new
            {
                trigger,
                reviewedConsent,
                policyRevision,
                retryOfRunId,
                pairIds = new[] { pair.Id },
                pairNames = new[] { pair.Name },
                connectionId = pair.ConnectionId,
                selected,
                skipped = normalizedPlan.Choices.Count - selected
            });
    }

    /// <summary>
    /// Reads one retained compact reference. A present-but-malformed reference is never
    /// treated as an ordinary run, and an unreadable value never authorizes an effect.
    /// </summary>
    internal static SyncReviewedConsentReference ReadRequired(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A retained retry consent reference is not a readable object. No file action was authorized.");
        SyncReviewedConsentReference? reference;
        try { reference = element.Deserialize<SyncReviewedConsentReference>(); }
        catch (JsonException error)
        {
            throw new InvalidDataException("A retained retry consent reference could not be read. No file action was authorized.",
                error);
        }
        if (reference is null || string.IsNullOrWhiteSpace(reference.CommandId))
            throw new InvalidDataException("A compact retry consent reference must name its exact original approval.");
        if (reference.RequestSha256 is not { Length: 64 } hash || !hash.All(IsLowercaseHex))
            throw new InvalidDataException("A compact retry consent reference must bind the lowercase SHA-256 of the retained original request.");
        if (reference.ChoiceCount < 0)
            throw new InvalidDataException("A compact retry consent reference has an invalid original choice count.");
        if (reference.SelectionBitsBase64 is null)
            throw new InvalidDataException("A compact retry consent reference has no selection bitmap.");
        if (reference.ConsentId is { } consentId && !string.IsNullOrWhiteSpace(consentId) &&
            (consentId.Length > 160 || consentId.Any(char.IsControl)))
            throw new InvalidDataException(
                "A compact retry consent reference names an unreadable normalized original consent identity.");
        return reference;
    }

    /// <summary>
    /// The one production payload builder for a consent-backed accepted reviewed run. The root command retains
    /// only its action, the exact UTF-8 fingerprint of the approved original request, the normalized original
    /// consent identity and the small existing summary fields; it never retains a Choices array, so a retry or a
    /// later read no longer depends on the whole original plan being repeated per command.
    /// </summary>
    internal static string BuildConsentBackedRunRequestJson(SyncPair pair, SyncBootstrapActionPlan normalizedPlan,
        string trigger, long policyRevision, string? retryOfRunId, string consentId, string originalRequestSha256)
    {
        ArgumentNullException.ThrowIfNull(pair);
        ArgumentNullException.ThrowIfNull(normalizedPlan);
        ArgumentException.ThrowIfNullOrWhiteSpace(consentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalRequestSha256);
        var selected = normalizedPlan.Choices.Count(item =>
            !string.Equals(item.Action, SkipAction, StringComparison.Ordinal));
        return JsonSerializer.Serialize(new
        {
            action = "reviewed_first_sync",
            originalRequestSha256,
            consentId,
            trigger,
            policyRevision,
            retryOfRunId,
            pairIds = new[] { pair.Id },
            pairNames = new[] { pair.Name },
            connectionId = pair.ConnectionId,
            selected,
            skipped = normalizedPlan.Choices.Count - selected
        });
    }

    /// <summary>
    /// The immutable non-Choices header of one original consent. It is the exact approved original request with
    /// its whole Choices array replaced by the normalized consent identity and the exact reviewed plan identity,
    /// so every original root, configuration, cut, direction, approval and summary datum is retained verbatim
    /// while no choice body is repeated here.
    /// </summary>
    internal static string BuildConsentHeaderJson(string originalRequestJson, SyncBootstrapActionPlan plan,
        string consentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalRequestJson);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(consentId);
        JsonObject? node;
        try { node = JsonNode.Parse(originalRequestJson) as JsonObject; }
        catch (JsonException error)
        {
            throw new InvalidDataException("The approved original request is not readable.", error);
        }
        if (node is null)
            throw new InvalidDataException("The approved original request is not a readable object.");
        node.Remove("reviewedPlan");
        node["action"] = "reviewed_first_sync";
        node["consentId"] = consentId;
        node["plan"] = JsonNode.Parse(JsonSerializer.Serialize(new
        {
            plan.PairId,
            plan.LocalScanGenerationId,
            plan.DropboxScanGenerationId
        }));
        return node.ToJsonString();
    }

    /// <summary>
    /// Decodes the selection filter. The base64 text length is checked before anything is
    /// allocated, the decoded length must equal ceil(count/8), and any bit beyond the
    /// original count must be zero.
    /// </summary>
    internal static bool[] DecodeSelection(SyncReviewedConsentReference reference)
    {
        var encoded = reference.SelectionBitsBase64 ?? string.Empty;
        var expectedBytes = EncodedByteLength(reference.ChoiceCount);
        if (encoded.Length != ((expectedBytes + 2) / 3) * 4)
            throw new InvalidDataException("A retry consent selection bitmap does not match its retained original choice count.");
        byte[] bits;
        try { bits = Convert.FromBase64String(encoded); }
        catch (FormatException error)
        {
            throw new InvalidDataException("A retry consent selection bitmap is not valid base64.", error);
        }
        if (bits.Length != expectedBytes)
            throw new InvalidDataException("A retry consent selection bitmap decoded to the wrong length.");
        var trailingBits = reference.ChoiceCount % 8;
        if (trailingBits != 0 && expectedBytes > 0 && (bits[^1] & (byte)(0xFF << trailingBits)) != 0)
            throw new InvalidDataException("A retry consent selection bitmap sets bits beyond its original choice count.");
        var selection = new bool[reference.ChoiceCount];
        for (var index = 0; index < selection.Length; index++)
            selection[index] = (bits[index >> 3] & (1 << (index & 7))) != 0;
        return selection;
    }

    /// <summary>
    /// Verifies this retry's filter against the exact original retained approval: the
    /// count must match, no original "Skip for now" may be selected, and no original
    /// reviewed path may be selected twice. The filter never replaces an original
    /// action, group, move, root identity or revision.
    /// </summary>
    internal static void RequireSelectionAgainstRoot(SyncReviewedConsentReference reference,
        SyncBootstrapActionPlan originalPlan)
    {
        var layout = originalPlan.ConsentLayout;
        if (reference.ChoiceCount != (layout?.OriginalCount ?? originalPlan.Choices.Count))
            throw new InvalidDataException("A retry consent reference does not match the retained original choice count.");
        var selection = DecodeSelection(reference);
        var selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Historical child bitmaps may still include terminal ordinals. Those
        // bits grant no new choice: fresh materialization rejects any retired
        // selection, and the surviving choices retain their original positions.
        if (layout is not null && layout.OriginalOrdinals.Count != originalPlan.Choices.Count)
            throw new InvalidDataException("The surviving original choices lost their exact ordinal map.");
        for (var index = 0; index < originalPlan.Choices.Count; index++)
        {
            var ordinal = layout?.OriginalOrdinals[index] ?? index;
            if (!selection[ordinal]) continue;
            var choice = originalPlan.Choices[index];
            if (string.Equals(choice.Action, SkipAction, StringComparison.Ordinal))
                throw new InvalidDataException("A retry cannot select a file the original review left for now.");
            if (!selectedPaths.Add(PilotPath.NormalizeRelative(choice.RelativePath)))
                throw new InvalidDataException("A retry consent reference repeats an original reviewed path.");
        }
    }

    private static SyncReviewedConsentReference CreateWithOriginalOrdinals(SyncAuthorityRunHistory originalRun,
        SyncBootstrapActionPlan originalPlan, IReadOnlyList<SyncBootstrapActionChoice> normalizedChoices,
        string? consentId)
    {
        var layout = originalPlan.ConsentLayout!;
        if (layout.OriginalOrdinals.Count != originalPlan.Choices.Count)
            throw new InvalidDataException("The surviving original choices lost their exact ordinal map.");
        var selected = normalizedChoices.Where(c => c.Action != SkipAction)
            .Select(c => PilotPath.NormalizeRelative(c.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bits = new byte[EncodedByteLength(layout.OriginalCount)];
        for (var i = 0; i < originalPlan.Choices.Count; i++)
            if (selected.Remove(PilotPath.NormalizeRelative(originalPlan.Choices[i].RelativePath)))
            {
                var ordinal = layout.OriginalOrdinals[i];
                if (layout.IsRetired(ordinal)) throw new InvalidDataException("A retired original item cannot be selected.");
                bits[ordinal >> 3] |= (byte)(1 << (ordinal & 7));
            }
        if (selected.Count != 0)
            throw new InvalidDataException("A retry selected a path outside its surviving original approval.");
        return new(originalRun.Id, LowercaseRequestSha256(originalRun.RequestJson), layout.OriginalCount,
            Convert.ToBase64String(bits), consentId);
    }

    private static bool IsLowercaseHex(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
