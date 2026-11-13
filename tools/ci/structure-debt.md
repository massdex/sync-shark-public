# Structure debt register

Owner: docs/v2-engineering-standards.md §1.3. Source: spec-ratchet review of rc/0377 3f6745416 (226 structural findings,
adjudicated 2026-11-06). Every row is a confirmed violation still open.

**Rule (AGENTS.md):** a change that edits a file listed here clears that file's rows in the same change (split, record
the reason, or fix the type), or records in its review why a row cannot be cleared yet. Clearing a row deletes it here.
Rows are never dropped any other way. The CI register check fails a change that touches a listed file without
updating its section.

CI receipts are generated only by `python -I tools/ci/check-spec-ratchet.py --refresh-register src/<file>.cs`
(after reviewing that file's section). Multiple exact paths may follow the option; cleared sections are deleted,
not refreshed. `tools/ci/spec_reviews.py` owns the shared code hash used by this updater and the checker:
SHA-256 over canonical code tokens, with comments/directives excluded, literal contents retained, whitespace
canonicalized and CRLF/CR normalized to LF. A changed file needs a refreshed section receipt or an exact-content
`register_reviews` entry in `tools/ci/spec-verdicts.json`, with a non-empty reason and reviewer family.

The verdict ledger records each reviewed unit's identity, original content hash, size, member set, verdict and
reviewer family. Non-material edits carry forward against that original anchor; growth over 10% or any gained
member requires a fresh review, never a rolling baseline. A REASON-RECORDED verdict also requires a non-empty
comment immediately above the unit. N/R seed rows remain SPLIT-PENDING until the reason-writing lane records
and verifies the reason; S rows remain linked to this register. R2 also owns the 13 existing-reason rows. Removing a receipt or ledger is not clearance.

Human provenance fields `source_row` and `source_revision` identify the original review row and reviewed source;
they are audit references, not freshness inputs. The three fresh split verdicts for rows 43, 207 and 226
were reviewed at source 1ba4297d3; original anchors remain at 3f6745416.
An XML summary satisfies the mechanical nonempty-comment check; semantic review still verifies its reason.

Literals (§2.1/§2.4) are tracked as open items, not per row: 112 unowned user-facing strings move to their owner with
the CLI consolidation. The tunables item is not cleared. The final adjudication counted 30 violations (one double
count removed; AdaptiveLimiter's cited policy accepted). Lane R3 moved the application tunables to their settings
data files (`src/SyncShark.Core/V2/v2-tunables.json`, owned by `V2Tunables`; `src/SyncShark.Engine/engine-settings.json`,
owned by `EngineSettings`). Each adjudicated site R3 did not move keeps the status below; none is moved into user
settings to meet a count.

| Site | Status | Reason |
|---|---|---|
| `src/SyncShark.Core/SyncDiscoveryClient.cs:59` 20 s wait | RETAINED | Keep the current bounded manual wait for this release; any change to immediate refusal or release/cancellation waiting requires a separate decision. Immediate refusal reduces latency but changes manual-check behavior; indefinite waiting can hang behind a stuck discovery. No timing choice is inferred from planned v1 retirement. |
| `src/SyncShark.Core/V2/Adapters/EngineComposition.cs:12` write slice | RETAINED | Transport bound adopted from Okio's 64 KiB slice (READING 8.4); a transport fact, not a user tunable. |
| `src/SyncShark.Core/V2/ContentBlockHasher.cs:8` block size | RETAINED | Dropbox content-hash format (4 MiB blocks); a provider format fact owned by the hasher. |
| `src/SyncShark.Core/NtfsUsnJournal.cs:22-27` record layouts | RETAINED | Native USN structure sizes are owned by `V2/NtfsRecordLayout.cs`, with Windows SDK citations; `NtfsUsnJournal.MinimumPageBufferBytes` derives the native buffer minimum from that owner for the Engine settings parser. |
| `src/SyncShark.Core/NtfsIdentityPaths.cs` FILE_ID_DESCRIPTOR | RETAINED | Windows SDK native layout has one owner: `NtfsIdentityPaths.OpenById`; absence verification, both journal gap opens and the native Engine fixture now call it. Descriptor Size is derived from its explicit layout. |
| `src/SyncShark.Core/V2/NtfsRecordLayout.cs` name/USN bounds | RETAINED | NTFS maximum name component (255 UTF-16 units) and aligned V2 record size have one dependency-safe fact owner with SDK citations. Logical replay requires a whole record; the native buffer separately adds its continuation prefix. |
| `src/SyncShark.Engine/SettingsParser.cs:168-169` atomic-replace ceilings | RETAINED | Keep the current 10-attempt/60-second-input ceilings for this release; the 511-minute maximum remains a follow-up policy question after 0.3.80. Current combined maximum waits sum to 511 minutes; the Win32 wait range does not derive these two independent limits. Removing them can overflow the exponential schedule or prolong a synchronous publication hold. |
| `src/SyncShark.Engine/Storage/StateSchema.cs:821-822` deleted-history batch | RETAINED | Keep the 5,000-key persisted block limit and current batching for this release; any separately compatible batch-policy change requires measured lock-hold evidence. StateEncoding validates both encoded and decoded counts against this bound. Lowering it can reject saved history; changing batching without measurements can worsen lock holds. The unsupported measurement claim is removed; stored rows and numeric behavior are unchanged. |

Totals: 207 rows in 93 files. Planned own lane: src/SyncShark.Engine/Storage/StateStore.cs (0.3.78).

## Tooling review reasons

These tooling records are not runtime-source debt rows and do not change the source totals above.
Line ranges describe this correction; named units remain the identity if subsequent edits move them.

| Unit | Jobs and line ranges | Recorded reason |
|---|---|---|
| `tools/ci/check-spec-ratchet.py:collect_review_units` | Evidence construction 253-257; shared owner/size/member inventory 259-271; member/local units 272-289; class units 290-295; file unit 296-299. | These are one review-unit output from one parsed inventory. Keeping its accumulation together avoids re-parsing declarations or passing a second mutable inventory between stages; receipt/freshness policy stays in spec_reviews. |
| `tools/ci/check-spec-ratchet.py` | Source findings 84-357 (literal counters 117-179, declaration inventory 182-299, file assembly 302-338); baseline/comparison pipeline 360-498; CLI orchestration 501-517. | SPLIT-PENDING: literal classification is a separate job with two consumers. Retained during O3: the SQL permission heuristic is removed and both scans consume the literals-owner artifact; the remaining heuristic classifier/inventory split is separate step-5 work. Owner and destination: refactor stream A, SPEC-2 step 5 (shared inventory). Trigger: step-5 inventory work; integration retains this finite obligation if that work is not scheduled. Clear after the classifier has one inventory owner, both consumers use it, and enforcement fixtures and cross-family review pass. |
| `tools/ci/spec_cslex.py` | Mask literals/comments and exact UTF-8 token spans 12–191; delimiter matching 194–222; parsed member representation 232–248; list/signature parsing 251–406; type/member parsing 409–529; source coordinates 532–535. | These stages implement the existing offset-preserving C# lexical parser. They share masked-source offsets and one member representation; callers consume this parser as one module, while structural decisions remain outside it. |
| `tools/ci/test-spec-ratchet.py` | Synthetic source/artifact fixtures 22-39; public checker contracts 42-605; mutation controls 608-753; pinned-release and artifact entry-point contracts 757-1033; unittest entry 1036-1037. | SPLIT-PENDING: trusted-release cases have their own fixture, separate from the structural contract and mutation controls. Retained during O3 to reuse the pinned-release fixture for artifact integrity, permission consumption and normalization cases without duplicating its setup; the planned contract split remains open. Owner and destination: refactor stream A, SPEC-2 step 5 (inventory enforcement suites). Trigger: step-5 suite rewrite; integration retains this finite obligation if that work is not scheduled. Clear after fixtures are split by contract without duplication or lost coverage and cross-family review passes. |
| `tools/Test-PreReview.ps1` | Help and exact-export artifact prerequisite 1-122; native/Git execution 134-188; registry selection 190-295; documentation drift 297-391; ratchet reporting/completion 393-478; lane selection 480-522; citations 524-672; ownership 674-725; repeat diagnostics 727-750; check dispatch 752-774; binary freshness 776-802; self-test 812-1435; command orchestration 1444-1755. | SPLIT-PENDING: selection, citations and fixture construction are separable jobs. O3 changes only the help to require the exact public-head checkout and its same-run CI artifact; extraction remains outside this help-only correction and must preserve the existing loading and self-test entry contracts. Owner: refactor stream A, SPEC-2 step 4 (failure evidence). Destination: focused modules beside tools/ci/prereview_checks.py, called by this command, and a separate pre-review fixture entry. Trigger: the next structural change to this command, with this obligation checked at the tooling integration review even if no later change occurs. Clear only after the split preserves the hosted positive/negative contract and receives cross-family review; otherwise leave this finite code obligation open, not silently cleared. |
| `tools/ci/prereview_checks.py` | Git/source evidence 59–102; receipt checking 105–129; unit growth 132–208; registration parsing and exemptions 211–300; witnesses 303–335; registration inventory/checking 338–401; CLI dispatch 404–429. | SPLIT-PENDING: receipt/growth adapters and registration/witness analysis are distinct jobs. Retained while the imported gate's registration compatibility and mandatory checks settle; N16 is cleared: namespace-tail extraction now calls spec_file_identity.type_prefix at 226-227, deleting its copied namespace regex. Registration/witness interpretation remains here; the broader split obligation is unchanged. Owner: refactor stream A, SPEC-2 step 4 (failure evidence). Destination: registration/witness module under tools/ci, leaving receipt/growth adapters and dispatch here. Trigger: the next structural change to this helper, with the obligation checked at tooling integration even without a later change. Clear after extraction preserves all enforcement fixtures and a cross-family reviewer confirms ownership; until then keep this finite obligation explicit. |
| `tools/ci/spec_trust.py:check` | Release/candidate findings and artifact validation 179-191; measured comparison using resolved permissions 192-200. | N7 OPEN: findings and structural scans still lex each tree separately, and identity matching repeats. The nested type-name join also repeats in check-spec-ratchet.py:263 and spec_trust.py:119 (N15). O3 reuses resolved permission offsets across both scans, but does not clear this broader debt. Sharing a per-file scan result is not a few-line correction: it changes the scanner/identity handoff used by both entry points. Owner: refactor stream A, SPEC-2 step 5; destination: step-5 tools inventory and the shared scan protocol in check-spec-ratchet.py/spec_trust.py. Trigger: step-5 inventory review or the next scan-protocol change, whichever occurs first. Clear only after one parsed inventory feeds both consumers, unchanged enforcement fixtures pass, and before/after work is measured. If that stage is not scheduled, the integration review retains the explicit open obligation; no automatic product wait is involved. |
| `.github/workflows/sync-shark-ci.yml` | Workflow configuration 1-41; GitHub jobs 43-1363, including ratchet 217-275, mandatory outcome 277-300, engine build 348-401 and independent literal artifact producer 403-438. | Retained as the single job graph: each named job owns its execution and each step one action. File length reflects the explicit matrix/artifact dependencies; splitting into reusable workflows would add cross-workflow inputs and outputs without separating another policy. Mandatory policy remains in MandatoryChecks.ps1. |
| Ratchet committed baseline and manifest (O1) | spec_trust.py baseline_findings 88-105 and measured_baseline 151-176; candidate manifest policy visibility 63-85. | OPEN: committed baseline/manifest no longer determine measured debt clearance; they still provide provenance display and edit guards. No behavior change in this round. Owner: refactor stream A, SPEC-2 step 3 (verdict evidence). Trigger: step-3 design/integration review. Clear after their remaining consumers are justified or removed with matching fixtures and cross-family review; retain this finite obligation if step 3 is not scheduled. |
| Release-relative policy reporting (O2) | spec_trust.py policy_findings 63-85. | OPEN: inherited release-relative changes obscure lane-only policy changes. No behavior change in this round. Owner: refactor stream A, SPEC-2 step 3 (verdict evidence). Trigger: step-3 review-reference design. Clear after lane-specific reviewed policy changes are distinguishable with positive/negative fixtures; until then reviewers compare policy files against main. Retain the obligation if step 3 is not scheduled. |
| `tests/SyncShark.Engine.Tests/Layout/LiteralsChecks.cs` | Fence entry 19-70; message-catalog policy 72-332; enforcement fixtures 334-514; literal rules and ownership 515-719 (shared string permission 556-573; literal-kind predicate 575-576); coordinates 721-725; repository/type-to-file inventory 727-774; fence source selection 776-789. | SPLIT-PENDING: message-catalog policy and the shared type-to-file inventory are separable from literal enforcement. Required passing/failing fixtures stay with their rules. O3 extracts an existing permission branch and adds producer assertions, not a new policy job; the broader split is retained for structural work. Owner: refactor stream A, SPEC-2 step 5 (inventory integration lane). Destination: dedicated message-catalog and type-file inventory owners beside this file, with DependencyChecks, OwnershipChecks and SettingsParserChecks calling the inventory owner. Trigger: the next structural edit to LiteralsChecks, with this obligation checked at tooling integration even if no later edit occurs. Clear after both jobs are extracted, all callers reuse their owners, enforcement fixtures pass and cross-family review confirms the split. |

## src/SyncShark.Engine/Storage/StateStore.cs

B-222 disposition: existing split/reason obligations remain open under their recorded structural owners and clearing triggers. The absence-retirement fix extends the existing retirement, closing or checked-removal job, not a new workflow. No debt row is cleared by this change; transactional ordering and proof forwarding remain with their current owners.

Category-change split clearing condition (class-size and file-size rows, and the FinishRepair row that cites this condition): splitting StateStore is deferred beyond 0.3.82, whose scope is serious sync-reliability fixes only. Clearing trigger: the 0.3.83 refactor unit split (owner-approved 10 Nov), at its cleanup-only structural recheck. Owner: storage structural lane. Destination: this register section and the extracted owners under src/SyncShark.Engine/Storage/. Closure requires the completed extraction preserving transaction, locking and publication semantics, plus a fresh cross-family verdict; if the coupled storage, runner and Host interfaces are still changing then, the obligation stays visibly open at that release gate and is not treated as cleared.

<!-- spec-source: c9df9dc9d048~957d0d06a2d1~4a223e6daa71~b458f275a65e~cf13a2064ba2~8cdf -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 8 | class-size | class-size | Split by job | Connection lifetime, schema migration, scan/history and folder persistence are distinct jobs. 0.3.79 adds expected-notice fencing for retry reads and visibility (B-13), the live-refusal read (R6), the published-USN migration step (#90b) and block-wise history restore (B-21) to the same class: 3490 to 3588 lines. 0.3.80: the cut-over deletes SaveImportedPair; selection admission now answers instead of throwing (BeginSelection), WorkAttempted, RequireDiscovery and SelectionBaseline forward to ChangeTable, and InitializeSchema gains the version-12 discovery_required step: 3588 to 3595 lines. Same split; no new job. 0.3.83 (#36): the one owed-history rule RequireForOwedHistory :1570-1575 with its transaction-taking history read :818-821, FinishRepair's committed fact and secondary output :1339-1416, SQLite FULL normalization in Write :3306-3311 and Apply :3408, the unsafe flusher scheduling :450-451 and the scan-batch admission port :178-183, :757-758: 3779 to 3811 lines on #36, 3908 in the integrated file. Durable completion and queue persistence; same split, no new job. B-137: CountWork answers both sides' pending counts for the pass queue snapshot and still forwards one ChangeTable statement; queue persistence, same split. B-177: ReadItemRetryForSubject never retires on a miss (INT-B177-1); ReadReplacedItemRetry reads the path's one episode bound to another file id (one ItemEpisodesAtPath seek) before a fresh Local read, and RetireReplacedItemRetry ends exactly that token (fenced on the attempt registry for the old Local item, one exact-token write); B-195: ReadReplacedItemRetry returns every episode at the path whose token or member subjects record another file id, and RetireReplacedItemRetry retires each against the fresh read (EpisodeItemsAt and Recorded: the same seek, now decoding member subjects through StateEncoding.ReadIdentifiedItems; R7: each item is kept or skipped on its own, an item stored with no file id is skipped, so it never proves a replacement, never retires and never throws, while the identified items beside it still do); same retry persistence. SettleEpisodeWork is the one episode-end settlement shared with CompleteItemAttempt, and four lost-claim, held-row or overtaken-capture checks refuse as SelectionChanged: retry persistence, same split, no new job. B-195b: RetireReplacedItemRetry takes a null fresh id, meaning the fresh read found no file, so every recorded file id at the path is stale; an empty id still proves nothing; same fences, same exact-token write, same retry persistence, no new member. RG-1/RG-2: ReadItemEpisodeIssues reads a full Repair's open episode IDs once over the existing PairItemEpisodes statement, and RetryNoticeProtected adds IssueTable.AtPendingPath to its answer. RG-R1: RetryNoticeProtection prepares that answer once per reconciliation (one pending read); RG-R2: both pending settlements clear the note before their own closures; RG-R2a: each then closes the note's own recovery notice through IssueTable.Close. Retry persistence and issue answers, same split, no new job. 43a: current class/file sizes are 4100/4107; capture :22-61, CommitItemRename :2190-2196 and SettleItemAttempt :2200-2287 stay in retry persistence. Same jobs; the split remains open. |
| 2683 | long-member | AdvanceSubmission(SqliteTransaction,long,FolderSubmission,FolderSubmis | Record why it cannot be split | Record submission and unsettled member transitions as one atomic receipt application. |
| 2578 | long-member | ArmFolderSubmission(string,IReadOnlyList<FolderRemovalEffect>) 43 | Record why it cannot be split | Record exact-effect validation and immutable wire membership as one all-or-none transition. |
| 2100 | long-member | BeginFolderRemovalCore(string,FolderPlan,int,CancellationToken,WorkSelection?) 43 | Record why it cannot be split | Atomically creates one folder-removal operation, seeds its root/frozen/delegated counterpart members and optionally links the admitted rename continuation, then commits; counterpart discovery has already moved out. |
| 2200 | long-member | SettleItemAttempt :2200-2287 (88) | Split by job | SPLIT-PENDING. Jobs: authority and scan attach :2208-2210; transaction and generation :2211-2212; before-image advance or respelling revalidation :2213-2218; completed-pending validation and LocalRecovery fence :2219-2240; claim :2241-2242; failure continuation :2243-2260; success settlement :2261-2278; commit and publication :2279-2286. Extract the completed-pending validation as one private validator returning the fenced candidate. It predates 43a; behaviour-preserving extraction stays separate from this behaviour fix under section 7.1. Clearing trigger, owner and destination: this section's storage structural clearing condition. CompleteItemAttempt :2183-2186 now delegates and is below the review figure. 43a: the body is the one private SettleItemAttempt; CommitItemRename asserts StateChange.RespellsWithin and calls it with keepEpisode, which skips only ClearItemEpisode and SettleEpisodeWork, so the respelling keeps the wait under the same claim and fences. One settlement, no copy, no new job. 43a round 3: a selection-null respelling revalidates its whole saved before-image in the same transaction, and ReadItemRetryForSubject reads the held binding only after an exact miss, for the legacy counterpart check; same settlement and capture jobs. 43a round 7 (47e): the completed-pending membership test calls IssueSubject.HoldsRespelling (IsRespelling) on a once-built losing item instead of exact Contains; same job, no new statement. N2 remains open for the next storage structural change: the acquired-anchor closure match :2274-2277 is ordinal while IssueTable.Close :116-121 uses canonical keys. A respelled selection-less completion can therefore leave its acquired anchor for one extra observation/provider metadata read; no loop or notice impact. Reconcile that match through the path-identity owner, with a fresh review; no production change in this fix. |
| 3082 | long-member | DrainRecords(out Exception?) 108 | Split by job | Queue take and failure-isolation scheduling are separable from transactional apply with post-commit publication and closure update (local Apply) #36 normalizes SQLite FULL once in Apply, context-free; no new job. |
| 712 | long-member | EndScanAsync(string,Guid,int) 46 | Record why it cannot be split | Record whole-file/partition alternatives and failure cleanup as one scan-ending lifetime. |
| 1287 | long-member | FinishRepair(string,RepairSummary,long,Guid,LocalCatchUp,DropboxCatchU | Split by job | SPLIT-PENDING (spec-verdicts FinishRepair(...,out Exception?)): fenced completion :1349-1396 (source/checkpoint fences, owed history and retirement of covered work in one transaction) and post-commit maintenance :1400-1408 (Reclaim/checkpoint after the durable result commits) are two jobs. #36 grew it 60 to 76 lines (owed history joins the transaction, committed is set right after Commit, a later publication or maintenance failure returns as secondaryFailure) and adds no job. Clearing condition: this section's category-change clearing condition. |
| 2069 | long-member | InsertFolderMember(string,string,long,FolderRemovalItem.ProvenanceKind | Record why it cannot be split | Record provenance/scope validation and reuse/insertion as one recovery-member admission. |
| 1765 | long-member | RecordInUseFailure(string,long,long,string,IssueSubject,TextKey,WorkSe | Record why it cannot be split | Record attempt validation, issue/work binding and episode claim as one retry-responsibility installation. |
| 769 | long-member | RestoreDeletedHistory(string,Guid,Action<long>?) 57 | Record why it cannot be split | Record verified replay and continuation publication as one completed acquisition. B-21 grew it 43 to 57: one block per lock hold with a withdrawn-cursor check and a progress count between blocks; the per-block step is still owed as its own method. |
| 1217 | long-member | SavePairDecision(PairConfig,long,PairConfig,Issue?,bool,string?) 67 | Record why it cannot be split | Record definition, generations, responsibility and scan invalidation as one configuration transition. |
| 2428 | long-member | ValidateFolderChange(SqliteTransaction,long,string,FolderRemovalState, | Split by job | Pure permitted-change-shape validation differs from database evidence traversal and coverage counting. |
| 1112 | long-member | static ForgetTouchesUnreadable(SqliteConnection,string,IEnumerable<(Si | Record why it cannot be split | Record local/mapped ranges as one deduplicated forget-footprint overlap query. |
| 3626 | long-member | static InitializeSchema(SqliteConnection,bool) 43 | Record why it cannot be split | One schema-open admission lifetime: version/reference checks, read-only admission and the choice of maintenance versus migration, with the commands and reference connection sharing that lifetime; the upgrade steps are delegated to MigrateSchema :3670. The earlier 126-line inline-upgrade split no longer applies; the class/file split stays open. |
| 8 | file-size | file-size | Split by job | Connection lifetime, migration, scan/history and folder persistence are distinct jobs; 3497 to 3595 lines in 0.3.79, 3602 in 0.3.80 (see class-size). 3811 lines after #36, 3908 integrated (see class-size). 43a: current class/file sizes are 4100/4107; capture :22-61, CommitItemRename :2190-2196 and SettleItemAttempt :2200-2287 stay in retry persistence. Same jobs; the split remains open. |

## src/SyncShark.Engine/Run/EngineHost.cs

<!-- spec-source: dc454d5c6860~86edc3111da2~51a685fd23c6~61da213de00f~e12e8c9104a1~ae55 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size (2176) | Split by job | SPLIT-PENDING. Jobs in the merged file (2187 lines, one type plus nested records): (1) source supervision: background failure, journal start/observe/fail/stop :59-173; (2) composition and settings :175-342, including the attempt-registry fence wiring for the store, openStore(host.IsRegisteredItemAttempt) :319; (3) registration, generation admission, borrowing, drain, wake and pause transitions :344-615; (4) configuration transitions and volume membership: ChangeGenerationAsync, Recompose, Install, journal members, freeze, feed :616-941; (5) source bases, catch-up and post-Repair checkpoint :942-1046; (6) pair admission and IR-1 item waits: Admission, ItemRetryHolds, HeldForRetry, ItemWaitNarrows, ReleaseItemWait, RecordItemKeys, LiveItemWait :1047-1125; (7) prerequisite Repair attempts: RunPrerequisiteAsync, completion, failure recording and wait report :1126-1365; (8) listing attempts: BeginListing and RetryNotice :1366-1423; (9) item attempts: port, reserve, the one active-attempt hold predicate HoldsItem :1486-1488 (shared by ReserveItem :1475 and the empty-id fence in IsRegisteredItemAttempt :1492-1494 that the store uses before retiring a replaced episode, B-177), failure/temporary/completion reporting, equality helpers, inventory failure :1424-1714; (10) scheduling, pause/resume, platform wake, startup and preferences :1716-1813; (11) progress, quiet status, retry presentation and status subscription :1815-2024; (12) shutdown and disposal :2026-2076; nested state records :2082-2186. Extraction order for 0.3.83, least coupled first: (a) status publisher, jobs 11 and the progress half of 10; (b) listing attempts, job 8; (c) item attempts with item waits, jobs 9 and 6 (one ItemAttempt/ItemWait owner, under the host gate through a narrow port); (d) prerequisite Repair attempts, job 7, which then calls (c) for item keys; (e) source supervision with source bases and catch-up, jobs 1 and 5; (f) configuration transitions and volume membership, job 4. Registration/generation admission (3), composition (2), scheduling/startup (10) and shutdown (12) stay in EngineHost as the composition root. A review ruled SPLIT-NOW; SPLIT-PENDING rests on the coordinator's recorded deferral. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: EngineHost structural lane; destination: this register row and the extracted owners under src/SyncShark.Engine/Run/. Closure requires the extraction and a fresh cross-family verdict; if the composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. 43a: CommitItemRename beside CompleteItemAttempt (attempt-registry job) refuses a completed, reported or other-token attempt and commits a respelling without setting Completed; same job, no new state. 43a continuing-report integration: ReportInventoryFailure :1707 reserves the captured subject, same item-attempt job. N3 fixture-realism note remains open for the Host structural lane: EngineHostChecks.LocalBranchCaseRename.cs :41,56-59 uses a file for the inventory variant, whereas LocalListing.cs:74 reports folders. The path does not branch on IsFolder; on the next fixture update, use a folder-shaped inventory entry and recheck the same assertions. No test change in this fix. |
| 1 | file-size | file-size (2187) | Split by job | SPLIT-PENDING; same jobs and extraction order as the class row (the file holds EngineHost alone, with its nested records :2082-2186). Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: EngineHost structural lane; destination: this register row and the extracted owners under src/SyncShark.Engine/Run/. Closure requires the extraction and a fresh cross-family verdict; if the composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |
| 109 | long-member | JournalFailed(Volume,Exception?) 50 | Record why it cannot be split | All members publish debt and attribute it before accounting under the same membership/lifecycle boundary; diagnostics follow outside that boundary. |
| 296 | long-member | static Compose(string,Action<string>,PilotStateAuthority,EngineSettings,...,EngineExternals,...) 46 | Record why it cannot be split | Record dependency wiring and failure cleanup as one object-graph ownership transfer. |
| 208 | long-member | static Compose(string,Action<string>,PilotStateAuthority,Mode,EngineStartupAdmission,...) 65 | Record why it cannot be split | The production composition creates log, HTTP client, monitor and host in one try whose single catch dismantles each created part in order (reason :205). |
| 386 | long-member | ExecuteInGenerationAsync<1>(string,RegistrationKind,bool,Func<object, ...>) 51 | Record why it cannot be split | Record admission, execution and retirement around one registration. |
| 616 | long-member | ChangeGenerationAsync<1>(PairConfig,Func<object, IStateStore, ...>) 78 | Record why it cannot be split | Record drain, caller commit and guaranteed reopen as one configuration transition. |
| 779 | long-member | InstallCurrentAsync(IReadOnlyList<string>) 44 | Record why it cannot be split | Record resource replacement, membership and reopening as one captured-generation installation. |
| 1126 | long-member | RunPrerequisiteAsync(string,CancellationToken,RunLog?,Issue?,schedule) 118 | Record why it cannot be split | Admission, caller-stop classification, log disposal and exact completion share one attempt lifetime; selected schedule freshness is checked before registration. Moves whole with job 7 at extraction step (d). |
| 1266 | long-member | CompletePrerequisiteAsync(Prerequisite,RepairSummary?,Exception?,RunLog?) 67 | Record why it cannot be split | The memoized completion owns one exact registration through accounting, release and deferred callbacks; the merged item-wait tuple (:1305-1306) keeps the item keys and the WaitItemRetry report on that same path. |
| 1278 | long-member | CompletePrerequisiteAsync::CompleteAsync() 54 | Record why it cannot be split | Accounting and exact removal stay indivisible to source loss; finally delivers the same attempt's captured opportunities. |
| 1366 | long-member | BeginListing(object,AttemptOwner) 52 | Record why it cannot be split | Record creation, completion and disposal as one listing identity/retry episode. |
| 1538 | long-member | ReportTemporaryFailure(object,Guid,long,string,IssueSubject,TextKey,...) 68 | Record why it cannot be split | One temporary-failure acceptance under the host gate: exact attempt check, retained endpoint (Successor), item wait keys and the stored retry row are one decision. Moves whole with job 9 at step (c). |
| 1831 | long-member | PublishQuietStatus() 52 | Record why it cannot be split | One captured eligibility answer accompanies log delivery and synchronous status publication; log callbacks execute outside the host gate, and work they start runs without that flow (WithoutFlow). |

## src/SyncShark.Engine/Executor/FolderDeleteRunner.cs

B-222 disposition: existing split/reason obligations remain open under their recorded structural owners and clearing triggers. The absence-retirement fix extends the existing retirement, closing or checked-removal job, not a new workflow. No debt row is cleared by this change; transactional ordering and proof forwarding remain with their current owners.

<!-- spec-source: 64d615edcd6c~f7741b91691a~bb4671b7ee2f~f47c1547e76f~92679e547072~99a1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (1097) | Split by job | Current jobs: operation orchestration :34-218; post-window observation/settlement :223-384; recovery stages/publication/settlement/reporting :386-632; aggregate conclusion :634-710; effect admission/submission/join :713-1008; nested progress state :1010-1102. B-164 round 3 (REVIEW-B164-2) moved the Local effect and receipt-check job to LocalFolderEffects; frozen-member classification (IsFrozen :841), the late record and notice (LateAsync :364) and the deleted event stay here. SPLIT-PENDING: recovery publication, Dropbox job observation and overall progression remain separate jobs. |
| 223 | long-member | CloseWindowAsync (65) | Record why it cannot be split | Current :223-287 checks the post-window feed, selects readable verified or absent saved-only members for settlement, reports completed children and retires its cursor. The ordering comments do not themselves prove that these steps cannot be separated; existing reason obligation remains open. |
| 634 | long-member | ConcludeAsync (45) | Record why it cannot be split | Current :634-678 maps mutually exclusive aggregate progress states to the next operation transition; uses the same Progress answer. Independent disposition is unchanged by this description refresh. |
| 968 | long-member | ObserveAsync (41) | Record why it cannot be split | Current :968-1008 translates one submitted-job observation into durable member accounting and retry/failure disposition. |
| 485 | long-member | PublishAsync (55) | Record why it cannot be split | Current :485-539 owns one recovery-stage publication and its no-effect, occupied-target and uncertain-effect outcomes. |
| 752 | long-member | SendAndJoinAsync (44) | Split by job | SPLIT-PENDING (REVIEW-B164-2): candidate preparation and readability filtering :752-774 and committed submission and join :775-795 are separate jobs; extract preparation while preserving admission and committed ordinal ordering. Not required in B-164 round 3. |
| 876 | long-member | SubmitAsync (43) | Record why it cannot be split | Current :876-918 translates accepted, terminal, no-effect and unknown submission outcomes for the same attempt. |
| 1 | file-size | file-size (1103) | Split by job | Same current jobs and retained debt as the class row; B-164 round 3 moved the Local effect and receipt job to LocalFolderEffects (REVIEW-B164-2). |

## src/SyncShark.Engine/Storage/ChangeTable.cs

Category-change split clearing condition (class-size and file-size rows): splitting ChangeTable is deferred beyond 0.3.82, whose scope is serious sync-reliability fixes only. Clearing trigger: the 0.3.83 refactor unit split (owner-approved 10 Nov), at its cleanup-only structural recheck. Owner: storage structural lane. Destination: this register section and the extracted owners under src/SyncShark.Engine/Storage/. Closure requires the completed extraction preserving transaction, locking and publication semantics, plus a fresh cross-family verdict; if the coupled storage, runner and Host interfaces are still changing then, the obligation stays visibly open at that release gate and is not treated as cleared.

<!-- spec-source: 64952f500578~785156785511~6ff5fb35a5e3~c5ae30d5140e~7328e0cfef89~a6a2 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | class-size | class-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody lifecycle. 0.3.79 adds the live-refusal answer (R6), notice content merging (S2) and own-write USN evidence (#90b): 1473 to 1543 lines. 0.3.80 adds the per-pass attempted answer (WorkAttempted/Attempted :503-508) and the durable discovery-required work flag (RequireDiscovery :510-519, NeedsOwnSelection :1283-1284): 1543 to 1565 lines. Both are work-selection custody; same split. 0.3.82 (B-101): one shared UnownedIdentity :576-581 (the probe and admission both use it) and the path-selection identity refusal :1118-1124; #36 plumbing adds an optional transaction to Obligated :66-67, :1558-1560. Work-selection custody; same split. B-137: CountWork (:248-262) reads the pair's two source rows and one shared count statement for the pass queue snapshot, its two count columns named as ordinals; work-selection custody, same split (1822 lines). B-176: the exact later-evidence probe moves out of ValidateScopeEvidence into RefuseLaterExact :1210, now shared with RefuseLaterEvidence :1246, which SelectionAdmission runs for its footprint before coverage capture; work-selection freshness, same split. B-198: InUseBlocks reads member subjects through StateEncoding.ReadIdentifiedItems (an item with no file id is skipped; a member left with no item never blocks), and ProjectIssueWork through StateEncoding.ReadSubjectItems (only a null file id admitted; the projection reads side and path); work-selection custody, same split, no new job. B-210 (REVIEW-CODE-B210-3 §3-4, exact 8dbfd966e; round-1/2 verdicts confirmed): path-only coalescing (SelectWork :265-301, CoalescePathWork :305-367 and TransferBound :369-397) and shared IntegrityRefusal :1036-1037 / RollbackMutation :1039-1045 remain work-selection custody, same split, no new job category; class 2150 to 2267 lines. B1 separates the transfer protection bound; class debt is retained, not cleared, under the clearing condition above. |
| 102-151 | long-member | AcquireJournalPage(string,JournalPosition?,JournalPosition,IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision, bool LostInterval)>,IEnumerable<(string PairId, ChangeNotice Notice)>) (50) | Recorded reason retained | The existing reason at :96 binds member/position fences, notices and checkpoint advancement as one atomic acquisition. B-210 changes no acquisition responsibility; historical obligation retained, not a new member verdict. |
| 933-978 | long-member | AdvanceBefore(SqliteTransaction,string,WorkSelection,StateChange) (46) | Recorded reason retained | The existing reason at :932 binds one selected work item's replacement baseline derivation and propagation. B-210 changes no baseline-advance responsibility; historical obligation retained, not a new member verdict. |
| 1072-1088; 1090-1164 | long-member | AttachIssueWork: registration entry (17); transaction-taking custody implementation (75) | Retain custody obligation | Retain/move/create alternatives remain one atomic responsibility handoff. The former entry now delegates to the transaction-taking implementation; retain that implementation's existing SPLIT-PENDING verdict and the open custody obligation rather than clearing it because the entry is short. B-210 changes no handoff responsibility. |
| 1541-1593 | long-member | EnsureIssueWork(SqliteTransaction,string,long,long,long,string,IssueSubject,WorkSelection?,ItemRetryDispatch,PendingEffect?) (53) | Recorded reason retained | The existing reason at :1537 binds selected/source-less paths establishing the same exact issue-work custody. B-210 changes no ensure responsibility; historical obligation retained, not a new member verdict. |
| 1267-1342 | long-member | ReadLocalDirty(string,long,long,string,string?,string?,string?) (76) | Recorded reason retained | The existing reason at :1262 binds identity/current/recorded-path probes as one consistent dirty-evidence answer. #90b grew it 61 to 76 with the own-write USN fold (local Count) and the OwnWrite check. B-210 changes no dirty-answer responsibility; historical obligation retained, not a new member verdict. |
| 1382-1406 | long-member | ValidateLiveCoverage(WorkSelection,LiveCoverage,IReadOnlyList<LiveEffectScope>,Func<string,bool,bool>) (25) | Retain freshness obligation | Footprint/later-evidence checks remain one admitted-selection freshness proof, now delegated to ValidateSelectionBinding and ValidateScopeEvidence. Retain the historical obligation rather than treating a size below the prompt as a structural verdict; B-210 changes no coverage-validation responsibility. |
| 11 | file-size | file-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody persistence; 1483 to 1553 lines in 0.3.79, 1575 in 0.3.80 (see class-size); 0.3.82 B-101 and #36 plumbing +9 lines (see class-size). B-137: CountWork (:248-262) reads the pair's two source rows and one shared count statement for the pass queue snapshot, its two count columns named as ordinals; work-selection custody, same split (1832 lines). B-210 (REVIEW-CODE-B210-3 §3-4, exact 8dbfd966e; round-1/2 verdicts confirmed): path-only coalescing (SelectWork :265-301, CoalescePathWork :305-367 and TransferBound :369-397) and shared IntegrityRefusal :1036-1037 / RollbackMutation :1039-1045 remain work-selection custody; file 2160 to 2277 lines. B1 separates the transfer protection bound; file debt is retained, not cleared, under the clearing condition above. |

## src/SyncShark.Engine/Executor/ItemSync.cs

<!-- spec-source: d4e4b83411af~42dbf79d8e08~c7164d1b28ef~a12feebc1191~6f92879df6fa~8e3e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 81 | long-member | RunRowAttemptAsync :81-130 (50) | Split by job | Current registered-attempt envelope captures its shared dirty answer, prepares/settles fast outcomes and routes admitted execution through ItemExecution. Frozen 49 lines, current 50; the structural review keeps its split pending. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ItemSync structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ItemSync.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Core/SyncCliRegistry.cs

<!-- spec-source: e36edc584de5~6554710b6439~e0263570f2cf~2b9f5103aca7~69b41cdfa9fc~78ff -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | class-size | class-size | Split by job | Command metadata, argument parsing, action dispatch and result presentation are distinct jobs. IsStateFree :741-746 (0.3.79) reads a row's Mode and belongs to the metadata job. 0.3.80.1 (B-58): the setup rows move into SetupRows :50-54, spread into Rows :58, and IsSetupCommand :751-757 routes setup by those exact rows; both are metadata, so the command identity is still spelled once. 2389 to 2407. |
| 1263 | long-member | static DispatchAsync(ISyncCliRuntime,SyncCliInvocation,TextWriter,Text | Split by job | Progress presentation/sanitization differs from selecting and invoking the command action. |
| 766 | long-member | static Parse(string[]) 411 -> 456 | Split by job | Generic tokenization differs from command-specific combination/value validation. |
| 1858 | long-member | static TryRender(object?,out string) 477 -> 499 | Split by job | Reclassified to split (R2-11): each result shape's formatting is an independent job, and the capability case already delegates to RenderCapabilities. |
| 15 | file-size | file-size | Split by job | Registry metadata, parser, dispatcher and presenter implementations are distinct jobs. 0.3.80.1: metadata only (SetupRows, IsSetupCommand); 2403 to 2421. |

## src/SyncShark.Engine/Dropbox/DropboxEntries.cs

<!-- spec-source: 01ba2e7d16d8~406b551d4d69~dc83eef83498~fbdadf95caff~89c5b3a9f6b9~eca5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Lexical validation, field projection and Dropbox metadata interpretation are distinct jobs. |
| 113 | long-member | Read(ReadOnlySpan<byte>,bool,ReadOnlySpan<string>,bool,CancellationTok | Record why it cannot be split | Record one token pump sharing incremental consumption, readiness and final-block state. |
| 238 | long-member | StringByte(byte,ReadOnlySpan<string>) 92 | Record why it cannot be split | Record escape, surrogate and UTF-8 branches as transitions of one incremental string decoder. |
| 156 | long-member | StructuralByte(byte,ReadOnlySpan<string>,bool) 66 | Record why it cannot be split | Record punctuation, container closure and lexeme start as one grammar-frame transition. |
| 7 | file-size | file-size | Split by job | Incremental JSON lexing, selected-field projection and metadata interpretation are distinct jobs. |

## src/SyncShark.Engine/Executor/ScanPass.cs

B-222 disposition: existing split/reason obligations remain open under their recorded structural owners and clearing triggers. The absence-retirement fix extends the existing retirement, closing or checked-removal job, not a new workflow. No debt row is cleared by this change; transactional ordering and proof forwarding remain with their current owners.

<!-- spec-source: 763b18769865~bebc00f8e176~b8fc75ea1a12~fc9ba382a769~45b0c4b01ddc~48b4 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (527) | Split by job | Current capacity/orchestration :22-51; staged scan implementation :53-379; unreadable enrollment :388-408; folder continuation/plans :412-459; issue reconciliation :461-516; progress :518-527. Cross-pair stale-stage cleanup :73-91 remains inside RunRowsAsync. RowCapacity/TotalCapacity :22-26 are shared with Syncer. B9 migrates calls to ItemSync owners and ItemFailures, without closing this historical split. RG-1: ReadItemRetryPaths covers every open episode on a full Repair, and RG-2 moves issue reconciliation's inline pending-path copy to the issue owner, prepared once per reconciliation through RetryNoticeProtection (RG-R1); same jobs. |
| 461 | long-member | ReconcileIssuesAsync :461-516 (56) | Split by job | Evidence setup :465-483; obsolete-issue retirement :485-495; unknown-move gate :496-506 (the by-ID read is ItemRenames.ObserveUnknownMoveAsync; the E6(3) exit test stays here); reusable conflict-hash proof collection :507-513. The unknown-move gate adds provider I/O to retirement and is separable. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ScanPass structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ScanPass.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |
| 53 | long-member | RunRowsAsync :53-379 (327) | Split by job | Current stages: skips/cleanup/exclusions :57-128; rename scheduling/barriers :129-261; row contexts/results/dispatch :262-364; folder plans/first-run finalization :365-378. These embedded stage implementations remain the historical split obligation. |
| 1 | file-size | file-size (527) | Split by job | Same current jobs as the class row, including nested rename-execution state :381-386. Receipt refresh records current caller locations only. |
| 163 | long-member | RunRowsAsync::RunRename :163-205 (43) | Record why it cannot be split | One rename group's task lifetime signals completion exactly once; the dependency rerun continues the same entry. Reason recorded above RunRename. |

## src/SyncShark.Engine/Run/RunLog.cs

<!-- spec-source: e5622f3e4554~a8da79daf4fc~9195c880c144~5d67b61d38c9~1f03d400111b~6b0a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size (771) | Split by job | Canonical codec differs from mutable accounting, flushing and subscriber delivery. Grew to 685 with S2 active-branch tracking, to 713 with the 0.3.83 first-failure exception (field :58, accessor :204/:207, remaining sites :627/:636/:655, disk-full rendering :573), to 761 with row side/work-id stage records and the one-per-pass held report, to 767 with the B-145 whole-subject branch identity, to 769 with the component-agreeing partial-branch fallback, to 770 when that fallback was limited to facts that omit part of their identity, and to 771 with the named failure-message separator the literals rule requires. The split obligation is unchanged; this row records the current size. Split plan (extraction not complete): the logging owner first extracts the pure canonical codec/grammar, then the progress reduction/snapshot state, preserving sink lifetime, ordered delivery, canonical bytes, failure capture and the public callers. Destination: the Run module and this register section. Trigger: the next logging correction or structural cleanup gate. Closure: current-source independent review plus the applicable codec/progress/delivery tests. |
| 222 | long-member | Event(RunEvent) :222-331 (110) | Split by job | Progress reduction, canonical accounting and ordered human delivery are distinct jobs. Grew 70 to 87 with S2 branch tracking, to 102 with the row side/work-id stage records and the held-wait wording, to 107 with the B-145 whole-subject branch identity, to 109 with the component-agreeing reducer, which removes only the exact matching subject, and to 110 when a whole-subject miss stopped walking the active list. Same split plan, destination, trigger and closure as the class row. |
| 86 | long-member | ReportProgress(string?,bool) 48 | Record why it cannot be split | Record one quiet observation’s matching diagnostic and sequenced human snapshots. |
| 13 | file-size | file-size (783) | Split by job | Canonical codec differs from mutable accounting, flush lifetime and delivery; 783 lines under the same split obligation as the class row, whose row records the split plan, destination, trigger and closure. |

## src/SyncShark.Core/NtfsUsnJournal.cs

<!-- spec-source: d18a0485ef66~6cd95eb74bb3~88e3c3cca824~6305c88d7489~fc22fb02b33e~b048 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | class-size | class-size | Split by job | NtfsUsnJournal.File.cs:11-154 per-file USN evidence/gap-handle opening is a job distinct from volume journal reading; partials do not answer 1.3 |
| 308 | long-member | static TryDeviceIoControlPage(SafeFileHandle,uint,byte[],byte[],out in | Reason recorded | The comment at :306 binds pinned buffers, completion, cancellation drain and release to one overlapped request; the event low bit keeps manual completion out of a FileStream completion port. |
| 15 | file-size | file-size | Split by job | File holds journal-state query, page read with record walk/parse, and native overlapped I/O plus P/Invoke; header :10-14 descriptive. The record-size correction shares pure layout facts only; separating native request lifetimes is outside this correction and remains open. |

## src/SyncShark.Core/SyncCliOperations.cs

<!-- spec-source: b85ef530fb4a~246eaba2d518~785b805f3861~1bc51b3433c5~c9c4577afb33~f1cb -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 208 | class-size | class-size | Split by job | Account administration, pair actions, settings transactions, updates, sharing and storage operations are distinct jobs. |
| 1542 | long-member | SaveSettingsAsync(string,CancellationToken) 167 -> 223 | Split by job | Separate document decoding/patch validation, data migration and coordinated application/rollback. |
| 208 | file-size | file-size | Split by job | Broad operational capabilities plus endpoint-authoring contracts/reader implementation. |

## src/SyncShark.Core/SyncDataLocation.Recovery.cs

<!-- spec-source: eeae3a9c11e4~e5ebdd83d63d~3a495b2b1c0f~99c47dde0996~a9bf8939a275~0376 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size | Split by job | SyncDataLocation: product-root pointer/validation vs data move and move-recovery journal (recovery partial C06); partials do not answer 1.3 |
| 271 | long-member | static RemoveManifestCopy(string,MoveJournal,bool,bool,string) 61 | Record why it cannot be split | Record one manifest-constrained removal proving preserved counterparts before exact deletion. |
| 6 | file-size | file-size | Record why it cannot be split | Record manifest preimages, copy selection and evidence consumption as one recovery protocol. |
| 171 | long-member | static RecoverMove(PilotStateAuthority) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 53. |

## src/SyncShark.Core/V2/Local/DateRepair.cs

<!-- spec-source: 1e95ee90e1e9~6111248ecd92~883dc0d5eeaa~65abe04d81ef~4571ff194701~9450 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size | Split by job | Guarded date effects, audit replay/encoding and CSV/resume presentation are distinct jobs. |
| 42 | long-member | static RunAsync(IStateStore,PairDef,Func<string, CancellationToken, Ta | Split by job | 267-line RunAsync mixes report/CSV writer setup, remote metadata fetch, per-item guarded date write and CLI resume-command construction; no reason recorded |
| 42 | file-size | file-size | Split by job | Partial-class file whose RunAsync does several jobs (row 56); rule 3; no reason recorded |

## src/SyncShark.Engine/Dropbox/DropboxListing.cs

<!-- spec-source: d2eaa6514772~9dcb456002d6~4d3deadc9872~90ea72bb7b5a~9d227efc105c~6a34 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (424) | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. |
| 27 | long-member | ReadAll(string,ListOptions,ListingReadContext,CancellationToken) :27-141 (115) | Split by job | Retry-owned page acquisition/cleanup differs from traversal spelling, filtering and progress. |
| 7 | file-size | file-size (424) | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. ReadByIdAsync :392-423 (32) is under the figure. |

## src/SyncShark.Engine/Dropbox/DropboxWriter.cs

<!-- spec-source: e6f929f25beb~7729614b59b7~e3099b0548c4~59a0cd5fbb3d~b157f91536c1~8baa -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | class-size | class-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 27 | long-member | StageAsync(string,Stream,Entry,CancellationToken) 46 | Record why it cannot be split | Record one verified stage sharing hash, offset and terminal session result across upload alternatives. B-70 threads one request attempt through it for the failure disposition (42 to 46). |
| 8 | file-size | file-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 256 | long-member | SettleCohort(List<PendingPublish>,IReadOnlyList<(Entry?Entry, Exception?Error, bool KnownNoEffect, bool WriteThrottled)>,RequestAttempt) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 68. |

## src/SyncShark.Engine/Run/EngineOperations.cs

<!-- spec-source: 2893d2f165ac~7bc3ab87ea53~b311022a9d40~b425cff97a2d~a9544bdb91e8~165f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size | Split by job | Action facade also implements configuration mutation, recovery workflows and historical logs. B-13 adds the reviewed-notice token check and expected-notice routing: 582 to 593 lines. 0.3.80: the cut-over deletes ImportPairAsync; B-74 adds the dependency-cycle Retry group RetryCycleGroupAsync :356-372 and the single-issue Retry-runs-Repair route in ResolveAsync (both resolution/recovery implementation, the owed split); 1A moves startup admission into WithHostAsync and R3 routes settings through CurrentSettings :51. 593 to 617 lines. 0.3.83 (#36): the Repair scope inside the execute lambda :201-206, +2 lines; no new job. |
| 239 | long-member | ResolveAsync(string,long?,ResolutionChoice,string?,FirstRunPolicy?,boo | Split by job | Configuration/root choices, bulk retry aggregation and individual recovery dispatch are distinct implementations. B-13 adds the reviewed parameter and its check (113 to 116). B-74 adds the cycle-group and single-issue Repair routes (116 to 127, :228-354). B-9 P2-4 adds the same-path open-Failure test to the stale-admission summary (127 to 129, :228-356). |
| 12 | file-size | file-size | Split by job | Action facade embeds configuration, resolution/recovery and historical-log implementations. This correction removes the stale direct-operation cache without relocating action authority; the larger facade split remains open rather than being coupled to the settings-lifetime fix. 604 to 628 lines in 0.3.80 (see class-size). #36 +2 lines (see class-size). |

## src/SyncShark.Engine/Executor/ActionExecutor.cs

<!-- spec-source: d90e05290ee3~68e1f3de0417~9f5fe1fa337a~150018fc68f1~9acb6ab32ba4~fb4e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 288 | long-member | CopyAsync :288-397 (110) | Split by job | Current selected-copy path sequences retained-info settlement, replacement removal, parent ensure, destination write and completion settlement; transfer/removal lifetimes stay in their effect owners. Existing reason obligation is not newly adjudicated by this receipt. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |
| 1 | file-size | file-size (453) | Split by job | Removal effects :16-77; settlement :79-92; execute/attempt :98-177; apply :179-292; copy :294-403; readability refusals :407-450; target :452. 0.3.82 B-140A carries the admitted new-Local-item fact through execute, apply and copy (439 to 448). File holds one type with these separate jobs. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. 43a: execute/attempt registers the captured token subject (one argument); same job. |
| 173 | long-member | ApplyAsync :173-285 (113) | Split by job | Per-kind application :178-276 and shared decorate/settle/emit tail :277-284 remain. Construction/decoration calls ActionChanges; copy/replace dispatch calls CopyAsync :288-397. The earlier R2-13 split obligation remains pending independent reconciliation. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |
| 6 | class-size | class-size (448) | Split by job | Removal effects :16-77; settlement :79-92; execute/attempt :98-177; apply :179-292; copy :294-403; readability refusals :407-450; target :452. File holds one type with these separate jobs. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |

## src/SyncShark.Engine/Executor/RenameRunner.cs

<!-- spec-source: b769fca19f2d~e92f485d78e4~e48ea278edc5~196914ee5e16~ed6953b7e532~9e56 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 127 | long-member | RunCurrentAsync :127-220 (94) | Split by job | Current attempt: callback/error boundary :133-139; readable/listed/fresh observation and footprint checks :143-167; dirty/hash evidence and Planner choice :168-199; unseen-Local split check (saved Local identity only) and application :200-204; occupied-destination observation and clash settlement now in OccupiedAsync :223-249, called from the clash catch :206-210 so its awaits stay inside that catch; freshness/failure mapping :211-219. Further job extraction is pending an independent verdict. |
| 47 | long-member | RunUnrecordedAsync :47-117 (71) | Record why it cannot be split | Current preparation loop retains binding discovery, footprint acquisition and the one-reacquisition stale disposition; the reacquired flag is at :52 and the shared stale policy is used at the widening exits. |
| 1 | file-size | file-size (625) | Split by job | Current clash/entry routing :12-44; binding/admission preparation :47-129 (ReadRetry :107-129); current observation/decision execution :132-225; occupied target :227-284; readability/adoption :286-323; observation/identity projection :325-374; application/adopt/follow :376-480; destination ownership :482-499; settlement :501-526; failure mapping :528-555; subject/closure/footprint construction :557-624. B9 only ports the three failure-owner calls to ItemFailures; this historical file split remains open. 43a: preparation (ReadRetry) also names the saved Dropbox counterpart and the case-rename leg at its saved spelling and captures through ActionExecutor.CaptureAttempt; settlement (SaveAsync) chooses the respelling route after the Move and before the store, building the stripped change only for a held wait. Same jobs. |
| 334 | long-member | ApplyAsync :334-453 (120) | Verify existing reason (R2) | Current results-table application and settlement use the same rename decision, saved binding and observations; the existing constraint comment immediately precedes the method at :332-333. Reason verification and semantic verdict remain independent work. |

## src/SyncShark.Engine/Run/PairSession.cs

<!-- spec-source: f343e15568cc~f835bb356199~a3aa40bba675~6143506bc7a2~282418bcd95b~e5fa -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | long-member | PairSession(IStateStore,PairConfig,EngineSettings,PathScope,ISide,ISid | Record why it cannot be split | Record one session’s dependency graph and invariant wiring. |
| 87 | long-member | static ComposeGeneration(IStateStore,PairConfig,IReadOnlyList<PairConf | Record why it cannot be split | Record account/scope/source resources and transport cleanup as one generation composition. |
| 137 | long-member | static Open(IStateStore,PairConfig,EngineSettings,HttpClient,bool,RunL | Split by job | Open embeds a transient-failure retry/backoff executor and a disk-space reservation with generation hold, behaviour beyond composition; doc comment :136 descriptive |

## src/SyncShark.Engine/Storage/IssueTable.cs

B-222 disposition: existing split/reason obligations remain open under their recorded structural owners and clearing triggers. The absence-retirement fix extends the existing retirement, closing or checked-removal job, not a new workflow. No debt row is cleared by this change; transactional ordering and proof forwarding remain with their current owners.

<!-- spec-source: 760e57da33e5~5e1f199b55b4~a44d1e7494d7~93de7ffbb64a~5c04dcb56b9c~3008 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | IssueTable is the file’s only type, so it has the file-size jobs; the earlier class FALSE-POSITIVE contradicted this register and is not carried forward (851 to 859 lines). 43a: AcknowledgeFirstRun finds the Failure by key and matches the item through IssueSubject.IsRespelling, so a wait raised under the old spelling is acknowledged after a case-only rename; round 3 reads every Failure at the key and updates each matching row by its own exact path, as Close already does; raise/close lifecycle, same job. Current class/file sizes are 966/972; AcknowledgeFirstRun :133-153 stays in raise/close lifecycle. Optional cost note: replacing its Where with foreach/if saves three short-lived objects per call; deferred to the storage structural lane at its next IssueTable change, retaining the same all-rows-at-key acknowledgement and exact-path updates. No production rewrite in this fix. |
| 569 | long-member | static MergeSkip(SqliteConnection,SqliteTransaction,string,long,string | Record why it cannot be split | Record both side members and their single path-level issue as one reconciliation. |
| 16 | long-member | static Raise(SqliteConnection,SqliteTransaction,string,IssueKind,TextK | Record why it cannot be split | Record continuation/protection/refresh/replacement as one precedence-ordered raise transition. |
| 7 | file-size | file-size | Split by job | Raise/close lifecycle, paged listing snapshots, skip-member reconciliation, unreadable queries, upgrade backfill, retry-episode lifecycle and invalid-job projection are distinct jobs; header :6 is a grouping. B-13 adds exact-notice SetIgnored to the raise/close lifecycle (857 to 865). 0.3.80.1 (B-93): Close :88-101 closes by canonical path key and the closure-key read :441 returns canonical keys, both in the raise/close lifecycle (865 to 869). RG-2 (0.3.82): AtPendingPath, the one pending-path rule the scan's protection and DeleteIssue share, joins the raise/close lifecycle; RG-R1 moves its one read and decode into PendingPath, so a reconciliation decodes the note once. RG-R2a: Close takes an optional issue filter and reports whether it closed, and IsRecoveryNotice is the one recovery-cause rule DeleteIssue and settlement share; same lifecycle. |

## src/SyncShark.Engine/Storage/StoredFolderPlan.cs

<!-- spec-source: cea56d9ddc6e~1e0f2b6897ac~74f366ff25e5~c033dd3c6b41~15ec5486bab7~14ff -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 62 | long-member | CaptureAsync(IAsyncEnumerable<Listed>,CancellationToken) 47 | Record why it cannot be split | Record enumeration, producer disposal, commit and publication as one single-use capture. |
| 141 | long-member | Children(CancellationToken) 42 | Record why it cannot be split | Record enumeration and mandatory acknowledgement as one classification cursor lifetime. |
| 6 | file-size | file-size | Record why it cannot be split | Record capture, classification, freeze and disposal as one single-use scratch-plan state machine. |

## src/SyncShark.Engine/LogView.cs

<!-- spec-source: d9aa0d1de5e8~674defbbe648~bba82b131027~04cc5e890629~d1bfb6a31d5e~6671 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | file-type-name | file-type-name | One type per file / name matches type | Top-level `LogRecord` needs its own matching file. |
| 14 | file-type-name | file-type-name | One type per file / name matches type | Top-level `LogViewStatus` needs its own matching file. |
| 6 | top-level-types | top-level-types | One type per file / name matches type | Three genuine top-level view/record/status types. |

## src/SyncShark.Core/SyncCli.cs

<!-- spec-source: fa026f027599~d80f63292595~48600ed964a4~7a7825801ce8~dd09c858bd84~5c1d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 207 | class-size | class-size | Split by job | Command execution, human/help rendering and IPC response-budget fitting are distinct jobs. |
| 293 | long-member | static ExecuteAsync(string[],TextWriter,TextWriter,Func<ISyncCliRuntim | Split by job | SyncCli.cs:312-333 state-free help/version/capabilities answers are a separate job from the runtime invocation/deadline/disposal lifetime :336-442. F2 resolved at 584016769: setup now delegates in one line to SetupCli :356-357, routed by SyncCliRegistry.IsSetupCommand (0.3.80.1, B-58), and the state-free test asks SyncCliRegistry.IsStateFree :455; 150 lines, within the reviewed size. |
| 1 | file-size | file-size | Split by job | 40 top-level types in one file (section 1.3, one type per file). F2 resolved: 909 lines, below the 916 baseline. |

## src/SyncShark.Core/SyncLocalFileAuthority.cs

<!-- spec-source: a1c8e7253bb9~38ac859d6ee0~24e9ab39e240~519e7d87f7c5~02a676d4e6ec~7255 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | Held-file authority and ACL-recovery envelope encoding/protection remain distinct jobs. Async opening, the staging default-buffer choice (:47-54, :117-120) and full-path rename extend the held-file job; both rename forms call SetRenameInformation (:383-421), not separate native implementations. 0.3.82 B-140A adds CreateDirectoryInExistingParent and its CreateDirectoryW import to the held-object job. |
| 9 | file-size | file-size | Split by job | Held-object opening/identity/rename/disposal (:49-462) differs from ACL recovery-envelope encoding/protection (:464-736); the guarded stream (:738-814) belongs to held-object authority. New async/rename/buffer members do not close this split debt. 0.3.82 B-140A adds CreateDirectoryInExistingParent and its CreateDirectoryW import to the held-object job. |

## src/SyncShark.Core/V2/Adapters/AutoSyncProcess.cs

<!-- spec-source: b44b83802f48~077098ddfcbe~b3dca97c0f3d~e53f468bac25~4abf1cfe5128~cd0c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 16 | class-size | class-size | Split by job | Startup registration/migration differs from detached process launch, readiness and native lifetime. |
| 16 | file-size | file-size | Split by job | Startup registration differs from native process launch/readiness lifetime. |

## src/SyncShark.Core/V2/FailureRetryOwner.cs

<!-- spec-source: 679a12673d2f~374367f7a8eb~2499f36e6942~7ed10d9893c7~d7b069fba8a3~3504 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | class-size | class-size | Split by job | Retry state/scheduling and notice wording/construction are distinct jobs; retain shared retry state. |
| 90 | file-size | file-size | Split by job | Retry state/scheduling differs from user-facing notice construction. |

## src/SyncShark.Core/V2/Adapters/HydrationNoticeWriter.cs

<!-- spec-source: b446585c807e~66eed7f7848c~5f6c42743961~d64a6ed71d18~d3b3b5bda049~5a02 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 16 | class-size | class-size | Split by job | Newly discovered pre-existing debt: 621 lines before R3, 623 after. Queue scheduling/cache bookkeeping and notice persistence are distinct jobs. R3 adds settings wiring, not a new job; the existing split debt is retained because splitting those pre-existing responsibilities is outside this tunables change. Planned retirement is not a reason the class cannot be split. A change that edits this listed file clears its rows in the same change or records in its review why they cannot be cleared yet. |
| 16 | file-size | file-size | Split by job | Newly discovered pre-existing debt: 636 lines before R3, 638 after. Queue scheduling/cache bookkeeping and notice persistence are distinct jobs. R3 adds settings wiring, not a new job; the existing split debt is retained because splitting those pre-existing responsibilities is outside this tunables change. Planned retirement is not a reason the file cannot be split. A change that edits this listed file clears its rows in the same change or records in its review why they cannot be cleared yet. |

## src/SyncShark.Core/WindowsCloudFiles.cs

<!-- spec-source: ee16130cf87d~a6c2080b7da5~cec7be1d2c17~04663626a59c~5300c0d10736~118e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 134 | class-size | class-size | Split by job | Root registration, placeholder residency and hydration admission/transfer are distinct jobs. |
| 134 | file-size | file-size | Split by job | Root registration, placeholders, hydration and identity/status contracts are distinct jobs. |

## src/SyncShark.Engine/Local/LocalSide.cs

<!-- spec-source: e16cf0d9d284~8f69a169dfa7~feb2a3cda34a~62c31b8f5986~176397989a3e~e9f5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 14 | class-size | class-size | Split by job | Checked IO, publication/removal, recovery custody and identity/drive observation remain distinct jobs. Held-stage lifetime and publication-boundary helpers extend publication (:74-315); StageAsync keeps the FileStream default buffer through the existing authority (:88-89), so the #90b growth adds no new job. 0.3.83 (#36): the page spool is created as an AdmittedSpool after a creation admission :827-845, +3 lines; checked IO, same split. 0.3.82 B-140A: EnsureFolderAsync creates only in an existing parent through the native owner; same publication job. 0.3.82 B-179: ReadByIdAsync and ReadByIdDocumentedAsync share ReadById (:474-512), +8 lines; identity observation, same split. |
| 14 | file-size | file-size | Split by job | Checked IO (:27-182), pending/publication effects (:184-482), recovery custody (:610-795) and identity/drive observation (:549-607, :852-886) remain distinct jobs; the publication boundary (:268-315) belongs to publication effects. #36 +3 lines in CreatePageSpoolAsync (see class-size). 0.3.82 B-140A: EnsureFolderAsync creates only in an existing parent through the native owner; same publication job. 0.3.82 B-179: the documented-absence by-id read (:474-512) is identity observation (see class-size). |

## src/SyncShark.Engine/Storage/StateEncoding.cs

<!-- spec-source: c64bd1818ca8~0d29f0727d0f~80a2bd1a9813~fe3510986d31~a9bc4d8bcf3c~5847 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 16 | class-size | class-size | Split by job | Compressed-history codec, strict JSON validation and SQLite row projection are distinct jobs. B-195 R7: ReadIdentifiedItems returns a stored issue subject's items that carry a file id; the shared strict path (DecodeValue, RequireShape/RequireItems with unknownIds) admits only a null IssueItem.Id, and that item is skipped. Strict JSON validation, same split, no new job. B-198: ReadSubjectItems is that same strict path returning every item (null id included); ReadIdentifiedItems filters it. Same job. |
| 16 | file-size | file-size | Split by job | Compressed history, strict JSON validation and typed SQLite projection are distinct encoding jobs. |

## src/SyncShark.Engine/Dropbox/AdaptiveLimiter.cs

<!-- spec-source: bbd1d87eae34~181bbd76856d~7d73a2b6328c~5f6fdf1d2107~c6f5f1154478~ca2b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 172 | long-member | AdmitUploadAsync((string AccountId, string RouteName, string SourceIde | Record why it cannot be split | Record one queue-node lifetime through provider wait, permit transfer or cancellation removal. |
| 118 | long-member | EnterWorkAsync(bool,CancellationToken) 44 | Record why it cannot be split | Record one FIFO admission lifetime, including direct-upload revocation and cancellation. |
| 5 | file-size | file-size | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 190. |

## src/SyncShark.Engine/Dropbox/DropboxApi.cs

<!-- spec-source: 074db16536bf~881b7e71f18e~f4bb76fd457c~94913e310a30~4aadccf43aa3~14c0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size (528) | Split by job | Two jobs that predate B-70: execute and settle one Dropbox HTTP exchange (request observation :24-80, entry points :82-212, CopyCompletion :269-275, ConsumeAsync :277-337, release/send/build :339-410, header acceptance and wait publication :412-461, failure decoding and classification :463-525, Exchange :526-540), and orchestrate retries across Request-owned exchanges (the `ExecuteAsync` loop, :214-267). B-70 classifies temporary and unknown writes through the transient-evidence owner DropboxErrors.TransientEvidence (:501); B-38 gives the retry Wait the failed request's RequestId (:232-233, :255) and threads it into ConsumeAsync (:280-282). Both grow the class inside those two jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Dropbox transport structural lane; destination: this row and the extracted owners in src/SyncShark.Engine/Dropbox/DropboxApi.cs. Closure requires the split and a fresh cross-family verdict; if the coupled seams are still changing then, the row stays explicitly open at that release gate, not closed. |
| 216 | long-member | ExecuteAsync<1>(string,object,Stream?,ContentBlockHasher?,DropboxReply<T>,RequestAttempt,bool,CancellationToken,string?,Func<ContentBlockHasher>?,string) :216-267 (52) | Record why it cannot be split | Record replay eligibility, request episode and terminal completion as one retry lifecycle (reason :214-215). B-70 adds the request-retry Wait (:250-255) inside it; B-38 adds the per-attempt RequestId (:232-233) that the Wait carries (49 to 52). Deferred with the structural work outside 0.3.82's serious-reliability-only scope. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Dropbox transport structural lane; destination: this row, the current ExecuteAsync verdict in tools/ci/spec-verdicts.json, and src/SyncShark.Engine/Dropbox/DropboxApi.cs. Close only after independent adjudication records an accepted one-job reason or a completed job split; otherwise keep this row open at that release gate. |
| 11 | file-size | file-size (540) | Split by job | The file holds DropboxApi alone, with the class row's two jobs: exchange execute/settle (:24-212, :277-540) and request retry orchestration (:214-275). The :11 comment names the exchange owner, not one job for the whole file. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Dropbox transport structural lane; destination: this row and the extracted owners in src/SyncShark.Engine/Dropbox/DropboxApi.cs. Closure requires the split and a fresh cross-family verdict; if the coupled seams are still changing then, the row stays explicitly open at that release gate, not closed. |

## src/SyncShark.Engine/Executor/FirstRunFolders.cs

<!-- spec-source: ddf42caa24c1~915ad9e723a6~739c953da99c~035945b3ae5a~bee64efeed8c~1b9c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 89 | long-member | FinalizeFirstRunFolderAsync :89-192 (104) | Split by job | Lock footprint :99-106; stale retry and settlement :107-111, :175-187; first-run deletion decision :123-158; admitted execution :159-173. Deletion decision is separable from retry lifetime. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: FirstRunFolders structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/FirstRunFolders.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |
| 9 | long-member | FinalizeFoldersAsync :9-66 (58) | Record why it cannot be split | Current deepest-first folder barrier schedules finalization, gathers failures and propagates unfinished ancestors before the next depth. Existing reason obligation is retained, not newly adjudicated. |

## src/SyncShark.Engine/Executor/FolderSync.cs

B-222 disposition: existing split/reason obligations remain open under their recorded structural owners and clearing triggers. The absence-retirement fix extends the existing retirement, closing or checked-removal job, not a new workflow. No debt row is cleared by this change; transactional ordering and proof forwarding remain with their current owners.

<!-- spec-source: 6f694a19ccf8~91cc4bccb0ef~f791b311d19e~d54b9c59fefd~6a0a34c25f96~d379 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (485) | Split by job | Current plan preparation/freeze and unfinished-map custody :17-134; follow-up/survivor/deferred completion :137-246; refused-survivor dependency-cycle diagnosis/reporting :248-289; settlement/group execution :291-317; exclusion/chosen/live/vacated/replacement routes :319-450; live-plan sequencing :452-490. Cycle diagnosis remains separate from unfinished-map custody, preserving the historical split obligation. B9 migrates item preparation/execution/rename calls to their owners. |
| 1 | file-size | file-size (491) | Split by job | Same current jobs and retained split obligation as the class row; the file holds FolderSync alone. |
| 361 | long-member | RemoveVacatedAsync :361-435 (75) | Split by job | Prior-operation recovery :365-374 is separable from fresh admission :375-396 and PrepareAsync validation :398-434. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: FolderSync structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/FolderSync.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Planner/Decide.cs

Closed rows: the class (353) and file (359) REASON-RECORDED verdicts stand in the ledger (one ordinary-policy job, reason :5; DropboxMatchesBaseline :225-227 is reused by InSyncIdentityHolds :215-217, UnchangedBoth (:178) and CrossedFar (:270-271)), so they carry no register row.

<!-- spec-source: c531a04bd7b1~f40bb27428ca~7bc3f6decf70~40f3de3e0af7~f1939013bb9f~5e7f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 73 | long-member | static NoRecord(Row,FirstRunPolicy,DateTimeOffset) :73-130 (58) | Split by job | Retain the carried SPLIT-PENDING verdict: first-run deletion evidence handling :86-98 and ordinary no-record precedence :99-129 are separable decisions; preserve their ordered Table A outcome. Deferred with the structural work outside 0.3.82's serious-reliability-only scope. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Planner ordinary-policy structural lane; destination: this row and the extracted first-run/no-record decision helper in src/SyncShark.Engine/Planner/Decide.cs. Closure requires the extraction with Table A precedence preserved and a fresh cross-family verdict; until then the row remains explicitly open at that release gate. |

## src/SyncShark.Engine/Run/HumanLog.cs

<!-- spec-source: 26fa9519b240~b8dea3ad8024~f7c33fac7c45~6825bf1a6dae~1d0c6fcf97fc~e672 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 321 | long-member | AppendOutcomes(StringBuilder,IReadOnlyDictionary<string, int>?,IReadOn | Split by job | Reclassified to split (R2-11): effect-row formatting and the notice/measurement footer use distinct inputs and are separable. |
| 9 | file-size | file-size | Record why it cannot be split | Record one finite-state human projection shared by live and historical input. |

## src/SyncShark.Engine/Run/Monitor.cs

<!-- spec-source: ffddbe4ef3f7~f7718eb9fdfc~176e4f0d4ee1~41c7a9c567e4~715ab52c6bfe~9dec -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Record why it cannot be split | One timing authority: wake and deadline registration, earliest-due scheduling, callback lifetime and shutdown; the item wait observation belongs to it. |
| 190 | long-member | RunAsync(CancellationToken) | Record why it cannot be split | Record deadline selection, arming, dispatch and wake consumption as one scheduling loop. |
| 6 | file-size | file-size | Record why it cannot be split | Record one earliest-deadline scheduling and shutdown authority for all opportunity kinds. |

## src/SyncShark.Engine/Run/PairScan.cs

<!-- spec-source: 224b8bd46596~a2034b026fb3~8b8cbfaffd60~1cd07f2e3600~9ea4a7e519a4~e37d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 35 | long-member | RunAsync(TaskSet,RunLog,bool,long,CancellationToken,string?,Action<str | Record why it cannot be split | Record both producers’ shared cancellation/join and incomplete-result bracket. #36 (43 to 47): a disk-full in the listing or either abort is never reduced to an incomplete inventory :74-78; the same bracket. |
| 115 | long-member | static ListAsync(ISide,IScanSink,List<(string Path, TextKey Cause, Iss | Record why it cannot be split | Record ingestion, skip capture and complete/abort cleanup as one streamed transfer. |

## src/SyncShark.Engine/Storage/StateCommitter.cs

Closed rows: the class (362) and file (368) REASON-RECORDED verdicts stand in the ledger (one ordered StateChange interpreter inside the caller transaction, reason :6 and :11), so they carry no register row.

<!-- spec-source: 2b482059ec1b~d342bb8e6bdc~2694f43828b6~14e74966c59d~ac95eb099737~72cd -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|

## src/SyncShark.Core/AtomicFile.cs

<!-- spec-source: cb34ca9563d0~b796e160a654~dc12bd61a307~3b65a40f0722~11c9e84c32b1~e2eb -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-type-name | file-type-name | One type per file / name matches type | `AtomicReplacePolicy` is a genuine top-level record in a differently named file. |
| 5 | top-level-types | top-level-types | One type per file / name matches type | Two top-level types: `AtomicReplacePolicy` and `AtomicFile`. |

## src/SyncShark.Engine/FolderRetirementDisposition.cs

<!-- spec-source: 7baab7f113f2~8f4c1c5faeca~7013efc7fd0b~6887ac24297b~acd5d48effe2~3132 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | file-type-name | file-type-name | One type per file / name matches type | Top-level `FolderRetirement` does not match its file. |
| 5 | top-level-types | top-level-types | One type per file / name matches type | Genuine disposition enum and policy class, not nested/generated declarations. |

## src/SyncShark.Core/PilotSafety.cs

<!-- spec-source: 0fc92e060318~be00859e1b33~17274611dfe8~699385420673~a4eaaaa46da9~5f3d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 371 | long-member | static OpenCore(string,PilotDirectoryIdentity?,bool,bool,bool,bool,boo | Record why it cannot be split | Record one directory-handle acquisition, validation and transfer-or-disposal lifetime. |

## src/SyncShark.Core/SyncDataLocation.cs

<!-- spec-source: 426bb94faa3b~73c7df8070e2~09b8f0e07549~e0c1346fc5e9~60b83559d22c~462d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 69 | long-member | static MoveAsync(PilotStateAuthority,string,CancellationToken) 127 ->  | Split by job | Destination preflight and recursive copy are separable from publish/pointer commit; the only nearby text is a caller precondition (:68), not a reason |

## src/SyncShark.Core/V2/Local/DateRepair.Restore.cs

<!-- spec-source: 5e9d2ee9d50f~9627282231c0~ee96fb227360~b60fe9f3586b~862d9722c7c5~1e09 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 18 | long-member | static RestoreAsync(IStateStore,PairDef,string,string,bool,IReadOnlyLi | Split by job | Audit decoding/output serialization differs from guarded reversal of a verified date change. |

## src/SyncShark.Core/V2/Adapters/EngineComposition.cs

<!-- spec-source: 35a981718a2d~94150f3c65aa~edae1f8ca822~e5f4166f8e94~25415e0af044~1070 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 85 | long-member | static Open(EngineHostInputs,V2Settings,TimeProvider) 60 -> 65 | Record why it cannot be split | Record one composition acquisition transferring resources together or disposing partial construction. |

## src/SyncShark.Core/V2/PathRules.cs

<!-- spec-source: 3cf360523d78~665990a0a6db~6cdb66355c3f~e4a3c0cc85bc~dee3d7c4db5a~870c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 21 | long-member | static NormalizeRelative(string) 48 | Record why it cannot be split | Record one normalization procedure preserving the allocation-free canonical fast path. |

## src/SyncShark.Engine/Dropbox/DropboxDeletionEvidence.cs

<!-- spec-source: 2d4190b999dd~4ad3824a3dc2~d65adeb3cef0~44b7557f9155~b18259969755~a14f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 41 | long-member | ReadFileAsync(string,CancellationToken) 47 | Record why it cannot be split | Record target observation, retained-history head and identity recheck as one ordered evidence acquisition. |

## src/SyncShark.Engine/Dropbox/ResponseStream.cs

<!-- spec-source: 61d35cccf56d~d51b102171e9~472b7d3afd48~e541e574ce33~025d4367d1ad~260c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 81 | long-member | ReadAsync(Memory<byte>,CancellationToken) 55 | Record why it cannot be split | Record cancellation, source-failure attribution and exactly-once completion as one read lifetime. |

## src/SyncShark.Engine/Dropbox/UploadContent.cs

<!-- spec-source: 4d162bd7ff93~22cce5040ec7~6725780e693d~2255ad6045ec~0be46fd99879~ca9a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 118 | long-member | SerializeCoreAsync(Stream,CancellationToken) 49 | Record why it cannot be split | Record one pooled-buffer, fused read/hash/write and terminal-seal lifetime. |

## src/SyncShark.Engine/Edge/EngineHostClient.cs

<!-- spec-source: 9f8daff058d0~6c8a5f3ca10a~f6fa06061d5f~a901116a815c~428cf19e86c2~b69b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | TryExecuteResultAsync(string[],TextWriter,TextWriter,CancellationToken | Split by job | Repair wait-monitor presentation differs from authenticated Accepted/output/Exit transport. |

## src/SyncShark.Engine/Edge/EngineHostServer.cs

<!-- spec-source: 722a071da62e~7d1b31db648c~9a9cd4df188a~bd13d296f856~2b3188a4785a~a9e0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 94 | long-member | ServeAsync(PipeStream,CancellationToken) 42 | Record why it cannot be split | Record trusted intake and irreversible Accepted-to-Exit handoff as one connection lifetime. |
| 34 | repeated-procedure | ListenAsync(NamedPipeServerStream,Func<NamedPipeServerStream>,int,CancellationToken,Func<ValueTask>) 56 | Extract shared owner | The bounded accept/replace/prune/shed/join loop :46-65 repeats AutoSyncBackgroundHost.ListenAsync :102-145 (pre-existing; #46 moved this copy here from the app entry; B-56). That copy is V2 and is deleted in 0.3.81, which removes the repetition; if any second host keeps a loop, one LocalPipePeer listener beside AcceptAsync owns it and must keep IsCompletedSuccessfully pruning :55 and the shutdown hook. |
| 34 | long-member | ListenAsync(NamedPipeServerStream,Func<NamedPipeServerStream>,int,CancellationToken,Func<ValueTask>) 56 | Split by job | 0.3.80.1 (W2): 37 to 56. Fault-to-stop (ServeConnectionAsync :80-89 cancels intake) and the ordered drain (:68-78: dispose waiting, cancel intake, owner shutdown, then join) join the accept loop :46-65. The accept/replace/prune/shed loop is the shared-listener job above; the drain is this host's lifetime and stays here when that loop moves out. |

## src/SyncShark.Engine/Executor/AdmittedExecution.cs

<!-- spec-source: 2d7a4d8f856a~ddb42897cf82~e996b9231802~96bce1d6872b~5fe0f744cae2~a7f8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static AcquireAsync(IReadOnlyList<ExecutionRequest>,Func<bool>,Cancell | Record why it cannot be split | Record canonical acquisition and reverse unwind as one acquisition transaction. |

## src/SyncShark.Engine/Executor/FolderPlanBuilder.cs

<!-- spec-source: bfa7c7ad943e~7d63e2432989~3c9f7818da52~1ea5a7532569~445ee014b419~e0c4 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 30 | long-member | BuildCoreAsync :30-101 (72) | Split by job | The reason at :29 asks that one open plan handle cover the whole pass; it does not make the pass one job: build one folder plan under one open plan handle whose disposal and completion cover the whole pass. Inventory: identity guard and plan open with IO-error mapping :35-46; capture with incomplete-inventory translation through ItemFailures.InventoryFailure (ItemFailures.cs:152-153) :48-59; per-child classification through ClassifyAsync :118-154 and MarkClassified :61-72; survivor branch selection through FolderDeletes.SelectBranch :74-84; completion and readability :85-87; disposal with the combined primary/cleanup error :89-100. Since 1383b8714 (bb1b58c3c HARD-R5), the temporary-selection predicate is threaded from BuildAsync :18-20 to ClassifyAsync, where FolderSync.TemporaryThisRun (FolderSync.cs:158) decides it once :133-134. That is 70 to 72 lines: one parameter and a wrapped call, with no new job. Survivor branch selection :74-84 reads and writes only the open plan (PrepareBranches, RemovedChildren, HasSurvivorAtOrBelow, HasSelectedAncestor, SelectBranch) and can move to a helper called inside the same try, so the handle's disposal :89-100 and Complete :85 still cover it; the ledger row is SPLIT-PENDING. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: folder-plan structural lane; destination: this row and the extracted owners in src/SyncShark.Engine/Executor/FolderPlanBuilder.cs. Closure requires the split and a fresh cross-family verdict; if the coupled seams are still changing then, the row stays explicitly open at that release gate, not closed. |

## src/SyncShark.Engine/Executor/KeepBothRunner.cs

<!-- spec-source: 4a15da5ca912~3b6f62aefe53~290e215a65d2~98ab2f80875b~46ab1b2b69dd~4f5e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 22 | long-member | RunAsync(Row,KeepBothLabels,CancellationToken) 48 | Record why it cannot be split | Record selected keep-both legs and their combined binding change as one result. |

## src/SyncShark.Engine/Executor/Parents.cs

<!-- spec-source: 79c23f214f6a~7c5e30a279ff~a6c63543a819~dc2167270ec4~d62873a0b7bd~8191 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 40 | long-member | EnsureAsync(Side,string,Entry?,CancellationToken) 55 | Record why it cannot be split | Record ancestor dependency ordering and coalesced waiter release as one creation protocol. |

## src/SyncShark.Engine/Executor/Recovery.cs

<!-- spec-source: 30ee9f234e59~ad8374785e52~b3fdf1ef1eab~1f482a28fa97~0f3b6edc16a0~ed8d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 85 | long-member | ResolveFolderChoiceAsync(FolderRemovalState,RecoveryChoice,long?,long? | Record why it cannot be split | Record exact-target validation and selected recovery action within one admitted choice. |

## src/SyncShark.Engine/Executor/ResolutionRunner.cs

<!-- spec-source: 2c4be4ef0693~4ff2b5f3229d~fa7ca9e4fa28~eaa544ddf26c~9f150f3295e8~7073 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 1 | file-size | file-size (325) | Split by job | Retry (RetryAsync, SettleMoveAsync :99-147; SyncDescendantsAsync, SyncFollowUpAsync, FollowUpFailureAsync, Unobserved :182-263) is a separate job from choice resolution (:14-97, :148-181, :265-325). The unknown-move Retry pushed the file over the figure. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ResolutionRunner structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ResolutionRunner.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |
| 6 | class-size | class-size (325) | Split by job | Same jobs as the file row; one type. |
| 31 | long-member | ResolveAsync :31-97 (67) | Split by job | Choice routing :34-43; held refusal :51-55; separable folder-replacement continuation :59-70; root execution :73-76; follow-up collection :77-82 and sync :90-96. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ResolutionRunner structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ResolutionRunner.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |

## src/SyncShark.Engine/Executor/Transfers.cs

<!-- spec-source: 14a27b539235~644020a3bb77~8aabb6913d9e~314a68d6dc7f~b2806ae386df~e870 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 101 | long-member | StageAsync :101-150 (50) | Record why it cannot be split | Current stage loop initializes source/attempt custody :102-113, reserves and opens/stages the source :115-122, closes source views and settles the attempt before retry/error :124-137, releases the reservation :139-143 and completes the successful attempt before returning borrowed source views :144-146. The existing reason obligation is retained. |
| 40 | long-member | CopyAsync :40-80 (41) | Record why it cannot be split | One stage, publication and custody lifetime: stage disposal, source handoff, replacement turn, publication callback and NoEffect settlement belong to it; forwarding the admitted new-Local-item fact (0.3.82 B-140A) adds no job. REVIEW-B140A-1 (openai) verdict REASON-RECORDED at the current signature. |

## src/SyncShark.Engine/IssuePolicy.cs

B-222 disposition: existing split/reason obligations remain open under their recorded structural owners and clearing triggers. The absence-retirement fix extends the existing retirement, closing or checked-removal job, not a new workflow. Obsolete gains the live-episode predicate and shared absence predicate; its changed signature and local-function shape require a fresh cross-family reason verdict before acceptance.

Category-change scope: PublicKind and its immutable cause list own public labels beside the stored issue policies; the existing Obsolete decision is unchanged. Its recorded single-transition reason remains applicable; independent review must verify the new category owner separately.

<!-- spec-source: 9b493aacd927~c330327a0492~f97f9f045c67~ccaa51e137d8~b113c613d75e~d85f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 1 | file-size | file-size (335) | Split by job | Ignore consequence and closure authority :10-47; first-run retained-subject policy :53-117; holds and keys :120-221; stale-retry policy :222-247; protected skip :251-254; retirement :262-335. Moving the first-run helpers to their own owner brings it under the figure with no rule change; deferred because it touches storage callers mid-integration. Clearing trigger: cleanup-only 0.3.83 structural pass, after the transitional IgnoreConsequence/Retried members are deleted at 0.3.82 integration; owner: IssuePolicy structural lane; destination: this row and a first-run policy owner beside src/SyncShark.Engine/IssuePolicy.cs. Closure requires the move and a fresh cross-family verdict; no product wait. |
| 262 | long-member | Obsolete :262-319 (58) | Record why it cannot be split | One issue/scope evidence set decides retirement by ordered predicates; the P5/P6 lines are its own rule. SameStaleEvidence :197-200 now compares validated previous/current digests without granting null-current authority. |

## src/SyncShark.Engine/Local/DownloadSpace.cs

<!-- spec-source: e14bf87a36f9~bc19f6adf8c5~02d5d3ddcf5a~de2fbec56f90~66aa37ce545c~0fe8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 26 | long-member | ReserveAsync(string,string,long,CancellationToken) 44 | Record why it cannot be split | Record volume observation, free-space admission and lease creation as one reservation transaction. #36 keeps this body unchanged as ReserveInAsync(string,string,long,CancellationToken) :40-82 behind the download entry :27-28 and the IWriteSpace product-write entry :31-38; one reservation transaction. |

## src/SyncShark.Engine/Local/LocalListing.cs

<!-- spec-source: 31c71fa4c35f~3af900a416f6~8c499a2b46c0~1bef70c71c2c~8d795805b4d9~ffe5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | long-member | Walk(string,ListOptions,CancellationToken) 65 (:15-79) | Explanation exists at :13 (one walk lifetime); formal structural disposition remains pending | Record enumerator custody through observations, close failure and unreadable coverage. |

## src/SyncShark.Engine/Local/LogFiles.cs

<!-- spec-source: d573967f7532~254517a644b8~02c6a7d602f9~61b54dae1fac~855210bfe811~69e9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | static Read(string,Action<string, bool>,int,CancellationToken,out bool) 57 | Split by job | Filesystem open/read/error ownership differs from incremental BOM/newline record framing. |

## src/SyncShark.Engine/Local/UsnPaths.cs

<!-- spec-source: c228a2637a61~8b6bee696ee8~a07db713c2db~a32682ba5314~931a5190c6bf~61fb -->

No open §1.3 size obligation. Since 07ff9d097 (3b9d021a9, 36efd4772, cb637f88a), ResolveOne :118-151 is 34 lines. Hard-link alias notices moved to AliasNotices :184-203 and the single-path notice moved to PathNotice :153-170. Every unit is under the 40-line prompt, and the file is 243 lines, under the 300-line prompt, so the checker reports no size unit here. The old :106 reason comment went with the split, and the cleared row is deleted (AGENTS.md:105-107). The section remains only because the ledger's historical SPLIT-PENDING anchor for ResolveOne(ulong,uint,FileAttributes,...) (43 lines) links here. That anchor is provenance in tools/ci/spec-verdicts.json, not current authority.

## src/SyncShark.Engine/Planner/FolderDeletes.cs

<!-- spec-source: 486795467295~e1208ce42826~e1d3655b2853~f51c2982432c~3efc08cdb8db~ff60 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 36 | long-member | static FirstRun(Row,bool?,bool,bool) 44 | Record why it cannot be split | Record one folder decision over the same completed-content and mapped-target evidence. |

## src/SyncShark.Engine/Run/FolderBatch.cs

<!-- spec-source: e10909bbddc7~4479e5933118~de3d081c8711~91de2b6f794e~7b184b5a644f~86a0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static RunAsync(ExecutorContext,SyncPorts,ISide,RunLog,WorkSelection?,Side,string,IReadOnlySet<long>?,bool,Func<CancellationToken, Task>,ScanOwnership,Func<bool>,CancellationToken) 69 (:11-79) | Lifetime explanation exists at :10; formal structural disposition remains pending | Record the scoped scan reservation through listing, execution, joining and cleanup. |

## src/SyncShark.Engine/Run/Repair.cs

<!-- spec-source: 0a036dce7d5f~2a5d3645311d~b3abacfbc170~ba8a87f17f02~e9f91348b2f8~be55 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static RunAsync(PairSession,CancellationToken,IReadOnlySet<long>?) :11-213 (203) | Split by job | The doc comments at :7 and :10 describe behaviour, not a reason it cannot be split. Inventory: admission and reservation :17-24; root read and the drive-change block :41-56; settling outstanding work with a disk check :58-61; scan reset, deleted-history restore and sink open :63-87; listing :88-90; post-list roots, execution and limiter save :91-110; checkpoint, catch-up and the completion closure :111-123; join, resource-failure preference and drive-evidence merge :127-144; summary and the authoritative completion with the superseded fallback :145-170; post-commit secondary work (scan end, log finish, prune, log-failed mark) :171-196; the finally block's owed-history handoff and scan end :200-212. Helpers: Recorded :218, RequireSpace :220-223, Primary :226-228. Since 3db618a47, B-38 (d178b1d4f, c49ab685f) routes the drive-change issue through store.Commit's settledIssue to ActionChanges.CollectSettlement and EmitSettled (ActionChanges.cs:100-120) at :49-54. That uses the one settlement-emission owner instead of a direct RunEvent.Issue, and grows the method 202 to 203 with no new job. Separable jobs: scan setup :63-87, summary publication :145-170 and post-commit secondary work :171-196. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Repair structural lane; destination: this row and the extracted methods in src/SyncShark.Engine/Run/Repair.cs. Closure needs the extraction and a fresh cross-family verdict; until then the row stays explicitly open at that release gate, not as an automatic product wait. |

## src/SyncShark.Engine/SettingsParser.cs

<!-- spec-source: 63e99df7b759~e06c0f2ce00a~d159873ac635~b54d00cd1156~51a4e27f77c4~b2da -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 20 | long-member | static Parse(JsonElement,JsonElement?) 49 | Split by job | Effective-settings construction differs from backoff representability/error-attribution validation. |

## src/SyncShark.Engine/Storage/IssueFacts.cs

<!-- spec-source: 7e28886bd111~dd8ad566cc75~e91d742fe709~b0458bb3b573~d2a7b78cebbd~0e2c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 17 | long-member | Replace(long,Issue?) 42 | Record why it cannot be split | Record remove-old/add-new indexes as one locked projection replacement. |

## src/SyncShark.Engine/Storage/ScanEntries.cs

<!-- spec-source: 276249ff51bc~9fc5d5d7f3bd~50d5bf5301d4~d216b5ff201e~974874b4c5ec~3277 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 56 | long-member | Apply(SqliteTransaction,Listed,string?) 41 | Record why it cannot be split | Record tombstone, override and type-replacement cases as one ordered scan-event application. |

## src/SyncShark.Engine/Storage/ScanWriter.cs

<!-- spec-source: d35afc472867~1994dd14669e~b8ed6112ca46~e5f9a67c2794~669a59c6b310~f912 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 93 | long-member | SaveLoopAsync(int) 45 | Record why it cannot be split | Drain up to one store batch and save it in one transaction under the write lock; #36 admits the batch's input bytes before the lock (:104-106) and holds the grant through the transaction, and an abandoned scan cancels a pending admission (:119-122). Admission is part of saving the batch: one job (#36 implementation and cross-family row reviews, size row 14; reason recorded above the unit :93). |

## src/SyncShark.Engine/Storage/ScanReader.cs

<!-- spec-source: 80d65037475c~585fba1383f3~87e7a37cbf79~bb80166a17c3~a25d937b08b5~645f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | long-member | static Rows(SqliteConnection,string,Func<Row, RowContext>,string?,byte | Record why it cannot be split | Record candidate grouping in one short snapshot closed before yielding caller work. |

## src/SyncShark.Core/DropboxProvider.cs

<!-- spec-source: cc2e940b0825~b3a872e29a61~1530099e5c47~3e2529ccd9fd~d75c6923100f~01f9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | file-size | file-size | Split by job | Connection/configuration, inventory/evidence, transfers/recovery and HTTP handling are distinct jobs. |

## src/SyncShark.Core/SyncCliViews.cs

<!-- spec-source: 4ec57d30e7f1~7d0489df2866~dfc3c136f995~6eae80368f65~975bcef6a2c6~0718 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 131 | file-type-name | file-type-name | One type per file / name matches type | Top-level `SyncCliNoticePreviewData` does not match its file. |

## src/SyncShark.Engine/Dropbox/DropboxErrors.cs

<!-- spec-source: 6a9f072231d8~6ac9225307af~7af300bc8c24~881ef43c0389~70aa11db4ba3~23de -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | B-70: the write-failure disposition and its decoration (`Disposition` :230, `NoUsableReply`, `Contention`, `WithProviderWait`, `WithMove`, `Copy`) is a sub-job beside refusal construction. Split at the next structure pass; kept whole in B-70 because the design names `DropboxErrors` the one classification owner (317 to 388). |

## src/SyncShark.Engine/Dropbox/DropboxFeed.cs

<!-- spec-source: f86983802328~51661fd98381~6be5decce82b~4a94072f929d~96711ac1cb5f~4d3e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Record why it cannot be split | Record installation, continuation, longpoll and reconnect as one cursor/handoff lifecycle. 0.3.80.1 (B-64): the finite-refusal flag _refusedCatchUp :83, set at :40 and consumed once in Accepted :355-362 through the existing WorkCommitted wake, is part of that lifecycle (366 to 375). 0.3.82 (B-163): the handed-out terminal _handedCursor :86 (set at :43, cleared and woken once at :241-243) and the resumed-turn skip AtTerminal :203 are part of the same hand-back lifecycle (375 to 391). |

## src/SyncShark.Engine/Dropbox/DropboxReply.cs

<!-- spec-source: 4aebb79b56b9~75f1eec41324~759329c36d88~e532d4155e12~55a9ea3e86a3~a26e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 312 | file-size | file-size | Record why it cannot be split | DropboxReply.cs now holds one type, DropboxReply<T> :10-312; the static DropboxReply moved out of this file. One incremental JSON reply decode; #36 adds the spool drain delegate (:16, :27, :63) inside that job (+3 lines). |

## src/SyncShark.Engine/IStateStore.cs

Category-change split clearing condition: the 0.3.83 structure pass clears these SPLIT-PENDING jobs once the storage, runner and Host interfaces they depend on are stable; splitting them while those interfaces change would conflict with that change.

<!-- spec-source: cecbc92074e9~8015f6d4423f~8245f65b1f71~df42d45002f0~650c5d311d90~05a2 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size (427) | Split by job | IStateStore.cs:5-427 spans the pair/work/pending/retry/folder-removal groups both reviews require StateStore to split along; owner decision 30 (docs/design/owner-decisions.md:101) is the interface-split precedent. ReadItemRetry :200-202, CompleteItemRetry :203-206 and RecordTemporaryItem :207-213 extend the existing retry group with item-only state and temporary-turn retention, not a new responsibility. The file was already 354 lines before these contract members; the split-by-job obligation remains open. B-137: CountWork :22 returns both sides' pending counts in one statement; same work group. 0.3.82 Host+DROP merge: the interface becomes partial (:5) and the FinishRepair contract names the kept item-only wait (:35-37), retry group (401 lines). Clearing trigger: the clearing condition above, at the cleanup-only 0.3.83 structural recheck; owner: storage structural lane; destination: this register row and the split interfaces under src/SyncShark.Engine/. If those interfaces are still changing then, the row stays explicitly open at that release gate, not closed. B-177: ReadReplacedItemRetry and RetireReplacedItemRetry state the replaced-episode read and its exact-token, registry-fenced retirement; two members in the same retry group, no new group. B-176: RefuseLaterEvidence :192 is the pre-capture form of ValidateLiveCoverage's later-evidence answer in the same work/coverage group. RG-1: ReadItemEpisodeIssues joins the same retry group. RG-R1: RetryNoticeProtection, the per-reconciliation form of RetryNoticeProtected, joins the same issue-answer group (RG-R1b: no default body, so every implementer prepares once). B-195b: the RetireReplacedItemRetry summary records a null fresh id (no file there) as proof that the recorded file is gone; same retry group, no new member. 43a: CommitItemRename extends the retry group as a default member. Current CommitItemRename :250-253 joins the retry/item-attempt group beside CompleteItemAttempt :245-249; same group, no new group, split still open. |

## src/SyncShark.Engine/PathNames.cs

<!-- spec-source: 9dfb6f0ae89f~fccf64295c9e~7f88a5659016~339cbec26fc8~76326d42101e~d953 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size (318; :12-329) | Split by job | The file's only type, so the same split inventory as the file-size row below; the :9 comment names the domain owner, not a constraint against splitting (REVIEW-RATCHET-0382-1 R1). Split actual jobs, keeping each shared rule in one owner. Same clearing trigger, owner and destination as the file row. |
| 1 | file-size | file-size (329; class :12-329, 318) | Split by job | The :9 comment names one path-identity owner, but the current file holds separable jobs. (a) Loading and validating the embedded frozen case table: CaseTable :17-23, LoadCaseTable :280-304, ReadScalar :306-312. (b) Key and comparison: :25-48 and :54-81, plus the folding helpers SameSpelling/UnderBoundary/FoldNext/UpperAscii :246-278. (c) The dormant spelling-mapping facade :50-53 and :83-103, with saved-parent mapping MappedDestination :140-152. (d) Relative-path grammar :106-138 and :154-217 (Child to HasDescendantKey and IsSelfOrUnder to IsSingleSegment), with the side selector Other at :105. (e) Dropbox root grammar :219-244. (f) Local root normalization :314-328. Job (a) parses a persisted JSON format, and jobs (e) and (f) are each side's root grammar; none of them is key or comparison work. Since e2e26b750 (ed76a395c), HasDescendantKey :192-197 is the one strict-descendant probe over an ordinal key set and reuses DescendantBounds :189-190 and IsUnderKey :163-165; that is grammar job (d), 322 to 329 lines. SameName :127-129 (B-92) is unchanged. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: path-identity structural lane; destination: this row and the case-table and root-grammar types split out of src/SyncShark.Engine/PathNames.cs, with docs/design/engine.md's domain-to-owner map updated in the same change (§1.1). |

## src/SyncShark.Engine/Run/PairSafety.cs

<!-- spec-source: a8aaa5699529~7b353cabd755~a5ba44ee66f6~faf7818bd0a9~3181cedf01a0~1ba1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | Same jobs as file-size; the class passes 300 lines (310) in 0.3.79. |
| 9 | file-size | file-size | Split by job | Root identity comparison/refusal is separate from execution entry admission (lock footprints, live/recovery/selected validation); header :8 names a grouping only. B-13 adds a third job, the reviewed-notice token (IssueFingerprint/ForReview :284-297), whose natural home is Issue beside Issue.Matches (306 to 318). |

## src/SyncShark.Engine/Storage/StateSchema.cs

Category-change scope: Public page, count and keyset statements reuse one bound SQL expression. The catalogue still contains declarations only, without execution or transaction policy, so arbitrary catalogue splitting does not separate a runtime job. Absence-retirement scope changes only the index access directives of PairItemEpisodes and ReadIssueRetryViews; the existing catalogue reason remains subject to content-bound review.

<!-- spec-source: a5947f1be144~dc830364aa06~a5907d71e161~21ab40c74699~a1ea19609984~5358 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | file-size | file-size | Record why it cannot be split | Record one declarative SQL/schema catalogue, without execution or transaction policy (reason :3). #90b adds schema version 11 and the published-USN columns and statements; B-21 adds the batched deleted-name insert (1433 to 1450). 0.3.80: the cut-over deletes the pair-import SQL; schema version 12 adds the discovery_required work column and its two statements (1450 to 1420). 0.3.80.1 (B-93): schema version 13 adds IssuePathKeyIndex :50 and its upgrade version :13, serving the existing SelectIssueByPathKeyKind :264 (1420 to 1424). B-137: the queue count is one const statement, CountWork (:793-797), composed from the shared const eligibility and epoch fragments (:774-780) and named count ordinals (:1230-1231), taking $localEpoch/$remoteEpoch for the epochs the caller already read (1479 lines); catalogue entries only. B-177: ItemEpisodesAtPath also returns issue id and identity (two ordinals) so the retry lookup is one path seek; same catalogue. |

## src/SyncShark.Engine/TextKey.cs

<!-- spec-source: a3d7850dced3~c6233b2a7b38~6065d4421ad8~3f0f90887dfa~d4a32ea525c9~934a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 3 | file-size | file-size | Record why it cannot be split | Record one strings-catalog identifier enum; arbitrary splitting would fragment that contract (reason :3). 0.3.80: the cut-over removes four import keys; adds the dependency-cycle, changed-retry and Retry-runs-Repair keys and 14 folder-browser/storage keys moved from the app into this catalog (now 441 lines). 0.3.83 (#36) adds RepairStoppedNoSpace and RepairCompletedSecondaryFailure. B-137 adds AutoSyncQueueAtStart (449 lines). |

## src/SyncShark.Engine/Executor/TaskSet.cs

<!-- spec-source: 9cbc0d8b10ec~fc3e422d6df2~53d9ed25659d~27419942deac~d3470eb68481~9535 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 40 | long-member | Add(Func<Action, Task>) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 106. |

## src/SyncShark.Engine/Local/JournalSource.cs

<!-- spec-source: 4dfe65a39c9c~dc936c1d91e1~07a3c1598b00~5d8944b3ddd6~031d150950f3~5af8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size (639; :6-644) | Split by job | Same seven jobs as the file-size row below; besides the journal-position lifecycle (:3-5) the class raises DriveChange issues from drive evidence (:275-283) and owns item-failure reporting (:436-454), so the earlier FALSE-POSITIVE no longer holds. Same clearing trigger, owner and destination as the file row. |
| 227 | long-member | CaptureFiniteAsync(IReadOnlyList<Member>?,CancellationToken,Action?) :227-267 (41) | Split by job | No reason comment sits directly above the unit: the :218-220 summary belongs to CheckpointAfterRepairAsync :221-225. The R2 "verify existing reason" action therefore cannot close it as REASON-RECORDED. Jobs: membership and obsolete-prefix admission under the gate :230-240; native journal state and retention check :241-250; the finite page loop :251-257; the final admission and commit with the stale-storage retry :258-266. The admission and retention checks are separable from the loop. Same clearing trigger as the file row. |
| 337 | long-member | ReadPageAsync(bool,CancellationToken,List<Member>?,ulong?) :337-386 (50) | Split by job | No reason comment above it. Jobs: choosing the classification vector under the membership gate :340-356; the per-member BeginPage and the native page read with its Observe callback :357-369; the page-status dispatch to RetainOrCommit, ClassifyAsync or Restart :370-385. Choosing the vector is separable from the read and dispatch. Same clearing trigger as the file row. |
| 3 | file-size | file-size (644; class :6-644, 639) | Split by job | The :3-5 summary gives the lifecycle reason (one durable position, one uncommitted interval), but the file holds separable jobs. (1) Pair catch-up and checkpoint entries: CatchUpForPairAsync :49-68, CheckpointForPairAsync :197-216, CheckpointAfterRepairAsync :221-225, CaptureFiniteAsync :227-267. (2) Source lifetime: ctor :73-92, Invalidate :101-105, CatchUpAsync :122-133, RunAsync :137-159, StartAsync :162-169, StopAsync :173-195. (3) Journal comparison and retention: Compare :269-304, InRetention :306-307, ApplyRetention :310-311, Restart :313-335. (4) Page read and classification: ReadPageAsync :337-386, ClassifyAsync :388-420, PublishClassified :422-434, CollectAsync :439-454. (5) Durable publication: RetainOrCommit :456-473, CommitRead :475-498, TryPublish :502-533, AcquirePage :535-551. (6) Candidate adoption: RefreshCandidates :553-565, AdoptCandidates :568-590. (7) Interval reset and fences: RestartRead :592-600, DiscardRead :602-607, SameAcquisition :609-623, RefuseCatchUp :625-629, Stale :631-632, FencesCurrent :634-642. Since 3f67c5690, cb637f88a extracted PublishClassified, AcquirePage and RefreshCandidates. TryPublish :502-533 is now 32 lines, under the prompt, so its row is cleared and deleted. The file grew 615 to 644 lines and the class 610 to 639 with no new job. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: journal-source structural lane; destination: these rows and the types split out of src/SyncShark.Engine/Local/JournalSource.cs (publication and candidate adoption first). If the coupled host seams are still changing then, the rows stay explicitly open at that release gate, not closed. |

## src/SyncShark.Core/ProviderActivityStream.cs

<!-- spec-source: 734a40e14a74~a37634f408c3~6e1b5c7e4c17~d965404deac9~6d3e9f8a2108~5f51 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 176. |

## src/SyncShark.Engine/Executor/RowContexts.cs

<!-- spec-source: 3924de5f9402~855023712dc3~771ba6cf1750~71678f4fd2b7~5866348ab32b~1564 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size (302; :12-313) | Split by job | Three jobs: per-execution row-context projection (:12-81, :163-269), the run-local hold and Retry-authority state (:83-161) and first-run deletion and inventory projection (:271-313); the gain since the last review is Covers(key, selectedIssue) :85-97 in the hold job. Same split, clearing trigger, owner and destination as the file-size row below. |
| 1 | file-size | file-size (313; class :12-313, 302) | Split by job | Three jobs with separate consumers and output types. (1) Per-execution row-context projection: factories :15-52, listing folder sets :54-70, WithProof :73-81, For/ForSelectionAsync/ForFollowUpAsync :164-192, UnderUnreadable :195-202, UnderRetainedSubtree with CoveringRoots/RootHolds/Known/Remember :205-240, Build :242-269. (2) The run-local hold and Retry-authority state machine: Covers :85-97, HoldRename/HoldRenamePaths :100-111, HoldsRename :114-120, HoldDependents :125-128, Observe :130-137, RetriesFor :139-145, AddExecutionRoots :147-161. (3) Completed-acquisition first-run deletion and inventory projection: RefreshDeletionEligibility :273-280, RefreshInventory :283-287, DeletionEligible :289-312. Split into RowContexts, ExecutionHolds and FirstRunProjection, calling IssuePolicy for hold and first-run policy and IStateStore for spent-key queries. ExecutionHolds and FirstRunProjection share the existing gate by reference. Preserve the current critical-section span and store-call order, without replacing Build's protected combination with separately locked snapshots. FirstRunProjection also owns firstRun and deletedName alongside acquisition validity and the template; eligibility is checked before the template read. Since 60e13434f, df767f62f adds Covers(string,long?) :87-97: with a selected issue, the store answer comes from IStateStore.HeldByOtherIssue instead of HeldCovers, and the rename and dependent-root checks under the gate are unchanged. That is job (2), file 309 to 313 lines and class 298 to 302, with no new job. The class-size row (item 4) shares this inventory and trigger. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: RowContexts structural lane; destination: this row and the three types above under src/SyncShark.Engine/Executor/. |

## src/SyncShark.Core/V2/Adapters/AutoSyncBackgroundHost.cs

<!-- spec-source: e4ada547ac88~c3da6019bc2a~1fc7c7313bc3~5ad556a516e3~f0b2d93abdf0~733f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 102 | long-member | ListenAsync(NamedPipeServerStream) 41 -> 45 | Split by job | The bounded accept/replace/prune/shed loop :108-129 repeats EngineHostServer.ListenAsync :43-70 and belongs to one LocalPipePeer listener; host shutdown, connection drain and authority release :131-145 stay here. The 0.3.79 growth only adopts the shared LocalPipePeer.AcceptAsync owner :107-113. |

## src/SyncShark.Core/SetupUpdates.cs

<!-- spec-source: 6c2bd264ed71~0cf0d2985905~9fc63ff8d8df~d8f99010a2d9~1f3f84a5325f~fc40 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 96 | long-member | static VerifySignatureAsync(string,CancellationToken) 47 | Split by job (B-57, 0.3.80) | The pinned-identity, chain and timestamp checks :113-141 are the same rule shape as SyncSharkUpdaterSignature.Verify (tools/SyncShark.UpdaterSignature.cs:40-71) but weaker: no exactly-one-signer check, no RFC3161 binding to the signature, no future-timestamp refusal and no exclusive/critical timestamp EKU. Calling that owner today would break the contract: setup is package-signed (docs/windows-installation.md:34-37, :121-122) and the owner ties the identity role to the container format (:40-53, :143-148). B-57 separates role from format in the one owner; setup then calls it with role=package and this unit keeps only the PowerShell evidence read :98-112. |

## src/SyncShark.Core/SyncPublicUpdates.cs

<!-- spec-source: 464458dadb6c~25a9749481f4~43c1f0881469~3f51f668ec92~b5153c98c8f8~baa5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 1 | file-size | file-size | Split by job | Three top-level types (section 1.3): SyncPublicUpdate :12, the legacy release discovery/download client SyncPublicUpdates :15-183 and the extracted-package lease SyncDownloadedUpdate :187-359. The legacy channel is superseded by setup (docs/windows-installation.md:136-138): delete it at its retirement change, moving the shared IsNewer :26 and CopyBoundedAsync :128 owners (both also used by SetupUpdates/SetupCli) to the setup channel first; otherwise split by type. |

## src/SyncShark.Engine/Executor/Syncer.cs

<!-- spec-source: 6782e50a4cc5~00f2d7d690c5~c60af1ab32cb~c6118d9ffa58~7e5ae13b31ce~baef -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 169 | long-member | SyncSelectionAsync :169-290 (122) | ACCEPT (current authority: the current review's §6.2 job inventory classifies the dependency traversal as one state-machine job — REVIEW-LOGS-0382-4, whose F2/F7 subject and prerequisite-classification findings are fixed in this change and await re-review. Historical provenance only: REVIEW-B9-B134-1 reviewed an earlier 83-line form, and the ledger's 111-line REASON-RECORDED anchor carries forward under the 10 % growth rule.): one traversal state machine, reason comment :167-168 | Pending-chain setup :172-182; per-frame cancellation and subject reset :185-191; ancestor traversal :192-199 (stale fence :195); pass execution inside the B-133 stale-run boundary :200-207; the rename-prerequisite catch closes the dependents' selection and ends the pass for a held Local write, leaving their work pending for the owed Repair, and only refuses a prerequisite clash :208-225; prerequisite push and cycle refusal :226-235; an already-attempted prerequisite closes its dependents without a fabricated NameClash (B-147) :236-250; refused/clash containment :251-262; retire :263-264; held/refused reporting names the attempted subject from the branch boundary, with the selection frame only as the pre-subject fallback (B-146) :266-289. Rename prerequisite refusal calls SelectionRenames.RefusePrerequisite through pass.RenameWork :223/:230/:260. The stack, visited set and per-frame outcome share one selection lifetime and one stale-run boundary; splitting would pass that state between methods. |
| 1 | file-size | file-size (419) | Split by job | Same jobs as the class row; the file holds Syncer alone. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Syncer structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/Syncer.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |
| 6 | class-size | class-size (414) | Split by job | Run orchestration :80-119, lane scheduling RunLanesAsync :122-184, selection traversal SyncSelectionAsync :188-336, temporary-outcome bookkeeping :338-386 (with the IR-1 HeldForRetry checks :355, :364) and settlement/recovery/resolution forwarding :388-419 are distinct jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Syncer structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/Syncer.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/ExclusionRunner.cs

<!-- spec-source: 6a4de93d0b02~e649c3286bc2~5d52f90009e9~84d7b91e1357~c333b2924576~d4a7 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 72 | long-member | PreserveCopiesAsync (63) | Split by job | Walk/dependent-prefix bookkeeping and each child observe/prepare/custody/admit attempt are separate jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ExclusionRunner structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ExclusionRunner.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/ItemExecution.cs

<!-- spec-source: fe59daaf6912~152c8b8addd1~7a177a87a132~673cca17ef75~0a0435590094~6692 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | long-member | RunAdmittedAsync :10-141 (132) | Split by job | Retry state :20-31/:113-121; reobserve-before-prepare :35-43; prepare/no-effect :44-55; admission/handoff :56-77; adoption :78-96; failure mapping :99-112; custody :26/:124-127; refresh :129-140. Duplication is removed; adoption mutates eight loop locals and needs an agreed state protocol before extraction. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ItemExecution structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ItemExecution.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/ItemPreparation.cs

<!-- spec-source: 2400ff75e1a1~87966f7fab2c~e656f5c92bd5~3a80e381ace9~5f3a5a40aa1d~53b6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 50 | long-member | PrepareAsync (54) | Split by job | PrepareAsync (54): planner preparation/destinations, proof custody, deletion evidence and hash acquisition remain separate jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ItemPreparation structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ItemPreparation.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/SelectionBranches.cs

<!-- spec-source: 9da5cd9bca66~4ec31942f0d7~ca2a4b287b94~438dc1cafd21~41679e73e54d~ca71 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 84 | long-member | EnumerateBranchesAsync :84-235 (152) | Split by job | EnumerateBranchesAsync :84-235: primary identity acquisition with its fence, the B-179 gone-identity probe (ProbeAsync :64-82, used at :104-145 with the unowned-occupant check :116-122) and relocation :123-140, old path :162-169, batch :173-190 and walk/continuation :191-232 remain separate jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: SelectionBranches structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/SelectionBranches.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/SelectionPass.cs

<!-- spec-source: 4f944e499e04~9db686c8e815~8577cf32e3f1~3506aabbff91~c08f7566de5f~d959 -->

No open §1.3 size obligation. The 0.3.82 extraction moved branch enumeration to SelectionBranches and selected renames to SelectionRenames, leaving this type the pass lifetime alone (active row/branch, refused row, prerequisite, per-selection lifecycle). The current class spans :6-240 (235 lines) and the file 240 lines (B-179 adds the gone-branch skip in RunSelectionAsync), both under §1.3's 300-line review trigger, which prompts review rather than setting a limit. The cleared rows are deleted (AGENTS.md:105-107); the section remains only because the ledger's historical SPLIT-PENDING anchors (673/678) link here, and those anchors are provenance in tools/ci/spec-verdicts.json, not current authority.

## src/SyncShark.Engine/Executor/ItemRenames.cs

<!-- spec-source: 9042e1e1cc49~9323039b421a~dfa6685fe633~67c13b24fcd9~f3a849c4cf2f~28d8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 25 | long-member | SettleUnknownMoveAsync :25-92 (68) | Split by job | Close and bound Local identity read :29-52; act and classification :53-76; exception restore decision :78-91. Extract the read and the unsettled predicate as private methods. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ItemRenames structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ItemRenames.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |

## src/SyncShark.Engine/Executor/ItemFailures.cs

<!-- spec-source: 101f9fc8296e~4736fa73a5d7~0f0718727c97~6c53bba63a28~88b35ef8aec0~a79e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 108 | long-member | FailCoreAsync :108-178 (71) | Split by job | Jobs: first-run decoration :112-113; filter and cancellation :114-116; mapping :117-122; disposition :123-126; temporary settlement and return :127-135; occupant read and subject enrichment :136-157 (separable provider I/O); captured continuation and change decoration :158-166; dispatch and settlement :167-175; emit and outcome :176-177. The debt predicate calls ActionChanges.Owed (ActionChanges.cs:8) rather than adding a second debt owner. Since e93f8ee5b, 333bda006 (B-38 G8) replaces ActionExecutor.EmitIssues with the settled-issue callback: ActionChanges.CollectSettlement collects only issues the store actually settled, and EmitSettled publishes them (ActionChanges.cs:100-120, here :142-148, and in SettleFailure's settledIssue parameter :52 and the local-branch caller :40-44). Events are decided once from the committed settlement (§1.7), and the method grows 64 to 66 with no new job. It is still more than one job: occupant enrichment is separate provider I/O. Clearing trigger: the cleanup-only 0.3.83 structure pass after the coupled storage, runner and Host seams land; owner: ItemFailures structural lane; destination: this row and an extracted occupant-enrichment method in src/SyncShark.Engine/Executor/ItemFailures.cs. The release review keeps this finite code obligation open until the extraction and a fresh verdict; no product queue or automatic wait is introduced. 43a: change decoration reports a continuing local-access wait with its captured token items and Local spelling (+7 lines); same job. 43a continuing-report integration: FailItem :39-40 retains the captured Local spelling for inventory and journal reports, same job. N1 remains open for the ItemFailures structural lane: FailItem :40 uses LocalItemAt(key), while FailCoreAsync :161 uses FirstOrDefault(Local), as does RenameRunner :538. Captured subjects have exactly one Local item at that key today; give this spelling selection one private expression at the existing structural extraction, with fresh review. No production change in this fix. |

## src/SyncShark.Core/NtfsIdentityPaths.cs

<!-- spec-source: 4046aab9b4ae~93865a46beab~7d59724b7378~5c0acc31ef13~ce7754e2e11d~d129 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 68 | long-member | static Resolve(string,DirectoryIdentity,ulong,bool,bool,CancellationToken,Action?,Action?,Action<ulong, bool>?) :68-107 (40) | Split by job | The :67 comment keeps the volume and identity handles held, but a helper can return the held handle, so it does not prevent extraction. Root-volume open and serial verification :77-90 is one job; identity open and absence mapping :91-97, link count and final path :98-105 and link delegation :106 are another. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: Core native-identity structural lane; destination: this row and the extracted volume helper in src/SyncShark.Core/NtfsIdentityPaths.cs. Closure requires the split and a fresh cross-family verdict. |

## src/SyncShark.Engine/Executor/SelectionRenames.cs

<!-- spec-source: f1d29c0d6f1f~d2723791288f~b4132c38b84f~c865e192effc~93be38af513c~694c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 101 | long-member | RefusePrerequisite(WorkSelection,IEnumerable<SyncedEntry>?) :101-141 (41) | Split by job | No reason comment. Issue-subject assembly from the notice, saved binding and blockers :108-121 is separable from clash-refusal routing (Covered/Attach/raise) and commit :122-137; the selection ends in the finally (:140). Callers :55, :69, :83 and Syncer.cs:261, :268, :306. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: Executor rename structural lane; destination: this row and the extracted subject builder in src/SyncShark.Engine/Executor/SelectionRenames.cs. Closure requires the split and a fresh cross-family verdict. |
