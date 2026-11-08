using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Massdex.SyncShark.Engine.Local;
using Microsoft.Win32.SafeHandles;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for LocalListing (`--engine-checks local`), written by LocalListing's writer.</summary>
public static class LocalListingChecks
{
    private const int MaxNameCharacters = 255;

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory(nameof(LocalListingChecks));
        try
        {
            var pair = new PairConfig(nameof(PairConfig), nameof(LocalListingChecks), root.FullName, "", "",
                [nameof(SyncIgnoreKind.FolderName) + ':' + nameof(PathScope)],
                [new Exclusion(Side.Local, nameof(Exclusion), true)], FirstRunPolicy.NewestWins, true, null);
            var nested = pair with
            {
                PairId = nameof(PathNames),
                LocalRoot = Path.Combine(root.FullName, nameof(PathNames)),
                IgnoreRules = [],
                Exclusions = []
            };
            var inventoryFailures = new List<(string Folder, Entry? Observed, TextKey Cause, bool FullRoot)>();
            var listing = new LocalListing(pair, new PathScope(pair, [pair, nested]),
                (folder, entry, cause, fullRoot) => inventoryFailures.Add((folder, entry, cause, fullRoot)));
            var folderNames = new[] { nameof(Entry), nameof(Exclusion), nameof(PathScope), nameof(PathNames) };
            foreach (var folder in folderNames)
            {
                Directory.CreateDirectory(Path.Combine(root.FullName, folder));
                File.WriteAllText(Path.Combine(root.FullName, folder, nameof(Listed)), nameof(Listed));
            }
            var filePath = Path.Combine(root.FullName, nameof(PairConfig));
            File.WriteAllText(filePath, nameof(PairConfig));
            var foreignNames = new[] { ResourceNames.NewId(), nameof(Entry) + ".stage", ".stage" };
            foreach (var name in foreignNames) File.WriteAllText(Path.Combine(root.FullName, name), "");
            var stage = StageNames.For(filePath);
            File.WriteAllText(stage, "");
            Check.Require(!File.GetAttributes(stage).HasFlag(FileAttributes.Hidden), nameof(FileAttributes.Hidden));
            var hiddenStage = StageNames.For(filePath);
            File.WriteAllText(hiddenStage, "");
            File.SetAttributes(hiddenStage, File.GetAttributes(hiddenStage) | FileAttributes.Hidden);
            var childStage = StageNames.For(Path.Combine(root.FullName, nameof(Entry), nameof(Listed)));
            File.WriteAllText(childStage, "");
            var stages = new HashSet<string>(StringComparer.Ordinal) { stage, hiddenStage, childStage };
            void RequireStages() => Check.Require(stages.SetEquals(listing.StaleStages) &&
                listing.StaleStages.Count == stages.Count, nameof(LocalListing.StaleStages));
            var protectedStages = folderNames.Where(name => name != nameof(Entry))
                .Select(folder => StageNames.For(Path.Combine(root.FullName, folder, nameof(Entry)))).ToArray();
            foreach (var protectedStage in protectedStages) File.WriteAllText(protectedStage, "");
            var stageFolder = StageNames.For(Path.Combine(root.FullName, nameof(ListOptions)));
            Directory.CreateDirectory(stageFolder);
            File.WriteAllText(Path.Combine(stageFolder, nameof(Listed)), nameof(Listed));
            var child = PathNames.Child(nameof(Entry), nameof(Listed));
            var stageFolderName = Path.GetFileName(stageFolder);

            foreach (var recursive in new[] { false, true })
            foreach (var includeOutOfScope in new[] { false, true })
            {
                var entries = await ReadAsync(listing, "", new ListOptions(recursive, includeOutOfScope));
                var expected = new HashSet<string>(StringComparer.Ordinal)
                {
                    nameof(Entry), nameof(PairConfig), stageFolderName
                };
                expected.UnionWith(foreignNames);
                if (recursive)
                {
                    expected.Add(child);
                    expected.Add(PathNames.Child(stageFolderName, nameof(Listed)));
                }
                if (includeOutOfScope)
                {
                    foreach (var folder in folderNames.Where(name => name != nameof(Entry)))
                    {
                        expected.Add(folder);
                        if (recursive) expected.Add(PathNames.Child(folder, nameof(Listed)));
                    }
                }
                Check.Require(entries.Count == expected.Count && expected.SetEquals(entries.Select(entry => entry.Path)),
                    nameof(ListOptions));
                Check.Require(entries.All(entry => entry.Skip is null), nameof(Entry.Skip));
                if (recursive)
                    RequireStages();
                else
                    Check.Require(listing.StaleStages.Count == 0, nameof(LocalListing.StaleStages));
            }
            foreach (var folder in folderNames)
            foreach (var recursive in new[] { false, true })
            foreach (var includeOutOfScope in new[] { false, true })
            {
                var entries = await ReadAsync(listing, folder, new ListOptions(recursive, includeOutOfScope));
                var expected = folder == nameof(Entry) || includeOutOfScope
                    ? new[] { PathNames.Child(folder, nameof(Listed)) } : Array.Empty<string>();
                Check.Require(entries.Select(entry => entry.Path).SequenceEqual(expected), nameof(StageNames));
                RequireStages();
            }

            foreach (var item in WindowsDirectoryListing.List(root.FullName))
                Check.Require(WindowsDirectoryListing.Named(root.FullName, item.Name.ToLowerInvariant()) == item,
                    nameof(WindowsDirectoryListing.Named));
            // Invalid parents prove that impossible names return before any directory open.
            var missingDirectory = Path.Combine(root.FullName, nameof(InventoryIncompleteException));
            foreach (var name in new[] { "", new string('x', MaxNameCharacters + 1) })
                Check.Require(WindowsDirectoryListing.Named(missingDirectory, name) is null,
                    nameof(WindowsDirectoryListing.Named));
            Check.Require(WindowsDirectoryListing.Named(root.FullName, nameof(InventoryIncompleteException)) is null,
                nameof(WindowsDirectoryListing.Named));
            var native = WindowsDirectoryListing.List(root.FullName).Single(item => item.Name == nameof(PairConfig));
            var one = listing.One(nameof(PairConfig).ToLowerInvariant());
            Check.Require(one == new Entry(nameof(PairConfig), false, native.Size, new DateTimeOffset(native.LastWriteTimeUtc),
                native.FileId.ToString(CultureInfo.InvariantCulture), "", "", null), nameof(LocalListing.One));
            var folderEntry = listing.One(nameof(Entry));
            Check.Require(folderEntry is { IsFolder: true, Size: 0 } && folderEntry.Modified == default,
                nameof(Entry.IsFolder));
            Check.Require(listing.One(nameof(InventoryIncompleteException)) is null, nameof(LocalListing.One));
            Check.Require(listing.One("") is null, nameof(LocalListing.One));
            Check.Require(listing.One(PathNames.Child(nameof(Exclusion), nameof(Listed))) is not null,
                nameof(Exclusion));
            Check.Require(listing.One(Path.GetFileName(stage)) is not null, nameof(StageNames));
            var full = await ReadAsync(listing, "", new ListOptions(true, false));
            foreach (var entry in full)
            {
                Check.Require(listing.One(entry.Path) == entry, nameof(LocalListing.One));
                var request = PathNames.Child(PathNames.Parent(entry.Path), Path.GetFileName(entry.Path).ToLowerInvariant());
                Check.Require(listing.One(request) == entry, nameof(Entry.Path));
            }
            // A metadata-only directory query must work even when opening the child is forbidden by sharing.
            using (new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check.Require(listing.One(nameof(PairConfig)) == one, nameof(LocalListing.One));
            var longName = new string('Ω', MaxNameCharacters);
            var longPath = Path.Combine(root.FullName, longName);
            File.WriteAllText(longPath, nameof(LocalListing));
            try
            {
                var entries = await ReadAsync(listing, "", new ListOptions(false, false));
                Check.Require(listing.One(longName.ToLowerInvariant()) == entries.Single(entry => entry.Path == longName),
                    nameof(LocalListing.One));
                Check.Require(listing.One(longName + nameof(Entry)) is null, nameof(LocalListing.One));
            }
            finally { File.Delete(longPath); }

            await CheckOfflineAsync(listing, filePath, stage);
            await CheckLinksAsync(listing, root.FullName);
            await CheckUnreadableAsync(pair);
            await CheckAcquisitionAsync(pair);
            await CheckRootIdentityAsync(pair, filePath);
            await CheckInUseAsync(pair);
            await CheckPrefixIgnoresAsync(pair);
            var missing = nameof(InventoryIncompleteException);
            try
            {
                await ReadAsync(listing, missing, new ListOptions(true, false));
                Check.Require(false, nameof(InventoryIncompleteException));
            }
            catch (InventoryIncompleteException error)
            {
                Check.Require(error.Message.Contains(missing) && !error.Message.Contains('{') &&
                    error.InnerException is { } inner && error.Message.Contains(inner.Message),
                    nameof(InventoryIncompleteException));
            }
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await ExpectAsync<OperationCanceledException>(async () =>
                    { await ReadAsync(listing, "", new ListOptions(true, false), canceled.Token); });
            }
            using (var canceled = new CancellationTokenSource())
            {
                await using var walk = listing.Walk("", new ListOptions(true, false), canceled.Token).GetAsyncEnumerator();
                Check.Require(await walk.MoveNextAsync(), nameof(IAsyncEnumerator<Listed>.MoveNextAsync));
                canceled.Cancel();
                await ExpectAsync<OperationCanceledException>(async () => { await walk.MoveNextAsync(); });
            }
            await using (var walk = listing.Walk("", new ListOptions(true, false), CancellationToken.None).GetAsyncEnumerator())
                Check.Require(await walk.MoveNextAsync(), nameof(IAsyncEnumerator<Listed>.MoveNextAsync));
            await ReadAsync(listing, "", new ListOptions(true, false));
            RequireStages();
            foreach (var stale in stages) File.Delete(stale);
            await ReadAsync(listing, "", new ListOptions(true, false));
            Check.Require(listing.StaleStages.Count == 0, nameof(LocalListing.StaleStages));

            Check.Require(listing.One(PathNames.Child(nameof(InventoryIncompleteException), nameof(Entry))) is null,
                nameof(LocalListing.One));
            Check.Require(inventoryFailures.Count == 0, nameof(LocalListing.Walk));
            // Special and cloud-file reparse points need a provider to create; they are proven by the live run.
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task CheckUnreadableAsync(PairConfig pair)
    {
        // shortcut: post-yield, managed I/O/access-denied and disposal faults lack a deterministic seam, cover when available.
        var root = Path.Combine(pair.LocalRoot, nameof(CheckUnreadableAsync));
        var isolatedPair = pair with { LocalRoot = root, IgnoreRules = [], Exclusions = [] };
        var inventoryFailures = 0;
        var listing = new LocalListing(isolatedPair, new PathScope(isolatedPair, [isolatedPair]),
            (_, _, _, _) => inventoryFailures++);
        foreach (var fullWalk in new[] { false, true })
        foreach (var includeOutOfScope in new[] { false, true })
        {
            var under = fullWalk ? "" : nameof(Entry);
            var folderNames = new[] { nameof(LocalListing), nameof(ListOptions) };
            try
            {
                foreach (var name in folderNames)
                {
                    var folder = Directory.CreateDirectory(Path.Combine(root, under, name));
                    File.WriteAllText(Path.Combine(folder.FullName, nameof(Listed)), nameof(Listed));
                }
                Entry? failedFolder = null;
                var observed = new List<Entry>();
                var knownFolders = 0;
                var incomplete = false;
                try
                {
                    await foreach (var item in listing.Walk(under, new ListOptions(true, includeOutOfScope),
                        CancellationToken.None))
                    {
                        Check.Require(item.Entry is not null && item.Path == item.Entry.Path, nameof(Listed));
                        var entry = item.Entry!;
                        observed.Add(entry);
                        if (entry.IsFolder && entry.Skip is null && PathNames.Parent(entry.Path) == under &&
                            ++knownFolders == folderNames.Length)
                        {
                            // The last observed sibling is visited first by the walk's stack, before the healthy sibling.
                            failedFolder = entry;
                            Directory.Delete(Path.Combine(root, entry.Path), true);
                        }
                    }
                }
                catch (InventoryIncompleteException error)
                {
                    Check.Require(!fullWalk && failedFolder is not null &&
                        error.InnerException is ItemFailedException { InnerException: Win32Exception },
                        nameof(ItemFailedException));
                    incomplete = true;
                }
                Check.Require(failedFolder is not null && incomplete == !fullWalk, nameof(LocalListing.Walk));
                if (fullWalk)
                {
                    var marker = observed.Single(entry => entry.Skip is not null);
                    Check.Require(marker == (failedFolder! with { Skip = TextKey.SkipUnreadable.ToString() }),
                        nameof(TextKey.SkipUnreadable));
                    var healthyFolder = observed.Single(entry => entry.IsFolder && entry.Skip is null &&
                        entry.Path != failedFolder!.Path);
                    var healthyChild = observed.Single(entry => entry.Path ==
                        PathNames.Child(healthyFolder.Path, nameof(Listed)));
                    Check.Require(observed.IndexOf(healthyChild) > observed.IndexOf(marker), nameof(LocalListing.Walk));
                    Check.Require(observed.Count == folderNames.Length + 2, nameof(Listed));
                }
                else
                {
                    Check.Require(observed.Count == folderNames.Length && observed.All(entry => entry.Skip is null),
                        nameof(InventoryIncompleteException));
                }
                var restored = Directory.CreateDirectory(Path.Combine(root, failedFolder!.Path));
                File.WriteAllText(Path.Combine(restored.FullName, nameof(Listed)), nameof(Listed));
                var readable = await ReadAsync(listing, under, new ListOptions(true, includeOutOfScope));
                var expected = folderNames.SelectMany(name => new[]
                {
                    PathNames.Child(under, name), PathNames.Child(PathNames.Child(under, name), nameof(Listed))
                }).ToHashSet(StringComparer.Ordinal);
                Check.Require(readable.Count == expected.Count && expected.SetEquals(readable.Select(entry => entry.Path)) &&
                    readable.All(entry => entry.Skip is null), nameof(TextKey.SkipUnreadable));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        foreach (var recursive in new[] { false, true })
        foreach (var includeOutOfScope in new[] { false, true })
        {
            try
            {
                await ReadAsync(listing, "", new ListOptions(recursive, includeOutOfScope));
                Check.Require(false, nameof(InventoryIncompleteException));
            }
            catch (InventoryIncompleteException error)
            {
                Check.Require(error.InnerException is ItemFailedException { InnerException: Win32Exception },
                    nameof(ItemFailedException));
            }
        }
        // A gone folder is not an in-use family failure, so the composition callback stays silent.
        Check.Require(inventoryFailures == 0, nameof(IssuePolicy.IsLocalAccessRetryCause));
    }

    // A107, A117-A121: the report names the opened handle, after it closed, and never a directory that was not walked.
    private static async Task CheckAcquisitionAsync(PairConfig pair)
    {
        var root = Path.Combine(pair.LocalRoot, nameof(CheckAcquisitionAsync));
        var isolated = pair with { LocalRoot = root, IgnoreRules = [], Exclusions = [] };
        var listing = new LocalListing(isolated, new PathScope(isolated, [isolated]), (_, _, _, _) => { });
        const string Walked = "Walked";
        var nested = PathNames.Child(Walked, nameof(Entry));
        Directory.CreateDirectory(Path.Combine(root, nested));
        Directory.CreateDirectory(Path.Combine(root, nameof(Exclusion)));
        File.WriteAllText(Path.Combine(root, Walked, nameof(Listed)), nameof(Listed));
        var aside = Path.Combine(root, nameof(Path));
        var folder = Path.Combine(root, Walked);
        var reports = new List<(string Path, string Id)>();
        var options = new ListOptions(true, false)
        {
            DirectoryAcquired = (path, id) =>
            {
                // No sharing succeeds only once the enumeration handle is closed.
                using var closed = HoldDirectory(Path.Combine(root, path));
                reports.Add((path, id));
            }
        };
        var original = listing.One(Walked)!.Id;

        await ReadAsync(listing, "", options);
        Check.Require(reports.Select(report => report.Path).Order(StringComparer.Ordinal)
            .SequenceEqual(new[] { nameof(Exclusion), Walked, nested }.Order(StringComparer.Ordinal)) &&
            reports.Single(report => report.Path == Walked).Id == original,
            "every walked directory, empty ones too, reports once after disposal; the pair root never does");

        reports.Clear();
        await foreach (var item in listing.Walk("", options, CancellationToken.None))
            if (item.Path == Walked)
            {
                Directory.Move(folder, aside);
                Directory.CreateDirectory(folder);
            }
        Check.Require(reports.Single(report => report.Path == Walked).Id != original,
            "a queued descendant swapped before its open reports the replacement's handle");
        Directory.Delete(folder);
        Directory.Move(aside, folder);

        reports.Clear();
        var walk = listing.Walk(Walked, options, CancellationToken.None).GetAsyncEnumerator();
        Directory.Move(folder, aside);
        Directory.CreateDirectory(folder);
        while (await walk.MoveNextAsync()) { }
        await walk.DisposeAsync();
        Check.Require(reports.Single().Id != original, "a walk root swapped before MoveNext reports the replacement");
        Directory.Delete(folder);
        Directory.Move(aside, folder);

        reports.Clear();
        using (var held = HoldDirectory(folder))
            await ExpectAsync<InventoryIncompleteException>(() => ReadAsync(listing, Walked, options));
        Check.Require(reports.Count == 0, "a directory that failed is unreported");

        using var cancel = new CancellationTokenSource();
        await ExpectAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in listing.Walk(Walked, options, cancel.Token)) await cancel.CancelAsync();
        });
        Check.Require(reports.All(report => report.Path != Walked), "a walk cancelled in a directory never reports it");

        reports.Clear();
        await ReadAsync(listing, "", new ListOptions(true, false));
        Check.Require(reports.Count == 0, "a null option asks nothing");
    }

