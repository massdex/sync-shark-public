using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

/// <summary>The retained lifecycle of one normalized original consent header.</summary>
public enum SyncAuthorityConsentLifecycle
{
    Building,
    Sealed,
    Active,
    Inactive,
    PartiallyExpired,
    Closed
}

/// <summary>The retained lifecycle of one original consent item.</summary>
public enum SyncAuthorityConsentItemLifecycle
{
    Eligible,
    Issued,
    Completed,
    Expired
}

/// <summary>How a selected-ordinal read treats an item that can no longer authorize a fresh attempt.</summary>
public enum SyncAuthorityConsentSelectionMode
{
    /// <summary>Read exactly what is retained, including completed and expired items.</summary>
    ExactRead,

    /// <summary>
    /// Refuse the exact selection when any requested original item is missing, expired or already completed, so a
    /// retry can never reissue an item that is no longer available.
    /// </summary>
    ReissueSelection
}

/// <summary>The exact immutable identity of one original reviewed approval before or during its preparation.</summary>
public sealed record SyncAuthorityConsentPreparation(
    string ConsentId,
    string? OriginatingCommandId,
    string ReservedCommandId,
    string PairId,
    string OriginalRequestSha256,
    string OriginalChoiceDigest,
    string HeaderJson,
    int OriginalChoiceCount,
    bool ContinueExistingPreparation = false);

/// <summary>The bounded progress state of one consent header.</summary>
public sealed record SyncAuthorityConsentCandidate(
    string ConsentId,
    SyncAuthorityConsentLifecycle Lifecycle,
    int OriginalChoiceCount,
    int PreparedChoiceCount,
    long PreparedChoiceBytes,
    string? SealedUtc,
    bool AlreadyComplete);

/// <summary>One exact original choice prepared by its caller, before it is retained.</summary>
public sealed record SyncAuthorityConsentPreparedChoice(
    string RelativePath,
    string ExpectedGroup,
    string Action,
    string ChoiceJson,
    string ChoiceSha256,
    long EncodedBytes);

/// <summary>The retained immutable header of one original reviewed approval.</summary>
public sealed record SyncAuthorityConsentHeader(
    string ConsentId,
    string? OriginatingCommandId,
    string PairId,
    string OriginalRequestSha256,
    int OriginalChoiceCount,
    string OriginalChoiceDigest,
    string HeaderJson,
    long HeaderEncodedBytes,
    int PreparedChoiceCount,
    long PreparedChoiceBytes,
    string? PreparedCommandId,
    SyncAuthorityConsentLifecycle Lifecycle,
    int ExpiredChoiceCount,
    string CreatedUtc,
    string? SealedUtc,
    string? LastUseUtc);

/// <summary>One retained complete original choice body. It is never inferred from current paths.</summary>
public sealed record SyncAuthorityConsentItem(
    string ConsentId,
    int Ordinal,
    string RelativePathKey,
    string RelativePath,
    string ExpectedGroup,
    string Action,
    string ChoiceJson,
    string ChoiceSha256,
    long EncodedBytes,
    SyncAuthorityConsentItemLifecycle Lifecycle,
    string? IssuedUtc,
    string? CompletedUtc,
    string? ExpiredUtc);

/// <summary>One lean original item row: path, group, action and lifecycle without its choice body.</summary>
public sealed record SyncAuthorityConsentItemOutline(
    string ConsentId,
    int Ordinal,
    string RelativePathKey,
    string RelativePath,
    string ExpectedGroup,
    string Action,
    SyncAuthorityConsentItemLifecycle Lifecycle);

/// <summary>The exact original consent one retained command is bound to.</summary>
public sealed record SyncAuthorityConsentBinding(
    string ConsentId,
    string PairId,
    int OriginalChoiceCount,
    string OriginalRequestSha256,
    string OriginalChoiceDigest,
    SyncAuthorityConsentLifecycle Lifecycle);

/// <summary>
/// One per-item retirement fact for the later bounded maintenance step: the lifecycle, the exact reverse
/// reference count of retained operations and their exact statuses. It carries no choice body.
/// </summary>
public sealed record SyncAuthorityConsentItemPin(
    string ConsentId,
    int Ordinal,
    string RelativePathKey,
    string RelativePath,
    SyncAuthorityConsentItemLifecycle Lifecycle,
    string? IssuedUtc,
    string? CompletedUtc,
    int OperationCount,
    string? OperationStatus);

/// <summary>The bounded receipt of one explicit non-destructive legacy original import.</summary>
public sealed record SyncAuthorityConsentImportResult(
    string ConsentId,
    string SourceCommandId,
    string OriginalRequestSha256,
    int ItemCount,
    bool AlreadyImported);

/// <summary>
/// The one canonical encoding of an original reviewed selection. Every producer and every reader uses these
/// helpers, so a retained original choice body is byte-identical to the body the approved request would have
/// carried and its ordered digest is computed the same way on both sides.
/// </summary>
internal static class SyncAuthorityConsentEncoding
{
    /// <summary>At most 256 original choices are retained in one preparation chunk.</summary>
    internal const int MaximumItemsPerChunk = 256;

    /// <summary>At most 1 MiB of encoded choice payload is retained in one preparation chunk.</summary>
    internal const long MaximumChunkEncodedBytes = 1024 * 1024;

    /// <summary>At most this many exactly selected original ordinals are read in one paged call.</summary>
    internal const int MaximumSelectedOrdinalsPerPage = 4096;

    /// <summary>The paged outline page size used when a whole original run is streamed.</summary>
    internal const int OutlinePageSize = 1024;

    /// <summary>The retained consent identity of one reserved command identity. One reservation, one header.</summary>
    internal static string ConsentIdForReservedCommand(string reservedCommandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservedCommandId);
        var suffix = reservedCommandId.StartsWith("command-", StringComparison.Ordinal)
            ? reservedCommandId["command-".Length..]
            : reservedCommandId;
        return "consent-" + suffix;
    }

    internal static string Sha256Hex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    /// <summary>The exact retained choice body of one approved original choice.</summary>
    internal static string SerializeChoice(SyncBootstrapActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        return JsonSerializer.Serialize(choice);
    }

    internal static SyncAuthorityConsentPreparedChoice Prepare(SyncBootstrapActionChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        var json = SerializeChoice(choice);
        return new SyncAuthorityConsentPreparedChoice(PilotPath.NormalizeRelative(choice.RelativePath),
            choice.ExpectedGroup.Trim(), choice.Action.Trim(), json, Sha256Hex(json),
            Encoding.UTF8.GetByteCount(json));
    }

    internal static SyncBootstrapActionChoice DeserializeChoice(string choiceJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choiceJson);
        SyncBootstrapActionChoice? choice;
        try { choice = JsonSerializer.Deserialize<SyncBootstrapActionChoice>(choiceJson); }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "A retained original consent choice is not readable. No file action was authorized.", error);
        }
        return choice ?? throw new InvalidDataException(
            "A retained original consent choice is empty. No file action was authorized.");
    }

    /// <summary>
    /// The one canonical ordered-choice digest: the lowercase SHA-256 of the exact ordinal-then-body-hash
    /// serialization of every original choice in original array order. It binds the complete ordered original
    /// selection without re-reading every body, so it is verified exactly at the atomic seal and never shrinks
    /// when an individual item later expires.
    /// </summary>
    internal static string OrderedDigest(IEnumerable<(int Ordinal, string ChoiceSha256)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var builder = new StringBuilder();
        foreach (var item in items)
            builder.Append(item.Ordinal.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(item.ChoiceSha256).Append('\n');
        return Sha256Hex(builder.ToString());
    }

    internal static string OrderedDigest(IReadOnlyList<SyncAuthorityConsentPreparedChoice> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var pairs = new List<(int Ordinal, string ChoiceSha256)>(choices.Count);
        for (var index = 0; index < choices.Count; index++)
            pairs.Add((index, choices[index].ChoiceSha256));
        return OrderedDigest(pairs);
    }

    /// <summary>
    /// The exact approved original non-Choices identity a consent header binds: the plan's own pair and the two
    /// reviewed inventory cut identities, read from the retained header JSON under strict validation.
    /// </summary>
    internal static (string PairId, string LocalScanGenerationId, string DropboxScanGenerationId)
        ReadPlanIdentity(string headerJson)
    {
        using var header = ParseHeader(headerJson);
        var root = header.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "A retained original consent header carries no reviewed plan identity. No file action was authorized.");
        return (RequiredString(plan, "PairId"), RequiredString(plan, "LocalScanGenerationId"),
            RequiredString(plan, "DropboxScanGenerationId"));
    }

    internal static JsonDocument ParseHeader(string headerJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerJson);
        try { return JsonDocument.Parse(headerJson); }
        catch (JsonException error)
        {
            throw new InvalidDataException("A retained original consent header is not readable JSON.", error);
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidDataException(
                "A retained original consent header is incomplete: " + name + " is absent. No file action was authorized.");
}

