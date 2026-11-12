using Massdex.SyncShark;
using System.Text.Json;

internal static class IgnoreRuleChecks
{
    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The real ignore-policy inventory check requires Windows.");
        var rules = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileExtension, " TMP "),
            new(SyncIgnoreKind.FileName, "Thumbs.db"), new(SyncIgnoreKind.FolderName, "cache"),
            new(SyncIgnoreKind.FileNamePrefix, "~$")]);
        Require(rules[0].Value == ".tmp", "Plain file type input is normalized without regex");
        Require(SyncIgnorePolicy.IsIgnored("Work/DRAFT.TMP", false, rules), "File type matches future items case-insensitively");
        Require(!SyncIgnorePolicy.IsIgnored("report.tmp", true, rules), "File type does not exclude a similarly named directory");
        Require(!SyncIgnorePolicy.IsIgnored("Thumbs.db", true, rules), "File Name does not exclude a directory with that name");
        Require(SyncIgnorePolicy.IsIgnored("Work/cache/sub/file.txt", false, rules) &&
            !SyncIgnorePolicy.IsIgnored("Work/cached/file.txt", false, rules), "Folder names match complete path components at any depth");
        try { SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileName, "*.txt")]); throw new Exception("Wildcard unexpectedly accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS Unsupported wildcard gives a plain-input validation error"); }

        // P3 8.1(a): the appended kind keeps the existing ordinals and parses through the one shared grammar.
        Require((int)SyncIgnoreKind.FileExtension == 0 && (int)SyncIgnoreKind.FileName == 1 &&
            (int)SyncIgnoreKind.FolderName == 2 && (int)SyncIgnoreKind.FileNamePrefix == 3,
            "Appending the prefix kind preserves the existing enum ordinals");
        var parsedPrefixes = SyncIgnorePolicy.Normalize(SyncIgnorePolicy.Parse(
            "FileNamePrefix:~$;file-name-prefix:.~;FILENAMEPREFIX:~$"));
        Require(parsedPrefixes.Count == 2 &&
            parsedPrefixes[0] == new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, ".~") &&
            parsedPrefixes[1] == new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "~$"),
            "Canonical, hyphenated and case-varied prefix kind text parse; duplicates collapse and values order");

        // P3 8.1(b): every matcher example for the two documented prefixes and unaffected existing kinds.
        var tildeRule = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileNamePrefix, "~$")]);
        foreach (var matching in new[]
        {
            "~$Book.xlsx", "Parent/~$Report.docx", "Parent/Child/~$notes.tmp", "~$",
            "PARENT/~$REPORT.DOCX", "Parent\\Child\\~$notes.tmp"
        })
            Require(SyncIgnorePolicy.IsIgnored(matching, false, tildeRule), "Prefix rule matches '" + matching + "'");
        foreach (var notMatching in new[] { "Book.xlsx", "Parent/Book~$copy.xlsx", "Parent/~Report.docx" })
            Require(!SyncIgnorePolicy.IsIgnored(notMatching, false, tildeRule), "Prefix rule does not match '" + notMatching + "'");
        Require(!SyncIgnorePolicy.IsIgnored("~$Folder", true, tildeRule) &&
            !SyncIgnorePolicy.IsIgnored("~$Folder/ordinary.txt", false, tildeRule),
            "A same-named directory and its ordinary child are not matched by the final-name prefix");
        var dotTildeRule = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileNamePrefix, ".~")]);
        foreach (var matching in new[] { ".~", ".~lock.Book.ods#", "Parent/.~lock.Book.ods#", "Parent/Child/.~notes" })
            Require(SyncIgnorePolicy.IsIgnored(matching, false, dotTildeRule), "Second prefix matches '" + matching + "'");
        foreach (var notMatching in new[] { "Book.ods", "Parent/x.~lock.Book.ods#", ".~Folder/ordinary.txt" })
            Require(!SyncIgnorePolicy.IsIgnored(notMatching, false, dotTildeRule), "Second prefix does not match '" + notMatching + "'");
        Require(!SyncIgnorePolicy.IsIgnored(".~Folder", true, dotTildeRule), "Second prefix does not exclude a same-named directory");
        var exactTilde = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileName, "~$")]);
        Require(SyncIgnorePolicy.IsIgnored("~$", false, exactTilde) &&
            !SyncIgnorePolicy.IsIgnored("~$Book.xlsx", false, exactTilde),
            "FileName:~$ remains an exact name and never becomes a prefix");
        var genericPrefix = SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileNamePrefix, "temp")]);
        Require(SyncIgnorePolicy.IsIgnored("Parent/TEMPfile.txt", false, genericPrefix) &&
            !SyncIgnorePolicy.IsIgnored("Parent/attempt.txt", false, genericPrefix),
            "A generic custom prefix uses the same case-insensitive comparison, not an Office special case");

        // P3 8.1(c): refusals, Unicode normalization and retained path handling.
        var invalidPrefixes = new[]
        {
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, ""),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "   "),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "~$*"),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "a/b"),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "a\\b"),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "a:b"),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "."),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, ".."),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "name."),
            new SyncIgnoreRule(SyncIgnoreKind.FileNamePrefix, "a\u0001b"),
            new SyncIgnoreRule((SyncIgnoreKind)99, "~$")
        };
        for (var i = 0; i < invalidPrefixes.Length; i++)
        {
            var refused = false;
            try { SyncIgnorePolicy.Normalize([invalidPrefixes[i]]); }
            catch (InvalidDataException) { refused = true; }
            Require(refused, "Invalid prefix rule " + i + " is refused by the shared normalizer");
        }
        Require(SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileNamePrefix, "CAF\u00C9")]).Single().Value == "caf\u00E9" &&
            SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileNamePrefix, "CAFE\u0301")]).Single().Value == "caf\u00E9",
            "Prefix values are trimmed, Unicode-normalized and lowercased like the existing kinds");
        Require(SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileExtension, "tmp"), new(SyncIgnoreKind.FileExtension, " .TMP "),
            new(SyncIgnoreKind.FileExtension, "*.tmp")]).All(rule => rule == new SyncIgnoreRule(SyncIgnoreKind.FileExtension, ".tmp")),
            "Existing file-type spellings still normalize to the same leading-dot value");
        Require(!SyncIgnorePolicy.IsIgnored("~$\u0001note.txt", false, tildeRule),
            "Control characters in a path still match no rule instead of raising");
        try { SyncIgnorePolicy.IsIgnored("../~$note.txt", false, tildeRule); throw new Exception("Unsafe path unexpectedly accepted"); }
        catch (ArgumentException) { Console.WriteLine("PASS Malformed inventory paths keep the existing argument error"); }

        // P3 8.1(d): the edit-only none sentinel stays out of ordinary Parse and settings parsing.
        Require(SyncIgnorePolicy.ParseForEdit("none").Count == 0 && SyncIgnorePolicy.ParseForEdit(" NONE ").Count == 0 &&
            SyncIgnorePolicy.ParseForEdit("None").Count == 0 && SyncIgnorePolicy.ParseForEdit("").Count == 0 &&
            SyncIgnorePolicy.ParseForEdit(" ; ").Count == 0,
            "Edit input clears on the case-insensitive none sentinel and keeps empty or separator-only input empty");
        Require(SyncIgnorePolicy.ParseForEdit("FileName:none").Single() == new SyncIgnoreRule(SyncIgnoreKind.FileName, "none"),
            "FileName:none remains an ordinary exact-name rule");
        try { SyncIgnorePolicy.ParseForEdit("none;FileName:x"); throw new Exception("Compound none unexpectedly accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS none is a sentinel only as the whole edit value"); }
        try { SyncIgnorePolicy.Parse("none"); throw new Exception("Ordinary parse unexpectedly accepted none"); }
        catch (InvalidDataException) { Console.WriteLine("PASS Ordinary Parse keeps rejecting the edit-only none sentinel"); }

        // P3 8.1(e): descriptions stay nonthrowing and accurate for every defined kind.
        Require(Enum.GetValues<SyncIgnoreKind>().Select(kind => new SyncIgnoreRule(kind, "v").Description).SequenceEqual(new[]
            {
                "Files ending in v", "Files named v", "Folders named v and everything inside", "Files whose names start with v"
            }),
            "Every ignore kind describes itself accurately, including the prefix case");
        try { SyncIgnorePolicy.Normalize([new(SyncIgnoreKind.FileNamePrefix, " ")]); throw new Exception("Blank prefix unexpectedly accepted"); }
        catch (InvalidDataException error)
        { Require(error.Message.Contains("prefix", StringComparison.OrdinalIgnoreCase), "Invalid-value guidance mentions the name-prefix kind, not only three kinds"); }

        // P3 8.6 shared-type transition fixture: a deliberately restored pre-prefix rule set still round-trips.
        var restoredOldRules = SyncIgnorePolicy.Normalize(SyncIgnorePolicy.Parse(
            "FileExtension:tmp;FileName:Thumbs.db;FolderName:cache"));
        Require(restoredOldRules.Count == 3 && restoredOldRules.Select(rule => rule.Description).All(text => text.Length > 0),
            "A deliberately restored pre-prefix rule set still parses and renders through the capable shared types");
        var local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "baseline.tmp"), "retained baseline");
        await File.WriteAllTextAsync(Path.Combine(local, "gone.tmp"), "retained missing baseline");
        await File.WriteAllTextAsync(Path.Combine(local, "~$baseline.xlsx"), "retained prefix baseline");
        await File.WriteAllTextAsync(Path.Combine(local, "~$gone.xlsx"), "retained prefix missing baseline");
        Directory.CreateDirectory(Path.Combine(local, "~$folder"));
        await File.WriteAllTextAsync(Path.Combine(local, "~$folder", "ordinary.txt"), "directory is eligible");
        var remote = new FakeSyncRemoteStore();
        remote.Put("baseline.tmp", "retained baseline"); remote.Put("gone.tmp", "retained missing baseline");
        remote.Put("~$baseline.xlsx", "retained prefix baseline"); remote.Put("~$gone.xlsx", "retained prefix missing baseline");
        remote.Put("~$folder/ordinary.txt", "directory is eligible");
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair("ignore-policy-pair", "Ignore check", local, lease.Identity.VolumeSerialNumber,
            lease.Identity.FileId, "/Ignore", "id:ignore", PropagateDeletions: true,
            ConnectionId: "account-first");
        pair = pair with { ControlNamespace = new SyncControlNamespaceBinding("ignore-installation", pair.Id,
            SyncControlNamespace.BindLocalForTest(lease, "ignore-installation", pair.Id)) };
        var db = new SyncAuthorityDatabase(Path.Combine(root, "authority.db"));
        await db.InitializeAsync("ignore-installation", "ignore-profile", "ignore-check", "{}");
        await db.RegisterPairAsync(pair.Id, 1);
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        await db.RetainPairDefinitionAsync(pair, true);
        await db.RegisterRootBindingAsync("ignore-local", pair.Id, "local", 1,
            JsonSerializer.Serialize(new { lease.Identity.VolumeSerialNumber, lease.Identity.FileId }), local);
        await db.RegisterRootBindingAsync("ignore-dropbox", pair.Id, "dropbox", 1,
            "{\"folderId\":\"id:ignore\"}", pair.DropboxRoot);
        var observer = new SyncAuthorityObservationCoordinator(pair, remote, db, "ignore-local", "ignore-dropbox");
        await observer.ObserveAsync("initial"); await observer.ObserveAsync("initial");
        await new SyncAuthorityInitialPlanner(db).AdoptAsync(pair.Id);
        await File.WriteAllTextAsync(Path.Combine(local, "pending.tmp"), "not dispatched yet");
        await observer.ObserveAsync("manual");
        var options = new SyncAuthorityPlannerOptions(1, "ignore-profile", "ignore-control", "ignore-control",
            EnableDeletionEffects: true, EnableMoveEffects: true);
        var priorPlan = await new SyncAuthorityReconciliationPlanner(db, options, admission: admission).PlanAsync(pair.Id);
        Require(priorPlan.Any(item => item.RelativePath == "pending.tmp" && item.OperationId is not null), "An upload is genuinely queued before changing policy");
        try { await db.EnsurePairConfigurationMutableAsync(pair.Id); throw new Exception("A queued policy change was unexpectedly accepted"); }
        catch (InvalidOperationException) { Console.WriteLine("PASS A policy change is fenced while an old operation remains queued"); }
        await db.SetPairStateAsync(pair.Id, "active");
        await new SyncAuthorityPairRunner(pair, remote, db, "ignore-local", "ignore-dropbox", options,
            new PilotRunControl(), admission: admission).RunAsync("manual");
        await db.EnsurePairConfigurationMutableAsync(pair.Id);
        pair = pair with { IgnoreRules = rules, PolicyRevision = 2 };
        await db.RegisterPairAsync(pair.Id, 2); await db.RetainPairDefinitionAsync(pair, true);
        await File.WriteAllTextAsync(Path.Combine(local, "new.tmp"), "ignored after rule saved");
        File.Delete(Path.Combine(local, "baseline.tmp")); // The remote must NOT be deleted by sync.
        File.Delete(Path.Combine(local, "gone.tmp")); remote.Delete("gone.tmp");
        File.Delete(Path.Combine(local, "~$baseline.xlsx")); // A saved-only prefix file must keep its cloud copy.
        File.Delete(Path.Combine(local, "~$gone.xlsx")); remote.Delete("~$gone.xlsx");
        await File.WriteAllTextAsync(Path.Combine(local, "~$new.xlsx"), "ignored after rule saved");
        await File.WriteAllTextAsync(Path.Combine(local, "keep.txt"), "sync me");
        await File.WriteAllTextAsync(Path.Combine(local, "Thumbs.db"), "keep local");
        Directory.CreateDirectory(Path.Combine(local, "cache"));
        await File.WriteAllTextAsync(Path.Combine(local, "cache", "local.txt"), "ignored subtree");
        Directory.CreateDirectory(Path.Combine(local, "report.tmp"));
        await File.WriteAllTextAsync(Path.Combine(local, "report.tmp", "allowed.txt"), "directory is included");
        remote.Put("cloud.tmp", "keep in cloud"); remote.Put("cloud.txt", "download me");
        remote.Put("~$cloud.xlsx", "keep in cloud");
        var observed = await new SyncAuthorityObservationCoordinator(pair, remote, db, "ignore-local", "ignore-dropbox").ObserveAsync("manual");
        var ignored = await db.LoadIgnoredPathsAsync(pair.Id, rules);
        Require(ignored.Contains("gone.tmp") && ignored.Contains("baseline.tmp") &&
            ignored.Contains("~$gone.xlsx") && ignored.Contains("~$baseline.xlsx"),
            "Ignore evidence includes retained history even when both sides are absent");
        var current = await new SyncAuthorityReconciliationPlanner(db, options with { ConfigurationRevision = 2 }, admission: admission).PlanAsync(pair.Id);
        foreach (var item in current.Where(item => item.OperationId is not null))
            Console.WriteLine("PLANNED " + item.RelativePath + " " + item.Kind);
        Require(current.Where(item => item.OperationId is not null).All(item =>
            !SyncIgnorePolicy.IsIgnored(item.RelativePath, Directory.Exists(Path.Combine(local, item.RelativePath)), rules)), "Real planner emits no effect for ignored local, remote or historical items");
        await db.SetPairStateAsync(pair.Id, "active");
        await new SyncAuthorityPairRunner(pair, remote, db, "ignore-local", "ignore-dropbox",
            options with { ConfigurationRevision = 2 }, new PilotRunControl(), admission: admission).RunAsync("manual");
        Require(remote.Exists("keep.txt") && File.Exists(Path.Combine(local, "cloud.txt")), "Nonmatching files still sync in both directions through the real runner");
        Require(remote.Exists("baseline.tmp") && !File.Exists(Path.Combine(local, "baseline.tmp")), "Ignored missing local file does not delete or re-download its cloud copy");
        Require(!remote.Exists("new.tmp") && !remote.Exists("Thumbs.db") && !remote.Exists("cache/local.txt") &&
            !File.Exists(Path.Combine(local, "cloud.tmp")), "Ignored files and subtree contents are neither uploaded nor downloaded");
        Require(remote.Exists("~$baseline.xlsx") && !File.Exists(Path.Combine(local, "~$baseline.xlsx")),
            "A saved-only prefix file keeps its cloud copy and is not re-downloaded");
        Require(!remote.Exists("~$gone.xlsx") && !remote.Exists("~$new.xlsx") && !File.Exists(Path.Combine(local, "~$cloud.xlsx")),
            "Prefix-matched files are neither uploaded nor downloaded and absence is not deletion evidence");
        Require(File.Exists(Path.Combine(local, "~$folder", "ordinary.txt")) && remote.Exists("~$folder/ordinary.txt"),
            "A directory named like a prefix rule and its ordinary child remain eligible");
        Require(remote.Exists("report.tmp/allowed.txt"), "Directory ending in an ignored file type remains functional");
        Require(File.ReadAllText(Path.Combine(local, "pending.tmp")) == "not dispatched yet" &&
            File.ReadAllText(Path.Combine(local, "cache", "local.txt")) == "ignored subtree", "Adding rules retains existing local bytes");
        var otherAccountPair = pair with { Id = "other-account-pair", ConnectionId = "account-second", IgnoreRules = [] };
        Require(SyncSelectionPolicy.IsIncluded("pending.tmp", otherAccountPair) &&
            SyncSelectionPolicy.IsIncluded("~$new.xlsx", otherAccountPair), "Rules belong to one pair and do not leak to another account");
        var restored = JsonSerializer.Deserialize<SyncPair>(JsonSerializer.Serialize(pair))!;
        Require(SyncIgnorePolicy.Normalize(restored.IgnoreRules).SequenceEqual(rules), "Ignore rules survive pair persistence");
        Require(observed.LocalFilesHashed <= 3, "Ignored file contents are not opened or hashed during inventory");
    }

    private static void Require(bool condition, string label)
    { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
}
