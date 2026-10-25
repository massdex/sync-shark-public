using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// CC2F focused proofs: the real revision-bound common-content digest resolver over real isolated SQLite
// authority state, the real typed cloud endpoint ports, the real cloud observation component and an ACTUAL
// Dropbox HTTP client fed by a synthetic in-process wire.
//
// The Dropbox side of every positive proof is the production DropboxPilotClient together with the production
// resolver and sink: the read is a real /files/download range call and the digest is computed by the
// production seam over exactly the bytes that call delivered. A scripted store supplies input only for the
// Google side, whose native SHA-256 is reused directly and needs no body read, and for the refusal cases where
// a misbehaving transport is the very input being rejected. No fixture replaces the decision under proof.
//
// No live account, credential, network, installed database, product state, schema change, Host wiring or
// activation is reachable from this file, and nothing here claims effect dispatch or runtime completion.
internal static class CloudCommonDigestChecks
{
    private const string InstallationId = "installation-cloud-common-digest-316";
    private const string SourceRevision = "cloud-common-digest-316";
    private const int BlockSize = 4 * 1024 * 1024;
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud common digest fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("A-algorithm-reference",
            "The one hashing seam reproduces independent reference digests at every block boundary",
            AlgorithmReferenceAsync);
        await CaseAsync("B-block-boundary-sizes",
            "Real range reads resolve 0/1/4MiB-1/4MiB/4MiB+1 objects with no zero-length range call",
            BlockBoundarySizesAsync);
        await CaseAsync("C-equal-and-different",
            "Equal content resolves Equal and different same-size bytes resolve Different",
            EqualAndDifferentAsync);
        await CaseAsync("D-comparable-native-no-body",
            "Known native identities and a direct Google SHA-256 need no extra body read",
            ComparableNativeNoBodyAsync);
        await CaseAsync("E-cache-reuse-and-revision",
            "The exact-key cache reuses, a new revision re-resolves and a superseded revision never reuses",
            CacheAndRevisionAsync);
        await CaseAsync("F-body-and-receipt-refusals",
            "A corrupt, truncated, oversized or mislabelled body and a drifted source all refuse",
            BodyAndReceiptRefusalsAsync);
        await CaseAsync("G-request-refusals",
            "A foreign side, root, pair, account, namespace, path, version, kind, size or algorithm refuses",
            RequestRefusalsAsync);
        await CaseAsync("H-sink-contract",
            "The sink is write-only, bounded, excess-refusing and rejects disposed or short streams",
            SinkContractAsync);
        await CaseAsync("I-cancellation",
            "Cancellation propagates and leaves no cached digest behind",
            CancellationAsync);
        await CaseAsync("J-overlap-and-unretained",
            "A shared backing object and an unverified ancestry are surfaced, never normalised",
            OverlapAndUnretainedAsync);
        await CaseAsync("K-planner-integration",
            "The real shared planner consumes the real resolver into a baseline or a conflict",
            PlannerIntegrationAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudCommonDigestChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-common-digest-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud common digest: " + _assertions + " assertions; real Dropbox HTTP range" +
            " reads through the production resolver, sink and shared comparison over real SQLite authority" +
            " state, with every identity, body, receipt, capability, cancellation and overlap refusal" +
            " returning no verified digest.");
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
        if (!condition) throw new InvalidDataException("CC2F assertion failed: " + label);
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidDataException("CC2F requirement failed: " + label);
    }

    // ----- reference implementations and payloads ------------------------------------------------

    private static byte[] Pattern(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * 31 + 7) % 251);
        return payload;
    }

    private static byte[] OtherPattern(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * 17 + 3) % 253);
        return payload;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string Md5Hex(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(MD5.HashData(payload)).ToLowerInvariant();

    // An independent reference for the Dropbox block policy, written here without the production seam.
    private static string ReferenceBlockHash(ReadOnlySpan<byte> payload)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < payload.Length; offset += BlockSize)
            aggregate.AppendData(SHA256.HashData(payload.Slice(offset,
                Math.Min(BlockSize, payload.Length - offset))));
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<(int Offset, int Length)> IrregularChunks(int size)
    {
        var sizes = new[] { 1, 3, 7, 4093, 65537, 1, 131071, 251, 5, 1048573, 17, 8388607 };
        var offset = 0;
        var index = 0;
        while (offset < size)
        {
            var length = Math.Min(sizes[index % sizes.Length], size - offset);
            if (length <= 0) break;
            yield return (offset, length);
            offset += length;
            index++;
        }
    }

    private static async Task AlgorithmReferenceAsync()
    {
        foreach (var size in new[] { 0, 1, BlockSize - 1, BlockSize, BlockSize + 1, 2 * BlockSize + 7 })
        {
            var payload = Pattern(size);
            var reference = ReferenceBlockHash(payload);
            Require(reference == DropboxContentHash.Compute(payload),
                "the block hasher matches the independent reference in memory at " + size + " bytes");
            Require(reference == SyncContentIdentity.Compute(SyncProviders.Dropbox, payload),
                "the Dropbox provider identity matches the reference at " + size + " bytes");
            Require(Sha256Hex(payload) == SyncContentIdentity.Compute(SyncProviders.GoogleDrive, payload),
                "the Google provider identity is the plain whole-file SHA-256 at " + size + " bytes");
            var incremental = SyncContentIdentity.CreateIncrementalHasher(SyncContentIdentity.DropboxBlockSha256);
            foreach (var (offset, length) in IrregularChunks(size))
                incremental.Append(payload.AsSpan(offset, length));
            Require(incremental.FinishDigest() == reference,
                "irregular multiwrite chunks produce the reference digest at " + size + " bytes");
            var streamed = await DropboxContentHash.ComputeAsync(new MemoryStream(payload, false));
            Require(streamed == reference, "the stream seam matches the reference at " + size + " bytes");
        }
        Require(SyncContentIdentity.EmptyDigest(SyncContentIdentity.DropboxBlockSha256) ==
                Sha256Hex(Array.Empty<byte>()),
            "the empty Dropbox-block aggregate is SHA-256 over zero bytes");
        Require(SyncContentIdentity.EmptyDigest(SyncContentIdentity.Sha256) == Sha256Hex(Array.Empty<byte>()) &&
                SyncContentIdentity.EmptyDigest(SyncContentIdentity.Md5) == Md5Hex(Array.Empty<byte>()),
            "the empty common and MD5 digests are the standard empty digests");
        Require(SyncContentIdentity.SupportsIncrementalHashing(SyncContentIdentity.Sha256) &&
                SyncContentIdentity.SupportsIncrementalHashing(SyncContentIdentity.Md5) &&
                SyncContentIdentity.SupportsIncrementalHashing(SyncContentIdentity.DropboxBlockSha256) &&
                !SyncContentIdentity.SupportsIncrementalHashing("sha512") &&
                !SyncContentIdentity.SupportsIncrementalHashing(null),
            "only the registered algorithms have an incremental implementation");
        var rejected = false;
        try
        {
            _ = SyncContentIdentity.CreateIncrementalHasher("sha512");
        }
        catch (NotSupportedException)
        {
            rejected = true;
        }
        Assert(rejected, "an unregistered algorithm is refused rather than substituted");
    }

    // ----- the real range path over an actual Dropbox client --------------------------------------

    private static async Task BlockBoundarySizesAsync()
    {
        foreach (var size in new[] { 0, 1, BlockSize - 1, BlockSize, BlockSize + 1 })
        {
            var payload = Pattern(size);
            var harness = await Harness.CreateAsync("boundary-" + size, "b" + size);
            harness.AttachLeft("size.bin", "id:size-" + size, "rev-size-" + size, payload);
            harness.AttachRightFixture(payload);
            await harness.ObserveAsync();
            var left = await harness.LeftAtAsync("size.bin");
            var right = await harness.RightAtAsync("size.bin");
            Require(left.Size == size && right.Size == size,
                "the fixture retained the " + size + "-byte size on both sides");

            var resolver = harness.Resolver();
            var resolved = await resolver.ResolveAsync(RequestFor(
                SyncAuthorityCloudCommonDigestResolver.LeftSide, left));
            Assert(resolved is not null, "the resolver returned a verified digest at " + size + " bytes");
            Assert(resolved!.Sha256 == Sha256Hex(payload) && resolved.ByteCount == size &&
                    resolved.Size == size && resolved.NativeHash == left.ContentHash &&
                    resolved.ObjectVersionId == left.ObjectVersionId &&
                    resolved.RootBindingId == left.RootBindingId &&
                    resolved.NativeAlgorithm == SyncContentIdentity.DropboxBlockSha256,
                "the resolved digest binds the exact identity, version, size and common SHA-256 at " + size);
            var expectedReads = size == 0 ? 0 : 1;
            Assert(harness.LeftWire!.DownloadCalls == expectedReads,
                size == 0
                    ? "zero bytes needed no range call at all"
                    : "one exact range read delivered the bytes");

            var comparison = harness.Comparison(harness.Resolver());
            var compared = await comparison.CompareAsync(left, right);
            Assert(compared.IsEqual, "equal content compares Equal at " + size + " bytes; observed " +
                compared.Kind + ": " + compared.Reason);
            Assert(harness.LeftWire!.DownloadCalls == expectedReads * 2,
                "the shared comparison resolved exactly the incomparable side once at " + size + " bytes");
            await comparison.CompareAsync(left, right);
            Assert(harness.LeftWire!.DownloadCalls == expectedReads * 2,
                "the exact repeated key reused the digest without another body read at " + size + " bytes");
            Assert(comparison.CachedDigestCount == 1,
                "one exact key is retained for the incomparable side at " + size + " bytes");
        }
    }

    private static async Task EqualAndDifferentAsync()
    {
        var content = Pattern(4096);
        var equal = await Harness.CreateAsync("equal-bytes", "e1");
        equal.AttachLeft("same.txt", "id:equal", "rev-equal", content);
        equal.AttachRightFixture(content);
        await equal.ObserveAsync();
        var equalComparison = equal.Comparison(equal.Resolver());
        var equalResult = await equalComparison.CompareAsync(await equal.LeftAtAsync("same.txt"),
            await equal.RightAtAsync("same.txt"));
        Assert(equalResult.IsEqual, "equal Dropbox and Google content compares Equal; observed " +
            equalResult.Kind + ": " + equalResult.Reason);
        Assert(equal.LeftWire!.DownloadCalls == 1, "the equal case read the incomparable body exactly once");

        var different = await Harness.CreateAsync("different-bytes", "e2");
        var other = OtherPattern(content.Length);
        different.AttachLeft("same.txt", "id:different", "rev-different", content);
        different.AttachRightFixture(other);
        await different.ObserveAsync();
        var differentLeft = await different.LeftAtAsync("same.txt");
        var differentRight = await different.RightAtAsync("same.txt");
        Assert(differentLeft.Size == differentRight.Size, "the different case retains one identical size");
        var differentComparison = different.Comparison(different.Resolver());
        var differentResult = await differentComparison.CompareAsync(differentLeft, differentRight);
        Assert(differentResult.IsDifferent, "different same-size bytes compare Different; observed " +
            differentResult.Kind + ": " + differentResult.Reason);
        Assert(different.LeftWire!.DownloadCalls == 1,
            "the different case read the incomparable body exactly once");
    }

    private static async Task ComparableNativeNoBodyAsync()
    {
        var content = Pattern(2048);
        var sameProvider = await Harness.CreateAsync("comparable-dropbox", "n1", sameProvider: true);
        sameProvider.AttachLeft("shared.txt", "id:left-native", "rev-left-native", content);
        sameProvider.AttachRightDropbox("shared.txt", "id:right-native", "rev-right-native", content);
        await sameProvider.ObserveAsync();
        var comparison = sameProvider.Comparison(sameProvider.Resolver());
        var result = await comparison.CompareAsync(await sameProvider.LeftAtAsync("shared.txt"),
            await sameProvider.RightAtAsync("shared.txt"));
        Assert(result.IsEqual, "two known Dropbox native identities at one size compare Equal; observed " +
            result.Kind + ": " + result.Reason);
        Assert(sameProvider.LeftWire!.DownloadCalls == 0 && sameProvider.RightWire!.DownloadCalls == 0,
            "a comparable known native identity needs no body read on either side");

        var directGoogle = await Harness.CreateAsync("comparable-google", "n2");
        directGoogle.AttachLeft("report.txt", "id:direct", "rev-direct", content);
        directGoogle.AttachRightFixture(content);
        await directGoogle.ObserveAsync();
        var directComparison = directGoogle.Comparison(directGoogle.Resolver());
        var directResult = await directComparison.CompareAsync(await directGoogle.LeftAtAsync("report.txt"),
            await directGoogle.RightAtAsync("report.txt"));
        Assert(directResult.IsEqual, "a direct Google SHA-256 equals the resolved Dropbox digest; observed " +
            directResult.Kind + ": " + directResult.Reason);
        Assert(directGoogle.LeftWire!.DownloadCalls == 1,
            "only the Dropbox side performed a read; the Google side reused its native SHA-256");
    }

    private static async Task CacheAndRevisionAsync()
    {
        var content = Pattern(3000);
        var harness = await Harness.CreateAsync("cache-revision", "c1");
        harness.AttachLeft("cache.txt", "id:cache", "rev-cache-1", content);
        harness.AttachRightFixture(content);
        await harness.ObserveAsync();
        var firstLeft = await harness.LeftAtAsync("cache.txt");
        var firstRight = await harness.RightAtAsync("cache.txt");
        var comparison = harness.Comparison(harness.Resolver());
        Assert((await comparison.CompareAsync(firstLeft, firstRight)).IsEqual,
            "the first comparison resolves and proves equality");
        Assert(harness.LeftWire!.DownloadCalls == 1, "the first comparison read the body once");
        Assert((await comparison.CompareAsync(firstLeft, firstRight)).IsEqual &&
                harness.LeftWire!.DownloadCalls == 1,
            "the repeated exact key reused the cached digest without another read");
        Assert(comparison.CachedDigestCount == 1, "exactly one exact key is retained");

        harness.AdvanceRevision("cache.txt", "rev-cache-2");
        await harness.ObserveAsync();
        var secondLeft = await harness.LeftAtAsync("cache.txt");
        var secondRight = await harness.RightAtAsync("cache.txt");
        Assert(secondLeft.ObjectVersionId != firstLeft.ObjectVersionId,
            "the new revision retained a new immutable object version");
        Assert((await comparison.CompareAsync(secondLeft, secondRight)).IsEqual,
            "the new revision resolves and compares Equal");
        Assert(harness.LeftWire!.DownloadCalls == 2, "the changed revision invalidated the cached digest");
        Assert((await comparison.CompareAsync(secondLeft, secondRight)).IsEqual &&
                harness.LeftWire!.DownloadCalls == 2,
            "the new revision is itself cached under its own exact key");
        Assert(comparison.CachedDigestCount == 2, "two exact revision keys are retained");

        var superseded = harness.Comparison(harness.Resolver());
        Assert((await superseded.CompareAsync(firstLeft, secondRight)).IsUnknown,
            "a superseded retained revision yields no digest instead of a stale one");
        Assert(harness.LeftWire!.DownloadCalls == 2,
            "the superseded revision performed no provider read before it was refused");
        Assert(superseded.CachedDigestCount == 0, "a refused superseded revision caches nothing");
    }

    private static async Task BodyAndReceiptRefusalsAsync()
    {
        var content = Pattern(2500);
        var other = OtherPattern(content.Length);

        var corrupt = await Harness.CreateAsync("corrupt-body", "f1");
        corrupt.AttachLeft("corrupt.txt", "id:corrupt", "rev-corrupt", content, servedBytes: other);
        corrupt.AttachRightFixture(content);
        await corrupt.ObserveAsync();
        await AssertNoVerifiedDigestAsync(corrupt, "corrupt.txt", "a corrupt same-size body", 1);

        var truncated = await Harness.CreateAsync("truncated-body", "f2");
        truncated.AttachLeft("truncated.txt", "id:truncated", "rev-truncated", content,
            downloadOverride: (file, offset, length) =>
            {
                var slice = new byte[Math.Max(1, length - 16)];
                Array.Copy(file.Content, offset, slice, 0, slice.Length);
                return RangeResponse(file, slice, offset, length);
            });
        truncated.AttachRightFixture(content);
        await truncated.ObserveAsync();
        await AssertNoVerifiedDigestAsync(truncated, "truncated.txt", "a truncated body", 1);

        var oversized = await Harness.CreateAsync("oversized-body", "f3");
        oversized.AttachLeft("oversized.txt", "id:oversized", "rev-oversized", content,
            downloadOverride: (file, offset, length) =>
            {
                var slice = new byte[length + 8];
                Array.Copy(file.Content, offset, slice, 0, length);
                return RangeResponse(file, slice, offset, length);
            });
        oversized.AttachRightFixture(content);
        await oversized.ObserveAsync();
        await AssertNoVerifiedDigestAsync(oversized, "oversized.txt", "a body longer than its range", 1);

        var notPartial = await Harness.CreateAsync("not-partial", "f4");
        notPartial.AttachLeft("rangeless.txt", "id:rangeless", "rev-rangeless", content,
            downloadOverride: (file, offset, length) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(file.Content)
            });
        notPartial.AttachRightFixture(content);
        await notPartial.ObserveAsync();
        await AssertNoVerifiedDigestAsync(notPartial, "rangeless.txt", "a 200 response to a range call", 1);

        var wrongRange = await Harness.CreateAsync("wrong-range", "f5");
        wrongRange.AttachLeft("wrong-range.txt", "id:wrong-range", "rev-wrong-range", content,
            downloadOverride: (file, offset, length) =>
            {
                var slice = new byte[length];
                Array.Copy(file.Content, offset, slice, 0, length);
                return RangeResponse(file, slice, offset + 1, length);
            });
        wrongRange.AttachRightFixture(content);
        await wrongRange.ObserveAsync();
        await AssertNoVerifiedDigestAsync(wrongRange, "wrong-range.txt", "a different content range", 1);

        var drift = await Harness.CreateAsync("drifted-metadata", "f6");
        drift.AttachLeft("drift.txt", "id:drift", "rev-drift", content,
            afterDownload: file => file.Revision = file.Revision + "-moved");
        drift.AttachRightFixture(content);
        await drift.ObserveAsync();
        await AssertNoVerifiedDigestAsync(drift, "drift.txt", "source metadata drifting after the read", 1);

        var forgotten = await Harness.CreateAsync("forgotten-identity", "f7");
        forgotten.AttachLeft("gone.txt", "id:gone", "rev-gone", content);
        forgotten.AttachRightFixture(content);
        await forgotten.ObserveAsync();
        forgotten.ForgetId("id:gone");
        await AssertNoVerifiedDigestAsync(forgotten, "gone.txt", "a source identity no longer current", 0);

        var excess = await Harness.CreateAsync("sink-excess", "f8");
        excess.AttachLeft("excess.txt", "id:excess", "rev-excess", content);
        excess.AttachRightFixture(content);
        await excess.ObserveAsync();
        var excessLeft = await excess.LeftAtAsync("excess.txt");
        var excessStore = RangeFixtureStore();
        excessStore.Writer = (destination, offset, length) =>
        {
            var bytes = new byte[length + 4];
            destination.Write(bytes, 0, bytes.Length);
        };
        Assert(await excess.ResolverWithLeftRangeStore(AsStore(excessStore)).ResolveAsync(RequestFor(
                SyncAuthorityCloudCommonDigestResolver.LeftSide, excessLeft)) is null,
            "a transport that writes more bytes than the reviewed length is refused by the sink");
        Assert(excessStore.RangeCalls == 1, "the oversized transport was actually exercised");

        var badReceipt = await Harness.CreateAsync("bad-receipt", "f9");
        badReceipt.AttachLeft("receipt.txt", "id:receipt", "rev-receipt", content);
        badReceipt.AttachRightFixture(content);
        await badReceipt.ObserveAsync();
        var receiptLeft = await badReceipt.LeftAtAsync("receipt.txt");
        var receiptStore = RangeFixtureStore();
        receiptStore.Writer = (destination, offset, length) =>
            destination.Write(new byte[(int)length], 0, (int)length);
        receiptStore.Receipt = expected => new SyncRangeDownloadResult(expected.Id!,
            expected.Revision! + "-other", expected.Size, expected.ContentHash!, 0, expected.Size);
        Assert(await badReceipt.ResolverWithLeftRangeStore(AsStore(receiptStore)).ResolveAsync(RequestFor(
                SyncAuthorityCloudCommonDigestResolver.LeftSide, receiptLeft)) is null,
            "a range receipt naming another revision is refused");
        Assert(receiptStore.RangeCalls == 1, "the mislabelled receipt was actually returned");
    }

    private static async Task AssertNoVerifiedDigestAsync(Harness harness, string path, string label,
        int expectedReads)
    {
        var left = await harness.LeftAtAsync(path);
        var right = await harness.RightAtAsync(path);
        var comparison = harness.Comparison(harness.Resolver());
        var result = await comparison.CompareAsync(left, right);
        Assert(result.IsUnknown, label + " leaves the comparison Unknown, not Equal or Different; observed " +
            result.Kind + ": " + result.Reason);
        Assert(!string.IsNullOrWhiteSpace(result.Reason), label + " surfaces its refusal reason");
        Assert(comparison.CachedDigestCount == 0, label + " caches no digest");
        Assert(harness.LeftWire!.DownloadCalls == expectedReads,
            label + " performed exactly " + expectedReads + " provider read(s)");
        Assert(await harness.Resolver().ResolveAsync(RequestFor(
                SyncAuthorityCloudCommonDigestResolver.LeftSide, left)) is null,
            label + " returns no verified digest from the resolver itself");
    }

    // ----- request refusals ----------------------------------------------------------------------

    private static async Task RequestRefusalsAsync()
    {
        var content = Pattern(1500);
        var harness = await Harness.CreateAsync("request-refusals", "g1");
        harness.AttachLeft("target.txt", "id:target", "rev-target", content);
        harness.AttachRightFixture(content);
        await harness.ObserveAsync();
        var left = await harness.LeftAtAsync("target.txt");
        var right = await harness.RightAtAsync("target.txt");
        var resolver = harness.Resolver();
        var valid = RequestFor(SyncAuthorityCloudCommonDigestResolver.LeftSide, left);
        Assert(await resolver.ResolveAsync(valid) is not null, "the control request resolves normally");
        var readsAfterControl = harness.LeftWire!.DownloadCalls;
        Assert(readsAfterControl == 1, "the control request performed exactly one body read");

        async Task RefusedAsync(string label, SyncAuthorityCommonDigestRequest request,
            SyncAuthorityCloudCommonDigestResolver? subject = null)
        {
            var result = await (subject ?? resolver).ResolveAsync(request);
            Assert(result is null, label + " returns no verified digest");
        }

        await RefusedAsync("a side that is neither left nor right", valid with { Side = "middle" });
        await RefusedAsync("the stored compatibility tag used as a request side",
            valid with { Side = SyncAuthorityCloudSides.LogicalLeft });
        await RefusedAsync("another root binding", valid with { RootBindingId = harness.RightRoot });
        await RefusedAsync("MD5 as the native algorithm", valid with
        {
            NativeAlgorithm = SyncContentIdentity.Md5, NativeHash = Md5Hex(content)
        });
        await RefusedAsync("an unregistered algorithm", valid with { NativeAlgorithm = "sha512" });
        await RefusedAsync("a malformed native hash", valid with { NativeHash = "not-a-hash" });
        await RefusedAsync("a directory kind", valid with { ObjectKind = "directory" });
        await RefusedAsync("a negative size", valid with { Size = -1 });
        await RefusedAsync("an unnormalized path", valid with { RelativePath = "/" + left.RelativePath });
        await RefusedAsync("a path that is not retained", valid with { RelativePath = "absent.txt" });
        await RefusedAsync("an object version that is not retained",
            valid with { ObjectVersionId = "version-not-retained" });
        await RefusedAsync("a native hash that is not the retained one",
            valid with { NativeHash = Sha256Hex(content) });
        await RefusedAsync("another side's retained version",
            valid with { ObjectVersionId = right.ObjectVersionId });
        await RefusedAsync("another retained size", valid with { Size = left.Size!.Value + 1 });

        await RefusedAsync("another retained provider account", valid,
            harness.ResolverWithLeftAccount("account-other"));
        var driftedNamespace = harness.ValidateDriftedNamespace("ns-drift");
        Assert(driftedNamespace.Refusals.Any(item => item.Error == SyncEndpointError.EndpointKindMismatch) &&
                driftedNamespace.Refusals.Any(item =>
                    item.Reason.Contains("is not the identity derived", StringComparison.Ordinal)),
            "a namespace change without an explicit pair rebind is refused before any resolver port exists");
        await RefusedAsync("an adapter with no range capability", valid,
            harness.ResolverWithLeftRangeStore(DispatchProxy.Create<ISyncRemoteStore, NoBehaviourStore>()));

        var foreignPair = await Harness.CreateAsync("foreign-pair", "g2");
        foreignPair.AttachLeft("target.txt", "id:foreign", "rev-foreign", content);
        foreignPair.AttachRightFixture(content);
        await foreignPair.ObserveAsync();
        await RefusedAsync("a version retained by another pair", valid, foreignPair.Resolver());

        Assert(harness.LeftWire!.DownloadCalls == readsAfterControl,
            "every refused request happened before any provider body read");
    }

    // ----- the sink contract ---------------------------------------------------------------------

    private static async Task SinkContractAsync()
    {
        var payload = Pattern(BlockSize + 7);
        var reference = ReferenceBlockHash(payload);

        var sink = new SyncContentHashingSink(SyncContentIdentity.DropboxBlockSha256, payload.Length);
        Assert(!sink.CanRead && !sink.CanSeek && sink.CanWrite,
            "the sink is write-only and not seekable");
        var single = new byte[1];
        Assert(Refused(() => sink.Seek(0, SeekOrigin.Begin)) &&
                Refused(() => sink.SetLength(1)) &&
                Refused(() => _ = sink.Length) &&
                Refused(() => _ = sink.Position) &&
                Refused(() => sink.Position = 0) &&
                Refused(() => sink.Read(single, 0, 1)) &&
                Refused(() => sink.Read(single.AsSpan())),
            "every read, seek, length change and position access is refused");
        var offset = 0;
        var index = 0;
        var chunkSizes = new[] { 1, 3, 5, 131071, 1, 4, 2097151, 7, 65537 };
        while (offset < payload.Length)
        {
            var length = Math.Min(chunkSizes[index % chunkSizes.Length], payload.Length - offset);
            if (index % 2 == 0) sink.Write(payload.AsSpan(offset, length));
            else await sink.WriteAsync(payload.AsMemory(offset, length));
            offset += length;
            index++;
        }
        Assert(sink.ByteCount == payload.Length && sink.IsComplete,
            "the sink counted exactly the reviewed bytes across irregular multiwrite chunks");
        var digest = sink.Finish();
        Assert(digest.CommonSha256 == Sha256Hex(payload) && digest.NativeDigest == reference &&
                digest.ByteCount == payload.Length &&
                digest.NativeAlgorithm == SyncContentIdentity.DropboxBlockSha256,
            "the sink computed both the common SHA-256 and the native digest over the streamed bytes");
        Assert(Refused(() => sink.Write(single, 0, 1)), "a completed sink refuses further use");

        var bounded = new SyncContentHashingSink(SyncContentIdentity.DropboxBlockSha256, 4);
        bounded.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
        var refused = false;
        try
        {
            bounded.Write(new byte[] { 5 }, 0, 1);
        }
        catch (IOException)
        {
            refused = true;
        }
        Assert(refused && bounded.ByteCount == 4 && bounded.CanWrite,
            "an excess byte is refused BEFORE it is retained or hashed");
        bounded.Dispose();
        Assert(Refused(() => bounded.Write(new byte[] { 6 }, 0, 1)) && Refused(() => bounded.Finish()),
            "a disposed sink refuses further writes and digests");

        var shortSink = new SyncContentHashingSink(SyncContentIdentity.Sha256, 4);
        shortSink.Write(new byte[] { 1, 2 }, 0, 2);
        var shortRefused = false;
        try
        {
            _ = shortSink.Finish();
        }
        catch (InvalidDataException)
        {
            shortRefused = true;
        }
        Assert(shortRefused, "an incomplete stream produces no digest");

        var unsupported = false;
        try
        {
            _ = new SyncContentHashingSink("sha512", 1);
        }
        catch (NotSupportedException)
        {
            unsupported = true;
        }
        Assert(unsupported, "the sink refuses an unregistered native algorithm");

        var large = Pattern(2 * BlockSize + 11);
        var buffer = new byte[64 * 1024];
        var before = GC.GetAllocatedBytesForCurrentThread();
        using (var streamed = new SyncContentHashingSink(SyncContentIdentity.DropboxBlockSha256, large.Length))
        {
            var position = 0;
            while (position < large.Length)
            {
                var length = Math.Min(buffer.Length, large.Length - position);
                Array.Copy(large, position, buffer, 0, length);
                streamed.Write(buffer.AsSpan(0, length));
                position += length;
            }
            Require(streamed.Finish().NativeDigest == ReferenceBlockHash(large),
                "the streamed large object matches the reference digest");
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert(allocated < large.Length / 2, "hashing " + large.Length + " bytes retained only " + allocated +
            " bytes of allocation, so no whole-file buffer was taken");
        Assert(SyncContentHashingSinkSourceBoundary(),
            "the seam contains no whole-file buffer and the resolver used the range capability");
    }

    private static bool SyncContentHashingSinkSourceBoundary()
    {
        var repositoryRoot = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (repositoryRoot is not null &&
               !File.Exists(Path.Combine(repositoryRoot.FullName, "SyncShark.sln")))
            repositoryRoot = repositoryRoot.Parent;
        if (repositoryRoot is null)
            throw new InvalidDataException("Run the cloud common digest checks from the repository tree.");
        var seam = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "SyncShark.Core",
            "SyncContentIdentity.cs")) + File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src",
            "SyncShark.Core", "DropboxContentHash.cs"));
        var resolver = File.ReadAllText(Path.Combine(repositoryRoot.FullName, "src", "SyncShark.Core",
            "SyncAuthorityCloudCommonDigestResolver.cs"));
        return !seam.Contains("MemoryStream", StringComparison.Ordinal) &&
               !seam.Contains("ToArray(", StringComparison.Ordinal) &&
               !resolver.Contains(".DownloadAsync(", StringComparison.Ordinal) &&
               resolver.Contains("DownloadRangeAsync(before.Expected, 0, request.Size, sink",
                   StringComparison.Ordinal);
    }

    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (NotSupportedException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    // ----- cancellation --------------------------------------------------------------------------

    private static async Task CancellationAsync()
    {
        var content = Pattern(900);
        var harness = await Harness.CreateAsync("cancellation", "i1");
        harness.AttachLeft("cancel.txt", "id:cancel", "rev-cancel", content);
        harness.AttachRightFixture(content);
        await harness.ObserveAsync();
        var left = await harness.LeftAtAsync("cancel.txt");
        var right = await harness.RightAtAsync("cancel.txt");
        var request = RequestFor(SyncAuthorityCloudCommonDigestResolver.LeftSide, left);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var propagated = false;
        try
        {
            await harness.Resolver().ResolveAsync(request, cancelled.Token);
        }
        catch (OperationCanceledException)
        {
            propagated = true;
        }
        Assert(propagated, "an already cancelled token reaches the caller instead of becoming unknown");
        Assert(harness.LeftWire!.DownloadCalls == 0, "a cancelled resolution performed no provider read");

        using var midFlight = new CancellationTokenSource();
        var cancellingStore = RangeFixtureStore();
        cancellingStore.Writer = (destination, offset, length) =>
        {
            midFlight.Cancel();
            throw new OperationCanceledException(midFlight.Token);
        };
        var midPropagated = false;
        try
        {
            await harness.ResolverWithLeftRangeStore(AsStore(cancellingStore))
                .ResolveAsync(request, midFlight.Token);
        }
        catch (OperationCanceledException)
        {
            midPropagated = true;
        }
        Assert(midPropagated,
            "a cancellation raised during the read propagates instead of being swallowed as unknown");

        using var comparisonCancel = new CancellationTokenSource();
        var comparisonStore = RangeFixtureStore();
        comparisonStore.Writer = (destination, offset, length) =>
        {
            comparisonCancel.Cancel();
            throw new OperationCanceledException(comparisonCancel.Token);
        };
        var comparison = harness.Comparison(harness.ResolverWithLeftRangeStore(AsStore(comparisonStore)));
        var comparisonPropagated = false;
        try
        {
            await comparison.CompareAsync(left, right, comparisonCancel.Token);
        }
        catch (OperationCanceledException)
        {
            comparisonPropagated = true;
        }
        Assert(comparisonPropagated, "the shared comparison observes the cancellation");
        Assert(comparison.CachedDigestCount == 0, "a cancelled comparison caches no successful digest");
    }

    // ----- the shared object overlap guard and the unverified ancestry surface --------------------

    private static async Task OverlapAndUnretainedAsync()
    {
        var content = Pattern(1200);
        var overlap = await Harness.CreateAsync("overlap", "j1", sameProvider: true);
        overlap.AttachLeft("left-copy.txt", "id:shared-object", "rev-shared", content);
        overlap.AttachRightDropbox("right-copy.txt", "id:shared-object", "rev-shared", content);
        var guarded = false;
        try
        {
            await overlap.ObserveAsync();
        }
        catch (SyncAuthorityCloudObjectOverlapException error)
        {
            guarded = true;
            Require(error.Message.Contains("can both reach one backing", StringComparison.Ordinal) &&
                    error.Message.Contains("id:shared-object", StringComparison.Ordinal),
                "the overlap refusal names the shared provider object it refuses");
        }
        Assert(guarded, "one shared backing object on two access routes refuses the observation");
        Assert((await overlap.LeftEntriesAsync()).Count == 0 && (await overlap.RightEntriesAsync()).Count == 0,
            "the overlap was refused before either scan generation was retained");
        Assert(overlap.LeftWire!.DownloadCalls == 0 && overlap.RightWire!.DownloadCalls == 0,
            "the overlap refusal performed no provider read");

        var unretainedRequest = new SyncAuthorityCommonDigestRequest(
            SyncAuthorityCloudCommonDigestResolver.LeftSide, "left-copy.txt", overlap.LeftRoot,
            "version-shared", "file", content.Length, SyncContentIdentity.DropboxBlockSha256,
            DropboxContentHash.Compute(content));
        Assert(await overlap.Resolver().ResolveAsync(unretainedRequest) is null,
            "an unretained overlapping object yields no verified digest");
        // The same refusal, reached through the shared comparison of an incomparable pair: a revision the
        // authority never retained yields no verified digest and surfaces as Unknown with its reason rather
        // than being normalised into equality or difference.
        var unretained = await Harness.CreateAsync("unretained-membership", "j3");
        unretained.AttachLeft("gone.txt", "id:unretained", "rev-unretained", content);
        unretained.AttachRightFixture(content);
        await unretained.ObserveAsync();
        var observedLeft = await unretained.LeftAtAsync("gone.txt");
        var observedRight = await unretained.RightAtAsync("gone.txt");
        var comparison = unretained.Comparison(unretained.Resolver());
        var result = await comparison.CompareAsync(
            observedLeft with { ObjectVersionId = "version-never-retained" }, observedRight);
        Assert(result.IsUnknown && !string.IsNullOrWhiteSpace(result.Reason),
            "an unretained revision surfaces as Unknown with its reason instead of being normalised; observed " +
            result.Kind + ": " + result.Reason);
        Assert(unretained.LeftWire!.DownloadCalls == 0,
            "the unretained membership performed no provider read");

        var unverified = await Harness.CreateAsync("unverified-ancestry", "j2", sameProvider: true,
            deferRoots: true);
        var unverifiedRefused = false;
        try
        {
            await unverified.RegisterChainsAsync(complete: false);
        }
        catch (InvalidOperationException error)
        {
            unverifiedRefused = true;
            Require(error.Message.Contains("cannot be established", StringComparison.Ordinal) &&
                    error.Message.Contains("refused rather than assumed disjoint", StringComparison.Ordinal),
                "the unverified ancestry reason is surfaced exactly");
        }
        Assert(unverifiedRefused,
            "a same-provider root with no verified ancestry is refused as Unverified, not treated as disjoint");
    }

    private static async Task PlannerIntegrationAsync()
    {
        var content = Pattern(3333);
        var equal = await Harness.CreateAsync("planner-equal", "k1");
        equal.AttachLeft("report.bin", "id:planner-equal", "rev-planner-equal", content);
        equal.AttachRightFixture(content);
        await equal.ObserveAsync();
        var comparison = equal.Comparison(equal.Resolver());
        var decisions = await new SyncAuthorityInitialPlanner(equal.Db, comparison).AdoptAsync(equal.Pair.Id);
        var decision = decisions.Single(item => item.RelativePath == "report.bin");
        Assert(decision.Kind == SyncDecisionKind.Baseline,
            "the real planner adopts a verified equal pair as a baseline; observed " + decision.Kind + ": " +
            decision.Reason);
        Assert(equal.LeftWire!.DownloadCalls == 1,
            "the planner equality decision performed exactly one range read");
        var points = await equal.Db.LoadLatestSynchronizationPointsAsync(equal.Pair.Id);
        Assert(points.Count == 1, "the adopted baseline retained one synchronization point");
        Assert((await equal.Db.ListRunnableOperationsForPairAsync(equal.Pair.Id)).Count == 0,
            "adopting a verified baseline plans no effect");

        var different = await Harness.CreateAsync("planner-different", "k2");
        var other = OtherPattern(content.Length);
        different.AttachLeft("report.bin", "id:planner-different", "rev-planner-different", content);
        different.AttachRightFixture(other);
        await different.ObserveAsync();
        var differentComparison = different.Comparison(different.Resolver());
        var differentDecisions = await new SyncAuthorityInitialPlanner(different.Db, differentComparison)
            .AdoptAsync(different.Pair.Id);
        var differentDecision = differentDecisions.Single(item => item.RelativePath == "report.bin");
        Assert(differentDecision.Kind == SyncDecisionKind.Conflict,
            "the real planner records a conflict for verified different content; observed " +
            differentDecision.Kind + ": " + differentDecision.Reason);
        Assert(different.LeftWire!.DownloadCalls == 1,
            "the differing planner decision performed exactly one range read");
        Assert((await different.Db.LoadOpenConflictIdsAsync(different.Pair.Id)).Count == 1,
            "the verified difference retained one open conflict");
        Assert((await different.Db.ListRunnableOperationsForPairAsync(different.Pair.Id)).Count == 0,
            "a verified conflict plans no effect");
    }

    // ----- helpers -------------------------------------------------------------------------------

    private static SyncAuthorityCommonDigestRequest RequestFor(string side, SyncAuthorityObservedEntry entry) =>
        new(side, entry.RelativePath, entry.RootBindingId, entry.ObjectVersionId, entry.ObjectKind,
            entry.Size ?? 0, SyncContentIdentity.DropboxBlockSha256, entry.ContentHash ?? "");

    private static PilotRemoteEntry GoogleEntry(string relativePath, byte[] content) =>
        new(relativePath, PilotEntryKind.File, "gd-" + relativePath, "gd-rev-1", content.Length,
            Sha256Hex(content));

    private static ISyncRemoteStore AsStore(RangeFixtureProxy fixture) => (ISyncRemoteStore)(object)fixture;

    private static HttpResponseMessage RangeResponse(WireFile file, byte[] slice, long offset, long declaredLength)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice)
        };
        response.Content.Headers.ContentRange =
            new ContentRangeHeaderValue(offset, offset + declaredLength - 1, file.Size ?? file.Content.Length);
        response.Headers.Add("Dropbox-API-Result", file.MetadataJson());
        return response;
    }

    private sealed record EndpointSpec(SyncEndpointKind Kind, string Provider, string ConnectionId,
        string AccountId, string RootNamespaceId, string HomeNamespaceId, string EndpointNamespaceId,
        string ProviderAccountId, string FolderId, string FolderPath);

    private static EndpointSpec DropboxEndpoint(string index, string folderId) => new(
        SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "conn-d" + index, "dbid-" + index, "ns-root-" + index,
        "ns-home-" + index, "ns-root-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, "dbid-" + index,
            "ns-root-" + index, "ns-home-" + index),
        folderId, "/Dropbox " + index);

    private static EndpointSpec DriveEndpoint(string index, string folderId) => new(
        SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "conn-g" + index, "perm-" + index,
        "key-" + index, "key-" + index, "perm-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, "perm-" + index,
            "key-" + index, "key-" + index),
        folderId, "/Drive " + index);

    private static SyncEndpoint EndpointFor(EndpointSpec spec, string folderId) =>
        SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId, spec.EndpointNamespaceId, folderId, spec.FolderPath);

    private static SyncAuthorityCloudHierarchySnapshot CorpusChain(EndpointSpec spec, string rootBindingId) =>
        new(spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, rootBindingId, spec.FolderId,
            new[] { CorpusRoot(spec), spec.FolderId })
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = CorpusRoot(spec),
            ChainNodeKeys = new[]
            {
                NodeKeysFor(spec.Provider, CorpusRoot(spec)),
                NodeKeysFor(spec.Provider, spec.FolderId)
            },
            ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(spec.Provider)
        };

    private static SyncAuthorityCloudHierarchyNodeKeys NodeKeysFor(string provider, string folderId) =>
        new(folderId, new[] { SyncCloudObjectKey.Native(provider, folderId) });

    private static string CorpusRoot(EndpointSpec spec) =>
        SyncEndpointProviders.Get(spec.Kind).PathsDescribeAncestry
            ? PilotPath.DropboxNamespaceRootIdentity(spec.RootNamespaceId)
            : "corpus-" + spec.FolderId;

    private sealed class Harness
    {
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal EndpointSpec LeftSpec = null!;
        internal EndpointSpec RightSpec = null!;
        internal string LeftRoot = "";
        internal string RightRoot = "";
        internal string LeftFolderId = "";
        internal string RightFolderId = "";
        internal DropboxWire? LeftWire;
        internal DropboxPilotClient? LeftClient;
        internal DropboxWire? RightWire;
        internal DropboxPilotClient? RightClient;
        internal FixtureStore? RightFixture;
        private bool _chainNeeded;
        private bool _chainsRegistered;

        internal ISyncRemoteStore RightAdapter =>
            (ISyncRemoteStore?)RightClient ?? RightFixture?.Store ??
            throw new InvalidDataException("The right adapter is not attached.");

        internal static async Task<Harness> CreateAsync(string caseName, string index,
            bool sameProvider = false, bool deferRoots = false)
        {
            var directory = Path.Combine(_root, caseName);
            Directory.CreateDirectory(directory);
            var harness = new Harness
            {
                LeftSpec = DropboxEndpoint(index + "l", "id:" + caseName + "-left"),
                RightSpec = sameProvider
                    ? DropboxEndpoint(index + "r", "id:" + caseName + "-right")
                    : DriveEndpoint(index + "r", "gd-" + caseName + "-right"),
                LeftRoot = "root-" + caseName + "-left",
                RightRoot = "root-" + caseName + "-right"
            };
            harness.LeftFolderId = harness.LeftSpec.FolderId;
            harness.RightFolderId = harness.RightSpec.FolderId;
            harness.Db = new SyncAuthorityDatabase(Path.Combine(directory, "syncshark.db"));
            await harness.Db.InitializeAsync(InstallationId, "profile-cloud-common-digest-316",
                SourceRevision, "{}");
            await harness.Db.RegisterProviderAccountAsync(harness.LeftSpec.ProviderAccountId, InstallationId,
                harness.LeftSpec.AccountId, harness.LeftSpec.RootNamespaceId, harness.LeftSpec.HomeNamespaceId,
                "CC2F left account", 1, cancellationToken: default, provider: harness.LeftSpec.Provider);
            await harness.Db.RegisterProviderAccountAsync(harness.RightSpec.ProviderAccountId, InstallationId,
                harness.RightSpec.AccountId, harness.RightSpec.RootNamespaceId, harness.RightSpec.HomeNamespaceId,
                "CC2F right account", 1, cancellationToken: default, provider: harness.RightSpec.Provider);
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                EndpointFor(harness.LeftSpec, harness.LeftFolderId),
                EndpointFor(harness.RightSpec, harness.RightFolderId), SyncMode.TwoWay), "CC2F " + caseName);
            var normalized = SyncPairStore.NormalizePair(draft);
            await harness.Db.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
            await harness.Db.RetainPairDefinitionAsync(normalized, true);
            harness.Pair = normalized;
            harness._chainNeeded = sameProvider;
            if (!deferRoots) await harness.RegisterChainsAsync(complete: sameProvider);
            return harness;
        }

        // complete:true supplies a verified, alias-covered chain for BOTH roots, so two roots of one provider
        // are legitimately separable. complete:false supplies the candidate chain only, leaving the retained
        // root ancestry unverified, which must therefore be refused as Unverified.
        internal async Task RegisterChainsAsync(bool complete = true)
        {
            if (_chainsRegistered) return;
            IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? leftChains =
                complete ? new[] { CorpusChain(LeftSpec, LeftRoot) } : null;
            IReadOnlyList<SyncAuthorityCloudHierarchySnapshot> rightChains = complete
                ? _chainNeeded
                    ? new[] { CorpusChain(RightSpec, RightRoot), CorpusChain(LeftSpec, LeftRoot) }
                    : Array.Empty<SyncAuthorityCloudHierarchySnapshot>()
                : new[] { CorpusChain(RightSpec, RightRoot) };
            await Db.RegisterCloudEndpointRootBindingAsync(LeftRoot, Pair.Id,
                SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId, LeftSpec.Provider,
                LeftSpec.ProviderAccountId, LeftSpec.EndpointNamespaceId, LeftFolderId, LeftSpec.FolderPath,
                cancellationToken: default, hierarchies: leftChains);
            await Db.RegisterCloudEndpointRootBindingAsync(RightRoot, Pair.Id,
                SyncAuthorityCloudSides.LogicalRight, 1, InstallationId, RightSpec.Provider,
                RightSpec.ProviderAccountId, RightSpec.EndpointNamespaceId, RightFolderId, RightSpec.FolderPath,
                cancellationToken: default, hierarchies: rightChains);
            _chainsRegistered = true;
        }

        internal void AttachLeft(string relativePath, string id, string revision, byte[] content,
            byte[]? servedBytes = null, Func<WireFile, long, long, HttpResponseMessage?>? downloadOverride = null,
            Action<WireFile>? afterDownload = null)
        {
            var wire = new DropboxWire(LeftSpec.AccountId, LeftSpec.RootNamespaceId, LeftSpec.HomeNamespaceId,
                "/Left", LeftFolderId);
            wire.Add(new WireFile
            {
                RelativePath = relativePath,
                Id = id,
                Revision = revision,
                Content = servedBytes ?? content,
                ContentHash = DropboxContentHash.Compute(content),
                Size = content.Length,
                DownloadOverride = downloadOverride,
                AfterDownload = afterDownload
            });
            LeftWire = wire;
            LeftClient = new DropboxPilotClient(new HttpClient(wire), new SyntheticTokenSource(), "/Left",
                Binding(LeftSpec), LeftFolderId);
        }

        internal void AttachRightDropbox(string relativePath, string id, string revision, byte[] content)
        {
            var wire = new DropboxWire(RightSpec.AccountId, RightSpec.RootNamespaceId,
                RightSpec.HomeNamespaceId, "/Right", RightFolderId);
            wire.Add(new WireFile
            {
                RelativePath = relativePath,
                Id = id,
                Revision = revision,
                Content = content,
                ContentHash = DropboxContentHash.Compute(content),
                Size = content.Length
            });
            RightWire = wire;
            RightClient = new DropboxPilotClient(new HttpClient(wire), new SyntheticTokenSource(), "/Right",
                Binding(RightSpec), RightFolderId);
        }

        internal void AttachRightFixture(byte[] content)
        {
            var path = LeftWire?.SinglePath() ??
                       throw new InvalidDataException("The left adapter and its file must be attached first.");
            var fixture = (FixtureStore)(object)DispatchProxy.Create<ISyncRemoteStore, FixtureStore>();
            fixture.Entries = new[] { GoogleEntry(path, content) };
            RightFixture = fixture;
        }

        internal void AdvanceRevision(string relativePath, string revision) =>
            (LeftWire ?? throw new InvalidDataException("The left adapter must be attached first."))
            .AdvanceRevision(relativePath, revision);

        internal void ForgetId(string id) =>
            (LeftWire ?? throw new InvalidDataException("The left adapter must be attached first."))
            .ForgetId(id);

        internal async Task ObserveAsync()
        {
            var set = SyncAuthorityCloudEndpointSet.FromPair(Pair, LeftClient!, RightAdapter, LeftRoot, RightRoot,
                LeftSpec.ProviderAccountId, RightSpec.ProviderAccountId);
            var coordinator = new SyncAuthorityObservationCoordinator(Pair, LeftClient!, Db, LeftRoot, RightRoot,
                onInventory: null, cloudEndpoints: set);
            Require(coordinator.UsesCloudEndpointRouting, "the cloud endpoint set routes the observation");
            await coordinator.ObserveAsync("initial");
        }

        internal async Task<IReadOnlyList<SyncAuthorityObservedEntry>> LeftEntriesAsync() =>
            await Db.LoadLatestCompleteObservationAsync(Pair.Id, SyncAuthorityCloudSides.LogicalLeft);

        internal async Task<IReadOnlyList<SyncAuthorityObservedEntry>> RightEntriesAsync() =>
            await Db.LoadLatestCompleteObservationAsync(Pair.Id, SyncAuthorityCloudSides.LogicalRight);

        internal async Task<SyncAuthorityObservedEntry> LeftAtAsync(string relativePath) =>
            await SingleAsync(await LeftEntriesAsync(), relativePath, "left");

        internal async Task<SyncAuthorityObservedEntry> RightAtAsync(string relativePath) =>
            await SingleAsync(await RightEntriesAsync(), relativePath, "right");

        private static Task<SyncAuthorityObservedEntry> SingleAsync(
            IReadOnlyList<SyncAuthorityObservedEntry> entries, string relativePath, string side)
        {
            var matches = entries.Where(item => item.RelativePath == relativePath).ToArray();
            Require(matches.Length == 1,
                "the " + side + " side retained exactly one observation for " + relativePath);
            return Task.FromResult(matches[0]);
        }

        internal SyncAuthorityCloudCommonDigestResolver Resolver() => new(Db, EndpointSet());

        internal SyncAuthorityCloudCommonDigestResolver ResolverWithLeftAccount(string providerAccountId) =>
            new(Db, SyncAuthorityCloudEndpointSet.FromPair(Pair, LeftClient!, RightAdapter, LeftRoot, RightRoot,
                providerAccountId, RightSpec.ProviderAccountId));

        internal SyncAuthorityCloudCommonDigestResolver ResolverWithLeftRangeStore(ISyncRemoteStore store) =>
            new(Db, SyncAuthorityCloudEndpointSet.FromPair(Pair, store, RightAdapter, LeftRoot, RightRoot,
                LeftSpec.ProviderAccountId, RightSpec.ProviderAccountId));

        // A native namespace change on an endpoint document is never a silent drift: the pair document no
        // longer derives the retained pair id, so no typed port with a foreign namespace can even be built.
        internal SyncEndpointValidation ValidateDriftedNamespace(string driftedNamespace)
        {
            var drifted = Pair with
            {
                LeftEndpoint = Pair.EndpointDocument!.Left! with { NamespaceId = driftedNamespace }
            };
            SyncAuthorityCloudEndpointSet.TryFromPair(drifted, LeftClient!, RightAdapter, LeftRoot, RightRoot,
                LeftSpec.ProviderAccountId, RightSpec.ProviderAccountId, out var set, out var validation);
            Require(set is null, "a drifted namespace must never produce a typed cloud port set");
            return validation;
        }

        internal SyncAuthorityCloudEndpointSet EndpointSet() =>
            SyncAuthorityCloudEndpointSet.FromPair(Pair, LeftClient!, RightAdapter, LeftRoot, RightRoot,
                LeftSpec.ProviderAccountId, RightSpec.ProviderAccountId);

        internal SyncAuthorityProviderAwareContentComparison Comparison(
            ISyncAuthorityCommonDigestResolver resolver)
        {
            // Each side's algorithm comes from its own registered provider definition on the typed port, so a
            // same-provider pair compares native identities directly and an incomparable pair resolves.
            var set = EndpointSet();
            return SyncAuthorityProviderAwareContentComparison.ForAlgorithmNames(
                set.Left.Definition.ContentHashAlgorithm, set.Right.Definition.ContentHashAlgorithm, resolver);
        }

        private static DropboxConnectionBinding Binding(EndpointSpec spec) =>
            new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, spec.AccountId,
                spec.RootNamespaceId, spec.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes).Normalize();
    }

    private sealed class WireFile
    {
        internal string RelativePath = "";
        internal string Id = "";
        internal string Revision = "";
        internal string RootPath = "/";
        internal byte[] Content = [];
        internal string? ContentHash;
        internal long? Size;
        internal Func<WireFile, long, long, HttpResponseMessage?>? DownloadOverride;
        internal Action<WireFile>? AfterDownload;

        internal string MetadataJson()
        {
            var display = RootPath == "/" ? "/" + RelativePath : RootPath + "/" + RelativePath;
            var builder = new StringBuilder();
            builder.Append("{\".tag\":\"file\",\"name\":\"").Append(NameOf(RelativePath))
                .Append("\",\"id\":\"").Append(Id).Append("\",\"rev\":\"").Append(Revision)
                .Append("\",\"size\":").Append((Size ?? Content.Length).ToString(CultureInfo.InvariantCulture));
            if (ContentHash is not null)
                builder.Append(",\"content_hash\":\"").Append(ContentHash).Append('"');
            builder.Append(",\"path_display\":\"").Append(display).Append('"')
                .Append(",\"path_lower\":\"").Append(display.ToLowerInvariant()).Append("\"}");
            return builder.ToString();
        }
    }

    private static string NameOf(string relativePath) =>
        relativePath.Contains('/') ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath;

    // An actual in-process Dropbox endpoint. It serves the account read, folder metadata, a recursive listing
    // and the exact range download, and it counts every call so a proof can assert the real read count.
    private sealed class DropboxWire : HttpMessageHandler
    {
        private readonly Dictionary<string, WireFile> _byId = new(StringComparer.Ordinal);
        private readonly List<WireFile> _files = [];
        private readonly string _root;
        private readonly string _rootId;

        internal DropboxWire(string accountId, string rootNamespaceId, string homeNamespaceId, string rootPath,
            string rootId)
        {
            AccountId = accountId;
            RootNamespaceId = rootNamespaceId;
            HomeNamespaceId = homeNamespaceId;
            _root = PilotPath.NormalizeDropboxRoot(rootPath);
            _rootId = rootId;
        }

        internal string AccountId { get; }
        internal string RootNamespaceId { get; }
        internal string HomeNamespaceId { get; }
        internal int AccountCalls { get; private set; }
        internal int MetadataCalls { get; private set; }
        internal int ListCalls { get; private set; }
        internal int DownloadCalls { get; private set; }
        internal long BytesServed { get; private set; }

        internal void Add(WireFile file)
        {
            file.RootPath = _root;
            _files.Add(file);
            _byId[file.Id] = file;
        }

        internal void ForgetId(string id) => _byId.Remove(id);

        internal string SinglePath()
        {
            Require(_files.Count == 1, "this wire serves exactly one file for the digest fixture");
            return _files[0].RelativePath;
        }

        internal void AdvanceRevision(string relativePath, string revision)
        {
            var matches = _files.Where(item => item.RelativePath == relativePath).ToArray();
            Require(matches.Length == 1, "the wire retains exactly one file for " + relativePath);
            matches[0].Revision = revision;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post)
                throw new InvalidDataException("A non-POST Dropbox call is not part of this packet");
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            {
                AccountCalls++;
                return Json("{\"account_id\":\"" + AccountId + "\",\"root_info\":{\"root_namespace_id\":\"" +
                            RootNamespaceId + "\",\"home_namespace_id\":\"" + HomeNamespaceId + "\"}}");
            }
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
            {
                ListCalls++;
                var entries = string.Join(',', _files.Select(item => item.MetadataJson()));
                return Json("{\"entries\":[" + entries +
                            "],\"cursor\":\"cc2f-cursor\",\"has_more\":false}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                MetadataCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var path = JsonDocument.Parse(body).RootElement.GetProperty("path").GetString() ?? "";
                if (path == _rootId)
                    return Json("{\".tag\":\"folder\",\"name\":\"" + NameOf(_root) + "\",\"id\":\"" +
                                _rootId + "\",\"path_lower\":\"" + _root.ToLowerInvariant() +
                                "\",\"path_display\":\"" + _root + "\"}");
                return _byId.TryGetValue(path, out var found) ? Json(found.MetadataJson()) : Conflict();
            }
            if (route.EndsWith("/files/download", StringComparison.Ordinal))
                return await DownloadAsync(request, cancellationToken);
            throw new InvalidDataException("An unexpected Dropbox route was requested: " + route);
        }

        private Task<HttpResponseMessage> DownloadAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;
            var arg = request.Headers.TryGetValues("Dropbox-API-Arg", out var values)
                ? JsonDocument.Parse(values.Single()).RootElement.GetProperty("path").GetString() ?? ""
                : "";
            var file = _files.FirstOrDefault(item => "rev:" + item.Revision == arg);
            if (file is null) return Task.FromResult(Conflict());
            var range = request.Headers.Range?.Ranges.SingleOrDefault();
            var offset = range?.From ?? 0;
            var length = (range?.To ?? ((file.Size ?? file.Content.Length) - 1)) - offset + 1;
            if (file.DownloadOverride is { } custom && custom(file, offset, length) is { } response)
            {
                BytesServed += length;
                file.AfterDownload?.Invoke(file);
                return Task.FromResult(response);
            }
            var slice = new byte[length];
            Array.Copy(file.Content, offset, slice, 0, Math.Min(length, file.Content.Length - offset));
            BytesServed += slice.Length;
            var normal = RangeResponse(file, slice, offset, length);
            file.AfterDownload?.Invoke(file);
            return Task.FromResult(normal);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Conflict() => new(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error_summary\":\"not_found\"}", Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class SyntheticTokenSource : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("synthetic-not-a-credential");
        }
    }

    public class FixtureStore : DispatchProxy
    {
        internal IReadOnlyList<PilotRemoteEntry> Entries { get; set; } = [];
        internal int VerifyCalls;
        internal int ListCalls;

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => List(),
            _ => throw new InvalidOperationException(
                "Unexpected provider effect in a cloud common digest fixture: " + method?.Name)
        };

        private object Verify()
        {
            VerifyCalls++;
            return Task.CompletedTask;
        }

        private object List()
        {
            ListCalls++;
            return Task.FromResult(Entries);
        }
    }

    private interface ICloudRangeFixture : ISyncRemoteStore, ISyncRemoteRangeStore
    {
    }

    public class RangeFixtureProxy : DispatchProxy
    {
        internal Action<Stream, long, long>? Writer;
        internal Func<PilotRemoteEntry, SyncRangeDownloadResult>? Receipt;
        internal int RangeCalls;
        internal long Offset;
        internal long Length;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteRangeStore.DownloadRangeAsync) => Range(arguments),
            _ => throw new InvalidOperationException(
                "Unexpected provider effect in a cloud common digest range fixture: " + method?.Name)
        };

        private Task<SyncRangeDownloadResult> Range(object?[]? arguments)
        {
            RangeCalls++;
            var expected = (PilotRemoteEntry)arguments![0]!;
            Offset = (long)arguments[1]!;
            Length = (long)arguments[2]!;
            var destination = (Stream)arguments[3]!;
            Writer?.Invoke(destination, Offset, Length);
            return Task.FromResult(Receipt is null
                ? new SyncRangeDownloadResult(expected.Id!, expected.Revision!, expected.Size,
                    expected.ContentHash!, Offset, Length)
                : Receipt(expected));
        }
    }

    public class NoBehaviourStore : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException("This fixture store implements no provider behaviour.");
    }

    private static RangeFixtureProxy RangeFixtureStore() =>
        (RangeFixtureProxy)(object)DispatchProxy.Create<ICloudRangeFixture, RangeFixtureProxy>();
}
