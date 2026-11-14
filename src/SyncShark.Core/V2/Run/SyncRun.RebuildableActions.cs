namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private const int RebuildablePreviewSize = 50;

    // Review-metadata actions only: none of these deletes, moves, copies or excludes a file.
    private string HandleRebuildableRuleChoice(PairDef pair, Notice notice, string choice, NoticeActionInput? input,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? update)
    {
        var rule = notice.RebuildableRule
            ?? throw new SyncRunRefusedException("This notice is not a rebuildable rule. Refresh notices; nothing was changed.");
        return choice switch
        {
            NoticeChoices.RetryNow => CheckRebuildableRuleAgain(pair, notice, rule, update),
            NoticeChoices.PreviewMatches => PreviewRebuildableRuleMatches(pair, rule, input?.Offset ?? 0),
            NoticeChoices.ChooseFolder => ChooseRebuildableFolder(pair, notice, rule, input?.Name, update),
            NoticeChoices.RemoveRule => RemoveRebuildableRule(pair, notice, rule, update),
            NoticeChoices.KeepRule => KeepRebuildableRule(notice),
            _ => throw new SyncRunRefusedException(
                "Choose Check again, Preview matches, Choose folder, Remove rule or Keep. Nothing was changed.")
        };
    }

    // Check again re-runs the rule's own detection against the current inventory. It uses the same bounded
    // name query as Preview matches, then revalidates each candidate with the category's real markers, so a mere
    // name match is never reported as detected. A detected folder closes the notice; otherwise it stays open.
    private string CheckRebuildableRuleAgain(PairDef pair, Notice notice, NoticeRebuildableRule rule,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? update)
    {
        var sides = RebuildableSelection.Sides(pair) ?? throw new SyncRunRefusedException(
            "This pair has no single local side, so rebuildable rules do not apply here. Nothing was changed.");
        var local = sides.Local == Side.A ? pair.A : pair.B;
        var include = BuildPredicates(pair, PairInclude.ForAction);
        var includeLocal = sides.Local == Side.A ? include.A : include.B;
        var names = RebuildableDetector.FolderNames(rule.Category);
        // One bounded page is enough: any detected folder means the rule now matches.
        var candidates = _store.RebuildableFoldersUnder(local.RootId, rule.Prefix, names, 0, RebuildablePreviewSize + 1)
            .Where(includeLocal);
        var readText = RebuildableDetector.DiskReader(local.RootPath);
        foreach (var candidate in candidates)
        {
            var folder = _store.Live(local.RootId, [candidate])
                .FirstOrDefault(item => item.Kind == ItemKind.Folder);
            if (folder is null) continue;
            // The marker checks read the candidate's own children and its siblings, exactly like a prepare scan.
            var items = _store.LiveChildren(local.RootId, candidate)
                .Concat(_store.LiveChildren(local.RootId, RowCodec.Split(candidate).Parent))
                .Append(folder)
                .DistinctBy(item => item.Path, StringComparer.Ordinal)
                .ToArray();
            if (RebuildableDetector.Detect(items, readText).Any(detected =>
                    PairPath.Same(detected.Path, candidate) &&
                    string.Equals(detected.Category, rule.Category, StringComparison.OrdinalIgnoreCase)))
            {
                // Same guard as Choose folder: the retained review was frozen under the old prefix and does not
                // contain this folder, so an apply must wait for a fresh prepare that detects it (C1).
                update?.Invoke(pair, new RebuildableChoice(rule.Category, rule.Prefix),
                    new RebuildableChoice(rule.Category, rule.Prefix));
                _store.CloseNotice(notice.NoticeId!, NoticeChoices.RetryNow);
                return $"Found a '{rule.Category}' folder under '{rule.Prefix}/'. Run prepare again to include it " +
                    "in the first-sync review; no files were changed.";
            }
        }
        return $"Still no '{rule.Category}' folders under '{rule.Prefix}/'. Run prepare again after the inventory " +
            "changes; the notice stays open and no files were changed.";
    }

    // A bounded SQL page of candidate folder paths: LIMIT is applied in the database, never over a loaded subtree.
    private string PreviewRebuildableRuleMatches(PairDef pair, NoticeRebuildableRule rule, int offset)
    {
        if (offset < 0 || offset > int.MaxValue - RebuildablePreviewSize - 1)
            throw new SyncRunRefusedException("Invalid page offset.");
        var sides = RebuildableSelection.Sides(pair) ?? throw new SyncRunRefusedException(
            "This pair has no single local side, so rebuildable rules do not apply here. Nothing was changed.");
        var local = sides.Local == Side.A ? pair.A : pair.B;
        var include = BuildPredicates(pair, PairInclude.ForAction);
        var includeLocal = sides.Local == Side.A ? include.A : include.B;
        var names = RebuildableDetector.FolderNames(rule.Category);
        // One extra row tells the caller there is a next page without a second count query.
        var found = _store.RebuildableFoldersUnder(local.RootId, rule.Prefix, names, offset, RebuildablePreviewSize + 1);
        // The extra row is a raw-row signal; excluded rows must not hide that a further page exists.
        var more = found.Count > RebuildablePreviewSize;
        var candidates = found.Where(includeLocal).ToArray();
        var paths = candidates.Take(RebuildablePreviewSize).ToArray();
        var lines = new List<string>
        {
            $"Rule '{rule.Category}' under '{rule.Prefix}/': candidate folder paths under the current inventory" +
                (offset == 0 ? " (up to 50)" : $" (offset {offset}, up to 50)") + ":"
        };
        if (paths.Length == 0) lines.Add("  (none under this prefix)");
        else lines.AddRange(paths.Select(path => "  " + path));
        if (more) lines.Add($"More candidates: next offset {offset + RebuildablePreviewSize}.");
        lines.Add("No files were changed or deleted; prepare still applies each tool's own marker check.");
        return string.Join(Environment.NewLine, lines);
    }

    private string ChooseRebuildableFolder(PairDef pair, Notice notice, NoticeRebuildableRule rule, string? folder,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? update)
    {
        if (update is null)
            throw new SyncRunRefusedException(
                "This host cannot change the retained first-sync rule. Run prepare again with the corrected choices; nothing was changed.");
        var chosen = NormalizeRebuildableFolder(folder);
        var sides = RebuildableSelection.Sides(pair) ?? throw new SyncRunRefusedException(
            "This pair has no single local side, so rebuildable rules do not apply here. Nothing was changed.");
        var local = sides.Local == Side.A ? pair.A : pair.B;
        var live = _store.Live(local.RootId, [chosen])
            .FirstOrDefault(item => item.Kind == ItemKind.Folder && PairPath.Same(item.Path, chosen));
        if (live is null)
            throw new SyncRunRefusedException(
                "That folder is not in this pair's current inventory. Run prepare again or choose a folder inside the pair; nothing was changed.");
        var include = BuildPredicates(pair, PairInclude.ForAction);
        if (!(sides.Local == Side.A ? include.A : include.B)(live.Path))
            throw new SyncRunRefusedException(
                "That folder is excluded, protected or belongs to a nested pair. Review folder settings first; nothing was changed.");
        var message = update(pair, new RebuildableChoice(rule.Category, rule.Prefix), new RebuildableChoice(rule.Category, live.Path));
        _store.CloseNotice(notice.NoticeId!, NoticeChoices.ChooseFolder);
        return message;
    }

    private string RemoveRebuildableRule(PairDef pair, Notice notice, NoticeRebuildableRule rule,
        Func<PairDef, RebuildableChoice, RebuildableChoice?, string>? update)
    {
        if (update is null)
            throw new SyncRunRefusedException(
                "This host cannot change the retained first-sync rule. Run prepare again without this rule; nothing was changed.");
        var message = update(pair, new RebuildableChoice(rule.Category, rule.Prefix), null);
        _store.CloseNotice(notice.NoticeId!, NoticeChoices.RemoveRule);
        return message;
    }

    private string KeepRebuildableRule(Notice notice)
    {
        _store.CloseNotice(notice.NoticeId!, NoticeChoices.KeepRule);
        return NoticeChoices.Result(NoticeChoices.KeepRule);
    }

    private static string NormalizeRebuildableFolder(string? folder)
    {
        var value = folder?.Trim() ?? string.Empty;
        value = value.TrimEnd('/');
        if (value.Length == 0 || value.StartsWith('/') || value.Contains('\\') ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new SyncRunRefusedException(
                "Enter one folder path inside this pair, without a leading slash, backslash, '.' or '..'. Nothing was changed.");
        return value;
    }
}
