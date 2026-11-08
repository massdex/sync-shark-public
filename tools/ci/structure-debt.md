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
were reviewed by DeepSeek at source 1ba4297d3; original anchors remain at 3f6745416.
An XML summary satisfies the mechanical nonempty-comment check; semantic review still verifies its reason.

Literals (§2.1/§2.4) are tracked as two open items, not per row: 32 undeclared tunables and magic values move to the
settings data file (lane R3), and 112 unowned user-facing strings move to their owner with the CLI consolidation.

Totals: 207 rows in 90 files. Planned own lane: src/SyncShark.Engine/Storage/StateStore.cs (0.3.78).

## Tooling review reasons

These tooling records are not runtime-source debt rows and do not change the source totals above.
Line ranges describe this correction; named units remain the identity if subsequent edits move them.

| Unit | Jobs and line ranges | Recorded reason |
|---|---|---|
| `tools/ci/check-spec-ratchet.py:collect_review_units` | Evidence construction 236–241; shared owner/size/member inventory 243–255; member/local units 256–273; class units 274–279; file unit 280–283. | These are one review-unit output from one parsed inventory. Keeping its accumulation together avoids re-parsing declarations or passing a second mutable inventory between stages; receipt/freshness policy stays in spec_reviews. |
| `tools/ci/check-spec-ratchet.py` | Source findings 83–343 (literal counters 116–163, declaration inventory 166–283, file assembly 286–324); baseline/comparison pipeline 346–520; CLI orchestration 523–538. | This is the existing snapshot-safe command, coordinating parser, identity and review owners. The retained source/baseline stages form its one comparison pipeline; splitting its command-local stages into another module would add shared counter/key protocol without separating another policy. Review policy and lexical parsing already have separate owners. |
| `tools/ci/spec_cslex.py` | Mask literals/comments 12–188; delimiter matching 191–219; parsed member representation 229–245; list/signature parsing 248–403; type/member parsing 406–526; source coordinates 529–532. | These stages implement the existing offset-preserving C# lexical parser. They share masked-source offsets and one member representation; callers consume this parser as one module, while structural decisions remain outside it. |
| `tools/ci/test-spec-ratchet.py` | Synthetic source 20–21; public checker contract cases and their fixture 24–594; assertion-producing mutation controls 597–742; unittest entry 745–746. | The contract suite and controls deliberately share one fixture and execute in one discovery entry. Keeping the paired positive/negative evidence here prevents fixture copies or a second test runner; no production hook is introduced. |

## src/SyncShark.Engine/Storage/StateStore.cs

