using System.Globalization;
using System.Text;
using Massdex.SyncShark.Engine.Local;

namespace Massdex.SyncShark.Engine.Tests.Local;

/// <summary>Checks for PendingResolver (`--engine-checks local`), written by PendingResolver's writer.
/// They cover the PendingRead, RecoveryChoice and RemovalResult contract through the public resolver.</summary>
public static class PendingResolverChecks
{
    private const string Target = "file.bin";
    private const string Operation = "operation-id";
    private const long Generation = 3;
    private const string Incoming = "incoming";
    private const string Original = "original";
    private const string Unrelated = "unlinked";

    private static readonly FileAttributes[] StageAttributes =
    [
        FileAttributes.Archive | FileAttributes.NotContentIndexed,
        FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.NotContentIndexed
    ];

    // Phases whose note is manual, uncertain or already past the safe point: Check only observes them.
    private static readonly PendingPhase[] ObservedPhases =
    [
        PendingPhase.Armed, PendingPhase.Recycled, PendingPhase.PermanentArmed, PendingPhase.RestoreArmed,
        PendingPhase.WrongRestored, PendingPhase.Retained, PendingPhase.Unknown
    ];

    private static readonly PendingPhase[] ManualPhases =
        [PendingPhase.WrongRestored, PendingPhase.Retained, PendingPhase.Unknown];

    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var previous = LocalBin.SendOverride;
        var sends = 0;
        LocalBin.SendOverride = _ =>
        {
            sends++;
            return false;
        };
        try
        {
            await CheckNoPendingAsync();
            await CheckPublishArmedAsync();
            await CheckPublishFromRemovedAsync();
            await CheckRemovedDeleteAsync();
            await CheckStageProofRefusedAsync();
            await CheckTopBitProofRefusedAsync();
            await CheckObservedPhasesAsync();
            await CheckReceiptPhasesAsync();
            await CheckStaleNoteAsync();
            await CheckPersistenceFailureAsync();
            await CheckCausesAsync();
            await CheckAcceptCurrentAsync();
            await CheckAbandonIncomingAsync();
            await CheckDiscardArmedAsync();
            await CheckBinItemKeptAsync();
            await CheckLegacyAsync();
            await CheckBlockedStageAsync();
            await CheckLockedProofReadAsync();
            await CheckSkippedFinalAsync();
            await CheckWrongSizeLockedAsync();
            await CheckCancelledAsync();
        }
        finally { LocalBin.SendOverride = previous; }
        Check.Require(sends == 0, "Recovery never sends a deletion to the bin");
    }

    private static async Task CheckNoPendingAsync()
    {
        foreach (var choice in Enum.GetValues<RecoveryChoice>())
        {
            using var fixture = new Fixture();
            var attempt = await fixture.ResolveAsync(choice);
            Check.Require(attempt.Result is
                { Outcome: RemovalOutcome.NoEffect, OperationId: null, Final: null, Cause: null },
                "No note is a no-effect result naming no operation");
            Check.Require(fixture.Advances.Count == 0 && File.ReadAllText(fixture.Final) == Original,
                "No note changes nothing");
        }
    }

    private static async Task CheckPublishArmedAsync()
    {
        var cases = new (string Name, bool Stage, bool Moved, string? Final, bool Folder, bool Publish)[]
        {
            ("stage only", true, false, null, false, true),
            ("moved before the crash", false, true, null, false, true),
            ("stage and unrelated final", true, false, Unrelated, false, false),
            ("stage and same-content final", true, false, Incoming, false, false),
            ("unrelated same-size final", false, false, Unrelated, false, false),
            ("same-content final of another identity", false, false, Incoming, false, false),
            ("neither file", false, false, null, false, false),
            ("final is a folder", false, false, null, true, false),
            ("stage and final folder", true, false, null, true, false)
        };
        foreach (var attributes in StageAttributes)
        foreach (var item in cases)
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            var stage = fixture.WriteStage(attributes);
            if (item.Moved)
            {
                File.SetAttributes(stage.Path, attributes & ~FileAttributes.Hidden);
                File.Move(stage.Path, fixture.Final);
            }
            else if (!item.Stage) File.Delete(stage.Path);
            if (item.Final is not null) File.WriteAllText(fixture.Final, item.Final);
            if (item.Folder) Directory.CreateDirectory(fixture.Final);
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));

            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            if (item.Publish) await RequirePublishedAsync(fixture, stage, attributes, attempt, item.Name);
            else RequireNotAdopted(fixture, stage, attempt, item.Stage, item.Final, item.Folder, item.Name);
        }
    }

    private static async Task RequirePublishedAsync(Fixture fixture, StageProof stage, FileAttributes attributes,
        Attempt attempt, string name)
    {
        Check.Require(attempt.Result is { Outcome: RemovalOutcome.Published, OperationId: Operation },
            name + ": Published names its operation");
        Check.Require(SameFile(attempt.Result!.Final, fixture.Listing.One(Target)), name + ": returns the final entry");
        Check.Require(File.ReadAllText(fixture.Final) == Incoming, name + ": published content");
        Check.Require(File.GetLastWriteTimeUtc(fixture.Final) == fixture.Source.Modified.UtcDateTime,
            name + ": preserves modification time");
        Check.Require(File.GetAttributes(fixture.Final) == (attributes & ~FileAttributes.Hidden),
            name + ": publication clears only Hidden");
        Check.Require(!File.Exists(stage.Path), name + ": no stage remains");
        Check.Require(fixture.Advances.SequenceEqual([PendingPhase.Published]), name + ": records Published once");

        var again = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(again.Result?.Outcome == RemovalOutcome.Published && fixture.Advances.Count == 1,
            name + ": a repeated restart re-proves Published without another effect");
    }

    private static void RequireNotAdopted(Fixture fixture, StageProof stage, Attempt attempt, bool hasStage,
        string? final, bool folder, string name)
    {
        Check.Require(attempt.RecoveryOwned, name + ": stays recovery-owned");
        Check.Require(!fixture.Advances.Any(IsAuthority), name + ": records no success");
        Check.Require(File.Exists(stage.Path) == hasStage, name + ": the stage is neither lost nor invented");
        if (hasStage) Check.Require(File.ReadAllText(stage.Path) == Incoming, name + ": keeps the stage bytes");
        if (final is not null) Check.Require(File.ReadAllText(fixture.Final) == final, name + ": keeps the occupant");
        else Check.Require(!File.Exists(fixture.Final) && Directory.Exists(fixture.Final) == folder,
            name + ": keeps the final absence or folder");
    }

    private static async Task CheckPublishFromRemovedAsync()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Final);
        var stage = fixture.WriteStage();
        fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.Removed, stage));
        fixture.BeforeAdvance = phase =>
        {
            var moved = phase == PendingPhase.Published;
            Check.Require(File.Exists(stage.Path) != moved && File.Exists(fixture.Final) == moved,
                "PublishArmed is durable before the move and Published only after it");
        };
        var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(attempt.Result?.Outcome == RemovalOutcome.Published &&
            fixture.Advances.SequenceEqual([PendingPhase.PublishArmed, PendingPhase.Published]),
            "A verified removal completes its intended publication through both durable phases");
        Check.Require(File.ReadAllText(fixture.Final) == Incoming, "The incoming bytes are published");
    }

    private static async Task CheckRemovedDeleteAsync()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Final);
        fixture.Hold(fixture.Note(PendingKind.LocalDelete, PendingPhase.Removed, null));
        var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(attempt.Result is { Outcome: RemovalOutcome.Removed, OperationId: Operation, Final: null },
            "A verified deletion reports Removed for exact settlement");
        Check.Require(fixture.Advances.Count == 0, "Reporting a verified removal advances nothing");
    }

    private static async Task CheckStageProofRefusedAsync()
    {
        foreach (var phase in new[] { PendingPhase.Removed, PendingPhase.PublishArmed })
        foreach (var replace in new[] { false, true })
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            var stage = fixture.WriteStage();
            var content = fixture.Tamper(stage, replace);
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, phase, stage));
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.RecoveryOwned && !fixture.Advances.Any(IsAuthority),
                "A substituted or edited stage is retained, never published");
            Check.Require(!File.Exists(fixture.Final) && File.ReadAllText(stage.Path) == content,
                "A foreign stage is neither moved nor deleted");
        }
    }

    // No live file carries a top-bit ID, so this pins that the signed spelling is compared as written, not parsed.
    private static async Task CheckTopBitProofRefusedAsync()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Final);
        var stage = fixture.WriteStage();
        var topBit = stage with { FileId = LocalIdentity.FileIdText(0x8000000000000001UL) };
        fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, topBit));
        var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(attempt.RecoveryOwned && !File.Exists(fixture.Final) && File.Exists(stage.Path),
            "A top-bit identity in its signed decimal spelling matches only that exact identity");
    }

    private static async Task CheckObservedPhasesAsync()
    {
        foreach (var phase in ObservedPhases)
        foreach (var kind in Enum.GetValues<PendingKind>())
        foreach (var permanent in new[] { false, true })
        foreach (var occupant in new[] { Original, Unrelated, null })
        {
            using var fixture = new Fixture();
            var stage = kind == PendingKind.LocalReplace ? fixture.WriteStage() : null;
            fixture.Occupy(occupant);
            fixture.Hold(fixture.Note(kind, phase, stage) with { PermanentAllowed = permanent });
            var first = await fixture.ResolveAsync(RecoveryChoice.Check);
            var second = await fixture.ResolveAsync(RecoveryChoice.Check);
            var name = $"{kind} {phase} with {occupant ?? "an empty path"}";
            RequireObserved(first, phase, name);
            Check.Require(second.Result?.Outcome == first.Result!.Outcome, name + ": a repeated restart agrees");
            Check.Require(!fixture.Advances.Any(IsAuthority), name + ": Check records no effect it did not prove");
            Check.Require(occupant is null ? !File.Exists(fixture.Final) : File.ReadAllText(fixture.Final) == occupant,
                name + ": Check never resends a removal");
            if (stage is not null) Check.Require(File.ReadAllText(stage.Path) == Incoming, name + ": keeps the stage");
        }
    }

    private static void RequireObserved(Attempt attempt, PendingPhase phase, string name)
    {
        Check.Require(attempt.Result is { OperationId: Operation } && !attempt.Settles,
            name + ": no success is claimed for this operation");
        // This matrix supplies no receipt or victim, so nothing here can tell a crash before the effect from one after.
        var expected = phase switch
        {
            PendingPhase.Retained => RemovalOutcome.Retained,
            PendingPhase.WrongRestored => RemovalOutcome.WrongRestored,
            _ => RemovalOutcome.Unknown
        };
        Check.Require(attempt.Result!.Outcome == expected, name + ": an occupant or empty path decides nothing");
    }

    private static async Task CheckReceiptPhasesAsync()
    {
        const string binItem = "bin-item";
        foreach (var verified in new[] { true, false })
        {
            using var fixture = new Fixture();
            var bin = Path.Combine(fixture.Root, binItem);
            if (verified) File.Move(fixture.Final, bin);
            else File.WriteAllText(bin, Unrelated);
            fixture.Occupy(null);
            fixture.Hold(fixture.Note(PendingKind.LocalDelete, PendingPhase.Recycled, null) with { BinLocator = bin });
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.Result?.Outcome == (verified ? RemovalOutcome.Removed : RemovalOutcome.Unknown),
                "Only a receipt proving the selected object is a verified removal");
            Check.Require(fixture.Advances.Contains(PendingPhase.Removed) == verified && File.Exists(bin),
                "A verified receipt advances to Removed and no receipt is touched");
        }
        using (var fixture = new Fixture())
        {
            fixture.Occupy(Unrelated);
            var victim = fixture.Proof(fixture.Final, Target, Unrelated);
            var note = fixture.Note(PendingKind.LocalDelete, PendingPhase.RestoreArmed, null);
            fixture.Hold(note with { Victim = victim });
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.Result?.Outcome == RemovalOutcome.WrongRestored &&
                SameFile(attempt.Result.Final, fixture.Listing.One(Target)) &&
                fixture.Advances.SequenceEqual([PendingPhase.WrongRestored]),
                "An observed restored victim proves WrongRestored without repeating the restore");
        }
    }

    private static async Task CheckStaleNoteAsync()
    {
        foreach (var phase in new[] { PendingPhase.Removed, PendingPhase.PublishArmed })
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            var stage = fixture.WriteStage();
            var note = fixture.Note(PendingKind.LocalReplace, phase, stage);
            fixture.Hold(note with { Generation = Generation + 1 });
            await RequireStoppedAsync(fixture, stage, new CurrentPending(note));
        }
    }

    private static async Task CheckPersistenceFailureAsync()
    {
        using (var fixture = new Fixture())
        {
            File.Delete(fixture.Final);
            var stage = fixture.WriteStage();
            var note = fixture.Note(PendingKind.LocalReplace, PendingPhase.Removed, stage);
            fixture.Hold(note);
            fixture.BeforeAdvance = _ => throw new InvalidOperationException("injected persistence failure");
            await RequireStoppedAsync(fixture, stage, new CurrentPending(note));
        }
        using (var fixture = new Fixture())
        {
            File.Delete(fixture.Final);
            var stage = fixture.WriteStage();
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));
            fixture.BeforeAdvance = phase =>
            {
                if (phase == PendingPhase.Published) throw new InvalidOperationException("injected failure");
            };
            var failed = await TryResolveAsync(fixture, fixture.Current);
            Check.Require(failed?.Settles != true, "A failed outcome write is not reported as Published");
            Check.Require(File.ReadAllText(fixture.Final) == Incoming, "The move before the failed write stands");
            fixture.BeforeAdvance = null;
            var recovered = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(recovered.Result?.Outcome == RemovalOutcome.Published &&
                fixture.Advances.SequenceEqual([PendingPhase.Published]),
                "Restart proves the completed publication against the stage and repeats no move");
        }
    }

    private static async Task RequireStoppedAsync(Fixture fixture, StageProof stage, PendingRead read)
    {
        var held = fixture.Current;
        var attempt = await TryResolveAsync(fixture, read);
        Check.Require(attempt is null && fixture.Advances.Count == 0, "A refused write fails and claims nothing");
        Check.Require(fixture.Current == held, "A refused write leaves the durable note");
        Check.Require(File.Exists(stage.Path) && !File.Exists(fixture.Final), "A refused write stops the effect");
    }

    // A stale note and a failed write surface as exceptions; neither is no-effect evidence.
    private static async Task<Attempt?> TryResolveAsync(Fixture fixture, PendingRead read)
    {
        try { return await fixture.ResolveAsync(RecoveryChoice.Check, CancellationToken.None, read); }
        catch (Exception error) when (error is InvalidOperationException or ChangedUnderneathException)
        {
            return null;
        }
    }

    private static async Task CheckCausesAsync()
    {
        foreach (var cause in new[] { TextKey.FailLocalInUse, TextKey.FailLocalInUseOrAccessDenied })
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            fixture.Hold(fixture.Note(PendingKind.LocalDelete, PendingPhase.Unknown, null) with { Cause = cause });
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.Result is { Outcome: RemovalOutcome.Unknown } && attempt.Result.Cause == cause,
                "A lock on an uncertain effect keeps its exact cause and changes no outcome");
        }
        foreach (TextKey? cause in new TextKey?[] { null, TextKey.RecycleFailed })
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            fixture.Hold(fixture.Note(PendingKind.LocalDelete, PendingPhase.Unknown, null) with { Cause = cause });
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.Cause is not { } actual || !IssuePolicy.IsLocalAccessRetryCause(actual),
                "A missing or outside cause never defaults into the in-use family");
        }
    }

    private static async Task CheckAcceptCurrentAsync()
    {
        foreach (var phase in ManualPhases)
        foreach (var occupant in new[] { Unrelated, null })
        {
            using var fixture = new Fixture();
            var stage = fixture.WriteStage();
            fixture.Occupy(occupant);
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, phase, stage));
            var attempt = await fixture.ResolveAsync(RecoveryChoice.AcceptCurrent);
            Check.Require(attempt.Result is { OperationId: Operation } && !attempt.Settles,
                "AcceptCurrent never claims the historical removal or publication succeeded");
            Check.Require(occupant is null ? attempt.Result!.Final is null
                : SameFile(attempt.Result!.Final, fixture.Listing.One(Target)),
                "AcceptCurrent names only the positively observed current object");
            Check.Require(occupant is null ? !File.Exists(fixture.Final) : File.ReadAllText(fixture.Final) == occupant,
                "AcceptCurrent changes the current object in no way");
            Check.Require(File.Exists(stage.Path) || fixture.Advances.Contains(PendingPhase.DiscardArmed),
                "A stage leaves custody only through armed disposal");
        }
    }

    private static async Task CheckAbandonIncomingAsync()
    {
        foreach (var phase in ManualPhases)
        foreach (var foreign in new[] { false, true })
        {
            using var fixture = new Fixture();
            var stage = fixture.WriteStage();
            var content = foreign ? fixture.Tamper(stage, replace: true) : null;
            fixture.Occupy(Unrelated);
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, phase, stage));
            fixture.BeforeAdvance = next => Check.Require(next != PendingPhase.DiscardArmed || File.Exists(stage.Path),
                "DiscardArmed is durable before the stage is deleted");
            var attempt = await fixture.ResolveAsync(RecoveryChoice.AbandonIncoming);
            Check.Require(attempt.Result is { OperationId: Operation } && !attempt.Settles,
                "AbandonIncoming never claims the historical removal or publication succeeded");
            Check.Require(File.ReadAllText(fixture.Final) == Unrelated,
                "AbandonIncoming never deletes the current occupant");
            if (foreign) Check.Require(File.ReadAllText(stage.Path) == content, "A foreign stage is never disposed");
            else Check.Require(!File.Exists(stage.Path) && fixture.Advances.Contains(PendingPhase.DiscardArmed),
                "AbandonIncoming disposes the verified owned stage through DiscardArmed");
        }
    }

    private static async Task CheckDiscardArmedAsync()
    {
        foreach (var state in new[] { "owned", "missing", "foreign" })
        {
            using var fixture = new Fixture();
            var stage = fixture.WriteStage();
            var content = state == "foreign" ? fixture.Tamper(stage, replace: true) : null;
            if (state == "missing") File.Delete(stage.Path);
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.DiscardArmed, stage));
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.Result is { OperationId: Operation } && !attempt.Settles,
                state + ": armed disposal is neither a removal nor a publication");
            Check.Require(content is null ? !File.Exists(stage.Path) : File.ReadAllText(stage.Path) == content,
                state + ": armed disposal removes only the verified owned stage");
            Check.Require(File.ReadAllText(fixture.Final) == Original,
                state + ": armed disposal leaves the final path");
        }
    }

    private static async Task CheckBinItemKeptAsync()
    {
        foreach (var choice in Enum.GetValues<RecoveryChoice>())
        {
            using var fixture = new Fixture();
            const string binItem = "bin-item";
            var bin = Path.Combine(fixture.Root, binItem);
            File.WriteAllText(bin, Unrelated);
            var victim = fixture.Proof(bin, binItem, Unrelated);
            fixture.Hold(fixture.Note(PendingKind.LocalDelete, PendingPhase.WrongRestored, null) with
            {
                BinLocator = bin,
                Victim = victim
            });
            var attempt = await fixture.ResolveAsync(choice);
            Check.Require(!attempt.Settles && File.ReadAllText(bin) == Unrelated,
                "A known wrong victim stays where it is, with no claimed success");
            Check.Require(File.ReadAllText(fixture.Final) == Original, "The restored object stays in place");
        }
    }

    private static async Task CheckLegacyAsync()
    {
        foreach (var choice in Enum.GetValues<RecoveryChoice>())
        {
            var allowed = await ResolveLegacyAsync(choice, stagePresent: false);
            var blocked = await ResolveLegacyAsync(choice, stagePresent: true);
            if (choice == RecoveryChoice.Check) continue;
            Check.Require(allowed.Result is not null, "A proved-absent stage locator permits acknowledgement");
            Check.Require(blocked.Result?.Outcome != allowed.Result!.Outcome,
                "A present stage locator blocks the acknowledgement its absence permits");
        }
    }

    private static async Task<Attempt> ResolveLegacyAsync(RecoveryChoice choice, bool stagePresent)
    {
        using var fixture = new Fixture();
        var stage = StageNames.For(fixture.Final);
        if (stagePresent) File.WriteAllText(stage, Incoming);
        var note = new LegacyPending("{\"Path\":\"file.bin\"}", "raw-token", Target, stage, fixture.Source);
        fixture.Current = note;
        var attempt = await fixture.ResolveAsync(choice);
        Check.Require(!attempt.Settles, "A legacy note is never published or removed");
        Check.Require(fixture.Advances.Count == 0 && fixture.Current == note, "A legacy note is only observed");
        Check.Require(File.ReadAllText(fixture.Final) == Original, "A legacy note leaves the current target");
        Check.Require(File.Exists(stage) == stagePresent, "A legacy note neither adopts nor deletes its stage");
        if (stagePresent) Check.Require(File.ReadAllText(stage) == Incoming, "A legacy note keeps the stage bytes");
        if (choice == RecoveryChoice.AcceptCurrent && !stagePresent)
            Check.Require(SameFile(attempt.Result?.Final, fixture.Listing.One(Target)),
                "Legacy AcceptCurrent observes the current target");
        return attempt;
    }

    private static async Task CheckBlockedStageAsync()
    {
        foreach (var share in new[] { FileShare.Read, FileShare.None })
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            var attributes = FileAttributes.Archive | FileAttributes.Hidden;
            var stage = fixture.WriteStage(attributes);
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));
            using (File.Open(stage.Path, FileMode.Open, FileAccess.Read, share))
            {
                await RequireRefusedAsync(fixture);
            }
            Check.Require(File.ReadAllText(stage.Path) == Incoming && !File.Exists(fixture.Final),
                "A safe refusal keeps the stage and publishes nothing");
            var kept = ~FileAttributes.Hidden;
            Check.Require((File.GetAttributes(stage.Path) & kept) == (attributes & kept),
                "A safe refusal keeps every non-Hidden stage attribute");
            var recovered = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(recovered.Result?.Outcome == RemovalOutcome.Published,
                "The same note recovers at the next real opportunity");
        }
    }

    private static async Task CheckLockedProofReadAsync()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Final);
        var stage = fixture.WriteStage(FileAttributes.Archive);
        File.Move(stage.Path, fixture.Final);
        fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));
        using (File.Open(fixture.Final, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            await RequireRefusedAsync(fixture);
        }
        Check.Require(File.ReadAllText(fixture.Final) == Incoming, "A refused proof read leaves the final bytes");
        var recovered = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(recovered.Result?.Outcome == RemovalOutcome.Published,
            "The same note proves the publication once the lock is gone");
    }

    // The cause is written once on the unchanged phase so it survives a restart; a repeated refusal writes nothing.
    private static async Task RequireRefusedAsync(Fixture fixture)
    {
        var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(attempt.Result is { Outcome: RemovalOutcome.NoEffect, OperationId: Operation, Final: null } &&
            attempt.Cause == TextKey.FailLocalInUse, "A refused read keeps its exact cause and proves nothing");
        Check.Require(fixture.Advances.SequenceEqual([PendingPhase.PublishArmed]) &&
            fixture.Current is CurrentPending { Value.Cause: TextKey.FailLocalInUse },
            "A refused read records its cause on the unchanged phase");
        var held = fixture.Current;
        var repeated = await fixture.ResolveAsync(RecoveryChoice.Check);
        Check.Require(repeated.Cause == TextKey.FailLocalInUse && fixture.Current == held &&
            fixture.Advances.Count == 1, "A repeated refusal writes nothing");
    }

    private static async Task CheckSkippedFinalAsync()
    {
        foreach (var hasStage in new[] { false, true })
        {
            using var fixture = new Fixture();
            File.Delete(fixture.Final);
            var target = Path.Combine(fixture.Root, "target");
            File.WriteAllText(target, Incoming);
            var stage = fixture.WriteStage();
            if (!hasStage) File.Delete(stage.Path);
            File.CreateSymbolicLink(fixture.Final, target);
            Check.Require(fixture.Listing.One(Target)?.Skip == TextKey.SkipLink.ToString(),
                "Skipped-final fixture is a link");
            fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));
            using (File.Open(target, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
                Check.Require(attempt.RecoveryOwned,
                    "A skipped final is never adopted and its target is never opened, even when the hash would match");
            }
            Check.Require(File.Exists(stage.Path) == hasStage && File.ReadAllText(target) == Incoming,
                "A skipped final leaves the stage and the link target");
        }
    }

    private static async Task CheckWrongSizeLockedAsync()
    {
        const string other = "other-size";
        using var fixture = new Fixture();
        var stage = fixture.WriteStage();
        File.Delete(stage.Path);
        fixture.Occupy(other);
        fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));
        using (File.Open(fixture.Final, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var attempt = await fixture.ResolveAsync(RecoveryChoice.Check);
            Check.Require(attempt.RecoveryOwned, "A locked final of the wrong size is refused without being read");
        }
        Check.Require(File.ReadAllText(fixture.Final) == other, "A wrong-size final is left untouched");
    }

    private static async Task CheckCancelledAsync()
    {
        using var fixture = new Fixture();
        File.Delete(fixture.Final);
        var attributes = FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.NotContentIndexed;
        var stage = fixture.WriteStage(attributes);
        fixture.Hold(fixture.Note(PendingKind.LocalReplace, PendingPhase.PublishArmed, stage));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var stopped = false;
        try { await fixture.ResolveAsync(RecoveryChoice.Check, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        Check.Require(stopped, "A pre-cancelled token stops recovery");
        Check.Require(!File.Exists(fixture.Final) && fixture.Advances.Count == 0,
            "A cancelled recovery moves and records nothing");
        Check.Require(File.ReadAllText(stage.Path) == Incoming, "A cancelled recovery leaves the stage bytes intact");
        Check.Require(File.GetAttributes(stage.Path) == attributes,
            "A cancelled recovery leaves all stage attributes intact");
    }

    private static bool IsAuthority(PendingPhase phase) => phase is PendingPhase.Removed or PendingPhase.PublishArmed
        or PendingPhase.Published or PendingPhase.PermanentArmed;

    // A listing entry carries no content hash, so the final is matched on its observed identity.
    private static bool SameFile(Entry? left, Entry? right) => left is not null && right is not null &&
        left.Path == right.Path && left.Id == right.Id && left.Size == right.Size && left.Modified == right.Modified;

    private static string Hash(string text) => DropboxContentHash.Compute(Encoding.UTF8.GetBytes(text));

    /// <summary>A resolver's answer: a result, or the item failure it refused with.</summary>
    private sealed record Attempt(RemovalResult? Result, ItemFailedException? Failure)
    {
        public TextKey? Cause => Result?.Cause ?? Failure?.Cause;
        public bool Settles => Result?.Outcome is RemovalOutcome.Removed or RemovalOutcome.Published;
        public bool RecoveryOwned => Result?.Outcome is RemovalOutcome.Retained or RemovalOutcome.Unknown;
    }

    /// <summary>One temporary pair holding the selected original, with the pair-bound note store the resolver
    /// composes. Each resolve builds a fresh resolver, so a repeated call stands for a restart.</summary>
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory(nameof(PendingResolverChecks)).FullName;
        public string Final { get; }
        public LocalListing Listing { get; }
        public Entry Source { get; }
        public PendingRead Current { get; set; } = new NoPending();
        public List<PendingPhase> Advances { get; } = [];
        public Action<PendingPhase>? BeforeAdvance { get; set; }

        private readonly PairConfig _pair;
        private readonly string _serial;
        private readonly RemovalProof _losing;

        public Fixture()
        {
            Final = Path.Combine(Root, Target);
            _pair = new PairConfig("pair", "Pair A", Root, "", "account", [], [], FirstRunPolicy.NewestWins, true, null);
            Listing = new LocalListing(_pair, new PathScope(_pair, [_pair]), (_, _, _, _) => { });
            // Every proof string is the invariant decimal form LocalBin compares ordinally.
            _serial = VolumeSpace.Serial(Root).ToString(CultureInfo.InvariantCulture);
            Source = new Entry(Target, false, Incoming.Length, DateTimeOffset.UnixEpoch.AddDays(1), "source-id",
                "source-revision", Hash(Incoming), null);
            File.WriteAllText(Final, Original);
            _losing = new RemovalProof(Listing.One(Target)!, _serial, Hash(Original));
        }

        public void Hold(PendingEffect note) => Current = new CurrentPending(note);

        public PendingEffect Note(PendingKind kind, PendingPhase phase, StageProof? stage)
        {
            var expected = _losing.Expected;
            var saved = new SyncedEntry(Target, Target, Target, "/" + Target, false, expected.Id, expected.Size,
                expected.Modified.UtcTicks, "dropbox-id", _losing.ContentHash);
            return new PendingEffect(1, Operation, Generation, kind, phase, Target, _losing, saved,
                kind == PendingKind.LocalReplace ? Source : null, stage, null, null, false, null);
        }

        public StageProof Proof(string full, string relative, string content) =>
            new(full, _serial, Listing.One(relative)!.Id, Encoding.UTF8.GetByteCount(content), Hash(content));

        public StageProof WriteStage(FileAttributes attributes = FileAttributes.Archive | FileAttributes.Hidden)
        {
            var stage = StageNames.For(Final);
            File.WriteAllText(stage, Incoming);
            File.SetLastWriteTimeUtc(stage, Source.Modified.UtcDateTime);
            var proof = Proof(stage, Path.GetFileName(stage), Incoming);
            File.SetAttributes(stage, attributes);
            return proof;
        }

        /// <summary>Breaks the proof: replaced keeps the bytes under a new identity, otherwise an in-place edit
        /// keeps identity, size and time. Returns the content left at the stage path.</summary>
        public string Tamper(StageProof stage, bool replace)
        {
            var content = replace ? Incoming : Unrelated;
            File.SetAttributes(stage.Path, FileAttributes.Normal);
            if (replace) File.Delete(stage.Path);
            File.WriteAllText(stage.Path, content);
            File.SetLastWriteTimeUtc(stage.Path, Source.Modified.UtcDateTime);
            return content;
        }

        /// <summary>Leaves the original, replaces it with another object, or empties the path.</summary>
        public void Occupy(string? content)
        {
            if (content == Original) return;
            File.Delete(Final);
            if (content is not null) File.WriteAllText(Final, content);
        }

        public async Task<Attempt> ResolveAsync(RecoveryChoice choice, CancellationToken ct = default,
            PendingRead? read = null)
        {
            var resolver = new PendingResolver(_pair, Listing, Advance, () => Current);
            try { return new Attempt(await resolver.ResolveAsync(read ?? Current, choice, ct), null); }
            catch (ItemFailedException error) { return new Attempt(null, error); }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Advance(PendingEffect expected, PendingEffect next)
        {
            BeforeAdvance?.Invoke(next.Phase);
            if (Current != new CurrentPending(expected)) throw new InvalidOperationException("Stale pending note.");
            Check.Require(next.OperationId == expected.OperationId && next.Generation == expected.Generation + 1,
                "An advance keeps the operation and takes the next generation");
            Check.Require(next.Kind == expected.Kind && next.Path == expected.Path && next.Losing == expected.Losing &&
                next.Saved == expected.Saved && next.Source == expected.Source,
                "An advance changes no selected fact");
            Current = new CurrentPending(next);
            Advances.Add(next.Phase);
        }
    }
}
