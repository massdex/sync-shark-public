using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LocalBin (`--engine-checks local`), written by LocalBin's writer.</summary>
public static class LocalBinChecks
{
    // OS facts the sink and the classification order are built from: TSF_DELETE_RECYCLE_IF_POSSIBLE and E_ABORT.
    private const uint RecycleFlag = 0x0080;
    private const int EAbort = unchecked((int)0x80004004);

    private static readonly Type SinkType = typeof(LocalBin).GetNestedType("DeleteSink", BindingFlags.NonPublic)!;
    private static readonly Type SinkContract =
        typeof(LocalBin).GetNestedType("IFileOperationProgressSink", BindingFlags.NonPublic)!;
    // The seam skips the Shell receipt and cannot supply an abort flag, so these candidate members are driven directly.
    private static readonly MethodInfo ClassifyMethod =
        typeof(LocalBin).GetMethod("Classify", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo VerifyReceiptMethod =
        typeof(LocalRecycleReceipt).GetMethod("VerifyAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo CheckResultMethod =
        typeof(LocalBin).GetMethod("CheckResult", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static void Run()
    {
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        var previous = LocalBin.SendOverride;
        var root = Directory.CreateTempSubdirectory(nameof(LocalBinChecks)).FullName;
        try
        {
            var pair = new PairConfig("pair", "Pair A", root, "", "", [], [],
                FirstRunPolicy.NewestWins, true, null);
            var listing = new LocalListing(pair, new PathScope(pair, [pair]), (_, _, _, _) => { });
            var serial = VolumeSpace.Serial(root);
            await CheckSeamAsync(root);
            CheckSetupFailures(root);
            await CheckProofAsync(listing, root, serial);
            await CheckPermanentAsync(listing, root, serial);
            CheckReceipts(listing, root, serial);
            CheckSink(listing, root, serial);
        }
        finally
        {
            LocalBin.SendOverride = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckSeamAsync(string root)
    {
        var folder = Path.Combine(root, "seam-folder");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "child"), "child");
        var caller = Environment.CurrentManagedThreadId;
        var called = 0;
        LocalBin.SendOverride = path =>
        {
            Check.Require(path == folder, nameof(LocalRecycle.SendAsync));
            Check.Require(Environment.CurrentManagedThreadId != caller, nameof(Environment.CurrentManagedThreadId));
            Check.Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, nameof(ApartmentState.STA));
            called++;
            return true;
        };
        var (recycled, locator, victim) = await LocalRecycle.SendAsync(folder, null, CancellationToken.None);
        Check.Require(recycled == new RemovalResult(RemovalOutcome.Removed, null, null, null) && locator is null &&
            victim is null && called == 1,
            "The seam reports one verified expected recycle of a folder on a dedicated STA thread.");

        called = 0;
        LocalBin.SendOverride = _ => { called++; return false; };
        (recycled, locator, victim) = await LocalRecycle.SendAsync(folder, null, CancellationToken.None);
        Check.Require(recycled.Outcome == RemovalOutcome.NoEffect && recycled.Cause == TextKey.RecycleUnavailable &&
            locator is null && victim is null && called == 1 && Directory.Exists(folder),
            "A refused recycle is a definite no-effect with the unavailable cause and keeps the folder.");

        var file = Path.Combine(root, "unproved");
        File.WriteAllText(file, "bytes");
        called = 0;
        LocalBin.SendOverride = _ => { called++; return true; };
        var failure = await FailureAsync(() => LocalRecycle.SendAsync(file, null, CancellationToken.None));
        Check.Require(failure is ArgumentNullException && called == 0 && File.ReadAllText(file) == "bytes",
            "A file removal without its selected proof is refused before any effect.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        called = 0;
        failure = await FailureAsync(() => LocalRecycle.SendAsync(folder, null, cancelled.Token));
        Check.Require(failure is OperationCanceledException && called == 0 && Directory.Exists(folder),
            "A cancelled send stops before the Shell.");
    }

    private static void CheckSetupFailures(string root)
    {
        var path = Path.Combine(root, "setup");
        foreach (var (code, cause) in new (int, TextKey)[]
        {
            (unchecked((int)0x80270027), TextKey.FailLocalInUse),
            (unchecked((int)0x80070005), TextKey.RecycleFailed),
        })
        {
            var failure = Capture(() => CheckResultMethod.Invoke(null, new object?[] { code, path }));
            Check.Require(failure is ItemFailedException item && item.Cause == cause && item.Path == path &&
                item.InnerException?.GetType() == Marshal.GetExceptionForHR(code)?.GetType(),
                $"A Shell setup failure {code:x8} stops before any effect with its classified cause.");
        }
    }

    private static async Task CheckProofAsync(LocalListing listing, string root, uint serial)
    {
        var file = Path.Combine(root, "proven");
        var bytes = new byte[] { 1, 2, 3, 4 };
        File.WriteAllBytes(file, bytes);
        var entry = listing.One("proven")!;
        var proof = new RemovalProof(entry, serial.ToString(CultureInfo.InvariantCulture),
            DropboxContentHash.Compute(bytes));

        // A top-bit file id is a negative invariant decimal in the listing's spelling that LocalBin compares.
        Check.Require(LocalIdentity.FileIdText(0x8000000000000001UL) == "-9223372036854775807",
            nameof(LocalIdentity.FileIdText));

        var called = 0;
        LocalBin.SendOverride = path =>
        {
            Check.Require(path == file, nameof(LocalRecycle.SendAsync));
            called++;
            return true;
        };
        var (result, locator, victim) = await LocalRecycle.SendAsync(file, proof, CancellationToken.None);
        Check.Require(result == new RemovalResult(RemovalOutcome.Removed, null, null, null) && locator is null &&
            victim is null && called == 1 && File.ReadAllBytes(file).SequenceEqual(bytes),
            "The held identity, size and complete selected hash verify the selected proof before the Shell acts.");

        foreach (var (description, stale) in new (string, RemovalProof)[]
        {
            ("a different file id", proof with { Expected = entry with { Id = NextId(entry.Id) } }),
            ("a hexadecimal file id", proof with { Expected = entry with { Id = HexId(entry.Id) } }),
            ("a different volume", proof with { VolumeId = (serial + 1).ToString(CultureInfo.InvariantCulture) }),
            ("a different size", proof with { Expected = entry with { Size = entry.Size + 1 } }),
            ("different selected bytes", proof with { ContentHash = DropboxContentHash.Compute(new byte[] { 9 }) }),
        })
        {
            called = 0;
            LocalBin.SendOverride = _ => { called++; return true; };
            var failure = await FailureAsync(() => LocalRecycle.SendAsync(file, stale, CancellationToken.None));
            Check.Require(failure is ChangedUnderneathException changed && changed.Path == file && called == 0 &&
                File.ReadAllBytes(file).SequenceEqual(bytes), $"A proof with {description} changes nothing.");
            var observation = ((ChangedUnderneathException)failure!).ObservedLocal;
            Check.Require(description == "different selected bytes"
                    ? observation == entry with { ContentHash = DropboxContentHash.Compute(bytes) }
                    : observation is null,
                $"Only a complete selected-content mismatch exports endpoint-relative content: {description}.");
        }

        called = 0;
        LocalBin.SendOverride = _ => { called++; return false; };
        var (refused, refusedLocator, refusedVictim) = await LocalRecycle.SendAsync(file, proof, CancellationToken.None);
        Check.Require(refused.Outcome == RemovalOutcome.NoEffect && refused.Cause == TextKey.RecycleUnavailable &&
            refusedLocator is null && refusedVictim is null && called == 1 && File.ReadAllBytes(file).SequenceEqual(bytes),
            "A refused recycle keeps the selected file and reports no effect.");

        // A vanished target and a target held without sharing both stop before the Shell.
        var vanished = Path.Combine(root, "vanished");
        File.WriteAllBytes(vanished, bytes);
        var vanishedProof = new RemovalProof(listing.One("vanished")!,
            serial.ToString(CultureInfo.InvariantCulture), DropboxContentHash.Compute(bytes));
        File.Delete(vanished);
        called = 0;
        LocalBin.SendOverride = _ => { called++; return true; };
        var vanishedFailure = await FailureAsync(() =>
            LocalRecycle.SendAsync(vanished, vanishedProof, CancellationToken.None));
        Check.Require(vanishedFailure is ItemFailedException && called == 0,
            "A selected file that vanished before its removal is refused before any effect.");

        var locked = Path.Combine(root, "locked");
        File.WriteAllBytes(locked, bytes);
        var lockedProof = new RemovalProof(listing.One("locked")!, serial.ToString(CultureInfo.InvariantCulture),
            DropboxContentHash.Compute(bytes));
        called = 0;
        LocalBin.SendOverride = _ => { called++; return true; };
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedFailure = await FailureAsync(() =>
                LocalRecycle.SendAsync(locked, lockedProof, CancellationToken.None));
            Check.Require(lockedFailure is ItemFailedException { Cause: TextKey.FailLocalInUse } && called == 0,
                "A file held without sharing is refused with the in-use cause before any effect.");
        }

        // The Shell wrapper hides its Win32 result under a generic exception; the specific cause must still surface.
        foreach (var (error, cause) in new (Exception, TextKey)[]
        {
            (new COMException(string.Empty, unchecked((int)0x80270021)), TextKey.FailLocalInUseOrAccessDenied),
            (new COMException(string.Empty, unchecked((int)0x80270027)), TextKey.FailLocalInUse),
            (new IOException("native failure", new Win32Exception(32)), TextKey.FailLocalInUse),
            (new IOException("disk full", new Win32Exception(112)), TextKey.FailLocalNoSpace),
            (new COMException(string.Empty, unchecked((int)0x80070005)), TextKey.RecycleFailed),
        })
        {
            LocalBin.SendOverride = _ => throw error;
            var failure = await FailureAsync(() => LocalRecycle.SendAsync(file, proof, CancellationToken.None));
            Check.Require(failure is ItemFailedException item && item.Cause == cause &&
                ReferenceEquals(item.InnerException, error), $"A Shell failure keeps its classified cause {cause}.");
        }
    }

    // The removal owner's durable arming is the entry's authority; the test supplies one exactly as LocalSide would.
    private static PendingEffect Armed(RemovalProof proof) => new(1, "permanent", 0, PendingKind.LocalDelete,
        PendingPhase.PermanentArmed, proof.Expected.Path, proof, null, null, null, null, null, true, null);

    private static async Task CheckPermanentAsync(LocalListing listing, string root, uint serial)
    {
        var file = Path.Combine(root, "permanent");
        var bytes = new byte[] { 5, 6, 7 };
        File.WriteAllBytes(file, bytes);
        var proof = new RemovalProof(listing.One("permanent")!, serial.ToString(CultureInfo.InvariantCulture),
            DropboxContentHash.Compute(bytes));
        LocalBin.SendOverride = _ => throw new InvalidOperationException("The disposition never sends through the Shell.");
        Check.Require(await LocalDisposition.DeletePermanentlyAsync(file, Armed(proof), CancellationToken.None) ==
            new RemovalResult(RemovalOutcome.Removed, null, null, null) && !File.Exists(file),
            "The armed exact-handle disposition revalidates the selected file and removes it.");

        var other = Path.Combine(root, "permanent-other");
        File.WriteAllBytes(other, bytes);
        var otherEntry = listing.One("permanent-other")!;
        foreach (var (description, stale) in new (string, RemovalProof)[]
        {
            ("a different file id", new RemovalProof(otherEntry with { Id = NextId(otherEntry.Id) },
                serial.ToString(CultureInfo.InvariantCulture), DropboxContentHash.Compute(bytes))),
            ("a hexadecimal file id", new RemovalProof(otherEntry with { Id = HexId(otherEntry.Id) },
                serial.ToString(CultureInfo.InvariantCulture), DropboxContentHash.Compute(bytes))),
            ("different selected bytes", new RemovalProof(otherEntry,
                serial.ToString(CultureInfo.InvariantCulture), DropboxContentHash.Compute(new byte[] { 9 }))),
            ("a different size", new RemovalProof(otherEntry with { Size = otherEntry.Size + 1 },
                serial.ToString(CultureInfo.InvariantCulture), DropboxContentHash.Compute(bytes))),
        })
        {
            var failure = await FailureAsync(() =>
                LocalDisposition.DeletePermanentlyAsync(other, Armed(stale), CancellationToken.None));
            Check.Require(failure is ChangedUnderneathException && File.ReadAllBytes(other).SequenceEqual(bytes),
                $"A permanent disposition with {description} removes nothing.");
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var current = new RemovalProof(otherEntry, serial.ToString(CultureInfo.InvariantCulture),
            DropboxContentHash.Compute(bytes));
        var cancelledFailure = await FailureAsync(() =>
            LocalDisposition.DeletePermanentlyAsync(other, Armed(current), cancelled.Token));
        Check.Require(cancelledFailure is OperationCanceledException && File.ReadAllBytes(other).SequenceEqual(bytes),
            "A cancelled permanent disposition removes nothing.");
    }

    private static void CheckReceipts(LocalListing listing, string root, uint serial)
    {
        var expectedFile = Path.Combine(root, "receipt-expected");
        var victimFile = Path.Combine(root, "receipt-victim");
        var expectedBytes = new byte[] { 1, 1, 1 };
        var victimBytes = new byte[] { 2, 2 };
        File.WriteAllBytes(expectedFile, expectedBytes);
        File.WriteAllBytes(victimFile, victimBytes);
        var expected = Identity(listing.One("receipt-expected")!, serial);
        var victimEntry = listing.One("receipt-victim")!;
        var serialText = serial.ToString(CultureInfo.InvariantCulture);

        var matched = VerifyReceipt(expectedFile, expected);
        Check.Require(matched.Result == new RemovalResult(RemovalOutcome.Removed, null, null, null) &&
            matched.BinLocator == expectedFile && matched.Victim is null,
            "A receipt carrying the held identity is the verified expected recycle.");

        // The expected original is gone, so only the mismatching receipt can be read and hashed as the victim.
        File.Delete(expectedFile);
        var retained = VerifyReceipt(victimFile, expected);
        var victimHash = DropboxContentHash.Compute(victimBytes);
        Check.Require(retained.Result.Outcome == RemovalOutcome.Retained && retained.BinLocator == victimFile &&
            retained.Victim is { } victim && victim.Path == victimFile && victim.VolumeId == serialText &&
            victim.FileId == victimEntry.Id && victim.Size == victimBytes.Length &&
            victim.ContentHash == victimHash && victimHash != DropboxContentHash.Compute(expectedBytes),
            "A mismatching receipt supplies the victim proof from its own bytes, never the expected object's.");

        var missing = Path.Combine(root, "receipt-missing");
        var unreadable = VerifyReceipt(missing, expected);
        Check.Require(unreadable.Result.Outcome == RemovalOutcome.Unknown && unreadable.BinLocator == missing &&
            unreadable.Victim is null, "An unreadable receipt is unknown and still names the Shell-returned path.");

        var unidentified = VerifyReceipt(victimFile, null);
        Check.Require(unidentified.Result.Outcome == RemovalOutcome.Unknown &&
            unidentified.BinLocator == victimFile && unidentified.Victim is null,
            "A receipt without a held identity is unknown, never a verified recycle.");

        var unnamed = VerifyReceipt(null, expected);
        Check.Require(unnamed.Result.Outcome == RemovalOutcome.Unknown && unnamed.BinLocator is null &&
            unnamed.Victim is null, "An unnamed receipt is unknown, never an expected recycle.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledRead = VerifyReceipt(victimFile, expected, cancelled.Token);
        Check.Require(cancelledRead.Result.Outcome == RemovalOutcome.Unknown &&
            cancelledRead.BinLocator == victimFile && cancelledRead.Victim is null,
            "A cancelled receipt proof read is an unknown effect, not an absent one.");
    }

    private static void CheckSink(LocalListing listing, string root, uint serial)
    {
        var file = Path.Combine(root, "sink");
        File.WriteAllText(file, "sink");
        var held = Identity(listing.One("sink")!, serial);
        var accepted = NewSink(file, held);
        Check.Require(SinkCall(accepted, "PreDeleteItem", RecycleFlag, IntPtr.Zero) == 0 && VetoCause(accepted) is null,
            "The sink allows the exact held pathname for a recycle.");

        File.WriteAllText(Path.Combine(root, "sink-other"), "other");
        var changed = NewSink(file, Identity(listing.One("sink-other")!, serial));
        Check.Require(SinkCall(changed, "PreDeleteItem", RecycleFlag, IntPtr.Zero) == EAbort &&
            VetoCause(changed) == TextKey.FailChanged, "A changed victim identity is vetoed at the last moment.");

        var missing = NewSink(Path.Combine(root, "sink-missing"), held);
        Check.Require(SinkCall(missing, "PreDeleteItem", RecycleFlag, IntPtr.Zero) == EAbort &&
            VetoCause(missing) == TextKey.FailChanged, "An unreadable last-moment pathname is vetoed.");

        var noRecycle = NewSink(file, held);
        Check.Require(SinkCall(noRecycle, "PreDeleteItem", 0u, IntPtr.Zero) == EAbort &&
            VetoCause(noRecycle) == TextKey.RecycleUnavailable,
            "A missing recycle flag is a definite no-effect veto.");

        foreach (var (code, cause) in new (int, TextKey)[]
        {
            (unchecked((int)0x80270027), TextKey.FailLocalInUse),
            (unchecked((int)0x80270028), TextKey.FailLocalInUse),
            (unchecked((int)0x80270021), TextKey.FailLocalInUseOrAccessDenied),
            (unchecked((int)0x80070021), TextKey.FailLocalInUse),
            (unchecked((int)0x80070005), TextKey.RecycleFailed),
            (unchecked((int)0x80270022), TextKey.RecycleFailed),
        })
        {
            var sink = NewSink(file, null);
            Check.Require(SinkCall(sink, "PostDeleteItem", 0u, IntPtr.Zero, code, IntPtr.Zero) == 0,
                "The sink records the first specific per-item result.");
            Check.Require(Classify(sink, 0).Result is { Outcome: RemovalOutcome.Unknown } unknown && unknown.Cause == cause,
                $"The specific per-item result {code:x8} keeps its classified cause.");
        }

        var first = NewSink(file, null);
        SinkCall(first, "PostDeleteItem", 0u, IntPtr.Zero, unchecked((int)0x80270028), IntPtr.Zero);
        SinkCall(first, "PostDeleteItem", 0u, IntPtr.Zero, unchecked((int)0x80270021), IntPtr.Zero);
        Check.Require(Classify(first, EAbort).Result is { Cause: TextKey.FailLocalInUse },
            "The first specific per-item failure outranks a later one and the generic aggregate abort.");

        var succeeded = NewSink(file, null);
        SinkCall(succeeded, "PostDeleteItem", 0u, IntPtr.Zero, unchecked((int)0x80270028), IntPtr.Zero);
        SinkCall(succeeded, "PostDeleteItem", 0u, IntPtr.Zero, 0, IntPtr.Zero);
        Check.Require(Classify(succeeded, 0).Result is { Cause: TextKey.FailLocalInUse },
            "A later successful per-item result never replaces the first specific failure.");

        var finished = NewSink(file, null);
        SinkCall(finished, "PostDeleteItem", 0u, IntPtr.Zero, unchecked((int)0x80270027), IntPtr.Zero);
        SinkCall(finished, "FinishOperations", unchecked((int)0x80070005));
        Check.Require(Classify(finished, 0).Result is { Cause: TextKey.FailLocalInUse },
            "A generic FinishOperations failure never replaces the specific per-item cause.");

        var finish = NewSink(file, null);
        SinkCall(finish, "FinishOperations", unchecked((int)0x80070005));
        Check.Require(Classify(finish, 0).Result is { Outcome: RemovalOutcome.Unknown, Cause: TextKey.RecycleFailed },
            "A generic finish failure without a specific item result is unknown, never success.");

        var aggregate = NewSink(file, null);
        Check.Require(Classify(aggregate, EAbort).Result is
                { Outcome: RemovalOutcome.Unknown, Cause: TextKey.RecycleFailed },
            "A generic aggregate abort without a specific item result is unknown and never an in-use cause.");

        var veto = NewSink(file, null);
        SinkCall(veto, "PreDeleteItem", 0u, IntPtr.Zero);
        SinkCall(veto, "PostDeleteItem", 0u, IntPtr.Zero, unchecked((int)0x80270027), IntPtr.Zero);
        Check.Require(Classify(veto, EAbort).Result is
                { Outcome: RemovalOutcome.NoEffect, Cause: TextKey.RecycleUnavailable },
            "The sink veto is the only definite no-effect and outranks every failure.");

        var operationType = typeof(LocalBin).GetNestedType("IFileOperation", BindingFlags.NonPublic)!;
        foreach (var (result, aborted, cause) in new (int, bool, TextKey?)[]
        {
            (0, true, TextKey.RecycleFailed),
            (EAbort, false, TextKey.RecycleFailed),
            (0, false, null),
        })
        {
            var operation = DispatchProxy.Create(operationType, typeof(AbortQueryProxy));
            var query = (AbortQueryProxy)operation;
            query.HResult = result;
            query.Aborted = aborted;
            var classified = Classify(NewSink(file, null), 0, operation);
            Check.Require(classified.Result is { Outcome: RemovalOutcome.Unknown } unknown &&
                unknown.Cause == cause && classified.BinLocator is null && query.Calls == 1,
                "The actual classifier preserves the abort-query result and calls it once.");
        }
    }

    /// <summary>B-164: the recycled folder's receipt walk proves the root and every subfolder on its own handle and
    /// yields the contents rebased onto the original path; a receiptless shell success is never a folder success.</summary>
    public static void RunReceiptContents()
    {
        ReceiptContentsAsync().GetAwaiter().GetResult();
        ReceiptlessIsUnknown();
    }

    private static async Task ReceiptContentsAsync()
    {
        var root = Directory.CreateTempSubdirectory(nameof(RunReceiptContents)).FullName;
        try
        {
            var unbound = new PairConfig("pair", "Pair A", root, "", "", [], [], FirstRunPolicy.NewestWins, true, null);
            var pair = unbound with { Roots = new RootBinding(root, "/Parent", "account",
                WindowsDirectoryListing.ReadIdentity(root),
                new DropboxRootIdentity("account", "namespace", "namespace", "remote")) };
            var scope = new PathScope(pair, [pair]);
            var listing = new LocalListing(pair, scope, (_, _, _, _) => { });
            const string original = "Gone/Folder";

            // (a) A nested tree with mixed-case and non-ASCII names: rebased paths, IDs, sizes and times.
            var tree = Path.Combine(root, "receipt");
            Directory.CreateDirectory(Path.Combine(tree, "MiXed", "Ünïcødé"));
            Directory.CreateDirectory(Path.Combine(tree, "Empty"));
            File.WriteAllText(Path.Combine(tree, "top.txt"), "1");
            File.WriteAllText(Path.Combine(tree, "MiXed", "Two.TXT"), "22");
            File.WriteAllText(Path.Combine(tree, "MiXed", "Ünïcødé", "drei.bin"), "333");
            var walked = new List<Listed>();
            await foreach (var listed in new LocalListing(unbound with { LocalRoot = tree }, scope, (_, _, _, _) => { })
                .Walk("", new ListOptions(true, true), CancellationToken.None))
                walked.Add(listed);
            var contents = await ContentsAsync(pair, scope, tree, original, listing.One("receipt")!.Id);
            var expected = walked.Select(listed => Fact(PathNames.Child(original, listed.Path), listed.Entry!))
                .OrderBy(fact => fact, StringComparer.Ordinal).ToList();
            Check.Require(walked.Count == 6 && contents.Select(listed => Fact(listed.Path, listed.Entry!))
                    .OrderBy(fact => fact, StringComparer.Ordinal).SequenceEqual(expected) &&
                contents.All(listed => listed.Entry!.Path == listed.Path &&
                    listed.Path.StartsWith(original + PathNames.PathSeparator, StringComparison.Ordinal)),
                "(a) the receipt walk yields every recycled descendant rebased onto the original path");

            // (b) An authenticated empty receipt yields nothing and does not throw.
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            Check.Require((await ContentsAsync(pair, scope, empty, original, listing.One("empty")!.Id)).Count == 0,
                "(b) an empty receipt is proved empty");

            // (c) A replaced receipt root: the folder ID is another directory's.
            Check.Require(await FailureAsync(() => ContentsAsync(pair, scope, empty, original,
                    listing.One("receipt")!.Id)) is InventoryIncompleteException,
                "(c) a receipt root with another identity is an incomplete inventory");

            // (d) A subfolder swapped between its parent's enumeration and its own open is refused before any entry.
            var swap = Path.Combine(root, "swap");
            Directory.CreateDirectory(Path.Combine(swap, "s"));
            File.WriteAllText(Path.Combine(swap, "s", "old.txt"), "old");
            var yielded = new List<string>();
            var swapping = new LocalListing(unbound with { LocalRoot = swap }, scope, (_, _, _, _) => { });
            var options = new ListOptions(true, true)
            {
                RequireListedIdentity = true,
                Progress = (folder, _) =>
                {
                    if (folder.Length != 0) return;
                    Directory.Move(Path.Combine(swap, "s"), Path.Combine(swap, "s-old"));
                    Directory.CreateDirectory(Path.Combine(swap, "s"));
                    File.WriteAllText(Path.Combine(swap, "s", "new.txt"), "new");
                }
            };
            var swapped = await FailureAsync(async () =>
            {
                await foreach (var listed in swapping.Walk("", options, CancellationToken.None)) yielded.Add(listed.Path);
            });
            Check.Require(swapped is ChangedUnderneathException &&
                !yielded.Any(path => path.StartsWith("s" + PathNames.PathSeparator, StringComparison.Ordinal)),
                "(d) a replaced subfolder throws before any of its entries is yielded");

            // (e) A missing receipt throws.
            Check.Require(await FailureAsync(() => ContentsAsync(pair, scope, Path.Combine(root, "missing"), original,
                    listing.One("receipt")!.Id)) is not null,
                "(e) a missing receipt throws");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task<List<Listed>> ContentsAsync(PairConfig pair, PathScope scope, string receipt,
        string path, string folderId)
    {
        var contents = new List<Listed>();
        await foreach (var listed in LocalRecycleReceipt.ContentsAsync(pair, scope, receipt, path, folderId,
            CancellationToken.None))
            contents.Add(listed);
        return contents;
    }

    private static string Fact(string path, Entry entry) => string.Join("|", path, entry.Id, entry.IsFolder,
        entry.Size.ToString(CultureInfo.InvariantCulture), entry.Modified.UtcTicks.ToString(CultureInfo.InvariantCulture));

    // T5b: the shell named no receipt, so the folder's contents cannot be checked; production never takes the seam branch.
    private static void ReceiptlessIsUnknown()
    {
        var file = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var sink = NewSink(file, null);
        SinkType.GetProperty("Received", BindingFlags.Instance | BindingFlags.Public)!.SetValue(sink, true);
        var operation = DispatchProxy.Create(
            typeof(LocalBin).GetNestedType("IFileOperation", BindingFlags.NonPublic)!, typeof(AbortQueryProxy));
        Check.Require(Classify(sink, 0, operation) is { Result: { Outcome: RemovalOutcome.Unknown, Cause: null },
                BinLocator: null },
            "T5b a receiptless recycle is Unknown, never Removed");
    }

    /// <summary>B-164 G4 (informational, no ratio): times the real-shell receipt walk against the ordinary walk of an
    /// identical synthetic tree at 5,000 and 50,000 files. It recycles through the real Shell, so it runs only on an
    /// ephemeral GitHub-hosted runner; a runner with no usable Recycle Bin fails as G4 MISSING, never passes.</summary>
    public static void MeasureReceiptWalk()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
        {
            Console.WriteLine("SKIP B164-MEASURE: the real-shell recycle runs only on an ephemeral GitHub-hosted runner");
            Check.Skip();
            return;
        }
        var previous = LocalBin.SendOverride;
        LocalBin.SendOverride = null;
        try
        {
            foreach (var files in new[] { 5_000, 50_000 }) MeasureReceiptWalkAsync(files).GetAwaiter().GetResult();
        }
        finally { LocalBin.SendOverride = previous; }
    }

    private static async Task MeasureReceiptWalkAsync(int files)
    {
        var root = Directory.CreateTempSubdirectory(nameof(MeasureReceiptWalk)).FullName;
        try
        {
            // Depth 4 with fan-out 10 and five files per leaf folder; names padded to a path length of about 60.
            foreach (var copy in new[] { "walked", "recycled" })
                for (var index = 0; index < files; index++)
                {
                    var leaf = index / 5;
                    var folder = Path.Combine(root, copy, "level1-" + leaf / 1000 % 10, "level2-" + leaf / 100 % 10,
                        "level3-" + leaf / 10 % 10, "level4-" + leaf % 10);
                    Directory.CreateDirectory(folder);
                    File.WriteAllBytes(Path.Combine(folder, "synthetic-file-" + index.ToString("D6",
                        CultureInfo.InvariantCulture) + ".txt"), [1]);
                }
            var unbound = new PairConfig("pair", "Pair A", root, "", "", [], [], FirstRunPolicy.NewestWins, true, null);
            var pair = unbound with { Roots = new RootBinding(root, "/Parent", "account",
                WindowsDirectoryListing.ReadIdentity(root),
                new DropboxRootIdentity("account", "namespace", "namespace", "remote")) };
            var scope = new PathScope(pair, [pair]);
            var listing = new LocalListing(pair, scope, (_, _, _, _) => { });
            var folderId = listing.One("recycled")!.Id;
            var walk = await TimedAsync(listing.Walk("walked", new ListOptions(true, true), CancellationToken.None));
            var (result, receipt, _) = await LocalRecycle.SendAsync(Path.Combine(root, "recycled"), null,
                CancellationToken.None);
            Check.Require(result.Outcome == RemovalOutcome.Removed && receipt is not null,
                "B164-MEASURE G4 MISSING: the runner's Shell named no Recycle Bin receipt (" + result.Outcome + ")");
            var inspection = await TimedAsync(LocalRecycleReceipt.ContentsAsync(pair, scope, receipt!, "recycled",
                folderId, CancellationToken.None));
            Console.WriteLine($"B164-MEASURE files={files} entries walk={walk.Entries} receipt={inspection.Entries}; " +
                $"elapsed-ms walk={walk.Elapsed.TotalMilliseconds:F0} receipt={inspection.Elapsed.TotalMilliseconds:F0}; " +
                $"entries-per-s walk={walk.Entries / walk.Elapsed.TotalSeconds:F0} " +
                $"receipt={inspection.Entries / inspection.Elapsed.TotalSeconds:F0}; " +
                $"peak-managed-bytes walk={walk.PeakManaged} receipt={inspection.PeakManaged}");
            Check.Require(inspection.Entries == walk.Entries,
                "B164-MEASURE the receipt walk yields every recycled entry the identical tree walk yields");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // Peak managed heap is sampled every 1,000 entries, so a shorter spike between samples is not seen.
    private static async Task<(long Entries, TimeSpan Elapsed, long PeakManaged)> TimedAsync(IAsyncEnumerable<Listed> items)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long entries = 0, peak = GC.GetTotalMemory(false);
        await foreach (var _ in items)
            if (++entries % 1000 == 0) peak = Math.Max(peak, GC.GetTotalMemory(false));
        watch.Stop();
        return (entries, watch.Elapsed, Math.Max(peak, GC.GetTotalMemory(false)));
    }

    private static PilotDirectoryIdentity Identity(Entry entry, uint serial) =>
        new(serial, unchecked((ulong)long.Parse(entry.Id, CultureInfo.InvariantCulture)));

    private static (RemovalResult Result, string? BinLocator, StageProof? Victim) VerifyReceipt(
        string? receiptPath, PilotDirectoryIdentity? expected, CancellationToken ct = default) =>
        ((Task<(RemovalResult Result, string? BinLocator, StageProof? Victim)>)VerifyReceiptMethod
            .Invoke(null, new object?[] { receiptPath, expected, ct })!).GetAwaiter().GetResult();

    private static object NewSink(string fullPath, PilotDirectoryIdentity? held) =>
        Activator.CreateInstance(SinkType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, new object?[] { fullPath, held }, null)!;

    private static int SinkCall(object sink, string method, params object?[] arguments) =>
        (int)SinkContract.GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!.Invoke(sink, arguments)!;

    private static TextKey? VetoCause(object sink) =>
        (TextKey?)SinkType.GetProperty("VetoCause", BindingFlags.Instance | BindingFlags.Public)!.GetValue(sink);

    // The Shell instance is only reached after the sink veto, the per-item result, the aggregate and the finish result.
    private static (RemovalResult? Result, string? BinLocator) Classify(
        object sink, int aggregate, object? operation = null) =>
        ((RemovalResult?, string?))ClassifyMethod.Invoke(null,
            new object?[] { operation, sink, aggregate, null })!;

    private static string NextId(string id) =>
        (long.Parse(id, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);

    private static string HexId(string id) =>
        unchecked((ulong)long.Parse(id, CultureInfo.InvariantCulture)).ToString("x16", CultureInfo.InvariantCulture);

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (TargetInvocationException error) { return error.InnerException; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> FailureAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return null;
        }
        catch (Exception error) { return error; }
    }

    /// <summary>Supplies only the abort query to the actual candidate classifier.</summary>
    public class AbortQueryProxy : DispatchProxy
    {
        public int HResult { get; set; }
        public bool Aborted { get; set; }
        public int Calls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != "GetAnyOperationsAborted" || args is not { Length: 1 })
                throw new InvalidOperationException("Unexpected operation member.");
            Calls++;
            args[0] = Aborted;
            return HResult;
        }
    }
}