/// <summary>
/// The ONE normalized original-consent authority of the product: schema 12's consent_header/consent_item
/// tables, their bounded non-authorizing chunked preparation, their single atomic seal, the exact
/// command-acceptance binding, the scoped paged selection reads, the per-item lifecycle markers and the
/// bounded explicit legacy import. Nothing here backfills on startup, rewrites a retained payload, deletes an
/// original input or grants an effect on its own: a header that is not sealed authorizes nothing, and a
/// selected ordinal that is missing, expired or completed always refuses with an explicit message.
/// </summary>
public sealed partial class SyncAuthorityDatabase
{
    private const string ConsentItemColumns =
        "consent_id,ordinal,relative_path_key,original_relative_path,original_expected_group,original_action," +
        "choice_json,choice_sha256,encoded_bytes,lifecycle,issued_utc,completed_utc,expired_utc";

    private const string ConsentOutlineColumns =
        "consent_id,ordinal,relative_path_key,original_relative_path,original_expected_group,original_action,lifecycle";

    private const string ConsentHeaderColumns =
        "consent_id,originating_command_id,pair_id,original_request_sha256,original_choice_count," +
        "original_choice_digest,header_json,header_encoded_bytes,prepared_choice_count,prepared_choice_bytes," +
        "prepared_command_id,lifecycle,expired_choice_count,created_utc,sealed_utc,last_use_utc";

    internal static string ConsentLifecycleWire(SyncAuthorityConsentLifecycle value) => value switch
    {
        SyncAuthorityConsentLifecycle.Building => "building",
        SyncAuthorityConsentLifecycle.Sealed => "sealed",
        SyncAuthorityConsentLifecycle.Active => "active",
        SyncAuthorityConsentLifecycle.Inactive => "inactive",
        SyncAuthorityConsentLifecycle.PartiallyExpired => "partially_expired",
        SyncAuthorityConsentLifecycle.Closed => "closed",
        _ => throw new InvalidDataException("An unsupported consent lifecycle was supplied.")
    };

    internal static SyncAuthorityConsentLifecycle ParseConsentLifecycle(string value) => value switch
    {
        "building" => SyncAuthorityConsentLifecycle.Building,
        "sealed" => SyncAuthorityConsentLifecycle.Sealed,
        "active" => SyncAuthorityConsentLifecycle.Active,
        "inactive" => SyncAuthorityConsentLifecycle.Inactive,
        "partially_expired" => SyncAuthorityConsentLifecycle.PartiallyExpired,
        "closed" => SyncAuthorityConsentLifecycle.Closed,
        _ => throw new InvalidDataException("A retained consent lifecycle is not recognized.")
    };

    internal static string ConsentItemLifecycleWire(SyncAuthorityConsentItemLifecycle value) => value switch
    {
        SyncAuthorityConsentItemLifecycle.Eligible => "eligible",
        SyncAuthorityConsentItemLifecycle.Issued => "issued",
        SyncAuthorityConsentItemLifecycle.Completed => "completed",
        SyncAuthorityConsentItemLifecycle.Expired => "expired",
        _ => throw new InvalidDataException("An unsupported consent item lifecycle was supplied.")
    };

    internal static SyncAuthorityConsentItemLifecycle ParseConsentItemLifecycle(string value) => value switch
    {
        "eligible" => SyncAuthorityConsentItemLifecycle.Eligible,
        "issued" => SyncAuthorityConsentItemLifecycle.Issued,
        "completed" => SyncAuthorityConsentItemLifecycle.Completed,
        "expired" => SyncAuthorityConsentItemLifecycle.Expired,
        _ => throw new InvalidDataException("A retained consent item lifecycle is not recognized.")
    };

