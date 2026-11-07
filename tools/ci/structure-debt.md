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

Totals: 205 rows in 88 files. Planned own lane: src/SyncShark.Engine/Storage/StateStore.cs (0.3.78).

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

<!-- spec-source: 94e85001d0bf~8301af621262~df4ec982466a~9720c2675f93~ac9d93206c4a~0294 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 8 | class-size | class-size | Split by job | Connection lifetime, schema migration, scan/history and folder persistence are distinct jobs. |
| 2593 | long-member | AdvanceSubmission(SqliteTransaction,long,FolderSubmission,FolderSubmis | Record why it cannot be split | Record submission and unsettled member transitions as one atomic receipt application. |
| 2489 | long-member | ArmFolderSubmission(string,IReadOnlyList<FolderRemovalEffect>) 43 | Record why it cannot be split | Record exact-effect validation and immutable wire membership as one all-or-none transition. |
| 1845 | long-member | BeginFolderRemoval(string,FolderPlan,int,CancellationToken) 134 | Split by job | Counterpart saved-only range discovery with ancestor anchoring and path de-duplication is a separable job from operation and member insertion |
| 1755 | long-member | CompleteItemAttempt(ItemRetryCandidate,Guid,StateChange,PendingEffect? | Record why it cannot be split | Record failure/success outcomes, custody changes and reporting for one claimed attempt. |
| 2992 | long-member | DrainRecords(out Exception?) 108 | Split by job | Queue take and failure-isolation scheduling are separable from transactional apply with post-commit publication and closure update (local Apply) |
| 702 | long-member | EndScanAsync(string,Guid,int) 46 | Record why it cannot be split | Record whole-file/partition alternatives and failure cleanup as one scan-ending lifetime. |
| 1222 | long-member | FinishRepair(string,RepairSummary,long,Guid,LocalCatchUp,DropboxCatchU | Record why it cannot be split | Record source/checkpoint fences and retirement of covered work as one durable completion. |
| 1981 | long-member | InsertFolderMember(string,string,long,FolderRemovalItem.ProvenanceKind | Record why it cannot be split | Record provenance/scope validation and reuse/insertion as one recovery-member admission. |
| 1682 | long-member | RecordInUseFailure(string,long,long,string,IssueSubject,TextKey,WorkSe | Record why it cannot be split | Record attempt validation, issue/work binding and episode claim as one retry-responsibility installation. |
| 758 | long-member | RestoreDeletedHistory(string,Guid) 43 | Record why it cannot be split | Record verified replay and continuation publication as one completed acquisition. |
| 1153 | long-member | SavePairDecision(PairConfig,long,PairConfig,Issue?,bool,string?) 67 | Record why it cannot be split | Record definition, generations, responsibility and scan invalidation as one configuration transition. |
| 2340 | long-member | ValidateFolderChange(SqliteTransaction,long,string,FolderRemovalState, | Split by job | Pure permitted-change-shape validation differs from database evidence traversal and coverage counting. |
| 1049 | long-member | static ForgetTouchesUnreadable(SqliteConnection,string,IEnumerable<(Si | Record why it cannot be split | Record local/mapped ranges as one deduplicated forget-footprint overlap query. |
| 3248 | long-member | static InitializeSchema(SqliteConnection,bool) 111 | Split by job | Version admission/reference validation, read-only handling, transactional upgrade and reclaim/checkpoint maintenance are separable; comment :3289 is local only |
| 8 | file-size | file-size | Split by job | Connection lifetime, migration, scan/history and folder persistence are distinct jobs. |

## src/SyncShark.Engine/Run/EngineHost.cs

<!-- spec-source: fdce4d782436~d304c53acd69~a85bb145f1b9~70e73b006c2e~9422096139cc~22b0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size | Split by job | Composition, generation admission, source supervision, attempts and status are distinct jobs. |
| 1147 | long-member | BeginListing(object,AttemptOwner) 52 | Record why it cannot be split | Record creation, completion and disposal as one listing identity/retry episode. |
| 567 | long-member | ChangeGenerationAsync<1>(PairConfig,Func<object, IStateStore, IReadOnl | Record why it cannot be split | Record drain, caller commit and guaranteed reopen as one configuration transition. |
| 344 | long-member | ExecuteInGenerationAsync<1>(string,RegistrationKind,bool,Func<object,  | Record why it cannot be split | Record admission, execution and retirement around one registration. |
| 731 | long-member | InstallCurrentAsync(IReadOnlyList<string>) 44 | Record why it cannot be split | Record resource replacement, membership and reopening as one captured-generation installation. |
| 1006 | long-member | RunPrerequisiteAsync(string,CancellationToken,RunLog?) 81 | Record why it cannot be split | Record prerequisite admission, classification and deregistration as one attempt. |
| 259 | long-member | static Compose(string,Action<string>,PilotStateAuthority,EngineSetting | Record why it cannot be split | Record dependency wiring and failure cleanup as one object-graph ownership transfer. |
| 174 | long-member | static Compose(string,Action<string>,PilotStateAuthority,Mode,RunLog?, | Record why it cannot be split | Record settings, transport and diagnostics acquisition as one exception-safe composition boundary. |
| 12 | file-size | file-size | Split by job | Composition, generations, source supervision, attempts and status are distinct jobs. |

## src/SyncShark.Engine/Executor/FolderDeleteRunner.cs

<!-- spec-source: 2d6feac59a98~874c36d49838~b7c8c4555ca7~589dbfffb144~a5d8033d6fd9~24f5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Submission/job observation, post-window settlement and recovery-stage lifecycle are distinct jobs. |
| 220 | long-member | CloseWindowAsync(FolderRemovalState,Progress,CancellationToken) 65 | Record why it cannot be split | One settlement job; rule 4: :218-219 "Mappings settle only after the post-window feed agrees with the frozen set; the cursor retires last, so a crash re-reads it..." states an ordering, not why the steps cannot be separate units (no shared state, lock, transaction or lifetime named) |
| 619 | long-member | ConcludeAsync(FolderRemovalState,Progress,WorkSelection?,CancellationT | Record why it cannot be split | Record mutually exclusive aggregate conclusions as one operation-transition decision. |
| 995 | long-member | ObserveAsync(FolderSubmission,int,object,bool,CancellationToken) 41 | Record why it cannot be split | Record one observation’s durable accounting and retry/failure disposition. |
| 473 | long-member | PublishAsync(FolderRemovalState,FolderRemovalItem,Progress,Cancellatio | Record why it cannot be split | Record no-effect, occupied-target and uncertain-effect outcomes within one publication lifetime. |
| 736 | long-member | SendAndJoinAsync(IReadOnlyList<FolderRemovalEffect>,IAdmittedExecution | Record why it cannot be split | Record revalidation, arming, membership commitment and release as one submission/join handoff. |
| 904 | long-member | SubmitAsync(FolderSubmission,IReadOnlyList<FolderRemovalEffect>,Cancel | Record why it cannot be split | Record accepted, terminal, no-effect and unknown cases as one attempt-result translation. |
| 1 | file-size | file-size | Split by job | Submission observation, post-window settlement and recovery-stage protocols are distinct jobs. |

## src/SyncShark.Engine/Storage/ChangeTable.cs

<!-- spec-source: dbd669bea5ab~d6bcf5c8bd1b~42c0f829e486~88dde65d500c~0d579755b697~65c5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | class-size | class-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody lifecycle. |
| 87 | long-member | AcquireJournalPage(string,JournalPosition?,JournalPosition,IReadOnlyLi | Record why it cannot be split | Record member/position fences, notices and checkpoint advancement as one atomic acquisition. |
| 552 | long-member | AdvanceBefore(SqliteTransaction,string,WorkSelection,StateChange) 46 | Record why it cannot be split | Record one selected work item’s replacement baseline derivation and propagation. |
| 658 | long-member | AttachIssueWork(WorkSelection,Issue,IEnumerable<(ChangeNotice Notice,  | Record why it cannot be split | Record retain/move/create alternatives as one atomic responsibility handoff. |
| 910 | long-member | EnsureIssueWork(SqliteTransaction,string,long,long,long,string,IssueSu | Record why it cannot be split | Record selected/source-less paths establishing the same exact issue-work custody. |
| 747 | long-member | ReadLocalDirty(string,long,long,string,string?,string?,string?) 61 | Record why it cannot be split | Record identity/current/recorded-path probes as one consistent dirty-evidence answer. |
| 833 | long-member | ValidateLiveCoverage(WorkSelection,LiveCoverage,IReadOnlyList<LiveEffe | Record why it cannot be split | Record all footprint/later-evidence checks as one admitted-selection freshness proof. |
| 11 | file-size | file-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody persistence. |

## src/SyncShark.Engine/Executor/ItemSync.cs

<!-- spec-source: 711bd9973b85~642172e258ef~66f708566c5f~19b87f89d94f~dab3e95fe3a4~2f98 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size | Split by job | ItemSync: preparation PrepareAsync :493-525, freshness IsFreshAsync :536-586, admission/routing, folder replacement, skipped-notice upkeep :625-653 |
| 65 | long-member | DispatchPreparedAsync(SyncAction,CancellationToken,Action?,Action<Row, | Record why it cannot be split | Record promotion, stage release and stale fallback as one prepared-dispatch lifetime. |
| 536 | long-member | IsFreshAsync(Row,SyncAction,CancellationToken) 51 | Record why it cannot be split | Record action-specific checks as one freshness verifier returning reusable observed evidence. |
| 347 | long-member | RunAdmittedAsync(Row,CancellationToken,Observer?,SyncAction?,IAdmitted | Record why it cannot be split | Record reobservation, footprint checking and stale refusal within one admitted execution loop. |
| 219 | long-member | RunRowAttemptAsync(Row,CancellationToken,Action?,Action<Row, SyncOutco | Record why it cannot be split | Record fast settlement and expected-retry capture within one attempt envelope. |
| 1 | file-size | file-size | Split by job | same jobs as row 25 (ItemSync file) |

## src/SyncShark.Engine/Executor/SelectionPass.cs

<!-- spec-source: f75f14a2f881~b5d52b270051~c027c17a5fc9~94fc77dfd79f~f0383f7a6764~c46b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | class-size | class-size | Split by job | Branch execution/settlement is separable from branch acquisition (walks, folder batches, unreadable release/replay) |
| 335 | long-member | EnumerateBranchesAsync(WorkSelection,CancellationToken) 120 | Split by job | Branch discovery, folder dispatch and unreadable-release/replay are distinct protocols. |
| 482 | long-member | ListAsync(PendingChange,string,ListOptions,CancellationToken) 45 | Record why it cannot be split | Record the manual iterator lifetime needed for failure yield, cancellation and exactly-once disposal. |
| 58 | long-member | RunSelectionAsync(WorkSelection,CancellationToken) 201 | Split by job | Residual projection, branch observation/claiming and prepared-action dispatch are embedded jobs. |
| 114 | long-member | RunSelectionAsync(WorkSelection,CancellationToken)::ObserveProjectedAs | Record why it cannot be split | Record primary-observation reuse, dirty evidence and peer authority as one projected-row result. |
| 1 | file-size | file-size | Split by job | Same as row 28: execution separable from acquisition and unreadable release/replay |

## src/SyncShark.Core/SyncCliRegistry.cs

<!-- spec-source: 12d15282816a~c5e5c4a61b9d~1fe1dd016019~de1d7d145003~96a952eae2f9~f9c6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | class-size | class-size | Split by job | Command metadata, argument parsing, action dispatch and result presentation are distinct jobs. |
| 1236 | long-member | static DispatchAsync(ISyncCliRuntime,SyncCliInvocation,TextWriter,Text | Split by job | Progress presentation/sanitization differs from selecting and invoking the command action. |
| 739 | long-member | static Parse(string[]) 411 -> 456 | Split by job | Generic tokenization differs from command-specific combination/value validation. |
| 1831 | long-member | static TryRender(object?,out string) 477 -> 499 | Split by job | Reclassified to split (R2-11): each result shape's formatting is an independent job, and the capability case already delegates to RenderCapabilities. |
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

<!-- spec-source: 7985eafb2cda~78186198060e~8b8d966a6a09~802bab3cb3f9~4563ef22d9cc~349c -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | ScanPass runs four inline stage implementations plus separable issue reconciliation and cross-pair stale-stage cleanup; header line 6 only describes shared state, so not one job |
| 357 | long-member | ReconcileIssues(PrefixSet,IReadOnlyList<(string Path, TextKey Cause, I | Split by job | Obsolete-issue retirement differs from reusable conflict-hash proof collection; preserve the single traversal. |
| 59 | long-member | RunRowsAsync(ListedSkips,Guid,bool,Func<string, bool>?,IReadOnlySet<lo | Split by job | Cleanup, exclusion implementation, rename barriers and row/result production are distinct stage jobs. |
| 1 | file-size | file-size | Split by job | Same multi-job type as row 27; header :6 describes shared state but the file still holds separable stage, reconciliation and cleanup jobs |

## src/SyncShark.Engine/Run/RunLog.cs

<!-- spec-source: 5f3991c98c49~711841c6c856~0fb7b5adc99f~2e933aeb19d3~e559f04feae2~bc8b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size | Split by job | Canonical codec differs from mutable accounting, flushing and subscriber delivery. |
| 204 | long-member | Event(RunEvent) 70 | Split by job | Progress reduction, canonical accounting and ordered human delivery are distinct jobs. |
| 77 | long-member | ReportProgress(string?) 43 | Record why it cannot be split | Record one quiet observation’s matching diagnostic and sequenced human snapshots. |
| 13 | file-size | file-size | Split by job | Canonical codec differs from mutable accounting, flush lifetime and delivery. |

## src/SyncShark.Core/SyncCliHostReads.cs

<!-- spec-source: fed22447dc5c~de7d037b6f57~1a168e6050e0~f20bee433d12~adb16f15910f~2680 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 22 | file-type-name | file-type-name | One type per file / name matches type | Top-level `EngineHostReadCatalog` does not match its file. |
| 5 | file-type-name | file-type-name | One type per file / name matches type | Top-level `EngineHostReadKind` does not match its file. |
| 14 | file-type-name | file-type-name | One type per file / name matches type | Top-level `IEngineHostReads` does not match its file. |
| 5 | top-level-types | top-level-types | One type per file / name matches type | Three genuine top-level types at lines 5, 14 and 22. |

## src/SyncShark.Core/NtfsUsnJournal.cs

<!-- spec-source: e0903135ccb6~5d3ba0d7b17f~4ea71fa2a45e~8f6c1f76deda~13445a8c87e3~c345 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | class-size | class-size | Split by job | NtfsUsnJournal.File.cs:11-154 per-file USN evidence/gap-handle opening is a job distinct from volume journal reading; partials do not answer 1.3 |
| 307 | long-member | static TryDeviceIoControlPage(SafeFileHandle,uint,byte[],byte[],out in | Record why it cannot be split | Record that one overlapped request owns pinned buffers, completion, cancellation drain and release. |
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

<!-- spec-source: 0a329091d316~a80caabb4757~183202d4e7ee~9636bc33c46d~932a94f79946~d0fe -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size | Split by job | Action facade also implements configuration mutation, recovery workflows and historical logs. |
| 231 | long-member | ResolveAsync(string,long?,ResolutionChoice,string?,FirstRunPolicy?,boo | Split by job | Configuration/root choices, bulk retry aggregation and individual recovery dispatch are distinct implementations. |
| 12 | file-size | file-size | Split by job | Action facade embeds configuration, resolution/recovery and historical-log implementations. |

## src/SyncShark.Engine/Executor/ActionExecutor.cs

<!-- spec-source: aa0e108ed419~6233e6b8de49~d1420a9f3ca7~9f4fdda8c757~eda4c5053408~160f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 357 | long-member | CopyAsync(SyncAction,bool,CancellationToken,Func<ValueTask>?,(Side Sid | Record why it cannot be split | Record one selected copy outcome preserving each publication/settlement boundary. |
| 208 | long-member | FailCoreAsync(Row,SyncAction?,Exception,CancellationToken,Action<Issue | Record why it cannot be split | Record one failed-item mapper/settler, including occupant enrichment and retry disposition. |
| 1 | file-size | file-size | Split by job | Header itself names two jobs (ExecuteAsync table and item-error mapping); readability rules and event emission are further separable jobs |
| 256 | long-member | ApplyAsync(SyncAction,KeepBothLabels?,bool,CancellationToken,Func<ValueTask>?,(Side Side, string Path, Entry?Entry)?,Action<Issue>?,Row?,Guid?,ItemRetryCandidate?,WorkSelection?) | Split by job | Reclassified to split (R2-13): per-kind change construction and the shared decorate/settle/emit tail are separable; the copy and replace turn already delegates to CopyAsync. |

## src/SyncShark.Engine/Executor/RenameRunner.cs

<!-- spec-source: 1e72e1c0c3b7~1239e7d2dc99~f2dfa5f00ed4~f6ca3102f5c8~d8e64c900023~cbaf -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 82 | long-member | RunCurrentAsync(WorkSelection?,IReadOnlyList<RenameCandidate>,SyncedEn | Record why it cannot be split | Record observation, Planner choice and outcome mapping as one current rename attempt. |
| 32 | long-member | RunUnrecordedAsync(IAdmittedExecution?,WorkSelection?,IReadOnlyList<Re | Record why it cannot be split | Record footprint refresh/reacquisition and binding discovery as one rename preparation loop. |
| 1 | file-size | file-size | Split by job | Admission/binding resolution, observation+decision, results-table application and failure mapping are separable; header :5 lists them as three steps |
| 212 | long-member | ApplyAsync(WorkSelection?,RenameDecision,IReadOnlyList<RenameCandidate>,SyncedEntry,Observations,Action<Issue>?,Guid,ItemRetryCandidate?,CancellationToken) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 96. |

## src/SyncShark.Engine/Run/PairSession.cs

<!-- spec-source: 68020ecc0437~8b5ab894ad5b~c65b456c6464~3de16bcc8d2d~4211f4106b84~a29d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | long-member | PairSession(IStateStore,PairConfig,EngineSettings,PathScope,ISide,ISid | Record why it cannot be split | Record one session’s dependency graph and invariant wiring. |
| 87 | long-member | static ComposeGeneration(IStateStore,PairConfig,IReadOnlyList<PairConf | Record why it cannot be split | Record account/scope/source resources and transport cleanup as one generation composition. |
| 137 | long-member | static Open(IStateStore,PairConfig,EngineSettings,HttpClient,bool,RunL | Split by job | Open embeds a transient-failure retry/backoff executor and a disk-space reservation with generation hold, behaviour beyond composition; doc comment :136 descriptive |

## src/SyncShark.Engine/Storage/IssueTable.cs

<!-- spec-source: 3025c34d474b~84eb5206e23e~c999c23d1cd8~6170db287bd0~bcd9150c4503~af0d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 561 | long-member | static MergeSkip(SqliteConnection,SqliteTransaction,string,long,string | Record why it cannot be split | Record both side members and their single path-level issue as one reconciliation. |
| 15 | long-member | static Raise(SqliteConnection,SqliteTransaction,string,IssueKind,TextK | Record why it cannot be split | Record continuation/protection/refresh/replacement as one precedence-ordered raise transition. |
| 7 | file-size | file-size | Split by job | Raise/close lifecycle, paged listing snapshots, skip-member reconciliation, unreadable queries, upgrade backfill, retry-episode lifecycle and invalid-job projection are distinct jobs; header :6 is a grouping |

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

<!-- spec-source: b94ff1e9a862~82d9637cff75~9bb621b8b450~b42c93039ddf~aba0b32190c0~84a9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 207 | class-size | class-size | Split by job | Command execution, human/help rendering and IPC response-budget fitting are distinct jobs. |
| 293 | long-member | static ExecuteAsync(string[],TextWriter,TextWriter,Func<ISyncCliRuntim | Split by job | SyncCli.cs:312-334 state-free help/version/capabilities answers are a separate job from the runtime invocation/deadline/disposal lifetime :336-439 |

## src/SyncShark.Core/SyncLocalFileAuthority.cs

<!-- spec-source: c8d4a9e41032~68b75640bafc~d8c007d82375~67d9f29d159f~5c44b03daee9~0ff6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | Held-file authority and ACL-recovery envelope encoding/protection are distinct jobs. |
| 9 | file-size | file-size | Split by job | Held-object authority differs from ACL recovery-envelope encoding/protection. |

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

<!-- spec-source: b12b8b0b4446~0b2fecf5d3a6~e7949b22d3ea~d26ac5b1c0ca~4907046d713a~d10d -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 16 | class-size | class-size | Split by job | Checked IO, publication/removal, recovery custody and identity/drive observation are distinct jobs. |
| 16 | file-size | file-size | Split by job | Checked IO, pending effects, recovery custody and identity/drive observation are distinct jobs. |

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

<!-- spec-source: e470cc50c505~21dfa0c4e524~ce760d81da78~a2dd95d5632d~70ecc3b14508~8878 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 263 | long-member | RunLiveFolderPlanAsync(Row,Action<Row, Issue>,WorkSelection,LiveCovera | Record why it cannot be split | Record the plan operation’s deliberate inside/outside-admission sequencing. |
| 1 | file-size | file-size | Record why it cannot be split | Record one unfinished-folder-plan continuation/lifetime owner across its entry routes. |

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

<!-- spec-source: f8380ecdbaff~51651a5a8cb1~672f0a7f66e6~aba0c215ff89~affc9598d276~cfab -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 136 | long-member | static ApplyRename(SqliteConnection,SqliteTransaction,string,RenameApp | Record why it cannot be split | One rename application; type header :6 "Applies one StateChange inside the caller's transaction, in the order of section 3.5." describes what the type does and does not say why the function cannot be split (its helpers already share that transaction) |
| 11 | long-member | static Commit(SqliteConnection,SqliteTransaction,ChangeTable,string,St | Record why it cannot be split | One ordered StateChange interpreter; same header :6 is a description, not a reason (helpers such as ApplyRename already take the same transaction) |

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

<!-- spec-source: c37bdc62ae1f~2fce67ae5878~7cff37b9eeb1~fa6e58e2955c~e8c0ed812f5b~bc53 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 33 | long-member | ServeAsync(PipeStream,CancellationToken) 42 | Record why it cannot be split | Record trusted intake and irreversible Accepted-to-Exit handoff as one connection lifetime. |

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

<!-- spec-source: 9bdec871923a~8d7fcbc713b1~21bb49c970d7~e8085646fe2b~03e4e4ab5b89~93aa -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | EnsureAsync(Side,string,Entry?,CancellationToken) 55 | Record why it cannot be split | Record ancestor dependency ordering and coalesced waiter release as one creation protocol. |

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

<!-- spec-source: ac54a0b5d37e~c36d9654e662~8db071fb561c~7e8ef55a0029~23c376295326~66b8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 94 | long-member | StageAsync(Side,Entry,string,CancellationToken) 50 | Record why it cannot be split | Record source views, reservation release, attempt completion and transfer within one stage lifetime. |

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

<!-- spec-source: 71acab9ad0dc~06efe5185ab0~76564085e133~834d88edd04f~a7cd3624d555~8354 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 106 | long-member | ResolveOne(ulong,uint,FileAttributes,CancellationToken) 43 | Record why it cannot be split | Record one USN event’s identity/fallback resolution, invalidation and resulting notice. |

## src/SyncShark.Engine/Planner/FolderDeletes.cs

<!-- spec-source: 486795467295~e1208ce42826~e1d3655b2853~f51c2982432c~3efc08cdb8db~ff60 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 36 | long-member | static FirstRun(Row,bool?,bool,bool) 44 | Record why it cannot be split | Record one folder decision over the same completed-content and mapped-target evidence. |

## src/SyncShark.Engine/Run/FolderBatch.cs

<!-- spec-source: 474aea9b6e9b~00b28d731285~702b76e7b100~d055e84ab7cc~fae4fe4ebfc7~e355 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | long-member | static RunAsync(ExecutorContext,SyncPorts,ISide,RunLog,WorkSelection?, | Record why it cannot be split | Record the scoped scan reservation through listing, execution, joining and cleanup. |

## src/SyncShark.Engine/Run/Repair.cs

<!-- spec-source: 650f92bda46f~da8175a46178~60153ee0f726~641215cec899~abd198450266~9afa -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | long-member | static RunAsync(PairSession,CancellationToken,IReadOnlySet<long>?) 179 | Split by job | Scan sink/resource setup and summary publication with log pruning and log-failed marking are separable from orchestration; doc comments :7,:10 describe behaviour |

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

<!-- spec-source: 3f240ad35513~e60ccd1bbd1b~be7370f8d759~fc7f6c32b186~beeaca81a11e~9f68 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 3 | file-size | file-size | Split by job | IStateStore.cs:5-324 spans the same pair/work/pending/retry/folder-removal groups both reviews require StateStore to split along; owner decision 30 (docs/design/owner-decisions.md:101) is the interface-split precedent |

## src/SyncShark.Engine/Local/LocalBin.cs

<!-- spec-source: 235ddb021997~26a1f3576f3f~9681cafa15ad~3b74139d30b1~65151dcc0d52~dd50 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | file-size | file-size | Record why it cannot be split | One removal job; rule 4: header :10-11 "Verification, receipt and exact-handle disposition are one removal job, so they stay in this type." asserts a grouping without naming the constraint, so not R |

## src/SyncShark.Engine/PathNames.cs

<!-- spec-source: 8e8aaebf7c14~f5866e8f92b1~2067bfcfd2f6~43a1bf455500~18f8626cc0c7~a0bf -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | file-size | file-size | Record why it cannot be split | Record one frozen path-identity/key facade whose derived operations reuse canonical comparison. |

## src/SyncShark.Engine/Run/PairSafety.cs

<!-- spec-source: 0d7ac9692982~891677c3873d~b4cfb89b259f~52d6fcc1e61c~7cdb09601f3f~325e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | file-size | file-size | Split by job | Root identity comparison/refusal is separate from execution entry admission (lock footprints, live/recovery/selected validation); header :8 names a grouping only |

## src/SyncShark.Engine/Storage/StateSchema.cs

<!-- spec-source: b42dbdaa0065~0e388cfdad8e~215536f0a025~61a368b93f40~46f04620f322~e94f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Record why it cannot be split | Record one declarative SQL/schema catalogue, without execution or transaction policy. |

## src/SyncShark.Engine/TextKey.cs

<!-- spec-source: 8e97812d9e09~a8413d9a3714~7ca5008b5406~e1a9009a8bdd~7c7bc2249fa0~8519 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 3 | file-size | file-size | Record why it cannot be split | Record one strings-catalog identifier enum; arbitrary splitting would fragment that contract. |

## src/SyncShark.Engine/Executor/TaskSet.cs

<!-- spec-source: 30327fc55e1c~f5e5a1509367~46dcc47b6fa0~cea754848a2f~aada3ee1f8c8~7677 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | Add(Func<Action, Task>) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 106. |

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

<!-- spec-source: c0160217b0e6~8bcedea1aa8d~84795e16ebc6~97f57c7436ad~b2f4f0fc62b4~40d5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 1 | file-size | file-size | Split by job | Per-execution row-context projection, the run-local hold/Retry-authority state machine and completed-acquisition first-run deletion/inventory projection are distinct jobs with separate consumers and output types; split into RowContexts, ExecutionHolds and FirstRunProjection, calling IssuePolicy for hold/first-run policy and spent-key queries through IStateStore. ExecutionHolds and FirstRunProjection share the existing gate by reference; preserve the current critical-section span and store-call order, without replacing Build's protected combination with separately locked snapshots. FirstRunProjection also owns firstRun and deletedName alongside acquisition validity and the template; eligibility is checked before the template read. |
