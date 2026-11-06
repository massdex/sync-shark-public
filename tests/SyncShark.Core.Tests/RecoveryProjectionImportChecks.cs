using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using Rig = CloudPairHostBindingChecks.HostRig;

// SS-D11 exact recovery projection reads and the bounded legacy-history import.
//
// A: the exact pair read, the exact pair-and-artifact read, the pair-scoped artifact read and the one-statement
// usage aggregate are compared value-for-value against the existing PUBLIC global readers over a real isolated
// SQLite authority: two active pairs plus a retired pair, seven retained copies of the main pair covering the
// retained / verified / release_authorized / released / blocked statuses, both sides, distinct sizes and
// distinct retained instants, three copies of the other pair, and requested / unrelated / missing / foreign
// artifact identities. It also proves an unrelated pair whose retained definition is unreadable is never
// hydrated by any exact request while the whole-set reader still refuses it.
//
// B: the real Host recovery consumers - usage, catalog, the unchanged public page boundaries and the exact-copy
// removal resolution - decide exactly what they decided before, over the same authority.
//
// C: the legacy-history import keeps its exact identity contract and gains bounded atomic batches: one
// in-batch database refusal rolls back only that batch, the earlier batches stay retained, the retry writes
// exactly the missing rows once, and an unchanged repeated import completes with NO writer transaction while an
// external BEGIN IMMEDIATE holds the database write slot.
//
// D: the accepted legacy-pair receipt is short-circuited only by one positively exact accepted row; an absent or
// non-matching row keeps the unchanged transactional collision refusal.
//
// E: the real query plan of each new statement is captured from the product statement text.
//
// Only retained rows and provider-free fixtures are supplied here. No provider transport request is issued, no
// schema or index is changed, and no fixture replaces a production decision.
internal static class RecoveryProjectionImportChecks
{
    private const string InstallationId = "installation-recovery-projection-import";
    private const string ProfileId = "recovery-projection-import-profile";
    private const string SourceRevision = "recovery-projection-import-1";
    private const string AbsentPairId = "absent-pair-recovery-projection";
    private const int MainArtifactCount = 7;
    private const int OtherArtifactCount = 3;
    private const int CleanHistoryEntries = 450;
    private const int OrphanIndex = 419;
    private const int HugeReasonLength = 1_200_000;
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static readonly Dictionary<string, string> Plans = new(StringComparer.Ordinal);
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated recovery projection import fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        Plans.Clear();
        _assertions = 0;
        CloudPairHostBindingChecks.UseFixtureRoot(_root);

        using var rig = await Rig.CreateAsync("recovery-projection-import");
        await rig.Db.InitializeAsync(rig.InstallationId, ProfileId, SourceRevision, "{}");
        var fixture = await BuildFixtureAsync(rig);

        await CaseAsync("A-exact-recovery-reads",
            "The exact pair, exact artifact, pair-scoped artifact and usage aggregate reads equal the public global readers and never hydrate an unrelated pair",
            () => ExactReadsAsync(rig, fixture));
        await CaseAsync("B-host-recovery-consumers",
            "The real Host usage, catalog, page boundaries and exact-copy removal resolution keep the exact previous decisions",
            () => HostConsumersAsync(rig, fixture));
        await CaseAsync("C-legacy-history-batch-import",
            "One in-batch database refusal rolls back only its own batch, the retry writes the missing rows once, and an unchanged repeat import needs no writer transaction",
            () => LegacyHistoryAsync(rig, fixture));
        await CaseAsync("D-accepted-legacy-pair-receipt",
            "A repeated accepted pair import is answered from one exact accepted receipt with no writer transaction, while a non-matching row still refuses transactionally",
            () => AcceptedPairReceiptAsync(rig, fixture));
        await CaseAsync("E-recovery-read-query-plans",
            "The real query plan of every new statement is captured from the product statement text",
            () => QueryPlansAsync(rig, fixture));

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "RecoveryProjectionImportChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases,
            ["query_plans"] = Plans
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "recovery-projection-import-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS recovery projection and import: " + _assertions + " assertions; the exact pair," +
            " artifact and usage reads equal the public global readers and hydrate no unrelated pair, the Host" +
            " recovery consumers are unchanged, the legacy-history import batches and retries atomically with no" +
            " writer transaction for an unchanged repeat, the accepted pair receipt short-circuits only on one" +
            " exact accepted row, and every new query plan is captured.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("Recovery projection import assertion failed: " + label);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    // ----- fixture ----------------------------------------------------------------------------------

    private sealed class Fixture
    {
        internal SyncPair Main = null!;
        internal SyncPair Other = null!;
        internal SyncPair Retired = null!;
        internal SyncPair Poisoned = null!;
        internal string MainCommand = "";
        internal string OtherCommand = "";
        internal string MainReleasedArtifact = "";
        internal readonly List<string> MainArtifactIds = [];
        internal readonly List<string> OtherArtifactIds = [];
    }

    private sealed record ArtifactSeed(string Tag, long Size, string Status, string Side, int AgeDays);

    private static readonly ArtifactSeed[] MainSeeds =
    [
        new("retained-one", 1_001, "retained", "dropbox", 1),
        new("verified-old", 2_002, "verified", "dropbox", 31),
        new("release-old", 4_004, "release_authorized", "dropbox", 31),
        new("released-one", 8_008, "released", "dropbox", 1),
        new("blocked-local", 16_016, "blocked", "local", 1),
        new("retained-local", 32_032, "retained", "local", 0),
        new("verified-two", 64_064, "verified", "dropbox", 2)
    ];