    /// <summary>
    /// Starts ONE non-authorizing building header for one reserved command identity. The caller has already
    /// serialized the header outside every write transaction, so no SQLite write transaction is ever held while
    /// the whole original request is serialized. An exact same-identity replay returns the retained progress and
    /// never rewrites a byte; a different identity under the same command or reservation refuses; a building
    /// candidate is only resumed by an explicit same-reservation resume.
    /// </summary>
    public async Task<SyncAuthorityConsentCandidate> BeginConsentPreparationAsync(
        SyncAuthorityConsentPreparation preparation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        RequireIdentifier(preparation.ConsentId, nameof(preparation));
        RequireIdentifier(preparation.ReservedCommandId, nameof(preparation));
        RequireIdentifier(preparation.PairId, nameof(preparation));
        if (preparation.OriginatingCommandId is not null)
            RequireIdentifier(preparation.OriginatingCommandId, nameof(preparation));
        RequireHash(preparation.OriginalRequestSha256, nameof(preparation));
        RequireHash(preparation.OriginalChoiceDigest, nameof(preparation));
        RequireJson(preparation.HeaderJson, nameof(preparation));
        if (preparation.OriginalChoiceCount < 0)
            throw new ArgumentOutOfRangeException(nameof(preparation));
        var headerBytes = Encoding.UTF8.GetByteCount(preparation.HeaderJson);
        var expected = preparation.OriginalRequestSha256.ToLowerInvariant();
        SyncAuthorityConsentCandidate? candidate = null;
        await WriteAsync(async (connection, transaction) =>
        {
            if (await ScalarLongAsync(connection, transaction,
                    "SELECT count(*) FROM sync_pair WHERE pair_id=$pair;", cancellationToken,
                    ("$pair", preparation.PairId)) != 1)
                throw new InvalidDataException("The reviewed consent does not name a retained folder pair.");
            var retained = await ReadConsentHeaderCoreAsync(connection, transaction, preparation.ConsentId,
                cancellationToken);
            if (retained is not null)
            {
                if (retained.PairId != preparation.PairId ||
                    retained.OriginatingCommandId != preparation.OriginatingCommandId ||
                    retained.PreparedCommandId != preparation.ReservedCommandId ||
                    retained.OriginalRequestSha256 != expected ||
                    retained.OriginalChoiceCount != preparation.OriginalChoiceCount ||
                    retained.OriginalChoiceDigest != preparation.OriginalChoiceDigest.ToLowerInvariant() ||
                    retained.HeaderJson != preparation.HeaderJson ||
                    retained.HeaderEncodedBytes != headerBytes)
                    throw new InvalidDataException(
                        "The retained original consent header does not match this exact approved request. No chunk was retained.");
                if (retained.Lifecycle == SyncAuthorityConsentLifecycle.Building &&
                    !preparation.ContinueExistingPreparation)
                    throw new InvalidOperationException(
                        "An incomplete consent preparation is already retained for this reserved command identity; a fresh reviewed run never silently resumes another preparation.");
                candidate = Candidate(retained, retained.Lifecycle != SyncAuthorityConsentLifecycle.Building);
                return;
            }
            var duplicate = await ScalarLongAsync(connection, transaction,
                preparation.OriginatingCommandId is null
                    ? "SELECT count(*) FROM consent_header WHERE prepared_command_id=$command;"
                    : "SELECT count(*) FROM consent_header WHERE originating_command_id=$origin OR prepared_command_id=$command;",
                cancellationToken, ("$origin", preparation.OriginatingCommandId),
                ("$command", preparation.ReservedCommandId));
            if (duplicate != 0)
                throw new InvalidOperationException(
                    "This approved request or reserved command identity already retains a different consent preparation.");
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO consent_header(consent_id,originating_command_id,pair_id,original_request_sha256,
                    original_choice_count,original_choice_digest,header_json,header_encoded_bytes,
                    prepared_choice_count,prepared_choice_bytes,prepared_command_id,lifecycle,
                    expired_choice_count,created_utc)
                VALUES($id,$origin,$pair,$request,$count,$digest,$header,$bytes,0,0,$command,'building',0,$utc);
                """, cancellationToken, ("$id", preparation.ConsentId),
                ("$origin", preparation.OriginatingCommandId), ("$pair", preparation.PairId),
                ("$request", expected), ("$count", preparation.OriginalChoiceCount),
                ("$digest", preparation.OriginalChoiceDigest.ToLowerInvariant()),
                ("$header", preparation.HeaderJson), ("$bytes", headerBytes),
                ("$command", preparation.ReservedCommandId), ("$utc", Utc(DateTimeOffset.UtcNow)));
            if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                throw new InvalidOperationException("The original consent header was not retained exactly once.");
            candidate = new SyncAuthorityConsentCandidate(preparation.ConsentId,
                SyncAuthorityConsentLifecycle.Building, preparation.OriginalChoiceCount, 0, 0, null, false);
        }, cancellationToken);
        return candidate!;
    }

    /// <summary>
    /// Retains ONE contiguous chunk of at most 256 original choices or 1 MiB of encoded choice payload. A single
    /// oversized valid item is prepared on its own and is never truncated. Preparation is never approval: the
    /// header stays non-building-consuming until the separate atomic seal succeeds. A repeated chunk whose
    /// bytes differ from the retained originals refuses; an identical repeated chunk is idempotent.
    /// </summary>
    public async Task<SyncAuthorityConsentCandidate> AppendConsentItemsAsync(string consentId, int firstOrdinal,
        IReadOnlyList<SyncAuthorityConsentPreparedChoice> choices, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        ArgumentNullException.ThrowIfNull(choices);
        if (firstOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(firstOrdinal));
        if (choices.Count == 0)
            throw new ArgumentException("At least one original choice is required.", nameof(choices));
        if (choices.Count > SyncAuthorityConsentEncoding.MaximumItemsPerChunk)
            throw new ArgumentException(
                "One preparation chunk retains at most 256 original choices.", nameof(choices));
        var chunkBytes = 0L;
        var keys = new string[choices.Count];
        for (var index = 0; index < choices.Count; index++)
        {
            var choice = choices[index] ?? throw new ArgumentException(
                "A prepared original choice is required.", nameof(choices));
            RequireIdentifier(choice.RelativePath, nameof(choices));
            RequireHash(choice.ChoiceSha256, nameof(choices));
            RequireJson(choice.ChoiceJson, nameof(choices));
            if (choice.EncodedBytes < 0) throw new ArgumentOutOfRangeException(nameof(choices));
            chunkBytes = checked(chunkBytes + choice.EncodedBytes);
            keys[index] = PilotPath.CanonicalComparisonKey(choice.RelativePath);
        }
        if (choices.Count > 1 && chunkBytes > SyncAuthorityConsentEncoding.MaximumChunkEncodedBytes)
            throw new ArgumentException(
                "One preparation chunk retains at most 1 MiB of encoded choice payload; one oversized valid choice is prepared on its own.",
                nameof(choices));
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
            throw new InvalidDataException(
                "The original reviewed selection names one path twice; the original duplicate-path refusal is retained.");
        var lastOrdinal = checked(firstOrdinal + choices.Count);
        SyncAuthorityConsentCandidate? candidate = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var header = await ReadConsentHeaderCoreAsync(connection, transaction, consentId, cancellationToken)
                ?? throw new InvalidDataException(
                    "No original consent preparation is retained under this identity; nothing was retained.");
            if (lastOrdinal > header.OriginalChoiceCount)
                throw new InvalidDataException(
                    "The preparation chunk exceeds the exact original choice count; nothing was retained.");
            if (firstOrdinal > header.PreparedChoiceCount)
                throw new InvalidDataException(
                    "Preparation chunks must be contiguous from the first original ordinal; nothing was retained.");
            var everyOrdinalRetained = true;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO consent_item(consent_id,ordinal,relative_path_key,original_relative_path,
                        original_expected_group,original_action,choice_json,choice_sha256,encoded_bytes,lifecycle)
                    VALUES($consent,$ordinal,$key,$path,$group,$action,$json,$sha,$bytes,'eligible');
                    """;
                var pConsent = insert.Parameters.Add("$consent", SqliteType.Text);
                var pOrdinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
                var pKey = insert.Parameters.Add("$key", SqliteType.Text);
                var pPath = insert.Parameters.Add("$path", SqliteType.Text);
                var pGroup = insert.Parameters.Add("$group", SqliteType.Text);
                var pAction = insert.Parameters.Add("$action", SqliteType.Text);
                var pJson = insert.Parameters.Add("$json", SqliteType.Text);
                var pSha = insert.Parameters.Add("$sha", SqliteType.Text);
                var pBytes = insert.Parameters.Add("$bytes", SqliteType.Integer);
                for (var index = 0; index < choices.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var choice = choices[index];
                    var ordinal = firstOrdinal + index;
                    var retained = await ReadConsentItemCoreAsync(connection, transaction, consentId, ordinal,
                        cancellationToken);
                    if (retained is not null)
                    {
                        if (retained.RelativePathKey != keys[index] ||
                            retained.ChoiceJson != choice.ChoiceJson ||
                            retained.ChoiceSha256 != choice.ChoiceSha256.ToLowerInvariant() ||
                            retained.EncodedBytes != choice.EncodedBytes)
                            throw new InvalidDataException(
                                "A repeated consent preparation chunk does not match the retained original choice at its ordinal; nothing was retained.");
                        continue;
                    }
                    everyOrdinalRetained = false;
                    pConsent.Value = consentId;
                    pOrdinal.Value = ordinal;
                    pKey.Value = keys[index];
                    pPath.Value = choice.RelativePath;
                    pGroup.Value = choice.ExpectedGroup;
                    pAction.Value = choice.Action;
                    pJson.Value = choice.ChoiceJson;
                    pSha.Value = choice.ChoiceSha256.ToLowerInvariant();
                    pBytes.Value = choice.EncodedBytes;
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            if (header.Lifecycle != SyncAuthorityConsentLifecycle.Building)
            {
                if (!everyOrdinalRetained || lastOrdinal > header.PreparedChoiceCount)
                    throw new InvalidOperationException(
                        "A sealed original consent accepts no further preparation chunk.");
                candidate = Candidate(header, true);
                return;
            }
            var target = Math.Max(header.PreparedChoiceCount, lastOrdinal);
            var prefixBytes = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(sum(encoded_bytes),0) FROM consent_item WHERE consent_id=$c AND ordinal<$limit;",
                cancellationToken, ("$c", consentId), ("$limit", target));
            await ExecuteAsync(connection, transaction,
                """
                UPDATE consent_header SET prepared_choice_count=$count, prepared_choice_bytes=$bytes,
                    last_use_utc=$utc
                WHERE consent_id=$c AND lifecycle='building' AND prepared_choice_count=$previous;
                """, cancellationToken, ("$count", target), ("$bytes", prefixBytes),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$c", consentId),
                ("$previous", header.PreparedChoiceCount));
            if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                throw new InvalidOperationException(
                    "The consent preparation is no longer building at its exact retained progress.");
            candidate = new SyncAuthorityConsentCandidate(consentId, SyncAuthorityConsentLifecycle.Building,
                header.OriginalChoiceCount, target, prefixBytes, null, false);
        }, cancellationToken);
        return candidate!;
    }

