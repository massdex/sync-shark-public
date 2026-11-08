using System.Buffers.Binary;
using System.Security.AccessControl;
using System.Security.Principal;
using Massdex.SyncShark;

// AutoSync design section 7 (M1 / C-M1) requires the compiled Core page reader: one native response
// becomes a classified finite page without materialising records, a record that crosses a requested
// finite end is left for the next read, malformed/unsupported/no-progress responses are classified
// rather than thrown, a waiting read cancels through CancelIoEx, and the native dummy boundary agrees
// with the decoder. These checks drive the production reader and its pure page walk; no substituted
// implementation is asserted here.
internal static class NtfsUsnPageChecks
{
    private const int Header = 60;
    private static int _assertions;

    private static readonly Action<ulong, ulong, ulong, long, uint, FileAttributes> Noop =
        (fileId, parentFileId, usn, timestamp, reason, attributes) => { };

    internal static async Task RunAsync(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("A fixture root is required.", nameof(root));
        _assertions = 0;
        LinkResponseValidation();
        EmptyPageCompletesAtTheSameCursor();
        EmptyPageWithBackwardContinuationDoesNotProgress();
        OpenPageReportsMoreAndAdvances();
        RecordsBeforeTheStartAreCoveredWithoutDelivery();
        FiniteEndReachedExactlyIsComplete();
        FiniteEndBehindTheBoundaryIsMore();
        CrossingRecordIsLeftForTheNextPage();
        FiniteEndInJournalPagePaddingIsComplete();
        NonAdvancingBoundaryIsNoProgress();
        MalformedAndTruncatedRecordsAreInvalid();
        TrailingPartialRecordIsInvalid();
        UnsupportedRecordVersionIsClassified();
        CallbackFailureLeavesThePageUnadvanced();
        RecordFieldsKeepTheFullNativeIdentity();
        StreamingAllocationDoesNotGrowWithRecordCount();
        ArgumentValidationIsStateFree();
        await CancelledBeforeStartReturnsCancelledTaskAsync();
        await NonDriveRootIsUnavailableAsync();
        var native = await NativeDummyPageAsync(Path.Combine(Path.GetFullPath(root), "ntfs-usn-page"));
        if (native) NativeLinkNames(Path.Combine(Path.GetFullPath(root), "ntfs-link-names"));
        Console.WriteLine("PASS NTFS USN page: " + _assertions + " assertions; the compiled Core page reader " +
            "classifies complete/more/unavailable/journal-changed/unsupported/invalid/no-progress " +
            "pages, leaves a record that crosses a finite end for the next read, and delivers full native identities " +
            "without per-record allocation" +
            (native ? ", and the real NTFS dummy boundary agrees, including a waiting read that cancels."
                    : " (native NTFS boundary skipped on this host)."));
    }

