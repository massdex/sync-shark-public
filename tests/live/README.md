# Sync Shark core v2 live acceptance tests

`Invoke-V2LiveTests.ps1` drives the real `SyncShark.exe` CLI against a real Dropbox account
through the core v2 surface (**selected with the `SYNCSHARK_CORE=2` setting**; the explicit
`--cli --core v2` switch selects the same core) and Sync Shark's own Dropbox CLI
(`--cli dropbox ...`). It is a **live** test: it creates real folder pairs, seeds files, runs
the first sync, mutates the local side with PowerShell and the remote side with the Dropbox
CLI, runs an incremental sync, and asserts the outcome on **both sides**. It writes files
only.

It is Windows PowerShell 5.1 compatible. It does not run itself; you run it when you mean to
exercise the account.

## Prerequisites

From `docs/design/layout-and-wiring-plan.md` ("Live-run prerequisites"):

- Storage, the A-fixes and the wiring are merged into the branch under test.
- A Dropbox connection with its credential in the Windows credential store (the user re-signs
  in if it is missing); its exact connection id is passed as `-Account`.
- The running application is **closed** (owner decision 27): the direct v2 path takes the same
  exclusive product lock the application holds, so a live run needs the application shut down.
- A Recovery location with enough space on the target drive.
- The pair is created through the CLI flow `create -> preview (first-sync prepare) -> enable ->
  first-sync apply`.
- Remote changes go through `SyncShark.exe --cli dropbox` (owner decision 31 superseded on
  2026-10-15), never the Composio connector.
- The run-report mapping is done for the branch under test.

## The flow it exercises

For every scenario:

1. create a fresh local folder and a remote folder whose name carries a random suffix; the
   remote folder is created with `dropbox mkdir`,
2. `pairs create` (created **disabled**),
3. `first-sync prepare` (the reviewed first-sync preview; records the choices digest the apply
   is bound to),
4. `pairs enable` (RQ-031: an apply needs an enabled pair),
5. `first-sync apply` (`--allow-recommended`, or the same `--choices` passed to prepare),
6. seed/mutate the local side with PowerShell and the remote side with the Dropbox CLI
   (`put`, `rm`, `mv`),
7. `run` (incremental), and
8. assert from what the CLI exposes: the local folder listing, `pairs show`, `first-sync
   prepare`, `run`, the Dropbox CLI's `ls`/`stat`/`get`/`revisions`, and the pair-specific
   `conflicts list` / `recovery list`.

Two shared helpers run one command and parse the single-JSON-object envelope
(`{schemaVersion, command, ok, data, error}`): `Invoke-Cli` for the core commands
(`--cli <command> ... --json`) and `Invoke-DropboxCli` for the Dropbox group
(`--cli dropbox <verb> ... --json`). Both front the same process/JSON runner (`Invoke-CliRaw`),
and both **validate the envelope** — the schema version, the expected `command`, and `ok:true` —
so a command that exits 0 with the wrong envelope is caught, not trusted.

## Core selection

Every child process selects the core through the `SYNCSHARK_CORE` setting (`CoreSwitch`): the
script sets `SYNCSHARK_CORE=2` **only while launching each child** and restores it right after, so
the invoking shell is left as it was — the core commands are invoked as `--cli <command> ...` with
no `--core` switch. The explicit `--cli --core v2` switch is the equivalent CLI form. A one-line
startup probe runs `status` and refuses to continue unless it reports the v2 mode (`direct-v2`), so
a missing or wrong setting cannot silently exercise the other core.

## The Dropbox CLI side

Every remote change and every remote check goes through Sync Shark's Dropbox CLI, never the
Composio connector (owner decision 31 superseded on 2026-10-15). The account is the same exact
retained Dropbox connection id passed as `-Account`; every verb takes
`--account <connection-id>`.

The `dropbox` group lives in the shared command registry and is **not** core-specific, so it is
dispatched before the core surface; it is invoked without the `--core` switch:

```powershell
SyncShark.exe --cli dropbox <verb> --account <id> ... --json
```

