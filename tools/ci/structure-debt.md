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
| `tools/ci/check-spec-ratchet.py:collect_review_units` | Evidence construction 236–241; shared owner/size/member inventory 243–255; member/local units 256–273; class units 274–279; file unit 280–283. | These are one review-unit output from one parsed inventory. Keeping its accumulation together avoids re-parsing declarations or passing a second mutable inventory between stages; receipt/freshness policy stays in spec_reviews. |
| `tools/ci/check-spec-ratchet.py` | Source findings 83–343 (literal counters 116–163, declaration inventory 166–283, file assembly 286–324); baseline/comparison pipeline 346–520; CLI orchestration 523–538. | This is the existing snapshot-safe command, coordinating parser, identity and review owners. The retained source/baseline stages form its one comparison pipeline; splitting its command-local stages into another module would add shared counter/key protocol without separating another policy. Review policy and lexical parsing already have separate owners. |
| `tools/ci/spec_cslex.py` | Mask literals/comments 12–188; delimiter matching 191–219; parsed member representation 229–245; list/signature parsing 248–403; type/member parsing 406–526; source coordinates 529–532. | These stages implement the existing offset-preserving C# lexical parser. They share masked-source offsets and one member representation; callers consume this parser as one module, while structural decisions remain outside it. |
| `tools/ci/test-spec-ratchet.py` | Synthetic source 20–21; public checker contract cases and their fixture 24–594; assertion-producing mutation controls 597–742; unittest entry 745–746. | The contract suite and controls deliberately share one fixture and execute in one discovery entry. Keeping the paired positive/negative evidence here prevents fixture copies or a second test runner; no production hook is introduced. |

## src/SyncShark.Engine/Storage/StateStore.cs

