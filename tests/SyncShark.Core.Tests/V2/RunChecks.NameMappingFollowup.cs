namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    public static Task MappingPreparedMatchExclusionAsync()
    {
        var failures = new List<string>();
        foreach (var cloudOnA in new[] { false, true })
        foreach (var excluded in new[] { "X_", "X:" })
        {
            var (store, original) = MappingStore("prepared-match-exclusion");
            using (store)
            {
                var pair = cloudOnA ? original with { A = original.B, B = original.A } : original;
                store.SavePair(pair);
                var local = MappingItem("X_", "local");
                var cloud = MappingItem("X:", "cloud");
                var a = cloudOnA ? cloud : local;
                var b = cloudOnA ? local : cloud;
                MappingSnapshot(store, pair.A, Side.A, a);
                MappingSnapshot(store, pair.B, Side.B, b);
                var scope = new RunScope(store, path => path != excluded, path => path != excluded).LoadFull(pair);
                var label = $"prepared-match-exclusion {cloudOnA}/{excluded}";
                Require(scope.Baselines.Count == 0 && scope.A.Concat(scope.B).All(item =>
                    item.Flags.HasFlag(ItemFlags.Ignored) == (item.Path == excluded)),
                    label + ": only the excluded actual path is ignored without a baseline");
                var rows = Planner.Join(scope.Baselines, scope.A, scope.B,
                    new Dictionary<string, DateTimeOffset?>(), new Dictionary<string, DateTimeOffset?>(),
                    new(true, true, new HashSet<string>(), new HashSet<string>()), false);
                var match = new PrepareMatch(Content.SideStamp(a), Content.SideStamp(b));
                var plan = Planner.Decide(pair.PairId, 1, rows, new(TimeSpan.Zero, true,
                    new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10)
                {
                    // Production persists MatchedPair(row.A.Path, ...): exactly one A-side key.
                    MatchedAtPrepare = new Dictionary<string, PrepareMatch>(StringComparer.OrdinalIgnoreCase)
                    { [a.Path] = match }
                });
                if (plan.Actions.Any(action => action.Kind == ActionKind.Delete) ||
                    plan.Actions.Count == 0 || plan.Actions.Any(action => action.Kind != ActionKind.Hold) ||
                    !plan.Notices.Any(notice => notice.ReasonKey == "name-mapping-collision"))
                    failures.Add(label + ": an ignored mapped occupant must hold the stale prepared match, never rule 6 deletion; " +
                        string.Join(", ", plan.Actions.Select(action => $"{action.Kind} {action.Target}:{action.Path} rule={action.Rule}")));
            }
        }
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
        return Task.CompletedTask;
    }

    public static Task MappingLinkedExclusionTripleAsync()
    {
        foreach (var cloudOnA in new[] { false, true })
        foreach (var link in new[] { "none", "literal", "mapped" })
        foreach (var excluded in new[] { "local", "literal", "mapped", "none" })
        foreach (var touched in new[] { false, true })
        {
            var (store, original) = MappingStore("exclusion-triple");
            using (store)
            {
                var pair = cloudOnA ? original with { A = original.B, B = original.A } : original;
                store.SavePair(pair);
                var local = MappingItem("X_", "local");
                var literal = MappingItem("X_", "literal");
                var mapped = MappingItem("X:", "mapped");
                var localSide = cloudOnA ? Side.B : Side.A;
                var cloudSide = cloudOnA ? Side.A : Side.B;
                MappingSnapshot(store, cloudOnA ? pair.B : pair.A, localSide, local);
                MappingSnapshot(store, cloudOnA ? pair.A : pair.B, cloudSide, literal, mapped);
                if (link != "none")
                {
                    var cloud = link == "literal" ? literal : mapped;
                    var adopt = new SyncAction("triple-link", 3, ActionKind.Adopt, cloudSide, cloud.Path,
                        null, local, cloud, false, "triple-link");
                    store.RecordIntent(pair.PairId, adopt);
                    store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done,
                        cloudOnA ? cloud : local, cloudOnA ? local : cloud, null, null));
                }
                bool LocalIncluded(string path) => excluded != "local" || path != "X_";
                bool CloudIncluded(string path) => path != (excluded == "literal" ? "X_" : excluded == "mapped" ? "X:" : "");
                var loader = new RunScope(store, cloudOnA ? CloudIncluded : LocalIncluded,
                    cloudOnA ? LocalIncluded : CloudIncluded);
                var scope = touched ? loader.LoadTouched(pair, ["X_"], [], []) : loader.LoadFull(pair);
                var localRow = (cloudOnA ? scope.B : scope.A).Single();
                var cloudRows = cloudOnA ? scope.A : scope.B;
                var label = $"triple {cloudOnA}/{link}/{excluded}/{touched}";
                Require(localRow.Flags.HasFlag(ItemFlags.Ignored) == (excluded == "local" || link != "none" && excluded == link),
                    label + ": only its own path or its identity-linked cloud entity suppresses local X_");
                Require(cloudRows.Single(i => i.ObjectId == "literal").Flags.HasFlag(ItemFlags.Ignored) ==
                    (excluded == "literal" || excluded == "local" && link == "literal"),
                    label + ": literal cloud X_ follows only its own path and its linked counterpart");
                Require(cloudRows.Single(i => i.ObjectId == "mapped").Flags.HasFlag(ItemFlags.Ignored) ==
                    (excluded == "mapped" || excluded == "local" && link == "mapped"),
                    label + ": mapped cloud X: is never suppressed by the separate literal X_ spelling");
                var plan = MappingPlan(scope);
                var actions = string.Join(", ", plan.Actions.Select(a => $"{a.Kind} {a.Target}:{a.Path} rule={a.Rule}"));
                Require(plan.Actions.All(a => (a.Path is "X_" or "X:") && a.Kind != ActionKind.Delete),
                    label + ": never manufacture labelled copies or delete a colliding entity; " + actions);
                var mappingHold = plan.Actions.Count > 0 && plan.Actions.All(a => a.Kind == ActionKind.Hold) &&
                    plan.Notices.Any(n => n.ReasonKey == "name-mapping-collision");
                bool Adopts(string cloudId) => plan.Actions.Count == 1 && plan.Actions[0].Kind == ActionKind.Adopt &&
                    new[] { plan.Actions[0].Source?.ObjectId, plan.Actions[0].ExpectedTarget?.ObjectId }
                        .Order().SequenceEqual(new[] { cloudId, "local" }.Order());
                var quiet = plan.Actions.Count == 0 && scope.Baselines.Count == 1;
                var expected = (excluded, link) switch
                {
                    ("none", _) or ("local", _) => mappingHold,         // ambiguity, or active cloud beside an excluded local
                    ("literal", "none") => Adopts("mapped"),             // decision 82c: unlinked literal left candidacy
                    ("mapped", "none") => Adopts("literal"),             // unique literal X_ pair, no baseline
                    ("literal", "literal") or ("mapped", "mapped") => mappingHold, // orphan beside excluded linked pair
                    _ => quiet,                                          // excluded unlinked entity; linked pair unchanged
                };
                Require(expected, label + ": ordinary rules plus decision 82 ambiguity, never an unwanted copy; " + actions);
            }
        }
        return Task.CompletedTask;
    }

    private static Task MappingEmptyFolderAsync()
    {
        foreach (var cloudOnA in new[] { false, true })
        {
            var (store, originalPair) = MappingStore("empty-folder");
            using (store)
            {
                var pair = cloudOnA ? originalPair with
                {
                    A = originalPair.A with { Provider = SyncProviders.Dropbox },
                    B = originalPair.B with { Provider = SyncProviders.Local }
                } : originalPair;
                store.SavePair(pair);
                var local = MappingItem("a_b", "local-folder", ItemKind.Folder);
                var cloud = MappingItem("a:b", "cloud-folder", ItemKind.Folder);
                var child = MappingItem("a_b/new.txt", "new-child");
                var a = cloudOnA ? cloud : local;
                var b = cloudOnA ? local : cloud;
                MappingSnapshot(store, pair.A, Side.A, cloudOnA ? [cloud] : [local, child]);
                MappingSnapshot(store, pair.B, Side.B, cloudOnA ? [local, child] : [cloud]);
                var adopt = new SyncAction("empty-link", 3, ActionKind.Adopt, Side.B, b.Path, null, a, b, false, "folder-link");
                store.RecordIntent(pair.PairId, adopt);
                store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, a, b, null, null));
                var cloudRoot = cloudOnA ? pair.A.RootId : pair.B.RootId;
                Require(store.DirectorySpellings(cloudRoot, "a_b").Count == 0,
                    "empty-folder fixture has no cloud child directory to resolve");
                var recorder = new RecordingStore(store);
                var scope = new RunScope(recorder, _ => true, _ => true).LoadTouched(pair, [child.Path], [], []);
                Require(scope.A.Count + scope.B.Count == 1 && scope.Baselines.Count == 0 && recorder.ChildDirectories.Count == 0,
                    "empty ancestor stays context-only, with no sibling enumeration or ancestor planner rows");
                var plan = MappingPlan(scope);
                Require(plan.Actions.Count == 1 && plan.Actions[0].Kind == ActionKind.CopyNew &&
                    plan.Actions[0].Target == (cloudOnA ? Side.A : Side.B) && plan.Actions[0].Path == "a:b/new.txt",
                    "new child under an empty linked mapped folder uploads into the original colon folder in both orientations");
            }
        }
        return Task.CompletedTask;
    }

    private static Task MappingAtomicSaveAsync()
    {
        foreach (var cloudOnA in new[] { false, true })
        {
            var local = MappingItem("a_b", "old-local-id");
            var cloud = DropboxNameMapping.FromStoredDropbox(MappingItem("a:b", "cloud-id"));
            var saved = local with { ObjectId = "replacement-local-id", VersionId = "v2", Hash = "22" };
            var basis = new Baseline("mapping-review", "linked", cloudOnA ? cloud : local, cloudOnA ? local : cloud, 1);
            var scope = new RunScope.Scope(cloudOnA ? [cloud] : [saved], cloudOnA ? [saved] : [cloud], [basis]);
            var plan = MappingPlan(scope);
            Require(plan.Actions.Count == 1 && plan.Actions[0].Kind == ActionKind.Update &&
                plan.Actions[0].Target == (cloudOnA ? Side.A : Side.B) && plan.Actions[0].Path == "a:b" &&
                !plan.Notices.Any(n => n.ReasonKey == "name-mapping-collision"),
                "atomic-save replacement id remains the claimed linked entity and updates the original colon path");
        }
        return Task.CompletedTask;
    }

    private static Task MappingCachedBlocksAsync()
    {
        var blocked = MappingItem("export:only", "export") with
        { Hash = null, Flags = ItemFlags.Unreadable | ItemFlags.Locked | ItemFlags.ProviderNameRefused };
        var mapped = DropboxNameMapping.FromStoredDropbox(blocked);
        Require(mapped.IdentityJson is not null && mapped.Flags == blocked.Flags,
            "legacy export-only rows retain every block despite a transferable mapped spelling");
        var downloadable = DropboxNameMapping.FromStoredDropbox(blocked with { Hash = "11" });
        Require(downloadable.Flags == (ItemFlags.Locked | ItemFlags.ProviderNameRefused),
            "clearing a proven legacy filename block never clears independent block bits");
        var folder = DropboxNameMapping.FromStoredDropbox(blocked with { Kind = ItemKind.Folder });
        Require(folder.Flags == (ItemFlags.Locked | ItemFlags.ProviderNameRefused),
            "folders need no file hash to clear a legacy filename block");
        return Task.CompletedTask;
    }
}
