# Sync Shark live acceptance tests

`Invoke-V2LiveTests.ps1` drives the shipped `syncshark.exe` command line (see
[`CLI-CAPABILITIES.md`](../../CLI-CAPABILITIES.md)) against a real Dropbox test folder and a real
local folder. It is a **live** test: it creates its own pair, forces the first comparison,
mutates the local side with PowerShell and the remote side through the Dropbox HTTP API, forces
another comparison, and asserts the outcome on **both sides**. It also checks the run logs: the
current file is readable while the host writes it, and individual file operations appear only
with `--show-files`.

It is Windows PowerShell 5.1 compatible and does not run itself. Run it when you mean to exercise
the test account. It is not part of the offline CI matrix.

## How the new CLI is driven

The command line prints plain text; there is no JSON envelope and there is no `dropbox` command
group. The driver uses only the registered commands:

| Step | Command |
|---|---|
| create the pair | `pairs add --name <name> --local <folder> --path <folder> --account <id> [--first-sync newest\|pc\|dropbox]` |
| confirm the roots | `pairs bind --pair <id> --yes` |
| force a comparison | `repair --pair <id>` |
| inspect | `status [--pair <id>]`, `notices --pair <id>` |
| control the host | `host start`, `host stop`, `pause [--pair <id>]`, `resume [--pair <id>]` |
| read logs | `logs --pair <id> --run <id> [--show-files]` |
| finish | `pairs remove --pair <id>` |

`pairs add` prints the new pair id; where it does not, the driver reads `pairs list`. Pair ids
and run ids are the 32-hex `N`-format identifiers, and a stored run is also found by name under the
product's `logs` folder (`<pairId>-<runId>.log`). The host keeps the pairs running; `repair`
returns when its pass has finished, so the driver asserts immediately afterwards.

Every command's exit code, stdout and stderr is written under `-ReportRoot`.

## Dropbox side: HTTP API with a test-account token

Remote changes and checks use the Dropbox HTTP API v2 directly (there is no CLI provider group):

| Call | Used for |
|---|---|
| `files/create_folder_v2` | create each scenario's own remote folder under `-RemoteBase` |
| `files/upload` | create or edit a remote file |
| `files/download` | read a remote file's content |
| `files/get_metadata` | prove a remote path exists (or is absent) |
| `files/delete_v2` | delete a remote path |
| `files/move_v2` | move a remote path (move-vs-edit cases) |
| `files/list_folder` + `.../continue` | list a remote folder recursively |
| `files/list_revisions` | prove a replaced version survives in Dropbox history |

The access token is read from the environment variable named by `-TokenVariable` (default
`SYNCSHARK_LIVE_DROPBOX_TOKEN`). It is never written to a report or passed on a command line. Use a
dedicated test account.

## Prerequisites

- The new build under test (`syncshark.exe`) is installed or staged; pass its absolute path as
  `-Exe`.
- A Dropbox connection for the test account is signed in, and its exact connection id is passed as
  `-Account`. The engine's own store keeps the connection.
- `-RemoteBase` already exists under the test account; the script never creates or changes it.
- The local volume has room for the scenario files and the engine's own recovery/bin locations.

Each scenario's local and remote folders carry a random 8-hex suffix. A local folder is created
without `-Force` and a remote folder with an exclusive create, so an existing folder is never
adopted. A double quote in a path parameter is rejected by the argument quoting rules; keep paths
free of embedded quotes.

## Parameters

| Parameter | Default | Meaning |
|---|---|---|
| `-Exe` | (required) | Absolute path to `syncshark.exe`. |
| `-Account` | (required) | Retained Dropbox connection id for the test account. |
| `-LocalBase` | `Join-Path $env:LOCALAPPDATA 'SyncSharkTests\live'` | Local root; each scenario gets a random-suffixed subfolder. |
| `-RemoteBase` | `/Apps/Sync Shark/Testing/live` | Existing Dropbox root for scenario subfolders; never created or modified. |
| `-Scenario` | `all` | Exact scenario name, a substring, or `all`. |
| `-TokenVariable` | `SYNCSHARK_LIVE_DROPBOX_TOKEN` | Environment variable holding the test-account token. |
| `-CommandTimeoutSeconds` | `180` | Timeout for quick commands. |
| `-RunTimeoutSeconds` | `1800` | Timeout for `repair`. |
| `-ReportRoot` | `<LocalBase>\_reports` | Where per-command output is written. |

## Scenarios

Every scenario runs end to end and asserts both sides:

| Scenario | What it asserts |
|---|---|
| `FirstSyncNewestWins` | A newer local file and an older remote file at the same path converge on the newer version on both sides; the replaced version stays in Dropbox history. |
| `OneSidedEdits` | A local-only change uploads and a remote-only change downloads; both sides converge. |
| `BothSidesEdited` | Both sides edited differently keep both versions (a notice is raised and both names exist on Dropbox). |
| `EditVersusDelete` | A local edit against a remote delete restores the edit on Dropbox. |
| `LocalMove` | A local rename propagates as a move on both sides. |
| `LocalDelete` | A local delete propagates; untouched files stay. |
| `LocalRootMissingNoDeletes` | A missing local root causes no deletes; remote content is unchanged, then the pair converges. |
| `FolderDeleteEditedChild` | A removed local folder keeps a remotely-edited child and deletes the unchanged one. |
| `HostReadsAndPause` | A host-down read reports the host is not running and never starts it; `logs` still reads files; `pause`/`resume` are reported. |
| `LogsReadableAndPerFileSwitch` | The current log is readable while the host runs; per-file lines appear only with `--show-files` (A49). |
| `BinRemovableWithWindowsBin` | On a **removable** volume, a removed file goes to the engine's own root `Recycle Bin`, not the Windows bin, even when a Windows bin also exists (R4; F111). |

`BinRemovableWithWindowsBin` is not applicable when `-LocalBase` is on a fixed drive; it records a
note and skips that arm. Run it with `-LocalBase` on a removable volume that also has a Windows
Recycle Bin to prove the removable-with-Windows-bin case that the registered A48 arm must not
cover.

## Result status and exit code

- `PASS` - every attempted assertion passed; the reason lists any notes.
- `FAIL` - an assertion or unexpected error failed; counted.

The script prints one line per scenario and a summary table, and exits with the number of
failures (`0` means all passed).

## Safety

- The only deletions are the scenario's own local files inside its random-suffixed local folder and
  its own files inside its random-suffixed remote folder. No permanent delete is used.
- `-RemoteBase` is never created or modified; each scenario creates only its own subfolder.
- The local scenario folder is created without `-Force`; an existing one is never adopted.
- The token is read from the environment and never written to a report.
- Child CLI processes run with proxy variables removed, so an agent-injected proxy cannot reach the
  engine's transports.

## Permitted auxiliary writes

A live run is not limited to the scenario folders: the engine writes its own durable state,
recovery, logs and (on a removable volume) its root `Recycle Bin`. These are the engine's normal
product paths; the driver prints the local and remote bases and the report folder at startup.
