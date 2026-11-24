# Live acceptance

`Invoke-V2LiveTests.ps1` stops with a terminating error before launching a process, reading a credential, making a request or touching a fixture. It accepts no staging assertion on trust. There are **no runnable live cases** in this checkout. A refusal is not a pass, and hosted checks are not evidence of a live result.

The host uses the Windows `LocalApplicationData` known folder, not the `LOCALAPPDATA` environment variable. It may migrate a store, start pairs and trim volumes before a CLI reply. Staging must establish its own Windows identity and known folder under the disposable root, isolated candidate executables, disjoint pair and recovery volumes, and ownership of the specific host process before fixture work. A fresh store need not have a database until the first add. The CLI verifies the Dropbox account and namespace internally; a token and remote path alone do not establish that binding. A fixture needs that same verified namespace, a private test base and exact add, list and removal receipts for its pair. No staging interface supplying these proofs is available here. There is no fallback to an installed host, guessed pair ID, shared bin or default Dropbox namespace.

## Outstanding staging observations

All live arms remain **unproved**. In particular, the following are not substitutes for the required observations:

| Cases | Witness needed in isolated staging |
|---|---|
| A02, A05, A07, A10, A11 | Exact endpoint sets, including empty folders, and exact file bytes. For replacement or deletion, recover the displaced version from its own history or bin; for competing edits, verify both versions on both sides and the specified notice. |
| A18, A19, A21, A26 | Match object identity before and after a move or safe-save, not just final names. Prove old-name absence, the expected surviving versions and the case-specific notice. |
| A27 | Enumerate actual spelling on each side. A case-only move retains identity; a differing rename preserves each spelling with the specified notice. |
| A28, A39, A50 | Observe the exclusion's selected side and recovered bytes; inclusion compares without a forced Repair. For an in-use file, verify the actionable notice and independent work. For nested pairs, observe the outer pair skipping the inner root, then comparing the quiet range on removal. |
| A34 | For each first-sync strategy, verify the winner and exact loser custody, including equal-second and untrusted-time variants. |
| A44 | An upload and download are simultaneously in flight. Both eventually finishing is not evidence of overlap. |
| A47 | Observe the owned host exit. Non-log reads fail without starting it; detached logs read the expected stored run without starting it. A mutation starts a new owned host; a second start attaches to it. Verify pause and resume. |
| A48 | On an isolated volume with a proved recovery route, match the new recovery entry to the exact original fixture, operation and bytes. Never enumerate the aggregate Windows Recycle Bin or inspect a production recovery volume. |
| A49 | Identify the active writer for the same pair and run across both CLI reads. Verify normal readable records, with per-file cards visible only when requested. Growth of a closed log is not a witness. |

No live observation is claimed for any other case. A33 and A36–A38 are offline cases, not staging handoffs. There is no direct upload implementation here; a future content upload on the verified test connection must send `application/octet-stream`. Never retain raw command streams, error bodies, headers or credentials as evidence.
