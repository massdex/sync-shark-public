# Sync Shark live acceptance tests

`Invoke-V2LiveTests.ps1` drives the staged candidate `syncshark.exe` + `Sync Shark.exe` through the
new command line (see [`CLI-CAPABILITIES.md`](../../CLI-CAPABILITIES.md)) against a real Dropbox
**test root** and real local roots. It is a live test, not part of the offline CI matrix, and it is
never run by the build.

## Requirements

- Windows 10 or 11, **PowerShell 7 or later** (the script uses `#requires -Version 7.0`; its HTTP
  client is `System.Net.Http.HttpClient` with `UseProxy = false`).
- The staged build: `syncshark.exe` and `Sync Shark.exe` copied under the disposable test root.
- A dedicated Dropbox test account, a connection id, and the access token stored as a **generic
  credential** in Windows Credential Manager under the target you pass.
- A disposable test root that is not a drive root and not under a production root you supply
  (pass those with `-ProductionRoots`).

## How isolation is made real

The product's data location is fixed (`%LOCALAPPDATA%\Massdex\SyncShark`), so this driver does not
trust caller-supplied exclusions. It:

1. creates its own disposable roots under `-TestRoot` (`profile\LocalAppData`, `local`, `reports`);
2. starts its **own host** from the staged executables, with every child's `%LOCALAPPDATA%` pointed
   at `<TestRoot>\profile\LocalAppData`, so the store and logs land under the disposable root;
3. **before touching any fixture** proves from the host and the file system that:
   - the store file exists under the disposable root (`<profile>\Massdex\SyncShark\engine\state.db`);
   - `pairs list` is empty (no pre-existing pair is touched);
   - every running `Sync Shark` process is the staged host image, not another image;
   - the host did not attach to an installed host (a different host image makes it refuse);
4. creates its own pairs and removes them by the id `pairs add` returned, while the host runs; it
   stops the host last.

If any check fails, it refuses before its first fixture write. It never uses a supplied production
root or an installed path, and it never falls back to the installed executable.

## Credentials, networking and diagnostics

- The token is read only from Windows Credential Manager (`CredRead`, generic credential). It stays
  in process and is never printed or written.
- The script's own HTTP client sets `UseProxy = false`; every child runs with proxy variables
  removed. No parent proxy state is changed.
- Per-command output is written to report files under `-ReportRoot` **redacted** (Bearer tokens and
  `Authorization` headers removed). Thrown messages carry the command, exit code and report path,
  never raw output, headers or the token. HTTP error text is reduced to the provider's
  `error_summary`.

## Test data and assertions

Each scenario gets a random-suffixed local folder (created without `-Force`) and a random-suffixed
remote folder created with an **exclusive** create; an existing remote folder fails the scenario
(never adopted). Files are written and compared as **bytes** on both sides (no BOM, no added
newline). The pair is proved from the id `pairs add` returned and its exact list line (name, both
roots, account). A removal must report an attributable removal, not merely exit 0. A startup
self-check proves a wrong byte set is rejected and an empty tree is a defined empty set.

## Parameters

| Parameter | Meaning |
|---|---|
| `-CandidateExe` | (required) Absolute path to the staged `syncshark.exe`. |
| `-TestRoot` | (required) Disposable root the driver owns (not a drive root, not under a supplied production root). |
| `-ProductionRoots` | Production roots the driver must stay out of; a test root overlapping one is refused. |
| `-RemoteBase` | (required) Isolated test root on the test account. |
| `-Account` | (required) Test connection id for `pairs add --account`. |
| `-CredentialTarget` | (required) Windows Credential Manager generic-credential target holding the token. |
| `-NativeBinVolumes` | Volumes with existing PREP/native-bin evidence (for example `F:`) used by the bin arms. |
| `-Scenario` | Exact scenario name, a substring, or `all`. |
| `-CommandTimeoutSeconds` | Timeout for quick commands (default 180). |
| `-RunTimeoutSeconds` | Timeout for `repair` and the log-growth wait (default 1800). |

## Scenarios