<!-- spec-source: 3314bbcca51b~6729e956352c~44da9f7b2458~678769adb7f5~bb9c0e1ccc92~9e80 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 8 | class-size | class-size | Split by job | Connection lifetime, schema migration, scan/history and folder persistence are distinct jobs. 0.3.79 adds expected-notice fencing for retry reads and visibility (B-13), the live-refusal read (R6), the published-USN migration step (#90b) and block-wise history restore (B-21) to the same class: 3490 to 3588 lines. |
| 2683 | long-member | AdvanceSubmission(SqliteTransaction,long,FolderSubmission,FolderSubmis | Record why it cannot be split | Record submission and unsettled member transitions as one atomic receipt application. |
| 2578 | long-member | ArmFolderSubmission(string,IReadOnlyList<FolderRemovalEffect>) 43 | Record why it cannot be split | Record exact-effect validation and immutable wire membership as one all-or-none transition. |
| 1932 | long-member | BeginFolderRemoval(string,FolderPlan,int,CancellationToken) 134 | Split by job | Counterpart saved-only range discovery with ancestor anchoring and path de-duplication is a separable job from operation and member insertion |
| 1839 | long-member | CompleteItemAttempt(ItemRetryCandidate,Guid,StateChange,PendingEffect? | Record why it cannot be split | Record failure/success outcomes, custody changes and reporting for one claimed attempt. |
| 3082 | long-member | DrainRecords(out Exception?) 108 | Split by job | Queue take and failure-isolation scheduling are separable from transactional apply with post-commit publication and closure update (local Apply) |
| 712 | long-member | EndScanAsync(string,Guid,int) 46 | Record why it cannot be split | Record whole-file/partition alternatives and failure cleanup as one scan-ending lifetime. |
| 1287 | long-member | FinishRepair(string,RepairSummary,long,Guid,LocalCatchUp,DropboxCatchU | Record why it cannot be split | Record source/checkpoint fences and retirement of covered work as one durable completion. |
| 2069 | long-member | InsertFolderMember(string,string,long,FolderRemovalItem.ProvenanceKind | Record why it cannot be split | Record provenance/scope validation and reuse/insertion as one recovery-member admission. |
| 1765 | long-member | RecordInUseFailure(string,long,long,string,IssueSubject,TextKey,WorkSe | Record why it cannot be split | Record attempt validation, issue/work binding and episode claim as one retry-responsibility installation. |
| 769 | long-member | RestoreDeletedHistory(string,Guid,Action<long>?) 57 | Record why it cannot be split | Record verified replay and continuation publication as one completed acquisition. B-21 grew it 43 to 57: one block per lock hold with a withdrawn-cursor check and a progress count between blocks; the per-block step is still owed as its own method. |
| 1217 | long-member | SavePairDecision(PairConfig,long,PairConfig,Issue?,bool,string?) 67 | Record why it cannot be split | Record definition, generations, responsibility and scan invalidation as one configuration transition. |
| 2428 | long-member | ValidateFolderChange(SqliteTransaction,long,string,FolderRemovalState, | Split by job | Pure permitted-change-shape validation differs from database evidence traversal and coverage counting. |
| 1112 | long-member | static ForgetTouchesUnreadable(SqliteConnection,string,IEnumerable<(Si | Record why it cannot be split | Record local/mapped ranges as one deduplicated forget-footprint overlap query. |
| 3338 | long-member | static InitializeSchema(SqliteConnection,bool) 119 | Split by job | Version admission/reference validation, read-only handling, transactional upgrade and reclaim/checkpoint maintenance are separable; #90b adds the version-10 published-USN step (111 to 119) |
| 8 | file-size | file-size | Split by job | Connection lifetime, migration, scan/history and folder persistence are distinct jobs; 3497 to 3595 lines in 0.3.79 (see class-size). |

## src/SyncShark.Engine/Run/EngineHost.cs

<!-- spec-source: 9ecda25a1f60~3089964a281a~7820aae26e10~a315b25bc874~7378b2fb375b~a306 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size | Split by job | Composition, generation admission, source supervision, attempts and status are distinct jobs. |
| 1161 | long-member | BeginListing(object,AttemptOwner) 52 | Record why it cannot be split | Record creation, completion and disposal as one listing identity/retry episode. |
| 571 | long-member | ChangeGenerationAsync<1>(PairConfig,Func<object, IStateStore, IReadOnl | Record why it cannot be split | Record drain, caller commit and guaranteed reopen as one configuration transition. |
| 347 | long-member | ExecuteInGenerationAsync<1>(string,RegistrationKind,bool,Func<object,  | Record why it cannot be split | Record admission, execution and retirement around one registration. |
| 736 | long-member | InstallCurrentAsync(IReadOnlyList<string>) 44 | Record why it cannot be split | Record resource replacement, membership and reopening as one captured-generation installation. |
| 1012 | long-member | RunPrerequisiteAsync(string,CancellationToken,RunLog?,Issue?) 88 | Record why it cannot be split | Record prerequisite admission, classification and deregistration as one attempt. B-13 adds the optional expected notice (81 to 88): its fence is re-read after each await, and a stale notice skips completion. |
| 261 | long-member | static Compose(string,Action<string>,PilotStateAuthority,EngineSetting | Record why it cannot be split | Record dependency wiring and failure cleanup as one object-graph ownership transfer. |
| 175 | long-member | static Compose(string,Action<string>,PilotStateAuthority,Mode,RunLog?, | Record why it cannot be split | Record settings, transport and diagnostics acquisition as one exception-safe composition boundary. |
| 12 | file-size | file-size | Split by job | Composition, generations, source supervision, attempts and status are distinct jobs. |

## src/SyncShark.Engine/Executor/FolderDeleteRunner.cs

<!-- spec-source: a5d902326b6c~086534efb974~39885aabcc87~ecaa16282f3d~0752fbc96667~fd27 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Submission/job observation, post-window settlement and recovery-stage lifecycle are distinct jobs. |
| 221 | long-member | CloseWindowAsync(FolderRemovalState,Progress,CancellationToken) 65 | Record why it cannot be split | One settlement job; rule 4: :218-219 "Mappings settle only after the post-window feed agrees with the frozen set; the cursor retires last, so a crash re-reads it..." states an ordering, not why the steps cannot be separate units (no shared state, lock, transaction or lifetime named) |
| 622 | long-member | ConcludeAsync(FolderRemovalState,Progress,WorkSelection?,CancellationT | Record why it cannot be split | Record mutually exclusive aggregate conclusions as one operation-transition decision. |
| 1002 | long-member | ObserveAsync(FolderSubmission,int,object,bool,CancellationToken) 41 | Record why it cannot be split | Record one observation’s durable accounting and retry/failure disposition. |
| 475 | long-member | PublishAsync(FolderRemovalState,FolderRemovalItem,Progress,Cancellatio | Record why it cannot be split | Record no-effect, occupied-target and uncertain-effect outcomes within one publication lifetime. |
| 740 | long-member | SendAndJoinAsync(IReadOnlyList<FolderRemovalEffect>,IAdmittedExecution | Record why it cannot be split | Record revalidation, arming, membership commitment and release as one submission/join handoff. |
| 910 | long-member | SubmitAsync(FolderSubmission,IReadOnlyList<FolderRemovalEffect>,Cancel | Record why it cannot be split | Record accepted, terminal, no-effect and unknown cases as one attempt-result translation. |
| 1 | file-size | file-size | Split by job | Submission observation, post-window settlement and recovery-stage protocols are distinct jobs. |

## src/SyncShark.Engine/Storage/ChangeTable.cs

<!-- spec-source: 6ed0fe2af760~35fabc6b7201~1fc1338c9454~c475f86d3a8e~f11bed9d2b35~3571 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | class-size | class-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody lifecycle. 0.3.79 adds the live-refusal answer (R6), notice content merging (S2) and own-write USN evidence (#90b): 1473 to 1543 lines. |
| 91 | long-member | AcquireJournalPage(string,JournalPosition?,JournalPosition,IReadOnlyLi | Record why it cannot be split | Record member/position fences, notices and checkpoint advancement as one atomic acquisition. |
| 562 | long-member | AdvanceBefore(SqliteTransaction,string,WorkSelection,StateChange) 46 | Record why it cannot be split | Record one selected work item’s replacement baseline derivation and propagation. |
| 671 | long-member | AttachIssueWork(WorkSelection,Issue,IEnumerable<(ChangeNotice Notice,  | Record why it cannot be split | Record retain/move/create alternatives as one atomic responsibility handoff. |
| 968 | long-member | EnsureIssueWork(SqliteTransaction,string,long,long,long,string,IssueSu | Record why it cannot be split | Record selected/source-less paths establishing the same exact issue-work custody. |
| 774 | long-member | ReadLocalDirty(string,long,long,string,string?,string?,string?) 76 | Record why it cannot be split | Record identity/current/recorded-path probes as one consistent dirty-evidence answer. #90b grew it 61 to 76 with the own-write USN fold (local Count) and the OwnWrite check. |
| 890 | long-member | ValidateLiveCoverage(WorkSelection,LiveCoverage,IReadOnlyList<LiveEffe | Record why it cannot be split | Record all footprint/later-evidence checks as one admitted-selection freshness proof. |
| 11 | file-size | file-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody persistence; 1483 to 1553 lines in 0.3.79 (see class-size). |

## src/SyncShark.Engine/Executor/ItemSync.cs

<!-- spec-source: f16bac130f2b~c862b2f99ef7~b73da7a6124a~4d5c768e31ea~5d1984bd910b~8ae6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size | Split by job | ItemSync: preparation PrepareAsync :521-553, freshness IsFreshAsync :565-615, admission/routing, folder replacement, skipped-notice upkeep :654-683. 0.3.79 added two more separable jobs: the live Local-write refusal RequireLiveWrite/LiveWriteRefused :362-372, a cross-class rule called from ActionExecutor, Parents, RenameRunner, Transfers, FolderSync and ScanPass that belongs in its own owner, and the concurrent-lane row-context selection _selectionRows/SelectRows :31-33. AdmitUploadAsync :115-136 is an extraction now shared with SelectionPass, not a copy. |
| 68 | long-member | DispatchPreparedAsync(SyncAction,CancellationToken,Action?,Action<Row, | Record why it cannot be split | Record promotion, stage release and stale fallback as one prepared-dispatch lifetime. |
| 565 | long-member | IsFreshAsync(Row,SyncAction,CancellationToken) 51 | Record why it cannot be split | Record action-specific checks as one freshness verifier returning reusable observed evidence. |
| 234 | long-member | RunRowAttemptAsync(Row,CancellationToken,Action?,Action<Row, SyncOutco | Record why it cannot be split | Record fast settlement and expected-retry capture within one attempt envelope. |
| 1 | file-size | file-size | Split by job | same jobs as row 25 (ItemSync file) |

## src/SyncShark.Engine/Executor/SelectionPass.cs

<!-- spec-source: 13289654f873~7052b604cdbe~ffb3aabf001a~6ba80791cc23~77f7a39d8a86~bad8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size | Split by job | Branch execution/settlement is separable from branch acquisition (walks, folder batches, unreadable release/replay). 0.3.79 grew execution with live upload admission :246-265 and branch progress reporting :58-68, :352-363, :374-375 (aggregated by Syncer); same split. |
| 366 | long-member | EnumerateBranchesAsync(WorkSelection,CancellationToken) 120 | Split by job | Branch discovery, folder dispatch and unreadable-release/replay are distinct protocols. |
| 515 | long-member | ListAsync(PendingChange,string,ListOptions,CancellationToken) 45 | Record why it cannot be split | Record the manual iterator lifetime needed for failure yield, cancellation and exactly-once disposal. |
| 72 | long-member | RunSelectionAsync(WorkSelection,CancellationToken) 201 | Split by job | Residual projection, branch observation/claiming and prepared-action dispatch are embedded jobs. |
| 129 | long-member | RunSelectionAsync(WorkSelection,CancellationToken)::ObserveProjectedAs | Record why it cannot be split | Record primary-observation reuse, dirty evidence and peer authority as one projected-row result. |
| 1 | file-size | file-size | Split by job | Same as row 28: execution separable from acquisition and unreadable release/replay |

## src/SyncShark.Core/SyncCliRegistry.cs

<!-- spec-source: 6b22cb6c59aa~1bd69c11e896~8ba8afca95d2~40af6a252968~cca02e5f4b31~83ef -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | class-size | class-size | Split by job | Command metadata, argument parsing, action dispatch and result presentation are distinct jobs. IsStateFree :734-739 (0.3.79) reads a row's Mode and belongs to the metadata job. |
| 1245 | long-member | static DispatchAsync(ISyncCliRuntime,SyncCliInvocation,TextWriter,Text | Split by job | Progress presentation/sanitization differs from selecting and invoking the command action. |
| 748 | long-member | static Parse(string[]) 411 -> 456 | Split by job | Generic tokenization differs from command-specific combination/value validation. |
| 1840 | long-member | static TryRender(object?,out string) 477 -> 499 | Split by job | Reclassified to split (R2-11): each result shape's formatting is an independent job, and the capability case already delegates to RenderCapabilities. |
| 15 | file-size | file-size | Split by job | Registry metadata, parser, dispatcher and presenter implementations are distinct jobs. |

## src/SyncShark.Engine/Dropbox/DropboxEntries.cs

<!-- spec-source: 4a2ccca55e7f~62f3f77e8b5a~47de4ab79d39~05381257d579~01e2e651bffc~68b0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Lexical validation, field projection and Dropbox metadata interpretation are distinct jobs. |
| 113 | long-member | Read(ReadOnlySpan<byte>,bool,ReadOnlySpan<string>,bool,CancellationTok | Record why it cannot be split | Record one token pump sharing incremental consumption, readiness and final-block state. |
| 238 | long-member | StringByte(byte,ReadOnlySpan<string>) 92 | Record why it cannot be split | Record escape, surrogate and UTF-8 branches as transitions of one incremental string decoder. |
| 156 | long-member | StructuralByte(byte,ReadOnlySpan<string>,bool) 66 | Record why it cannot be split | Record punctuation, container closure and lexeme start as one grammar-frame transition. |
| 7 | file-size | file-size | Split by job | Incremental JSON lexing, selected-field projection and metadata interpretation are distinct jobs. |

## src/SyncShark.Engine/Executor/ScanPass.cs

<!-- spec-source: ea7cddb747f4~18f7deb21bd6~4ceccfa5e6c2~ad8a7b9acbc4~8df11e3a9318~4598 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | ScanPass runs four inline stage implementations plus separable issue reconciliation and cross-pair stale-stage cleanup; header line 6 only describes shared state, so not one job. 0.3.79: the live Local-write refusal containment :18-19, :49-50, :233-240 joins row/result production, and the row admission capacity RowCapacity/TotalCapacity :21-26 is now static and also read by Syncer; it moves with the stage split. |
| 363 | long-member | ReconcileIssues(PrefixSet,IReadOnlyList<(string Path, TextKey Cause, I | Split by job | Obsolete-issue retirement differs from reusable conflict-hash proof collection; preserve the single traversal. |
| 53 | long-member | RunRowsAsync(ListedSkips,Guid,bool,Func<string, bool>?,IReadOnlySet<lo | Split by job | Cleanup, exclusion implementation, rename barriers and row/result production are distinct stage jobs. |
| 1 | file-size | file-size | Split by job | Same multi-job type as row 27; header :6 describes shared state but the file still holds separable stage, reconciliation and cleanup jobs |

## src/SyncShark.Engine/Run/RunLog.cs

<!-- spec-source: 43d40f071903~db7c79e9a9b6~9054d08e5e95~64b698ff6016~18d2a8f0d5fc~3002 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size | Split by job | Canonical codec differs from mutable accounting, flushing and subscriber delivery. S2 adds active-branch tracking (_activeBranches, _branchOrder) to progress reduction: 665 to 685 lines. |
| 207 | long-member | Event(RunEvent) 87 | Split by job | Progress reduction, canonical accounting and ordered human delivery are distinct jobs. S2 grew the progress-reduction part (branch tracking) 70 to 87. |
| 80 | long-member | ReportProgress(string?) 43 | Record why it cannot be split | Record one quiet observation’s matching diagnostic and sequenced human snapshots. |
| 13 | file-size | file-size | Split by job | Canonical codec differs from mutable accounting, flush lifetime and delivery; 677 to 697 lines. |

## src/SyncShark.Core/NtfsUsnJournal.cs

<!-- spec-source: 1f797629144c~610fdb36ad40~bb401da22c86~1c8f1e347d29~b48c11003f11~bf74 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | class-size | class-size | Split by job | NtfsUsnJournal.File.cs:11-154 per-file USN evidence/gap-handle opening is a job distinct from volume journal reading; partials do not answer 1.3 |
| 308 | long-member | static TryDeviceIoControlPage(SafeFileHandle,uint,byte[],byte[],out in | Reason recorded | The comment at :306 binds pinned buffers, completion, cancellation drain and release to one overlapped request; the event low bit keeps manual completion out of a FileStream completion port. |
| 15 | file-size | file-size | Split by job | File holds journal-state query, page read with record walk/parse, and native overlapped I/O plus P/Invoke; header :10-14 descriptive |

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

<!-- spec-source: 716b43cdea73~b712089f902d~8a159e645d99~d1f78e7af38d~b2f740aea3b1~50c0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. |
| 27 | long-member | ReadAll(string,ListOptions,ListingReadContext,CancellationToken) 115 | Split by job | Retry-owned page acquisition/cleanup differs from traversal spelling, filtering and progress. |
| 7 | file-size | file-size | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. |

## src/SyncShark.Engine/Dropbox/DropboxWriter.cs

<!-- spec-source: 64366569256a~23d24a1ae134~a11a0a70c43f~c58481f636ca~d451625c7a4c~cf84 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | class-size | class-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 27 | long-member | StageAsync(string,Stream,Entry,CancellationToken) 42 | Record why it cannot be split | Record one verified stage sharing hash, offset and terminal session result across upload alternatives. |
| 8 | file-size | file-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 256 | long-member | SettleCohort(List<PendingPublish>,IReadOnlyList<(Entry?Entry, Exception?Error, bool KnownNoEffect, bool WriteThrottled)>,RequestAttempt) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 68. |

## src/SyncShark.Engine/Run/EngineOperations.cs

<!-- spec-source: 211749994887~a11cfafec4e3~b132c6bb52d2~d73abcb6f2b7~bde0b64256ca~cb2d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size | Split by job | Action facade also implements configuration mutation, recovery workflows and historical logs. B-13 adds the reviewed-notice token check and expected-notice routing: 582 to 593 lines. |
| 239 | long-member | ResolveAsync(string,long?,ResolutionChoice,string?,FirstRunPolicy?,boo | Split by job | Configuration/root choices, bulk retry aggregation and individual recovery dispatch are distinct implementations. B-13 adds the reviewed parameter and its check (113 to 116). |
| 12 | file-size | file-size | Split by job | Action facade embeds configuration, resolution/recovery and historical-log implementations. |

## src/SyncShark.Engine/Executor/ActionExecutor.cs

<!-- spec-source: 16b3a6e4d4c8~f233c6b316dc~b3ee8dbb67f1~0ae3c1caba2b~389b3c72d3bf~b60b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 362 | long-member | CopyAsync(SyncAction,bool,CancellationToken,Func<ValueTask>?,(Side Sid | Record why it cannot be split | Record one selected copy outcome preserving each publication/settlement boundary. |
| 212 | long-member | FailCoreAsync(Row,SyncAction?,Exception,CancellationToken,Action<Issue | Record why it cannot be split | Record one failed-item mapper/settler, including occupant enrichment and retry disposition. |
| 1 | file-size | file-size | Split by job | Header itself names two jobs (ExecuteAsync table and item-error mapping); readability rules and event emission are further separable jobs |
| 260 | long-member | ApplyAsync(SyncAction,KeepBothLabels?,bool,CancellationToken,Func<ValueTask>?,(Side Side, string Path, Entry?Entry)?,Action<Issue>?,Row?,Guid?,ItemRetryCandidate?,WorkSelection?) | Split by job | Reclassified to split (R2-13): per-kind change construction and the shared decorate/settle/emit tail are separable; the copy and replace turn already delegates to CopyAsync. |
| 7 | class-size | class-size | Split by job | The file holds only this type, so the file-row jobs apply: the ExecuteAsync table, item-error mapping, readability rules and event emission. Replaces the earlier class FALSE-POSITIVE (group v1). 0.3.79 only threads ownedFolderEffect :13-21 and the LocalPublished position :420-447. |

## src/SyncShark.Engine/Executor/RenameRunner.cs

<!-- spec-source: 6abb963c502c~a4fe94396829~6844d5464762~e75be08bbd95~c8c5511a0b83~b247 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 84 | long-member | RunCurrentAsync(WorkSelection?,IReadOnlyList<RenameCandidate>,SyncedEn | Record why it cannot be split | Record observation, Planner choice and outcome mapping as one current rename attempt. |
| 33 | long-member | RunUnrecordedAsync(IAdmittedExecution?,WorkSelection?,IReadOnlyList<Re | Record why it cannot be split | Record footprint refresh/reacquisition and binding discovery as one rename preparation loop. |
| 1 | file-size | file-size | Split by job | Admission/binding resolution, observation+decision, results-table application and failure mapping are separable; header :5 lists them as three steps |
| 216 | long-member | ApplyAsync(WorkSelection?,RenameDecision,IReadOnlyList<RenameCandidate>,SyncedEntry,Observations,Action<Issue>?,Guid,ItemRetryCandidate?,CancellationToken) | Verify existing reason (R2) | The constraint comment is now directly above the unit (:214-215); record REASON-RECORDED; source row 96. |

## src/SyncShark.Engine/Run/PairSession.cs

<!-- spec-source: 68020ecc0437~8b5ab894ad5b~c65b456c6464~3de16bcc8d2d~4211f4106b84~a29d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | long-member | PairSession(IStateStore,PairConfig,EngineSettings,PathScope,ISide,ISid | Record why it cannot be split | Record one session’s dependency graph and invariant wiring. |
| 87 | long-member | static ComposeGeneration(IStateStore,PairConfig,IReadOnlyList<PairConf | Record why it cannot be split | Record account/scope/source resources and transport cleanup as one generation composition. |
| 137 | long-member | static Open(IStateStore,PairConfig,EngineSettings,HttpClient,bool,RunL | Split by job | Open embeds a transient-failure retry/backoff executor and a disk-space reservation with generation hold, behaviour beyond composition; doc comment :136 descriptive |

## src/SyncShark.Engine/Storage/IssueTable.cs

<!-- spec-source: 0d7abb02e513~9dfdf2507858~ee973f5221fe~6c0cdb2df5f4~41fadabf731b~e864 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | IssueTable is the file’s only type, so it has the file-size jobs; the earlier class FALSE-POSITIVE contradicted this register and is not carried forward (851 to 859 lines). |
| 569 | long-member | static MergeSkip(SqliteConnection,SqliteTransaction,string,long,string | Record why it cannot be split | Record both side members and their single path-level issue as one reconciliation. |
| 16 | long-member | static Raise(SqliteConnection,SqliteTransaction,string,IssueKind,TextK | Record why it cannot be split | Record continuation/protection/refresh/replacement as one precedence-ordered raise transition. |
| 7 | file-size | file-size | Split by job | Raise/close lifecycle, paged listing snapshots, skip-member reconciliation, unreadable queries, upgrade backfill, retry-episode lifecycle and invalid-job projection are distinct jobs; header :6 is a grouping. B-13 adds exact-notice SetIgnored to the raise/close lifecycle (857 to 865). |

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

<!-- spec-source: 6fb3a629c51b~5e617c3ea774~56fba7ddf54f~2893de620d85~06f43035ecdc~fc72 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 207 | class-size | class-size | Split by job | Command execution, human/help rendering and IPC response-budget fitting are distinct jobs. |
| 293 | long-member | static ExecuteAsync(string[],TextWriter,TextWriter,Func<ISyncCliRuntim | Split by job | SyncCli.cs:312-333 state-free help/version/capabilities answers are a separate job from the runtime invocation/deadline/disposal lifetime :336-442. F2 resolved at 584016769: setup now delegates in one line to SetupCli :356-357 and the state-free test asks SyncCliRegistry.IsStateFree :455; 150 lines, within the reviewed size. |
| 1 | file-size | file-size | Split by job | 40 top-level types in one file (section 1.3, one type per file). F2 resolved: 909 lines, below the 916 baseline. |

## src/SyncShark.Core/SyncLocalFileAuthority.cs

<!-- spec-source: e30ed6f5a1d9~c0d6016185b9~b50592fccfe5~3096a1d3e37f~57d712089626~af70 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | Held-file authority and ACL-recovery envelope encoding/protection remain distinct jobs. Async opening, the staging default-buffer choice (:47-54, :117-120) and full-path rename extend the held-file job; both rename forms call SetRenameInformation (:383-421), not separate native implementations. |
| 9 | file-size | file-size | Split by job | Held-object opening/identity/rename/disposal (:49-462) differs from ACL recovery-envelope encoding/protection (:464-736); the guarded stream (:738-814) belongs to held-object authority. New async/rename/buffer members do not close this split debt. |

## src/SyncShark.Core/V2/Adapters/AutoSyncProcess.cs

<!-- spec-source: b44b83802f48~077098ddfcbe~b3dca97c0f3d~e53f468bac25~4abf1cfe5128~cd0c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 16 | class-size | class-size | Split by job | Startup registration/migration differs from detached process launch, readiness and native lifetime. |
| 16 | file-size | file-size | Split by job | Startup registration differs from native process launch/readiness lifetime. |

## src/SyncShark.Core/V2/FailureRetryOwner.cs

<!-- spec-source: 3498079cbbf7~8245694cc256~b08ba630baf2~0ef76d499e25~229431cf81a1~63b0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | class-size | class-size | Split by job | Retry state/scheduling and notice wording/construction are distinct jobs; retain shared retry state. |
| 90 | file-size | file-size | Split by job | Retry state/scheduling differs from user-facing notice construction. |

## src/SyncShark.Core/WindowsCloudFiles.cs

<!-- spec-source: ee16130cf87d~a6c2080b7da5~cec7be1d2c17~04663626a59c~5300c0d10736~118e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 134 | class-size | class-size | Split by job | Root registration, placeholder residency and hydration admission/transfer are distinct jobs. |
| 134 | file-size | file-size | Split by job | Root registration, placeholders, hydration and identity/status contracts are distinct jobs. |

## src/SyncShark.Engine/Local/LocalSide.cs

<!-- spec-source: eaf51d9483a6~7571d52cda06~c23607191067~72496bc84d69~82c6c3b3cab8~df70 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 14 | class-size | class-size | Split by job | Checked IO, publication/removal, recovery custody and identity/drive observation remain distinct jobs. Held-stage lifetime and publication-boundary helpers extend publication (:74-315); StageAsync keeps the FileStream default buffer through the existing authority (:88-89), so the #90b growth adds no new job. |
| 14 | file-size | file-size | Split by job | Checked IO (:27-182), pending/publication effects (:184-482), recovery custody (:610-795) and identity/drive observation (:549-607, :852-886) remain distinct jobs; the publication boundary (:268-315) belongs to publication effects. |

## src/SyncShark.Engine/Storage/StateEncoding.cs

<!-- spec-source: 1d8b11898acb~7a13d6b5c4d9~9190c7472b8c~8efc01a9b85b~3e7db4778081~e85a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 16 | class-size | class-size | Split by job | Compressed-history codec, strict JSON validation and SQLite row projection are distinct jobs. |
| 16 | file-size | file-size | Split by job | Compressed history, strict JSON validation and typed SQLite projection are distinct encoding jobs. |

## src/SyncShark.Engine/Dropbox/AdaptiveLimiter.cs

<!-- spec-source: bbd1d87eae34~181bbd76856d~7d73a2b6328c~5f6fdf1d2107~c6f5f1154478~ca2b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 172 | long-member | AdmitUploadAsync((string AccountId, string RouteName, string SourceIde | Record why it cannot be split | Record one queue-node lifetime through provider wait, permit transfer or cancellation removal. |
| 118 | long-member | EnterWorkAsync(bool,CancellationToken) 44 | Record why it cannot be split | Record one FIFO admission lifetime, including direct-upload revocation and cancellation. |
| 5 | file-size | file-size | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 190. |

## src/SyncShark.Engine/Dropbox/DropboxApi.cs

<!-- spec-source: b54b808eec2d~ca5760ddf33b~f377978bb192~c5f3125f8338~6592191ce87a~53ba -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 213 | long-member | ExecuteAsync<1>(string,object,Stream?,ContentBlockHasher?,DropboxReply | Record why it cannot be split | Record replay eligibility, request episode and terminal completion as one retry lifecycle. |
| 11 | file-size | file-size | Record why it cannot be split | Record one request/body/cleanup/terminal-publication HTTP exchange owner. |
| 270 | long-member | ConsumeAsync<1>(DropboxRoute,string,Stream?,ContentBlockHasher?,DropboxReply<T>,RequestAttempt,bool,CancellationToken,bool,long?,string) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 61. |

## src/SyncShark.Engine/Executor/FirstRunFolders.cs

<!-- spec-source: 46e42ae5139f~a7623a39b2b4~f0fde998a903~6867f4f321d3~d8dcfa6311e4~9210 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 67 | long-member | FinalizeFirstRunFolderAsync(Row,bool,CancellationToken,Action<Row, Syn | Split by job | Recursive content-evidence acquisition/classification is embedded inside admitted execution. |
| 8 | long-member | FinalizeFoldersAsync(List<(Row Folder, int Depth)>,PrefixSet,HashSet<s | Record why it cannot be split | Record deepest-first settlement and upward failure propagation as one dependency barrier. |

## src/SyncShark.Engine/Executor/FolderSync.cs

<!-- spec-source: 73db8459ded0~e848b1041bd6~f2a2db1088be~017fa29c7718~091eb1a6f18f~af4b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 279 | long-member | RunLiveFolderPlanAsync(Row,Action<Row, Issue>,WorkSelection,LiveCovera | Record why it cannot be split | Record the plan operation’s deliberate inside/outside-admission sequencing. |

## src/SyncShark.Engine/Planner/Decide.cs

<!-- spec-source: c1e7fd58607f~c3d6d3fe328f~ea4d692ff36f~3ebf7f996795~946f9324d0d5~dc87 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 71 | long-member | static NoRecord(Row,FirstRunPolicy,DateTimeOffset) 58 | Record why it cannot be split | Record Table A’s ordered no-record decision and shared precedence facts. |
| 5 | file-size | file-size | Record why it cannot be split | Record ordinary row tables/shared predicates as one policy owner, with folder/resolution policies delegated. |

## src/SyncShark.Engine/Run/HumanLog.cs

<!-- spec-source: 9f4f58783287~69c8151d4445~8cde0f8cfdd9~90e82c9b09f7~c4d26076bd6d~9bc2 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 321 | long-member | AppendOutcomes(StringBuilder,IReadOnlyDictionary<string, int>?,IReadOn | Split by job | Reclassified to split (R2-11): effect-row formatting and the notice/measurement footer use distinct inputs and are separable. |
| 9 | file-size | file-size | Record why it cannot be split | Record one finite-state human projection shared by live and historical input. |

## src/SyncShark.Engine/Run/Monitor.cs

<!-- spec-source: 5d690aea3364~ad5cbb2022fd~97b634b353db~ffec37b50e41~66938a97cfaf~a7da -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 179 | long-member | RunAsync(CancellationToken) 61 | Record why it cannot be split | Record deadline selection, arming, dispatch and wake consumption as one scheduling loop. |
| 6 | file-size | file-size | Record why it cannot be split | Record one earliest-deadline scheduling and shutdown authority for all opportunity kinds. |

## src/SyncShark.Engine/Run/PairScan.cs

<!-- spec-source: 673f718e3c10~38a53aeaeac8~039826050853~3e055ee9037b~8b697cadae98~26b9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 35 | long-member | RunAsync(TaskSet,RunLog,bool,long,CancellationToken,string?,Action<str | Record why it cannot be split | Record both producers’ shared cancellation/join and incomplete-result bracket. |
| 115 | long-member | static ListAsync(ISide,IScanSink,List<(string Path, TextKey Cause, Iss | Record why it cannot be split | Record ingestion, skip capture and complete/abort cleanup as one streamed transfer. |

## src/SyncShark.Engine/Storage/StateCommitter.cs

<!-- spec-source: 12cf38c837e7~7a5103b4a06c~e8dde34372fd~837c2fcbcd33~9f87a534aa6a~b31e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 141 | long-member | static ApplyRename(SqliteConnection,SqliteTransaction,string,RenameApp | Record why it cannot be split | The new :140 comment names atomic row/descendant rebase and scan moves. Shared transaction alone does not preclude job extraction, since helpers already accept that transaction; retain the reason/split obligation. The insert branch now supplies no publication boundary. |
| 12 | long-member | static Commit(SqliteConnection,SqliteTransaction,ChangeTable,string,St | Reason recorded | The :9-11 comment identifies one ordered interpreter whose later stages read earlier writes. Publication-boundary selection (:52-59) applies only to a matching nonfolder identity; Insert and narrow updates share RecordArguments (:266-274). |

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

<!-- spec-source: e86e137657ab~7fbb59eaa9f9~2777488a1b09~ee4e8ef62d1c~e505e3a4abb1~51ea -->

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

<!-- spec-source: 850ec9e75058~8883e33aabf5~bb67cd38cc24~21ab5df65b0d~376e7ec2ba1f~f94a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 75 | long-member | ServeAsync(PipeStream,CancellationToken) 42 | Record why it cannot be split | Record trusted intake and irreversible Accepted-to-Exit handoff as one connection lifetime. |
| 33 | repeated-procedure | ListenAsync(NamedPipeServerStream,Func<NamedPipeServerStream>,int,CancellationToken) 37 | Extract shared owner | The bounded accept/replace/prune/shed/join loop :43-70 repeats AutoSyncBackgroundHost.ListenAsync :108-145 (pre-existing; #46 moved this copy here from the app entry). One LocalPipePeer listener beside AcceptAsync should own it; each host passes only its serve delegate and bound. |

## src/SyncShark.Engine/Executor/AdmittedExecution.cs

<!-- spec-source: 2d7a4d8f856a~ddb42897cf82~e996b9231802~96bce1d6872b~5fe0f744cae2~a7f8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static AcquireAsync(IReadOnlyList<ExecutionRequest>,Func<bool>,Cancell | Record why it cannot be split | Record canonical acquisition and reverse unwind as one acquisition transaction. |

## src/SyncShark.Engine/Executor/ExclusionRunner.cs

<!-- spec-source: 24d04870484f~b939d6b58017~34e61f03a012~6d7ba2475954~1d6a6f502242~3b6a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | ApplyAsync(Exclusion,IReadOnlySet<long>?,CancellationToken) 136 | Split by job | Root preflight/protection checks, per-item preservation copy loop and root retirement are separable; doc comment :10 describes behaviour only |

## src/SyncShark.Engine/Executor/FolderPlanBuilder.cs

<!-- spec-source: 95854342416f~b67a58ef7711~d0e428cbacaa~0aa59a73a9fe~c49d3ccb708f~316e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 24 | long-member | BuildCoreAsync(Row,Side,IReadOnlySet<string>,bool,bool,Exclusion?,Fold | Record why it cannot be split | Record capture, delegated classification, branch choice and cleanup as one disposable-plan construction. |

## src/SyncShark.Engine/Executor/KeepBothRunner.cs

<!-- spec-source: 97f0e34e3288~aa3f771feb3c~e65b5270b182~f57a8958cfe4~698265c82c20~d5f6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 22 | long-member | RunAsync(Row,KeepBothLabels,CancellationToken) 48 | Record why it cannot be split | Record selected keep-both legs and their combined binding change as one result. |

## src/SyncShark.Engine/Executor/Parents.cs

<!-- spec-source: 4221bdacfe9e~db904f11125b~04be31ec7237~06a8b36e18f7~ecad47931e62~1dc2 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 40 | long-member | EnsureAsync(Side,string,Entry?,CancellationToken) 55 | Record why it cannot be split | Record ancestor dependency ordering and coalesced waiter release as one creation protocol. |

## src/SyncShark.Engine/Executor/Recovery.cs

<!-- spec-source: eb0ba73200b5~4fb5e66bbf18~579dc77b89b7~d25ad09f3471~2e4ec637af6c~133a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 85 | long-member | ResolveFolderChoiceAsync(FolderRemovalState,RecoveryChoice,long?,long? | Record why it cannot be split | Record exact-target validation and selected recovery action within one admitted choice. |

## src/SyncShark.Engine/Executor/ResolutionRunner.cs

<!-- spec-source: a0b6ed66778d~78d85257282a~41203e9bd5ed~4db1d8d11f19~fb28e7d545e8~9bc3 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 31 | long-member | ResolveAsync(Issue,ResolutionChoice,string?,IReadOnlyList<SyncIgnoreRu | Split by job | ResolutionRunner.cs:44-63 Retry branch is a separate procedure; Dismiss/KeepFolder already delegate |

## src/SyncShark.Engine/Executor/Transfers.cs

<!-- spec-source: 26957c2c4cf1~18fd659aff9e~3cd82f3b5342~da923cd829af~258901e29e55~b47a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 98 | long-member | StageAsync(Side,Entry,string,CancellationToken) 50 | Record why it cannot be split | Record source views, reservation release, attempt completion and transfer within one stage lifetime. |

## src/SyncShark.Engine/IssuePolicy.cs

<!-- spec-source: 2bc862e80f8e~26f611f08408~21fe3de4d7bb~a53dbf17b525~da20d522718c~131e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 180 | long-member | static Obsolete(Issue,Func<Side, string, Entry?>,PrefixSet,PrefixSet,P | Record why it cannot be split | Record local predicates over one issue/scope evidence set and precedence. |

## src/SyncShark.Engine/Local/DownloadSpace.cs

<!-- spec-source: 25dc44b1832f~18e5c86f3462~526402476aac~cbabcd1fe6a6~62dd65d64bae~9f01 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 26 | long-member | ReserveAsync(string,string,long,CancellationToken) 44 | Record why it cannot be split | Record volume observation, free-space admission and lease creation as one reservation transaction. |

## src/SyncShark.Engine/Local/LocalListing.cs

<!-- spec-source: 31c71fa4c35f~3af900a416f6~8c499a2b46c0~1bef70c71c2c~8d795805b4d9~ffe5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 14 | long-member | Walk(string,ListOptions,CancellationToken) 63 | Record why it cannot be split | Record enumerator custody through observations, close failure and unreadable coverage. |

## src/SyncShark.Engine/Local/LogFiles.cs

<!-- spec-source: 4f15781c3c4a~e4de0f589e2d~6b63eff189fd~265b2bf95bb6~df6e26ba6c97~ce37 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | static Read(string,Action<string, bool>,CancellationToken,out bool) 57 | Split by job | Filesystem open/read/error ownership differs from incremental BOM/newline record framing. |

## src/SyncShark.Engine/Local/UsnPaths.cs

<!-- spec-source: 01058426933b~a87b0aaf3d4f~413ab58ad671~ebfd950f88c2~3f43c352bee2~4614 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 107 | long-member | ResolveOne(ulong,ulong,uint,FileAttributes,CancellationToken) 43 | Reason recorded | The :106 comment binds one event's cache invalidation, identity/fallback resolution and fresh notice observation in order. The added USN is carried into that notice; path resolution, saved lookup and classification remain delegated to their existing helpers. |

## src/SyncShark.Engine/Planner/FolderDeletes.cs

<!-- spec-source: 486795467295~e1208ce42826~e1d3655b2853~f51c2982432c~3efc08cdb8db~ff60 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 36 | long-member | static FirstRun(Row,bool?,bool,bool) 44 | Record why it cannot be split | Record one folder decision over the same completed-content and mapped-target evidence. |

## src/SyncShark.Engine/Run/FolderBatch.cs

<!-- spec-source: e10909bbddc7~4479e5933118~de3d081c8711~91de2b6f794e~7b184b5a644f~86a0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static RunAsync(ExecutorContext,SyncPorts,ISide,RunLog,WorkSelection?, | Record why it cannot be split | Record the scoped scan reservation through listing, execution, joining and cleanup. |

## src/SyncShark.Engine/Run/Repair.cs

<!-- spec-source: f0b5cc1f9c39~ffb8d1322306~334d55f54c81~197ff4e40f81~ffd3cda2ff86~604c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static RunAsync(PairSession,CancellationToken,IReadOnlySet<long>?) 182 | Split by job | Scan sink/resource setup and summary publication with log pruning and log-failed marking are separable from orchestration; doc comments :7,:10 describe behaviour. B-21 adds the restore progress callback (179 to 182). |

## src/SyncShark.Engine/SettingsParser.cs

<!-- spec-source: 264dee9b1f17~b49b295a853c~4c51edafacb4~41b7a65f044f~bc92533b92d4~7e3b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 20 | long-member | static Parse(JsonElement,JsonElement?) 49 | Split by job | Effective-settings construction differs from backoff representability/error-attribution validation. |

## src/SyncShark.Engine/Storage/IssueFacts.cs

<!-- spec-source: ed4276fe24fd~1669357c1b16~a8ae01aaa4c0~0691b98707cc~14e634216aad~daac -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 17 | long-member | Replace(long,Issue?) 42 | Record why it cannot be split | Record remove-old/add-new indexes as one locked projection replacement. |

## src/SyncShark.Engine/Storage/ScanEntries.cs

<!-- spec-source: d1d58c65e907~f9e9bfb4ffe8~0d99e57c179b~a8d7c209f302~da891fd0c034~36eb -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 56 | long-member | Apply(SqliteTransaction,Listed,string?) 41 | Record why it cannot be split | Record tombstone, override and type-replacement cases as one ordered scan-event application. |

## src/SyncShark.Engine/Storage/ScanReader.cs

<!-- spec-source: 80d65037475c~585fba1383f3~87e7a37cbf79~bb80166a17c3~a25d937b08b5~645f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | long-member | static Rows(SqliteConnection,string,Func<Row, RowContext>,string?,byte | Record why it cannot be split | Record candidate grouping in one short snapshot closed before yielding caller work. |

## src/SyncShark.Core/DropboxProvider.cs

<!-- spec-source: e35d9c6d18ef~248689840641~b9cf79b01084~d34a31da7aec~81aabccd924e~e5a6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | file-size | file-size | Split by job | Connection/configuration, inventory/evidence, transfers/recovery and HTTP handling are distinct jobs. |

## src/SyncShark.Core/SyncCliViews.cs

<!-- spec-source: 4ec57d30e7f1~7d0489df2866~dfc3c136f995~6eae80368f65~975bcef6a2c6~0718 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 131 | file-type-name | file-type-name | One type per file / name matches type | Top-level `SyncCliNoticePreviewData` does not match its file. |

## src/SyncShark.Engine/Dropbox/DropboxErrors.cs

<!-- spec-source: 1aac7e9edb4a~6236136e9432~28298085c753~a8d3a34dbc8c~5dc78f10f474~85dc -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | file-size | file-size | Record why it cannot be split | Record shared tag-based refusals, interpretation and safe diagnostics as one error classifier. |

## src/SyncShark.Engine/Dropbox/DropboxFeed.cs

<!-- spec-source: f651a825b511~a07e2170eb78~170a09742b76~1b6b6bff8892~6f975adb3bc8~4fa5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Record why it cannot be split | Record installation, continuation, longpoll and reconnect as one cursor/handoff lifecycle. |

## src/SyncShark.Engine/Dropbox/DropboxReply.cs

<!-- spec-source: ec501a7920b6~a6752380e217~bf21e63a263c~ba1746afc576~4371a14d69d7~ced1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 312 | file-size | file-size | One type per file / name matches type | DropboxReply.cs holds two top-level types: DropboxReply<T> :10-309 and static DropboxReply :312-659; checker merges them by name |

## src/SyncShark.Engine/IStateStore.cs

<!-- spec-source: 76520675b10e~7bfc234cabb4~d7f70e3b9fc1~962970956ae4~939c94881ba5~ed34 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Split by job | IStateStore.cs:5-334 spans the same pair/work/pending/retry/folder-removal groups both reviews require StateStore to split along; owner decision 30 (docs/design/owner-decisions.md:101) is the interface-split precedent. 0.3.79 adds ReadLiveRefusal and expected-notice parameters (324 to 334). |

## src/SyncShark.Engine/Local/LocalBin.cs

<!-- spec-source: 6ff3c5e738d2~bfb97b2494ac~52e8faccfe61~3146022574bb~399d4c10cfa1~4a68 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | file-size | file-size | Reason recorded | The :7 comment now names the constraint: one held handle spans proof verification, Shell removal and receipt classification. HRESULT translation (:280-288) delegates to LocalErrors.Cause; the copied exception classifier has been removed. |

## src/SyncShark.Engine/PathNames.cs

<!-- spec-source: 8e8aaebf7c14~f5866e8f92b1~2067bfcfd2f6~43a1bf455500~18f8626cc0c7~a0bf -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | file-size | file-size | Record why it cannot be split | Record one frozen path-identity/key facade whose derived operations reuse canonical comparison. |

## src/SyncShark.Engine/Run/PairSafety.cs

<!-- spec-source: a8aaa5699529~7b353cabd755~a5ba44ee66f6~faf7818bd0a9~3181cedf01a0~1ba1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | Same jobs as file-size; the class passes 300 lines (310) in 0.3.79. |
| 9 | file-size | file-size | Split by job | Root identity comparison/refusal is separate from execution entry admission (lock footprints, live/recovery/selected validation); header :8 names a grouping only. B-13 adds a third job, the reviewed-notice token (IssueFingerprint/ForReview :284-297), whose natural home is Issue beside Issue.Matches (306 to 318). |

## src/SyncShark.Engine/Storage/StateSchema.cs

<!-- spec-source: 94df0f09a842~6491c36311e7~03f03a03672f~87781dccb583~69c36eda5bdb~7261 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | file-size | file-size | Record why it cannot be split | Record one declarative SQL/schema catalogue, without execution or transaction policy (reason :3). #90b adds schema version 11 and the published-USN columns and statements; B-21 adds the batched deleted-name insert (1433 to 1450). |

## src/SyncShark.Engine/TextKey.cs

<!-- spec-source: 8e97812d9e09~a8413d9a3714~7ca5008b5406~e1a9009a8bdd~7c7bc2249fa0~8519 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 3 | file-size | file-size | Record why it cannot be split | Record one strings-catalog identifier enum; arbitrary splitting would fragment that contract. |

## src/SyncShark.Engine/Executor/TaskSet.cs

<!-- spec-source: 1899bffdf5b2~50016fd4438b~d654994762e8~00bb06dcc75d~a08830b5c626~f4e5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 40 | long-member | Add(Func<Action, Task>) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 106. |

## src/SyncShark.Engine/Local/JournalSource.cs

<!-- spec-source: 617a67b78e7c~9531eb649e2b~a74134f3e0b8~1a46b4378401~97ab37166180~6eab -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 223 | long-member | CaptureFiniteAsync(IReadOnlyList<Member>?,CancellationToken,Action?) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 110. |
| 333 | long-member | ReadPageAsync(bool,CancellationToken,List<Member>?,ulong?) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 111. |
| 479 | long-member | TryPublish(JournalPosition,IReadOnlyList<Member>,IEnumerable<(string PairId, ChangeNotice Notice)>,IReadOnlySet<string>?,List<Member>?,out bool) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 112. |
| 3 | file-size | file-size | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 208. |

## src/SyncShark.Engine/Storage/PairImport.cs

<!-- spec-source: ee5bef18fba8~48b2a667480c~e9335a027cfc~9c9b807002a8~75b038290452~1e87 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 19 | long-member | static Open(string,string,EngineSettings,IStateStore) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 147. |

## src/SyncShark.Core/ProviderActivityStream.cs

<!-- spec-source: 734a40e14a74~a37634f408c3~6e1b5c7e4c17~d965404deac9~6d3e9f8a2108~5f51 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 176. |

## src/SyncShark.Engine/Executor/RowContexts.cs

<!-- spec-source: 45683a39e928~0829e2579e5f~6e3e52f3861d~15b874bea7d1~06cc2730405e~f7b0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 1 | file-size | file-size | Split by job | Per-execution row-context projection, the run-local hold/Retry-authority state machine and completed-acquisition first-run deletion/inventory projection are distinct jobs with separate consumers and output types; split into RowContexts, ExecutionHolds and FirstRunProjection, calling IssuePolicy for hold/first-run policy and spent-key queries through IStateStore. ExecutionHolds and FirstRunProjection share the existing gate by reference; preserve the current critical-section span and store-call order, without replacing Build's protected combination with separately locked snapshots. FirstRunProjection also owns firstRun and deletedName alongside acquisition validity and the template; eligibility is checked before the template read. |

## src/SyncShark.Core/V2/Adapters/AutoSyncBackgroundHost.cs

<!-- spec-source: e4ada547ac88~c3da6019bc2a~1fc7c7313bc3~5ad556a516e3~f0b2d93abdf0~733f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 102 | long-member | ListenAsync(NamedPipeServerStream) 41 -> 45 | Split by job | The bounded accept/replace/prune/shed loop :108-129 repeats EngineHostServer.ListenAsync :43-70 and belongs to one LocalPipePeer listener; host shutdown, connection drain and authority release :131-145 stay here. The 0.3.79 growth only adopts the shared LocalPipePeer.AcceptAsync owner :107-113. |

## src/SyncShark.Core/SetupUpdates.cs

<!-- spec-source: 6c2bd264ed71~0cf0d2985905~9fc63ff8d8df~d8f99010a2d9~1f3f84a5325f~fc40 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 96 | long-member | static VerifySignatureAsync(string,CancellationToken) 47 | Split by job (B-57, 0.3.80) | The pinned-identity, chain and timestamp checks :113-141 are the same rule shape as SyncSharkUpdaterSignature.Verify (tools/SyncShark.UpdaterSignature.cs:40-71) but weaker: no exactly-one-signer check, no RFC3161 binding to the signature, no future-timestamp refusal and no exclusive/critical timestamp EKU. Calling that owner today would break the contract: setup is package-signed (RELEASING.md:6, :28) and the owner ties the identity role to the container format (:40-53, :143-148). B-57 separates role from format in the one owner; setup then calls it with role=package and this unit keeps only the PowerShell evidence read :98-112. |

## src/SyncShark.Core/SyncPublicUpdates.cs

<!-- spec-source: 464458dadb6c~25a9749481f4~43c1f0881469~3f51f668ec92~b5153c98c8f8~baa5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 1 | file-size | file-size | Split by job | Three top-level types (section 1.3): SyncPublicUpdate :12, the legacy release discovery/download client SyncPublicUpdates :15-183 and the extracted-package lease SyncDownloadedUpdate :187-359. The legacy channel is superseded by setup (RELEASING.md:28): delete it at its retirement change, moving the shared IsNewer :26 and CopyBoundedAsync :128 owners (both also used by SetupUpdates/SetupCli) to the setup channel first; otherwise split by type. |