<!-- spec-source: cef391c2419c~f5bb1b7fcc0a~f3027538e119~f290f9003cc9~0b0c9d0ba33e~5402 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 8 | class-size | class-size | Split by job | Connection lifetime, schema migration, scan/history and folder persistence are distinct jobs. 0.3.79 adds expected-notice fencing for retry reads and visibility (B-13), the live-refusal read (R6), the published-USN migration step (#90b) and block-wise history restore (B-21) to the same class: 3490 to 3588 lines. 0.3.80: the cut-over deletes SaveImportedPair; selection admission now answers instead of throwing (BeginSelection), WorkAttempted, RequireDiscovery and SelectionBaseline forward to ChangeTable, and InitializeSchema gains the version-12 discovery_required step: 3588 to 3595 lines. Same split; no new job. 0.3.80.1 (B-93): the closure-key cache holds canonical keys (NeedsClosure :3203, cache update) and InitializeSchema gains the version-13 path-key index step; 3602 to 3618. |
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
| 3344 | long-member | static InitializeSchema(SqliteConnection,bool) 126 | Split by job | Version admission/reference validation, read-only handling, transactional upgrade and reclaim/checkpoint maintenance are separable; #90b adds the version-10 published-USN step (111 to 119); 0.3.80 adds the version-12 discovery_required step (119 to 126). |
| 8 | file-size | file-size | Split by job | Connection lifetime, migration, scan/history and folder persistence are distinct jobs; 3497 to 3595 lines in 0.3.79, 3602 in 0.3.80 (see class-size). |

## src/SyncShark.Engine/Run/EngineHost.cs

<!-- spec-source: da4c20e7d809~ba16c9f8e601~95f4636e9c5f~1f9aa7df44cc~83588dfef4b2~552c -->

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

<!-- spec-source: ccb32ff9662a~07c1a4575109~671ba9014160~3005c62eceb9~9cc3adbef696~7258 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (1131) | Split by job | Current jobs: operation orchestration :32-216; post-window observation/settlement :221-374; recovery stages/publication/settlement/reporting :376-620; aggregate conclusion :622-698; effect admission/submission/join :701-1042; nested progress state :1044-1136. B9 routes failure interpretation to ItemFailures; the historical multi-job debt remains open. |
| 221 | long-member | CloseWindowAsync (65) | Record why it cannot be split | Current :221-285 checks the post-window feed, selects readable verified or absent saved-only members for settlement, reports completed children and retires its cursor. The :218-220 ordering comments do not themselves prove that these steps cannot be separated; existing reason obligation remains open. |
| 622 | long-member | ConcludeAsync (45) | Record why it cannot be split | Current :622-666 maps mutually exclusive aggregate progress states to the next operation transition; uses the same Progress answer. Independent disposition is unchanged by this description refresh. |
| 1002 | long-member | ObserveAsync (41) | Record why it cannot be split | Current :1002-1042 translates one submitted-job observation into durable member accounting and retry/failure disposition. |
| 475 | long-member | PublishAsync (55) | Record why it cannot be split | Current :475-529 owns one recovery-stage publication and its no-effect, occupied-target and uncertain-effect outcomes. |
| 740 | long-member | SendAndJoinAsync (42) | Record why it cannot be split | Current :740-781 revalidates effects, arms/commits submission membership and joins after releasing execution admission while retaining the footprint. |
| 910 | long-member | SubmitAsync (43) | Record why it cannot be split | Current :910-952 translates accepted, terminal, no-effect and unknown submission outcomes for the same attempt. |
| 1 | file-size | file-size (1137) | Split by job | Same current jobs and retained debt as the class row. This receipt refresh describes the current caller migration, not semantic clearance. |

## src/SyncShark.Engine/Storage/ChangeTable.cs

<!-- spec-source: 077fd262878b~a70f149ae340~e52f7cf91088~dace9203ca33~af07e9521972~6fa9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 11 | class-size | class-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody lifecycle. 0.3.79 adds the live-refusal answer (R6), notice content merging (S2) and own-write USN evidence (#90b): 1473 to 1543 lines. 0.3.80 adds the per-pass attempted answer (WorkAttempted/Attempted :503-508) and the durable discovery-required work flag (RequireDiscovery :510-519, NeedsOwnSelection :1283-1284): 1543 to 1565 lines. Both are work-selection custody; same split. |
| 101 | long-member | AcquireJournalPage(string,JournalPosition?,JournalPosition,IReadOnlyLi | Record why it cannot be split | Record member/position fences, notices and checkpoint advancement as one atomic acquisition. |
| 774 | long-member | AdvanceBefore(SqliteTransaction,string,WorkSelection,StateChange) 46 | Record why it cannot be split | Record one selected work item’s replacement baseline derivation and propagation. |
| 884 | long-member | AttachIssueWork(WorkSelection,Issue,IEnumerable<(ChangeNotice Notice,  | Record why it cannot be split | Record retain/move/create alternatives as one atomic responsibility handoff. |
| 1190 | long-member | EnsureIssueWork(SqliteTransaction,string,long,long,long,string,IssueSu | Record why it cannot be split | Record selected/source-less paths establishing the same exact issue-work custody. |
| 987 | long-member | ReadLocalDirty(string,long,long,string,string?,string?,string?) 76 | Record why it cannot be split | Record identity/current/recorded-path probes as one consistent dirty-evidence answer. #90b grew it 61 to 76 with the own-write USN fold (local Count) and the OwnWrite check. |
| 1103 | long-member | ValidateLiveCoverage(WorkSelection,LiveCoverage,IReadOnlyList<LiveEffe | Record why it cannot be split | Record all footprint/later-evidence checks as one admitted-selection freshness proof. |
| 11 | file-size | file-size | Split by job | Source checkpoint acquisition differs from work-selection/issue-custody persistence; 1483 to 1553 lines in 0.3.79, 1575 in 0.3.80 (see class-size). |

## src/SyncShark.Engine/Executor/ItemSync.cs

<!-- spec-source: f7eec3f4317e~b2d993160a6c~2237fb33ac19~7d84bf6b27f5~ca5f9987f8b2~ba1c -->

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

<!-- spec-source: 4a2ccca55e7f~62f3f77e8b5a~47de4ab79d39~05381257d579~01e2e651bffc~68b0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Lexical validation, field projection and Dropbox metadata interpretation are distinct jobs. |
| 113 | long-member | Read(ReadOnlySpan<byte>,bool,ReadOnlySpan<string>,bool,CancellationTok | Record why it cannot be split | Record one token pump sharing incremental consumption, readiness and final-block state. |
| 238 | long-member | StringByte(byte,ReadOnlySpan<string>) 92 | Record why it cannot be split | Record escape, surrogate and UTF-8 branches as transitions of one incremental string decoder. |
| 156 | long-member | StructuralByte(byte,ReadOnlySpan<string>,bool) 66 | Record why it cannot be split | Record punctuation, container closure and lexeme start as one grammar-frame transition. |
| 7 | file-size | file-size | Split by job | Incremental JSON lexing, selected-field projection and metadata interpretation are distinct jobs. |

## src/SyncShark.Engine/Executor/ScanPass.cs

<!-- spec-source: 12778720ad86~b36784ae1b57~fdd0748fffff~b1aea9e9435f~9d3eff4bae86~a083 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (510) | Split by job | Current capacity/orchestration :22-51; staged scan implementation :53-379; unreadable enrollment :388-408; folder continuation/plans :412-459; issue reconciliation :461-505; progress :507-515. Cross-pair stale-stage cleanup :73-91 remains inside RunRowsAsync. RowCapacity/TotalCapacity :22-26 are shared with Syncer. B9 migrates calls to ItemSync owners and ItemFailures, without closing this historical split. |
| 461 | long-member | ReconcileIssues :461-505 (45) | Split by job | Obsolete-issue retirement and reusable conflict-hash proof collection remain within the same traversal. |
| 53 | long-member | RunRowsAsync :53-379 (327) | Split by job | Current stages: skips/cleanup/exclusions :57-128; rename scheduling/barriers :129-261; row contexts/results/dispatch :262-364; folder plans/first-run finalization :365-378. These embedded stage implementations remain the historical split obligation. |
| 1 | file-size | file-size (516) | Split by job | Same current jobs as the class row, including nested rename-execution state :381-386. Receipt refresh records current caller locations only. |
| 163 | long-member | RunRowsAsync::RunRename :163-205 (43) | Record why it cannot be split | One rename group's task lifetime signals completion exactly once; the dependency rerun continues the same entry. Reason recorded above RunRename. |

## src/SyncShark.Engine/Run/RunLog.cs

<!-- spec-source: 43d40f071903~db7c79e9a9b6~9054d08e5e95~64b698ff6016~18d2a8f0d5fc~3002 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size | Split by job | Canonical codec differs from mutable accounting, flushing and subscriber delivery. S2 adds active-branch tracking (_activeBranches, _branchOrder) to progress reduction: 665 to 685 lines. |
| 207 | long-member | Event(RunEvent) 87 | Split by job | Progress reduction, canonical accounting and ordered human delivery are distinct jobs. S2 grew the progress-reduction part (branch tracking) 70 to 87. |
| 80 | long-member | ReportProgress(string?) 43 | Record why it cannot be split | Record one quiet observation’s matching diagnostic and sequenced human snapshots. |
| 13 | file-size | file-size | Split by job | Canonical codec differs from mutable accounting, flush lifetime and delivery; 677 to 697 lines. |

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

<!-- spec-source: 716b43cdea73~b712089f902d~8a159e645d99~d1f78e7af38d~b2f740aea3b1~50c0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. |
| 27 | long-member | ReadAll(string,ListOptions,ListingReadContext,CancellationToken) 115 | Split by job | Retry-owned page acquisition/cleanup differs from traversal spelling, filtering and progress. |
| 7 | file-size | file-size | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. |

## src/SyncShark.Engine/Dropbox/DropboxWriter.cs

<!-- spec-source: 0608151b5c7a~d14838d284b7~82e3c52a8b48~07e4cf15d616~24ef93b50266~10b1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | class-size | class-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 27 | long-member | StageAsync(string,Stream,Entry,CancellationToken) 42 | Record why it cannot be split | Record one verified stage sharing hash, offset and terminal session result across upload alternatives. |
| 8 | file-size | file-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 256 | long-member | SettleCohort(List<PendingPublish>,IReadOnlyList<(Entry?Entry, Exception?Error, bool KnownNoEffect, bool WriteThrottled)>,RequestAttempt) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 68. |

## src/SyncShark.Engine/Run/EngineOperations.cs

<!-- spec-source: b2f19d179066~8b97e0801f69~2caaa0fc732c~94a1d4365696~477321a3c658~cbad -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size | Split by job | Action facade also implements configuration mutation, recovery workflows and historical logs. B-13 adds the reviewed-notice token check and expected-notice routing: 582 to 593 lines. 0.3.80: the cut-over deletes ImportPairAsync; B-74 adds the dependency-cycle Retry group RetryCycleGroupAsync :356-372 and the single-issue Retry-runs-Repair route in ResolveAsync (both resolution/recovery implementation, the owed split); 1A moves startup admission into WithHostAsync and R3 routes settings through CurrentSettings :51. 593 to 617 lines. |
| 239 | long-member | ResolveAsync(string,long?,ResolutionChoice,string?,FirstRunPolicy?,boo | Split by job | Configuration/root choices, bulk retry aggregation and individual recovery dispatch are distinct implementations. B-13 adds the reviewed parameter and its check (113 to 116). B-74 adds the cycle-group and single-issue Repair routes (116 to 127, :228-354). |
| 12 | file-size | file-size | Split by job | Action facade embeds configuration, resolution/recovery and historical-log implementations. This correction removes the stale direct-operation cache without relocating action authority; the larger facade split remains open rather than being coupled to the settings-lifetime fix. 604 to 628 lines in 0.3.80 (see class-size). |

## src/SyncShark.Engine/Executor/ActionExecutor.cs

<!-- spec-source: 39292f6c7e74~23ee05db39ac~7aff8c919a5a~4d97d73e3553~4ac49e535712~6f0e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 271 | long-member | CopyAsync :271-369 (99) | Split by job | Current selected-copy path sequences retained-info settlement, replacement removal, parent ensure, destination write and completion settlement; transfer/removal lifetimes stay in their effect owners. Existing reason obligation is not newly adjudicated by this receipt. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |
| 1 | file-size | file-size (426) | Split by job | Removal effects :12-85; execute/attempt :87-166; apply :168-268; copy :270-369; readability refusals :371-416; issue events :418-423. File holds one type with these separate jobs. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |
| 169 | long-member | ApplyAsync :169-268 (100) | Split by job | Per-kind application :173-259 and shared decorate/settle/emit tail :260-267 remain. Construction/decoration calls ActionChanges; copy/replace dispatch calls CopyAsync :271-369. The earlier R2-13 split obligation remains pending independent reconciliation. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |
| 6 | class-size | class-size (421) | Split by job | Removal effects :12-85; execute/attempt :87-166; apply :168-268; copy :270-369; readability refusals :371-416; issue events :418-423. File holds one type with these separate jobs. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ActionExecutor structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ActionExecutor.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |

## src/SyncShark.Engine/Executor/RenameRunner.cs

<!-- spec-source: 3a22b7ac6dc8~fa91948b920e~010516942346~96c1ade2882d~5d6acd5e767d~ad70 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 120 | long-member | RunCurrentAsync :120-241 (122) | Record why it cannot be split | Current attempt: callback/error boundary :127-136; readable/listed/fresh observation and footprint checks :138-166; dirty/hash evidence and Planner choice :167-205; application :207-208; clash/freshness/failure mapping :210-240. Existing single-attempt reason remains for independent evaluation; failure classification calls ItemFailures. |
| 47 | long-member | RunUnrecordedAsync :47-117 (71) | Record why it cannot be split | Current preparation loop retains binding discovery, footprint acquisition and the one-reacquisition stale disposition; the reacquired flag is at :52 and the shared stale policy is used at the widening exits. |
| 1 | file-size | file-size (530) | Split by job | Current clash/entry routing :12-44; binding/admission preparation :47-117; current observation/decision/application :120-241; observation/identity projection :243-319; results-table application :323-442; failure/subject mapping :444-473; footprint/path construction :475-529. B9 only ports the three failure-owner calls to ItemFailures; this historical file split remains open. |
| 323 | long-member | ApplyAsync :323-442 (120) | Verify existing reason (R2) | Current results-table application and settlement use the same rename decision, saved binding and observations; the existing constraint comment immediately precedes the method at :321-322. Reason verification and semantic verdict remain independent work. |

## src/SyncShark.Engine/Run/PairSession.cs

<!-- spec-source: d6d5a7baa22f~1478d71ff458~18bf436b0abd~bd713202c780~66da16219fd7~8fbc -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 15 | long-member | PairSession(IStateStore,PairConfig,EngineSettings,PathScope,ISide,ISid | Record why it cannot be split | Record one session’s dependency graph and invariant wiring. |
| 87 | long-member | static ComposeGeneration(IStateStore,PairConfig,IReadOnlyList<PairConf | Record why it cannot be split | Record account/scope/source resources and transport cleanup as one generation composition. |
| 137 | long-member | static Open(IStateStore,PairConfig,EngineSettings,HttpClient,bool,RunL | Split by job | Open embeds a transient-failure retry/backoff executor and a disk-space reservation with generation hold, behaviour beyond composition; doc comment :136 descriptive |

## src/SyncShark.Engine/Storage/IssueTable.cs

<!-- spec-source: 9bd45466cc99~05101776e96f~e1bc46c835f2~c8246b7a4a60~6ec861ab59e7~91c6 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | IssueTable is the file’s only type, so it has the file-size jobs; the earlier class FALSE-POSITIVE contradicted this register and is not carried forward (851 to 859 lines). |
| 569 | long-member | static MergeSkip(SqliteConnection,SqliteTransaction,string,long,string | Record why it cannot be split | Record both side members and their single path-level issue as one reconciliation. |
| 16 | long-member | static Raise(SqliteConnection,SqliteTransaction,string,IssueKind,TextK | Record why it cannot be split | Record continuation/protection/refresh/replacement as one precedence-ordered raise transition. |
| 7 | file-size | file-size | Split by job | Raise/close lifecycle, paged listing snapshots, skip-member reconciliation, unreadable queries, upgrade backfill, retry-episode lifecycle and invalid-job projection are distinct jobs; header :6 is a grouping. B-13 adds exact-notice SetIgnored to the raise/close lifecycle (857 to 865). 0.3.80.1 (B-93): Close :88-101 closes by canonical path key and the closure-key read :441 returns canonical keys, both in the raise/close lifecycle (865 to 869). |

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

<!-- spec-source: d88664ad1649~b5908bc27828~78444423a238~afcda471cd17~fe0b6747d96b~9544 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 14 | class-size | class-size (802) | Split by job | SPLIT-PENDING: recovery custody (ILocalRecovery) -> LocalRecovery, needs PairSession composition. Current jobs: checked content/stage/publication :29-309; folder/removal/move effects :312-398; stage ownership and checked identity/removal proof :401-535; pending resolution/recovery-stage custody :538-684 and :695-722; shared hashing/page spool/stale-stage cleanup :687-692 and :727-777; root/drive evidence :782-814. Pending-removal ownership moved to LocalRemoval; shared file proof to LocalFileProof; selected recycle custody to LocalRecycle. The Host composition owner must finish the recovery split at the scheduled cleanup-only 0.3.83 trigger; concurrent PairSession changes prevent it here. No automatic product wait/queue is added. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: Host composition/LocalSide structural lane; destination: this row and src/SyncShark.Engine/Local/LocalRecovery.cs with PairSession composition. Closure requires extraction and a fresh cross-family verdict; if composition remains blocked, the release gate retains this finite obligation, not an automatic product wait. |
| 1 | file-size | file-size (815) | Split by job | Same current jobs and recovery destination/Host composition collision/0.3.83 clearing trigger as the class row. Frozen/pre-split/current physical file lines: 912/925/815; historical growth +13 is retained for independent review. The existing class/file SPLIT-PENDING obligations remain open; refreshing this description is not semantic clearance. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: Host composition/LocalSide structural lane; destination: this row and src/SyncShark.Engine/Local/LocalRecovery.cs with PairSession composition. Closure requires extraction and a fresh cross-family verdict; if composition remains blocked, the release gate retains this finite obligation, not an automatic product wait. |
| 464 | long-member | ReadByIdAsync :464-508 (45) | Split by job | Native lookup :465-477, representative-link choice :480-497 and result validation/projection :498-506; representative-link choice is separable and belongs to the pending LocalSide cleanup split. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: LocalSide structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Local/LocalSide.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

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

<!-- spec-source: 0d2c5c4531e0~fc52084ce2df~48dfd7a16fe5~517e5220c3d0~a09e58fce9b6~d57b -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 89 | long-member | FinalizeFirstRunFolderAsync :89-190 (102) | Split by job | Lock footprint :99-106; stale retry and settlement :107-111, :173-185; first-run deletion decision :123-156; admitted execution :157-171. Deletion decision is separable from retry lifetime. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: FirstRunFolders structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/FirstRunFolders.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |
| 9 | long-member | FinalizeFoldersAsync :9-66 (58) | Record why it cannot be split | Current deepest-first folder barrier schedules finalization, gathers failures and propagates unfinished ancestors before the next depth. Existing reason obligation is retained, not newly adjudicated. |

## src/SyncShark.Engine/Executor/FolderSync.cs

<!-- spec-source: 3b0c31423781~16cd010d2747~64de6163603b~3cd3100d3970~0ce46a40618f~1a7a -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (485) | Split by job | Current plan preparation/freeze and unfinished-map custody :17-134; follow-up/survivor/deferred completion :137-246; refused-survivor dependency-cycle diagnosis/reporting :248-289; settlement/group execution :291-317; exclusion/chosen/live/vacated/replacement routes :319-450; live-plan sequencing :452-490. Cycle diagnosis remains separate from unfinished-map custody, preserving the historical split obligation. B9 migrates item preparation/execution/rename calls to their owners. |
| 1 | file-size | file-size (491) | Split by job | Same current jobs and retained split obligation as the class row; the file holds FolderSync alone. |
| 361 | long-member | RemoveVacatedAsync :361-435 (75) | Split by job | Prior-operation recovery :365-374 is separable from fresh admission :375-396 and PrepareAsync validation :398-434. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: FolderSync structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/FolderSync.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Planner/Decide.cs

<!-- spec-source: c282bf5ddb44~1222da0a1375~ca86e2534359~94fe42344b78~cdc96814b349~2222 -->

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

<!-- spec-source: ccf10083ad0b~24a3b33a7e90~959d0d96db0f~43e2daf69f44~55c89592eb6b~4052 -->

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

<!-- spec-source: ee40773167db~284f11ab0466~aa5b8eb5a81e~607df9c3808a~098a176e35a7~d15e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 25 | long-member | BuildCoreAsync :25-94 (70) | Record why it cannot be split | Current disposable plan acquisition/error mapping :28-40; capture and inventory-failure translation :42-53; delegated child classification :55-65; survivor branch selection/completion/readability :67-80; failure disposal with combined primary/cleanup error :82-93. ClassifyAsync :111-145 owns candidate classification; ItemFailures.InventoryFailure owns incomplete-inventory translation. This refresh records current owner calls without changing the existing reason verdict. |

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

<!-- spec-source: 504c240e40b1~e5f814a556d8~a9e6774a4373~a76300d53077~95af8b161ee8~0ca1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 31 | long-member | ResolveAsync :31-97 (67) | Split by job | Choice routing :34-43; held refusal :51-55; separable folder-replacement continuation :59-70; root execution :73-76; follow-up collection :77-82 and sync :90-96. Clearing trigger: cleanup-only 0.3.83 structural recheck; owner: ResolutionRunner structural lane; destination: this row and the extracted job owners in src/SyncShark.Engine/Executor/ResolutionRunner.cs. Closure requires extraction and a fresh cross-family verdict. Coupled composition changes prevent extraction here; if they remain blocked, the release gate keeps this finite obligation open, never an automatic product wait. |

## src/SyncShark.Engine/Executor/Transfers.cs

<!-- spec-source: 611b1d8ad864~8b0d89b4edc0~13f5a1ce9746~52b9a90b656b~06af119ab763~d83e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 99 | long-member | StageAsync :99-148 (50) | Record why it cannot be split | Current stage loop initializes source/attempt custody :102-113, reserves and opens/stages the source :115-122, closes source views and settles the attempt before retry/error :124-137, releases the reservation :139-143 and completes the successful attempt before returning borrowed source views :144-146. The existing reason obligation is retained. |
| 38 | long-member | CopyAsync :38-78 (41) | Review pending | Current stage-to-write orchestration preserves publication callbacks and pending NoEffect settlement, clears unusable unmatched observation, and disposes the stage on every exit. The new over-trigger unit awaits an independent semantic verdict; this writer description does not provide one. |

## src/SyncShark.Engine/IssuePolicy.cs

<!-- spec-source: 5e2a0c94a90f~0e718e2b72cf~6ce724ff8b2f~4fba2e14b6de~2374358cbb61~1109 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 206 | long-member | Obsolete :206-259 (54) | Record why it cannot be split | One issue/scope evidence set decides retirement by ordered predicates. SameStaleEvidence :178-182 now compares validated previous/current digests without granting null-current authority. |

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

<!-- spec-source: d573967f7532~254517a644b8~02c6a7d602f9~61b54dae1fac~855210bfe811~69e9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | static Read(string,Action<string, bool>,int,CancellationToken,out bool) 57 | Split by job | Filesystem open/read/error ownership differs from incremental BOM/newline record framing. |

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

<!-- spec-source: 63e99df7b759~e06c0f2ce00a~d159873ac635~b54d00cd1156~51a4e27f77c4~b2da -->

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

<!-- spec-source: 1aac7e9edb4a~6236136e9432~28298085c753~a8d3a34dbc8c~5dc78f10f474~85dc -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | file-size | file-size | Record why it cannot be split | Record shared tag-based refusals, interpretation and safe diagnostics as one error classifier. |

## src/SyncShark.Engine/Dropbox/DropboxFeed.cs

<!-- spec-source: 2b0aeaf803a1~78bdd8382ef1~e0fe3f203c40~8ff926d52f8f~eaf26a2fee3d~ccf5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Record why it cannot be split | Record installation, continuation, longpoll and reconnect as one cursor/handoff lifecycle. 0.3.80.1 (B-64): the finite-refusal flag _refusedCatchUp :83, set at :40 and consumed once in Accepted :355-362 through the existing WorkCommitted wake, is part of that lifecycle (366 to 375). |

## src/SyncShark.Engine/Dropbox/DropboxReply.cs

<!-- spec-source: ec501a7920b6~a6752380e217~bf21e63a263c~ba1746afc576~4371a14d69d7~ced1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 312 | file-size | file-size | One type per file / name matches type | DropboxReply.cs holds two top-level types: DropboxReply<T> :10-309 and static DropboxReply :312-659; checker merges them by name |

## src/SyncShark.Engine/IStateStore.cs

<!-- spec-source: d1f8696403a2~a78a2c2f794c~1cd0ada16ef7~3ad538beb7ea~3dd25e3edbaf~c55f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Split by job | IStateStore.cs:5-340 spans the same pair/work/pending/retry/folder-removal groups both reviews require StateStore to split along; owner decision 30 (docs/design/owner-decisions.md:101) is the interface-split precedent. 0.3.79 adds ReadLiveRefusal and expected-notice parameters (324 to 334). 0.3.80: the cut-over deletes SaveImportedPair; BeginSelection now answers admission and SelectionBaseline, WorkAttempted and RequireDiscovery are added (334 to 340). |

## src/SyncShark.Engine/PathNames.cs

<!-- spec-source: 5c0bafa3ce34~6bf2c38d5454~db77013332a0~0e40296aecba~b3962a100a9d~34dc -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | file-size | file-size | Record why it cannot be split | Record one frozen path-identity/key facade whose derived operations reuse canonical comparison. 0.3.80.1 (B-92): SameName :127-129, the one leaf-spelling compare (Decide.cs:103, LocalSide.cs:462); 318 to 322. |

## src/SyncShark.Engine/Run/PairSafety.cs

<!-- spec-source: a8aaa5699529~7b353cabd755~a5ba44ee66f6~faf7818bd0a9~3181cedf01a0~1ba1 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | Same jobs as file-size; the class passes 300 lines (310) in 0.3.79. |
| 9 | file-size | file-size | Split by job | Root identity comparison/refusal is separate from execution entry admission (lock footprints, live/recovery/selected validation); header :8 names a grouping only. B-13 adds a third job, the reviewed-notice token (IssueFingerprint/ForReview :284-297), whose natural home is Issue beside Issue.Matches (306 to 318). |

## src/SyncShark.Engine/Storage/StateSchema.cs

<!-- spec-source: 3759097992ae~60345ea8aeaf~417a544c8972~8ce9b14d429f~e9908b21dd9a~c111 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 6 | file-size | file-size | Record why it cannot be split | Record one declarative SQL/schema catalogue, without execution or transaction policy (reason :3). #90b adds schema version 11 and the published-USN columns and statements; B-21 adds the batched deleted-name insert (1433 to 1450). 0.3.80: the cut-over deletes the pair-import SQL; schema version 12 adds the discovery_required work column and its two statements (1450 to 1420). 0.3.80.1 (B-93): schema version 13 adds IssuePathKeyIndex :50 and its upgrade version :13, serving the existing SelectIssueByPathKeyKind :264 (1420 to 1424). |

## src/SyncShark.Engine/TextKey.cs

<!-- spec-source: 6f35a04f69f6~0ec41d224234~4251b2f83c76~29981e013f33~674f99a99f07~6076 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 3 | file-size | file-size | Record why it cannot be split | Record one strings-catalog identifier enum; arbitrary splitting would fragment that contract (reason :3). 0.3.80: the cut-over removes four import keys; adds the dependency-cycle, changed-retry and Retry-runs-Repair keys and 14 folder-browser/storage keys moved from the app into this catalog (now 441 lines). |

## src/SyncShark.Engine/Executor/TaskSet.cs

<!-- spec-source: 1899bffdf5b2~50016fd4438b~d654994762e8~00bb06dcc75d~a08830b5c626~f4e5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 40 | long-member | Add(Func<Action, Task>) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 106. |

## src/SyncShark.Engine/Local/JournalSource.cs

<!-- spec-source: ba10c3d37d93~d7308dfd9584~11502b102efd~e64c95621ed8~9366db39c5b0~c856 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 226 | long-member | CaptureFiniteAsync(IReadOnlyList<Member>?,CancellationToken,Action?) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 110. |
| 336 | long-member | ReadPageAsync(bool,CancellationToken,List<Member>?,ulong?) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 111. |
| 482 | long-member | TryPublish(JournalPosition,IReadOnlyList<Member>,IEnumerable<(string PairId, ChangeNotice Notice)>,IReadOnlySet<string>?,List<Member>?,out bool) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 112. 0.3.80.1 (B-64): 48 to 56; the once-only recovery wake :530-534 runs after the durable commit under the same gate, as reason :480-481 describes. |
| 3 | file-size | file-size | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 208. 0.3.80.1 (B-64): 600 to 615 for the finite-refusal flag :36, RefuseCatchUp :596-600 and its consumption in TryPublish and CommitRead :470. |

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

## src/SyncShark.Engine/Executor/Syncer.cs

<!-- spec-source: 80d926f223eb~2c41f151c91a~0ab241ea968c~c2ce7d82ea82~9972d25d4097~d391 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 166 | long-member | SyncSelectionAsync :166-241 (76) | Record why it cannot be split | Current pending-chain setup :169-177; ancestor/rename-prerequisite traversal and pass execution :178-231; stale-refusal containment :234-240. Rename prerequisite refusal calls SelectionRenames.RefusePrerequisite through pass.RenameWork :195/:202/:213/:227. Existing reason obligation remains unchanged; this describes the migrated owner calls and current chain, not a fresh semantic verdict. |

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

<!-- spec-source: 718239aaf0b2~b5b2a141a1df~fc4d6cdcc231~6a7ca03b5a29~d7c3b77002b2~14f8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 50 | long-member | PrepareAsync (54) | Split by job | PrepareAsync (54): planner preparation/destinations, proof custody, deletion evidence and hash acquisition remain separate jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ItemPreparation structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ItemPreparation.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/SelectionBranches.cs

<!-- spec-source: 64b63e47689d~31f431040661~e54b76d79d68~c39bb67ac928~8b9f6ae87a33~e3e5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 25 | long-member | EnumerateBranchesAsync (111) | Split by job | EnumerateBranchesAsync (111): primary identity acquisition :30-61, old path :63-70, discovery :71-90, walk :91-100 and unreadable continuation :101-133 remain separate jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: SelectionBranches structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/SelectionBranches.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Executor/ItemFailures.cs

<!-- spec-source: 8eae40be40e3~a07c1654fea1~2d993fc0b052~cad8d22d0ed4~642784e6f5cb~07e0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 64 | long-member | FailCoreAsync :64-110 (47) | Split by job | First-run decoration :68-69; filter/cancellation :70-72; issue mapping :73-77; occupant read/error fallback and subject enrichment :79-99; change decoration :100-101; retry dispatch :102-107; emit/outcome :108-109. Occupant enrichment is a separate provider-I/O job. Clearing trigger: the cleanup-only 0.3.83 structure pass after coupled storage, runner and Host seams land; owner: ItemFailures structural lane; destination: this row and an extracted occupant-enrichment method in Executor/ItemFailures.cs. The release review keeps this finite code obligation open until extraction and a fresh verdict; no product queue or automatic wait is introduced. Confirmed at source 452b371f7 (47 lines); restamp at integration once coupled issue-output changes land. |
