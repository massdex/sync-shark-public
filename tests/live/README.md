# Sync Shark live acceptance tests

`Invoke-V2LiveTests.ps1` drives the candidate `syncshark.exe` command line (see
[`CLI-CAPABILITIES.md`](../../CLI-CAPABILITIES.md)) against a real Dropbox **test root** and real
local roots, on an **approved, isolated staging profile**. It is a live test, not part of the
offline CI matrix, and it never runs against the installed product or production data.

## Safety gate: an approved staging setup must prove isolation

The product's data location is fixed (`%LOCALAPPDATA%\Massdex\SyncShark`), so a staging run needs
its **own Windows profile** (its own `%LOCALAPPDATA%` and credentials) and roots on
**volumes disjoint from production** (PLAN section 8). The script therefore refuses to write
anything or launch the candidate until it reads a **staging proof** JSON (passed as
`-StagingProof`) supplied by the approved staging setup, and every check passes:

- the candidate executable is under the staging root and is not the installed executable;
- the process's `%LOCALAPPDATA%` is the staging profile, not the production profile;
- the local base, report root and recovery root are under the staging root;
- the local volume is disjoint from every production volume and recovery root;
- the installed host is stopped (`installedHostStopped: true`) and no process with the installed
  host's name is running (a matching host would receive the CLI's pipe commands);
- the Dropbox test root, the test connection id and the Windows Credential Manager target are
  supplied.

If the proof is missing or any check fails, the script throws before its first write or CLI
launch. It never falls back to the installed host, the production profile or product paths.

### Staging proof schema (neutral placeholders)

```json
{
  "candidateExe": "<stagingRoot>/build/syncshark.exe",
  "installedExe": "<installRoot>/Sync Shark.exe",
  "installRoot": "<installRoot>",
  "installedHostExeName": "Sync Shark",
  "installedHostStopped": true,
  "stagingRoot": "<approved staging root>",
  "stagingLocalAppData": "<staging profile LocalAppData>",
  "productionLocalAppData": "<production profile LocalAppData>",
  "localBase": "<stagingRoot>/live/local",
  "reportRoot": "<stagingRoot>/live/reports",
  "recoveryRoot": "<stagingRoot>/live/recovery",
  "remoteBase": "<isolated test root on the test account>",
  "productionVolumes": ["<production volume>"],
  "productionRecoveryRoots": ["<production recovery root>"],
  "credentialTarget": "<test-credential target in Windows Credential Manager>",
  "account": "<test connection id>"
}
```

No real account, connection id, personal name or user path belongs in this file or in the
repository. The remote base is always the approved isolated test root; the script and this README
use neutral placeholders only.

## Credentials and networking

- The Dropbox access token is read **only** from Windows Credential Manager, under the target named
  by the proof, through the Win32 `CredRead` API. The token stays in the requesting process and is
  never printed, logged or written to a report.
- The injected proxy is bypassed for the script's own Dropbox requests
  (`WebRequest.DefaultWebProxy` is cleared) and stripped from every child process's environment.
  Both are restored when the script ends.
- Command output is written to per-command report files under the report root; the thrown message
  contains only the command, its exit code and the report path, never raw output or the token.

## Test data

Each scenario gets a random-suffixed local folder (created without `-Force`) and a
random-suffixed remote folder created with an **exclusive** create. If the remote folder already
exists, the scenario fails; the driver **never adopts** an existing folder. Every file is written as
exact bytes (no BOM, no added newline) and compared byte-for-byte on both sides, so a correctly
propagated fixture can never fail on a trailing newline and a mismatch can never pass.

The pair is proved before it is bound, repaired or removed: the driver snapshots `pairs list` before
and after `pairs add`, requires exactly one new id, and requires that id's line to name the exact
fixture, local root, remote root and account. It never selects a pair by a display-name substring,
and it removes only the proved id.

## Parameters

| Parameter | Meaning |
|---|---|
| `-StagingProof` | (required) Absolute path to the staging proof JSON above. |
| `-Scenario` | Exact scenario name, a substring, or `all`. |
| `-CommandTimeoutSeconds` | Timeout for quick commands (default 180). |
| `-RunTimeoutSeconds` | Timeout for `repair` and the log-growth wait (default 1800). |

