using Massdex.SyncShark;
using Massdex.SyncShark.V2;

// Focused checks for the read-only `conflicts resolve` preview. Notice numbers can be reused, so a typed
// number must show the notice it names now, before anything changes.
internal static partial class CliChecks
{
    internal static async Task NoticePreviewChecksAsync(string root)
    {
        Directory.CreateDirectory(root);
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        using var http = new HttpClient(new NoNetworkHandler());
        using var composition = EngineComposition.Open(
            new EngineHostInputs(root, instance.StateAuthority, http), V2Settings.Default, TimeProvider.System);
        var pair = new PairDef("preview-pair", "Preview fixture",
            new RootDef("r00000000000000pn", SyncProviders.Local, null, local, string.Empty),
            new RootDef("r00000000000000qn", SyncProviders.Dropbox, "preview-account", "/Preview", "id:preview"),
            [], [], true);
        composition.Store.SavePair(pair);
        var runtime = V2CliRuntime.Borrow(instance, composition, root);
        var when = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var oldItem = Item("docs/old-name.txt", when);
        var newItem = Item("docs/new-name.txt", when);
        composition.Store.AddNotices([
            new Notice(pair.PairId, 27, NoticeKind.AutoConflict, "docs/old-name.txt",
                "Two versions of this file differ; both stay protected until you choose.",
                "auto-conflict", RetainedVersion: new RetainedNoticeVersion("action-old", Side.A, oldItem, oldItem)),
            new Notice(pair.PairId, 27, NoticeKind.AutoConflict, "docs/new-name.txt",
                "A newer version was kept for this file.", "auto-conflict",
                RetainedVersion: new RetainedNoticeVersion("action-new", Side.A, newItem, newItem)),
        ]);
        var open = composition.Store.OpenNotices(pair.PairId).ToArray();
        var older = open.Single(notice => notice.Path == "docs/old-name.txt").NoticeId!;
        var newer = open.Single(notice => notice.Path == "docs/new-name.txt").NoticeId!;

        // Without --yes it only reads: the typed number is shown as it stands now, with its cause and the
        // exact command that would apply the choice.
        var preview = await RunCliAsync(["--cli", "conflicts", "resolve", "--pair", pair.PairId,
            "--key", older, "--generation", "0", "--choice", "dismiss"], () => runtime);
        Expect("an unconfirmed notice resolve exits 0", preview, 0);
        Require(preview.StandardOutput.Contains("docs/old-name.txt", StringComparison.Ordinal) &&
                preview.StandardOutput.Contains("Two versions of this file differ", StringComparison.Ordinal) &&
                preview.StandardOutput.Contains("Dismiss", StringComparison.Ordinal) &&
                preview.StandardOutput.Contains("--path docs/old-name.txt", StringComparison.Ordinal) &&
                preview.StandardOutput.Contains("--yes", StringComparison.Ordinal) &&
                preview.StandardOutput.Contains("Nothing was changed", StringComparison.Ordinal),
            "the preview names the notice's path and cause and prints the exact confirmed command");
        Require(composition.Store.OpenNotices(pair.PairId).Count() == 2, "the preview changes nothing");

        // A number that now belongs to a different notice is refused when the old path is supplied, and a
        // path that simply does not match the notice is refused the same way.
        var reused = await RunCliAsync(["--cli", "conflicts", "resolve", "--pair", pair.PairId,
            "--path", "docs/old-name.txt", "--key", newer, "--generation", "0", "--choice", "dismiss", "--yes"],
            () => runtime);
        Require(reused.ExitCode != 0 && composition.Store.OpenNotices(pair.PairId).Count() == 2,
            "a notice number that now belongs to a different notice is refused with nothing changed");
        var wrongPath = await RunCliAsync(["--cli", "conflicts", "resolve", "--pair", pair.PairId,
            "--path", "docs/missing.txt", "--key", older, "--generation", "0", "--choice", "dismiss", "--yes"],
            () => runtime);
        Require(wrongPath.ExitCode != 0 && composition.Store.OpenNotices(pair.PairId).Count() == 2,
            "a --path that does not match the notice is refused with nothing changed");

        // With --yes it applies as before and closes exactly the notice it names.
        var applied = await RunCliAsync(["--cli", "conflicts", "resolve", "--pair", pair.PairId,
            "--path", "docs/old-name.txt", "--key", older, "--generation", "0", "--choice", "dismiss", "--yes"],
            () => runtime);
        Expect("a confirmed notice resolve applies", applied, 0);
        Require(composition.Store.OpenNotices(pair.PairId).Select(notice => notice.NoticeId)
                .SequenceEqual(new[] { newer }),
            "the confirmed choice closes exactly the notice it names");

        // The closed key no longer names an open notice, and the preview says so without changing anything.
        var gone = await RunCliAsync(["--cli", "conflicts", "resolve", "--pair", pair.PairId,
            "--key", older, "--generation", "0", "--choice", "dismiss"], () => runtime);
        Expect("a missing notice preview exits 0", gone, 0);
        Require(gone.StandardOutput.Contains("No open notice", StringComparison.Ordinal) &&
                composition.Store.OpenNotices(pair.PairId).Count() == 1,
            "a missing notice is reported and nothing changes");

        static ItemState Item(string path, DateTimeOffset when) =>
            new(path, ItemKind.File, "id:" + path, "version", null, 4, when, ItemFlags.None);
    }
}