    private static void LinkResponseValidation()
    {
        // FILE_LINKS_INFORMATION header plus one entry and one UTF-16 character.
        var valid = new byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(valid, 30);
        BinaryPrimitives.WriteUInt32LittleEndian(valid.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(valid.AsSpan(16), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(valid.AsSpan(24), 1);
        valid[28] = (byte)'a';
        Require(NtfsIdentityPaths.ParseLinks(valid, 1) is [(42, "a")], "link parser reads the documented character-length layout");
        foreach (var (offset, value) in new (int, uint)[] { (0, 0), (4, 0), (4, 2), (8, 8), (8, 32), (24, 0), (24, uint.MaxValue) })
        {
            var malformed = (byte[])valid.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(offset), value);
            var refused = false;
            try { NtfsIdentityPaths.ParseLinks(malformed, 1); }
            catch (NtfsLinkEnumerationChangedException) { refused = true; }
            Require(refused, "malformed link response fails without publishing a partial set");
        }
        foreach (var length in Enumerable.Range(0, valid.Length))
        {
            var refused = false;
            try { NtfsIdentityPaths.ParseLinks(valid.AsSpan(0, length), 1); }
            catch (NtfsLinkEnumerationChangedException) { refused = true; }
            Require(refused, "truncated link response fails without publishing a partial set");
        }
    }

    private static void NativeLinkNames(string root)
    {
        Directory.CreateDirectory(root);
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(root, "inside")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            var original = Path.Combine(outside, "original.txt");
            var alias = Path.Combine(inside, "alias.txt");
            var third = Path.Combine(inside, "a-long-non-eight-dot-three-link-name.txt");
            File.WriteAllText(original, "synthetic");
            var fileId = unchecked((ulong)WindowsDirectoryListing.List(outside).Single(item => item.Name == "original.txt").FileId);
            var rootIdentity = NtfsIdentityPaths.ReadRootIdentity(inside);
            var single = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, false, default);
            Require(single.Path == original && single.Links is null, "ordinary identity resolution keeps the single-name fast path");
            NativeTestFiles.CreateHardLink(alias, original);
            NativeTestFiles.CreateHardLink(third, original);
            var multiple = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, false, default);
            Require(multiple.Links is not null && multiple.Links.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(new[] { original, alias, third }), "native enumeration includes all outside and inside names before filtering");
            QualifyLinkAccess(inside, outside, original, fileId, rootIdentity, [original, alias, third]);
            File.Delete(alias);
            var removed = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, true, default);
            Require(removed.Links is not null && removed.Links.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(new[] { original, third }), "native enumeration observes a removed middle alias");
            File.Delete(third);
            var longLinks = Enumerable.Range(0, 4).Select(index => Path.Combine(inside,
                new string((char)('a' + index), 180) + ".txt")).ToArray();
            foreach (var link in longLinks) NativeTestFiles.CreateHardLink(link, original);
            var overflow = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, true, default);
            Require(overflow.Links is not null && overflow.Links.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(longLinks.Append(original)), "Natural overflow returns every long-named link, never a partial response.");
            foreach (var link in longLinks) File.Delete(link);
            var forced = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, true, default);
            Require(forced.Links is [var remaining] && string.Equals(remaining, original, StringComparison.OrdinalIgnoreCase),
                "hard-link evidence forces enumeration even when only one name remains");
            var parked = Path.Combine(outside, "parked.txt");
            var foreignAlias = Path.Combine(inside, "foreign.txt");
            var afterQuery = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, true, default,
                null, () => File.Move(original, parked), null);
            try
            {
                Require(afterQuery.Links is [var queryName] && string.Equals(queryName, original, StringComparison.OrdinalIgnoreCase),
                    "B1 returns query-time names joined to later parent observations, not a return-time namespace snapshot.");
            }
            finally { File.Move(parked, original); }
            var retiredParent = Directory.CreateDirectory(Path.Combine(root, "retired-parent")).FullName;
            var retiringAlias = Path.Combine(retiredParent, "alias.txt");
            NativeTestFiles.CreateHardLink(retiringAlias, original);
            var parentId = NtfsIdentityPaths.ReadRootIdentity(retiredParent).FileId;
            var refusedParent = false;
            try
            {
                NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, true, default, null, null,
                    (id, after) =>
                    {
                        if (id != parentId || after) return;
                        File.Move(retiringAlias, alias);
                        Directory.Delete(retiredParent);
                    });
            }
            catch (NtfsLinkEnumerationChangedException) { refusedParent = true; }
            finally { File.Delete(alias); }
            Require(refusedParent, "B2 retired parent throws changed enumeration, never identity absence.");
            void Restore()
            {
                File.Delete(original);
                File.Move(parked, original);
            }
            try
            {
                var raced = NtfsIdentityPaths.ResolveLinksById(inside, rootIdentity, fileId, true, default,
                    () =>
                    {
                        File.Move(original, parked);
                        File.WriteAllText(original, "replacement");
                        NativeTestFiles.CreateHardLink(foreignAlias, original);
                    }, null, null);
                Require(raced.Links is [var heldName] && string.Equals(heldName, parked, StringComparison.OrdinalIgnoreCase),
                    "Native completeness: B0 replacement returns exactly the held original's parked name.");
            }
            finally
            {
                if (File.Exists(parked)) Restore();
                File.Delete(foreignAlias);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void QualifyLinkAccess(string root, string parent, string file, ulong fileId,
        DirectoryIdentity rootIdentity, string[] expected)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        Console.WriteLine("Native link access token administrator=" +
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));
        foreach (var target in new[] { file, parent })
        {
            var directory = target == parent;
            FileSystemSecurity original = directory ? new DirectoryInfo(target).GetAccessControl() : new FileInfo(target).GetAccessControl();
            FileSystemSecurity restricted = directory ? new DirectorySecurity() : new FileSecurity();
            restricted.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm());
            restricted.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny));
            void Set(FileSystemSecurity security)
            {
                if (directory) new DirectoryInfo(target).SetAccessControl((DirectorySecurity)security);
                else new FileInfo(target).SetAccessControl((FileSecurity)security);
            }
            try
            {
                Set(restricted);
                var result = NtfsIdentityPaths.ResolveLinksById(root, rootIdentity, fileId, true, default);
                Require(result.Links is not null && result.Links.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(expected),
                    directory ? "Attributes-only query resolves a parent denying directory listing." :
                        "Attributes-only query succeeds when file data reads are denied.");
            }
            catch (Exception error)
            {
                Console.WriteLine("Native attributes-only qualification failed for " + (directory ? "parent: " : "file: ") + error);
                throw;
            }
            finally
            {
                original.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
                Set(original);
            }
        }
    }

    private static void EmptyPageCompletesAtTheSameCursor()
    {
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(ReadOnlySpan<byte>.Empty, 500, null, 500, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.Complete && consumed == 500 && delivered == 0,
            "an empty immediate response at the same cursor is Complete so the caller waits instead of re-reading");
    }

    private static void EmptyPageWithBackwardContinuationDoesNotProgress()
    {
        var status = NtfsUsnJournal.WalkUsnRecords(ReadOnlySpan<byte>.Empty, 500, null, 499, out var consumed,
            (record, boundary) => { });
        Require(status == NtfsUsnPageStatus.NoProgress && consumed == 500,
            "a native continuation behind the requested start is NoProgress, not an empty complete page");
    }

    private static void OpenPageReportsMoreAndAdvances()
    {
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(Page(100), 100, null, 160, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.More && consumed == 160 && delivered == 1,
            "an open page advances to the native continuation and reports More for the caller to continue");
    }

    private static void RecordsBeforeTheStartAreCoveredWithoutDelivery()
    {
        ulong seen = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(Page(80, 140), 100, null, 200, out var consumed,
            (record, boundary) => seen = BinaryPrimitives.ReadUInt64LittleEndian(record[24..]));
        Require(status == NtfsUsnPageStatus.More && consumed == 200 && seen == 140,
            "a record before the requested start advances coverage without being delivered, and later records are delivered");
    }

    private static void FiniteEndReachedExactlyIsComplete()
    {
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(Page(100), 100, 160, 160, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.Complete && consumed == 160 && delivered == 1,
            "a finite page consumed to its exact end is Complete and still delivers its records");
    }

    private static void FiniteEndBehindTheBoundaryIsMore()
    {
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(Page(100), 100, 200, 160, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.More && consumed == 160 && delivered == 1,
            "a short page that stops before the requested end reports the consumed boundary as More, never the requested end");
    }

    private static void CrossingRecordIsLeftForTheNextPage()
    {
        var data = Page(100);
        var delivered = 0;
        var first = NtfsUsnJournal.WalkUsnRecords(data, 100, 120, 160, out var consumed,
            (record, boundary) => delivered++);
        Require(first == NtfsUsnPageStatus.More && consumed == 100 && delivered == 0,
            "a record whose end crosses the requested finite end is not consumed and is not delivered");
        var second = NtfsUsnJournal.WalkUsnRecords(data, consumed, 160, 160, out var final,
            (record, boundary) => delivered++);
        Require(second == NtfsUsnPageStatus.Complete && final == 160 && delivered == 1,
            "the retained crossing record is consumed exactly once by the next page");
    }

    // The native query reports the end of the last record; the next record may start on the next journal page.
    private static void FiniteEndInJournalPagePaddingIsComplete()
    {
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(Page(100, 4096), 100, 160, 4156, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.Complete && consumed == 160 && delivered == 1,
            "a finite end in journal-page padding is reached exactly, and the later page's record is left");
    }

    private static void NonAdvancingBoundaryIsNoProgress()
    {
        var delivered = 0;
        var single = NtfsUsnJournal.WalkUsnRecords(Page(100), 100, null, 100, out var consumed,
            (record, boundary) => delivered++);
        Require(single == NtfsUsnPageStatus.NoProgress && consumed == 100 && delivered == 0,
            "a boundary that does not advance past the requested start is NoProgress, never an empty page");
        var pair = NtfsUsnJournal.WalkUsnRecords(Page(100, 160), 100, null, 160, out var second,
            (record, boundary) => delivered++);
        Require(pair == NtfsUsnPageStatus.NoProgress && second == 160 && delivered == 1,
            "a later non-advancing record stops the page at the last advanced boundary instead of looping");
    }

    private static void MalformedAndTruncatedRecordsAreInvalid()
    {
        var delivered = 0;
        var shortHeader = new byte[Header - 1];
        var truncated = NtfsUsnJournal.WalkUsnRecords(shortHeader, 100, null, 160, out var truncatedAt,
            (record, boundary) => delivered++);
        Require(truncated == NtfsUsnPageStatus.InvalidRecord && truncatedAt == 100 && delivered == 0,
            "a response shorter than one record header is InvalidRecord, not an empty page");

        var shortLength = Page(100);
        BinaryPrimitives.WriteUInt32LittleEndian(shortLength, 4);
        Require(NtfsUsnJournal.WalkUsnRecords(shortLength, 100, null, 160, out _, (record, boundary) => { }) ==
            NtfsUsnPageStatus.InvalidRecord, "a record length below the fixed header is InvalidRecord");

        var overlong = Page(100);
        BinaryPrimitives.WriteUInt32LittleEndian(overlong, (uint)overlong.Length + 1);
        Require(NtfsUsnJournal.WalkUsnRecords(overlong, 100, null, 160, out _, (record, boundary) => { }) ==
            NtfsUsnPageStatus.InvalidRecord, "a record length beyond the returned response is InvalidRecord");
    }

    private static void TrailingPartialRecordIsInvalid()
    {
        var data = new byte[Header + 30];
        Page(100).CopyTo(data, 0);
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(data, 100, null, 160, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.InvalidRecord && consumed == 100 && delivered == 0,
            "a partial trailing record makes the whole page InvalidRecord rather than silently dropping bytes");
    }

    private static void UnsupportedRecordVersionIsClassified()
    {
        var delivered = 0;
        var status = NtfsUsnJournal.WalkUsnRecords(PageVersion(3, 100), 100, null, 160, out var consumed,
            (record, boundary) => delivered++);
        Require(status == NtfsUsnPageStatus.UnsupportedRecord && consumed == 100 && delivered == 0,
            "a record layout this decoder does not support is UnsupportedRecord and is never delivered");
    }

    private static void CallbackFailureLeavesThePageUnadvanced()
    {
        ulong consumed = 0;
        try
        {
            NtfsUsnJournal.WalkUsnRecords(Page(100, 160), 100, null, 220, out consumed,
                (record, boundary) => { throw new InvalidOperationException("consumer failure"); });
            throw new InvalidOperationException("the page walk swallowed a consumer failure");
        }
        catch (InvalidOperationException error) when (error.Message == "consumer failure")
        {
            _assertions++;
        }
        Require(consumed == 100, "a consumer failure leaves the partially assembled page uncommittable at its start");
    }

    private static void RecordFieldsKeepTheFullNativeIdentity()
    {
        var data = PageVersion(2, 0xAAAA_BBBB_CCCC_DDDDUL);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), 0x0000_0001_0000_0010UL);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 0x0000_0002_0000_0020UL);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), 133_000_000_000_000_000L);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 0x123);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(52), (uint)(FileAttributes.Archive | FileAttributes.Directory));
        var decoded = default((ulong FileId, ulong Parent, ulong Usn, long Timestamp, uint Reason, FileAttributes Attributes));
        var status = NtfsUsnJournal.WalkUsnRecords(data, 0xAAAA_BBBB_CCCC_DDDDUL, null, 0xBBBB_BBBB_BBBB_BBBBUL,
            out _, (record, boundary) =>
            {
                NtfsUsnJournal.ReadUsnRecordFields(record, out var fileId, out var parent, out var usn,
                    out var timestamp, out var reason, out var attributes);
                decoded = (fileId, parent, usn, timestamp, reason, attributes);
            });
        Require(status == NtfsUsnPageStatus.More &&
            decoded.FileId == 0x0000_0001_0000_0010UL && decoded.Parent == 0x0000_0002_0000_0020UL &&
            decoded.Usn == 0xAAAA_BBBB_CCCC_DDDDUL && decoded.Timestamp == 133_000_000_000_000_000L &&
            decoded.Reason == 0x123 && decoded.Attributes == (FileAttributes.Archive | FileAttributes.Directory),
            "the reader preserves the full 64-bit file reference including sequence bits, parent, USN, timestamp, reason and attributes");
    }

    private static void StreamingAllocationDoesNotGrowWithRecordCount()
    {
        var small = WalkAllocation(128);
        var large = WalkAllocation(512);
        Require(small < 2048, "a 128-record page allocates a constant amount (" + small + " bytes)");
        Require(large < 2048, "a 512-record page allocates a constant amount (" + large + " bytes)");
        Require(large - small <= 1024,
            "streaming allocation does not grow with record count (" + small + " to " + large + " bytes)");
    }

    private static long WalkAllocation(int count)
    {
        var usns = new ulong[count];
        for (var index = 0; index < count; index++) usns[index] = (ulong)(index + 1) * Header;
        var data = Page(usns);
        var continuation = (ulong)(count + 1) * Header;
        var delivered = 0;
        _ = NtfsUsnJournal.WalkUsnRecords(data, Header, null, continuation, out _,
            (record, boundary) => delivered++);
        Require(delivered == count, count + "-record open page delivers every record");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = NtfsUsnJournal.WalkUsnRecords(data, Header, null, continuation, out var consumed,
            (record, boundary) => delivered++);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(status == NtfsUsnPageStatus.More && consumed == continuation,
            count + "-record open page advances to the native continuation");
        return allocated;
    }

    private static void ArgumentValidationIsStateFree()
    {
        Throws<ArgumentNullException>(() =>
            { _ = NtfsUsnJournal.ReadChangePageAsync(null!, 1, 0, null, false, Noop, default); }, "a null root");
        Throws<ArgumentException>(() =>
            { _ = NtfsUsnJournal.ReadChangePageAsync("  ", 1, 0, null, false, Noop, default); }, "a blank root");
        Throws<ArgumentNullException>(() =>
            { _ = NtfsUsnJournal.ReadChangePageAsync("C:\\", 1, 0, null, false, null!, default); }, "a null record sink");
        Throws<ArgumentOutOfRangeException>(() =>
            { _ = NtfsUsnJournal.ReadChangePageAsync("C:\\", 1, 10, 9, false, Noop, default); },
            "a finite end before the start");
        Throws<ArgumentException>(() =>
            { _ = NtfsUsnJournal.ReadChangePageAsync("C:\\", 1, 0, 9, true, Noop, default); },
            "a finite page that waits for new records");
    }

    private static async Task CancelledBeforeStartReturnsCancelledTaskAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var task = NtfsUsnJournal.ReadChangePageAsync("C:\\", 1, 0, null, false, Noop, cancelled.Token);
        Require(task.IsCanceled, "a cancelled caller receives a cancelled page task instead of a native read");
        try
        {
            await task;
            throw new InvalidOperationException("the cancelled page task completed.");
        }
        catch (OperationCanceledException)
        {
            _assertions++;
        }
    }

    private static async Task NonDriveRootIsUnavailableAsync()
    {
        var page = await NtfsUsnJournal.ReadChangePageAsync(@"\\no-such-host\share", 1, 700, 700, false, Noop, default);
        Require(page.Status == NtfsUsnPageStatus.Unavailable && page.ConsumedNextUsn == 700 &&
            page.NativeError is null,
            "a root that is not a local drive is Unavailable with no consumed progress and no invented native error");
    }

    private static async Task<bool> NativeDummyPageAsync(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP native NTFS USN page checks: they require a real Windows NTFS journal.");
            return false;
        }
        if (Directory.Exists(folder))
            throw new IOException("Use a fresh NTFS USN page fixture: " + folder);
        Directory.CreateDirectory(folder);
        if (!NtfsUsnJournal.TryReadState(folder, out var journalId, out _, out _, out _, out _, out _,
                out var stateError))
        {
            throw new PlatformNotSupportedException(
                "The isolated fixture volume has no readable NTFS USN journal (native error " + stateError + ").");
        }
        var before = NtfsUsnJournal.ReadCheckpoint(folder);
        Require(before.JournalId == journalId, "the checkpoint and the state query agree on the journal identity");

        var created = new List<ulong>();
        for (var index = 0; index < 3; index++)
        {
            var path = Path.Combine(folder, "dummy-" + index + ".txt");
            await File.WriteAllTextAsync(path, "dummy " + index);
            Require(NtfsUsnJournal.TryReadFileUsn(path, out var evidence),
                "created dummy " + index + " exposes native identity evidence");
            created.Add(evidence!.FileId);
        }
        var through = NtfsUsnJournal.ReadCheckpoint(folder);
        Require(through.JournalId == before.JournalId && through.NextUsn > before.NextUsn,
            "the dummy writes advanced the journal inside the captured identity");

        var empty = await NtfsUsnJournal.ReadChangePageAsync(folder, journalId, (ulong)before.NextUsn,
            (ulong)before.NextUsn, false, Noop, default);
        Require(empty.Status == NtfsUsnPageStatus.Complete && empty.ConsumedNextUsn == (ulong)before.NextUsn &&
            empty.NativeError is null,
            "an empty finite range at the current journal position is a valid Complete page");

        var observed = new List<(ulong FileId, ulong Usn)>();
        var crossing = await NtfsUsnJournal.ReadChangePageAsync(folder, journalId, (ulong)before.NextUsn,
            (ulong)before.NextUsn + 1, false, Collect(observed), default);
        Require(crossing.Status == NtfsUsnPageStatus.More && crossing.ConsumedNextUsn == (ulong)before.NextUsn &&
            observed.Count == 0,
            "a real record that crosses a one-byte finite end is left for the next read");

        var completed = crossing;
        var position = crossing.ConsumedNextUsn;
        while (completed.Status == NtfsUsnPageStatus.More)
        {
            var next = await NtfsUsnJournal.ReadChangePageAsync(folder, journalId, position, (ulong)through.NextUsn,
                false, Collect(observed), default);
            Require(next.ConsumedNextUsn > position, "a continuing finite page always advances past its start");
            position = next.ConsumedNextUsn;
            completed = next;
        }
        Require(completed.Status == NtfsUsnPageStatus.Complete && completed.ConsumedNextUsn == (ulong)through.NextUsn &&
            completed.NativeError is null,
            "the continued finite page consumes exactly to the captured end");
        Require(observed.All(entry => entry.Usn < (ulong)through.NextUsn),
            "no delivered record lies at or beyond the captured finite end");
        Require(created.All(id => observed.Any(entry => entry.FileId == id)),
            "every created dummy file appears with its full native identity");

        var changed = await NtfsUsnJournal.ReadChangePageAsync(folder, journalId + 1, (ulong)through.NextUsn, null,
            false, Noop, default);
        Require(changed.Status == NtfsUsnPageStatus.JournalChanged && changed.NativeError is not null,
            "a different journal id is classified JournalChanged with the native error, never read as records");

        Require(await BlockedCancellationAsync(folder, journalId, (ulong)through.NextUsn),
            "a waiting native page read cancels instead of hanging");
        return true;
    }

    private static async Task<bool> BlockedCancellationAsync(string folder, ulong journalId, ulong position)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.CancelAfter(TimeSpan.FromMilliseconds(150));
            try
            {
                var page = await NtfsUsnJournal.ReadChangePageAsync(folder, journalId, position, null, true,
                    Noop, cancelled.Token).WaitAsync(TimeSpan.FromSeconds(30));
                Require(page.ConsumedNextUsn >= position,
                    "a waiting page that completes before cancellation still advances from its start");
                position = page.ConsumedNextUsn;
            }
            catch (OperationCanceledException)
            {
                return true;
            }
        }
        return false;
    }

    private static Action<ulong, ulong, ulong, long, uint, FileAttributes> Collect(
        List<(ulong FileId, ulong Usn)> into) =>
        (fileId, parentFileId, usn, timestamp, reason, attributes) => into.Add((fileId, usn));

    private static byte[] Page(params ulong[] usns) => PageVersion(2, usns);

    private static byte[] PageVersion(ushort version, params ulong[] usns)
    {
        var data = new byte[Header * usns.Length];
        for (var index = 0; index < usns.Length; index++)
        {
            var record = data.AsSpan(index * Header, Header);
            BinaryPrimitives.WriteUInt32LittleEndian(record, Header);
            BinaryPrimitives.WriteUInt16LittleEndian(record[4..], version);
            BinaryPrimitives.WriteUInt64LittleEndian(record[8..], 0x1000_0000_0000_0000UL + (ulong)index);
            BinaryPrimitives.WriteUInt64LittleEndian(record[16..], 0x2000_0000_0000_0000UL);
            BinaryPrimitives.WriteUInt64LittleEndian(record[24..], usns[index]);
            BinaryPrimitives.WriteInt64LittleEndian(record[32..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record[40..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(record[52..], (uint)FileAttributes.Archive);
            BinaryPrimitives.WriteUInt16LittleEndian(record[56..], 0);
            BinaryPrimitives.WriteUInt16LittleEndian(record[58..], Header);
        }
        return data;
    }

    private static void Throws<T>(Action action, string what) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            _assertions++;
            return;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                what + " threw " + error.GetType().Name + " instead of " + typeof(T).Name + ".", error);
        }
        throw new InvalidOperationException(what + " did not throw " + typeof(T).Name + ".");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition) throw new InvalidOperationException("NTFS USN page check failed: " + what);
        _assertions++;
    }
}
