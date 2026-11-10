# Changelog

All notable changes to Sync Shark, one section per released version.

Entries are ordered by the date of the tagged commit, newest first, and each
section groups its changes as Added, Changed, Fixed, Removed and Internal. A
version marked "No source commit is recorded" was published from a build stamp
that has no matching commit in this repository, so there is nothing to describe.
Version numbers do not always descend: a few versions were tagged after later
numbers had already shipped, and their position here follows when their code
actually landed.

## [Unreleased]

## [0.3.81] - 2026-11-08

### Changed
- Closing an issue now finds it by its canonical path through an index, and the issue store upgrades its schema once.
- Rename handling refreshes its evidence before any provider effect and counts those reads in the live read budget.
- Setup commands share one registry identity and are routed by the version-order owner.

### Fixed
- A folder whose spelling was never recorded now settles correctly.
- Work held back after a silent source recovery is woken again.
- The Engine host drains safely after a listener or connection failure.
- A local excluded file with no binding no longer triggers remote reads for its notice.

### Internal
- Timer, fake Dropbox and test fixtures were made deterministic; the structural review verdicts for the combined lanes are recorded.

## [0.3.80] - 2026-11-08

### Changed
- The command line can run while the Engine host and automatic sync are running.
- Legacy operations are cut off behind one gate, so the Engine is the only sync path.
- A changed file during a Repair is retried before it is reported as a failure, and a folder whose anchor is reacquired finishes its unfinished children.

### Fixed
- A live admission that keeps going stale no longer retries in a tight loop.
- Stale children of a folder replaced by a file no longer loop on a local read failure or keep re-reading Dropbox metadata until the next Repair.
- A deleted folder tree no longer causes a hot refusal loop while it settles.
- A local folder delete no longer spins on a pair root that Dropbox still holds leftovers for.

### Internal
- User interface message text has one strings owner, and tunable values moved to the settings file.
- The engineering-spec structural checks were tightened.

## [0.3.79] - 2026-11-07

### Added
- A no-elevation, per-user setup installs Sync Shark beside the existing version and switches the active launcher; a running host keeps its version until the next start.

### Changed
- Startup is faster.
- Automatic sync keeps working while a Repair is owed.
- The first Repair after an upgrade restores deleted-file history faster.
- A notice choice applies to the replacement subject, and a stale error maps to the correct result.
- Sync no longer re-reads a file that Sync Shark itself just downloaded.

### Fixed
- A failed provider probe connection no longer stops the host or automatic sync.

### Internal
- The engineering-spec ratchet and its structural checks were tightened; setup and syncer debt was reduced.
- Test paths no longer touch real locations.
- Release builds no longer depend on a PowerShell 7 module path when signing.

## [0.3.78] - 2026-11-07

Hotfix: 0.3.77 plus one fix only.

### Fixed
- Repair can finish after safely refusing an item whose ancestor changed; ordinary automatic sync retains and processes the unfinished work instead of triggering another full Repair.

## [0.3.77] - 2026-11-07

### Added
- Automatic sync catches up once when it starts, so a change made while Sync Shark was not running is not missed.
- An ignored notice can be shown again and restored; the issue and conflict lists accept `--ignored`, and the help and capability list describe it.
- Deleted-file history is kept in compact form, and a pair whose saved history belongs to an older version acquires it on its next run.

### Changed
- The first start upgrades the store and runs a full Repair on each pair before automatic sync resumes.
- A Repair during a Dropbox outage waits and retries the same request quietly instead of raising a notice, and recovers on its own when the network returns.
- Rename progress names the current group before the rename is applied.
- Pair import keeps the pair's identity and source state.
- A notice keeps its identity across a dialog refresh and is checked again before an action is applied.