Verbs this script uses:

| Verb | Used for | `--yes` |
|---|---|---|
| `mkdir` | create each scenario's own remote folder (never `-RemoteBase`) | **required** |
| `put` | create or edit a remote file (`--mode overwrite`; `--client-modified` to pin the remote time) | **required** |
| `rm` | delete a remote file (stages an edit-vs-delete) | **required** |
| `mv` | move a remote file (stages a move-vs-edit) | **required** |
| `stat` | verify one remote path exists and read its revision (`missing_target` = absent) | not needed |
| `ls` | verify remote folder contents (pages followed with `--cursor`) | not needed |
| `get` | download a remote file to prove its content | only to replace an existing `--out` |
| `revisions` | prove a replaced version remains in Dropbox history | not needed |

Every destructive verb (`mkdir`, `put`, `rm`, `mv`) requires `--yes`; the script passes it on
every such call. Read verbs do not, except `get` onto an existing `--out`.

A Dropbox result too large for the reply envelope is spilled to a file (`data.spilled`), so the
script reads the real data back from `data.resultFile` when that happens (the 2,000-file `ls` in
`Perf`).

## The first-sync `--choices` document

`first-sync prepare` and `first-sync apply` accept `--choices`, either inline JSON or an absolute
JSON file. The script writes the document to a file under the report folder and passes its
absolute path, because an inline JSON argument carries double quotes that cannot be escaped
safely onto the Windows command line (the same reason the startup guard rejects a double quote
in any parameter).

The document (design `docs/design/first-sync-choices.md`, decisions 52-54):

```json
{"paths":{...legacy path->upload|download|no-change map...},
 "oneSided":[{"prefix":"alpha/","side":"Local","action":"remove"},
             {"prefix":"beta/","side":"Dropbox","action":"copy"}]}
```

- `side` names the side that **holds** the one-sided items, matched case-insensitively against
  the pair's provider display names (`Local`, `Dropbox`, `Google Drive`).
- `action` is `remove` (delete the one-sided item on the side that holds it, recovery-first) or
  `copy` (put it on the other side).
- `prefix` is a pair-relative folder; the longest matching prefix wins and matches the folder and
  everything below it, never a sibling (`alpha/` != `alphabeta/`).

Behaviour the CLI enforces (mapped from the current CLI, `V2/Adapters/V2CliRuntime.cs` and
`V2/Adapters/FirstSyncChoicesState.cs`; decision 54):

- `first-sync prepare` reports the choices digest (`choicesDigest`, a 64-hex SHA-256 of the
  normalized choices) and one effect per choice (`choiceEffects`: prefix, side, action, files,
  folders, bytes).
- `first-sync apply` is **bound to the reviewed prepare**: its `--choices` must have the same
  digest as the last prepare. A different (or absent) prepare is a **review error** (exit 5,
  `review_required`) and reaches no writer.
- A `remove` choice whose effect **grows** past what the prepare reported (new one-sided files
  appeared) is refused with the same review error (`RefuseRemoveGrowth`).
- Both refusals share the code `review_required` and exit **5**, so the scenarios distinguish them
  by a distinctive phrase of the message — the digest mismatch ("differ from the last prepare") or
  the growth guard ("more than the last prepare reported") — not by the code alone.

Because the script cannot create real folder names (decision 53), every scenario uses neutral
placeholders such as `alpha`, `beta`, `gamma`.

## Running it

```powershell
# the script sets SYNCSHARK_CORE=2 for its child processes; -Account is the exact retained id
.\Invoke-V2LiveTests.ps1 -Exe 'C:\TestBuild\SyncShark.exe' `
    -Account 'connection-<exact-id>' -Scenario all