    private static readonly ArtifactSeed[] OtherSeeds =
    [
        new("other-one", 3_003, "retained", "dropbox", 1),
        new("other-two", 6_006, "verified", "local", 40),
        new("other-three", 9_009, "released", "dropbox", 1)
    ];

    private static async Task<Fixture> BuildFixtureAsync(Rig rig)
    {
        var fixture = new Fixture();
        var mainLeft = CloudPairHostBindingChecks.HostDropbox("rpim-main-l", "/Rpim main left");
        var mainRight = CloudPairHostBindingChecks.HostDrive("rpim-main-r", "/Rpim main right");
        await rig.AddEndpointAsync(mainLeft);
        await rig.AddEndpointAsync(mainRight);
        fixture.Main = (await rig.AddPairAsync("rpim-main", mainLeft, mainRight,
            recoveryRetentionDays: 30)).Pair;
        var otherLeft = CloudPairHostBindingChecks.HostDropbox("rpim-other-l", "/Rpim other left");
        var otherRight = CloudPairHostBindingChecks.HostDrive("rpim-other-r", "/Rpim other right");
        await rig.AddEndpointAsync(otherLeft);
        await rig.AddEndpointAsync(otherRight);
        fixture.Other = (await rig.AddPairAsync("rpim-other", otherLeft, otherRight,
            recoveryRetentionDays: 90)).Pair;
        var retiredLeft = CloudPairHostBindingChecks.HostDropbox("rpim-retired-l", "/Rpim retired left");
        var retiredRight = CloudPairHostBindingChecks.HostDrive("rpim-retired-r", "/Rpim retired right");
        await rig.AddEndpointAsync(retiredLeft);
        await rig.AddEndpointAsync(retiredRight);
        fixture.Retired = (await rig.AddPairAsync("rpim-retired", retiredLeft, retiredRight)).Pair;
        var poisonedLeft = CloudPairHostBindingChecks.HostDropbox("rpim-poisoned-l", "/Rpim poisoned left");
        var poisonedRight = CloudPairHostBindingChecks.HostDrive("rpim-poisoned-r", "/Rpim poisoned right");
        await rig.AddEndpointAsync(poisonedLeft);
        await rig.AddEndpointAsync(poisonedRight);
        fixture.Poisoned = (await rig.AddPairAsync("rpim-poisoned", poisonedLeft, poisonedRight)).Pair;
        await rig.Db.RetirePairDefinitionAsync(fixture.Retired.Id);

        fixture.MainCommand = await OpenCommandAsync(rig, fixture.Main);
        fixture.OtherCommand = await OpenCommandAsync(rig, fixture.Other);
        var now = DateTimeOffset.UtcNow;
        foreach (var seed in MainSeeds)
        {
            var id = await SeedArtifactAsync(rig, fixture.Main, fixture.MainCommand, seed, now);
            fixture.MainArtifactIds.Add(id);
            if (seed.Status == "released") fixture.MainReleasedArtifact = id;
        }
        foreach (var seed in OtherSeeds)
            fixture.OtherArtifactIds.Add(await SeedArtifactAsync(rig, fixture.Other, fixture.OtherCommand,
                seed, now));
        Assert(fixture.MainArtifactIds.Count == MainArtifactCount &&
               fixture.OtherArtifactIds.Count == OtherArtifactCount,
            "the fixture retained seven copies for the main pair and three for the other pair");
        Assert(fixture.MainReleasedArtifact.Length > 0,
            "the fixture retains exactly one released main-pair copy for the exact-read proof");
        return fixture;
    }

    private static async Task<string> OpenCommandAsync(Rig rig, SyncPair pair)
    {
        var admission = await rig.Db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        var commandId = admission.CommandRequestId;
        await rig.Db.TransitionCommandRequestAsync(commandId, "executing");
        return commandId;
    }

