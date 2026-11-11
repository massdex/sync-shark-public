using System.Runtime.CompilerServices;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Checks for IssuePolicy, IssueClosure and SyncOutcome.RaisedBlocks (`--engine-checks issues`).</summary>
public static class IssuePolicyChecks
{
    public static void Run()
    {
        HoldingPolicy();
        LocalAccessRetryCause();
        PublicCategories();
        RetainedByOwner();
        ObsoleteSubjects();
        ConfiguredScopeRetirement();
        ConfiguredScopeLifetime();
        SubjectlessFailures();
        NonRetiringKinds();
        RaisedBlocks();
        OwesWorkAnswers();
        ClosureSets();
        InheritedKind();
        InheritedKindMatchesBaseline();
        HeldKindPrecedence();
        ObservedAndMappedHolds();
        ProtectedExclusion();
        FirstRunSubjects();
        ReobservedFirstRunSubjects();
        StaleContentProgress();
        UnknownMoveSubjects();
    }

    // Neutral subjects distinguish exact identity from canonical-key lookup.
    private const string ParentPath = "Parent";
    private const string ChildPath = "Parent/Child";
    private const string GrandchildPath = "Parent/Child/Leaf";
    private const string SiblingPath = "ParentSibling/Child";
    private const string CasePath = "parent";
    private const string OriginalId = "original-id";
    private const string ReplacementId = "replacement-id";
    private const string DestinationPath = "Destination";

    private static void StaleContentProgress()
    {
        var row = Make(PathNames.Key(ParentPath)) with { Local = Observed(ParentPath, OriginalId) };
        foreach (var (before, after, unchanged) in new (string?, string?, bool)[]
        {
            (null, null, true), (null, "digest-a", false), ("digest-a", "digest-a", true),
            ("digest-a", null, true), ("digest-a", "digest-b", false)
        })
        {
            Check.Require(IssuePolicy.SameStaleEvidence(row, row, previousHash: before, currentHash: after) == unchanged,
                "Only a new validated digest adds content progress; losing an observation is not progress.");
            Check.Require(!IssuePolicy.SameStaleEvidence(row, row with { Local = row.Local! with { Id = ReplacementId } },
                previousHash: before, currentHash: after), "Metadata change remains progress independently of content.");
        }
    }