    private static async Task CheckPrefixIgnoresAsync(PairConfig pair)
    {
        // Both shipped temporary prefixes take the same file-only matcher branch.
        await CheckPrefixIgnoresAsync(pair, "~$", "office");
        await CheckPrefixIgnoresAsync(pair, ".~", "temporary");
    }

    private static async Task CheckPrefixIgnoresAsync(PairConfig pair, string prefix, string name)
    {
        var root = Path.Combine(pair.LocalRoot, nameof(CheckPrefixIgnoresAsync), name);
        var prefixPair = pair with
        {
            LocalRoot = root,
            IgnoreRules = [nameof(SyncIgnoreKind.FileNamePrefix) + ':' + prefix],
            Exclusions = []
        };
        var listing = new LocalListing(prefixPair, new PathScope(prefixPair, [prefixPair]),
            (_, _, _, _) => { });
        try
        {
            var parent = Directory.CreateDirectory(Path.Combine(root, nameof(PathScope))).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(root, prefix + nameof(Entry))).FullName;
            foreach (var path in new[]
                     {
                         Path.Combine(root, nameof(Listed) + ".xlsx"),
                         Path.Combine(root, prefix + nameof(Listed) + ".docx"),
                         Path.Combine(root, prefix),
                         Path.Combine(parent, prefix + nameof(Listed) + ".xlsx"),
                         Path.Combine(parent, nameof(Listed) + prefix + "copy.xlsx"),
                         Path.Combine(folder, nameof(Listed))
                     })
                File.WriteAllText(path, nameof(Listed));
            async Task RequireAsync(ListOptions options, params string[] expected)
            {
                var entries = await ReadAsync(listing, "", options);
                Check.Require(entries.Count == expected.Length &&
                    expected.ToHashSet(StringComparer.Ordinal).SetEquals(entries.Select(entry => entry.Path)),
                    nameof(SyncIgnoreKind.FileNamePrefix));
            }
            // A prefixed file is ignored; a prefixed directory is not; IncludeOutOfScope reveals ignored files.
            await RequireAsync(new ListOptions(true, false), nameof(Listed) + ".xlsx", prefix + nameof(Entry),
                PathNames.Child(prefix + nameof(Entry), nameof(Listed)), nameof(PathScope),
                PathNames.Child(nameof(PathScope), nameof(Listed) + prefix + "copy.xlsx"));
            await RequireAsync(new ListOptions(true, true), nameof(Listed) + ".xlsx", prefix + nameof(Entry),
                PathNames.Child(prefix + nameof(Entry), nameof(Listed)), nameof(PathScope),
                PathNames.Child(nameof(PathScope), nameof(Listed) + prefix + "copy.xlsx"),
                prefix + nameof(Listed) + ".docx", prefix,
                PathNames.Child(nameof(PathScope), prefix + nameof(Listed) + ".xlsx"));
            await RequireAsync(new ListOptions(false, false), nameof(Listed) + ".xlsx",
                prefix + nameof(Entry), nameof(PathScope));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CheckRootIdentityAsync(PairConfig pair, string filePath)
    {
        var root = Path.Combine(pair.LocalRoot, nameof(CheckRootIdentityAsync));
        Directory.CreateDirectory(root);
        var identity = WindowsDirectoryListing.ReadIdentity(root);
        // The wide FileIdInfo form is preferred; the basic fallback is only for an unsupported information class.
        Check.Require(identity.FileIdHigh is not null, nameof(DirectoryIdentity.FileIdHigh));
        Check.Require(WindowsDirectoryListing.ReadIdentity(root) == identity,
            nameof(WindowsDirectoryListing.ReadIdentity));
        // A wide identity whose high half is zero is not the basic representation, so the two never compare equal.
        Check.Require(new DirectoryIdentity(identity.VolumeSerialNumber, identity.FileId, null) !=
            new DirectoryIdentity(identity.VolumeSerialNumber, identity.FileId, 0), nameof(DirectoryIdentity));
        File.WriteAllText(Path.Combine(root, nameof(Listed)), nameof(Listed));
        Check.Require(WindowsDirectoryListing.ListRoot(root, identity).Single().Name == nameof(Listed),
            nameof(WindowsDirectoryListing.ListRoot));
        var other = Directory.CreateDirectory(Path.Combine(root, nameof(PathScope))).FullName;
        Expect<IOException>(() => WindowsDirectoryListing.ListRoot(other, identity).ToList());
        // The identity must come from the handle being enumerated: one read from another path cannot authenticate it.
        var replaced = Directory.CreateDirectory(Path.Combine(root, nameof(LocalListing))).FullName;
        var saved = WindowsDirectoryListing.ReadIdentity(replaced);
        Directory.Delete(replaced);
        Directory.CreateDirectory(replaced);
        // NTFS may hand the new directory the same file id; that reused identity is still the correct proof.
        if (WindowsDirectoryListing.ReadIdentity(replaced) != saved)
            Expect<IOException>(() => WindowsDirectoryListing.ListRoot(replaced, saved).ToList());
        var link = Path.Combine(root, nameof(TextKey.SkipLink));
        Directory.CreateSymbolicLink(link, root);
        Expect<IOException>(() => WindowsDirectoryListing.ReadIdentity(link));
        Expect<IOException>(() => WindowsDirectoryListing.ReadIdentity(filePath));
        Expect<Win32Exception>(() => WindowsDirectoryListing.ReadIdentity(
            Path.Combine(root, nameof(ItemFailedException))));
        var roots = new RootBinding(root, pair.DropboxRoot, pair.ConnectionId, identity,
            new DropboxRootIdentity(nameof(DropboxRootIdentity), nameof(DropboxRootIdentity),
                nameof(DropboxRootIdentity), nameof(DropboxRootIdentity)));
        var bound = pair with { LocalRoot = root, IgnoreRules = [], Exclusions = [], Roots = roots };
        var reports = 0;
        var listing = new LocalListing(bound, new PathScope(bound, [bound]), (_, _, _, _) => reports++);
        var entries = await ReadAsync(listing, "", new ListOptions(true, false));
        Check.Require(entries.Any(entry => entry.Path == nameof(Listed)) && reports == 0,
            nameof(WindowsDirectoryListing.ListRoot));
        var mismatched = bound with { Roots = roots with { Local = WindowsDirectoryListing.ReadIdentity(other) } };
        try
        {
            await ReadAsync(new LocalListing(mismatched, new PathScope(mismatched, [mismatched]),
                (_, _, _, _) => { }), "", new ListOptions(true, false));
            Check.Require(false, nameof(WindowsDirectoryListing.ListRoot));
        }
        catch (InventoryIncompleteException error)
        {
            Check.Require(error.InnerException is ItemFailedException { InnerException: IOException },
                nameof(ItemFailedException));
        }
    }

    private static async Task CheckInUseAsync(PairConfig pair)
    {
        var root = Path.Combine(pair.LocalRoot, nameof(CheckInUseAsync));
        var isolatedPair = pair with { LocalRoot = root, IgnoreRules = [], Exclusions = [] };
        var failures = new List<(string Folder, Entry? Observed, TextKey Cause, bool FullRoot)>();
        var listing = new LocalListing(isolatedPair, new PathScope(isolatedPair, [isolatedPair]),
            (folder, entry, cause, fullRoot) => failures.Add((folder, entry, cause, fullRoot)));
        try
        {
            var child = Directory.CreateDirectory(Path.Combine(root, nameof(Entry)));
            File.WriteAllText(Path.Combine(child.FullName, nameof(Listed)), nameof(Listed));
            Check.Require(IssuePolicy.IsLocalAccessRetryCause(
                    LocalErrors.Cause(new Win32Exception(32), TextKey.ReadLocalFailed)) &&
                IssuePolicy.IsLocalAccessRetryCause(LocalErrors.Cause(
                    new COMException("", unchecked((int)0x80270021)), TextKey.ReadLocalFailed)) &&
                !IssuePolicy.IsLocalAccessRetryCause(
                    LocalErrors.Cause(new Win32Exception(5), TextKey.ReadLocalFailed)),
                nameof(IssuePolicy.IsLocalAccessRetryCause));
            using (HoldDirectory(child.FullName))
            {
                // A full walk has already yielded sibling entries when the held child folder fails.
                var entries = await ReadAsync(listing, "", new ListOptions(true, false));
                var marker = entries.Single(item => item.Skip is not null);
                var failed = failures.Single();
                var observed = failed.Observed;
                Check.Require(observed is { IsFolder: true, Skip: null } &&
                    marker == (observed with { Skip = TextKey.SkipUnreadable.ToString() }) &&
                    failed.Folder == nameof(Entry) && failed.Cause == TextKey.FailLocalInUse && !failed.FullRoot,
                    nameof(LocalListing.Walk));
                failures.Clear();
                try
                {
                    await ReadAsync(listing, nameof(Entry), new ListOptions(true, false));
                    Check.Require(false, nameof(InventoryIncompleteException));
                }
                catch (InventoryIncompleteException error)
                {
                    Check.Require(error.InnerException is ItemFailedException
                            { Path: nameof(Entry), Cause: TextKey.FailLocalInUse } typed &&
                        typed.InnerException is Win32Exception { NativeErrorCode: 32 },
                        nameof(ItemFailedException));
                }
                Check.Require(failures.Count == 1 && failures[0].Folder == nameof(Entry) &&
                    failures[0].Observed is null && !failures[0].FullRoot &&
                    failures[0].Cause == TextKey.FailLocalInUse, nameof(LocalListing.Walk));
                failures.Clear();
                try
                {
                    listing.One(PathNames.Child(nameof(Entry), nameof(Listed)));
                    Check.Require(false, nameof(LocalListing.One));
                }
                catch (ItemFailedException error)
                {
                    Check.Require(error.Cause == TextKey.FailLocalInUse &&
                        error.InnerException is Win32Exception { NativeErrorCode: 32 }, nameof(LocalListing.One));
                }
            }
            using (HoldDirectory(root))
            {
                failures.Clear();
                try
                {
                    await ReadAsync(listing, "", new ListOptions(true, false));
                    Check.Require(false, nameof(InventoryIncompleteException));
                }
                catch (InventoryIncompleteException error)
                {
                    Check.Require(error.InnerException is ItemFailedException { Cause: TextKey.FailLocalInUse },
                        nameof(ItemFailedException));
                }
                Check.Require(failures.Count == 1 && failures[0].Folder == "" &&
                    failures[0].Observed is null && failures[0].FullRoot &&
                    failures[0].Cause == TextKey.FailLocalInUse, nameof(LocalListing.Walk));
            }
            failures.Clear();
            var healthy = await ReadAsync(listing, "", new ListOptions(true, true));
            Check.Require(failures.Count == 0 &&
                healthy.Any(entry => entry.Path == PathNames.Child(nameof(Entry), nameof(Listed))),
                nameof(LocalListing.Walk));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static async Task CheckOfflineAsync(LocalListing listing, string filePath, string stage)
    {
        foreach (var offlinePath in new[] { filePath, stage })
        {
            var attributes = File.GetAttributes(offlinePath);
            try
            {
                File.SetAttributes(offlinePath, attributes | FileAttributes.Offline);
                var path = Path.GetFileName(offlinePath);
                var entries = await ReadAsync(listing, "", new ListOptions(true, false));
                Check.Require(!listing.StaleStages.Contains(offlinePath), nameof(LocalListing.StaleStages));
                using (new FileStream(offlinePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var entry = listing.One(path.ToLowerInvariant());
                    Check.Require(entry?.Skip == TextKey.SkipOnlineOnly.ToString(), nameof(TextKey.SkipOnlineOnly));
                    Check.Require(offlinePath == stage ? !entries.Any(item => item.Path == path)
                        : entry == entries.Single(item => item.Path == path), nameof(LocalListing.Walk));
                }
                Check.Require(File.GetAttributes(offlinePath) == (attributes | FileAttributes.Offline),
                    nameof(FileAttributes.Offline));
            }
            finally { File.SetAttributes(offlinePath, attributes); }
        }
    }

    private static async Task CheckLinksAsync(LocalListing listing, string root)
    {
        var link = Path.Combine(root, nameof(TextKey.SkipLink));
        var stageLink = StageNames.For(Path.Combine(root, nameof(LocalListing)));
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(root, nameof(Entry)));
            File.CreateSymbolicLink(stageLink, Path.Combine(root, nameof(PairConfig)));
            foreach (var recursive in new[] { false, true })
            foreach (var includeOutOfScope in new[] { false, true })
            {
                var entries = await ReadAsync(listing, "", new ListOptions(recursive, includeOutOfScope));
                foreach (var path in new[] { Path.GetFileName(link), Path.GetFileName(stageLink) })
                {
                    var entry = listing.One(path.ToLowerInvariant());
                    Check.Require(entry?.Skip == TextKey.SkipLink.ToString(), nameof(TextKey.SkipLink));
                    Check.Require(path == Path.GetFileName(stageLink) ? !entries.Any(item => item.Path == path)
                        : entry == entries.Single(item => item.Path == path), nameof(LocalListing.Walk));
                    Check.Require(!entries.Any(item => item.Path.StartsWith(path + PathNames.PathSeparator,
                        StringComparison.Ordinal)), nameof(LocalListing.Walk));
                }
                Check.Require(!listing.StaleStages.Contains(stageLink), nameof(LocalListing.StaleStages));
            }
            File.Delete(stageLink);
            File.CreateSymbolicLink(stageLink, Path.Combine(root, nameof(InventoryIncompleteException)));
            var dangling = await ReadAsync(listing, "", new ListOptions(false, false));
            Check.Require(!dangling.Any(entry => entry.Path == Path.GetFileName(stageLink)), nameof(LocalListing.Walk));
            Check.Require(listing.One(Path.GetFileName(stageLink))?.Skip == TextKey.SkipLink.ToString(),
                nameof(LocalListing.One));
        }
        finally
        {
            if (File.Exists(stageLink)) File.Delete(stageLink);
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    private static async Task<List<Entry>> ReadAsync(LocalListing listing, string under, ListOptions options,
        CancellationToken ct = default)
    {
        var entries = new List<Entry>();
        await foreach (var item in listing.Walk(under, options, ct))
        {
            Check.Require(item.Entry is not null && item.Path == item.Entry.Path, nameof(Listed));
            entries.Add(item.Entry!);
        }
        return entries;
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        Check.Require(false, typeof(T).Name);
    }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        Check.Require(false, typeof(T).Name);
    }

    // A directory held with no sharing denies the listing's own open, producing a real Win32 sharing violation.
    private static SafeFileHandle HoldDirectory(string path)
    {
        var handle = CreateFile(path, FileListDirectory, FileShare.None, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }

    // OS facts: FILE_LIST_DIRECTORY, OPEN_EXISTING and FILE_FLAG_BACKUP_SEMANTICS for a held directory handle.
    private const uint FileListDirectory = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
}