### Fixed
- A file or folder seen to change after a run has planned its effect is validated again before that effect, so a late edit is not overwritten or removed.
- A handled unreadable-child refusal no longer mistakes its own held dependent root for later ancestor evidence.
- A bounded batch no longer completes a folder before the children it still owes.
- Corrupt saved deletion history is reacquired within the current Repair instead of stalling Retry, and owed history work survives a Repair that exits early.
- A file that matches a name on the other side but belongs to another item is no longer paired with the wrong file.
- Waiting Dropbox entries are ordered, and tombstones stream without a second retained copy of their names.
- The engine host shuts down cleanly while status requests are unread, and a status subscription that fails completes instead of staying attached.
- Startup accepts a nine-step install journal and rejects a complete journal that has no application registration.
- A Pause during acquisition is recorded as a pause, and Resume continues at the next checkpoint.
- A transient failure to replace a local file is retried instead of failing the item.
- Unnamed working status text and the stale-state retry wording are corrected.

### Internal
- Fixture and ownership corrections across the scan, batch, history and startup checks; the live test root moved to an isolated testing folder; cost-check standards and the current-product documentation updated; internal labels removed, and an unreachable rejection fallback deleted.

## [0.3.76] - 2026-11-05

### Added
- Command line commands print readable output by default instead of raw structure.
- A read-only view of the log, with no effect on a running sync.

