using System.Globalization;
using System.Text;

namespace Massdex.SyncShark;

// The single command registry. Help, parsing, validation, the capability inventory and the dispatcher
// all read this table, so the direct path, the running-application path and the published feature map
// cannot drift apart.
public sealed record SyncCliCommandRow(string Command, string Usage, SyncCliRequestKind Kind, string Approval,
    string Effect, string Requires, string Mode, string Options, bool NeedsApplication = false,
    bool LiveVerificationRequired = false, bool Mutating = false, bool RequiresYes = false,
    bool RequiresPair = false, string? MutationOption = null);

public static class SyncCliRegistry
{
    // Keep stdout's single JSON envelope unchanged. Human progress on stderr
    // carries an unambiguous UTC event time, including across the app bridge.
    internal static string FormatProgress(string message, DateTimeOffset? occurredUtc = null) =>
        "Sync Shark: [" + (occurredUtc ?? DateTimeOffset.UtcNow).ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture) + "] " + message;

    private const string PairOnly = "--pair";
    private const string None = "";

    private static readonly string[] ValueOptions =
    [
        "--pair", "--account", "--path", "--key", "--search", "--choice", "--rules", "--settings", "--update",
        "--page-token", "--limit", "--offset", "--generation", "--minutes", "--selection", "--availability", "--local",
        "--until", "--timeout-seconds",
        "--name", "--priority", "--provider"
        , "--description", "--choices", "--group", "--filter", "--seconds", "--side",
        "--categories", "--severity", "--entity", "--run", "--order", "--type", "--since", "--after", "--before",
        "--id", "--path-prefix", "--action-id",
        "--source", "--destination", "--bytes", "--sha256", "--local-root", "--source-sha256", "--draft",
        "--keep", "--token", "--archive", "--local-date", "--from", "--to", "--exclude-list", "--audit-log", "--provenance-log", "--provenance-prefix",
        RepeatableExclude,
        ..DropboxCliCatalog.ValueOptions
    ];

    // The one value option that may be given more than once; every value is kept, in order.
    private const string RepeatableExclude = "--exclude";

    public static readonly IReadOnlyList<SyncCliCommandRow> Rows =
    [
        new("help", "help", SyncCliRequestKind.Help, "none", "prints this command map",
            "nothing", "state-free", None),
        new("version", "version", SyncCliRequestKind.Version, "none", "prints the product and bound source revision",
            "nothing", "state-free", None),
        new("capabilities", "capabilities", SyncCliRequestKind.Capabilities, "none",
            "prints the machine-readable capability inventory", "nothing", "state-free", None),
        new("autosync start", "autosync start", SyncCliRequestKind.State, "explicit", "start or attach to the shared background host", "installed executable", "background-host", None, Mutating: true),
        new("autosync install-startup", "autosync install-startup [--this-build]", SyncCliRequestKind.State, "explicit", "register stable installed logon startup; replace stale or legacy entries with a warning", "installed executable", "background-host", "--this-build", Mutating: true),
        new("autosync uninstall-startup", "autosync uninstall-startup", SyncCliRequestKind.State, "explicit", "remove per-user logon startup; running host unchanged", "installed executable", "background-host", None, Mutating: true),
        new("autosync stop", "autosync stop", SyncCliRequestKind.State, "explicit", "stop at a durable safe point", "running host", "background-host", None, Mutating: true),
        new("autosync status", "autosync status", SyncCliRequestKind.State, "none", "host identity and pair scheduler state; never launches", "nothing", "background-host", None),
        new("autosync enable", "autosync enable --pair <id>|--all", SyncCliRequestKind.State, "explicit", "enable a pair or automatic syncing globally", "running host", "background-host", "--pair,--all", Mutating: true),
        new("autosync disable", "autosync disable --pair <id>|--all", SyncCliRequestKind.State, "explicit", "disable a pair or automatic syncing globally", "running host", "background-host", "--pair,--all", Mutating: true),
        new("autosync interval", "autosync interval --minutes <1..1440>", SyncCliRequestKind.State, "explicit", "set the safety interval", "running host", "background-host", "--minutes", Mutating: true),
        new("autosync history", "autosync history --pair <id> [--limit <1..200>] [--after <cursor>]", SyncCliRequestKind.State, "none", "bounded persisted run summaries", "running host", "background-host", "--pair,--limit,--after", RequiresPair: true),
        new("autosync now", "autosync now --pair <id>", SyncCliRequestKind.State, "explicit", "signal one pair; active work is not replayed", "running host", "background-host", "--pair", Mutating: true, RequiresPair: true),
        new("autosync logs", "autosync logs [--follow] [--limit <1..200>]", SyncCliRequestKind.State, "none", "bounded run summary log; follow polls without launching", "running host", "background-host", "--follow,--limit"),
        new("status", "status", SyncCliRequestKind.State, "none",
            "product, accounts, pairs, pending work, run state and last success", "the product state or the running application",
            "direct|running-application", None),
        new("accounts list", "accounts list", SyncCliRequestKind.State, "none",
            "retained accounts with provider, identity, pause state and pair counts",
            "the product state or the running application", "direct|running-application", None),
        new("accounts show", "accounts show --account <exact-id>", SyncCliRequestKind.State, "none",
            "one account's namespaces, scopes, capability flags and pause state",
            "the product state or the running application", "direct|running-application", "--account"),
        new("accounts check", "accounts check [--account <exact-id>]", SyncCliRequestKind.State, "none",
            "live provider identity and credential health for one account",
            "the product state or the running application and the Dropbox provider",
            "direct|running-application", "--account", LiveVerificationRequired: true, Mutating: false),
        new("accounts shared-drives", "accounts shared-drives --account <exact-google-id> --enable|--disable --yes",
            SyncCliRequestKind.State, "yes", "enable or disable shared-drive access for one Google account; no sync or provider permission change",
            "the product state or the running application", "direct|running-application", "--account,--enable,--disable,--yes",
            Mutating: true, RequiresYes: true),
        new("accounts connect", "accounts connect [--provider dropbox|google-drive] --yes", SyncCliRequestKind.State,
            "yes",
            "opens the selected provider's registered authorization page, waits for its loopback callback and " +
            "stores the verified connection and its protected credential for this Windows user",
            "the product state or the running application and browser consent",
            "direct|running-application",
            "--provider,--yes", LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("accounts reauthorize", "accounts reauthorize --account <exact-id> --yes", SyncCliRequestKind.State,
            "yes",
            "opens the exact account's provider authorization page, waits for its loopback callback and renews " +
            "the verified connection and its protected credential for the same provider account and namespace",
            "the product state or the running application and browser consent",
            "direct|running-application",
            "--account,--yes", LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("accounts disconnect", "accounts disconnect --account <exact-id> --yes", SyncCliRequestKind.State,
            "yes", "removes the protected credential and keeps files, definitions and history",
            "the product state or the running application", "direct|running-application", "--account,--yes",
            Mutating: true, RequiresYes: true),
        new("accounts pending", "accounts pending", SyncCliRequestKind.State, "none",
            "the one interactive provider authorization attempt waiting in the running application, with its " +
            "ephemeral attempt id, provider, connect or reauthorize action, existing account and phase",
            "the running application", "running-application", None, NeedsApplication: true),
        new("accounts cancel", "accounts cancel --key <attempt-id> --yes", SyncCliRequestKind.State, "yes",
            "requests cancellation of exactly that waiting authorization attempt and reports its terminal result",
            "the running application", "running-application", "--key,--yes", NeedsApplication: true,
            Mutating: true, RequiresYes: true),
        new("pairs list", "pairs list", SyncCliRequestKind.State, "none",
            "retained pairs with roots, routing, enablement and review state",
            "the product state or the running application", "direct|running-application", None),
        new("pairs show", "pairs show --pair <exact-id>", SyncCliRequestKind.State, "none",
            "one pair's definition, policy revision and rule counts",
            "the product state or the running application", "direct|running-application", PairOnly,
            RequiresPair: true),
        new("pairs pause", "pairs pause --pair <exact-id>", SyncCliRequestKind.State, "none",
            "the pair checkpoints and pauses without changing files or enablement", "the core v2 product state; works while a run is active",
            "direct", "--pair", Mutating: true, RequiresPair: true),
        new("pairs resume", "pairs resume --pair <exact-id>", SyncCliRequestKind.State, "none",
            "the next run continues the pair from its checkpoints", "the core v2 product state; works while the app is open",
            "direct", "--pair", Mutating: true, RequiresPair: true),
        new("pairs enable", "pairs enable --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "the pair participates in automatic and manual runs again", "the product state or the running application",
            "direct|running-application", "--pair,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs disable", "pairs disable --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "the pair is paused; no local or remote content is changed", "the product state or the running application",
            "direct|running-application", "--pair,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs select", "pairs select --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "the pair becomes the retained selected pair", "the product state or the running application",
            "direct|running-application", "--pair,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs retire", "pairs retire --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "the pair definition is retired under the existing retention rules",
            "the product state or the running application", "direct|running-application", "--pair,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs remove", "pairs remove --pair <exact-id> --archive <directory> [--drop-unfinished] --yes", SyncCliRequestKind.State,
            "yes", "archives every pair-scoped row into a new verified database and manifest under the directory, " +
            "then removes the exact disabled pair; unfinished effects require --drop-unfinished; only enclosing " +
            "remaining pairs' prepares are cleared; root inventories and recovery payload files are retained and " +
            "no local or remote file is touched",
            "the product state and the exclusive product lock", "direct", "--pair,--archive,--drop-unfinished,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("dates repair", "dates repair --pair <exact-id> [--dry-run] [--yes] [--local-date yyyy-MM-dd | --from <local-date-or-time> --to <local-date-or-time>] [--exclude-list <file>] [--after <key>] [--provenance-log <file> ...] [--provenance-prefix <relative-prefix> ...]", SyncCliRequestKind.State, "yes",
            "repairs newer local download timestamps only after verifying identical baseline and Dropbox content; " +
            "logs every date change, never changes content or uploads; --dry-run reports without changing dates; " +
            "--provenance-log is repeatable; --provenance-prefix maps log paths under a relative pair folder (default .); " +
            "give one prefix for all logs or one per log in matching order",
            "the v2 store and exclusive product/run locks", "direct", "--pair,--dry-run,--yes,--local-date,--from,--to,--exclude-list,--after,--provenance-log,--provenance-prefix",
            Mutating: true, RequiresPair: true),
        new("dates restore", "dates restore --audit-log <file> [--dry-run] [--yes]", SyncCliRequestKind.State, "yes",
            "restores audited original dates only while repaired date, file identity and content still match",
            "the v2 store and exclusive product/run locks", "direct", "--audit-log,--dry-run,--yes", Mutating: true),
        new("dates report-mismatch", "dates report-mismatch --pair <exact-id> [--local-date yyyy-MM-dd | --from <local-date-or-time> --to <local-date-or-time>] [--exclude-list <file>] [--after <key>] [--provenance-log <file> ...] [--provenance-prefix <relative-prefix> ...]",
            SyncCliRequestKind.State, "none", "read-only date mismatch report; writes a CSV path list, never changes file dates or content; missing write provenance is unknown, not proof of a user edit",
            "the v2 store and exclusive product/run locks", "direct", "--pair,--local-date,--from,--to,--exclude-list,--after,--provenance-log,--provenance-prefix", RequiresPair: true),
        new("pairs rescan", "pairs rescan --pair <exact-id> [--side a|b|both] --yes", SyncCliRequestKind.State, "yes",
            "forgets the pair's stored scan cursors for the chosen side(s) so its next run re-reads those roots in " +
            "full; baselines, live rows and tombstones are untouched and no file is changed",
            "the product state and the exclusive product lock", "direct", "--pair,--side,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs recover-control",
            "pairs recover-control --pair <exact-id> --source <original-db-path> --source-sha256 <64-hex-lowercase> --local-root <held-local-root> [--draft <current-pair-json>] --yes",
            SyncCliRequestKind.State, "yes",
            "installs the retained original-control ownership metadata from the one pinned original authority database for the exact retained pair (cloud/cloud or local/cloud in either placement), optionally supplied as --draft for an unpublished initial candidate whose revision is validated as CAS at the recovery boundary, then publishes through the same guarded authoring path; no old database or policy is imported and the pair stays disabled for fresh first-sync review",
            "the running application or the exclusive product lock and the exact held local root",
            "direct|running-application", "--pair,--source,--source-sha256,--local-root,--draft,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs recover-publication",
            "pairs recover-publication --pair <exact-id> --yes",
            SyncCliRequestKind.State, "yes",
            "restores the last coherent PUBLISHED revision of an INTERRUPTED same-root publication for the exact pair - the narrow guarded recovery that refuses a coherent pair, an in-use orphan revision or any changed own-root identity, and rewrites no definition, policy or root binding",
            "the running application or the exclusive product lock",
            "direct|running-application", "--pair,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("pairs folders", "pairs folders --account <exact-id> [--path <remote-path>] [--search <text>]",
            SyncCliRequestKind.State, "none",
            "browses the account's remote folders through the app's own provider browser",
            "the running application or the exclusive product lock and the Dropbox provider",
            "direct|running-application", "--account,--path,--search", LiveVerificationRequired: true),
        new("pairs create", "pairs create --settings <endpoint-json> [--name <name>] [--priority <n>] [--enable] --yes | pairs create --account <exact-id> --path <remote-path> --local <local-root> [--name <name>] [--priority <n>] [--enable] [--exclude <pair-relative-path>]... --yes",
            SyncCliRequestKind.State, "yes",
            "binds ordered local or cloud endpoints through the shared app boundary; a new cloud/cloud pair stays disabled until first-sync review; <endpoint-json> is {\"left\":{...},\"right\":{...},\"direction\":TwoWay|UploadOnly|DownloadOnly}; each --exclude (core v2 only) keeps one pair-relative path out of sync on both sides",
            "the running application or the exclusive product lock, each cloud endpoint's exact account and both folders",
            "direct|running-application", "--settings,--account,--path,--local,--name,--priority,--enable,--exclude,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("pairs edit", "pairs edit --pair <exact-id> --settings <endpoint-json> [--name <name>] [--priority -100..100] [--enable|--disable] [--generation <policy-revision>] --yes | pairs edit --pair <exact-id> [--name <name>] [--priority -100..100] [--enable|--disable] [--account <exact-id> --generation <policy-revision> --settings <policy-json> --local <root> --path <remote-root>] --yes",
            SyncCliRequestKind.State, "yes",
            "edits under the app's shared validation; an endpoint document binds both ordered endpoints with revision checks; local/account root replacement publishes atomically after guarded private staging, preserves the old pair on refusal and requires first-sync review again; changed cloud/cloud identity also requires fresh review",
            "the product state or the running application", "direct|running-application",
            "--pair,--name,--priority,--enable,--disable,--account,--generation,--settings,--local,--path,--yes", Mutating: true, RequiresYes: true,
            RequiresPair: true),
        new("pairs duplicate", "pairs duplicate --pair <source-id> --account <exact-id> --generation <source-policy-revision> --name <name> --local <root> --path <remote-root> [--settings <policy-json>] [--enable|--disable] --yes",
            SyncCliRequestKind.State, "yes", "copies the exact source policy and rebinds different endpoints through binding admission; an optional --settings policy document overrides the copied direction, deletion, retention, storage, selection and ignore policy",
            "Windows folder identity and the account provider", "direct|running-application",
            "--pair,--account,--generation,--name,--local,--path,--settings,--enable,--disable,--yes", LiveVerificationRequired: true,
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("activity list", "activity list [--log] [--after <retained-sequence>] [--before <retained-sequence> --order newest] [--account <exact-id>] [--pair <exact-id>] [--categories <comma-separated categories>] [--severity Information|Success|Warning|Error] [--entity <type>] [--run <exact-id>] [--order oldest|newest] [--search <text>] [--filter <action>] [--type <format>] [--since <UTC-or-offset-time>] [--limit 1..200] [--offset <n>]",
            SyncCliRequestKind.State, "none", "retained activity rows and action-journal counts/bytes, incomplete accounting and warning status; --log --order newest opens the current tail, --before pages earlier history without starting sync",
            "the product state or the running application", "direct|running-application",
            "--log,--after,--before,--account,--pair,--categories,--severity,--entity,--run,--order,--search,--filter,--type,--since,--limit,--offset"),
        new("activity follow", "activity follow --seconds <1..60> [--after <retained-sequence>] [--run <exact-id>] [--pair <exact-id>] [--account <exact-id>]",
            SyncCliRequestKind.State, "none", "streams detailed retained events as they arrive with a resumable sequence cursor; never starts sync",
            "the product state or the running application", "direct|running-application", "--seconds,--after,--run,--pair,--account"),
        new("queue list", "queue list [--account <exact-id>] [--pair <exact-id>] [--limit 1..200] [--offset <n>]",
            SyncCliRequestKind.State, "none", "current pairs' retained pending operations; retired-pair history stays in Activity",
            "the product state or the running application", "direct|running-application",
            "--account,--pair,--limit,--offset"),
        new("attention list", "attention list", SyncCliRequestKind.State, "none",
            "current pairs needing attention and current pending count, retained first-sync/command failure causes and exact run IDs, monitor states and current coalesced dirty-item counts",
            "the product state or the running application", "direct|running-application", None),
        // DIAG354-4: one read command over the same shared in-app diagnostic action the Diagnostics page
        // uses. It runs inside the running application (NeedsApplication), needs no --yes, and returns
        // only the redacted report and safe structured outcomes.
        new("diagnostics run", "diagnostics run", SyncCliRequestKind.State, "none",
            "runs the same shared read-only diagnostic action as the Diagnostics page and returns its " +
            "structured outcomes and the identical redacted support report without showing or changing " +
            "anything", "the running application", "running-application", None, NeedsApplication: true),
        new("runs list", "runs list [--limit 1..200]", SyncCliRequestKind.State, "none",
            "retained run and command status history without raw request payloads",
            "the product state or the running application", "direct|running-application", "--limit"),
        new("jobs status", "jobs status", SyncCliRequestKind.State, "none", "exact current host job identity, progress and aggregate counters; byte-bounded active-file sample with explicit total/returned/has-more",
            "the running application", "running-application", None, NeedsApplication: true),
        new("jobs watch", "jobs watch --key <active-job-id> --seconds <1..60>", SyncCliRequestKind.State, "none",
            "waits for a change or completion of exactly the named job, within the stated bound; same complete counters and explicitly bounded active-file sample as jobs status", "the running application",
            "running-application", "--key,--seconds", NeedsApplication: true),
        new("conflicts list", "conflicts list [--pair <exact-id>] [--account <exact-id>] [--search <text>] [--group <group>] [--limit 1..200] [--offset <n>]",
            SyncCliRequestKind.State, "none", "open conflicts with group, recommendation and generation",
            "the product state or the running application", "direct|running-application",
            "--pair,--account,--search,--group,--limit,--offset"),
        new("conflicts select", "conflicts select --account <exact-id> [--pair <exact-id>] [--search <text>] [--group <group>]",
            SyncCliRequestKind.State, "none", "captures every exact matching conflict ID and generation", "retained authority",
            "direct|running-application", "--account,--pair,--search,--group"),
        new("conflicts resolve-selection", "conflicts resolve-selection --account <exact-id> --choice <KeepLocal|KeepDropbox|PreserveBoth> --choices <snapshot-json-file> --yes [--timeout-seconds <seconds>]",
            SyncCliRequestKind.State, "yes", "resolves the explicit captured targets with per-target stale guards",
            "retained snapshot and the account provider", "direct|running-application", "--account,--choice,--choices,--yes,--timeout-seconds",
            Mutating: true, RequiresYes: true, LiveVerificationRequired: true),
        new("conflicts show", "conflicts show --pair <exact-id> --path <relative-path> [--account <exact-id>]",
            SyncCliRequestKind.State, "none", "one conflict's identities, sizes, hashes and retained-text flags",
            "the product state or the running application", "direct|running-application",
            "--pair,--path,--account", RequiresPair: true),
        new("conflicts resolve", "conflicts resolve --pair <exact-id> [--path <relative-path-or-volume>] --choice <decision|restore-existing|restore-as-copy|dismiss|enlarge-journal|journal-not-now|journal-never> --key <conflict-id> --generation <n> --yes [--name <safe-name|confirmed-target|link-page-cursor|folder-path|remaining-page-cursor>] [--offset <n>] [--page-token <token>] [--timeout-seconds <seconds>]",
            SyncCliRequestKind.State, "yes", "conflict notices support compare, merge=<relative-path>, keep-a, keep-b, keep-both, rename-incoming=<relative-path>, replace-existing, restore-existing, restore-as-copy and dismiss; removal notices support review-removal, removal-next-page, removal-previous-page and confirm-removal (generation 0); review and pages never delete; applies one guarded conflict or quarantine choice: quarantine-retry, quarantine-ignore, quarantine-exclude, quarantine-delete-dropbox, defender-exclude (attended UAC; two exact file and sibling-stage exclusions; never folders; generation 0 for notices; retained ignored key supports later retry), defender-verify (explicit read-only UAC verification of recorded exclusions; hidden or unavailable visibility stays held), defender-keep (confirmed current file only; existing exclusion; no UAC/settings change), defender-proceed-deletions and defender-keep-held (reviewed UNKNOWN-history batch only), defender-release-held (all Keep-held items), defender-release-item (one Keep-held item named by --path; shared items open a separate review), defender-exclude-held (exclude one held item), defender-release-all-pairs and defender-keep-shared-held (explicit shared-item review choices); also applies explicit blocker actions with the stale-plan guard: rebuildable-rule preview/choose/remove/keep, single-file offline (including a confirmed large download) and document export (docx, markdown, pdf, html)",
            "the product state or the running application", "direct|running-application",
            "--pair,--path,--choice,--key,--generation,--account,--yes,--timeout-seconds,--name,--offset,--page-token", Mutating: true, RequiresYes: true,
            RequiresPair: true),
        new("recovery list", "recovery list --pair <exact-id> [--since <timestamp>] [--path-prefix <relative-path>] [--side local|dropbox] [--action-id <exact-id>] [--after <recovery-id>] [--limit 1..500] | recovery list --pair <exact-id> [--account <exact-id>] [--search <text>] [--limit 1..200] [--offset <n>] [--deleted]",
            SyncCliRequestKind.State,
            "none", "retained recovery catalog and optional deleted cloud items, including Google Trash; core v2 adds " +
            "bounded keyset filters that return one page plus the next recovery-id cursor and each row's current-state " +
            "classification and destination; partial-listing warnings are retained",
            "the product state or the running application", "direct|running-application",
            "--pair,--account,--search,--limit,--offset,--deleted,--since,--path-prefix,--side,--action-id,--after",
            RequiresPair: true),
        new("recovery revisions", "recovery revisions --pair <exact-id> --account <exact-id> --path <relative-path>",
            SyncCliRequestKind.State, "none", "loads exact provider revision history (Google binary files or Dropbox)", "the account provider",
            "direct|running-application", "--pair,--account,--path", LiveVerificationRequired: true, RequiresPair: true),
        new("recovery usage", "recovery usage --pair <exact-id>", SyncCliRequestKind.State, "none",
            "retained recovery size, oldest item and retention window",
            "the product state or the running application", "direct|running-application", PairOnly,
            RequiresPair: true),
        new("recovery restore", "recovery restore --pair <exact-id> --id <recovery-id> [--as-copy] [--dry-run] [--yes] | " +
            "recovery restore --pair <exact-id> (--since <timestamp> | --path-prefix <relative-path> | --action-id <exact-id>) " +
            "[--side local|dropbox] [--after <recovery-id>] [--limit 1..500] [--as-copy] [--dry-run] [--yes]; " +
            "legacy --key and --copy aliases remain",
            SyncCliRequestKind.State, "yes", "restores one exact retained copy/revision or one bounded filtered selection " +
            "(one keyset page per invocation with an explicit next cursor); a dry run previews and never writes unless --yes " +
            "confirms, and --dry-run always wins over --yes; a bare command with no target is refused; core v2 restores a " +
            "retained Dropbox version by a safe historical download and an add-only upload of that version, never by " +
            "overwriting another file; legacy provider-revision --account/--path restore and exact Google Trash items " +
            "remain on the v1 surface",
            "the product state or the running application", "direct|running-application",
            "--pair,--account,--path,--id,--key,--as-copy,--copy,--dry-run,--since,--path-prefix,--side,--action-id,--after,--limit,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("recovery cleanup", "recovery cleanup --pair <exact-id> --key <recovery-key> --yes",
            SyncCliRequestKind.State, "yes", "removes one retained recovery copy under the retention rules",
            "the product state or the running application", "direct|running-application", "--pair,--key,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("decisions list", "decisions list --pair <exact-id> [--limit 1..200] [--offset <n>]",
            SyncCliRequestKind.State, "none",
            "open needs-decision notices of the pair, each with its rule, reason key, detail, both sides' size and " +
            "modified time from the store, and a version token that decisions resolve requires",
            "the product state or the running application", "direct|running-application", "--pair,--limit,--offset",
            RequiresPair: true),
        new("decisions resolve",
            "decisions resolve --pair <exact-id> --path <relative-path> --keep <a|b|<side name>> --token <version-token> --yes",
            SyncCliRequestKind.State, "yes",
            "applies the chosen side on one open needs-decision path and closes its notice once the winner's effect " +
            "completes or both sides hold equal content; refused when the file changed since decisions list, and for a " +
            "folder decision (decide the files inside)",
            "the product state or the running application", "direct|running-application",
            "--pair,--path,--keep,--token,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("plan", "plan --pair <exact-id> [--limit 1..200] [--offset <n>]", SyncCliRequestKind.State, "none",
            "the retained first-sync plan with exact left/right endpoint destinations and separate baseline-proven update counts; no inventory refresh or provider call",
            "the product state or the running application", "direct|running-application", "--pair,--limit,--offset",
            RequiresPair: true),
        new("scan", "scan --pair <exact-id> --yes [--timeout-seconds <seconds>]", SyncCliRequestKind.State, "yes",
            "one effect-free inventory pass with live operation detail and bounded automatic discovery; failures retained in Activity and Problems",
            "the product state or the running application and the pair's selected endpoints",
            "direct|running-application", "--pair,--yes,--timeout-seconds", LiveVerificationRequired: true, Mutating: true,
            RequiresYes: true, RequiresPair: true),
        new("first-sync prepare", "first-sync prepare --pair <exact-id> --yes [--choices <absolute-json-file-or-inline-json>] [--timeout-seconds <seconds>]",
            SyncCliRequestKind.State, "yes",
            "refreshes and compares complete real endpoint inventories without dispatching retained work or approving first sync; reports per-path matching-baseline/conflict progress and returns phase timings, inventory identities, recommended transfer bytes and database/WAL growth; optional per-folder remove/copy choices and rebuildables (category ids or {id,prefix} objects) are previewed with their resolved effects and no file is touched",
            "the product state or the running application and the pair's selected endpoints",
            "direct|running-application", "--pair,--choices,--yes,--timeout-seconds", LiveVerificationRequired: true,
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("first-sync approve", "first-sync approve --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "records the reviewed first-sync decision so ordinary runs may proceed",
            "the product state or the running application", "direct|running-application", "--pair,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("first-sync repair-moves", "first-sync repair-moves --pair <exact-id> --yes [--timeout-seconds <seconds>]",
            SyncCliRequestKind.State, "yes",
            "finishes already-completed cloud move copies by retaining only exact unchanged obsolete local memberships in protected recovery; distinct cloud files instead receive explicitly verified current identity separation with both files and accepted history preserved; reports each repaired or preserved file; never uploads or repeats completed downloads",
            "the product state or the running application and the selected local/Dropbox pair",
            "direct|running-application", "--pair,--yes,--timeout-seconds", LiveVerificationRequired: true,
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("first-sync apply", "first-sync apply --pair <exact-id> --yes [--choices <absolute-json-file-or-inline-json>] [--allow-recommended] [--generation <policy-revision>] [--key <local-scan-generation>] [--timeout-seconds <seconds>]",
            SyncCliRequestKind.State, "yes",
            "applies reviewed copies and baseline-proven one-sided updates; choices are upload, download, move or no-change in a unique-path JSON document up to 64 MiB; --allow-recommended approves safe transfers only; offered moves require an explicit move choice or are refused per file; conflicts remain unchanged for separate resolution",
            "the product state or the running application and the selected endpoints",
            "direct|running-application", "--pair,--choices,--allow-recommended,--generation,--key,--yes,--timeout-seconds",
            LiveVerificationRequired: true, Mutating: true,
            RequiresYes: true, RequiresPair: true),
        new("run batch", "run batch --selection <json-array-of-exact-pair-ids> --yes [--timeout-seconds <seconds>]", SyncCliRequestKind.State, "yes",
            "runs an explicitly named set through the existing priority and concurrency scheduler after validating every target",
            "the product state or the running application and the providers", "direct|running-application",
            "--selection,--yes,--timeout-seconds", LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("run", "run --pair <exact-id> --yes [--timeout-seconds <seconds>]", SyncCliRequestKind.State, "yes",
            "one explicitly named, already-approved pair through the existing run guards; independent-file concurrency and per-file progress; later arrivals wait for the next sync, while stale plans and move dependencies are re-observed; retained completed-file results if a later scan is cancelled; no implicit time limit",
            "the product state or the running application and the Dropbox provider",
            "direct|running-application", "--pair,--yes,--timeout-seconds", LiveVerificationRequired: true, Mutating: true,
            RequiresYes: true, RequiresPair: true),
        new("run pause", "run pause --yes", SyncCliRequestKind.State, "yes",
            "pauses the active run at its next safe checkpoint", "the running application", "running-application",
            "--yes", NeedsApplication: true, Mutating: true, RequiresYes: true),
        new("run retry-plan", "run retry-plan --run <retained-run-id> [--pair <exact-id>] [--limit <1..200>] [--offset <n>]", SyncCliRequestKind.State, "none",
            "shows each retained approved file, exact failure and whether retry or verification is available",
            "retained run and approved choices", "direct|running-application", "--run,--pair,--limit,--offset"),
        new("run verify", "run verify --run <retained-run-id> --yes [--timeout-seconds <seconds>]", SyncCliRequestKind.State, "yes",
            "reconciles only the selected run and linked retries' exact uncertain operations; never transfers Ready work or recovers unrelated pair operations",
            "retained run and provider evidence", "direct|running-application", "--run,--yes,--timeout-seconds", Mutating: true, RequiresYes: true),
        new("run retry", "run retry --run <retained-run-id> [--pair <exact-id>] [--choice failed|unattempted|all] [--selection <json-array-of-paths>] --yes [--timeout-seconds <seconds>]",
            SyncCliRequestKind.State, "yes", "retries only retained approved failed or unattempted files after fresh validation; never replays completed or uncertain effects",
            "retained run and approved choices", "direct|running-application", "--run,--pair,--choice,--selection,--yes,--timeout-seconds",
            Mutating: true, RequiresYes: true),
        new("run resume", "run resume --yes", SyncCliRequestKind.State, "yes", "continues a paused active run",
            "the running application", "running-application", "--yes", NeedsApplication: true, Mutating: true,
            RequiresYes: true),
        new("cancel", "cancel --key <retained-run-id> --yes", SyncCliRequestKind.State, "yes",
            "stops exactly one named active job through the existing run control and waits for its checkpoint",
            "the running application", "running-application", "--key,--yes", NeedsApplication: true,
            Mutating: true, RequiresYes: true),
        new("pause", "pause [--account <exact-id>] [--minutes <n>|--until <instant>|--until-tomorrow|--until-resumed] --yes",
            SyncCliRequestKind.State, "yes",
            "pauses automatic work globally or for one exact account until a resolved deadline, tomorrow at the shared " +
            "resume hour, an exact instant, or until resumed",
            "the product state or the running application", "direct|running-application",
            "--account,--minutes,--until,--until-tomorrow,--until-resumed,--yes", Mutating: true, RequiresYes: true),
        new("resume", "resume [--account <exact-id>] --yes", SyncCliRequestKind.State, "yes",
            "resumes automatic work globally or for one exact account", "the product state or the running application",
            "direct|running-application", "--account,--yes", Mutating: true, RequiresYes: true),
        new("settings show", "settings show", SyncCliRequestKind.State, "none",
            "dataLocation, automation, transfer and interface settings plus current adaptive resource recommendation and measured limits",
            "the product state or the running application", "direct|running-application", None),
        new("settings set", "settings set --settings <json> --yes", SyncCliRequestKind.State, "yes",
            "validates and persists stated sections only; transfer.resourceMode: Auto, Performance or Low impact; " +
            "Performance uses available-memory headroom, not Auto's routine 85% RAM cutoff; all modes retain memory safeguards and provider backoff; " +
            "transfer.maximumConcurrentFiles: 0 (recommended) or 1..1024; " +
            "transfer.uploadBatchSize: 0 (default 1000) or 1..1000; transfer.uploadCommitConcurrency: 0 (default 1) or 1..4; " +
            "transfer.deleteBatchSize: 0 (default 1000) or 1..1000; uploadKiBPerSecond/downloadKiBPerSecond: 0 (unlimited) or an explicit cap; " +
            "interface.statusIconStyle: Colour, Symbol or Combined; dataLocation: empty local fixed-drive folder, changed alone with app closed and no pending work; restart required",
            "the product state or the running application", "direct|running-application", "--settings,--yes",
            Mutating: true, RequiresYes: true),
        new("ignores list", "ignores list --pair <exact-id>", SyncCliRequestKind.State, "none",
            "the pair's friendly ignore rules", "the product state or the running application",
            "direct|running-application", PairOnly, RequiresPair: true),
        new("ignores set", "ignores set --pair <exact-id> --rules <kind:value;...|none> --yes", SyncCliRequestKind.State,
            "yes", "replaces the pair's ignore rules through the app validator; core v2 invalidates prepare on change; no file effects",
            "the product state or the running application", "direct|running-application", "--pair,--rules,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("folders tree", "folders tree --pair <exact-id>", SyncCliRequestKind.State, "none",
            "the pair's folder selection tree with inclusion and availability state",
            "the product state or the running application", "direct|running-application", PairOnly,
            RequiresPair: true),
        new("folders preview-include", "folders preview-include --pair <exact-id> --path <relative-path>",
            SyncCliRequestKind.State, "none", "previews default reconciliation of only this folder without changing selection or files",
            "the product state or the running application", "direct|running-application", "--pair,--path",
            RequiresPair: true),
        new("folders include", "folders include --pair <exact-id> --path <relative-path> --yes",
            SyncCliRequestKind.State, "yes", "includes one folder or the pair root in the selection",
            "the product state or the running application", "direct|running-application", "--pair,--path,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("folders exclude", "folders exclude --pair <exact-id> --path <relative-path> --yes",
            SyncCliRequestKind.State, "yes", "excludes one folder or the pair root from the selection",
            "the product state or the running application", "direct|running-application", "--pair,--path,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("folders create-local", "folders create-local --local <existing-parent> --name <single-name> --yes",
            SyncCliRequestKind.State, "yes",
            "creates one ordinary folder inside an existing folder on this computer without changing any pair, account or sync state",
            "the product state or the running application", "direct|running-application",
            "--local,--name,--yes", Mutating: true, RequiresYes: true),
        new("availability set", "availability set --pair <exact-id> --path <relative-path> --availability <online-only|offline> --yes",
            SyncCliRequestKind.State, "yes", "sets one path's device availability through the existing storage rules",
            "the product state or the running application", "direct|running-application",
            "--pair,--path,--availability,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage devices", "storage devices", SyncCliRequestKind.State, "none",
            "read fixed-device capacity and available space; does not scan files", "Windows device metadata",
            "direct|running-application", ""),
        new("storage inventory", "storage inventory [--account <exact-id>] [--pair <exact-id>] [--offset <n>] [--limit 1..200]",
            SyncCliRequestKind.State, "none", "read saved cross-pair storage policy and indexed availability without scanning", "retained authority",
            "direct|running-application", "--account,--pair,--offset,--limit"),
        new("storage inspect", "storage inspect --local <exact-folder> [--pair <exact-id> --account <exact-id>] [--limit 1..200]",
            SyncCliRequestKind.State, "none", "bounded non-elevated on-disk metadata only; no hydration or ownership changes", "Windows no-recall metadata",
            "direct|running-application", "--local,--pair,--account,--limit"),
        new("storage show", "storage show --pair <exact-id> --account <exact-id> [--offset <n>] [--search <text>] [--filter <filter>]",
            SyncCliRequestKind.State, "none", "indexed storage, defaults, policies and folder participation", "retained authority",
            "direct|running-application", "--pair,--account,--offset,--search,--filter", RequiresPair: true),
        new("storage files", "storage files --pair <exact-id> --account <exact-id> [--offset <n>] [--limit 1..200] [--search <text>]",
            SyncCliRequestKind.State, "none", "identity-bound file overrides", "retained authority",
            "direct|running-application", "--pair,--account,--offset,--limit,--search", RequiresPair: true),
        new("storage default", "storage default --pair <exact-id> --account <exact-id> --generation <policy-revision> --availability <offline|online-only> --yes",
            SyncCliRequestKind.State, "yes", "changes the default for new files; preserves child overrides", "Windows and account provider",
            "direct|running-application", "--pair,--account,--generation,--availability,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage root", "storage root --pair <exact-id> --account <exact-id> --generation <policy-revision> --availability <offline|online-only> --yes",
            SyncCliRequestKind.State, "yes", "changes managed root availability including existing root items", "Windows and account provider",
            "direct|running-application", "--pair,--account,--generation,--availability,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage folders", "storage folders --pair <exact-id> --account <exact-id> --generation <policy-revision> --settings <JSON-array-of-exact-folders> --availability <offline|online-only|inherit> --yes",
            SyncCliRequestKind.State, "yes", "changes 1..100 selected folders or removes their overrides; selection is unchanged", "Windows and account provider",
            "direct|running-application", "--pair,--account,--generation,--settings,--availability,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage file", "storage file --pair <exact-id> --account <exact-id> --generation <policy-revision> --path <relative-path> --availability <offline|online-only> --yes",
            SyncCliRequestKind.State, "yes", "sets persistent identity-bound file storage", "Windows and account provider",
            "direct|running-application", "--pair,--account,--generation,--path,--availability,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage reset-file", "storage reset-file --pair <exact-id> --account <exact-id> --generation <policy-revision> --path <relative-path> --key <local-file-id> --yes",
            SyncCliRequestKind.State, "yes", "removes exactly the retained file override and follows its identity", "Windows and account provider",
            "direct|running-application", "--pair,--account,--generation,--path,--key,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage enable", "storage enable --pair <exact-id> --account <exact-id> --generation <policy-revision> --yes",
            SyncCliRequestKind.State, "yes", "enables managed storage preserving the current default", "Windows and account provider",
            "direct|running-application", "--pair,--account,--generation,--yes", Mutating: true, RequiresYes: true, RequiresPair: true),
        new("storage disable", "storage disable --pair <exact-id> --account <exact-id> --generation <policy-revision> --yes",
            SyncCliRequestKind.State, "yes", "downloads all managed files before removing online-only storage and its root registration", "the signed installed application and account provider",
            "running-application", "--pair,--account,--generation,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: true),
        new("updates check", "updates check [--download --yes]", SyncCliRequestKind.State, "yes when downloading",
            "consults the public release channel for a compatible signed update",
            "the public release channel", "direct|running-application", "--download,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true, MutationOption: "--download"),
        new("explorer status", "explorer status", SyncCliRequestKind.State, "none",
            "Explorer integration state, installed-process identity and pair count",
            "the product state or the running application", "direct|running-application", None),
        new("explorer restore", "explorer restore --yes", SyncCliRequestKind.State, "yes",
            "restores the installed app's existing Explorer registration workflow", "the signed installed application",
            "running-application", "--yes", NeedsApplication: true, Mutating: true, RequiresYes: true),
        new("app uninstall", "app uninstall --yes", SyncCliRequestKind.State, "yes",
            "makes managed files local, pauses sync and initiates the signed uninstaller while retaining data and rollback", "the signed installed application; Windows approval remains separate",
            "running-application", "--yes", NeedsApplication: true, Mutating: true, RequiresYes: true),
        new("locations open", "locations open --pair <exact-id> --path <relative-path-or-dot> --choice <local|cloud> --yes", SyncCliRequestKind.State, "yes",
            "opens the exact retained pair item in Explorer or the provider website", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--pair,--account,--path,--choice,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: true),
        new("explorer roots", "explorer roots --pair <exact-id>", SyncCliRequestKind.State, "none",
            "reads competing parent registrations and all affected pairs", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--pair,--account", NeedsApplication: true, Mutating: false, RequiresYes: false, RequiresPair: true),
        new("explorer inspect", "explorer inspect --pair <exact-id> --key <registration-id> --path <entire-root> --yes", SyncCliRequestKind.State, "yes",
            "inspects and holds the whole exact root without reading file content", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--pair,--account,--key,--path,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: true),
        new("explorer remove", "explorer remove --pair <exact-id> --key <inspection-key> --path <entire-root> --yes", SyncCliRequestKind.State, "yes",
            "removes only the inspected Dropbox shell registration; never a native cloud root", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--pair,--account,--key,--path,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: true),
        new("explorer undo", "explorer undo --pair <exact-id> --key <inspection-key> --path <entire-root> --yes", SyncCliRequestKind.State, "yes",
            "restores the exact removed shell registration if no provider has replaced it", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--pair,--account,--key,--path,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: true),
        new("explorer release", "explorer release --pair <exact-id> --key <inspection-key> --path <entire-root> --yes", SyncCliRequestKind.State, "yes",
            "releases inspection handles and discards this session's undo capability", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--pair,--account,--key,--path,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: true),
        new("sharing open", "sharing open --account <exact-id> --key <exact-link-url> [--selection <namespace-and-permission-json>] --yes", SyncCliRequestKind.State, "yes",
            "opens one account-verified shared link", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--account,--key,--selection,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: false),
        new("sharing manage", "sharing manage --account <exact-id> --key <exact-link-url> [--selection <namespace-and-permission-json>] --yes", SyncCliRequestKind.State, "yes",
            "opens the exact link's secure editor; password input stays outside command arguments", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--account,--key,--selection,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: false),
        new("file-requests open", "file-requests open --account <exact-id> --key <exact-request-id> [--selection <namespace-json>] --yes", SyncCliRequestKind.State, "yes",
            "opens one account-verified file request", "the running application; ownership mutations require its signed installed identity",
            "running-application", "--account,--key,--selection,--yes", NeedsApplication: true, Mutating: true, RequiresYes: true, RequiresPair: false),
        new("app status", "app status", SyncCliRequestKind.State, "none",
            "the actual in-process startup phase, completion or failure, progress time and run state; no database or provider read",
            "the running application", "running-application", None, NeedsApplication: true),
        new("app show", "app show --yes", SyncCliRequestKind.State, "yes",
            "activates the running Sync Shark window", "the running application", "running-application",
            "--yes", NeedsApplication: true, Mutating: false, RequiresYes: true)
        ,
        new("rewind checkpoint", "rewind checkpoint --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "pauses the pair at a complete local and Dropbox checkpoint for provider Rewind",
            "the product state or the running application and the Dropbox provider",
            "direct|running-application", "--pair,--yes", LiveVerificationRequired: true, Mutating: true,
            RequiresYes: true, RequiresPair: true),
        new("rewind preview", "rewind preview --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "reads and validates the provider rollback batch retained against the checkpoint",
            "the product state or the running application and the Dropbox provider",
            "direct|running-application", "--pair,--yes", LiveVerificationRequired: true, Mutating: true,
            RequiresYes: true, RequiresPair: true),
        new("rewind apply", "rewind apply --pair <exact-id> --key <reviewed-session-id> --source <reviewed-local-scan-id> --destination <reviewed-dropbox-scan-id> --yes", SyncCliRequestKind.State, "yes",
            "applies only the exact session and immutable preview scans returned by rewind preview; changed consent is refused",
            "the product state or the running application and the Dropbox provider",
            "direct|running-application", "--pair,--key,--source,--destination,--yes", LiveVerificationRequired: true, Mutating: true,
            RequiresYes: true, RequiresPair: true),
        new("rewind resume", "rewind resume --pair <exact-id> --yes", SyncCliRequestKind.State, "yes",
            "resumes ordinary synchronization after a checkpoint or an applied Rewind",
            "the product state or the running application", "direct|running-application", "--pair,--yes",
            Mutating: true, RequiresYes: true, RequiresPair: true),
        new("app exit", "app exit --yes", SyncCliRequestKind.State, "yes",
            "closes the running application through its own graceful exit checkpoint",
            "the running application", "running-application", "--yes",
            NeedsApplication: true, Mutating: true, RequiresYes: true),
        new("updates verify", "updates verify --path <absolute-installer-path>", SyncCliRequestKind.State, "none",
            "checks a local package with the installed signature, hash and compatibility verifier; does not install",
            "the running signed installed application", "running-application", "--path", NeedsApplication: true),
        new("updates apply", "updates apply --update <exact-version> [--path <absolute-installer-path>] --yes", SyncCliRequestKind.State, "yes",
            "verifies the retained exact version and starts the app's existing installer handoff; Windows consent remains required",
            "the running signed installed app and one exact local or retained downloaded package", "running-application", "--update,--path,--yes",
            NeedsApplication: true, LiveVerificationRequired: true, Mutating: true, RequiresYes: true)
        ,
        new("sharing list", "sharing list --account <exact-id> [--limit 1..200] [--offset <n>] [--settings {namespace}]", SyncCliRequestKind.State, "none",
            "lists account-wide shared links with audience, access, exact permission identity and incomplete-listing warnings, with a bounded non-identifying cause, stage and detail code on each item that could not be inspected",
            "the running application or the exclusive product lock and the selected Dropbox or Google Drive provider",
            "direct|running-application", "--account,--limit,--offset,--settings", LiveVerificationRequired: true),
        new("sharing create", "sharing create --account <exact-id> --path <Dropbox-path-or-Google-file-ID> --choice <public|domain:example.com|Dropbox-audience> --yes",
            SyncCliRequestKind.State, "yes",
            "creates one shared link for the exact requested item",
            "the running application or the exclusive product lock and the selected Dropbox or Google Drive provider",
            "direct|running-application", "--account,--path,--choice,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("sharing revoke-selected", "sharing revoke-selected --settings <json-with-links-array> --yes", SyncCliRequestKind.State,
            "yes", "revokes 1..100 exact account/namespace/URL identities; Google targets retain permissionId and revisionFingerprint; returns every outcome without uncertain replay; complete receipt size is checked before provider effects",
            "the running application or the exclusive product lock and the selected Dropbox or Google Drive provider",
            "direct|running-application", "--settings,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("sharing revoke", "sharing revoke --account <exact-id> --key <exact-url> [--settings {namespace}] --yes", SyncCliRequestKind.State,
            "yes", "revokes one unambiguous retained shared link; use revoke-selected for exact Google permission selection",
            "the running application or the exclusive product lock and the selected Dropbox or Google Drive provider",
            "direct|running-application", "--account,--key,--settings,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("file-requests list", "file-requests list --account <exact-id> [--limit 1..200] [--offset <n>] [--settings {namespace}]", SyncCliRequestKind.State, "none",
            "lists the account's file requests",
            "the running application or the exclusive product lock and the Dropbox provider",
            "direct|running-application", "--account,--limit,--offset,--settings", LiveVerificationRequired: true),
        new("sharing change", "sharing change --account <exact-id> --key <exact-url> --settings <json> --yes",
            SyncCliRequestKind.State, "yes",
            "changes supported Dropbox link settings or a Google role; Google settings accept permissionId and revisionFingerprint from sharing list",
            "the running application or the exclusive product lock and the selected Dropbox or Google Drive provider",
            "direct|running-application", "--account,--key,--settings,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("file-requests edit", "file-requests edit --account <exact-id> --key <request-id> --settings <json> --yes",
            SyncCliRequestKind.State, "yes",
            "edits or reopens one exact retained file request without changing its identity",
            "the running application or the exclusive product lock and the Dropbox provider",
            "direct|running-application", "--account,--key,--settings,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("file-requests create", "file-requests create --account <exact-id> --name <title> --path <destination> [--description <text>] [--settings {namespace}] --yes",
            SyncCliRequestKind.State, "yes", "creates one open file request for an exact destination",
            "the running application or the exclusive product lock and the Dropbox provider",
            "direct|running-application", "--account,--name,--path,--description,--settings,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("file-requests close", "file-requests close --account <exact-id> --key <request-id> [--settings {namespace}] --yes",
            SyncCliRequestKind.State, "yes", "closes exactly one retained file request",
            "the running application or the exclusive product lock and the Dropbox provider",
            "direct|running-application", "--account,--key,--settings,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("files upload",
            "files upload --account <exact-id> --source <absolute-local-file> --destination <namespace-qualified-path> --bytes <expected-byte-length> --sha256 <expected-sha256> --yes",
            SyncCliRequestKind.State, "yes",
            "uploads exactly one local file to one exact absent Dropbox destination with a single create-only " +
            "conditional commit; it never creates a parent folder, syncs a pair, autorenames or overwrites",
            "the running application or the exclusive product lock and the exact Dropbox account and existing destination parent",
            "direct|running-application", "--account,--source,--destination,--bytes,--sha256,--yes",
            LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("discovery status", "discovery status", SyncCliRequestKind.State, "none",
            "reports the protected Discovery helper's managed status without elevation",
            "the product state or the running application", "direct|running-application", None)
        ,
        new("discovery check", "discovery check --pair <exact-id>", SyncCliRequestKind.State, "none",
            "runs the existing guarded metadata check for one retained pair without planning or syncing",
            "the product state or the running application and the protected helper if installed",
            "direct|running-application", "--pair", RequiresPair: true),
        new("discovery configure", "discovery configure --yes", SyncCliRequestKind.State, "yes",
            "starts the existing signed Discovery setup; Windows administrator approval is still required",
            "the running signed installed app", "running-application", "--yes",
            NeedsApplication: true, LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("discovery restore", "discovery restore --yes", SyncCliRequestKind.State, "yes",
            "restores the previous protected Discovery setup through the existing guarded helper",
            "the running signed installed app and Windows administrator approval", "running-application", "--yes",
            NeedsApplication: true, LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        new("discovery remove", "discovery remove --yes", SyncCliRequestKind.State, "yes",
            "removes only the existing Discovery setup; ordinary scanning remains available",
            "the running signed installed app and Windows administrator approval", "running-application", "--yes",
            NeedsApplication: true, LiveVerificationRequired: true, Mutating: true, RequiresYes: true),
        ..DropboxCliCatalog.Rows
    ];

    // Exact blocked dependencies: named work that is not implemented here and is never faked as success.
    public static readonly IReadOnlyList<KeyValuePair<string, string>> Blocked =
    [
        // Only genuinely unavailable work is listed here; every implemented command has a row above and must not
        // appear in this array (asserted by the parity check).
        new("pairs bind",
            "the product admission path verifies the remote root through the real Dropbox client, so live "
            + "admission needs an authenticated provider; the CLI-facing creation flow is pairs create"),
        new("updates install",
            "installation is the signed installer's own privileged action after updates apply initiates the "
            + "existing app verifier/handoff; Windows consent and installed-result verification remain separate")
    ];

    public static readonly IReadOnlyDictionary<string, string> ExitCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["0"] = "success",
            ["2"] = "usage error: unknown command, unknown or duplicate option, missing required option or invalid value",
            ["3"] = "busy: another Sync Shark process holds the exclusive product state lock",
            ["4"] = "missing or ambiguous target: no retained object matches the exact selector",
            ["5"] = "the action needs review or an interactive prerequisite, or the result is blocked",
            ["6"] = "operation failed: the command ran and did not complete safely",
            ["7"] = "transient_unavailable: transport/provider unavailable; retryable, retained work is reconciled",
            ["8"] = "pending_verification: retryable; wait error.retryAfterSeconds before rerunning the same apply",
            ["130"] = "canceled or explicit command deadline exceeded; completed work is retained"
        };

    private static readonly string[] ChoiceNames = [.. Enum.GetNames<SyncAuthorityConflictResolutionKind>(),
        V2.NoticeChoices.UseOtherVersion, V2.NoticeChoices.SaveOtherVersionAsCopy, V2.NoticeChoices.Dismiss,
        V2.NoticeChoices.RetryNow, V2.NoticeChoices.Exclude, V2.NoticeChoices.Ignore,
        V2.NoticeChoices.ShowTarget, V2.NoticeChoices.SyncTargetSeparately, V2.NoticeChoices.CopyLink, V2.NoticeChoices.CopyLinkOutsideRoot, V2.NoticeChoices.CopyLinkAgain, V2.NoticeChoices.RemoveLinkStaging, V2.NoticeChoices.ConfirmLargeDownload, V2.NoticeChoices.MakeAvailableOffline, V2.NoticeChoices.ExportDocx, V2.NoticeChoices.ExportMarkdown, V2.NoticeChoices.ExportPdf, V2.NoticeChoices.ExportHtml, V2.NoticeChoices.CheckHistoryAgain, V2.NoticeChoices.ReviewDeletion, V2.NoticeChoices.OpenStorage, V2.NoticeChoices.ReviewReserve, V2.NoticeChoices.OpenRecoverySettings, V2.NoticeChoices.RepairHelp, V2.NoticeChoices.Rename, V2.NoticeChoices.ShowRemaining, V2.NoticeChoices.OpenDropbox, V2.NoticeChoices.DeleteReviewed, V2.NoticeChoices.KeepRestore,
        V2.NoticeChoices.PreviewMatches, V2.NoticeChoices.ChooseFolder, V2.NoticeChoices.RemoveRule, V2.NoticeChoices.KeepRule,
        V2.NoticeChoices.QuarantineRetry, V2.NoticeChoices.QuarantineIgnore, V2.NoticeChoices.QuarantineExclude,
        V2.NoticeChoices.QuarantineDeleteDropbox, V2.NoticeChoices.DefenderExclude, V2.NoticeChoices.DefenderVerify, V2.NoticeChoices.DefenderKeep, V2.NoticeChoices.DefenderProceed, V2.NoticeChoices.DefenderHold,
        V2.NoticeChoices.DefenderReleaseHeld, V2.NoticeChoices.DefenderReleaseItem, V2.NoticeChoices.DefenderExcludeHeld,
        V2.NoticeChoices.DefenderReleaseAllPairs, V2.NoticeChoices.DefenderKeepShared,
        V2.NoticeChoices.Compare, "merge=<relative-path>", V2.NoticeChoices.KeepA, V2.NoticeChoices.KeepB,
        V2.NoticeChoices.KeepBoth, "rename-incoming=<relative-path>", V2.NoticeChoices.ReplaceExisting,
        V2.NoticeChoices.RestoreExisting, V2.NoticeChoices.RestoreAsCopy,
        V2.NoticeChoices.ReviewRemoval, V2.NoticeChoices.ReviewRemovalA, V2.NoticeChoices.ReviewRemovalB, V2.NoticeChoices.ConfirmRemoval,
        V2.NoticeChoices.RemovalNextPage, V2.NoticeChoices.RemovalPreviousPage, V2.NoticeChoices.OpenFolderSettings, V2.JournalSizing.Enlarge, V2.JournalSizing.NotNow, V2.JournalSizing.Never];

    public static SyncCliCapabilityData Capabilities() => new("Sync Shark", ProductVersion(),
        BuildIdentity.SourceRevision,
        Rows.Select(row => new SyncCliCapabilityRow(row.Command,
            row.Usage, row.Mutating ? "mutating" : "read",
            row.Effect, row.Requires, row.NeedsApplication ? "running-application"
                : row.Mode)).ToArray(),
        Blocked.Select(item => item.Key + ": " + item.Value).ToArray(),
        Rows.Where(row => row.LiveVerificationRequired).Select(row => row.Command + ": " + row.Effect).ToArray());

    public static string RenderCapabilities(SyncCliCapabilityData capabilities)
    {
        var text = new StringBuilder();
        text.AppendLine($"Sync Shark {capabilities.Version}+{capabilities.SourceRevision}: " +
                        $"{capabilities.Commands.Count} command(s)");
        foreach (var row in capabilities.Commands)
            text.AppendLine($"{row.Command}  [{row.Kind}]  {row.Usage}");
        text.AppendLine();
        text.AppendLine("Live provider or OS verification required before these are proven:");
        foreach (var item in capabilities.LiveVerificationRequired) text.AppendLine("  " + item);
        text.AppendLine();
        text.AppendLine("Exact blocked dependencies (not implemented, not faked):");
        foreach (var item in capabilities.NotImplemented) text.AppendLine("  " + item);
        return text.ToString();
    }

    /// <summary>
    /// DIAG354-4: the refusal every command this one registry marks as needing the running application
    /// uses when no trusted same-product application owns the product runtime. It is offered instead of
    /// opening, creating, locking or reading product state, so a no-application answer is honest and
    /// effect-free.
    /// </summary>
    public const string NeedsApplicationRefusal =
        "This command runs the shared read-only action the running Sync Shark application owns. Start Sync " +
        "Shark and run the command again, so it executes inside that process. Nothing was opened, created " +
        "or changed.";

    /// <summary>
    /// True when this exact command is marked in the one registry as requiring the running application.
    /// The mark itself is the existing NeedsApplication capability, not a second classification.
    /// </summary>
    public static bool RequiresRunningApplication(string? command) =>
        command is not null && Rows.Any(row =>
            string.Equals(row.Command, command, StringComparison.Ordinal) && row.NeedsApplication);

    /// <summary>
    /// DIAG354-4: the commands whose own requirement is a state-free refusal when no trusted application
    /// owns the product runtime. Those commands must answer before a runtime is created, so the refusal
    /// never opens, locks or creates product state. Every other command keeps its existing order, where its
    /// own usage validation and its NeedsApplication refusal run inside the ordinary runtime path; extending
    /// this set is a product decision rather than a dispatcher default.
    /// </summary>
    private static readonly string[] StateFreeApplicationRefusalCommands = ["diagnostics run"];

    /// <summary>
    /// True when this exact registered command must refuse without a running application before any runtime
    /// is created. It is always also a command this registry marks as needing the running application.
    /// </summary>
    public static bool RequiresStateFreeApplicationRefusal(string? command) =>
        command is not null && StateFreeApplicationRefusalCommands.Contains(command, StringComparer.Ordinal) &&
        RequiresRunningApplication(command);

    private static string ProductVersion()
    {
        var informational = BuildIdentity.InformationalVersion;
        var separator = informational.IndexOf('+', StringComparison.Ordinal);
        return separator < 0 ? informational : informational[..separator];
    }

    public static SyncCliInvocation Parse(string[] args)
    {
        if (!SyncCli.IsCliInvocation(args))
            throw new SyncCliUsageException($"The CLI route must begin with --cli. Run: {SyncCli.ExecutableName} --cli help");
        var tokens = new List<string>();
        var json = false;
        var verbose = false;
        for (var index = 1; index < args.Length; index++)
        {
            var token = args[index] ?? string.Empty;
            if (string.Equals(token, "--verbose", StringComparison.Ordinal))
            {
                verbose = true;
                continue;
            }
            if (!string.Equals(token, "--json", StringComparison.Ordinal))
            {
                tokens.Add(token);
                continue;
            }
            if (json) throw new SyncCliUsageException("The option --json was supplied more than once.");
            json = true;
        }

        var next = 0;
        var words = new List<string>();
        while (next < tokens.Count && words.Count < 2 && !tokens[next].StartsWith("--", StringComparison.Ordinal))
            words.Add(tokens[next++]);
        if (words.Count == 0 && tokens.Count > 0)
            throw new SyncCliUsageException("The first argument after --cli must be a command, not an option.");
        if (words.Count == 0)
            throw new SyncCliUsageException($"A command is required. Run: {SyncCli.ExecutableName} --cli help");
        var command = string.Join(' ', words);
        var row = Rows.FirstOrDefault(candidate => string.Equals(candidate.Command, command, StringComparison.Ordinal))
                  ?? throw new SyncCliUsageException($"Unknown command '{command}'. Run: " +
                      $"{SyncCli.ExecutableName} --cli help");

        var allowed = row.Options.Length == 0
            ? []
            : row.Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var excludes = new List<string>();
        var provenanceLogs = new List<string>();
        var provenancePrefixes = new List<string>();
        while (next < tokens.Count)
        {
            var option = tokens[next++];
            if (!option.StartsWith("--", StringComparison.Ordinal))
                throw new SyncCliUsageException($"Unexpected argument '{option}'.");
            if (!allowed.Contains(option, StringComparer.Ordinal))
                throw new SyncCliUsageException(option == "--yes" || option == "--copy" || option == "--download" ||
                                                 option == "--until-resumed"
                    ? $"The option {option} is not supported by '{command}'."
                    : $"Unknown option '{option}' for '{command}'. Allowed options: " +
                      (allowed.Length == 0 ? "none" : string.Join(", ", allowed)) + ".");
            if (option is "--provenance-log" or "--provenance-prefix")
            {
                if (next >= tokens.Count || tokens[next].StartsWith("--", StringComparison.Ordinal))
                    throw new SyncCliUsageException($"The option {option} requires a value.");
                var values = option == "--provenance-log" ? provenanceLogs : provenancePrefixes;
                values.Add(tokens[next++]); options[option] = values[0]; continue;
            }
            if (option == RepeatableExclude)
            {
                if (next >= tokens.Count || tokens[next].StartsWith("--", StringComparison.Ordinal))
                    throw new SyncCliUsageException($"The option {option} requires a value.");
                excludes.Add(tokens[next++]);
                options[option] = excludes[0];
                continue;
            }
            if (options.ContainsKey(option))
                throw new SyncCliUsageException($"The option {option} was supplied more than once.");
            if (ValueOptions.Contains(option, StringComparer.Ordinal))
            {
                if (next >= tokens.Count || tokens[next].StartsWith("--", StringComparison.Ordinal))
                    throw new SyncCliUsageException($"The option {option} requires a value.");
                options[option] = tokens[next++];
                continue;
            }
            options[option] = null;
        }

        if (command.StartsWith("autosync ", StringComparison.Ordinal))
        {
            if (command is "autosync enable" or "autosync disable" &&
                options.ContainsKey("--pair") == options.ContainsKey("--all"))
                throw new SyncCliUsageException("Choose --pair <exact-id> or --all, not both.");
            if (command == "autosync interval" && (!options.TryGetValue("--minutes", out var intervalText) ||
                !int.TryParse(intervalText, out var interval) || interval is < 1 or > 1440))
                throw new SyncCliUsageException("AutoSync interval requires --minutes 1..1440.");
            if (options.TryGetValue("--after", out var cursorText) &&
                (!long.TryParse(cursorText, out var cursor) || cursor < 0))
                throw new SyncCliUsageException("History requires a nonnegative sequence cursor.");
        }

        if (options.TryGetValue("--timeout-seconds", out var timeoutText) &&
            (!double.TryParse(timeoutText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var timeout) ||
             !double.IsFinite(timeout) || timeout is < 0.001 or > 86400))
            throw new SyncCliUsageException("--timeout-seconds requires a decimal number from 0.001 to 86400, using '.' as the decimal separator.");

        var pairId = options.TryGetValue("--pair", out var pairValue) ? RequireSelector(pairValue, "--pair") : null;
        // Direct execution and a running-app bridge can have different working directories.
        // Resolve no local path implicitly on the receiver; the shared parser is their boundary.
        if (options.TryGetValue("--local", out var localPath) &&
            (string.IsNullOrWhiteSpace(localPath) || !Path.IsPathFullyQualified(localPath)))
            throw new SyncCliUsageException("--local requires an absolute folder path. A relative path cannot " +
                "be resolved against the running application's working directory.");
        if (command is "run retry" or "run retry-plan" or "run verify")
        {
            if (!options.TryGetValue("--run", out var runId)) throw new SyncCliUsageException("An exact --run is required.");
            RequireSelector(runId, "--run");
            if (options.TryGetValue("--choice", out var retryMode) && retryMode is not ("failed" or "unattempted" or "all"))
                throw new SyncCliUsageException("--choice requires failed, unattempted or all.");
        }
        if (command is "activity list" or "activity follow")
        {
            if (options.TryGetValue("--after", out var after) &&
                (!long.TryParse(after, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0))
                throw new SyncCliUsageException("--after requires a nonnegative retained event sequence.");
            if (command == "activity list" && options.ContainsKey("--after") && !options.ContainsKey("--log"))
                throw new SyncCliUsageException("--after requires --log, whose retained sequence is stable across live appends.");
            if (options.TryGetValue("--before", out var before) &&
                (!long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var beforeSequence) || beforeSequence < 1))
                throw new SyncCliUsageException("--before requires a positive retained event sequence.");
            if (options.ContainsKey("--before") && (!options.ContainsKey("--log") ||
                    options.GetValueOrDefault("--order") != "newest"))
                throw new SyncCliUsageException("--before requires --log --order newest to page earlier retained events.");
            if (options.ContainsKey("--after") && options.GetValueOrDefault("--order") == "newest")
                throw new SyncCliUsageException("Use --before with --log --order newest, or --after with forward log/follow commands.");
            if (options.ContainsKey("--log") && options.ContainsKey("--offset"))
                throw new SyncCliUsageException("Use --after, or --order newest --before, rather than --offset for the detailed operation log.");
            if (options.TryGetValue("--categories", out var categories) &&
                (string.IsNullOrWhiteSpace(categories) || categories.Split(',').Any(category =>
                    !SyncActivityCategories.All.Contains(category.Trim()))))
                throw new SyncCliUsageException("--categories accepts: " + string.Join(", ", SyncActivityCategories.All));
            if (options.TryGetValue("--severity", out var severity) && severity is not ("Information" or "Success" or "Warning" or "Error"))
                throw new SyncCliUsageException("--severity requires Information, Success, Warning or Error.");
            if (options.TryGetValue("--entity", out var entity) && entity is not ("File" or "Folder" or "Folder pair" or "Run" or "Account" or "Application" or "Unknown"))
                throw new SyncCliUsageException("--entity requires File, Folder, Folder pair, Run, Account, Application or Unknown.");
            if (options.TryGetValue("--order", out var order) && order is not ("oldest" or "newest"))
                throw new SyncCliUsageException("--order requires oldest or newest.");
            if (options.TryGetValue("--run", out var run)) _ = RequireSelector(run, "--run");
            if (options.TryGetValue("--since", out var since) && (since is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(since, @"(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                throw new SyncCliUsageException("--since requires an ISO timestamp with Z or an explicit UTC offset.");
            if (command == "activity follow" && (!options.TryGetValue("--seconds", out var seconds) ||
                !int.TryParse(seconds, out var duration) || duration is < 1 or > 60))
                throw new SyncCliUsageException("activity follow requires --seconds 1..60.");
        }
        if (command is "recovery list" or "recovery restore")
        {
            // The keyset filters share the activity timestamp vocabulary: an ISO instant with Z or an explicit
            // offset, never a bare local time that two machines could read differently.
            if (options.TryGetValue("--since", out var recoverySince) && (recoverySince is null ||
                !System.Text.RegularExpressions.Regex.IsMatch(recoverySince, @"(Z|[+-]\d{2}:\d{2})$") ||
                !DateTimeOffset.TryParse(recoverySince, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)))
                throw new SyncCliUsageException("--since requires an ISO timestamp with Z or an explicit UTC offset.");
            // Recovery rows name a side by root provider, so the vocabulary is the provider, not a/b.
            if (options.TryGetValue("--side", out var recoverySide) && recoverySide is not ("local" or "dropbox"))
                throw new SyncCliUsageException("For recovery, --side requires local or dropbox.");
            if (command == "recovery restore")
            {
                var exactId = options.GetValueOrDefault("--id");
                var legacyKey = options.GetValueOrDefault("--key");
                if (exactId is not null && legacyKey is not null &&
                    !string.Equals(exactId, legacyKey, StringComparison.Ordinal))
                    throw new SyncCliUsageException("--id and its legacy alias --key name different items; give one target.");
                var filtered = options.ContainsKey("--since") || options.ContainsKey("--path-prefix") ||
                    options.ContainsKey("--action-id") || options.ContainsKey("--side");
                if ((exactId is not null || legacyKey is not null) && filtered)
                    throw new SyncCliUsageException("Choose one exact --id (or legacy --key), or a filter selection, not both.");
                // Never fall through to "every retained row": a bare restore is a usage error.
                if (exactId is null && legacyKey is null && !options.ContainsKey("--since") &&
                    !options.ContainsKey("--path-prefix") && !options.ContainsKey("--action-id"))
                    throw new SyncCliUsageException("recovery restore needs an exact --id (or legacy --key), or at " +
                        "least one of --since, --path-prefix or --action-id. It never restores every retained item by default.");
                if (options.ContainsKey("--after") && (exactId is not null || legacyKey is not null))
                    throw new SyncCliUsageException("--after continues a filtered selection; an exact --id is a single page.");
            }
        }
        if (options.TryGetValue("--provider", out var selectedProvider) &&
            selectedProvider is not (SyncProviders.Dropbox or SyncProviders.GoogleDrive))
            throw new SyncCliUsageException("--provider must be dropbox or google-drive.");
        var accountId = options.TryGetValue("--account", out var accountValue)
            ? RequireSelector(accountValue, "--account")
            : null;
        if (row.RequiresPair && pairId is null)
            throw new SyncCliUsageException($"The command '{command}' requires --pair <exact-pair-id>.");
        if (command == "pairs remove" &&
            (!options.TryGetValue("--archive", out var archiveDirectory) ||
             string.IsNullOrWhiteSpace(archiveDirectory) || archiveDirectory.Length > 32_768))
            throw new SyncCliUsageException("'pairs remove' requires --archive <archive-directory>.");
        if (command == "files upload")
        {
            foreach (var boundedPath in new[] { "--source", "--destination" })
                if (options.TryGetValue(boundedPath, out var boundedValue) &&
                    (boundedValue is null || boundedValue.Length > SyncExactFileUploadRules.MaximumPathCharacters))
                    throw new SyncCliUsageException($"The option {boundedPath} requires a value of at most " +
                        SyncExactFileUploadRules.MaximumPathCharacters + " characters.");
            if (accountId is null)
                throw new SyncCliUsageException("'files upload' requires --account <exact-id>.");
            if (!options.TryGetValue("--source", out var uploadSource) || string.IsNullOrWhiteSpace(uploadSource) ||
                !Path.IsPathFullyQualified(uploadSource))
                throw new SyncCliUsageException("'files upload' requires --source <absolute-local-file>; a relative " +
                    "path cannot be resolved against the running application's working directory.");
            if (!options.TryGetValue("--destination", out var uploadDestination) ||
                !SyncExactFileUploadRules.TryParseDestination(uploadDestination, out _, out _))
                throw new SyncCliUsageException("'files upload' requires --destination " +
                    "<ns:<namespace-id>//<absolute-path>>, for example ns:123456//Documents/report.csv.");
            if (!options.TryGetValue("--bytes", out var uploadBytes) ||
                !long.TryParse(uploadBytes, NumberStyles.None, CultureInfo.InvariantCulture, out var expectedBytes) ||
                expectedBytes < 0)
                throw new SyncCliUsageException("'files upload' requires --bytes <expected-byte-length>, a " +
                    "nonnegative integer.");
            if (!options.TryGetValue("--sha256", out var uploadSha) || !SyncExactFileUploadRules.IsSha256(uploadSha))
                throw new SyncCliUsageException("'files upload' requires --sha256 <expected-sha256>, 64 lowercase " +
                    "hexadecimal characters.");
        }
        if (command == "rewind apply")
        {
            foreach (var option in new[] { "--key", "--source", "--destination" })
                if (!options.TryGetValue(option, out var identity) ||
                    string.IsNullOrWhiteSpace(identity) || identity.Length > 256)
                    throw new SyncCliUsageException("rewind apply requires the exact reviewed --key session, " +
                        "--source local preview scan and --destination Dropbox preview scan returned by rewind preview.");
        }
        if (command == "updates apply" && (!options.TryGetValue("--update", out var exactVersion) ||
            exactVersion is null || !System.Text.RegularExpressions.Regex.IsMatch(exactVersion,
                @"^[0-9]+\.[0-9]+\.[0-9]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)))
            throw new SyncCliUsageException("updates apply requires --update <exact-version>, for example 0.3.35.");
        if (command == "dates restore" && string.IsNullOrWhiteSpace(options.GetValueOrDefault("--audit-log")))
            throw new SyncCliUsageException("dates restore requires --audit-log <file>.");
        if (command is "dates repair" or "dates report-mismatch")
        {
            _ = V2.DateProvenanceLog.Sources(provenanceLogs, provenancePrefixes);
            DateOnly? Day(string option)
            {
                if (!options.TryGetValue(option, out var text)) return null;
                if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    throw new SyncCliUsageException(option + " requires a local calendar date in yyyy-MM-dd format.");
                return day;
            }
            _ = V2.DateRepair.UtcRange(Day("--local-date"), V2.DateRepairBound.Parse(options.GetValueOrDefault("--from")), V2.DateRepairBound.Parse(options.GetValueOrDefault("--to")), TimeZoneInfo.Local);
        }
        if (command == "dates restore" && !options.ContainsKey("--dry-run") && !options.ContainsKey("--yes"))
            throw new SyncCliUsageException("Use dates restore --audit-log <file> --dry-run to review, or add --yes to restore dates.");
        if (command == "dates repair" && !options.ContainsKey("--dry-run") && !options.ContainsKey("--yes"))
            throw new SyncCliUsageException("Use dates repair --pair <id> --dry-run to review, or add --yes to repair dates.");
        // Recovery restore defaults to a dry run, so --yes is the confirmation for an effect rather than a
        // parse-time requirement. The dispatcher still refuses a v1 restore without --yes before any write.
        if (row.RequiresYes && command != "recovery restore" &&
            (row.MutationOption is null || options.ContainsKey(row.MutationOption)) &&
            !options.ContainsKey("--yes"))
            throw new SyncCliUsageException($"The command '{command}' requires --yes. The CLI never auto-approves a " +
                "first sync, conflict, deletion, ownership or credential decision. Run: " +
                $"{SyncCli.ExecutableName} --cli {row.Usage}");
        if (command is "accounts show" or "accounts reauthorize" && accountId is null)
            throw new SyncCliUsageException(command + " requires --account <exact-id>.");
        // Recovery pages share the engine's own 1..500 bound; every other command keeps the activity bound.
        var limitMaximum = command is "recovery list" or "recovery restore"
            ? V2.RecoveryQuery.MaximumLimit
            : SyncCli.ActivityMaximumLimit;
        var limit = options.TryGetValue("--limit", out var limitValue)
            ? ParseBounded(limitValue!, "--limit", 1, limitMaximum)
            : SyncCli.ActivityDefaultLimit;
        // `pairs create --settings` and `pairs edit --settings` may carry the shared endpoint-authoring
        // document. It is recognised and validated here, and it is never mixed with the legacy selectors,
        // so a malformed or mixed document fails before product state is opened or a provider is reached.
        // The value is only parsed for recognition once it is inside the existing --settings bound, so an
        // oversized value keeps its ordinary refusal and is never parsed here.
        var endpointSettings = options.GetValueOrDefault("--settings");
        var endpointAuthoring = endpointSettings is { Length: <= 32_768 } &&
            (command is "pairs create" or "pairs edit") && SyncCliEndpointAuthoring.IsEndpointDocument(endpointSettings);
        if (endpointAuthoring)
        {
            if (options.ContainsKey("--account") || options.ContainsKey("--path") || options.ContainsKey("--local"))
                throw new SyncCliUsageException("The --settings endpoint document names both endpoints, so " +
                    "--account, --path and --local must be left out; the CLI never mixes the document with the " +
                    "legacy selectors.");
            _ = SyncCliEndpointAuthoring.Read(endpointSettings);
        }
        else if (command == "pairs create" && options.ContainsKey("--settings"))
            throw new SyncCliUsageException("'pairs create' accepts --settings only as the endpoint document: " +
                "{\"left\":{...},\"right\":{...},\"direction\":TwoWay|UploadOnly|DownloadOnly}.");
        var extendedPair = !endpointAuthoring && (command == "pairs duplicate" || command == "pairs edit" &&
            new[] { "--settings", "--local", "--path" }.Any(options.ContainsKey));
        if ((command.StartsWith("storage ", StringComparison.Ordinal) && command is not ("storage devices" or "storage inventory" or "storage inspect") || extendedPair ||
             command is "conflicts select" or "conflicts resolve-selection" or "recovery revisions" ||
             command == "recovery restore" && options.ContainsKey("--path")) && accountId is null)
            throw new SyncCliUsageException(command + " requires --account <exact-id>.");
        if ((extendedPair || command.StartsWith("storage ", StringComparison.Ordinal) && row.Mutating) && !options.ContainsKey("--generation"))
            throw new SyncCliUsageException(command + " requires --generation <policy-revision>.");
        if (command == "storage inspect" && (!options.ContainsKey("--local") || (pairId is null) != (accountId is null)))
            throw new SyncCliUsageException("storage inspect requires --local and either both --pair/--account or neither.");
        if (options.ContainsKey("--enable") && options.ContainsKey("--disable"))
            throw new SyncCliUsageException("Choose --enable or --disable, not both.");
        if (command == "accounts shared-drives" && (accountId is null ||
            !options.ContainsKey("--enable") && !options.ContainsKey("--disable")))
            throw new SyncCliUsageException("accounts shared-drives requires --account and exactly one of --enable or --disable.");
        if (command == "jobs watch")
        {
            if (!options.ContainsKey("--key") || !options.TryGetValue("--seconds", out var seconds))
                throw new SyncCliUsageException("jobs watch requires --key and --seconds 1..60.");
            ParseBounded(seconds!, "--seconds", 1, 60);
        }
        var offset = options.TryGetValue("--offset", out var offsetValue)
            ? ParseBounded(offsetValue!, "--offset", 0, int.MaxValue)
            : 0;
        long? generation = null;
        if (options.TryGetValue("--generation", out var generationValue))
        {
            var retainedNoticeChoice = command == "conflicts resolve" &&
                V2.NoticeChoices.IsExplicitNoticeChoice(options.GetValueOrDefault("--choice") ?? string.Empty);
            if (!long.TryParse(generationValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < (retainedNoticeChoice ? 0 : 1))
                throw new SyncCliUsageException(retainedNoticeChoice
                    ? "The option --generation requires a non-negative integer for a retained-version notice."
                    : "The option --generation requires a positive integer.");
            generation = parsed;
        }
        int? minutes = null;
        if (options.TryGetValue("--minutes", out var minutesValue))
        {
            if (!int.TryParse(minutesValue, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed is < 1 or > 43_200)
                throw new SyncCliUsageException("The option --minutes requires an integer between 1 and 43200.");
            minutes = parsed;
        }
        // An exact deadline always carries an explicit offset; a bare local time is never accepted.
        if (options.TryGetValue("--until", out var untilValue) &&
            !PauseSchedule.TryParseDeadline(untilValue, DateTimeOffset.UtcNow, out _, out var untilReason))
            throw new SyncCliUsageException(untilReason);
        foreach (var bounded in new[] { "--path", "--key", "--search", "--choice", "--rules", "--settings",
                     "--update", "--selection", "--availability", "--id", "--path-prefix", "--action-id" })
            if (options.TryGetValue(bounded, out var value) && (value is null || value.Length > 32_768))
                throw new SyncCliUsageException($"The option {bounded} requires a value of at most 32768 characters.");

        return new SyncCliInvocation(row.Kind, command, json, pairId, accountId,
            options.GetValueOrDefault("--path"), options.GetValueOrDefault("--key"),
            options.GetValueOrDefault("--search"), options.GetValueOrDefault("--choice"),
            options.GetValueOrDefault("--rules"), options.GetValueOrDefault("--settings"),
            options.GetValueOrDefault("--update"), options.GetValueOrDefault("--selection"),
            options.GetValueOrDefault("--availability"), limit, offset, generation,
            options.ContainsKey("--yes"), options.ContainsKey("--copy") || options.ContainsKey("--as-copy"),
            options.ContainsKey("--download"),
            options.ContainsKey("--until-resumed"), null, options, row.RequiresPair, minutes,
            options.GetValueOrDefault("--local"), options.GetValueOrDefault("--name"),
            options.TryGetValue("--priority", out var priorityText2) && priorityText2 is not null
                ? ParseBounded(priorityText2, "--priority", -100, 100)
                : null,
            options.ContainsKey("--enable"), options.ContainsKey("--disable"),
            options.GetValueOrDefault("--description"), options.GetValueOrDefault("--choices"),
            options.ContainsKey("--allow-recommended"), excludes, verbose, provenanceLogs, provenancePrefixes);
    }

    private static string RequireSelector(string? value, string option)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 200)
            throw new SyncCliUsageException($"The option {option} requires an exact identifier of 1 to 200 characters.");
        foreach (var character in value)
            if (char.IsWhiteSpace(character) || char.IsControl(character))
                throw new SyncCliUsageException($"The option {option} requires one exact identifier without " +
                    "whitespace; names, indexes and prefixes are never accepted.");
        return value;
    }

    internal static bool MutatesProductState(SyncCliInvocation invocation)
    {
        // Recovery restore defaults to a dry run: it is a mutation only when --yes confirms and --dry-run is absent
        // (the explicit dry run always wins over a confirmation).
        if (invocation.Command == "recovery restore")
            return invocation.Yes && !invocation.Flag("--dry-run");
        return Rows.Any(row => row.Command == invocation.Command && row.Mutating &&
            (row.MutationOption is null || invocation.Flag(row.MutationOption)));
    }

    private static int ParseBounded(string value, string option, int minimum, int maximum)
    {
        if (!int.TryParse(value, minimum < 0 ? NumberStyles.AllowLeadingSign : NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < minimum || parsed > maximum)
            throw new SyncCliUsageException($"The option {option} requires an integer between {minimum} and {maximum}.");
        return parsed;
    }

    public static async Task<object> DispatchAsync(ISyncCliRuntime runtime, SyncCliInvocation invocation,
        TextWriter standardError, CancellationToken cancellationToken)
    {
        // The command's verbosity lives on the run/log instance, never in process-global state.
        if (runtime is V2.V2CliRuntime v2Runtime) v2Runtime.Verbose = invocation.Verbose;
        void Progress(string message)
        {
            // Retained events are already product-projected, redacted JSON. Text
            // sanitation would truncate the record and change space-bearing paths.
            // The existing bridge fragments complete lines without losing bytes.
            if (invocation.Command == "activity follow")
            {
                standardError.WriteLine("Sync Shark: " + message);
                return;
            }
            var safe = SyncActivityText.Safe(message);
            if (safe.Length > 0) standardError.WriteLine(FormatProgress(safe));
        }

        // The Dropbox group is data-driven and reaches its own seam, so it is dispatched before the
        // shared operations surface is required. Every other command keeps its existing order.
        if (DropboxCliCatalog.Owns(invocation.Command))
            return await DropboxCliDispatcher.DispatchAsync(runtime as IDropboxCliAccess ??
                throw new SyncCliUnsupportedException(
                    "This command needs the Dropbox account surface, which is unavailable in this process."),
                invocation, Progress, cancellationToken);

        var operations = runtime as ISyncCliOperations
                         ?? throw new SyncCliUnsupportedException(
                             "This command needs the full Sync Shark operations surface, which is unavailable in " +
                             "this process.");
        switch (invocation.Command)
        {
            case "status":
                return await runtime.LoadStatusAsync(cancellationToken);
            case "accounts list":
                return await runtime.LoadAccountsAsync(cancellationToken);
            case "accounts show":
                return await operations.LoadAccountAsync(invocation.AccountId ??
                    throw new SyncCliUsageException("The command 'accounts show' requires --account <exact-id>."),
                    cancellationToken);
            case "accounts check":
                return await operations.CheckAccountAsync(invocation.AccountId, cancellationToken);
            case "accounts connect":
                return await operations.ConnectAccountAsync(false, invocation.AccountId, Progress, cancellationToken,
                    invocation.Value("--provider") ?? SyncProviders.Dropbox);
            case "accounts reauthorize":
                return await operations.ConnectAccountAsync(true, RequireAccount(invocation), Progress, cancellationToken);
            case "accounts disconnect":
                return await operations.DisconnectAccountAsync(RequireAccount(invocation), cancellationToken);
            case "accounts pending":
                return await operations.LoadPendingAuthorizationAsync(cancellationToken);
            case "accounts cancel":
                return await operations.CancelPendingAuthorizationAsync(RequireKey(invocation), cancellationToken);
            case "pairs list":
                return await runtime.LoadPairsAsync(cancellationToken);
            case "pairs show":
                return await runtime.LoadPairAsync(invocation.PairId!, cancellationToken);
            case "pairs pause":
                return new { pairId = invocation.PairId, paused = await operations.SetPairPausedAsync(invocation.PairId!, true, cancellationToken) };
            case "pairs resume":
                return new { pairId = invocation.PairId, paused = await operations.SetPairPausedAsync(invocation.PairId!, false, cancellationToken),
                    message = "Pair resumed. A running app observes CLI resume on its next safety-interval poll; in-app resume wakes it immediately." };
            case "pairs enable":
                return await operations.SetPairEnabledAsync(invocation.PairId!, true, cancellationToken, Progress);
            case "pairs disable":
                return await operations.SetPairEnabledAsync(invocation.PairId!, false, cancellationToken, Progress);
            case "pairs select":
                return await operations.SelectPairAsync(invocation.PairId!, cancellationToken);
            case "pairs retire":
                return await operations.RetirePairAsync(invocation.PairId!, cancellationToken);
            case "pairs remove":
                return await operations.RemovePairAsync(invocation.PairId!, invocation.Value("--archive")!,
                    invocation.Flag("--drop-unfinished"), cancellationToken);
            case "dates restore":
                if (runtime is not V2.V2CliRuntime v2Restore)
                    throw new SyncCliUnsupportedException("Date restore is available in core v2.");
                return await v2Restore.RestoreDatesAsync(invocation.Value("--audit-log")!, invocation.Flag("--dry-run"), cancellationToken);
            case "dates repair":
            case "dates report-mismatch":
                if (runtime is not V2.V2CliRuntime v2Dates)
                    throw new SyncCliUnsupportedException("Date repair is available in core v2. Select v2 and retry.");
                return await v2Dates.RepairDatesAsync(invocation.PairId!, invocation.Command == "dates report-mismatch" || invocation.Flag("--dry-run"), cancellationToken,
                    invocation.Value("--local-date") is { } date ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    invocation.Value("--after") ?? "",
                    V2.DateRepairBound.Parse(invocation.Value("--from")), V2.DateRepairBound.Parse(invocation.Value("--to")),
                    invocation.Value("--exclude-list"), invocation.Command == "dates report-mismatch",
                    V2.DateProvenanceLog.Sources(invocation.ProvenanceLogs, invocation.ProvenancePrefixes));
            case "pairs rescan":
                return await operations.RescanPairAsync(invocation.PairId!, invocation.Value("--side"),
                    cancellationToken);
            case "pairs recover-control":
                return await operations.RecoverRetainedControlAsync(invocation.PairId!,
                    invocation.Value("--source") ?? "", invocation.Value("--source-sha256") ?? "",
                    invocation.Value("--local-root") ?? "", invocation.Value("--draft"),
                    Progress, cancellationToken);
            case "pairs recover-publication":
                return await operations.RecoverInterruptedPublicationAsync(invocation.PairId!, cancellationToken);
            case "activity list":
                return await runtime.LoadActivityAsync(invocation.AccountId, invocation.Limit, invocation.Offset,
                    cancellationToken, new SyncActivityQuery(invocation.Value("--categories")?.Split(',')
                        .Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray(),
                        invocation.Value("--severity"), invocation.Value("--entity"), invocation.Value("--run"),
                        invocation.Value("--order") == "oldest" || invocation.Flag("--log"),
                        invocation.Value("--after") is { } after ? long.Parse(after, CultureInfo.InvariantCulture) : null,
                        invocation.Flag("--log"),
                        ThroughSequence: invocation.Value("--before") is { } before ? long.Parse(before, CultureInfo.InvariantCulture) - 1 : null,
                        NewestLogFirst: invocation.Flag("--log") && invocation.Value("--order") == "newest"),
                    invocation.Search, invocation.Value("--filter"),
                    invocation.PairId, invocation.Value("--type"), invocation.Value("--since") is { } since
                        ? DateTimeOffset.Parse(since, CultureInfo.InvariantCulture) : null);
            case "activity follow":
                // These are serialized retained events, with sanitized bounded text and
                // JSON-escaped controls, exactly as in activity list. The one-line status
                // formatter would truncate the record mid-JSON and hide its failure cause.
                return await operations.FollowActivityAsync(invocation,
                    record => standardError.WriteLine("Sync Shark: " + record), cancellationToken);
            case "queue list":
                return await operations.LoadQueueAsync(invocation.AccountId, invocation.PairId, invocation.Limit,
                    invocation.Offset, cancellationToken);
            case "attention list":
                return await operations.LoadAttentionAsync(cancellationToken);
            case "runs list":
                return await operations.LoadRunsAsync(invocation.Limit, cancellationToken);
            case "conflicts list":
            case "accounts shared-drives":
            case "jobs status":
            case "jobs watch":
            case "conflicts select":
            case "conflicts resolve-selection":
            case "pairs duplicate":
            case "storage devices":
            case "storage inventory":
            case "storage inspect":
            case "storage show":
            case "storage files":
            case "storage default":
            case "storage root":
            case "storage folders":
            case "storage file":
            case "storage reset-file":
            case "storage enable":
            case "recovery revisions":
                return await operations.ExecuteSurfaceAsync(invocation, cancellationToken, Progress);
            case "conflicts show":
                return await operations.LoadConflictAsync(invocation.PairId!, RequirePath(invocation),
                    invocation.AccountId, cancellationToken);
            case "conflicts resolve":
            {
                // Retry/retained-version choices are notice actions even without --name or --offset.
                var noticeChoice = invocation.Choice is { } selected &&
                    (V2.NoticeChoices.IsRetryChoice(selected.ToLowerInvariant()) ||
                     V2.NoticeChoices.IsRetainedVersionChoice(selected.ToLowerInvariant()));
                if (invocation.Name is not null || invocation.Options?.ContainsKey("--offset") == true ||
                    invocation.Value("--page-token") is not null || noticeChoice)
                    // Key-only pair notices carry an empty path: Windows PowerShell 5.1 drops a "" argument
                    // to a native exe, and the v2 runtime itself enforces --path for item notices.
                    return await operations.ResolveNoticeAsync(invocation.PairId!, invocation.Path ?? string.Empty,
                        RequireChoice(invocation), invocation.Key, invocation.Generation, invocation.AccountId,
                        cancellationToken, new NoticeActionInput(invocation.Name, invocation.Offset, invocation.Value("--page-token")));
                return await operations.ResolveConflictAsync(invocation.PairId!, invocation.Path ?? string.Empty,
                    RequireChoice(invocation), invocation.Key, invocation.Generation, invocation.AccountId,
                    cancellationToken, Progress);
            }
            case "recovery list":
                // The keyset filters are a core v2 contract only; v1 keeps its legacy catalog shape and never
                // silently ignores a filter the caller asked for.
                if (runtime is not V2.V2CliRuntime &&
                    (invocation.Flag("--since") || invocation.Flag("--path-prefix") || invocation.Flag("--side") ||
                     invocation.Flag("--action-id") || invocation.Flag("--after")))
                    throw new SyncCliUnsupportedException(
                        "Bounded recovery paging is available in core v2. Nothing was read.");
                return await operations.ExecuteSurfaceAsync(invocation, cancellationToken);
            case "recovery usage":
                var recovery = await operations.LoadRecoveryAsync(invocation.PairId!, null, 1, cancellationToken);
                return new SyncCliRecoveryUsageData(recovery.PairId, recovery.ItemCount, recovery.TotalSize,
                    recovery.Items.FirstOrDefault()?.RetainedUtc, recovery.CleanupEligibleCount,
                    recovery.RetentionDays);
            case "recovery restore":
                // Core v2 owns the bounded selection surface: one exact --id (legacy --key) or one keyset page of
                // filters, dry run unless --yes. The v1 route below keeps only its confirmed exact-key shape, so an
                // unconfirmed or new-shaped v1 request can never reach a write.
                if (runtime is V2.V2CliRuntime v2Recovery)
                    return await v2Recovery.RestoreRecoveryCommandAsync(invocation, cancellationToken);
                if (!invocation.Yes)
                    throw new SyncCliUsageException("The v1 recovery restore requires --yes to restore. Nothing was restored.");
                if (invocation.Flag("--dry-run") || invocation.Flag("--as-copy") || invocation.Value("--id") is not null ||
                    invocation.Flag("--since") || invocation.Flag("--path-prefix") || invocation.Flag("--action-id") ||
                    invocation.Flag("--after") || invocation.Flag("--side") || invocation.Flag("--limit"))
                    throw new SyncCliUnsupportedException(
                        "Bounded recovery selection is available in core v2. Nothing was restored.");
                if (invocation.Path is not null || invocation.AccountId is not null)
                    return await operations.ExecuteSurfaceAsync(invocation, cancellationToken);
                return await operations.RestoreRecoveryAsync(invocation.PairId!, RequireKey(invocation),
                    invocation.Copy, cancellationToken);
            case "recovery cleanup":
                return await operations.CleanupRecoveryAsync(invocation.PairId!, RequireKey(invocation),
                    cancellationToken);
            case "decisions list":
                return await operations.LoadDecisionsAsync(invocation.PairId!, invocation.Limit, invocation.Offset,
                    cancellationToken);
            case "decisions resolve":
                return await operations.ResolveDecisionAsync(invocation.PairId!, RequirePath(invocation),
                    RequireOption(invocation, "--keep"), RequireOption(invocation, "--token"), Progress,
                    cancellationToken);
            case "plan":
                return await runtime.LoadPlanAsync(invocation.PairId!, cancellationToken,
                    invocation.Limit, invocation.Offset);
            case "scan":
                return await operations.ScanAsync(invocation.PairId!, Progress, cancellationToken);
            case "first-sync prepare":
                return await operations.PrepareFirstSyncAsync(invocation.PairId!, Progress, cancellationToken,
                    invocation.Choices);
            case "first-sync repair-moves":
                return await operations.RepairCompletedCopyMovesAsync(invocation.PairId!, Progress, cancellationToken);
            case "first-sync approve":
                return await operations.ApproveFirstSyncAsync(invocation.PairId!, cancellationToken);
            case "first-sync apply":
                return await operations.ApplyFirstSyncAsync(invocation.PairId!, invocation.Choices,
                    invocation.AllowRecommended, invocation.Generation, Progress, cancellationToken,
                    invocation.Key);
            case "run batch":
                return await operations.RunBatchAsync(invocation.Selection ??
                    throw new SyncCliUsageException("run batch requires --selection <json-array-of-exact-pair-ids>."), Progress, cancellationToken);
            case "run":
                var run = await runtime.RunPairAsync(invocation.PairId!, Progress, cancellationToken);
                if (run.Stopped)
                    throw new SyncCliException("canceled",
                        $"The run for pair '{run.PairName}' ({run.PairId}) stopped before it finished.",
                        SyncCliExitCode.Canceled, data: run);
                if (run.Blocked > 0 || run.Conflicts > 0 || run.Failed > 0 ||
                    run.NotAttempted > 0 || run.NeedsVerification > 0)
                    throw new SyncCliReviewException($"The run for pair '{run.PairName}' ({run.PairId}) finished " +
                        $"with {run.Blocked} blocked item(s), {run.Conflicts} conflict(s), " +
                        $"{run.Failed} failed, {run.NotAttempted} not attempted and {run.NeedsVerification} needing verification. " +
                        "Open the retained run's operation log or attention list before retrying.", run);
                return run;
            case "run pause":
                return await operations.SetRunPauseAsync(true, cancellationToken);
            case "run verify":
                return await operations.VerifyRunAsync(invocation.Value("--run")!, Progress, cancellationToken);
            case "run retry":
            case "run retry-plan":
                var retry = await operations.RunRetryAsync(invocation.Value("--run")!, invocation.Value("--selection"),
                    invocation.Value("--choice") ?? "failed", invocation.Command == "run retry-plan", Progress, cancellationToken, invocation.PairId);
                return retry is SyncRunRetryPlan retryPlan
                    ? SyncCliRunRetryPlanData.Page(retryPlan, invocation.Limit, invocation.Offset) : retry;
            case "run resume":
                return await operations.SetRunPauseAsync(false, cancellationToken);
            case "cancel":
                return await operations.CancelRunAsync(RequireKey(invocation), cancellationToken);
            case "pause":
                return await SetPauseAsync(operations, invocation, true, cancellationToken);
            case "resume":
                return await SetPauseAsync(operations, invocation, false, cancellationToken);
            case "settings show":
                return await runtime.LoadSettingsAsync(cancellationToken);
            case "settings set":
                return await operations.SaveSettingsAsync(invocation.Settings ??
                    throw new SyncCliUsageException("The command 'settings set' requires --settings <json>."),
                    cancellationToken);
            case "ignores list":
                return await operations.LoadIgnoresAsync(invocation.PairId!, cancellationToken);
            case "ignores set":
                return await operations.SaveIgnoresAsync(invocation.PairId!, invocation.Rules ??
                    throw new SyncCliUsageException("The command 'ignores set' requires --rules <spec>."),
                    cancellationToken);
            case "folders tree":
                return await operations.LoadFoldersAsync(invocation.PairId!, cancellationToken);
            case "folders preview-include":
                return await operations.PreviewIncludeAsync(invocation.PairId!, RequirePath(invocation), cancellationToken);
            case "folders include":
                return await operations.SetSelectionAsync(invocation.PairId!, RequirePath(invocation), "include",
                    cancellationToken);
            case "folders exclude":
                return await operations.SetSelectionAsync(invocation.PairId!, RequirePath(invocation), "exclude",
                    cancellationToken);
            case "folders create-local":
                if (invocation.LocalRoot is null || invocation.Name is null)
                    throw new SyncCliUsageException("The command 'folders create-local' requires " +
                        "--local <existing-parent> and --name <single-name>. Nothing was created.");
                return await operations.CreateLocalFolderAsync(invocation.LocalRoot, invocation.Name,
                    cancellationToken);
            case "availability set":
                if (invocation.Availability is null || invocation.Path is null)
                    throw new SyncCliUsageException(
                        "The command 'availability set' requires --path and --availability.");
                return await operations.SetAvailabilityAsync(invocation.PairId!, invocation.Path,
                    invocation.Availability, cancellationToken, Progress);
            case "updates check":
                return await operations.UpdatesAsync(invocation.Download, cancellationToken);
            case "explorer status":
                return await operations.LoadExplorerStatusAsync(cancellationToken);
            // DIAG354-4: the diagnostics command is a read command, and it runs the same shared in-app
            // read-only diagnostic action the Diagnostics page runs. It is dispatched through the one
            // existing running-application adapter rather than its own implementation.
            case "diagnostics run":
            case "updates verify":
            case "locations open":
            case "storage disable":
            case "explorer restore":
            case "app uninstall":
            case "explorer roots":
            case "explorer inspect":
            case "explorer remove":
            case "explorer undo":
            case "explorer release":
            case "sharing open":
            case "sharing manage":
            case "file-requests open":
                return await operations.ExecuteAppActionAsync(invocation, cancellationToken);
            case "app status":
                return await operations.ExecuteAppActionAsync(invocation, cancellationToken);
            case "app show":
                return await operations.ShowApplicationAsync(cancellationToken);
            case "app exit":
                return await operations.ExitApplicationAsync(cancellationToken);
            case "rewind checkpoint":
                return await operations.RewindCheckpointAsync(invocation.PairId!, Progress, cancellationToken);
            case "rewind preview":
                return await operations.RewindPreviewAsync(invocation.PairId!, cancellationToken);
            case "rewind apply":
                return await operations.RewindApplyAsync(invocation.PairId!,
                    new SyncRewindApplyConfirmation(invocation.Key!, invocation.Value("--source")!,
                        invocation.Value("--destination")!), Progress, cancellationToken);
            case "rewind resume":
                return await operations.RewindResumeAsync(invocation.PairId!, cancellationToken);
            case "updates apply":
                if (invocation.Path is not null)
                    return await operations.ExecuteAppActionAsync(invocation, cancellationToken);
                return await operations.UpdatesApplyAsync(invocation.Update!, cancellationToken);
            case "discovery configure":
            case "discovery restore":
            case "discovery remove":
                return await operations.ConfigureDiscoveryAsync(invocation.Command switch
                {
                    "discovery configure" => "Configure", "discovery restore" => "Restore", _ => "Remove"
                }, cancellationToken);
            case "files upload":
                return await operations.UploadExactFileAsync(new SyncExactFileUploadRequest(
                    RequireAccount(invocation),
                    invocation.Value("--source") ?? throw new SyncCliUsageException(
                        "The command 'files upload' requires --source <absolute-local-file>."),
                    invocation.Value("--destination") ?? throw new SyncCliUsageException(
                        "The command 'files upload' requires --destination <namespace-qualified-path>."),
                    invocation.Value("--bytes") is { } exactBytesText &&
                    long.TryParse(exactBytesText, NumberStyles.None, CultureInfo.InvariantCulture, out var exactBytes)
                        ? exactBytes
                        : throw new SyncCliUsageException(
                            "The command 'files upload' requires --bytes <expected-byte-length>."),
                    invocation.Value("--sha256") ?? throw new SyncCliUsageException(
                        "The command 'files upload' requires --sha256 <expected-sha256>.")),
                    Progress, cancellationToken);
            case "pairs folders":
                return await operations.LoadRemoteFoldersAsync(RequireAccount(invocation), invocation.Path,
                    invocation.Search, cancellationToken);
            case "pairs create":
                if (invocation.Settings is not null && invocation.Excludes is { Count: > 0 })
                    throw new SyncCliUsageException("The option --exclude is not supported by 'pairs create' " +
                        "with --settings. Nothing was created.");
                if (invocation.Settings is not null)
                    return await operations.ExecuteSurfaceAsync(invocation, cancellationToken, Progress);
                if (invocation.Path is null || invocation.LocalRoot is null)
                    throw new SyncCliUsageException(
                        "The command 'pairs create' requires either --settings <endpoint-json> or both " +
                        "--path <remote-path> and --local <local-root>.");
                return await operations.CreatePairAsync(RequireAccount(invocation), invocation.Path,
                    invocation.LocalRoot, invocation.Name, invocation.Priority, invocation.Enable,
                    invocation.Excludes ?? [], cancellationToken, Progress);
            case "pairs edit":
                if (invocation.Settings is not null || invocation.LocalRoot is not null || invocation.Path is not null)
                    return await operations.ExecuteSurfaceAsync(invocation, cancellationToken, Progress);
                return await operations.EditPairAsync(invocation.PairId!, invocation.Name, invocation.Priority,
                    invocation.Enable ? true : invocation.Disable ? false : null, cancellationToken, Progress);
            case "sharing list":
                return await operations.LoadSharingAsync(RequireAccount(invocation), Namespace(invocation),
                    invocation.Limit, invocation.Offset, cancellationToken);
            case "sharing change":
                return await operations.ChangeSharedLinkAsync(RequireAccount(invocation), Namespace(invocation),
                    RequireKey(invocation), RequireSettings(invocation), cancellationToken);
            case "sharing create":
                if (invocation.Path is null || invocation.Choice is null)
                    throw new SyncCliUsageException(
                        "The command 'sharing create' requires --path and --choice <audience>.");
                return await operations.CreateSharedLinkAsync(RequireAccount(invocation), invocation.Path,
                    invocation.Choice, cancellationToken);
            case "sharing revoke":
                return await operations.RevokeSharedLinkAsync(RequireAccount(invocation), Namespace(invocation), RequireKey(invocation),
                    cancellationToken);
            case "sharing revoke-selected":
                return await operations.RevokeSharedLinksAsync(RequireSettings(invocation), cancellationToken);
            case "file-requests list":
                return await operations.LoadFileRequestsAsync(RequireAccount(invocation), Namespace(invocation),
                    invocation.Limit, invocation.Offset, cancellationToken);
            case "file-requests edit":
                return await operations.EditFileRequestAsync(RequireAccount(invocation), Namespace(invocation),
                    RequireKey(invocation), RequireSettings(invocation), cancellationToken);
            case "file-requests create":
                if (invocation.Name is null || invocation.Path is null)
                    throw new SyncCliUsageException(
                        "The command 'file-requests create' requires --name <title> and --path <destination>.");
                return await operations.CreateFileRequestAsync(RequireAccount(invocation), Namespace(invocation), invocation.Name,
                    invocation.Path, invocation.Description, cancellationToken);
            case "file-requests close":
                return await operations.CloseFileRequestAsync(RequireAccount(invocation), Namespace(invocation), RequireKey(invocation),
                    cancellationToken);
            case "discovery status":
                return await operations.LoadDiscoveryStatusAsync(cancellationToken);
            case "discovery check":
                return await operations.CheckDiscoveryAsync(invocation.PairId!, cancellationToken);
            default:
                throw new SyncCliUsageException($"The command '{invocation.Command}' is not implemented.");
        }
    }

    private static string RequireAccount(SyncCliInvocation invocation) =>
        invocation.AccountId ?? throw new SyncCliUsageException(
            $"The command '{invocation.Command}' requires --account <exact-id>.");

    private static string RequirePath(SyncCliInvocation invocation) =>
        invocation.Path ?? throw new SyncCliUsageException(
            $"The command '{invocation.Command}' requires --path <relative-path>.");

    private static string RequireKey(SyncCliInvocation invocation) =>
        invocation.Key ?? throw new SyncCliUsageException(
            $"The command '{invocation.Command}' requires --key <exact-retained-key>.");

    private static string RequireOption(SyncCliInvocation invocation, string option) =>
        invocation.Value(option) ?? throw new SyncCliUsageException(
            $"The command '{invocation.Command}' requires {option} <value>.");

    private static string RequireSettings(SyncCliInvocation invocation) =>
        string.IsNullOrWhiteSpace(invocation.Settings)
            ? throw new SyncCliUsageException($"The command '{invocation.Command}' requires --settings <json>.")
            : invocation.Settings;

    // Namespace selection travels inside the settings object so the invocation record stays untouched; an
    // unknown or foreign namespace is refused by the operations layer against the exact account.
    private static string? Namespace(SyncCliInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.Settings)) return null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(invocation.Settings);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty("namespace", out var value) &&
                   value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new SyncCliUsageException("The --settings value must be a JSON object.");
        }
    }

    private static string RequireChoice(SyncCliInvocation invocation)
    {
        var choice = invocation.Choice ?? throw new SyncCliUsageException(
            "The command 'conflicts resolve' requires --choice <value>.");
        if (!ChoiceNames.Contains(choice, StringComparer.OrdinalIgnoreCase) && !V2.NoticeChoices.IsExplicitNoticeChoice(choice))
            throw new SyncCliUsageException("The --choice value must be one of: " +
                string.Join(", ", ChoiceNames) + ".");
        return choice;
    }

    private static async Task<object> SetPauseAsync(ISyncCliOperations operations, SyncCliInvocation invocation,
        bool paused, CancellationToken cancellationToken)
    {
        if (invocation.PairId is not null)
        {
            // Pair-level pause is the product's enabled state: a disabled pair takes part in no automatic or
            // manual run, and the change is visible in 'pairs list'.
            return await operations.SetPairEnabledAsync(invocation.PairId, !paused, cancellationToken);
        }
        var scope = invocation.AccountId is not null ? PauseScopes.Account : PauseScopes.Global;
        var minutes = paused ? invocation.Minutes : null;
        DateTimeOffset? until = null;
        if (invocation.Value("--until") is { } untilText)
        {
            // Fail closed: a supplied deadline that no longer parses or validates is a usage error,
            // never a silent open-ended pause.
            if (!PauseSchedule.TryParseDeadline(untilText, DateTimeOffset.UtcNow, out var parsedUntil, out var untilReason))
                throw new SyncCliUsageException(untilReason);
            until = parsedUntil;
        }
        return await operations.SetPauseAsync(scope, invocation.AccountId, paused, minutes,
            invocation.UntilResumed, cancellationToken, until, invocation.Flag("--until-tomorrow"));
    }

    public static bool TryRender(object? data, out string text)
    {
        var builder = new StringBuilder();
        switch (data)
        {
            case SyncCliCapabilityData capabilities:
                text = RenderCapabilities(capabilities);
                return true;
            case SyncCliAccountDetailData detail:
                builder.AppendLine($"{detail.Account.ConnectionId}  {detail.Account.Provider}  " +
                                   $"{(detail.Account.IsPrimary ? "primary" : "additional")}  {detail.Account.DisplayName}");
                builder.AppendLine($"  namespaces: root={detail.RootNamespaceId} home={detail.HomeNamespaceId}  " +
                                   $"scopes={detail.ScopeCount}  file requests={detail.HasFileRequestAccess}  " +
                                   $"pairs={detail.PairCount}  paused={(detail.Paused ? "yes" : "no")}");
                builder.AppendLine($"  connected={detail.ConnectedUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "unknown"}  " +
                                   $"last verified={detail.LastVerifiedUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) ?? "unknown"}");
                break;
            case SyncCliAccountCheckData check:
                builder.AppendLine($"{check.ConnectionId}: {check.Health}  live={(check.LiveVerified ? "yes" : "no")}  " +
                                   $"credential={(check.HasProtectedCredential ? "present" : "unavailable")}  " +
                                   $"pairs={check.DependentPairCount}  failure={check.FailureType ?? "none"}");
                break;
            case SyncCliAccountActionData action:
                builder.AppendLine($"{action.Action}: {action.Result}  connection={action.ConnectionId}  " +
                                   $"live verification required={(action.LiveVerificationRequired ? "yes" : "no")}");
                break;
            case SyncCliPairRemovalData removal:
                builder.AppendLine($"Removed pair {removal.PairId}; archived first at {removal.ArchivePath}");
                builder.AppendLine($"  manifest={removal.ManifestPath}");
                builder.AppendLine($"  unfinished effects archived and dropped: {removal.UnfinishedEffects}");
                builder.AppendLine("  archived rows: " + string.Join(", ",
                    removal.RowCounts.Select(row => $"{row.Key}={row.Value}")));
                builder.AppendLine(removal.OrphanedRoots.Count == 0
                    ? "  roots: retained (still referenced by another pair)"
                    : "  roots with no remaining pair (their rows are retained): " +
                        string.Join(", ", removal.OrphanedRoots));
                if (removal.Warning.Length > 0) builder.AppendLine("  warning: " + removal.Warning);
                break;
            case SyncCliQueueListData queue:
                builder.AppendLine($"{queue.Returned} queued row(s) of {queue.PendingCount} pending " +
                                   $"(offset {queue.Offset}, limit {queue.Limit}, more={(queue.HasMore ? "yes" : "no")})");
                foreach (var item in queue.Items)
                    builder.AppendLine($"{item.EnqueuedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z  {item.State}  " +
                                       $"{item.Operation}  {item.RelativePath}  pair={item.PairId}  " +
                                       $"recovery={item.RecoveryRequired}  operation={item.OperationId}");
                break;
            case SyncCliAttentionData attention:
                foreach (var item in attention.Items ?? [])
                    builder.AppendLine($"{item.Title}  pair={item.PairId}\n  {item.Description}" +
                        (item.RunId is null ? "" : $"\n  run={item.RunId}"));
                builder.AppendLine($"pending retained actions: {attention.PendingCount}");
                builder.AppendLine(attention.PairIds.Count == 0
                    ? "No pair needs attention."
                    : "Pairs needing attention: " + string.Join(", ", attention.PairIds));
                foreach (var monitor in attention.Monitors)
                    builder.AppendLine($"  monitor {monitor.PairId}  {monitor.Kind}  {monitor.State}  " +
                                       $"pending-dirty-items={monitor.PendingDirtyItems}");
                break;
            case SyncCliRunListData runs:
                builder.AppendLine($"{runs.Count} retained run/command record(s)");
                foreach (var run in runs.Runs)
                    builder.AppendLine($"{run.StartedUtc.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z  {run.Status}  {run.Kind}  " +
                                       $"{run.Id}  completed=" +
                                       (run.CompletedUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "none"));
                break;
            case SyncCliConflictListData conflicts:
                builder.AppendLine($"{conflicts.Returned} conflict(s) shown of {conflicts.TotalOpen} open " +
                                   $"(offset {conflicts.Offset}, limit {conflicts.Limit}, more={(conflicts.HasMore ? "yes" : "no")})");
                foreach (var summary in conflicts.FreeSpacePending)
                    builder.AppendLine(summary.Detail);
                foreach (var item in conflicts.Items.Where(item => item.FreeSpace is null))
                {
                    builder.AppendLine($"{item.RelativePath}  pair={item.PairId}  group={ConflictGroupLabel(item.Group)}  " +
                                       $"recommendation={item.Recommendation}  conflict={item.ConflictId}  " +
                                       $"generation={item.Generation}");
                    if (item.RetainedRevision is { } revision) builder.AppendLine($"  retained revision={revision}");
                    if (item.Options.Count > 0)
                        builder.AppendLine("  choices=" + string.Join(", ", item.Options.Select(option => option.Choice)));
                }
                break;
            case SyncCliConflictDetailData detail:
                builder.AppendLine($"{detail.Conflict.RelativePath}  pair={detail.Conflict.PairId}  " +
                                   $"conflict={detail.Conflict.ConflictId}  generation={detail.Conflict.Generation}  " +
                                   $"group={ConflictGroupLabel(detail.Conflict.Group)}");
                builder.AppendLine($"  local size={detail.Conflict.LocalSize} hash={ShortHash(detail.Conflict.LocalHash)}  " +
                                   $"dropbox size={detail.Conflict.DropboxSize} hash={ShortHash(detail.Conflict.DropboxHash)}");
                builder.AppendLine($"  retained text: local={detail.HasLocalContent} dropbox={detail.HasDropboxContent}  " +
                                   $"{detail.Note ?? "no note"}");
                if (detail.LocalText is not null) builder.AppendLine("--- Local text ---").AppendLine(detail.LocalText);
                if (detail.DropboxText is not null) builder.AppendLine("--- Dropbox text ---").AppendLine(detail.DropboxText);
                break;
            case SyncCliDecisionListData decisions:
                builder.AppendLine($"{decisions.Returned} decision(s) shown of {decisions.TotalOpen} open " +
                                   $"(offset {decisions.Offset}, limit {decisions.Limit}, " +
                                   $"more={(decisions.HasMore ? "yes" : "no")})");
                foreach (var item in decisions.Items)
                    builder.AppendLine($"{item.RelativePath}  pair={item.PairId}  rule={item.Rule}  " +
                                       $"reason={item.ReasonKey}  kind={(item.IsFolder ? "folder" : "file")}  " +
                                       $"{item.SideA}: " +
                                       $"size={item.SizeA?.ToString(CultureInfo.InvariantCulture) ?? "absent"} " +
                                       $"modified={DecisionTime(item.ModifiedA)}  {item.SideB}: " +
                                       $"size={item.SizeB?.ToString(CultureInfo.InvariantCulture) ?? "absent"} " +
                                       $"modified={DecisionTime(item.ModifiedB)}  token={item.Token}  {item.Detail}");
                break;
            case SyncCliDecisionResolveData decided:
                builder.AppendLine($"{decided.RelativePath}  pair={decided.PairId}  keep={decided.Side}  " +
                                   $"applied={decided.Applied}  failed={decided.Failed}  held={decided.Held}  " +
                                   $"notice closed={(decided.NoticeClosed ? "yes" : "no")}");
                if (decided.Detail is not null) builder.AppendLine("  " + decided.Detail);
                break;
            case SyncCliExactFileUploadData upload:
                builder.AppendLine($"{upload.Command}: {upload.Outcome}  effectAttempted={(upload.EffectAttempted ? "yes" : "no")}");
                builder.AppendLine($"  account={upload.AccountId}  accountReference={upload.AccountReference}  namespace={upload.NamespaceId}");
                builder.AppendLine($"  source={upload.SourcePath}  bytes={upload.SourceBytes}  sha256={upload.SourceSha256}");
                builder.AppendLine($"  destination={upload.Destination}");
                if (upload.CommittedId is not null)
                    builder.AppendLine($"  committed id={upload.CommittedId}  revision={upload.CommittedRevision}  " +
                                       $"size={upload.CommittedSize}  dropboxContentHash={upload.CommittedContentHash}");
                if (upload.FailureReason is not null) builder.AppendLine("  reason: " + upload.FailureReason);
                if (upload.SafeNextStep is not null) builder.AppendLine("  next: " + upload.SafeNextStep);
                builder.AppendLine("  stages: started=" + upload.StartedUtc.ToString("O") +
                    " verified=" + (upload.VerifiedUtc?.ToString("O") ?? "not reached") +
                    " effect=" + (upload.EffectUtc?.ToString("O") ?? "not attempted") +
                    " completed=" + (upload.CompletedUtc?.ToString("O") ?? "not reached"));
                break;
            case SyncCliConflictSelectionData selection:
                builder.AppendLine($"{selection.Count} exact conflict selection(s). Use --json to retain a resolvable snapshot.");
                foreach (var item in selection.Items) builder.AppendLine($"{item.ConflictId}  pair={item.PairId}  generation={item.Generation}  {item.RelativePath}");
                break;
            case SyncCliConflictResolutionData resolved:
                builder.AppendLine($"Resolved {resolved.Count} exact conflict(s).");
                foreach (var item in resolved.Items) builder.AppendLine($"pair={item.PairId} uploaded={item.Uploaded} downloaded={item.Downloaded} conflicts={item.Conflicts} blocked={item.Blocked}");
                break;
            case SyncCliJobData job:
                builder.AppendLine($"Active={job.Active}  requested job finished={job.RequestedJobFinished}");
                if (job.Job is { } active) builder.AppendLine($"{active.Id}  {active.Kind}  pair={active.PairId}  paused={active.Paused}  stop requested={active.StopRequested}  started={active.StartedUtc:O}");
                if (job.RequestedJobFinished)
                    builder.AppendLine("The requested job is no longer active. This is its last observed snapshot, not proof of its terminal outcome.");
                if (job.Job is { } linkedJob)
                    builder.AppendLine("Retained operation log: activity list --log --run " + linkedJob.Id);
                if (job.Job?.Progress is { } jobProgress)
                {
                    builder.AppendLine("State: " + jobProgress.State);
                    if (jobProgress.Comparison is { } comparison)
                        builder.AppendLine($"{comparison.Phase}: {comparison.RelativePath}; " +
                            $"{comparison.CompletedChecks}/{comparison.TotalChecks} {comparison.ProgressUnit}; {comparison.Detail}");
                    if (jobProgress.Action is { } action)
                    {
                        builder.AppendLine($"{action.Stage}: {action.RelativePath}; {action.Detail}");
                        if (action.Batch is { } batch)
                        {
                            builder.AppendLine(batch.Describe(0));
                            builder.AppendLine($"Active file rows: {job.ActiveRowsReturned}/{job.ActiveRowsTotal}; " +
                                $"more={job.ActiveRowsHasMore}. The retained operation log contains every file event.");
                            foreach (var item in batch.Active)
                                builder.AppendLine($"{item.Stage}: {item.RelativePath}; {item.Detail}");
                        }
                    }
                }
                if (job.ResultSummary is not null)
                    builder.AppendLine("Retained result counters (per-file decisions are in the operation log): " +
                        System.Text.Json.JsonSerializer.Serialize(job.ResultSummary));
                break;
            case SyncCliStorageData storage:
                builder.AppendLine($"pair={storage.Pair.Id}  policy revision={storage.Pair.PolicyRevision}  files on demand={storage.Pair.FilesOnDemandEnabled}  default={storage.Pair.DefaultStorage}  synced files={storage.KnownSyncedFiles}  bytes={storage.KnownSyncedBytes}");
                builder.AppendLine(System.Text.Json.JsonSerializer.Serialize(storage.Folders));
                break;
            case SyncCliStorageFilesData files:
                builder.AppendLine($"pair={files.PairId}  policy revision={files.PolicyRevision}  {files.Returned} file override(s) at offset {files.Offset}  more={files.HasMore}");
                foreach (var item in files.Items) builder.AppendLine(System.Text.Json.JsonSerializer.Serialize(item));
                break;
            case SyncCliStorageResultData changed:
                builder.AppendLine($"{changed.Action}: pair={changed.PairId}  policy revision={changed.PolicyRevision}  files={changed.FilesChanged}  directories={changed.DirectoriesChanged}");
                foreach (var failure in changed.Failures) builder.AppendLine("  failed: " + failure);
                break;
            case SyncCliRecoveryListData recovery:
                builder.AppendLine($"pair {recovery.PairId}: {recovery.ItemCount} retained item(s), " +
                                   $"{recovery.TotalSize} byte(s), {recovery.CleanupEligibleCount} cleanup eligible, " +
                                   $"retention={recovery.RetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "never"}");
                builder.AppendLine($"  offset={recovery.Offset}  returned={recovery.Returned}  more={recovery.HasMore}  next offset={recovery.NextOffset}");
                foreach (var item in recovery.Items)
                    builder.AppendLine($"  {item.Key}  {item.Source}  {item.RelativePath}  {item.Status}  " +
                                       $"{item.Size} byte(s)  retained=" +
                                       (item.RetainedUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "unknown"));
                break;
            case V2.SyncCliRecoveryPageData recoveryPage:
                builder.AppendLine($"pair {recoveryPage.PairId}: {recoveryPage.Returned} retained item(s) " +
                                   $"(limit {recoveryPage.Limit}, more={(recoveryPage.HasMore ? "yes" : "no")}, " +
                                   $"may-have-more={(recoveryPage.MayHaveMore ? "yes" : "no")})");
                builder.AppendLine("  " + recoveryPage.CurrentStateHint);
                if (recoveryPage.NextCursor is { } recoveryCursor)
                    builder.AppendLine($"  next cursor={recoveryCursor}; repeat with the same filters plus --after {recoveryCursor}");
                foreach (var item in recoveryPage.Items)
                    builder.AppendLine($"  {item.Id}  {item.Side}  {item.Path}  {item.Classification}  " +
                                       $"{item.Size} byte(s)  created=" +
                                       item.Created.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                       $"  version={item.Version ?? "unknown"}  destination={item.Destination ?? "none"}" +
                                       (item.Detail is null ? "" : "  " + item.Detail));
                break;
            case V2.SyncCliRecoveryRestorePageData recoveryRestore:
                builder.AppendLine($"pair {recoveryRestore.PairId}: {recoveryRestore.Returned} processed retained " +
                                   $"item(s) (dry-run={recoveryRestore.DryRun}, as-copy={recoveryRestore.AsCopy}, " +
                                   $"more={(recoveryRestore.HasMore ? "yes" : "no")}, " +
                                   $"may-have-more={(recoveryRestore.MayHaveMore ? "yes" : "no")})");
                builder.AppendLine("  " + recoveryRestore.CurrentStateHint);
                if (recoveryRestore.NextCursor is { } restoreCursor)
                    builder.AppendLine($"  next cursor={restoreCursor}; repeat with the same filters plus --after {restoreCursor}");
                foreach (var item in recoveryRestore.Items)
                    builder.AppendLine($"  {item.Id}  {item.Side}  {item.Path}  {item.Action}  {item.Classification}  " +
                                       $"{item.Size} byte(s)  created=" +
                                       item.Created.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                                       $"  version={item.Version ?? "unknown"}  destination={item.Destination ?? "none"}" +
                                       (item.Detail is null ? "" : "  " + item.Detail));
                break;
            case SyncCliRecoveryUsageData usage:
                builder.AppendLine($"pair {usage.PairId}: {usage.ItemCount} item(s), {usage.Size} byte(s), " +
                                   $"{usage.CleanupEligibleCount} cleanup eligible, oldest=" +
                                   (usage.OldestRetainedUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "none") +
                                   $", retention={usage.RetentionDays?.ToString(CultureInfo.InvariantCulture) ?? "never"}");
                break;
            case SyncCliRecoveryActionData action:
                builder.AppendLine($"{action.Action}: {action.Result}  pair={action.PairId}  key={action.Key}");
                break;
            case SyncCliScanData scan:
                builder.AppendLine($"Scanned pair '{scan.PairName}' ({scan.PairId}): local-only={scan.LocalEntries} " +
                                   $"dropbox-only={scan.DropboxEntries}");
                builder.AppendLine($"  local generation={scan.LocalScanGenerationId}  " +
                                   $"dropbox generation={scan.DropboxScanGenerationId}");
                break;
            case SyncCliPreparationData prep:
                builder.AppendLine($"First-sync preparation for '{prep.PairName}' ({prep.PairId}): " +
                                   $"items={prep.TotalItems} local-only={prep.LocalOnlyItems} " +
                                   $"dropbox-only={prep.DropboxOnlyItems} conflicts={prep.ConflictItems}");
                builder.AppendLine($"  upload bytes={prep.RecommendedUploadBytes}  " +
                                   $"download bytes={prep.RecommendedDownloadBytes}  elapsed=" +
                                   prep.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture) + "s  " +
                                   $"file effects authorized={(prep.FileEffectsAuthorized ? "yes" : "no")}");
                builder.AppendLine($"  local generation={prep.LocalScanGenerationId}  " +
                                   $"dropbox generation={prep.DropboxScanGenerationId}");
                if (prep.ChoicesDigest is { } choicesDigest) builder.AppendLine("  choices digest=" + choicesDigest);
                builder.AppendLine($"  deletion evidence: held={prep.HeldDeletionCount}  already resolved={prep.DeletionEvidenceResolved}");
                foreach (var held in prep.HeldDeletionExamples.Take(50))
                    builder.AppendLine($"    {held.Path}  local side={held.Local}  rule={held.Rule}");
                if (prep.HeldDeletionCount > prep.HeldDeletionExamples.Count)
                    builder.AppendLine($"    showing {prep.HeldDeletionExamples.Count} of {prep.HeldDeletionCount} held paths");
                if (prep.StubDownloads > 0 || prep.StubsWithoutCounterpart > 0)
                    builder.AppendLine($"  online-only placeholders: replace by download={prep.StubDownloads} " +
                                       $"({prep.StubDownloadBytes} bytes)  no copy on the other side (kept)={prep.StubsWithoutCounterpart}");
                if (prep.LinksExcluded is { } links) builder.AppendLine("  links excluded on both sides: " + links);
                if (prep.StubsLeftAlone is { } stubsLeft) builder.AppendLine("  " + stubsLeft);
                foreach (var carried in prep.CarriedIgnoredDeletes.Where(item => item.Files + item.Folders > 0))
                    builder.AppendLine($"  folder delete '{carried.Path}' side={carried.Side}: carried ignored " +
                                       $"files={carried.Files} folders={carried.Folders} bytes={carried.Bytes} " +
                                       "(removed with the folder, not synced separately)");
                AppendChoiceEffects(builder, prep.ChoiceEffects);
                AppendRebuildables(builder, prep.Rebuildables);
                foreach (var warning in prep.Warnings) builder.AppendLine("Warning: " + warning);
                break;
            case SyncCliFirstSyncResultData first:
                if (first.Outcome == V2.RunOutcomes.Paused) builder.AppendLine("Pair paused; use pairs resume to continue from checkpoints.");
                builder.AppendLine($"Sync result for '{first.PairName}' ({first.PairId}): applied={first.AppliedItems} " +
                                   $"skipped={first.SkippedItems} complete={first.FirstSyncComplete}");
                builder.AppendLine($"  uploaded={first.Uploaded} downloaded={first.Downloaded} " +
                                   $"unchanged={first.Unchanged} conflicts={first.Conflicts} blocked={first.Blocked}");
                AppendChoiceEffects(builder, first.ChoiceEffects);
                foreach (var warning in first.Warnings) builder.AppendLine("Warning: " + warning);
                builder.AppendLine($"  deletion evidence pending={first.DeletionEvidencePendingCount}");
                foreach (var path in first.DeletionEvidencePendingExamples.Take(50))
                    builder.AppendLine("    pending: " + path);
                if (first.DeletionEvidencePendingCount > first.DeletionEvidencePendingExamples.Count)
                    builder.AppendLine($"    showing {first.DeletionEvidencePendingExamples.Count} of {first.DeletionEvidencePendingCount} pending paths");
                if (first.ContentUnconfirmedCount > 0)
                {
                    builder.AppendLine($"  content unconfirmed={first.ContentUnconfirmedCount}");
                    foreach (var path in first.ContentUnconfirmedExamples.Take(50))
                        builder.AppendLine("    content unconfirmed: " + path);
                    if (first.ContentUnconfirmedCount > first.ContentUnconfirmedExamples.Count)
                        builder.AppendLine($"    showing {first.ContentUnconfirmedExamples.Count} of {first.ContentUnconfirmedCount} content-unconfirmed paths");
                }
                if (first.Incomplete is { } incomplete) builder.AppendLine("  " + incomplete);
                if (first.FirstSyncComplete)
                    builder.AppendLine("  First sync complete. Reviewed work was applied or saved as an issue. Later changes are handled by ordinary sync, not by reopening this review.");
                foreach (var issue in first.OpenIssues.OrderBy(item => item.Key, StringComparer.Ordinal))
                    builder.AppendLine($"  open issue: {issue.Key}={issue.Value}");
                if (first.RunId is not null) builder.AppendLine($"  retained run={first.RunId}");
                if (first.HasMore) builder.AppendLine($"  {first.Returned} of {first.TotalFiles} file results included. Read the complete retained log with activity list --log --run {first.RunId}.");
                break;
            case SyncCliRunRetryPlanData retry:
                builder.AppendLine($"Run {retry.RunId}: {retry.Returned} of {retry.TotalItems} files, offset {retry.Offset}. " +
                    $"Failed={retry.Failed}, not attempted={retry.NotAttempted}, verify first={retry.NeedsVerification}.");
                if (retry.UnavailableReason is not null) builder.AppendLine(retry.UnavailableReason);
                foreach (var item in retry.Items) builder.AppendLine($"{item.State} | {item.Path} | {item.Reason}");
                if (retry.HasMore) builder.AppendLine($"Next page: run retry-plan --run {retry.RunId}" +
                    (retry.PairId is null ? "" : $" --pair {retry.PairId}") + $" --limit {retry.Limit} --offset {retry.Offset + retry.Returned}");
                break;
            case SyncCliControlData control:
                builder.AppendLine($"{control.Action}: {(control.Accepted ? "accepted" : "not accepted")}  " +
                                   $"{control.Result}" +
                                   (control.PendingCount is { } pending ? $"  pending={pending}" : string.Empty));
                if (control.RewindConfirmation is { } rewindReview)
                    builder.AppendLine($"  reviewed session={rewindReview.SessionId}  " +
                        $"local-preview={rewindReview.LocalPreviewScanId}  Dropbox-preview={rewindReview.DropboxPreviewScanId}");
                break;
            case SyncCliPauseData pause:
                var pauseTarget = string.IsNullOrEmpty(pause.Target) ? string.Empty : " " + pause.Target;
                if (pause.Refused is { } refusal)
                {
                    builder.AppendLine($"{pause.Scope}{pauseTarget}: refused  {refusal}");
                    break;
                }
                builder.AppendLine($"{pause.Scope}{pauseTarget}: {(pause.Paused ? "paused" : "resumed")}" +
                                   (pause.Indefinitely
                                       ? " indefinitely, until you resume"
                                       : pause.UntilUtc is { } until
                                           ? " until " + until.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" +
                                             (pause.LocalResumeUtc is { } local
                                                 ? " (" + local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " local, " +
                                                   (pause.TimeZoneId ?? "unknown zone") + ")"
                                                 : string.Empty)
                                           : string.Empty));
                break;
            case SyncCliIgnoreListData ignores:
                builder.AppendLine($"pair {ignores.PairId}: {ignores.Count} ignore rule(s)");
                foreach (var rule in ignores.Rules) builder.AppendLine($"  {rule.Kind}:{rule.Value}");
                foreach (var warning in ignores.Warnings) builder.AppendLine("  warning: " + warning);
                break;
            case SyncCliFolderTreeData folders:
                builder.AppendLine($"pair {folders.PairId}: {folders.Count} folder(s)");
                // W2-C: the tree names its OWN ordered endpoints. The retained physical-port booleans are
                // still projected, but only under the name of the endpoint that actually carries that port,
                // so a reversed or two-cloud pair is never described as this computer or Dropbox.
                if (folders.LeftEndpoint is { } left)
                    builder.AppendLine($"  left: {left.Title}  location={left.Location}  " +
                                       $"available={left.Available}  files-at-root={left.RootFiles?.ToString() ?? "unknown"}");
                else
                    builder.AppendLine($"  left: local hierarchy available={folders.LocalHierarchyAvailable}");
                if (folders.RightEndpoint is { } right)
                    builder.AppendLine($"  right: {right.Title}  location={right.Location}  " +
                                       $"available={right.Available}  files-at-root={right.RootFiles?.ToString() ?? "unknown"}");
                else
                    builder.AppendLine($"  right: dropbox hierarchy available={folders.DropboxHierarchyAvailable}");
                if (folders.LeftEndpoint is { Available: false, Message: not null } leftNote)
                    builder.AppendLine("  note: " + leftNote.Message);
                else if (folders.LeftEndpoint is null && !folders.LocalHierarchyAvailable &&
                         folders.LocalHierarchyMessage is not null)
                    builder.AppendLine("  note: " + folders.LocalHierarchyMessage);
                if (folders.RightEndpoint is { Available: false, Message: not null } rightNote)
                    builder.AppendLine("  note: " + rightNote.Message);
                else if (folders.RightEndpoint is null && !folders.DropboxHierarchyAvailable &&
                         folders.DropboxHierarchyMessage is not null)
                    builder.AppendLine("  note: " + folders.DropboxHierarchyMessage);
                foreach (var folder in folders.Folders)
                    builder.AppendLine($"  {(folder.Included ? "included" : "excluded")}  {folder.RelativePath}  " +
                                       $"state={folder.State}  online-only={folder.OnlineOnly}  " +
                                       $"local-items={folder.LocalItems}  mixed={folder.Mixed}");
                break;
            case SyncCliIncludePreviewData preview:
                builder.AppendLine($"pair {preview.PairId}: read-only include preview '{preview.RelativePath}' ({(preview.Complete ? "complete inventory" : "partial inventory")})");
                builder.AppendLine($"  copy A to B={preview.Summary.CopyAToB}  copy B to A={preview.Summary.CopyBToA}  " +
                    $"newest wins={preview.Summary.NewestWins}  ties held={preview.Summary.HeldTies}  content/time check needed={preview.Summary.HeldUnknown}  deletes=0");
                foreach (var rule in preview.Rules) builder.AppendLine("  " + rule);
                foreach (var warning in preview.Warnings) builder.AppendLine("  warning: " + warning);
                foreach (var item in preview.Examples)
                    builder.AppendLine($"  {item.Recommendation}: {item.RelativePath} — {item.Reason}");
                builder.AppendLine("  Selection is unchanged. Use folders include --pair " + preview.PairId +
                    " --path \"" + preview.RelativePath + "\" --yes to include it for the next run.");
                break;
            case SyncCliSelectionData selection:
                builder.AppendLine($"pair {selection.PairId}: '{selection.RelativePath}' " +
                                   $"{(selection.Included ? "included" : "excluded")}  " +
                                   $"rules={selection.SelectionRuleCount}");
                foreach (var warning in selection.Warnings) builder.AppendLine("  warning: " + warning);
                break;
            case SyncCliAvailabilityData availability:
                builder.AppendLine($"pair {availability.PairId}: '{availability.RelativePath}' " +
                                   $"{(availability.OnlineOnly ? "online-only" : "kept on device")}  " +
                                   $"{availability.Result}");
                break;
            case SyncCliUpdateData update:
                builder.AppendLine($"installed {update.CurrentVersion}: " +
                                   (update.UpdateAvailable
                                       ? $"version {update.Version} is available ({update.ArchiveBytes} byte(s))"
                                       : "no compatible newer release is offered for this installed identity"));
                if (update.Downloaded) builder.AppendLine("  the update package was downloaded and verified.");
                if (update.Detail is not null) builder.AppendLine("  " + update.Detail);
                break;
            case SyncCliExplorerStatusData explorer:
                builder.AppendLine($"installed process: {(explorer.InstalledProcess ? "yes" : "no")}  " +
                                   $"explorer integration: {(explorer.IntegrationEnabled ? "enabled" : "disabled")}  " +
                                   $"pairs: {explorer.PairCount}");
                break;
            case SyncCliRemoteFolderListData folders:
                builder.AppendLine($"account {folders.AccountId}" +
                                   (folders.ParentPath is null ? " root" : " under " + folders.ParentPath) +
                                   $": {folders.Count} folder(s), more={(folders.HasMore ? "yes" : "no")}");
                foreach (var folder in folders.Folders)
                    builder.AppendLine($"  {folder.Path}  {folder.Name}  {folder.FolderId}");
                break;
            case SyncCliSharingListData sharing:
                builder.AppendLine($"account {sharing.AccountId} ({sharing.NamespaceId}): {sharing.Count} " +
                                   $"shared link(s), more={(sharing.HasMore ? "yes" : "no")}");
                if (sharing.Incomplete) builder.AppendLine("WARNING: incomplete inventory; successful results are retained.");
                foreach (var issue in sharing.Issues ?? []) builder.AppendLine("  warning: " + issue.Message);
                foreach (var link in sharing.Links)
                    builder.AppendLine($"  {link.Url}  audience={link.Audience}  access={link.Access}  " +
                                       $"revocable={link.CanRevoke}  password={link.PasswordRequired}  " +
                                       $"download={(link.DownloadSettingKnown ? link.AllowDownload.ToString() : "not reported")}  permission={link.PermissionId ?? "not applicable"}  path={link.Path ?? link.Name}");
                break;
            case SyncCliSharedLinkData link:
                builder.AppendLine($"{link.Url}  audience={link.Audience}  access={link.Access}  " +
                                   $"revocable={link.CanRevoke}  password={link.PasswordRequired}  " +
                                   $"download={(link.DownloadSettingKnown ? link.AllowDownload.ToString() : "not reported")}  expires={link.Expires?.ToString("u") ?? "none"}  " +
                                   $"path={link.Path ?? link.Name}");
                break;
            case SyncCliFileRequestMappingData requests:
                builder.AppendLine($"account {requests.AccountId} ({requests.NamespaceId}): {requests.Count} " +
                                   $"file request(s), more={(requests.HasMore ? "yes" : "no")}");
                foreach (var request in requests.Requests)
                    builder.AppendLine($"  {request.Id}  {(request.IsOpen ? "open" : "closed")}  " +
                                       $"files={request.FileCount}  destination={request.Destination ?? "unset"}  " +
                                       $"{request.Title}  {request.Url}");
                break;
            case SyncCliFileRequestData request:
                builder.AppendLine($"{request.Id}  {(request.IsOpen ? "open" : "closed")}  files={request.FileCount}  " +
                                   $"destination={request.Destination ?? "unset"}  deadline={request.Deadline?.ToString("u") ?? "none"}  " +
                                   $"{request.Title}  {request.Url}");
                break;
            case SyncCliDiscoveryStatusData discovery:
                builder.AppendLine($"Discovery helper installed: {(discovery.Installed ? "yes" : "no")}  " +
                                   $"status: {discovery.Status}  configure from CLI: " +
                                   $"{(discovery.ConfigureSupported ? "yes" : "no")}");
                builder.AppendLine("  " + discovery.Detail);
                break;
            case SyncCliLocalFolderCreateData folder:
                builder.AppendLine("Created " + folder.Path);
                builder.AppendLine("  parent: " + folder.ParentPath);
                builder.AppendLine("  name:   " + folder.Name);
                builder.AppendLine("  pair, account and sync state unchanged");
                break;
            case IReadOnlyList<SharedLinkRevokeResult> revocations:
                builder.AppendLine($"Selected links: {revocations.Count}; every exact outcome follows.");
                foreach (var result in revocations)
                {
                    builder.AppendLine($"  {result.Status}: {result.Target.Url}");
                    builder.AppendLine($"    account={result.Target.AccountId}; namespace={result.Target.NamespaceId}; retryable={result.Retryable}");
                    if (result.Target.PermissionId is { } permission) builder.AppendLine("    permission=" + permission);
                    if (result.Target.RevisionFingerprint is { } revision) builder.AppendLine("    observed permission revision=" + revision);
                    builder.AppendLine("    " + result.Message);
                }
                break;
            case IDropboxCliData dropbox:
                text = DropboxCliViews.Render(dropbox);
                return true;
            default:
                text = string.Empty;
                return false;
        }
        text = builder.ToString();
        return true;
    }

    /// <summary>One line per resolved first-sync choice; nothing when the run carried no one-sided choices.</summary>
    private static void AppendChoiceEffects(StringBuilder builder, IReadOnlyList<SyncCliChoiceEffectData>? effects)
    {
        if (effects is null) return;
        foreach (var effect in effects)
            builder.AppendLine($"  choice {effect.Action} on {effect.Side} prefix '{effect.Prefix}': " +
                               $"{effect.Files} file(s), {effect.Folders} folder(s), {effect.Bytes} byte(s)");
    }

    /// <summary>One line per rebuildable category; every category is listed, chosen or not, with its recommendation.</summary>
    private static void AppendRebuildables(StringBuilder builder, IReadOnlyList<SyncCliRebuildableData>? rebuildables)
    {
        if (rebuildables is null) return;
        foreach (var row in rebuildables)
        {
            builder.AppendLine($"  rebuildable {row.Id}{(row.Chosen ? " (chosen to stop syncing)" : "")}: " +
                               $"{row.Folders} folder(s), {row.Files} file(s), {row.Bytes} byte(s) — {row.Explanation}" +
                               (row.Examples.Count == 0 ? "" : " e.g. " + string.Join(", ", row.Examples)));
            foreach (var folder in row.Candidates ?? [])
                builder.AppendLine($"    {folder.Path} (chosen={folder.Chosen.ToString().ToLowerInvariant()})");
        }
    }

    private static string ShortHash(string? hash) =>
        string.IsNullOrEmpty(hash) ? "none" : hash.Length <= 12 ? hash : hash[..12];

    private static string DecisionTime(DateTimeOffset? time) =>
        time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "absent";

    /// <summary>
    /// W2-A/B: the two retained compatibility group keys name a PHYSICAL port, not a provider and not a
    /// logical side, so human CLI text never prints them as a claim. The raw keys stay exactly as accepted,
    /// addressable and stored, and the JSON contract keeps them verbatim; only the human rendering is
    /// projected onto the truthful neutral category. Every other group label is unchanged.
    /// </summary>
    public static string ConflictGroupLabel(string group) => group switch
    {
        "Local newer" or "Dropbox newer" => SyncAuthorityRuntimeHost.NewerVersionGroup,
        _ => group
    };
}