    private static async Task<string> SeedArtifactAsync(Rig rig, SyncPair pair, string commandId,
        ArtifactSeed seed, DateTimeOffset now)
    {
        var id = "rpim-artifact-" + ShortId(pair.Id + "/" + seed.Tag);
        var relativePath = "recovery/" + seed.Tag + ".bin";
        var copy = new SyncArchiveResult("rpim-file-" + ShortId(seed.Tag), "rpim-revision-" + ShortId(seed.Tag),
            "retained-" + seed.Tag + ".bin", seed.Size,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Id + "/" + seed.Tag)))
                .ToLowerInvariant());
        await rig.Db.RegisterManualRecoveryCopyAsync(id, commandId, pair.Id, pair.PolicyRevision, ProfileId,
            relativePath, copy, CancellationToken.None);
        var updated = await ExecuteAsync(rig.Db.DatabasePath,
            "UPDATE recovery_artifact SET status=$status,side=$side,size_bytes=$size,retained_utc=$utc " +
            "WHERE recovery_artifact_id=$id;",
            ("$status", seed.Status), ("$side", seed.Side), ("$size", seed.Size),
            ("$utc", UtcText(now.AddDays(-seed.AgeDays))), ("$id", id));
        Assert(updated == 1, "the fixture retained the exact copy row for " + seed.Tag);
        return id;
    }

    // ----- A: exact reads versus the public global readers ------------------------------------------

    private static async Task ExactReadsAsync(Rig rig, Fixture fixture)
    {
        var main = await rig.Db.LoadPairDefinitionAsync(fixture.Main.Id);
        Assert(main is not null && main.Id == fixture.Main.Id &&
               main.PolicyRevision == fixture.Main.PolicyRevision && main.Name == fixture.Main.Name,
            "the exact pair read returns the requested retained pair definition");
        Assert(await rig.Db.LoadPairDefinitionAsync(AbsentPairId) is null,
            "an absent pair identity returns no definition");
        var retired = await rig.Db.LoadPairDefinitionAsync(fixture.Retired.Id);
        Assert(retired is not null && retired.Id == fixture.Retired.Id,
            "a retired pair definition stays readable through the default retired-inclusive read");
        Assert(await rig.Db.LoadPairDefinitionAsync(fixture.Retired.Id, includeRetired: false) is null,
            "the exact read keeps the optional retired filter of the whole-set reader");

        var all = await rig.Db.LoadRecoveryArtifactsAsync();
        var mainExpected = all.Where(item => string.Equals(item.PairId, fixture.Main.Id, StringComparison.Ordinal) &&
                                            item.Status != "released").ToArray();
        var mainScoped = await rig.Db.LoadRecoveryArtifactsForPairAsync(fixture.Main.Id);
        Assert(mainScoped.Count == MainArtifactCount - 1 && mainScoped.Count == mainExpected.Length,
            "the pair-scoped read returns exactly the pair's non-released copies; observed " + mainScoped.Count);
        for (var index = 0; index < mainExpected.Length; index++)
            Assert(mainScoped[index] == mainExpected[index],
                "the pair-scoped read returns the identical projection and retained order at index " + index);

        foreach (var artifact in all.Where(item =>
                     string.Equals(item.PairId, fixture.Main.Id, StringComparison.Ordinal)))
        {
            var exact = await rig.Db.LoadRecoveryArtifactAsync(fixture.Main.Id, artifact.RecoveryArtifactId);
            Assert(exact == artifact,
                "the exact artifact read returns the identical retained row for " + artifact.RecoveryArtifactId);
        }
        var released = await rig.Db.LoadRecoveryArtifactAsync(fixture.Main.Id, fixture.MainReleasedArtifact);
        Assert(released is not null && released.Status == "released",
            "the exact read keeps the released row the pair catalog filter omits");
        Assert(await rig.Db.LoadRecoveryArtifactAsync(fixture.Other.Id, fixture.MainArtifactIds[0]) is null,
            "an artifact identity bound to another pair is null");
        Assert(await rig.Db.LoadRecoveryArtifactAsync(fixture.Main.Id, "rpim-absent-artifact") is null,
            "a missing artifact identity is null");
        Assert(await rig.Db.LoadRecoveryArtifactAsync(fixture.Main.Id, fixture.MainArtifactIds[0], "local") is null,
            "a requested side that disagrees with the retained side is null");
        Assert(await rig.Db.LoadRecoveryArtifactAsync(fixture.Main.Id, fixture.MainArtifactIds[0], "dropbox")
               is not null,
            "the requested exact retained side returns the row");

        var otherScoped = await rig.Db.LoadRecoveryArtifactsForPairAsync(fixture.Other.Id);
        Assert(otherScoped.Count == OtherArtifactCount - 1 &&
               otherScoped.All(item => string.Equals(item.PairId, fixture.Other.Id, StringComparison.Ordinal)),
            "the other pair's scoped read contains only its own non-released copies");
        Assert((await rig.Db.LoadRecoveryArtifactsForPairAsync(fixture.Retired.Id)).Count == 0,
            "a pair with no retained copy returns no row");

        var now = DateTimeOffset.UtcNow;
        var mainDays = main!.RecoveryRetentionDays;
        Assert(mainDays == 30, "the main pair retains its 30-day recovery policy for the eligibility proof");
        var cutoff = now.AddDays(-mainDays!.Value);
        var mainUsage = await rig.Db.LoadRecoveryUsageAggregateAsync(fixture.Main.Id, cutoff);
        Assert(mainUsage == ExpectedUsage(all, fixture.Main.Id, cutoff),
            "the usage aggregate equals the previous global computation for the same captured instant");
        Assert(mainUsage.ItemCount == 5 && mainUsage.CleanupEligibleCount == 2 &&
               mainUsage.Size == 1_001 + 2_002 + 4_004 + 32_032 + 64_064,
            "the aggregate counts only retained/verified/release_authorized copies, sums their sizes and counts " +
            "exactly the two copies past the pair's 30-day retention; observed " + mainUsage.ItemCount + "/" +
            mainUsage.CleanupEligibleCount + "/" + mainUsage.Size);
        Assert(mainUsage.OldestRetainedUtc is not null,
            "the aggregate reports the oldest retained instant of the pair");
        var noEligibility = await rig.Db.LoadRecoveryUsageAggregateAsync(fixture.Main.Id, null);
        Assert(noEligibility.CleanupEligibleCount == 0 && noEligibility.ItemCount == 5 &&
               noEligibility.Size == mainUsage.Size,
            "a null retention policy makes no copy eligible while the count and size are unchanged");
        var otherUsage = await rig.Db.LoadRecoveryUsageAggregateAsync(fixture.Other.Id, null);
        Assert(otherUsage == ExpectedUsage(all, fixture.Other.Id, null) && otherUsage.ItemCount == 2 &&
               otherUsage.Size == 3_003 + 6_006,
            "the aggregate is pair-scoped and excludes the other pair's released copy");
        var emptyUsage = await rig.Db.LoadRecoveryUsageAggregateAsync(fixture.Retired.Id, null);
        Assert(emptyUsage.ItemCount == 0 && emptyUsage.Size == 0 && emptyUsage.OldestRetainedUtc is null &&
               emptyUsage.CleanupEligibleCount == 0,
            "a pair with no retained copy is a genuine zero count and size with no oldest instant");

        await UnreadableUnrelatedPairAsync(rig, fixture, mainUsage);
    }

    private static async Task UnreadableUnrelatedPairAsync(Rig rig, Fixture fixture,
        SyncRecoveryUsageAggregate mainUsage)
    {
        var original = await ScalarStringAsync(rig.Db.DatabasePath,
            "SELECT pair_json FROM sync_pair WHERE pair_id=$id;", ("$id", fixture.Poisoned.Id));
        Assert(original is not null, "the unrelated pair fixture retained its published definition");
        try
        {
            var wrongShape = await ExecuteAsync(rig.Db.DatabasePath,
                "UPDATE sync_pair SET pair_json=$json WHERE pair_id=$id;",
                ("$json", "1"), ("$id", fixture.Poisoned.Id));
            Assert(wrongShape == 1,
                "the unrelated pair row now retains valid JSON that is not a pair definition");
            var wholeSet = await CaptureAsync(() => rig.Db.LoadAllPairDefinitionsAsync());
            Assert(wholeSet is JsonException or InvalidDataException,
                "the whole-set reader still refuses the unrelated definition that is not a pair definition; " +
                "observed " + (wholeSet is null ? "no failure" : wholeSet.GetType().Name));
            var requested = await CaptureAsync(() => rig.Db.LoadPairDefinitionAsync(fixture.Poisoned.Id));
            Assert(requested is JsonException or InvalidDataException,
                "a requested pair row that is not a pair definition fails instead of returning a usable pair");
            var exact = await rig.Db.LoadPairDefinitionAsync(fixture.Main.Id);
            Assert(exact is not null && exact.Id == fixture.Main.Id,
                "the exact pair read is unaffected by an unrelated definition that is not a pair definition");
            var scoped = await rig.Db.LoadRecoveryArtifactsForPairAsync(fixture.Main.Id);
            Assert(scoped.Count == MainArtifactCount - 1,
                "the pair-scoped artifact read is unaffected by an unrelated definition that is not a pair " +
                "definition");
            Assert(await rig.Db.LoadRecoveryUsageAggregateAsync(fixture.Main.Id, null) ==
                   new SyncRecoveryUsageAggregate(mainUsage.ItemCount, mainUsage.Size,
                       mainUsage.OldestRetainedUtc, 0),
                "the usage aggregate is unaffected by an unrelated definition that is not a pair definition");

            var foreignDefinition = await ScalarStringAsync(rig.Db.DatabasePath,
                "SELECT pair_json FROM sync_pair WHERE pair_id=$id;", ("$id", fixture.Main.Id));
            Assert(foreignDefinition is not null,
                "the main pair definition is available for the identity-mismatch proof");
            var mismatched = await ExecuteAsync(rig.Db.DatabasePath,
                "UPDATE sync_pair SET pair_json=$json WHERE pair_id=$id;",
                ("$json", foreignDefinition), ("$id", fixture.Poisoned.Id));
            Assert(mismatched == 1, "the unrelated pair row now retains another pair's definition");
            var mismatchedWhole = await CaptureAsync(() => rig.Db.LoadAllPairDefinitionsAsync());
            Assert(mismatchedWhole is InvalidDataException,
                "the whole-set reader refuses a retained definition that does not match its own authority " +
                "identity; observed " + (mismatchedWhole is null ? "no failure" : mismatchedWhole.GetType().Name));
            var mismatchedRequested = await CaptureAsync(() => rig.Db.LoadPairDefinitionAsync(fixture.Poisoned.Id));
            Assert(mismatchedRequested is InvalidDataException,
                "the exact requested read refuses the same mismatched definition");
            var mismatchedExact = await rig.Db.LoadPairDefinitionAsync(fixture.Main.Id);
            Assert(mismatchedExact is not null && mismatchedExact.Id == fixture.Main.Id,
                "the exact pair read is unaffected by another pair's mismatched retained definition");
        }
        finally
        {
            var restored = await ExecuteAsync(rig.Db.DatabasePath,
                "UPDATE sync_pair SET pair_json=$json WHERE pair_id=$id;",
                ("$json", original), ("$id", fixture.Poisoned.Id));
            Assert(restored == 1, "the unrelated pair definition was restored after the poison proof");
            Assert((await rig.Db.LoadAllPairDefinitionsAsync()).Count > 0,
                "the whole-set reader is coherent again after the poison proof");
        }
    }

    private static SyncRecoveryUsageAggregate ExpectedUsage(
        IReadOnlyList<SyncRecoveryArtifactProjection> all, string pairId, DateTimeOffset? cutoff)
    {
        var retained = all.Where(item => string.Equals(item.PairId, pairId, StringComparison.Ordinal) &&
                                         item.Status is "retained" or "verified" or "release_authorized")
            .ToArray();
        var oldest = retained.Length == 0 ? (DateTimeOffset?)null : retained.Min(item => item.RetainedUtc);
        var eligible = cutoff is { } instant ? retained.Count(item => item.RetainedUtc <= instant) : 0;
        return new SyncRecoveryUsageAggregate(retained.Length, retained.Sum(item => item.Size), oldest, eligible);
    }

    // ----- B: the real Host recovery consumers ------------------------------------------------------

    private static async Task HostConsumersAsync(Rig rig, Fixture fixture)
    {
        var requestsBefore = rig.Wire.TotalRequests;
        var usage = await rig.Host.LoadRecoveryUsageAsync(fixture.Main.Id);
        Assert(usage.PairId == fixture.Main.Id && usage.ItemCount == 5 && usage.Size == 103_103 &&
               usage.CleanupEligibleCount == 2 && usage.RetentionDays == 30,
            "the Host usage surface reports the exact retained-copy aggregate of the requested pair");
        var all = await rig.Db.LoadRecoveryArtifactsAsync();
        var expectedKeys = all
            .Where(item => string.Equals(item.PairId, fixture.Main.Id, StringComparison.Ordinal) &&
                           item.Status != "released")
            .Select(item => item.RecoveryArtifactId).ToHashSet(StringComparer.Ordinal);
        var catalog = await rig.Host.LoadRecoveryCatalogAsync(fixture.Main.Id);
        Assert(catalog.Count == MainArtifactCount - 1 &&
               catalog.Select(item => item.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expectedKeys),
            "the Host catalog contains exactly this pair's non-released copies");
        Assert(catalog.All(item => string.Equals(item.PairId, fixture.Main.Id, StringComparison.Ordinal)),
            "the Host catalog never borrows another pair's copy identity");
        var mainSides = all.Where(item =>
                string.Equals(item.PairId, fixture.Main.Id, StringComparison.Ordinal) && item.Status != "released")
            .ToDictionary(item => item.RecoveryArtifactId, item => item.Side, StringComparer.Ordinal);
        Assert(catalog.All(item => item.Source == (mainSides[item.Key] == "local"
                ? SyncRecoverySource.LocalArchive
                : SyncRecoverySource.DropboxArchive)),
            "every resolved catalog item keeps the exact source decision of its retained side");
        Assert(rig.Wire.TotalRequests == requestsBefore,
            "no recovery read issued a provider transport request");

        var pageAll = await HostPageAsync(rig, fixture.Main.Id, 500, 0, "recovery/");
        Assert(pageAll.Items.Count == MainArtifactCount - 1 && !pageAll.HasMoreArchives &&
               pageAll.NextArchiveOffset == MainArtifactCount - 1,
            "the unchanged public maximum of 500 returns the pair's whole archive page without a continuation");
        var pageOne = await HostPageAsync(rig, fixture.Main.Id, 1, 0, "recovery/");
        Assert(pageOne.Items.Count == 1 && pageOne.HasMoreArchives && pageOne.NextArchiveOffset == 1,
            "page size 1 still reports a continuation");
        var page499 = await HostPageAsync(rig, fixture.Main.Id, 499, 0, "recovery/");
        Assert(page499.Items.Count == MainArtifactCount - 1 && !page499.HasMoreArchives,
            "page size 499 returns the remaining rows without a false continuation");
        var refusedZero = await CaptureAsync(() => HostPageAsync(rig, fixture.Main.Id, 0, 0, null));
        Assert(refusedZero is ArgumentOutOfRangeException,
            "the public Host caller still refuses page size 0");
        var refused501 = await CaptureAsync(() => HostPageAsync(rig, fixture.Main.Id, 501, 0, null));
        Assert(refused501 is ArgumentOutOfRangeException,
            "the public Host caller still refuses page size 501");

        var item = catalog.Single(entry => entry.Key == fixture.MainArtifactIds[0]);
        var missing = await CaptureAsync(() =>
            rig.Host.RemoveRecoveryCopyAsync(item with { Key = "rpim-absent-artifact" }));
        Assert(missing is InvalidDataException,
            "a missing artifact identity keeps the exact missing-copy refusal; observed " +
            (missing is null ? "no failure" : missing.GetType().Name));
        var foreign = await CaptureAsync(() =>
            rig.Host.RemoveRecoveryCopyAsync(item with { PairId = fixture.Other.Id }));
        Assert(foreign is InvalidDataException,
            "an artifact identity that belongs to another pair keeps the exact missing-copy refusal");
        var unknownPair = await CaptureAsync(() =>
            rig.Host.RemoveRecoveryCopyAsync(item with { PairId = AbsentPairId }));
        Assert(unknownPair is InvalidDataException,
            "an artifact identity of a pair that is not retained keeps the exact missing-pair refusal");

        var unrelatedUsage = await rig.Host.LoadRecoveryUsageAsync(fixture.Other.Id);
        Assert(unrelatedUsage.PairId == fixture.Other.Id && unrelatedUsage.ItemCount == 2,
            "the Host usage surface stays pair-scoped for a second pair");
    }

    private static async Task<SyncRecoveryCatalogPage> HostPageAsync(Rig rig, string pairId, int pageSize,
        int offset, string? search) =>
        await rig.Host.LoadRecoveryCatalogPageAsync(pairId, archiveOffset: offset, deletedCursor: null,
            includeDropboxDeletedItems: false, pageSize: pageSize, search: search);

    // ----- C: the bounded legacy-history import -----------------------------------------------------

    private static async Task LegacyHistoryAsync(Rig rig, Fixture fixture)
    {
        var withOrphan = BuildHistory(fixture, includeOrphan: true);
        var preparedWithOrphan = SyncAuthorityDatabase.PrepareLegacyHistoryImport(withOrphan);
        var batches = SyncAuthorityDatabase.BuildLegacyHistoryImportBatches(preparedWithOrphan);
        Assert(preparedWithOrphan.Count == CleanHistoryEntries + 1,
            "every entry of the source is normalized exactly once");
        Assert(batches.Count == 3 && batches[0].Count == 200 && batches[1].Count == 200 && batches[2].Count == 51,
            "the prepared records group into 200/200/51 batches");
        var lastBatch = batches[2].Select(record => record.DecisionId).ToHashSet(StringComparer.Ordinal);
        var orphan = preparedWithOrphan.Single(record => string.Equals(record.PairId, AbsentPairId,
            StringComparison.Ordinal));
        Assert(lastBatch.Contains(orphan.DecisionId),
            "the record that names a pair the authority does not retain belongs to the last batch");
        Assert(batches.All(batch => batch.Count <= SyncAuthorityDatabase.MaximumLegacyHistoryBatchRecords) &&
               batches.Where(batch => batch.Count > 1).All(batch =>
                   batch.Sum(record => (long)record.PayloadBytes) <=
                   SyncAuthorityDatabase.MaximumLegacyHistoryBatchPayloadBytes),
            "no batch exceeds 200 records and no multi-record batch exceeds the 1 MiB payload bound");

        var failure = await CaptureAsync(() => rig.Db.ImportLegacyHistoryAsync(withOrphan));
        Assert(failure is SqliteException or InvalidOperationException,
            "the orphan pair identity is refused by the retained authority before any row is retained; observed " +
            (failure is null ? "no failure" : failure.GetType().Name));
        var committed = await CountPairDecisionsAsync(rig.Db.DatabasePath, fixture);
        Assert(committed == 400,
            "the refused batch committed nothing while the two earlier batches stayed retained; observed " +
            committed);
        Assert(await CountExactDecisionAsync(rig.Db.DatabasePath, orphan.DecisionId) == 0,
            "the refused orphan row was never retained");

        var withoutOrphan = BuildHistory(fixture, includeOrphan: false);
        await rig.Db.ImportLegacyHistoryAsync(withoutOrphan);
        var completed = await CountPairDecisionsAsync(rig.Db.DatabasePath, fixture);
        Assert(completed == CleanHistoryEntries,
            "the retry wrote exactly the missing rows of the refused batch and nothing twice; observed " +
            completed);
        Assert(await CountExactDecisionAsync(rig.Db.DatabasePath, orphan.DecisionId) == 0,
            "the retry did not resurrect the refused orphan row");

        var baseUtc = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var duplicate = new SyncAuthorityDatabase.LegacyHistoryImportRecord("legacy-history-duplicate",
            fixture.Retired.Id, "unchanged", "{\"path\":\"duplicate\"}", UtcText(baseUtc), 40);
        var conflicting = new SyncAuthorityDatabase.LegacyHistoryImportRecord("legacy-history-duplicate",
            fixture.Retired.Id, "unchanged", "{\"path\":\"conflicting\"}", UtcText(baseUtc), 44);
        await rig.Db.ImportLegacyHistoryBatchAsync([duplicate, duplicate], CancellationToken.None);
        Assert(await CountExactDecisionAsync(rig.Db.DatabasePath, duplicate.DecisionId) == 1,
            "one exactly equal duplicate identity inside a batch writes one retained row");
        var conflict = await CaptureAsync(() =>
            rig.Db.ImportLegacyHistoryBatchAsync([duplicate, conflicting], CancellationToken.None));
        Assert(conflict is InvalidOperationException,
            "two records that share an identity but differ in a proof field are refused with the retained " +
            "collision refusal; observed " + (conflict is null ? "no failure" : conflict.GetType().Name));
        Assert(await CountExactDecisionAsync(rig.Db.DatabasePath, duplicate.DecisionId) == 1,
            "the refused conflicting batch left the single retained row unchanged");

        var before = await DecisionRowsAsync(rig.Db.DatabasePath, fixture);
        Assert(before.Count == CleanHistoryEntries,
            "the retained decision inventory of the two pairs is exact before the repeat import");
        var repeatElapsed = await WithHeldWriteSlotAsync(rig.Db.DatabasePath, async () =>
        {
            var started = Stopwatch.GetTimestamp();
            await rig.Db.ImportLegacyHistoryAsync(withoutOrphan);
            return Stopwatch.GetElapsedTime(started);
        });
        Assert(repeatElapsed < TimeSpan.FromSeconds(5),
            "the unchanged repeat import completed well inside the writer timeout while an external " +
            "BEGIN IMMEDIATE held the write slot; elapsed " +
            repeatElapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "ms");
        var after = await DecisionRowsAsync(rig.Db.DatabasePath, fixture);
        Assert(after.Count == before.Count && after.SequenceEqual(before, StringComparer.Ordinal),
            "the unchanged repeat import changed no retained row");

        var boundEntries = new List<SyncHistoryEntry>
        {
            new(baseUtc, fixture.Main.Id, fixture.Main.Name, "bound/one.bin", SyncDecisionKind.Uploaded, "one"),
            new(baseUtc.AddSeconds(1), fixture.Main.Id, fixture.Main.Name, "bound/huge.bin",
                SyncDecisionKind.Uploaded, new string('x', HugeReasonLength)),
            new(baseUtc.AddSeconds(2), fixture.Main.Id, fixture.Main.Name, "bound/three.bin",
                SyncDecisionKind.Uploaded, "three")
        };
        var preparedBound = SyncAuthorityDatabase.PrepareLegacyHistoryImport(new SyncHistory(boundEntries));
        var boundBatches = SyncAuthorityDatabase.BuildLegacyHistoryImportBatches(preparedBound);
        var oversized = preparedBound.Single(record =>
            record.PayloadBytes > SyncAuthorityDatabase.MaximumLegacyHistoryBatchPayloadBytes);
        Assert(boundBatches.Count == 3,
            "a record that exceeds the payload bound on its own is isolated in its own batch; observed " +
            boundBatches.Count);
        Assert(boundBatches[1].Count == 1 && boundBatches[1][0].DecisionId == oversized.DecisionId,
            "the oversized record keeps a single-record batch of its own");
        Assert(oversized.DecisionJson.Contains(new string('x', 1_000), StringComparison.Ordinal),
            "the oversized record is retained whole and never truncated");
        Assert(boundBatches.SelectMany(batch => batch).Select(record => record.DecisionId)
                .ToHashSet(StringComparer.Ordinal).SetEquals(
                    preparedBound.Select(record => record.DecisionId).ToHashSet(StringComparer.Ordinal)),
            "no prepared record is dropped or merged away by the batch grouping");
        Assert(preparedBound.Count == 3,
            "every prepared record keeps its own measured serialized payload; observed " + preparedBound.Count);
    }

    private static SyncHistory BuildHistory(Fixture fixture, bool includeOrphan)
    {
        var baseline = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var entries = new List<SyncHistoryEntry>(CleanHistoryEntries + 1);
        for (var index = 0; index < CleanHistoryEntries; index++)
        {
            // The orphan keeps its own instant between two clean instants, so it lands in the last batch
            // without shifting any other entry - and therefore without changing any other decision identity.
            if (includeOrphan && index == OrphanIndex)
                entries.Add(new SyncHistoryEntry(baseline.AddSeconds(index - 1).AddMilliseconds(500),
                    AbsentPairId, "Absent pair", "history/orphan.bin", SyncDecisionKind.Blocked,
                    "orphan history entry"));
            var pair = index % 2 == 0 ? fixture.Main : fixture.Other;
            var outcome = (index % 3) switch
            {
                0 => SyncDecisionKind.Baseline,
                1 => SyncDecisionKind.Uploaded,
                _ => SyncDecisionKind.Identical
            };
            entries.Add(new SyncHistoryEntry(baseline.AddSeconds(index), pair.Id, pair.Name,
                "history/" + index.ToString("D4", CultureInfo.InvariantCulture) + ".bin", outcome,
                "legacy reason " + index.ToString(CultureInfo.InvariantCulture)));
        }
        return new SyncHistory(entries);
    }

    private static async Task<List<string>> DecisionRowsAsync(string databasePath, Fixture fixture)
    {
        var rows = new List<string>();
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT reconciliation_decision_id,pair_id,decision_kind,decision_json,decided_utc " +
            "FROM reconciliation_decision WHERE pair_id IN ($main,$other) " +
            "ORDER BY reconciliation_decision_id;";
        command.Parameters.AddWithValue("$main", fixture.Main.Id);
        command.Parameters.AddWithValue("$other", fixture.Other.Id);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0) + "|" + reader.GetString(1) + "|" + reader.GetString(2) + "|" +
                     reader.GetString(3) + "|" + reader.GetString(4));
        return rows;
    }

    private static Task<long> CountPairDecisionsAsync(string databasePath, Fixture fixture) =>
        ScalarLongAsync(databasePath,
            "SELECT count(*) FROM reconciliation_decision WHERE pair_id IN ($main,$other);",
            ("$main", fixture.Main.Id), ("$other", fixture.Other.Id));

    private static Task<long> CountExactDecisionAsync(string databasePath, string decisionId) =>
        ScalarLongAsync(databasePath,
            "SELECT count(*) FROM reconciliation_decision WHERE reconciliation_decision_id=$id;",
            ("$id", decisionId));

    // ----- D: the accepted legacy-pair receipt ------------------------------------------------------

    private static async Task AcceptedPairReceiptAsync(Rig rig, Fixture fixture)
    {
        var source = fixture.Retired;
        var first = source with { Name = "Rpim accepted first" };
        var second = source with { Name = "Rpim accepted second" };
        await rig.Db.RecordAcceptedLegacyPairImportAsync(source, first, null, CancellationToken.None);
        Assert(await CountAcceptedReceiptsAsync(rig.Db.DatabasePath) == 1,
            "the first accepted pair import retained exactly one accepted receipt");
        var receiptId = await ScalarStringAsync(rig.Db.DatabasePath,
            "SELECT migration_import_id FROM migration_import WHERE source_kind='legacy_0_1_1_pairs' " +
            "AND status='accepted';");
        Assert(receiptId is not null, "the accepted receipt identity is retained");
        var retainedBefore = await ScalarStringAsync(rig.Db.DatabasePath,
            "SELECT imported_json FROM migration_import WHERE migration_import_id=$id;", ("$id", receiptId));

        var repeatElapsed = await WithHeldWriteSlotAsync(rig.Db.DatabasePath, async () =>
        {
            var started = Stopwatch.GetTimestamp();
            await rig.Db.RecordAcceptedLegacyPairImportAsync(source, first, null, CancellationToken.None);
            return Stopwatch.GetElapsedTime(started);
        });
        Assert(repeatElapsed < TimeSpan.FromSeconds(5),
            "a repeated accepted pair import is answered from the exact accepted receipt with no writer " +
            "transaction while an external BEGIN IMMEDIATE held the write slot; elapsed " +
            repeatElapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + "ms");
        Assert(await CountAcceptedReceiptsAsync(rig.Db.DatabasePath) == 1,
            "the repeated accepted pair import retained no second receipt");

        var refusal = await CaptureAsync(() => rig.Db.RecordAcceptedLegacyPairImportAsync(source, second));
        Assert(refusal is InvalidOperationException,
            "a row that shares the source identity but not the imported payload still keeps the collision " +
            "refusal; observed " + (refusal is null ? "no failure" : refusal.GetType().Name));
        var retainedAfter = await ScalarStringAsync(rig.Db.DatabasePath,
            "SELECT imported_json FROM migration_import WHERE migration_import_id=$id;", ("$id", receiptId));
        Assert(retainedBefore is not null && retainedAfter == retainedBefore,
            "the refused second import left the retained accepted receipt byte-identical");
        Assert(await CountAcceptedReceiptsAsync(rig.Db.DatabasePath) == 1,
            "the refused second import retained no additional accepted receipt");
    }

    private static Task<long> CountAcceptedReceiptsAsync(string databasePath) =>
        ScalarLongAsync(databasePath,
            "SELECT count(*) FROM migration_import WHERE source_kind='legacy_0_1_1_pairs' AND status='accepted';");

    // ----- E: the real statement plans --------------------------------------------------------------

    private static async Task QueryPlansAsync(Rig rig, Fixture fixture)
    {
        var cutoff = UtcText(DateTimeOffset.UtcNow.AddDays(-30));
        var captured = new List<(string Name, string Plan)>
        {
            ("pair-definition", await ExplainAsync(rig.Db.DatabasePath, SyncAuthorityDatabase.PairDefinitionSql,
                ("$pair", fixture.Main.Id), ("$includeRetired", 1))),
            ("exact-artifact", await ExplainAsync(rig.Db.DatabasePath,
                SyncAuthorityDatabase.ExactRecoveryArtifactSql, ("$pair", fixture.Main.Id),
                ("$artifact", fixture.MainArtifactIds[0]), ("$side", "dropbox"))),
            ("pair-artifacts", await ExplainAsync(rig.Db.DatabasePath,
                SyncAuthorityDatabase.PairRecoveryArtifactSql, ("$pair", fixture.Main.Id))),
            ("usage-aggregate", await ExplainAsync(rig.Db.DatabasePath,
                SyncAuthorityDatabase.RecoveryUsageAggregateSql, ("$pair", fixture.Main.Id), ("$cutoff", cutoff)))
        };
        foreach (var entry in captured)
        {
            Assert(!string.IsNullOrWhiteSpace(entry.Plan),
                "the " + entry.Name + " statement reported a query plan");
            Plans[entry.Name] = entry.Plan;
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = "E-plan-" + entry.Name,
                ["label"] = "EXPLAIN QUERY PLAN of the product statement text",
                ["result"] = "CAPTURED",
                ["query_plan"] = entry.Plan
            });
            Console.WriteLine("PLAN " + entry.Name + ": " + entry.Plan);
        }
    }

    private static async Task<string> ExplainAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.FieldCount < 4) continue;
            var detail = reader.GetValue(3)?.ToString();
            if (!string.IsNullOrEmpty(detail)) lines.Add(detail);
        }
        return string.Join("; ", lines);
    }

    // ----- shared fixture helpers -------------------------------------------------------------------

    private static async Task<T> WithHeldWriteSlotAsync<T>(string databasePath, Func<Task<T>> action)
    {
        // An external BEGIN IMMEDIATE takes the database write slot for the duration of the action. In WAL
        // mode it does not block readers, so a read-only import still completes; a write attempt would wait
        // out the authority connection's own 5 second busy timeout and then fail.
        await using var connection = await OpenAsync(databasePath);
        await using (var hold = connection.CreateCommand())
        {
            hold.CommandText = "PRAGMA busy_timeout=0; BEGIN IMMEDIATE;";
            await hold.ExecuteNonQueryAsync();
        }
        try
        {
            return await action();
        }
        finally
        {
            await using var release = connection.CreateCommand();
            release.CommandText = "ROLLBACK;";
            await release.ExecuteNonQueryAsync();
        }
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<int> ExecuteAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ScalarStringAsync(string databasePath, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static string UtcText(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static string ShortId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];
}