### Changed
- Scans admit work in bounded batches and apply back-pressure instead of piling up (#80).
- Automatic sync re-checks a folder delete that the provider still reports as running (#79).
- The command line reads through the running engine host (#92).
- The history of deleted files is searched within bounds, and attention lists page through their items.

### Fixed
- A file that cannot be read now raises a notice instead of failing silently (#95).
- Deleting a folder always completes its descendants first.
- Release review corrections across the engine and the command line.

## [0.3.75] - 2026-11-04

### Changed
- Reading large listings is faster and cheaper.
- Repair reports its startup and listing-drain phases, so a long first stage is explained.
- Startup diagnostics are recorded.

### Fixed
- A file deleted on one side stays deleted after the pair is handed over or the folder is included again.
- A file known only from saved state fails and retries through the same path as any other file.
- Case-only renames are adopted together instead of one at a time.
- Restored entries keep their place in the deletion history.

## [0.3.74] - 2026-11-04

### Added
- A full repair command group on the command line: repair, issue list, resolve and pair import, each with help, a capability row and the documented confirmation.
- Resolving every open issue at once, choosing an outcome for the first sync, and an explicit decision when a synced drive changes.
- One plain cause sentence on each notice, so an item states what went wrong.
- Pair import now brings the pair's baseline with it.
- Default ignored files for common Windows clutter such as desktop.ini, and no limit on how many ignore rules a pair may have.

### Changed
- The first run compares files that differ only in time, not only in size.
- Repair logging names the specific cause of each step and records stage progress.
- Settings are saved atomically and validated before they are written.
- Fewer locks and freshness checks are needed per item.
- Dropbox busy replies are retried instead of failing.
- A recreated drive journal no longer forces a reset.
- Automatic sync requeues a page that is still making progress instead of waiting for it to stall.

### Fixed
- A logging retention overflow no longer fails a Repair that has already finished.
- Rename bindings and child history survive a parent folder being moved.
- Case-only differences adopt the spelling already saved instead of creating a duplicate.
- Exclusions and unsafe removals are applied consistently.
- Settled folder members, saved-only rows and reused issue lists are no longer processed again.
- A name clash inside a subtree holds that subtree instead of the whole pair.
- Dropbox reply parsing, unreadable local folders, pair setup paths, cross-kind clashes and folder-deletion plans are corrected.
- The database reader waits for a lock instead of failing.
- Pair import keeps its identity, first-run history deletes behave, and the keep-both edge cases are fixed.
- Automatic sync notices a drive change and reacts to it.

### Removed
- The setting for a custom staging-file suffix.
- Keep-both cleanup on the reverse leg for excluded files.

### Internal
- Engine layers rebuilt, one-owner refactors, storage work, test infrastructure and coverage, logging and diagnostics, documentation, comment and privacy cleanup, a signed Debug build, dead-code removal and checkpoint commits.

## [0.3.73]
No source commit is recorded for this version.

## [0.3.72] - 2026-10-26

### Added
- A background tray client attached to the automatic sync host, showing status with Sync now, Pause and Resume.
- A needs-attention indicator, live per-pair progress and a `runs list` command.
- A durable supervisor log for automatic sync, and recording of failed or uncertain effects.
- Safe completion of a conflict choice, with any retained reconciliation blocker stated.

### Changed
- One owner now handles automatic sync's logon startup.
- Run summaries separate changed paths, reconciliation traces come from the shared message catalogue, and diagnostics are sanitized.
- Reconciliation of missed changes runs in bounded batches.

### Fixed
- A folder that differs only in letter case no longer stalls.
- A stale cached listing, an unlistable refresh parent and an empty first status cache are handled.
- A failed automatic run keeps its summary, and a status deadline no longer drops it.
- Stopping one pair no longer cancels unrelated listeners.
- The supervisor relaunches after any exit that was not a deliberate stop.
- Automatic sync stays quiet inside a Repair scope, and legacy gap work is retired only once proven complete.
- Deferred folders are retried, invalid change notifications are ignored, and conflict and case-only moves are classified correctly.
- Keep-version settlement completes without a frozen baseline.

### Removed
- The legacy GUI startup registration and startup mutex, synchronous startup observation, and the old synthetic promotion path.

### Internal
- Additional checks, a UI fixture and quick lane in continuous integration, one-owner refactors, and documentation updates.

## [0.3.71] - 2026-10-26

### Changed
- Throttling now frees slots when a provider cooldown ends, recovers its window additively and bounds how long it waits on a rate limit.
- Retry outcomes settle through the notice path instead of leaving work pending.

### Fixed
- Deletion stays safe after throttling, and planning traces record the decision that was taken.

## [0.3.70] - 2026-10-25

### Changed
- File-identity probes use the object index, so a large store answers them without a scan.

## [0.3.69] - 2026-10-25

### Changed
- Links and left-alone placeholder files never enter planning, so permanent holds no longer accumulate.
- Ignored paths are dropped from the settlement lane.
- Content-proof failures carry their stage and native error, and each file gets its own failure notice with Retry and Ignore.
- Urgent content proofs are prioritized while bulk and pair pauses still apply.

### Fixed
- Tied case-only renames are held for a decision instead of one spelling being chosen.
- Ignored content in a reviewed folder removal is proven over the right scope.
- Automatic sync logs are bounded and rotated.

## [0.3.68] - 2026-10-25

### Changed
- Planner-owned settlement intake is bounded by a durable cursor, so eligible rows past the per-run cap are reached on the next run instead of after a whole lap.
- Ignored paths stay out of the settle lane.
- Urgent content proofs are prioritized and inventory joins are scoped.

### Fixed
- A planning queue that is still shrinking re-runs immediately, while a page of held rows no longer loops.
- Settlement protection uses its restore snapshot, and compact baseline paths resolve as the baseline reader does.

## [0.3.67] - 2026-10-24

### Changed
- Paused deletes and moves are not settled, and display summaries are kept out of root holds.

## [0.3.66] - 2026-10-24

### Changed
- Journal replay reads in pages, and the debt for cached identities is retired.

### Fixed
- Journal and restore fixtures match the current contracts.

## [0.3.65] - 2026-10-24

### Changed
- Provider cooldowns and throttling were reworked, and stale NTFS identities are rediscovered.
- Protection snapshots are retained and scan retention is reduced.

## [0.3.64] - 2026-10-24

### Added
- Standalone content indexing with `index run` and `index status`.
- Per-item results on the first sync, Stop syncing as a choice of its own, and restore again from a notice or the command line.
- Automatic sync wakes on resume and on network return, and pairs can sync concurrently.
- Index and verification counts in reports, and protection against overwriting a local file with older content.

### Changed
- One retry policy for the whole product (ten seconds with exponential backoff, capped at five minutes).
- Ignore is no longer a form of exclusion.
- Diagnosis states one cause per problem, and notice history is kept and bounded.
- Deletion needs proof before it is applied, and a cloud-only file needs a verified hash before it is used.
- Files edited on both sides are kept with labelled copies.
- Case-only renames propagate, and the command line waits for its result.
- The v2 engine is the default, and large libraries stay responsive through indexed, paged reads.
- Damaged state stops writes, folder restores persist, and watched folders fall back to polling.
- CSV export is bounded.

### Fixed
- A prepared delete can no longer vanish, an interrupted case rename is recovered, and content-check debt is not lost.
- Deferred evidence is kept, refusal notices close, and the quarantine decision no longer races.
- Notice lookup no longer misses or duplicates, and a cancelled link copy does not leave a receipt.
- Progress is counted for gaps, the root re-list debt is handled, and Ignore no longer closes an actionable notice.
- Read-only control folders, truncated arguments, incomplete preview notices and sampled counts are corrected.
- Folder dispatch is revalidated, removal proof is reviewed, preserved-absent replacement is handled, and conflict-copy successors no longer loop.
- Restarting while stopping is safe, and a counted account halt behaves.

### Removed
- The global run gate and the GUI outer gate, foreground hashing and a separate scanner hash engine.
- Per-feature fixed retry timers, duplicate notice wording, and implicit path exclusion from Ignore.

### Internal
- Test fixtures, six continuous-integration shards and twenty-nine suites, dependency and layout checks, a spec-debt ratchet, refactors, schema migrations, documentation and neutral placeholders.

## [0.3.63]
No source commit is recorded for this version.

## [0.3.48] - 2026-10-22

This release records a long stretch of work (935 commits) that was published
under one version.

### Added
- A reviewed first sync: keep both, replace one file, or replace a whole folder, per item.
- Restore from Recovery, antivirus quarantine holds and deletion-history holds.
- Actionable notices with paging, and plan-only preview and export.
- Pause controls, and a background sync host.
- A change journal that grows on its own, run logs with live progress, low-disk handling and date repair.
- Automatic syncing after a run: folder watchers, debounce, retries, holds, wake on resume and tray status.
- First-sync exclusions for rebuildable folders such as JavaScript dependencies, Python caches and virtual environments, .NET build output and generated script bundles.
- Per-folder first-sync choices on the command line, pending-decision commands, and pair-creation exclusions.
- A full rescan, recovery-copy reports, open-conflict reports, pair retirement and a Dropbox command group.
- A notice when a delete stays stuck for six hours, and Cloud Files / online-only file handling.
- Recovery location and retention settings, and a move of the data location.
- Conflict handling where the newest file wins, older copies are archived, and a tie opens for a decision.
- Deletion-aware first sync, refusal of overlapping pairs, and retry of a failed pair creation.
- A default set of ignored files such as desktop.ini.

### Changed
- A reviewed first sync becomes the baseline.
- Fewer provider requests: batches of up to 1,000 for deletes and downloads, 250 per commit for transfers by default.
- Throttle counts state the real number, downloads keep their original time, and forbidden Dropbox names are mapped.
- Notices use plain causes, recovery skips identical copies, and conflicts create no automatic copies.
- Excluded work is visible, retries are narrower, paused really means paused, and run outcomes are truthful.
- One-way state database upgrade with a backup note.
- Scanning and first sync are faster; progress is ordered and bounded; resource use adapts to the machine.
- Safer defaults protect product folders, and more settings are exposed.

### Fixed
- A low-space notice is not raised twice, and a pause during a delete is honoured.
- A partial scan no longer deletes, and an edit during a journal gap is picked up.
- Large folder creation, malformed batched probes, free-space refusal, account verification and upload-session starts are corrected.
- Batched deletes are root-verified, interrupted conflict copies are cleaned up, and restore holds only the retry budget it needs.
- Quota holds, provider-refused names, export format, progress counts, stale classification and selected repairs are corrected.
- A spool handle leak is fixed and a locked store no longer blocks work.
- Dropbox operations report their real result instead of hanging, and account and root checks are bounded.
- Deletes and moves are interruption-safe, an unknown delete outcome is never replayed, and apply cannot delete a row it has not seen.
- AutoSync resumes after a failure, notices close only when the problem is gone, and the tray tells the truth.
- Case handling, sign-in and connection messages, malformed cloud metadata and the installer are all corrected.

### Removed
- The read-only "next run" plan preview, the separate recovery-location file, post-run conflict sweeps and the old first-sync deletion-review plumbing.

### Internal
- Rebuilt engine composition, continuous integration and regression checks, schema migration consolidation, one-owner refactors, a Files On-Demand foundation that is present but not wired, benchmarks and documentation.

## [0.3.62] - 2026-10-08

### Fixed
- A publication recovered in one build is recognised correctly by another.

## [0.3.61] - 2026-10-08

### Added
- Explicit release predecessor checks, a resource contract gate for the published executable, and a test-only cloned-state adapter.
- Recovery of a publication and of the application after a failed release.

### Changed
- Updater and release recovery paths were completed, and first-sync preparation and monitors were extended.
- Several performance improvements.

### Fixed
- User-interface corrections across the shell.

## [0.3.60]
No source commit is recorded for this version.

## [0.3.59]
No source commit is recorded for this version.

## [0.3.58]
No source commit is recorded for this version.

## [0.3.57]
No source commit is recorded for this version.

## [0.3.56]
No source commit is recorded for this version.

## [0.3.55]
No source commit is recorded for this version.

## [0.3.54]
No source commit is recorded for this version.

## [0.3.53]
No source commit is recorded for this version.

## [0.3.52]
No source commit is recorded for this version.

## [0.3.51]
No source commit is recorded for this version.

## [0.3.50]
No source commit is recorded for this version.

## [0.3.49]
No source commit is recorded for this version.

## [0.3.47] - 2026-09-25

### Fixed
- A failure to reach an account is retained instead of being dropped.
- One shared volume-identity resolver is used for every local path.
- Home's last-success state is scoped to the right pair.
- The sign-in return step is acknowledged.

## [0.3.46] - 2026-09-25

### Added
- First-sync failures are classified and reported in the window and on the command line.

## [0.3.45] - 2026-09-24

### Added
- A sharing summary per account, with classified reasons when Google refuses to share.

## [0.3.44] - 2026-09-23

### Added
- Provider marks in the account menu and a working Browse glyph.
- An Endpoints column in Activity.

### Changed
- The installed process identity is shared, and legacy pairs are bound to the current namespace.

### Fixed
- Release packaging and rollback binding for this version.

## [0.3.43] - 2026-09-22

### Fixed
- Changing the account in the header no longer navigates away and re-opens retained run details.

## [0.3.42] - 2026-09-22

### Fixed
- Installed Activity, Recovery and Storage presentation.

## [0.3.41] - 2026-09-22

### Changed
- Planner endpoint history is retained for later decisions.

## [0.3.40] - 2026-09-22

### Fixed
- Coordinated transfer summaries are accurate.
- Files with equal content but a different revision are reconciled.

## [0.3.39] - 2026-09-22

### Added
- Provider-neutral folder pairs, Google Drive sharing, full command-line parity and a more compact window.

## [0.3.38] - 2026-09-18

### Fixed
- Activity identifies the provider correctly, and the first sync reads back live results.

## [0.3.37] - 2026-09-18

### Added
- A provider-aware interface and Google approval, preparing this release.

## [0.3.36] - 2026-09-18

### Added
- Google Drive account routing and guarded sync, with full command-line support.
- An isolated Google Drive provider with protected sign-in seams.
- A sharing feed with revoke, plus a local Create Folder action available from the pair editor and the command line.
- Runs, Recovery, Storage and Folder Pairs share one compact row, filter and details pattern.
- A shared interface foundation: one page header, quiet search field, Simple/Detailed switch and details host.
- Pause scopes with fixed options (5 minutes to indefinitely) and an explicit pause and resume.
- Typed full-path entry in the folder picker, and bounded deep-tree browsing.
- Provider marks and exact-account labels on Connections, and settings split into App Info and Diagnostics.

### Fixed
- A saved recovery copy's Version column now displays.
- Activity layout and ordering, and typed-path draft generation.
- File Requests no longer offer link-only actions, and request rows show the application image.
- The sign-in return page states that the result is still pending instead of claiming success.

## [0.3.35] - 2026-09-17

### Added
- A full command-line control surface and Discovery diagnostics.

## [0.3.34] - 2026-09-16

### Fixed
- Explorer package changes are bound correctly and Discovery consumers are proven.

## [0.3.33] - 2026-09-16

### Fixed
- A Discovery deadline recovers instead of leaving the scan stuck.

## [0.3.32] - 2026-09-16

### Changed
- Long discovery scans report progress, and bootstrap icon access is repaired.

## [0.3.31] - 2026-09-16

### Changed
- A versioned runtime is protected, and publisher roles are bound.

## [0.3.30] - 2026-09-16

### Fixed
- The Explorer packaged runtime and startup dispatch.

## [0.3.29] - 2026-09-16

### Changed
- Whole-volume topology is bounded separately, and admitted Discovery roots are compacted.

## [0.3.28] - 2026-09-15

### Changed
- Discovery failures are diagnosed without changing scanner limits.

### Fixed
- Native package identities in release test inputs.

## [0.3.27] - 2026-09-15

### Changed
- Discovery retention is bounded to the selected trees.

### Fixed
- Elevation resource access.

## [0.3.26] - 2026-09-15

### Fixed
- Discovery task ownership and native elevation icon resources.

## [0.3.25] - 2026-09-15

### Added
- Bounded read-only Discovery verification inside the installed application.

## [0.3.24] - 2026-09-15

### Fixed
- The Task Scheduler account identity, and setup errors are now bounded and shown.

## [0.3.23] - 2026-09-15

### Changed
- Labelled interface summaries and corrected projected preview counts.
- The signed release archive includes the rollback contract.

## [0.3.22] - 2026-09-15

### Fixed
- Native interface regressions, and an open pair draft survives navigation.

## [0.3.21] - 2026-09-15

### Fixed
- Discovery linked identity and the brand elevation executable.

## [0.3.20] - 2026-09-15

### Changed
- Installed path handling and non-mutating maintenance admission.
- A more compact workspace, richer tray metadata and a themed folder picker.

### Fixed
- The Explorer lifecycle command is exported for installed release scripts.
- Packaged Explorer binding and embedded setup recovery.
- Discovery setup is hardened.

## [0.3.19] - 2026-09-15

### Added
- Multi-account storage, sharing and Windows integration.

### Changed
- The exact installed version is kept for signed update compatibility.
- Windows x64 release restore is bound without changing dependency versions.
- Isolated transaction snapshots stay separate from live mutable state.

### Fixed
- Interface filter retention and attention consistency.
- The tray tooltip is split across two lines.
- Partial Explorer registration cleanup is corrected.
- Known Release dependency copies are cleaned only inside the owner output boundary.

## [0.3.18] - 2026-09-14

### Changed
- A checkpoint of the combined client features and interface polish.

## [0.3.17] - 2026-09-13

### Fixed
- Home navigation layout, and overflow command icons are finished.
- One active sidebar selection is kept across utility and primary pages.

## [0.3.16] - 2026-09-13

### Fixed
- Installed Recovery navigation.

### Added
- Live inventory progress is shown.

## [0.3.15] - 2026-09-13

### Added
- One unified workspace with detailed activity and responsive folder browsing.

### Fixed
- Monitor warnings are attributed to the pair that produced them.

## [0.3.14] - 2026-09-13

### Fixed
- Retained activity is clearer, and background status is bound to the correct pair.

## [0.3.13] - 2026-09-13

### Changed
- Home, Activity and the tray were rebuilt with responsive status projections.
- Guided sync workflows and live activity proofs were completed.

### Fixed
- Live activity item types come from the authoritative source.
- Manual sync drafts are guarded and the release identity is bound.
- Unsaved draft admission paths are exercised.

## [0.3.12] - 2026-09-12

### Added
- A complete guided sync client iteration, with a Recovery chooser and constrained Recovery folder browsing.

### Fixed
- Dropbox containment lifecycle gaps.
- Containment consumer admission gaps.
- Predecessor identity gaps.

## [0.3.11] - 2026-09-11

### Changed
- Dropbox product artifacts are contained.

### Fixed
- A verified Dropbox binding stays connected during startup.

## [0.3.10] - 2026-09-11

### Added
- Hardened RFC 3161 timestamp admission, bound to the time it was issued.
- Retained manifests for every adverse state, including rejected rollbacks.
- A transaction receipt bound to the package commit.
- Bounded rollback state transitions, and refusal of unsafe rollback state before any change.

### Fixed
- Dropbox connection startup status.
- Crash recovery and parser acceptance gaps.
- Release recovery and provenance proof.
- Interface recovery paths and release verification.
- Folder-pair interface acceptance and editor interactions.

## [0.3.9] - 2026-09-10

### Fixed
- Pair editor architecture repairs.

### Internal
- Build identity aligned for this version.

## [0.3.8] - 2026-09-10

### Fixed
- The pair editor layout, and the status pill is restored.

## [0.3.7] - 2026-09-10

### Fixed
- Saved pair folder-tree bindings.

## [0.3.6] - 2026-09-10

### Changed
- The pair editor is redesigned around full folder trees.

## [0.3.5] - 2026-09-09

### Fixed
- Healthy roots render without their subfolders.
- Repaired pair health clears immediately.

## [0.3.4] - 2026-09-09

### Fixed
- Degraded pair health and hierarchy rendering.

## [0.3.3] - 2026-09-09

### Fixed
- Integrated pair editing, and native Dropbox roots.

## [0.3.2] - 2026-09-09

### Changed
- Folder pair management is complete, including the pair draft workflow.

## [0.3.1] - 2026-09-09

### Changed
- The folder pairs workflow is redesigned.

## [0.3.0] - 2026-09-08

### Added
- A complete Dropbox sync client.
- Cloud Files availability is separated from version identity.
- Recovery resume for Dropbox metadata, and recovery-copy resume.

### Fixed
- Nested folder identity verification.
- Nested local replacement path recovery.
- Dropbox nested folder acknowledgements and recursive root echoes.
- Directory subtree move coordination and object-scoped planning authority.
- Cross-pair observation ordering and crash lease probes.
- Reproducible external release builds.

### Changed
- Interactive Dropbox folder browsing is bounded.
- Availability adoption is interruption-safe.

## [0.2.4] - 2026-09-04

### Fixed
- The result of an archived deletion is retained.

## [0.2.3] - 2026-09-04

### Fixed
- Revision churn after a Dropbox rename.

## [0.2.2] - 2026-09-04

### Fixed
- Downloads are requested at the exact revision.

## [0.2.1] - 2026-09-04

### Added
- Pair configuration is authoritative in the local database, with commands routed through the runtime host.
- Root identity is bound to the retained volume and to the Dropbox account namespace.
- Preserve-both conflict resolution, and predecessor copies retained before a replacement or a propagated delete.

### Changed
- Local roots keep their authority across a path rename.
- Cross-pair moves are ordered by their verified destination.
- Durable monitor health and recovery are surfaced in the interface.

### Fixed
- The Dropbox folder-create metadata shape.
- Interrupted effects are recovered before startup planning.
- Overlapping physical roots are rejected.
- Move correlation is limited to the current authority.

## [0.2.0] - 2026-09-02

### Added
- The SQLite authority foundation, with one-way state upgrades.
- A recovery-owned true-sync substrate that survives a crash.
- Verified bidirectional replacements, and crash-recoverable protected file writes.
- Automated, recovery-safe sync operations as a baseline.

### Fixed
- Transfer ownership during cleanup.
- Sync artifacts are recovered before the entry ceiling.
- Bidirectional recovery boundaries.
- Pair-bound recovery and interface state.

### Changed
- Sync is gated to create-new transfers.

## [0.1.1] - 2026-09-01

### Added
- The Sync Shark user-interface foundation.

### Fixed
- Themed table selection.
- Acceptance findings from this version.

## [0.1.0] - 2026-09-01

### Added
- A provider-free reconciliation core.
- A bounded execution engine.
- Protected Dropbox provider integration, and an identity-bound create-only upload pilot.
- An additive Full Dropbox manual pilot.
- Pair-bound sync safety, and reserved, recoverable pilot and recovery state.