| Scenario | What it proves |
|---|---|
| `A02CreateOneSide` | A one-sided create lands at the same path with the same bytes. |
| `A05EditOneSide` | An existing-file edit replaces the other copy in both directions. |
| `A07EditBoth` | Different edits keep both exact versions and the notice names the item. |
| `A10DeleteOneSide` | A one-sided delete propagates; untouched files stay. |
| `A11EditVersusDelete` | The edit survives a remote delete, is restored, and the notice names the item. |
| `A18MoveOneSide` | A one-sided move lands at the exact new endpoint; the old endpoint is gone. |
| `A19MoveVersusEdit` | A move against a remote edit keeps both exact endpoints and raises the notice. |
| `A21MoveVersusDelete` | The move survives a remote delete and raises the notice. |
| `A26SafeSave` | `x.tmp` renamed over `x` replaces the same remote object with the new bytes. |
| `A27CapitalRename` | A capitals rename propagates both directions with the provider `path_display` spelling preserved. |
| `A28ExcludeKeepAndInclude` | `exclude --keep` keeps both copies; `include` compares the path at once. |
| `A28ExcludeNoKeep` | A no-keep exclusion warns with the recovery place, then removes with recovery. |
| `A39InUse` | A locked item raises a notice naming it, unrelated work continues, and it heals. |
| `A44ConcurrentDirections` | An upload and a download both land in one comparison. |
| `A47HostReadsPauseStop` | Host-down `status` is non-zero with the not-running message and starts nothing; `logs` reads stored records; pause/resume work. |
| `A48RootBinRemovable` | A removable volume **with proven native-bin evidence**: a Dropbox-side delete recycles the engine-removed PC copy into the volume-root `Recycle Bin` as a new item with the exact fixture bytes. |
| `A48NativeBinFixed` | A fixed volume with proven native-bin evidence: the engine-removed PC copy is observable in the Windows bin by exact name and original staging path. |
| `A49LogsWhileRunning` | The current run's log is read while the host writes it; a normal record is present and an `Effect` record appears only with `--show-files` (parsed from the shared TSV record). |
| `A50NestedPairs` | The inner pair syncs its own endpoint; removing it leaves the outer comparison to the automatic pass (no forced Repair as proof). |

Bin arms classify as **NOT OBSERVED** (never PASS) unless the selected volume has proven
native-bin evidence and the expected drive type; a leftover `$Recycle.Bin` directory is not proof.
No production bin is written, and the fixed-drive arm reads only items whose original path is under
the disposable test root.

## Live-arm map

The plan's A-table (its Live column) assigns live proof of each arm. This driver implements the
live arms that need live observation of true sync, the log view or the bin. The remaining live arms
are proved by the plan's separately assigned staging/manual arms; the offline CI cases cover the
synthetic and decision parts only. No new rehearsal is added.

- **This driver:** A02, A05, A07, A10, A11, A18, A19, A21, A26, A27, A28, A39, A44, A47, A48,
  A49, A50.
- **Separately assigned staging/manual arms (plan Live column, not this driver):** A01, A03, A04,
  A06, A08, A09, A13, A14, A15, A17, A20, A22, A23, A24, A29, A30, A31, A32, A34, A40, A41, A42,
  A43, A45, A46, A33, A36-A38, and the forced/large LIVE-01/02 arms.
- **Offline CI:** the synthetic and decision parts of every arm (for example A35 and A42's synthetic
  cases), never the live observation itself.

## Result status and exit code

- `PASS` - every attempted assertion passed.
- `FAIL` - an assertion or unexpected error failed; counted.
- `NOT OBSERVED` - the arm's required fixture (drive type or proven native bin) is absent; recorded,
  not counted as a failure and never reported as a PASS.

The script prints one line per scenario and a summary, and exits with the number of failures.

## Safety

- Works only inside its own disposable roots under `-TestRoot`; never creates or modifies the
  supplied remote root itself.
- Starts and stops only its own staged host; refuses to attach to a different host image or to touch
  an existing pair or an unproved store.
- The token comes from Windows Credential Manager and is never printed or persisted; report output is
  redacted.
- The only deletions are the scenario's own files; the pair is removed only by the proved id.