# one scenario (exact name or a substring)
.\Invoke-V2LiveTests.ps1 -Exe <exe> -Account <id> -Scenario Rule09
```

The script sets `SYNCSHARK_CORE` only while launching each child process and restores it right
after; to drive the CLI by hand, set the setting in the shell (or pass `--cli --core v2`):

```powershell
$env:SYNCSHARK_CORE = '2'
SyncShark.exe --cli status --json
```

Before the first scenario the script prints the resolved **v2 product root** (from `status`: where
the state database and the `first-sync-<pairId>.choices.json` prepare records live), alongside the
report folder, local base, remote base and account. The remaining auxiliary locations (recovery and
the private control namespace) are printed with their fixed shapes; see "Permitted auxiliary
writes" below.

Each scenario's remote folder is created under `-RemoteBase` with `dropbox mkdir`, but
`-RemoteBase` itself is **never created or modified** by the script: it must already exist, or
every scenario fails with a clear message. Each scenario folder (local and remote) carries a
random 8-hex suffix and the script refuses to start a scenario if its local folder or its remote
folder already exists (and creates the local folder without `-Force`, so an existing one is
never adopted).

A double quote in any parameter (`-Exe`, `-LocalBase`, `-RemoteBase`, `-Account`, `-Scenario`,
`-ReportRoot`) is rejected before anything runs, because it cannot be escaped safely onto the
Windows command line.

### Parameters

| Parameter | Default | Meaning |
|---|---|---|
| `-Exe` | (required) | Absolute path to `SyncShark.exe`. |
| `-LocalBase` | `Join-Path $env:LOCALAPPDATA 'SyncSharkTests\v2-live'` | Local root; each scenario gets a timestamped, random-suffixed subfolder. |
| `-RemoteBase` | `/Apps/Sync Shark/Testing/v2-live` | Remote root; must already exist. Each scenario gets a timestamped, random-suffixed subfolder under it. |
| `-Account` | (required) | Exact Dropbox connection id (`connection-...`); used by both the pair commands and every Dropbox CLI verb. |
| `-Scenario` | `all` | Exact scenario name, a substring, or `all`. |
| `-TinyFileCount` | `2000` | Files created for the throughput scenario. |
| `-TinyFileBytes` | `16` | Bytes per tiny file. |
| `-TieWindowSeconds` | `2` | The engine tie window (planner rules 4/5). Informational only; the engine owns it. |
| `-FirstSyncOlderSeconds` | `120` | How much older the remote copy is made in the first-sync scenario, so `put --client-modified` forces the rule-4 (newer wins) branch instead of the tie. |
| `-CommandTimeoutSeconds` | `120` | Timeout for quick commands (`status`, `first-sync prepare`, `pairs show`, `ls`, `stat`, `conflicts list`, `recovery list`). |
| `-LongTimeoutSeconds` | `1800` | Timeout for `first-sync prepare`, `first-sync apply`, `run`, and the remote `put`/`rm`/`mv`/`get` steps. |
| `-ReportRoot` | `<LocalBase>\_reports` | Where per-command JSON stdout/stderr are written (and the scratch files for `put`/`get` and the `--choices` documents). |

Every other value (the envelope schema version, the folder-suffix length, the keep-both label
marker and notice group, the run-effect field list, timeout milliseconds, the failure prefix,
the choices-digest length) is a named variable at the top of the file. Every command verb and
option string lives in the one `$script:Cli` block at the top (engineering standards section
2.2); the scenario bodies contain no magic numbers or command literals.

## Scenarios

Each scenario is one function named after the planner rule numbers (`planner.md`) it covers, the
`Choices` prefix for the first-sync choices feature, or the RQ id for the safety case. Every
scenario runs **end to end**; none is skipped.

| Scenario | Rules | Remote steps | What it asserts (local + remote) |
|---|---|---|---|
| `Rule04_05_FirstSyncNewerWinsAndTieHold` | 4, 5 | `mkdir`, `put --client-modified` (older remote copy) | First sync, same path, different content, one side clearly older: the clash is detected (`first-sync prepare` `conflictItems >= 1`), the **newer version wins on both sides** (`get`), and the replaced version survives only in Dropbox history (`revisions >= 2`) and in the pair's Recovery (`recovery list --pair`). The 2 s tie (rule 5) is proven by the planner unit tests (decision 33); here `--client-modified` forces the rule-4 branch. |
| `Rule02_OneSidedEditEachDirection` | 2 | `put` (remote edit) | Local edit uploads (`uploaded >= 1`, `get` proves the Dropbox content) and the pair converges; the remote edit downloads to local and converges. |
| `Rule27_EditBothSidesKeepBoth` | 27 | `put` (remote edit) | Edit both sides differently: the original keeps its own version and the labelled copy holds the other side's version, verified by content **on both sides**, and a pair-specific keep-both notice appears in `conflicts list --pair`. |
| `Rule07_EditVsDelete` | 7 | `rm` (remote delete) | Edit locally, delete remotely: the edit survives locally and is **restored on Dropbox** (`stat` + `get`), and the pair converges. |
| `Rule09_LocalMovePropagates` | 9 | `stat`/`get` (verify) | A local rename repeats as a move: old name gone and new name present **locally and on Dropbox**, the content matches, the pair converges, and no pair notice is raised (`conflicts list --pair`). |
| `Rule10_MoveVsEditKeepBoth` | 10 | `mv` (remote move) | Move remotely, edit locally: both names exist on both sides (`ls`) with the right content (`get` proves the remote carries the local edit and the moved content), and the local moved content is checked too. |
| `Rule06_DeletePropagatesToRecovery` | 6 | `stat` (verify) | A local delete propagates: the file is gone **from Dropbox** too, its version is retained in `recovery list --pair`, and the pair converges. |
| `Rule24_FolderDeleteEditedChildSurvives` | 24 | `put` (remote edit) | A local folder delete with a remotely-edited child: the edited child is restored locally (`get` proves the content) and stays on Dropbox; the unchanged child is deleted on both sides. |
| `RQ034_LocalRootMissingNoDeletes` | RQ-034 (safety) | `stat`/`revisions` (verify) | The local pair root missing on disk must not be read as "everything deleted": the run is a **no-op** (no uploaded/downloaded/deleted/moved/kept-both/blocked, not stopped) and the remote **content and revision are unchanged**; the rename-back is done in a `finally` and the pair converges. |
| `Choices_RemoveAndCopyByPrefix` | choices (52/53) | `stat`/`get` (verify) | A one-sided `remove` on one prefix and a one-sided `copy` on another, with neutral folder names: the prepare reports the digest and both effects, the apply reports both effects and completes the first sync, the removed file is gone on the side that held it and retained in Recovery, and the copied file lands on the other side with the right content. |
| `Choices_ChangedApplyRefused` | choices (54) | `stat` (verify) | An apply whose `--choices` differ from the last prepare is refused with exit **5** and the **digest-mismatch** reason (not the growth reason), and makes **no** effect: the local file's content is unchanged, nothing reaches the other side, and no recovery record is retained. As a positive control, the **original** prepared choices then apply to the same pair and copy the file across. |
| `Choices_PrepareApplySameChoices` | choices (54) | `put` (one-sided remote file) | The prepare -> apply happy path with the **same** choices: the prepare reports the 64-hex digest and the per-choice effect, the apply's effect equals the prepare's **field for field** (prefix, side, action, files, folders, bytes), and the reviewed copy (a one-sided remote file, absent locally before the apply) is performed. |
| `Choices_RemoveGrowthRefused` | choices (54) | `stat` (verify) | A `remove` choice whose effect grows after the prepare (a new one-sided file appears under its prefix) is refused with exit **5** and the **growth** reason, with no effect: both local files are untouched with their content, nothing reaches the other side, and nothing is retained. |
| `LocalDeleteAfterFirstSync_Delta` | 6 (delta) | `stat`/`get` (verify) | A first sync of three files, then one local delete: the incremental (delta) run reports a delete, the Dropbox copy is removed recovery-first (gone remotely, retained in `recovery list --pair`), the other two files are untouched on both sides with their content, and a second pass converges. |
| `LocalDeleteAfterFirstSync_DeleteAndMove` | 6, 9 (delta) | `stat`/`get` (verify) | The journal (delta) path with a delete and a rename: the delete is reported and removed recovery-first, the rename propagates (old name gone, new name carries the content on Dropbox), the untouched file stays, and a second pass converges. |
| `LocalDeleteAfterFirstSync_FullRescan` | 6 (full rescan) | `stat`/`get` (verify) | A forced full rescan via `pairs rescan` (see note 5): a settling run leaves each root a cursor, a local delete then removes one file, `pairs rescan --side both` forgets both cursors, and the next run re-reads each root in full yet still plans the delete from the retained baseline — the Dropbox copy is removed recovery-first, the other files are untouched on both sides, and a second pass converges. |
| `Perf_2000TinyFilesThroughput` | RQ-141/142 | `ls` (verify) | 2,000 tiny files through the first sync: the apply reports all as applied and uploaded, all 2,000 exist on Dropbox, and files/s is printed. |

## Result status and exit code

- `PASS` — every attempted assertion passed. The reason column lists any informational notes.
- `FAIL` — an assertion failed (or an unexpected error); counted.

The script prints one line per scenario (status, reason, timings), a summary table at the end,
and exits with **the number of failures** (`0` means all scenarios passed).

## What the core v2 CLI still does not cover

The remote side is covered by the Dropbox CLI, and the earlier catalog gaps are now closed:

- **`recovery list --pair`** exists (reads `IStateStore.Recoveries`) — used by `Rule06` and
  `Rule04_05`.
- **`conflicts list --pair`** exists (reads the pair's open notices; the notice kind is the
  group) — used by `Rule27` and `Rule09`.
- **Per-direction run counts** are real: `run` (and `first-sync apply`) report
  `uploaded`, `downloaded`, `deleted`, `moved`, kept-both (the `conflicts` field) and `blocked`
  instead of zeros. `unchanged` and `notAttempted` are still hard-coded to `0`.
- **`pairs retire`** exists, so test pairs can be retired after a run (the script does not
  retire them, so you can inspect them).

What remains, and how the assertions work around it:

1. **The incremental `plan` preview after the first sync is deliberately deferred** (owner
   decision 32/45): `plan` refuses a `FirstSyncDone` pair, so ongoing-sync assertions do not use
   the plan JSON. The first-sync preview used here is `first-sync prepare`, which is the
   reviewed preview the apply is bound to.
2. **The CLI exposes no explicit "local root missing/incomplete" flag** on a run, so RQ-034 is
   proven by the run being a total no-op plus the remote content and revision being unchanged
   (`stat`/`revisions`).
3. **No way to set a remote file's `server_modified`**; `put --client-modified` sets the
   Dropbox `client_modified`, which is what the engine compares, and is used to force the rule-4
   branch.
4. **A `remove`-choice growth refusal is now asserted live** by `Choices_RemoveGrowthRefused`: a new
   one-sided file is staged under the prefix between the prepare and the apply, and the unchanged
   document is refused by the growth guard (exit 5, the growth reason).
5. **A forced full rescan is supported** (`pairs rescan --pair <exact-id> [--side a|b|both] --yes`; the
   default side is `both`). The engine scans a root in full only when its stored cursor is null or the
   journal delta is cursor-invalid; this verb clears the chosen roots' stored cursors (`IStateStore.ForgetCursor`
   for the SQLite store and the in-memory fake) so the pair's next run re-reads them in full, and reports which
   roots it will rescan. It leaves baselines, live rows and tombstones untouched — a full scan reports no
   journal deletes, so it can detect a deletion only from the baseline it already holds. The verb is refused
   while a run is active (the same runtime lock the first sync and runs hold) and no cursor is reset then.
   `LocalDeleteAfterFirstSync_FullRescan` exercises this end to end; the first sync leaves no root cursor (its
   preview commits none), so that scenario runs once first to settle a cursor before the rescan.

## Safety

- The scenarios' only deletions are the scenario's own local files inside its timestamped local
  folder (e.g. `Rule06` deletes its own seeded file, `Choices_RemoveAndCopyByPrefix` removes only
  its own one-sided file) and the scenario's own files inside its timestamped remote folder on
  Dropbox. No permanent delete is used anywhere.
- Remote writes (`mkdir`, `put`, `rm`, `mv`) only ever touch the scenario's own timestamped
  remote folder under `-RemoteBase`. **`-RemoteBase` is never created or modified**: it must
  already exist, or every scenario fails with a clear message.
- Local scenario folders are created without `-Force` and must not already exist; a
  random-suffixed name makes a collision effectively impossible and the existence check refuses
  to reuse either folder.
- After `pairs create`, the pair's retained account, local root and remote root are re-read with
  `pairs show` and must equal the layout's exactly, before the pair is enabled or applied; any
  mismatch fails the scenario closed.
- The `--choices` documents are written as regular files under the report folder and passed to
  the CLI as absolute paths; nothing inline is shell-quoted.
- `SYNCSHARK_CORE` is set only while launching each child process and restored right after, so the
  invoking shell's environment is left as it was.
- The RQ-034 rename refuses an existing destination, uses `[System.IO.Directory]::Move` (which
  fails rather than nesting the source), and restores only the exact folder it moved (verified by
  the scenario's own marker file) when the original path is free.

### Permitted auxiliary writes

The scenarios are confined to their own folders, but a live run is **not** limited to them: the
CLI writes durable state and recovery through the product's own paths, and the script drives those
paths. These are the only other writers, and the script prints their resolved locations (or fixed
shapes) before the first scenario. This replaces any earlier claim that a run "never modifies
anything outside" its folders — it does not.

| Write | Location |
|---|---|
| per-command report files and `--choices` documents | `-ReportRoot` (default `<LocalBase>\_reports\<stamp>`) |
| v2 product state | `<product root>\v2\state.db` (with `-wal`/`-shm` sidecars) |
| first-sync prepare records | `<product root>\v2\first-sync-<pairId>.choices.json` |
| recovery | `<recovery root>\.syncshark-recovery-<pairId>\<operationId>.recovery` (the root comes from the Recovery location setting, decision 48) |
| private control namespace (a sibling of each local pair root) | `<parent of local pair root>\.syncshark-control-<namespaceId>\` with `.syncshark-owner.json` |

`<product root>` is the app's data location, printed from `status` at startup; `<recovery root>` is
the configured Recovery location. Cloud recovery relies on the provider's own version history (v2's
Dropbox writer makes no separate recovery call), so there is no separate remote recovery write.

While applying changes the engine also creates transient private staging artifacts
(`.syncshark-transaction-*`, `.syncshark-transfer-*`, `.syncshark-create-*`) that it removes when
the operation completes; the tests never write any of these by hand.

## Notes and assumptions

- The v2 core is selected with `SYNCSHARK_CORE=2` for each child process, set only for that launch
  (the `CoreSwitch` setting); the CLI form `--cli --core v2` is equivalent. The `dropbox` group is
  a shared registry group and is invoked as `--cli dropbox <verb> ... --json`.
- The envelope is validated (schema version, expected `command`, `ok`) rather than trusting the
  exit code.
- `pairs create` returns `data.id`; `pairs show` returns the bound account (`connectionId`) and
  roots (`localRoot`, `dropboxRoot`); `first-sync prepare` returns `totalItems`, `conflictItems`,
  `choicesDigest` and `choiceEffects`; `first-sync apply` reports `firstSyncComplete`,
  `appliedItems` and `choiceEffects`; the run reports per-kind counts.
- Both first-sync choices refusals report `review_required` with exit code **5**, so the choices
  scenarios assert the process exit code AND a distinctive phrase of the refusal message, not the
  code alone.
- The first-sync CLI flow is `create -> first-sync prepare -> enable -> first-sync apply` (run.md
  section 7, R-A6; decisions 52-54).
- JSON envelopes and stderr for every command are written under `-ReportRoot` for inspection,
  alongside the scratch files the `put`/`get` steps and the `--choices` documents use.