    private static void FirstRunSubjects()
    {
        var local = Observed(ParentPath, OriginalId);
        var row = Make(PathNames.Key(ParentPath)) with { Local = local };
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        foreach (var issueKind in Enum.GetValues<IssueKind>())
        {
            var subject = new IssueSubject([
                new(Side.Local, ParentPath, OriginalId), new(Side.Local, ChildPath, OriginalId),
                new(Side.Dropbox, DestinationPath, ReplacementId)], null, null, kind);
            var issue = Notice(issueKind, ParentPath, local, null) with { Subject = subject };
            var subjects = new Dictionary<string, IssueSubject>();
            IssuePolicy.AddFirstRunSubject(subjects, issue);
            var expectedKeys = issueKind == IssueKind.Failure && kind != IssueSubject.FirstRunDeletionKind.None
                ? new[] { PathNames.Key(ParentPath), PathNames.Key(ChildPath) } : Array.Empty<string>();
            Check.Require(subjects.Keys.ToHashSet().SetEquals(expectedKeys), nameof(FirstRunSubjects));
            foreach (var stored in subjects.Values)
                Check.Require(ReferenceEquals(stored, subject), nameof(FirstRunSubjects));
        }
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            var subject = new IssueSubject([new(Side.Local, ParentPath, OriginalId)], null, null, kind);
            var subjects = new Dictionary<string, IssueSubject> { [row.Key] = subject };
            foreach (var variant in new[] { row, row with { Synced = Synced(ParentPath, ParentPath) },
                row with { Local = local with { Skip = nameof(TextKey.SkipUnreadable) } },
                row with { Dropbox = local } })
            {
                Check.Require(IssuePolicy.FirstRunKind(variant, subjects, false) == kind,
                    nameof(FirstRunSubjects));
            }
            foreach (var changed in new Entry?[] { null, local with { Id = ReplacementId },
                local with { Path = CasePath }, local with { Path = ChildPath } })
            {
                var replaced = row with { Local = changed };
                Check.Require(!IssuePolicy.SameFirstRunLocal(local, changed) &&
                    IssuePolicy.FirstRunKind(replaced, subjects, false) == IssueSubject.FirstRunDeletionKind.None,
                    nameof(FirstRunSubjects));
            }
            var child = row with { Key = PathNames.Key(ChildPath), Local = local with { Path = ChildPath } };
            Check.Require(IssuePolicy.FirstRunKind(child, subjects, false) == IssueSubject.FirstRunDeletionKind.None &&
                IssuePolicy.FirstRunKind(child, subjects, true) == IssueSubject.FirstRunDeletionKind.Item,
                nameof(FirstRunSubjects));
            Check.Require(IssuePolicy.FirstRunKind(child with { Synced = Synced(ChildPath, ChildPath) },
                subjects, true) == IssueSubject.FirstRunDeletionKind.None, nameof(FirstRunSubjects));
            foreach (var first in new[] { false, true })
            {
                Check.Require(IssuePolicy.InFirstRunScope(row, first, kind) ==
                    (kind != IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
                        (first || kind != IssueSubject.FirstRunDeletionKind.None)), nameof(FirstRunSubjects));
            }
            var covering = IssuePolicy.CoveringSubtrees(subjects, PathNames.Key(ChildPath)).ToArray();
            Check.Require(covering.SequenceEqual(kind == IssueSubject.FirstRunDeletionKind.UnscannedSubtree
                ? new[] { subject } : Array.Empty<IssueSubject>()), nameof(FirstRunSubjects));
            Check.Require(!IssuePolicy.CoveringSubtrees(subjects, PathNames.Key(SiblingPath)).Any(),
                nameof(FirstRunSubjects));
        }
        var subtree = new IssueSubject([new(Side.Local, ParentPath, OriginalId)], null, null,
            IssueSubject.FirstRunDeletionKind.UnscannedSubtree);
        var nested = subtree with { Items = [new(Side.Local, ChildPath, ReplacementId)] };
        var tree = new Dictionary<string, IssueSubject>
        {
            [PathNames.Key(ParentPath)] = subtree, [PathNames.Key(ChildPath)] = nested
        };
        Check.Require(IssuePolicy.CoveringSubtrees(tree, PathNames.Key(ChildPath)).SequenceEqual([nested, subtree]) &&
            IssuePolicy.CoveringSubtrees(tree, PathNames.Key(GrandchildPath)).SequenceEqual([nested, subtree]),
            nameof(FirstRunSubjects));
        Check.Require(!IssuePolicy.SameFirstRunLocal(null, local) &&
            IssuePolicy.SameFirstRunLocal(local, local with { Size = local.Size + 1 }), nameof(FirstRunSubjects));
        foreach (var variant in new[] { row with { Local = null }, row with { Dropbox = local },
            row with { Synced = Synced(ParentPath, ParentPath) },
            row with { Local = local with { Skip = nameof(TextKey.SkipUnreadable) } } })
        {
            Check.Require(!IssuePolicy.UnbaselinedLocal(variant) &&
                !IssuePolicy.InFirstRunScope(variant, true, IssueSubject.FirstRunDeletionKind.Item),
                nameof(FirstRunSubjects));
        }
    }

    private static void ReobservedFirstRunSubjects()
    {
        var local = Observed(ParentPath, OriginalId);
        foreach (var kind in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        foreach (var first in new[] { false, true })
        foreach (var selected in new[] { false, true })
        {
            var original = Make(PathNames.Key(ParentPath)) with
            {
                Local = local,
                Context = new(false, false, false, null, false, first, true, selected,
                    FirstRunDeletion: kind)
            };
            var observed = original with
            {
                Deletion = new DeletionEvidence.NoRetainedHistory(ParentPath),
                Context = original.Context with { Destination = DestinationPath, Held = true,
                    HeldKind = IssueKind.Tie, OutOfScope = true, Skipped = true, FirstRun = !first }
            };
            foreach (var variant in new[] { observed, observed with { Synced = Synced(ParentPath, ParentPath) },
                observed with { Dropbox = local },
                observed with { Local = local with { Skip = nameof(TextKey.SkipUnreadable) } } })
            {
                var result = IssuePolicy.ReobserveFirstRun(original, variant);
                var remainsSelected = ReferenceEquals(variant, observed) && selected &&
                    kind != IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice;
                Check.Require(result.Deletion is null && result.Context == (variant.Context with
                    { FirstRunDeletion = kind, DeletionEligible = remainsSelected }) &&
                    ReferenceEquals(result.Synced, variant.Synced) && ReferenceEquals(result.Local, variant.Local),
                    nameof(ReobservedFirstRunSubjects));
            }
            foreach (var replacement in new Entry?[] { null, local with { Id = ReplacementId },
                local with { Path = CasePath } })
            {
                var result = IssuePolicy.ReobserveFirstRun(original, observed with { Local = replacement });
                Check.Require(result.Deletion is null && result.Context == (observed.Context with
                    { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None,
                        DeletionEligible = first && replacement is not null, CompleteInventory = false }),
                    nameof(ReobservedFirstRunSubjects));
            }
        }
    }

    private static void HoldingPolicy()
    {
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var cause in Enum.GetValues<TextKey>())
        {
            var expected = kind switch
            {
                IssueKind.Failure => cause is TextKey.FailDropboxNameRejected or TextKey.FailDropboxNoPermission,
                IssueKind.Tie or IssueKind.NameClash =>
                    cause is not (TextKey.ClashMappedNames or TextKey.ClashMappedDiffers),
                _ => false,
            };
            Check.Require(IssuePolicy.Holds(kind, cause) == expected, "Holds: " + kind + "/" + cause);
        }
    }

    private static void UnknownMoveSubjects()
    {
        var move = new IssueSubject.UnknownMove("remote-id", "Source", "Destination", "Intermediate", false);
        var subject = new IssueSubject([new(Side.Local, "Local", "local-id")], null) { Move = move };
        var issue = new Issue(1, IssueKind.Failure, TextKey.FailChanged, "Issue", subject,
            DateTimeOffset.UnixEpoch, false);
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var debt in Enum.GetValues<IssueSubject.FirstRunDeletionKind>())
        {
            var current = subject with { FirstRunDeletion = debt };
            var unknown = kind == IssueKind.Failure && debt == IssueSubject.FirstRunDeletionKind.None;
            Check.Require(IssuePolicy.Holds(kind, TextKey.FailChanged, current) ==
                (unknown || IssuePolicy.Holds(kind, TextKey.FailChanged)),
                "An unknown Failure holds independently of cause; first-run debt uses the ordinary cause table.");
            Check.Require(!IssuePolicy.Holds(IssueKind.Failure, TextKey.FailChanged, current with { Move = null }),
                "A move-less ordinary Failure does not acquire the unknown-move hold.");
        }
        Check.Require(IssuePolicy.HeldKeys(issue).ToHashSet().SetEquals(new[]
        {
            PathNames.KeyFor(Side.Local, "Local"), PathNames.KeyFor(Side.Local, "Issue"),
            PathNames.KeyFor(Side.Dropbox, "Issue"), PathNames.KeyFor(Side.Dropbox, "Source"),
            PathNames.KeyFor(Side.Dropbox, "Destination"), PathNames.KeyFor(Side.Dropbox, "Intermediate")
        }), "An unknown move holds its original items and every operation edge on its correct side.");
        // X1: the identity is held wherever it is observed (IssueFactsChecks.UnknownMoveHolds), not as a path key.
        Check.Require(IssuePolicy.HeldIdentity(issue.Kind, issue.Subject) == "remote-id" &&
            IssuePolicy.HeldIdentity(issue.Kind, issue.Subject with { Move = null }) is null &&
            IssuePolicy.HeldIdentity(issue.Kind, issue.Subject with
                { FirstRunDeletion = IssueSubject.FirstRunDeletionKind.Item }) is null,
            "Only an unknown move without first-run debt holds its expected identity.");
        Retires(issue, null, null, false, "unknown move survives absent observations");
        // OutsideScope ignores Notice.txt, so the issue path must sit under that configured exit.
        Retires(issue with { Path = "Notice.txt" }, null, null, true, "unknown move permits configured scope exit",
            outsideScope: true);
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var dismissed in new[] { false, true })
        foreach (var ignored in new[] { false, true })
        {
            var current = issue with { Kind = kind, Dismissed = dismissed, Ignored = ignored };
            var owns = !dismissed && kind is IssueKind.Failure or IssueKind.NameClash or IssueKind.Tie;
            Check.Require(IssuePolicy.OwnsItem(current, Side.Local, "local-id") == owns &&
                !IssuePolicy.OwnsItem(current, Side.Dropbox, "local-id") &&
                !IssuePolicy.OwnsItem(current, Side.Local, "unrelated-id"),
                "Only an open actionable notice owns the exact side and identity; Ignore does not release it.");
        }
        var local = Observed(ParentPath, OriginalId);
        var remote = Observed(DestinationPath, ReplacementId);
        var debtIssue = Notice(IssueKind.Failure, ParentPath, local, remote) with
        {
            Subject = new IssueSubject([new(Side.Local, ParentPath, OriginalId),
                new(Side.Dropbox, DestinationPath, ReplacementId)], null, null,
                IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
        };
        Retires(debtIssue, local, null, false, "missing Dropbox cannot retire owed Local history");
        Retires(debtIssue, null, remote, true, "missing Local retires debt despite surviving Dropbox");
    }

    private static void PublicCategories()
    {
        Check.Require(IssuePolicy.FailureInfoCauses.SequenceEqual([TextKey.FailLocalInUse]),
            "the immutable public category owner lists only confirmed sharing violations");
        foreach (var kind in Enum.GetValues<IssueKind>())
        foreach (var cause in Enum.GetValues<TextKey>())
            Check.Require(IssuePolicy.PublicKind(kind, cause) ==
                (kind == IssueKind.Failure && cause == TextKey.FailLocalInUse ? IssueKind.Info : kind),
                "PublicKind: " + kind + "/" + cause);
    }

    private static void LocalAccessRetryCause()
    {
        foreach (var cause in Enum.GetValues<TextKey>())
        {
            var expected = cause is TextKey.FailLocalInUse or TextKey.FailLocalInUseOrAccessDenied;
            Check.Require(IssuePolicy.IsLocalAccessRetryCause(cause) == expected,
                "IsLocalAccessRetryCause: " + cause);
        }
    }

    // The owner's retention answer must win before any scope or scan lookup, including the empty root.
    private static void RetainedByOwner()
    {
        var local = Observed("Local/Item.txt", "local-id");
        var dropbox = Observed("Remote/Item?.txt", "dropbox-id");
        (string What, Issue Issue)[] cases =
        [
            ("Failure with subjects", Notice(IssueKind.Failure, "Notice.txt", local, dropbox)),
            ("Failure on the empty root", Notice(IssueKind.Failure, "Notice.txt", null, null)),
            ("Tie with subjects", Notice(IssueKind.Tie, "Notice.txt", local, dropbox)),
            ("NameClash without subjects", Notice(IssueKind.NameClash, "Notice.txt", null, null)),
        ];
        foreach (var (what, issue) in cases)
        {
            var calls = 0;
            var retired = IssuePolicy.Obsolete(issue,
                (Side side, string path) => { calls++; return null; },
                new PrefixSet(), new PrefixSet(), new PrefixSet(), new PrefixSet(), Scope(),
                retainedByOwner: true);
            Check.Require(!retired && calls == 0,
                "Obsolete: owner-retained debt wins before every scan lookup: " + what);
        }
    }

    private static void ObsoleteSubjects()
    {
        var local = Observed("Local/Item.txt", "local-id");
        var dropbox = Observed("Remote/Item?.txt", "dropbox-id");
        foreach (var kind in new[] { IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash })
        foreach (var dismissed in new[] { false, true })
        {
            var issue = Notice(kind, "Notice.txt", local, dropbox) with { Dismissed = dismissed };
            Retires(issue, local, dropbox, false, "unchanged subjects");
            Retires(issue, null, null, true, "both subjects absent from trusted scans");
            foreach (var side in Enum.GetValues<Side>())
            {
                var before = side == Side.Local ? local : dropbox;
                (Entry? Entry, bool Expected, string What)[] cases =
                [
                    (null, true, "one former participant deleted while the other remains"),
                    (before with { Path = "Moved/Item.txt" }, true, "participant moved with the same id"),
                    (before with { Id = "replacement-id" }, true, "participant replaced at the same path"),
                    (before with { Id = "", Skip = nameof(TextKey.SkipUnreadable) }, false,
                        "skipped occupant is not an identity change"),
                    (before with { Size = before.Size + 1 }, false, "size alone does not retire identity"),
                    (before with { Modified = before.Modified.AddTicks(1) }, false, "time alone does not retire identity"),
                    (before with { ContentHash = "changed-hash" }, false, "hash alone does not retire identity"),
                ];
                foreach (var (entry, expected, what) in cases)
                    Retires(issue, side == Side.Local ? entry : local, side == Side.Dropbox ? entry : dropbox,
                        expected, what + " on " + side);
                Retires(issue, side == Side.Local ? null : local, side == Side.Dropbox ? null : dropbox,
                    false, "unreadable or skipped absence is not deletion on " + side, untrustedSide: side);
            }
        }
    }

    private static void ConfiguredScopeRetirement()
    {
        var local = Observed("Local/Item.txt", "local-id");
        var dropbox = Observed("Remote/Item.txt", "dropbox-id");
        // The owner derives both classifications now; these cases supply the scopes and exclusion sets it reads.
        (string What, bool OutsideScope, bool Applied, bool Pending, bool Expected)[] cases =
        [
            ("outside configured scope", true, false, false, true),
            ("exclusion applied or changed to Keep", false, true, false, true),
            ("pending exclusion: neither", false, false, true, false),
        ];
        foreach (var kind in new[] { IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash })
        foreach (var (what, outsideScope, applied, pending, expected) in cases)
        {
            var issue = Notice(kind, "Notice.txt", local, dropbox);
            Retires(issue, local, dropbox, expected, what, outsideScope, applied, pending);
            // Untrusted facts prove nothing, so they also defeat configured-scope retirement (engine.md 8.1 step 6).
            Retires(issue, null, null, outsideScope ? false : expected, what + " with untrusted absence",
                outsideScope, applied, pending, untrustedBoth: true);
        }
    }

    private static void SubjectlessFailures()
    {
        var local = Observed("Folder/Item.txt", "local-id");
        var dropbox = Observed(local.Path, "dropbox-id");
        var issue = Notice(IssueKind.Failure, local.Path, null, null);
        Retires(issue, local, dropbox, false, "subject-less Failure still has occupants");
        Retires(issue, local, null, false, "subject-less Failure still has a Local occupant");
        Retires(issue, null, dropbox, false, "subject-less Failure still has a Dropbox occupant");
        Retires(issue, null, null, true, "subject-less Failure absent on both trusted sides");
        foreach (var side in Enum.GetValues<Side>())
            Retires(issue, null, null, false, "subject-less Failure has untrusted absence on " + side,
                untrustedSide: side);
        Retires(issue, null, null, true, "subject-less Failure outside configured scope", outsideScope: true);
        Retires(issue, null, null, false, "untrusted absence never proves configured scope",
            outsideScope: true, untrustedBoth: true);
        Retires(issue, null, null, false, "subject-less Failure in a pending exclusion",
            untrustedBoth: true);
        Retires(issue, null, null, true, "subject-less Failure after exclusion application",
            applied: true, untrustedBoth: true);
    }

    private static void NonRetiringKinds()
    {
        var local = Observed("Local/Item.txt", "local-id");
        foreach (var kind in new[]
                 {
                     IssueKind.Info, IssueKind.Skipped, IssueKind.DriveChange, IssueKind.RootIdentity
                 })
        foreach (var outsideScope in new[] { false, true })
        foreach (var applied in new[] { false, true })
        {
            Retires(Notice(kind, "Notice.txt", local, null), null, null, false,
                "non-operational retirement kind with missing subject", outsideScope, applied);
            Retires(Notice(kind, "Notice.txt", null, null), null, null, false,
                "non-operational retirement kind without subject", outsideScope, applied);
        }
    }

    private static Entry Observed(string path, string id) =>
        new(path, false, 1, DateTimeOffset.UnixEpoch, id, "revision", "hash", null);

    private static Issue Notice(IssueKind kind, string path, Entry? local, Entry? dropbox)
    {
        var items = new List<IssueItem>();
        if (local is not null) items.Add(new(Side.Local, local.Path, local.Id));
        if (dropbox is not null) items.Add(new(Side.Dropbox, dropbox.Path, dropbox.Id));
        var cause = kind switch
        {
            IssueKind.Tie => TextKey.TieEqualTimes,
            IssueKind.NameClash => TextKey.ClashNameTaken,
            _ => TextKey.FailDropboxNoPermission,
        };
        return new(1, kind, cause, path, new IssueSubject(items, null), DateTimeOffset.UnixEpoch, false);
    }

    private static void Retires(Issue issue, Entry? local, Entry? dropbox, bool expected, string what,
        bool outsideScope = false, bool applied = false, bool pending = false,
        Side? untrustedSide = null, bool untrustedBoth = false)
    {
        var scope = outsideScope ? OutsideScope() : Scope();
        var actual = IssuePolicy.Obsolete(issue,
            (side, observed) =>
            {
                Check.Require(KnownPath(issue, observed),
                    "Obsolete looks up an original path, never a canonical key: " + what);
                return side == Side.Local ? local : dropbox;
            },
            untrustedBoth || untrustedSide == Side.Local ? SkippedKeys(issue, Side.Local) : new PrefixSet(),
            untrustedBoth || untrustedSide == Side.Dropbox ? SkippedKeys(issue, Side.Dropbox) : new PrefixSet(),
            applied ? CoveredKeys(issue) : new PrefixSet(),
            pending ? CoveredKeys(issue) : new PrefixSet(),
            scope, retainedByOwner: false);
        Check.Require(actual == expected, "Obsolete: " + issue.Kind + "/dismissed=" + issue.Dismissed + ": " + what);
    }

    // Every original spelling the owner may ask about: the issue path and each subject item's own path.
    private static bool KnownPath(Issue issue, string path) =>
        path == issue.Path || issue.Subject.Items.Any(item => item.Path == path);

    // Exactly the keys Covered asks for: both sides of the issue path, then each subject item's own side.
    private static PrefixSet CoveredKeys(Issue issue)
    {
        var set = new PrefixSet();
        set.Add(PathNames.KeyFor(Side.Local, issue.Path));
        set.Add(PathNames.KeyFor(Side.Dropbox, issue.Path));
        foreach (var item in issue.Subject.Items) set.Add(PathNames.KeyFor(item.Side, item.Path));
        return set;
    }

    private static PrefixSet SkippedKeys(Issue issue, Side side)
    {
        var set = new PrefixSet();
        set.Add(PathNames.KeyFor(side, issue.Path));
        foreach (var item in issue.Subject.Items) set.Add(PathNames.KeyFor(side, item.Path));
        return set;
    }

    // The derived configured scope is built once for the immutable scope's lifetime and cannot be supplied by a
    // caller: repeated retirement asks and repeated issues both read the one derivation (BD-2).
    private static void ConfiguredScopeLifetime()
    {
        var local = Observed("Local/Item.txt", "local-id");
        var dropbox = Observed("Remote/Item.txt", "dropbox-id");
        var issue = Notice(IssueKind.Failure, "Ignored.txt", local, dropbox);
        var scope = Scope("FileName:Ignored.txt", new Exclusion(Side.Local, "Pending", true));
        Check.Require(!ReferenceEquals(scope.Configured, scope) &&
            ReferenceEquals(scope.Configured, scope.Configured),
            "a scope with exclusions derives one configured scope and keeps it");
        for (var attempt = 0; attempt < 3; attempt++)
            Check.Require(IssuePolicy.Obsolete(issue,
                    (side, _) => side == Side.Local ? local : dropbox,
                    new PrefixSet(), new PrefixSet(), new PrefixSet(), new PrefixSet(), scope,
                    retainedByOwner: false),
                "each repeated issue is outside the one derived configured scope");
        Check.Require(ReferenceEquals(scope.Configured, scope.Configured),
            "repeated retirement asks do not rebuild the configured scope");
        var plain = Scope("FileName:Ignored.txt");
        Check.Require(ReferenceEquals(plain.Configured, plain),
            "a scope with no exclusions is its own configured scope");
    }

    // An ignored completion gives no answer at all, so the caller keeps an earlier unresolved alias; only a real
    // answer may clear it (BD-1).
    private static void OwesWorkAnswers()
    {
        var store = new InMemoryStateStore();
        var row = Make("row");
        Check.Require(Outcome(null, deferred: true).OwesWork(row, store, "pair") is null &&
            Outcome(null, false, NoneReason.RemovedWithFolder).OwesWork(row, store, "pair") is null,
            "OwesWork: a deferred or removed-with-folder outcome gives no answer");
        var excluded = row with { Context = row.Context with { OutOfScope = true } };
        Check.Require(Outcome(null, false, NoneReason.InSync).OwesWork(row, store, "pair") == false &&
            Outcome(null, false, NoneReason.OutOfScope).OwesWork(excluded, store, "pair") == false &&
            Outcome(null, false, NoneReason.OutOfScope).OwesWork(row, store, "pair") == true &&
            Outcome(IssueKind.Failure, false).OwesWork(row, store, "pair") == true,
            "OwesWork: completed, out-of-scope and failing rows give real answers");
        var unresolved = new HashSet<string>(StringComparer.Ordinal) { row.Key };
        foreach (var ignored in new[]
                 { Outcome(null, deferred: true), Outcome(null, false, NoneReason.RemovedWithFolder) })
            if (ignored.OwesWork(row, store, "pair") is { } owes)
                foreach (var key in new[] { row.Key })
                {
                    if (owes) unresolved.Add(key);
                    else unresolved.Remove(key);
                }
        Check.Require(unresolved.Contains(row.Key),
            "an ignored outcome leaves an earlier unresolved alias in place");
        if (Outcome(null, false, NoneReason.InSync).OwesWork(row, store, "pair") is { } completed)
            foreach (var key in new[] { row.Key })
            {
                if (completed) unresolved.Add(key);
                else unresolved.Remove(key);
            }
        Check.Require(unresolved.Count == 0, "a completed outcome clears the unresolved alias");
    }

    private static PathScope Scope(string? ignoreRules = null, params Exclusion[] exclusions) =>
        new(new PairConfig("pair", "pair", "Local", "Remote", "pair",
            ignoreRules is null ? [] : [ignoreRules], exclusions, FirstRunPolicy.NewestWins, true, null), []);

    // The notice and both participant names are ignored, so every one of them is outside configured scope.
    private static PathScope OutsideScope() => Scope("FileName:Notice.txt;FileName:Item.txt");

    private static void RaisedBlocks()
    {
        Blocks(IssueKind.Failure, true);
        Blocks(IssueKind.Tie, true);
        Blocks(IssueKind.NameClash, true);
        Blocks(IssueKind.Info, false);
        Blocks(IssueKind.Skipped, false);
        Blocks(null, false);
        Check.Require(Outcome(IssueKind.Failure, deferred: true).RaisedBlocks(),
            "RaisedBlocks looks only at the raised kind");
        Check.Require(Outcome(null, deferred: true, none: NoneReason.InSync).RaisedBlocks() == false,
            "RaisedBlocks ignores Deferred and None");
    }

    private static void Blocks(IssueKind? raised, bool expected) =>
        Check.Require(Outcome(raised, false).RaisedBlocks() == expected, "RaisedBlocks: " + raised);

    private static void ClosureSets()
    {
        RequireKinds(IssueClosure.AfterEffect, nameof(IssueClosure.AfterEffect), IssueKind.Failure);
        RequireKinds(IssueClosure.AfterInSync, nameof(IssueClosure.AfterInSync),
            IssueKind.Failure, IssueKind.NameClash);
        RequireKinds(IssueClosure.AfterHeldInSync, nameof(IssueClosure.AfterHeldInSync),
            IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash);
        RequireKinds(IssueClosure.AfterKeepBoth, nameof(IssueClosure.AfterKeepBoth),
            IssueKind.Failure, IssueKind.Tie);
        IReadOnlyList<IssueKind>[] all =
        [
            IssueClosure.AfterEffect, IssueClosure.AfterInSync, IssueClosure.AfterHeldInSync,
            IssueClosure.AfterKeepBoth
        ];
        for (var i = 0; i < all.Length; i++)
        for (var j = i + 1; j < all.Length; j++)
            Check.Require(!ReferenceEquals(all[i], all[j]), "closure sets stay distinct");
        Check.Require(ReferenceEquals(IssueClosure.AfterEffect, IssueClosure.AfterEffect),
            "closure sets are initialized once");
    }

    private static void InheritedKind()
    {
        var kinds = new Dictionary<string, IssueKind>(StringComparer.Ordinal)
        {
            ["held.txt"] = IssueKind.NameClash,
            ["folder"] = IssueKind.NameClash,
            ["p"] = IssueKind.Tie,
            ["p/c"] = IssueKind.NameClash,
            ["ab"] = IssueKind.NameClash,
            [""] = IssueKind.NameClash,
        };
        Kind(kinds, "held.txt", IssueKind.NameClash, "the key's own kind");
        Kind(kinds, "folder/child.txt", IssueKind.NameClash, "the nearest ancestor's kind");
        Kind(kinds, "folder/a/b", IssueKind.NameClash, "a deep ancestor's kind");
        Kind(kinds, "p/c/g", IssueKind.NameClash, "the nearest of two recorded ancestors");
        Kind(kinds, "p/d", IssueKind.Tie, "a sibling keeps the parent's kind");
        Kind(kinds, "p/c", IssueKind.NameClash, "the exact key beats its ancestor");
        Kind(kinds, "folderSibling/x", IssueKind.Tie, "an ancestor ends at a segment boundary");
        Kind(kinds, "a/b/x", IssueKind.Tie, "a name prefix is not an ancestor");
        Kind(kinds, "unrecorded/x", IssueKind.Tie, "Tie when nothing is recorded");
        Kind(kinds, "top", IssueKind.Tie, "the empty root key is never consulted");
    }

    private static void Kind(IReadOnlyDictionary<string, IssueKind> kinds, string key, IssueKind expected, string what) =>
        Check.Require(IssuePolicy.InheritedKind(kinds, key) == expected, "InheritedKind: " + what);

    // The owner span boundary search replaced the old string search; both must agree on every key.
    private static void InheritedKindMatchesBaseline()
    {
        var kinds = new Dictionary<string, IssueKind>(StringComparer.Ordinal)
        {
            ["held.txt"] = IssueKind.NameClash,
            ["folder"] = IssueKind.NameClash,
            ["p"] = IssueKind.Tie,
            ["p/c"] = IssueKind.NameClash,
            ["B"] = IssueKind.Tie,
            ["/a"] = IssueKind.NameClash,
            ["a//b"] = IssueKind.NameClash,
            ["a/"] = IssueKind.Tie,
            [""] = IssueKind.NameClash,
        };
        var alphabet = new[] { 'a', '/', 'B' };
        for (var length = 0; length <= 6; length++)
        {
            var count = 1;
            for (var index = 0; index < length; index++) count *= alphabet.Length;
            for (var code = 0; code < count; code++)
            {
                var chars = new char[length];
                var value = code;
                for (var index = 0; index < length; index++)
                {
                    chars[index] = alphabet[value % alphabet.Length];
                    value /= alphabet.Length;
                }
                var key = new string(chars);
                Check.Require(IssuePolicy.InheritedKind(kinds, key) == BaselineInheritedKind(kinds, key),
                    "InheritedKind agrees with the old string search for '" + key + "'");
            }
        }
        Check.Require(Throws<ArgumentNullException>(() => IssuePolicy.InheritedKind(kinds, null!)) &&
            Throws<ArgumentNullException>(() => BaselineInheritedKind(kinds, null!)),
            "both implementations reject a null key at the dictionary lookup");
        var ordinary = Capture(() => _ = IssuePolicy.InheritedKind(kinds, null!));
        Check.Require(ordinary == Capture(() => _ = BaselineInheritedKind(kinds, null!)) &&
            ordinary.StartsWith("System.ArgumentNullException|key|", StringComparison.Ordinal),
            "an ordinary dictionary rejects a null key with the same ArgumentNullException (param key): " + ordinary);

        var rejecting = new NullKeyKinds(null);
        var rejectingOwner = Capture(() => _ = IssuePolicy.InheritedKind(rejecting, null!));
        Check.Require(rejectingOwner == Capture(() => _ = BaselineInheritedKind(rejecting, null!)) &&
            rejectingOwner.StartsWith("System.NullReferenceException|", StringComparison.Ordinal),
            "a null-accepting dictionary that rejects the key keeps the old NullReferenceException: " + rejectingOwner);

        var hitting = new NullKeyKinds(IssueKind.Tie);
        Check.Require(IssuePolicy.InheritedKind(hitting, null!) == IssueKind.Tie &&
            BaselineInheritedKind(hitting, null!) == IssueKind.Tie,
            "a null-key hit returns the recorded kind before the key is touched");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IssueKind BaselineInheritedKind(IReadOnlyDictionary<string, IssueKind> kinds, string key)
    {
        if (kinds.TryGetValue(key, out var kind)) return kind;
        for (var boundary = key.LastIndexOf(PathNames.PathSeparator, StringComparison.Ordinal); boundary > 0;
             boundary = key.LastIndexOf(PathNames.PathSeparator, boundary - 1, StringComparison.Ordinal))
        {
            if (kinds.TryGetValue(key[..boundary], out kind)) return kind;
        }
        return IssueKind.Tie;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static string Capture(Action action)
    {
        try
        {
            action();
            return "no exception";
        }
        catch (ArgumentNullException error)
        {
            return error.GetType().FullName + "|" + error.ParamName + "|" + error.Message;
        }
        catch (Exception error)
        {
            return error.GetType().FullName + "|" + error.Message;
        }
    }

    // A null-accepting dictionary: a null key hit returns the supplied kind, otherwise the lookup returns false.
    private sealed class NullKeyKinds(IssueKind? hit) : IReadOnlyDictionary<string, IssueKind>
    {
        public IssueKind this[string key] => throw new NotSupportedException();
        public IEnumerable<string> Keys => [];
        public IEnumerable<IssueKind> Values => [];
        public int Count => 0;
        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, out IssueKind value)
        {
            value = hit ?? default;
            return key is null && hit is not null;
        }

        public IEnumerator<KeyValuePair<string, IssueKind>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, IssueKind>>().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void HeldKindPrecedence()
    {
        var kinds = new Dictionary<string, IssueKind>(StringComparer.Ordinal)
        {
            ["row"] = IssueKind.NameClash,
            ["savedlocal"] = IssueKind.Tie,
            ["saveddrop"] = IssueKind.NameClash,
            ["folder"] = IssueKind.NameClash,
            ["savedfolder"] = IssueKind.NameClash,
        };
        var synced = Synced("savedlocal", "saveddrop");

        var rowKey = new PrefixSet();
        rowKey.Add("row");
        HeldKind(rowKey, kinds, Make("row", synced), IssueKind.NameClash, "the row key wins");

        var localKey = new PrefixSet();
        localKey.Add("savedlocal");
        HeldKind(localKey, kinds, Make("row", synced), IssueKind.Tie, "the saved Local key comes second");

        var dropboxKey = new PrefixSet();
        dropboxKey.Add("saveddrop");
        HeldKind(dropboxKey, kinds, Make("row", synced), IssueKind.NameClash,
            "the saved Dropbox key comes third");

        var savedBoth = new PrefixSet();
        savedBoth.Add("savedlocal");
        savedBoth.Add("saveddrop");
        HeldKind(savedBoth, kinds, Make("row", synced), IssueKind.Tie,
            "the saved Local key beats the saved Dropbox key");

        var rowFallbackKinds = new Dictionary<string, IssueKind>(StringComparer.Ordinal)
        {
            ["saveddrop"] = IssueKind.NameClash,
        };
        var rowAndSaved = new PrefixSet();
        rowAndSaved.Add("row");
        rowAndSaved.Add("saveddrop");
        HeldKind(rowAndSaved, rowFallbackKinds, Make("row", synced), IssueKind.Tie,
            "a covered row key stops at its Tie fallback before the saved keys");

        var all = new PrefixSet();
        all.Add("row");
        all.Add("savedlocal");
        all.Add("saveddrop");
        HeldKind(all, kinds, Make("row", synced), IssueKind.NameClash, "the row key beats the saved keys");

        Check.Require(IssuePolicy.HeldKind(Make("row", synced), new PrefixSet(), kinds) is null,
            "HeldKind: a recorded kind does not hold an uncovered key");
        Check.Require(IssuePolicy.HeldKind(Make("row"), new PrefixSet(), kinds) is null,
            "HeldKind: a row with no saved entry is not held by an uncovered key");

        var renameHeld = new PrefixSet();
        renameHeld.Add("renameheld");
        HeldKind(renameHeld, kinds, Make("renameheld/child.txt"), IssueKind.Tie,
            "a held prefix with no recorded kind counts as Tie");

        var folder = new PrefixSet();
        folder.Add("folder");
        HeldKind(folder, kinds, Make("folder/child.txt"), IssueKind.NameClash, "an inherited ancestor's kind");
        HeldKind(folder, kinds, Make("folderSibling/x"), null, "a name prefix is not an ancestor boundary");

        var savedAncestor = new PrefixSet();
        savedAncestor.Add("savedfolder");
        HeldKind(savedAncestor, kinds, Make("other", Synced("savedfolder/child.txt", "saveddrop")),
            IssueKind.NameClash, "an ancestor covers the saved Local key");
        HeldKind(savedAncestor, kinds, Make("other", Synced("savedlocal", "savedfolder/child.txt")),
            IssueKind.NameClash, "an ancestor covers the saved Dropbox key");
    }

    private static void ObservedAndMappedHolds()
    {
        const string source = "Staging/Child";
        const string destination = "Current/Child";
        var held = new PrefixSet();
        held.Add(PathNames.Key("Current"));
        var kinds = new Dictionary<string, IssueKind> { [PathNames.Key("Current")] = IssueKind.NameClash };
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        foreach (var missing in new[] { false, true })
        {
            var sourceEntry = Observed(source, "source-id");
            var counterpart = missing ? null : Observed(destination, "counterpart-id");
            var row = new Row(PathNames.KeyFor(side, source),
                side == Side.Local ? sourceEntry : counterpart,
                side == Side.Dropbox ? sourceEntry : counterpart, null, [], null,
                RowContext.Neutral with { Destination = missing ? destination : null });
            HeldKind(held, kinds, row, IssueKind.NameClash,
                "the observed or missing-side mapped counterpart holds staging work: " + side + "/" + missing);
            using var facts = new InMemoryStateStore();
            facts.Commit("pair", StateChange.Empty with
            {
                Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, "Current",
                    new IssueSubject([new IssueItem(Side.Local, "Current", "folder-id")], null))]
            });
            var issue = facts.Issues("pair").Single();
            Check.Require(facts.HeldKind("pair", row, null) == IssueKind.NameClash && facts.HoldingIssue("pair", row) == issue,
                "held kind and attachment consult the same locations");
            var sibling = row with { Local = Observed("Sibling/Child", "sibling-local"),
                Dropbox = Observed("Sibling/Child", "sibling-remote"), Context = RowContext.Neutral };
            Check.Require(facts.HeldKind("pair", sibling, null) is null && facts.HoldingIssue("pair", sibling) is null,
                "an unrelated sibling is not held");
            facts.Commit("pair", StateChange.Empty with { Close = [("Current", new[] { IssueKind.NameClash })] });
            Check.Require(facts.HeldKind("pair", row, null) is null && facts.HoldingIssue("pair", row) is null,
                "explicit closure releases all observed and mapped locations");
        }
    }

    private static void HeldKind(PrefixSet heldKeys, IReadOnlyDictionary<string, IssueKind> kinds, Row row,
        IssueKind? expected, string what) =>
        Check.Require(IssuePolicy.HeldKind(row, heldKeys, kinds) == expected, "HeldKind: " + what);

    private static void ProtectedExclusion()
    {
        var (kind, cause) = IssuePolicy.ProtectedExclusion;
        Check.Require(kind == IssueKind.Failure, "ProtectedExclusion kind is Failure");
        Check.Require(cause == TextKey.FailExcludeHoldsProtected, "ProtectedExclusion cause");
        Check.Require(IssuePolicy.ProtectedExclusion == (IssueKind.Failure, TextKey.FailExcludeHoldsProtected),
            "ProtectedExclusion is stable");
    }

    private static SyncOutcome Outcome(IssueKind? raised, bool deferred, NoneReason? none = null) =>
        new(ActionKind.Notice, none, null, raised, deferred);

    private static void RequireKinds(IReadOnlyList<IssueKind> actual, string what, params IssueKind[] expected)
    {
        Check.Require(actual.SequenceEqual(expected), what + ": " + string.Join(", ", actual));
        RequireReadOnly(actual, what);
    }

    private static void RequireReadOnly<T>(IReadOnlyList<T> items, string what)
    {
        Check.Require(items is not T[], what + " does not expose its array");
        var threw = false;
        try
        {
            ((IList<T>)items).Add(default!);
        }
        catch (NotSupportedException)
        {
            threw = true;
        }
        Check.Require(threw, what + " is read-only");
    }

    private static SyncedEntry Synced(string localKey, string dropboxKey) =>
        new(localKey, localKey, dropboxKey, dropboxKey, false, "", 0, 0, "", "");

    private static Row Make(string key, SyncedEntry? synced = null) =>
        new(key, null, null, synced, [], null,
            new RowContext(false, false, false, null, false, false, false, false));
}