    /// <summary>
    /// The ONE atomic seal. It verifies the exact retained total, the exact contiguous ordinal set, the exact
    /// encoded byte total and the exact ordered-choice digest, requires the exact pending command reservation
    /// binding, and only then marks the header sealed. An incomplete or altered population never seals, and a
    /// building header is never authority for any command, planner, retry or operation.
    /// </summary>
    public async Task<SyncAuthorityConsentCandidate> SealConsentAsync(string consentId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        SyncAuthorityConsentCandidate? candidate = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var header = await ReadConsentHeaderCoreAsync(connection, transaction, consentId, cancellationToken)
                ?? throw new InvalidDataException("The original consent preparation to seal is not retained.");
            var retainedCount = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM consent_item WHERE consent_id=$c;", cancellationToken, ("$c", consentId));
            var lastOrdinal = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(max(ordinal),-1) FROM consent_item WHERE consent_id=$c;", cancellationToken,
                ("$c", consentId));
            var retainedBytes = await ScalarLongAsync(connection, transaction,
                "SELECT coalesce(sum(encoded_bytes),0) FROM consent_item WHERE consent_id=$c;", cancellationToken,
                ("$c", consentId));
            var digest = await ComputeConsentOrderedDigestCoreAsync(connection, transaction, consentId,
                cancellationToken);
            if (retainedCount != header.OriginalChoiceCount ||
                lastOrdinal != header.OriginalChoiceCount - 1 ||
                header.PreparedChoiceCount != header.OriginalChoiceCount ||
                header.PreparedChoiceBytes != retainedBytes ||
                !string.Equals(digest, header.OriginalChoiceDigest, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The retained original selection is incomplete or altered; no reviewed command may consume a building consent.");
            if (string.IsNullOrWhiteSpace(header.PreparedCommandId))
                throw new InvalidDataException(
                    "An original consent header without its exact pending command reservation is never sealed.");
            var sealedUtc = header.SealedUtc;
            if (header.Lifecycle == SyncAuthorityConsentLifecycle.Building)
            {
                sealedUtc = Utc(DateTimeOffset.UtcNow);
                await ExecuteAsync(connection, transaction,
                    """
                    UPDATE consent_header SET lifecycle='sealed', sealed_utc=$utc, last_use_utc=$utc
                    WHERE consent_id=$c AND lifecycle='building' AND prepared_choice_count=$count;
                    """, cancellationToken, ("$utc", sealedUtc), ("$c", consentId),
                    ("$count", header.OriginalChoiceCount));
                if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                    throw new InvalidOperationException("The consent header was not sealed exactly once.");
            }
            candidate = new SyncAuthorityConsentCandidate(consentId, SyncAuthorityConsentLifecycle.Sealed,
                header.OriginalChoiceCount, header.OriginalChoiceCount, retainedBytes, sealedUtc, true);
        }, cancellationToken);
        return candidate!;
    }

    /// <summary>
    /// The ONE consent-aware command acceptance. The original root command requires that exact SEALED header,
    /// binds command_request.consent_id to it and moves the header to active inside the same transaction; a
    /// retry command may only bind an already accepted header. An already retained exact row is resolved by
    /// reading it, never by inserting a second one, and never by accepting a different consent.
    /// </summary>
    public async Task<SyncAuthorityCommandAdmission> CreateAcceptedCommandAdmissionWithConsentAsync(
        SyncAuthorityCommandReservation? reservation, string consentId, string commandKind, string? pairId,
        string? targetId, long? expectedGeneration, string payloadJson, SyncAuthorityCommandPurpose purpose,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        if (pairId is not null) RequireIdentifier(pairId, nameof(pairId));
        if (expectedGeneration is <= 0) throw new ArgumentOutOfRangeException(nameof(expectedGeneration));
        if (commandKind is not ("run_pair" or "run_all" or "pause" or "resume" or "stop" or
            "resolve_conflict" or "cancel_unstarted" or "reconcile" or "backup" or "integrity_check"))
            throw new ArgumentOutOfRangeException(nameof(commandKind));
        RequireJson(payloadJson, nameof(payloadJson));
        // ONE reservation identity for this acceptance. The exact id used for the retained row must be the
        // reservation whose single insertion claim is consumed, so a null input mints exactly once here and that
        // same object is carried through identity, claim and insert. There is no late unrelated mint.
        var exactReservation = reservation ?? SyncAuthorityCommandReservation.Mint();
        var commandRequestId = exactReservation.CommandId;
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        SyncAuthorityCommandAdmission? admission = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var retained = await ScalarLongAsync(connection, transaction,
                "SELECT count(*) FROM command_request WHERE command_request_id=$id;", cancellationToken,
                ("$id", commandRequestId));
            if (retained > 1) throw new InvalidDataException("The command request identity is not unique.");
            var header = await ReadConsentHeaderCoreAsync(connection, transaction, consentId, cancellationToken)
                ?? throw new InvalidDataException(
                    "The reviewed original consent is not retained; no reviewed command may be accepted.");
            if (header.PreparedChoiceCount != header.OriginalChoiceCount)
                throw new InvalidDataException(
                    "An unsealed original selection is never authority for a reviewed command.");
            if (pairId is not null && !string.Equals(header.PairId, pairId, StringComparison.Ordinal))
                throw new InvalidDataException("The reviewed original consent belongs to a different folder pair.");
            if (retained == 1)
            {
                admission = await RequireExactAdmittedConsentCommandCoreAsync(connection, transaction,
                    commandRequestId, consentId, commandKind, pairId, targetId, expectedGeneration, payloadJson,
                    purpose, cancellationToken);
                return;
            }
            var isRoot = string.Equals(header.PreparedCommandId, commandRequestId, StringComparison.Ordinal);
            if (isRoot)
            {
                // A sealed selection is not permission to substitute a different
                // command payload before its first acceptance. Validate before
                // activating it or consuming the reservation's insertion claim.
                RequireOriginalConsentPayload(header, payloadJson);
                if (header.Lifecycle != SyncAuthorityConsentLifecycle.Sealed)
                    throw new InvalidDataException(header.Lifecycle == SyncAuthorityConsentLifecycle.Building
                        ? "An unsealed original selection is never authority for a reviewed command."
                        : "This original consent approval has already been accepted.");
                await ExecuteAsync(connection, transaction,
                    """
                    UPDATE consent_header SET lifecycle='active', last_use_utc=$utc
                    WHERE consent_id=$c AND lifecycle='sealed' AND prepared_choice_count=original_choice_count;
                    """, cancellationToken, ("$utc", Utc(DateTimeOffset.UtcNow)), ("$c", consentId));
                if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                    throw new InvalidOperationException(
                        "The original consent approval was not activated exactly once.");
            }
            else
            {
                if (header.Lifecycle is SyncAuthorityConsentLifecycle.Building or
                    SyncAuthorityConsentLifecycle.Sealed)
                    throw new InvalidDataException(
                        "Only an already accepted original consent approval can be bound by a retry command.");
                await ExecuteAsync(connection, transaction,
                    "UPDATE consent_header SET last_use_utc=$utc WHERE consent_id=$c;", cancellationToken,
                    ("$utc", Utc(DateTimeOffset.UtcNow)), ("$c", consentId));
            }
            if (!exactReservation.TryClaimInsertion())
                throw new InvalidOperationException(
                    "This reserved command identity already consumed its single insertion claim and created no retained command; a genuinely new command requires a fresh pending identity.");
            var (generation, sequence) = await AllocateCommandAdmissionSequenceCoreAsync(connection, transaction,
                cancellationToken);
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO command_request(command_request_id,command_kind,pair_id,target_id,expected_generation,
                    payload_json,requested_utc,status,admission_generation,admission_purpose,admission_sequence,
                    consent_id)
                VALUES($id,$kind,$pair,$target,$generation,$payload,$utc,'requested',$admission,$purpose,$sequence,
                    $consent);
                """, cancellationToken, ("$id", commandRequestId), ("$kind", commandKind), ("$pair", pairId),
                ("$target", targetId), ("$generation", expectedGeneration), ("$payload", payloadJson),
                ("$utc", Utc(DateTimeOffset.UtcNow)), ("$admission", generation),
                ("$purpose", SyncAuthorityCommandPurposeNames.Wire(purpose)), ("$sequence", sequence),
                ("$consent", consentId));
            if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                throw new InvalidOperationException("The consent-bound command was not retained exactly once.");
            await TransitionCommandRequestCoreAsync(connection, transaction, commandRequestId, "accepted", null,
                cancellationToken);
            admission = new SyncAuthorityCommandAdmission(commandRequestId, generation, sequence, purpose);
        }, cancellationToken);
        return admission!;
    }

    private static void RequireOriginalConsentPayload(SyncAuthorityConsentHeader header, string payloadJson)
    {
        using var payload = JsonDocument.Parse(payloadJson);
        using var approved = JsonDocument.Parse(header.HeaderJson);
        var value = payload.RootElement;
        if (value.ValueKind != JsonValueKind.Object ||
            !value.TryGetProperty("consentId", out var id) || id.ValueKind != JsonValueKind.String ||
            id.GetString() != header.ConsentId ||
            !value.TryGetProperty("originalRequestSha256", out var fingerprint) ||
            fingerprint.ValueKind != JsonValueKind.String || fingerprint.GetString() != header.OriginalRequestSha256)
            throw new InvalidDataException("The original command does not bind its exact sealed approval.");
        foreach (var field in new[] { "action", "trigger", "policyRevision", "retryOfRunId",
                     "pairIds", "pairNames", "connectionId", "selected", "skipped" })
        {
            if (!value.TryGetProperty(field, out var actual) || !approved.RootElement.TryGetProperty(field, out var expected) ||
                !System.Text.Json.Nodes.JsonNode.DeepEquals(
                    System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText()),
                    System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText())))
                throw new InvalidDataException("The original command changed the sealed approval field: " + field + ".");
        }
    }

    private static async Task<SyncAuthorityCommandAdmission> RequireExactAdmittedConsentCommandCoreAsync(
        SqliteConnection connection, SqliteTransaction transaction, string commandRequestId, string consentId,
        string commandKind, string? pairId, string? targetId, long? expectedGeneration, string payloadJson,
        SyncAuthorityCommandPurpose purpose, CancellationToken cancellationToken)
    {
        var exact = await ScalarLongAsync(connection, transaction,
            """
            SELECT count(*) FROM command_request
            WHERE command_request_id=$id AND command_kind=$kind AND pair_id IS $pair AND target_id IS $target
              AND expected_generation IS $generation AND payload_json=$payload AND admission_purpose=$purpose
              AND consent_id=$consent;
            """, cancellationToken, ("$id", commandRequestId), ("$kind", commandKind), ("$pair", pairId),
            ("$target", targetId), ("$generation", expectedGeneration), ("$payload", payloadJson),
            ("$purpose", SyncAuthorityCommandPurposeNames.Wire(purpose)), ("$consent", consentId));
        if (exact != 1)
            throw new InvalidOperationException(
                "The reserved command identity is already retained with a different kind, target, payload, generation, purpose or consent.");
        return await LoadRetainedCommandAdmissionAsync(connection, transaction, commandRequestId, cancellationToken)
               ?? throw new InvalidOperationException(
                   "The reserved command identity is retained without a current admission.");
    }

    /// <summary>The exact original consent one retained command is bound to, or null when it has none.</summary>
    public async Task<SyncAuthorityConsentBinding?> LoadConsentBindingForCommandAsync(string commandRequestId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadConsentBindingForCommandCoreAsync(connection, null, commandRequestId, cancellationToken);
    }

    /// <summary>The read-only form used by the retry inspection path, which must not claim health or authority.</summary>
    internal async Task<SyncAuthorityConsentBinding?> InspectConsentBindingForCommandAsync(string commandRequestId,
        CancellationToken cancellationToken)
    {
        RequireIdentifier(commandRequestId, nameof(commandRequestId));
        await using var connection = await OpenReadOnlyConnectionAsync(cancellationToken);
        return await LoadConsentBindingForCommandCoreAsync(connection, null, commandRequestId, cancellationToken);
    }

    private static async Task<SyncAuthorityConsentBinding?> LoadConsentBindingForCommandCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string commandRequestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT h.consent_id,h.pair_id,h.original_choice_count,h.original_request_sha256,
                   h.original_choice_digest,h.lifecycle
            FROM command_request c JOIN consent_header h ON h.consent_id=c.consent_id
            WHERE c.command_request_id=$id;
            """;
        command.Parameters.AddWithValue("$id", commandRequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncAuthorityConsentBinding(reader.GetString(0), reader.GetString(1), checked((int)reader.GetInt64(2)),
            reader.GetString(3), reader.GetString(4), ParseConsentLifecycle(reader.GetString(5)));
    }

    public async Task<SyncAuthorityConsentHeader?> LoadConsentHeaderAsync(string consentId,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadConsentHeaderCoreAsync(connection, null, consentId, cancellationToken);
    }

    internal static async Task<SyncAuthorityConsentHeader?> ReadConsentHeaderCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string consentId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + ConsentHeaderColumns + " FROM consent_header WHERE consent_id=$id;";
        command.Parameters.AddWithValue("$id", consentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SyncAuthorityConsentHeader(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.GetString(3), checked((int)reader.GetInt64(4)), reader.GetString(5),
            reader.GetString(6), reader.GetInt64(7), checked((int)reader.GetInt64(8)), reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), ParseConsentLifecycle(reader.GetString(11)),
            checked((int)reader.GetInt64(12)), reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));
    }

    internal static async Task<SyncAuthorityConsentItem?> ReadConsentItemCoreAsync(SqliteConnection connection,
        SqliteTransaction? transaction, string consentId, int ordinal, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + ConsentItemColumns +
            " FROM consent_item WHERE consent_id=$c AND ordinal=$ordinal;";
        command.Parameters.AddWithValue("$c", consentId);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadConsentItem(reader);
    }

    private static SyncAuthorityConsentItem ReadConsentItem(SqliteDataReader reader) =>
        new(reader.GetString(0), checked((int)reader.GetInt64(1)), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt64(8),
            ParseConsentItemLifecycle(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12));

    /// <summary>
    /// Scoped paged selection loading by consent identity and exact original ordinals. It reads exactly the
    /// requested ordinals and never decodes an unselected original choice merely to resolve one retry. In the
    /// reissue mode a missing, expired or already completed requested ordinal refuses with an explicit
    /// expired-approval message, so only an attempted selection is ever refused.
    /// </summary>
    public async Task<IReadOnlyList<SyncAuthorityConsentItem>> LoadConsentItemsAsync(string consentId,
        IReadOnlyList<int> ordinals, SyncAuthorityConsentSelectionMode mode,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        ArgumentNullException.ThrowIfNull(ordinals);
        if (ordinals.Count == 0) return [];
        if (ordinals.Count > SyncAuthorityConsentEncoding.MaximumSelectedOrdinalsPerPage)
            throw new ArgumentException(
                "A paged selection read carries at most 4096 original ordinals.", nameof(ordinals));
        foreach (var ordinal in ordinals)
            if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinals));
        if (ordinals.Distinct().Count() != ordinals.Count)
            throw new ArgumentException("The selected original ordinals are not distinct.", nameof(ordinals));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var retained = await ReadConsentItemsCoreAsync(connection, null, consentId, ordinals, cancellationToken);
        var byOrdinal = retained.ToDictionary(item => item.Ordinal);
        var items = new List<SyncAuthorityConsentItem>(ordinals.Count);
        foreach (var ordinal in ordinals)
        {
            if (!byOrdinal.TryGetValue(ordinal, out var item))
                throw new InvalidDataException(
                    "The original approval for ordinal " + ordinal.ToString(CultureInfo.InvariantCulture) +
                    " is no longer retained; a fresh reviewed plan is required. No file action was authorized.");
            if (mode == SyncAuthorityConsentSelectionMode.ReissueSelection)
                RequireConsentItemReissueEligible(item);
            items.Add(item);
        }
        return items;
    }

    /// <summary>
    /// The ONE per-item reissue eligibility gate. A selected original choice that is already completed or expired
    /// can never authorize a new attempt, so the exact attempted selection refuses before any plan, command or
    /// effect. The public selected read and the retry plan materialization both apply it, so no retry can ever
    /// deserialize a completed or expired original body into a plan it would act on. The lifecycle of an unrelated
    /// ordinal never refuses an otherwise valid exact selection.
    /// </summary>
    private static void RequireConsentItemReissueEligible(SyncAuthorityConsentItem item)
    {
        if (item.Lifecycle is SyncAuthorityConsentItemLifecycle.Expired or
            SyncAuthorityConsentItemLifecycle.Completed)
            throw new InvalidDataException(
                "The original approval for '" + item.RelativePath + "' is " +
                ConsentItemLifecycleWire(item.Lifecycle) +
                " and cannot authorize a new attempt; a fresh reviewed plan is required. No file action was authorized.");
    }

    private static async Task<IReadOnlyList<SyncAuthorityConsentItem>> ReadConsentItemsCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string consentId, IReadOnlyList<int> ordinals,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + ConsentItemColumns +
            " FROM consent_item WHERE consent_id=$c AND ordinal IN (SELECT value FROM json_each($ordinals)) ORDER BY ordinal;";
        command.Parameters.AddWithValue("$c", consentId);
        command.Parameters.AddWithValue("$ordinals", JsonSerializer.Serialize(ordinals));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SyncAuthorityConsentItem>(ordinals.Count);
        while (await reader.ReadAsync(cancellationToken)) items.Add(ReadConsentItem(reader));
        return items;
    }

    /// <summary>One bounded lean page of original items: path, group, action and lifecycle without any body.</summary>
    public async Task<IReadOnlyList<SyncAuthorityConsentItemOutline>> LoadConsentItemOutlinesAsync(string consentId,
        int fromOrdinal, int limit, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        if (fromOrdinal < 0) throw new ArgumentOutOfRangeException(nameof(fromOrdinal));
        if (limit is < 1 or > SyncAuthorityConsentEncoding.OutlinePageSize)
            throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadConsentItemOutlinesCoreAsync(connection, null, consentId, fromOrdinal, limit,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<SyncAuthorityConsentItemOutline>> ReadConsentItemOutlinesCoreAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string consentId, int fromOrdinal, int limit,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT " + ConsentOutlineColumns +
            " FROM consent_item WHERE consent_id=$c AND ordinal>=$from AND ordinal<$to ORDER BY ordinal;";
        command.Parameters.AddWithValue("$c", consentId);
        command.Parameters.AddWithValue("$from", fromOrdinal);
        command.Parameters.AddWithValue("$to", checked(fromOrdinal + limit));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<SyncAuthorityConsentItemOutline>(limit);
        while (await reader.ReadAsync(cancellationToken))
            items.Add(new SyncAuthorityConsentItemOutline(reader.GetString(0), checked((int)reader.GetInt64(1)),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                ParseConsentItemLifecycle(reader.GetString(6))));
        return items;
    }

    /// <summary>
    /// The explicit per-item retirement facts a later bounded maintenance step needs: lifecycle, the exact
    /// reverse-reference count of retained operations and their exact statuses, with no choice body read. An
    /// item with any retained operation reference is never eligible to lose its body.
    /// </summary>
    public async Task<IReadOnlyList<SyncAuthorityConsentItemPin>> LoadConsentItemPinsAsync(string consentId,
        IReadOnlyList<SyncAuthorityConsentItemLifecycle> lifecycles, int maximum,
        CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        ArgumentNullException.ThrowIfNull(lifecycles);
        if (maximum is < 1 or > SyncAuthorityConsentEncoding.MaximumSelectedOrdinalsPerPage)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT i.consent_id,i.ordinal,i.relative_path_key,i.original_relative_path,i.lifecycle,
                   i.issued_utc,i.completed_utc,
                   (SELECT count(*) FROM operation o
                    WHERE o.consent_id=i.consent_id AND o.consent_item_ordinal=i.ordinal),
                   (SELECT min(o.status) FROM operation o
                    WHERE o.consent_id=i.consent_id AND o.consent_item_ordinal=i.ordinal)
            FROM consent_item i
            WHERE i.consent_id=$c
              AND i.lifecycle IN (SELECT value FROM json_each($lifecycles))
            ORDER BY i.ordinal LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$c", consentId);
        command.Parameters.AddWithValue("$lifecycles",
            JsonSerializer.Serialize(lifecycles.Select(ConsentItemLifecycleWire).ToArray()));
        command.Parameters.AddWithValue("$limit", maximum);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pins = new List<SyncAuthorityConsentItemPin>();
        while (await reader.ReadAsync(cancellationToken))
            pins.Add(new SyncAuthorityConsentItemPin(reader.GetString(0), checked((int)reader.GetInt64(1)),
                reader.GetString(2), reader.GetString(3), ParseConsentItemLifecycle(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), checked((int)reader.GetInt64(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        return pins;
    }

    /// <summary>
    /// Marks exactly the named original items expired, and only when each named item is still eligible and no
    /// retained operation references it, so a retirement step can never remove an item an existing operation or
    /// completion still depends on. The header's original count and original ordered digest never shrink; only
    /// the retained expired count and the partially_expired/closed lifecycle advance. This lane performs no
    /// production expiry and removes no body.
    /// </summary>
    public async Task<SyncAuthorityConsentCandidate> MarkConsentItemsExpiredAsync(string consentId,
        IReadOnlyList<int> ordinals, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        ArgumentNullException.ThrowIfNull(ordinals);
        if (ordinals.Count == 0)
            throw new ArgumentException("At least one retained original ordinal is required.", nameof(ordinals));
        if (ordinals.Distinct().Count() != ordinals.Count)
            throw new ArgumentException("The expired original ordinals are not distinct.", nameof(ordinals));
        SyncAuthorityConsentCandidate? candidate = null;
        await WriteAsync(async (connection, transaction) =>
        {
            var header = await ReadConsentHeaderCoreAsync(connection, transaction, consentId, cancellationToken)
                ?? throw new InvalidDataException("The original consent to expire is not retained.");
            var now = Utc(DateTimeOffset.UtcNow);
            foreach (var ordinal in ordinals)
            {
                var item = await ReadConsentItemCoreAsync(connection, transaction, consentId, ordinal,
                    cancellationToken)
                    ?? throw new InvalidDataException(
                        "The original ordinal to expire is not retained; nothing was expired.");
                if (item.Lifecycle != SyncAuthorityConsentItemLifecycle.Eligible)
                    throw new InvalidDataException(
                        "Only a still-eligible original item can expire; nothing was expired.");
                if (await ScalarLongAsync(connection, transaction,
                        "SELECT count(*) FROM operation WHERE consent_id=$c AND consent_item_ordinal=$ordinal;",
                        cancellationToken, ("$c", consentId), ("$ordinal", ordinal)) != 0)
                    throw new InvalidDataException(
                        "A retained operation still references this original item; nothing was expired.");
                await ExecuteAsync(connection, transaction,
                    """
                    UPDATE consent_item SET lifecycle='expired', expired_utc=$utc
                    WHERE consent_id=$c AND ordinal=$ordinal AND lifecycle='eligible';
                    """, cancellationToken, ("$utc", now), ("$c", consentId), ("$ordinal", ordinal));
                if (await ScalarLongAsync(connection, transaction, "SELECT changes();", cancellationToken) != 1)
                    throw new InvalidOperationException("The original item was not expired exactly once.");
            }
            var expired = await ScalarLongAsync(connection, transaction,
                """
                SELECT (SELECT count(*) FROM consent_item WHERE consent_id=$c AND lifecycle='expired')+
                    coalesce((SELECT sum(json_extract(j.value,'$.Last')-json_extract(j.value,'$.First')+1)
                        FROM consent_header h,json_each(h.terminal_ranges_json) j WHERE h.consent_id=$c
                          AND json_extract(j.value,'$.State')='expired'),0);
                """,
                cancellationToken, ("$c", consentId));
            var lifecycle = expired == 0 ? header.Lifecycle
                : expired >= header.OriginalChoiceCount ? SyncAuthorityConsentLifecycle.Closed
                : SyncAuthorityConsentLifecycle.PartiallyExpired;
            await ExecuteAsync(connection, transaction,
                "UPDATE consent_header SET expired_choice_count=$expired, lifecycle=$lifecycle WHERE consent_id=$c;",
                cancellationToken, ("$expired", expired), ("$lifecycle", ConsentLifecycleWire(lifecycle)),
                ("$c", consentId));
            candidate = new SyncAuthorityConsentCandidate(consentId, lifecycle, header.OriginalChoiceCount,
                header.PreparedChoiceCount, header.PreparedChoiceBytes, header.SealedUtc, true);
        }, cancellationToken);
        return candidate!;
    }

    /// <summary>
    /// The bounded, explicitly invoked import of ONE exact legacy original request into this database's
    /// normalized tables. It reads the source command's retained payload from an isolated source database
    /// read-only, validates the exact identity and the exact UTF-8 request fingerprint, and writes only to this
    /// instance's own tables. It never changes the source payload, the source database bytes or any original
    /// product state, and it refuses when the source and the target are the same database.
    /// </summary>
    public async Task<SyncAuthorityConsentImportResult> ImportLegacyOriginalConsentAsync(string sourceCommandId,
        string sourceDatabasePath, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(sourceCommandId, nameof(sourceCommandId));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        var sourcePath = Path.GetFullPath(sourceDatabasePath);
        if (string.Equals(sourcePath, _databasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "A legacy original import requires an isolated successor or fixture database, never the source database itself.");
        var requestJson = await ReadIsolatedLegacyOriginalRequestAsync(sourcePath, sourceCommandId, cancellationToken);
        var (plan, requestSha, consentId, headerJson, prepared, digest) =
            PrepareLegacyOriginalConsentBody(sourceCommandId, requestJson, cancellationToken);
        var retained = await LoadConsentHeaderAsync(consentId, cancellationToken);
        if (retained is not null)
        {
            if (retained.PairId != plan.PairId || retained.OriginalRequestSha256 != requestSha ||
                retained.OriginalChoiceCount != prepared.Count || retained.OriginalChoiceDigest != digest)
                throw new InvalidDataException(
                    "An original consent with this identity is already retained with different authority; nothing was imported.");
            return new SyncAuthorityConsentImportResult(consentId, sourceCommandId, requestSha, prepared.Count, true);
        }
        await BeginConsentPreparationAsync(new SyncAuthorityConsentPreparation(consentId, sourceCommandId,
            sourceCommandId, plan.PairId, requestSha, digest,
            headerJson, prepared.Count),
            cancellationToken);
        var offset = 0;
        while (offset < prepared.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunkBytes = 0L;
            var count = 0;
            while (offset + count < prepared.Count && count < SyncAuthorityConsentEncoding.MaximumItemsPerChunk &&
                   (count == 0 || chunkBytes + prepared[offset + count].EncodedBytes <=
                       SyncAuthorityConsentEncoding.MaximumChunkEncodedBytes))
            {
                chunkBytes += prepared[offset + count].EncodedBytes;
                count++;
            }
            await AppendConsentItemsAsync(consentId, offset,
                prepared.GetRange(offset, count), cancellationToken);
            offset += count;
        }
        await SealConsentAsync(consentId, cancellationToken);
        return new SyncAuthorityConsentImportResult(consentId, sourceCommandId, requestSha, prepared.Count, false);
    }

    // One legacy plan at a time. The public explicit import and the offline
    // successor use identical original-byte, choice/move normalization and digest
    // rules; this pure preparation grants no eligibility or effect authority.
    private static (SyncBootstrapActionPlan Plan, string RequestSha, string ConsentId, string HeaderJson,
        List<SyncAuthorityConsentPreparedChoice> Prepared, string Digest) PrepareLegacyOriginalConsentBody(
        string sourceCommandId, string requestJson, CancellationToken cancellationToken)
    {
        var requestSha = SyncAuthorityConsentEncoding.Sha256Hex(requestJson);
        using var request = JsonDocument.Parse(requestJson);
        var root = request.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("reviewedPlan", out var retainedPlan) || retainedPlan.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "The named legacy original request retains no readable reviewed plan; nothing was imported.");
        SyncBootstrapActionPlan? plan;
        try { plan = retainedPlan.Deserialize<SyncBootstrapActionPlan>(); }
        catch (JsonException error)
        {
            throw new InvalidDataException("The named legacy original request is not readable; nothing was imported.",
                error);
        }
        if (plan is null || plan.Choices.Count == 0)
            throw new InvalidDataException(
                "The named legacy original request retains no original choices; nothing was imported.");
        var consentId = SyncAuthorityConsentEncoding.ConsentIdForReservedCommand(sourceCommandId);
        var headerJson = SyncReviewedConsentReference.BuildConsentHeaderJson(requestJson, plan, consentId);
        var prepared = new List<SyncAuthorityConsentPreparedChoice>(plan.Choices.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var choice in plan.Choices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = choice with
            {
                RelativePath = PilotPath.NormalizeRelative(choice.RelativePath),
                ExpectedGroup = choice.ExpectedGroup.Trim(),
                Action = choice.Action.Trim()
            };
            if (!seenKeys.Add(PilotPath.CanonicalComparisonKey(normalized.RelativePath)))
                throw new InvalidDataException(
                    "The named legacy original request repeats a reviewed path; nothing was imported.");
            prepared.Add(SyncAuthorityConsentEncoding.Prepare(normalized));
        }
        var digest = SyncAuthorityConsentEncoding.OrderedDigest(prepared);
        return (plan, requestSha, consentId, headerJson, prepared, digest);
    }

    private static async Task<string> ReadIsolatedLegacyOriginalRequestAsync(string sourcePath,
        string sourceCommandId, CancellationToken cancellationToken)
    {
        EnsureSqliteBootstrap();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null,
            "PRAGMA query_only=ON; PRAGMA busy_timeout=5000; PRAGMA trusted_schema=OFF;", cancellationToken);
        var payload = await ScalarStringAsync(connection, null,
            "SELECT payload_json FROM command_request WHERE command_request_id=$id AND command_kind='run_pair';",
            cancellationToken, ("$id", sourceCommandId));
        if (string.IsNullOrWhiteSpace(payload))
            throw new InvalidDataException(
                "The named legacy original command is not retained in the isolated source database; nothing was imported.");
        return payload;
    }

    /// <summary>
    /// The single production operation-creation stamp: when an operation is created inside an operation group
    /// whose retained detail names one exact original consent, the operation records the exact original consent
    /// item that authorized it. The ordinal is resolved only from the operation's own reviewed path. A reviewed
    /// operation whose group names a missing or unsealed header, a foreign folder pair, or an absent or ambiguous
    /// original ordinal fails closed rather than being issued unbound. An operation that belongs to no reviewed
    /// consent keeps both columns NULL, so ordinary unreviewed sync, control allocation and unrelated revision
    /// restore never fabricate consent.
    /// </summary>
    private static async Task StampOperationConsentAuthorityAsync(SqliteConnection connection,
        SqliteTransaction transaction, SyncAuthorityOperationPlan plan, string? persistedPreconditions,
        CancellationToken cancellationToken)
    {
        if (plan.OperationGroupId is null) return;
        var consentId = await ScalarStringAsync(connection, transaction,
            """
            SELECT json_extract(detail_json,'$.consentId') FROM operation_group
            WHERE operation_group_id=$group AND json_valid(detail_json);
            """, cancellationToken, ("$group", plan.OperationGroupId));
        if (string.IsNullOrWhiteSpace(consentId)) return;
        var header = await ReadConsentHeaderCoreAsync(connection, transaction, consentId, cancellationToken)
            ?? throw new InvalidDataException(
                "An operation whose retained group names an original consent has no retained consent header; no reviewed operation is ever issued unbound.");
        if (header.Lifecycle == SyncAuthorityConsentLifecycle.Building)
            throw new InvalidDataException(
                "An operation whose retained group names an unsealed original consent is never issued; no reviewed operation is ever issued unbound.");
        if (!string.Equals(header.PairId, plan.PairId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "An operation whose reviewed consent belongs to a different folder pair is never issued unbound.");
        var preferred = ReadReviewedPreconditionPathKey(persistedPreconditions ?? plan.PreconditionsJson);
        if (preferred is not null)
        {
            var preferredOrdinal = await FindConsentOrdinalAsync(connection, transaction, consentId, preferred,
                cancellationToken);
            if (preferredOrdinal is not null)
            {
                await StampOperationConsentAsync(connection, transaction, plan.OperationId, consentId,
                    preferredOrdinal.Value, cancellationToken);
                return;
            }
        }
        var matches = new SortedSet<int>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT DISTINCT i.ordinal FROM consent_item i
                WHERE i.consent_id=$c AND i.relative_path_key IN
                    (SELECT expected_path_key FROM operation_member
                     WHERE operation_id=$id AND expected_path_key IS NOT NULL)
                ORDER BY i.ordinal;
                """;
            command.Parameters.AddWithValue("$c", consentId);
            command.Parameters.AddWithValue("$id", plan.OperationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) matches.Add(checked((int)reader.GetInt64(0)));
        }
        if (matches.Count != 1)
            throw new InvalidDataException(
                "The reviewed original choice of this operation is absent or ambiguous; no reviewed operation is ever issued unbound.");
        await StampOperationConsentAsync(connection, transaction, plan.OperationId, consentId, matches.Min,
            cancellationToken);
    }

    private static async Task StampOperationConsentAsync(SqliteConnection connection, SqliteTransaction transaction,
        string operationId, string consentId, int ordinal, CancellationToken cancellationToken)
    {
        // Issued is monotonic. An exact retained known-no-effect refusal may
        // retry without resetting that state; a missing/unknown/completed prior
        // action never supplies this proof. Share the real retry predicate.
        if (await ScalarLongAsync(connection, transaction, $"""
                SELECT count(*) FROM consent_item i JOIN consent_header h ON h.consent_id=i.consent_id
                WHERE i.consent_id=$c AND i.ordinal=$ordinal
                  AND h.lifecycle IN ('active','partially_expired')
                  AND (i.lifecycle='eligible' OR (i.lifecycle='issued'
                    AND EXISTS(SELECT 1 FROM operation o WHERE o.consent_id=i.consent_id
                        AND o.consent_item_ordinal=i.ordinal)
                    AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.consent_id=i.consent_id
                        AND o.consent_item_ordinal=i.ordinal AND NOT ({KnownNoEffectRefusalPredicate}))));
                """, cancellationToken, ("$c", consentId), ("$ordinal", ordinal)) != 1)
            throw new InvalidDataException("This original choice is completed, expired, inactive or lacks exact retained no-effect retry proof; it cannot authorize a fresh action.");
        await ExecuteAsync(connection, transaction,
            """
            UPDATE operation SET consent_id=$c, consent_item_ordinal=$ordinal
            WHERE operation_id=$id AND consent_id IS NULL AND consent_item_ordinal IS NULL;
            """, cancellationToken, ("$c", consentId), ("$ordinal", ordinal), ("$id", operationId));
    }

    private static async Task<int?> FindConsentOrdinalAsync(SqliteConnection connection,
        SqliteTransaction transaction, string consentId, string relativePathKey,
        CancellationToken cancellationToken)
    {
        var ordinal = await ScalarLongAsync(connection, transaction,
            """
            SELECT coalesce(min(ordinal),-1) FROM consent_item
            WHERE consent_id=$c AND relative_path_key=$key;
            """, cancellationToken, ("$c", consentId), ("$key", relativePathKey));
        return ordinal < 0 ? null : checked((int)ordinal);
    }

    private static string? ReadReviewedPreconditionPathKey(string preconditionsJson)
    {
        try
        {
            using var preconditions = JsonDocument.Parse(preconditionsJson);
            if (preconditions.RootElement.ValueKind != JsonValueKind.Object ||
                !preconditions.RootElement.TryGetProperty("path", out var path) ||
                path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
                return null;
            return PilotPath.CanonicalComparisonKey(path.GetString()!);
        }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// Rebuilds the exact retained original plan of one consent-backed approval. Every original ordinal keeps its
    /// exact retained path, expected group and action from the lean projection, so retry count, order and
    /// skip semantics stay exact, and only the deliberately selected ordinals are materialized with their full
    /// immutable choice body. A whole original run streams bounded pages and never decodes unselected bodies.
    /// </summary>
    internal async Task<SyncBootstrapActionPlan> MaterializeRetainedOriginalConsentPlanAsync(string consentId,
        IReadOnlySet<string>? materializePaths, CancellationToken cancellationToken = default)
    {
        RequireIdentifier(consentId, nameof(consentId));
        await using var connection = await OpenReadOnlyConnectionAsync(cancellationToken);
        await using var snapshot = connection.BeginTransaction(deferred: true);
        var header = await ReadConsentHeaderCoreAsync(connection, snapshot, consentId, cancellationToken)
            ?? throw new InvalidDataException("The retained original consent is absent; no reviewed retry is authorized.");
        if (header.Lifecycle == SyncAuthorityConsentLifecycle.Building)
            throw new InvalidDataException("An unsealed original consent is never authority for a reviewed retry.");
        var identity = SyncAuthorityConsentEncoding.ReadPlanIdentity(header.HeaderJson);
        if (!string.Equals(identity.PairId, header.PairId, StringComparison.Ordinal))
            throw new InvalidDataException("The retained original consent header contradicts its own reviewed pair.");
        var layout = await ReadConsentTerminalLayoutCoreAsync(connection, snapshot, consentId, cancellationToken);
        var wanted = materializePaths is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) :
            new HashSet<string>(materializePaths.Select(PilotPath.NormalizeRelative), StringComparer.OrdinalIgnoreCase);
        var choices = new List<SyncBootstrapActionChoice>();
        var ordinals = new List<int>();
        var states = new List<SyncAuthorityConsentItemLifecycle>();
        var wantedOrdinals = new List<int>();
        var position = new Dictionary<int, int>();
        var from = layout.NextRetainedOrdinal(0);
        while (from < header.OriginalChoiceCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadConsentItemOutlinesCoreAsync(connection, snapshot, consentId, from,
                SyncAuthorityConsentEncoding.OutlinePageSize, cancellationToken);
            if (page.Count == 0)
                throw new InvalidDataException("An original choice is missing without exact terminal compaction proof.");
            foreach (var outline in page)
            {
                if (outline.Ordinal != from || layout.IsRetired(outline.Ordinal))
                    throw new InvalidDataException("The surviving original selection contradicts its exact terminal ranges.");
                position.Add(outline.Ordinal, choices.Count);
                ordinals.Add(outline.Ordinal);
                states.Add(outline.Lifecycle);
                choices.Add(new(outline.RelativePath, outline.ExpectedGroup, outline.Action));
                if (wanted.Contains(outline.RelativePath)) wantedOrdinals.Add(outline.Ordinal);
                from = layout.NextRetainedOrdinal(checked(outline.Ordinal + 1));
            }
        }
        if (checked((long)choices.Count + layout.RetiredCount) != header.OriginalChoiceCount)
            throw new InvalidDataException("The surviving and compacted ordinals do not cover the exact original count.");
        if (wantedOrdinals.Count != wanted.Count)
            throw new InvalidDataException("A selected original choice is missing or retired; a fresh reviewed plan is required.");
        for (var offset = 0; offset < wantedOrdinals.Count;)
        {
            var count = Math.Min(SyncAuthorityConsentEncoding.MaximumSelectedOrdinalsPerPage, wantedOrdinals.Count - offset);
            var bodies = await ReadConsentItemsCoreAsync(connection, snapshot, consentId,
                wantedOrdinals.GetRange(offset, count), cancellationToken);
            if (bodies.Count != count)
                throw new InvalidDataException("A selected original choice body is no longer retained.");
            foreach (var body in bodies)
            {
                RequireConsentItemReissueEligible(body);
                choices[position[body.Ordinal]] = SyncAuthorityConsentEncoding.DeserializeChoice(body.ChoiceJson);
            }
            offset += count;
        }
        return new SyncBootstrapActionPlan(header.PairId, identity.LocalScanGenerationId,
            identity.DropboxScanGenerationId, choices)
        { ConsentLayout = layout with { OriginalOrdinals = ordinals, SurvivingStates = states } };
    }

    private static SyncAuthorityConsentCandidate Candidate(SyncAuthorityConsentHeader header, bool complete) =>
        new(header.ConsentId, header.Lifecycle, header.OriginalChoiceCount, header.PreparedChoiceCount,
            header.PreparedChoiceBytes, header.SealedUtc, complete);

    private static async Task<string> ComputeConsentOrderedDigestCoreAsync(SqliteConnection connection,
        SqliteTransaction transaction, string consentId, CancellationToken cancellationToken)
    {
        var pairs = new List<(int Ordinal, string ChoiceSha256)>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ordinal,choice_sha256 FROM consent_item WHERE consent_id=$c ORDER BY ordinal;";
        command.Parameters.AddWithValue("$c", consentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            pairs.Add((checked((int)reader.GetInt64(0)), reader.GetString(1)));
        return SyncAuthorityConsentEncoding.OrderedDigest(pairs);
    }
}