## Scenarios implemented here

| Scenario | What it proves |
|---|---|
| `A02CreateOneSide` | A one-sided create lands at the same path with contents. |
| `A05EditOneSide` | An **existing-file** edit replaces the other copy in both directions. |
| `A07EditBoth` | Different edits keep both versions and raise a notice. |
| `A10DeleteOneSide` | A one-sided delete propagates; untouched files stay. |
| `A11EditVersusDelete` | The edit survives a remote delete, is restored, and raises the notice. |
| `A18MoveOneSide` | A one-sided move repeats as a move, not a delete and create. |
| `A19MoveVersusEdit` | A move against a remote edit keeps both and raises the notice. |
| `A21MoveVersusDelete` | The move survives a remote delete and raises the notice. |
| `A26SafeSave` | `x.tmp` renamed over `x` is a replace (both sides end with the new bytes), not a delete and create. |
| `A27CapitalRename` | A capitals-only PC rename propagates both ways. |
| `A28ExcludeKeepAndInclude` | `exclude --keep` leaves both copies; `include` compares the path at once. |
| `A28ExcludeNoKeep` | A no-keep exclusion warns with the recovery place, then removes with recovery. |
| `A39InUse` | A locked item raises a failure notice, unrelated work continues, and the item heals on the next run. |
| `A44ConcurrentDirections` | An upload and a download run in the same repair and both land. |
| `A47HostReadsPauseStop` | Host-down `status` exits 3 with the message via the raw seam; `logs` reads; a state change starts the host; a second start attaches; pause/resume work. |
| `A48RootBinRemovable` | On a **removable** volume that also has a Windows bin: delete on Dropbox, and the engine-removed PC copy lands in `<drive>\Recycle Bin` (not the Windows bin). |
| `A48NativeBinFixed` | On a **fixed** volume with a Windows bin: the engine-removed PC copy is observable in the Windows Recycle Bin by its exact fixture name and staging path. |
| `A49LogsWhileRunning` | The current run log is read **while the host is writing it** (observed growth), normal records are present, and per-file `Effect` lines appear only with `--show-files`. |
| `A50NestedPairs` | The outer pair skips the inner pair root; both pairs work; removing the inner pair re-compares its subtree. |

The bin arms classify as **NOT OBSERVED** (not PASS) when the staging volume does not have the
required drive type or native-bin evidence: a leftover `$Recycle.Bin` directory is not proof that
native recycling works. Each arm runs on a disjoint staging volume and never reads or writes a
production bin.

## Live-arm map

The plan's A-table (its Live column) assigns live proof of each arm. This driver implements the
live arms that need live observation of true sync, the log view or the bin. The remaining live arms
are proved by the plan's separately assigned staging/manual arms; the offline CI cases cover the
synthetic and decision parts only. No new rehearsal is added.

- **This driver:** A02, A05, A07, A10, A11, A18, A19, A21, A26, A27, A28, A39, A44, A47, A48,
  A49, A50.
- **Separately assigned staging/manual arms (plan Live column, not this driver):** A01, A03, A04,
  A06, A08, A09, A13, A14, A15, A17, A22, A23, A24, A29, A30, A31, A32, A34, A40, A41, A42, A43,
  A45, A46, A33, A36-A38, and the forced/large LIVE-01/02 arms.
- **Offline CI:** the synthetic and decision parts of every arm (for example A35 and A42's synthetic
  cases), never the live observation itself.

## Result status and exit code

- `PASS` - every attempted assertion passed.
- `FAIL` - an assertion or unexpected error failed; counted.
- `NOT OBSERVED` - the arm's required fixture (drive type or native bin) is absent; recorded, not
  counted as a failure and never reported as a PASS.

The script prints one line per scenario and a summary, and exits with the number of failures.

## Safety

- Works only inside its random-suffixed disposable roots under the staging root; never creates or
  modifies the supplied remote root itself.
- Never reaches the installed host, a real pair, production folders, or a production drive's root
  Recycle Bin; the staging proof must show the local volume is disjoint from production.
- The token comes from Windows Credential Manager and is never printed or persisted.
- Child processes run with proxy variables removed; the script's own requests bypass the proxy.
- The only deletions are the scenario's own files; the pair is removed only by its proved id.
