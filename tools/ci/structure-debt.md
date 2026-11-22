# Structure debt register

Owner: docs/v2-engineering-standards.md §1.3. Source: spec-ratchet review of rc/0377 3f6745416 (226 structural findings,
adjudicated 2026-11-06). Every row is a confirmed violation still open.

**Rule (AGENTS.md):** a change that edits a file listed here clears that file's rows in the same change (split, record
the reason, or fix the type), or records in its review why a row cannot be cleared yet. Clearing a row deletes it here.
Rows are never dropped any other way. The CI register check parses only the `## src/…` sections below; a change to
a file listed in one of those sections without updating it fails the check.

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
| `tools/ci/check-spec-ratchet.py` | Source findings 84-357 (literal counters 117-179, declaration inventory 182-299, file assembly 302-338); baseline/comparison pipeline 360-498; CLI orchestration 501-517. | SPLIT-PENDING: literal classification is a separate job with two consumers. Retained during O3: the SQL permission heuristic is removed and both scans consume the literals-owner artifact; the remaining heuristic classifier/inventory split is separate step-5 work. Owner and destination: refactor stream A, structure refactor step 5 (shared inventory). Trigger: step-5 inventory work; integration retains this finite obligation if that work is not scheduled. Clear after the classifier has one inventory owner, both consumers use it, and enforcement fixtures and cross-family review pass. |
| `tools/ci/spec_cslex.py` | Mask literals/comments and exact UTF-8 token spans 12–191; delimiter matching 194–222; parsed member representation 232–248; list/signature parsing 251–406; type/member parsing 409–529; source coordinates 532–535. | These stages implement the existing offset-preserving C# lexical parser. They share masked-source offsets and one member representation; callers consume this parser as one module, while structural decisions remain outside it. |
| `tools/ci/test-spec-ratchet.py` | Synthetic source/artifact fixtures 22-39; public checker contracts 42-605; mutation controls 608-753; pinned-release and artifact entry-point contracts 757-1064 (optional-policy cases 832-861); unittest entry 1067-1068. | SPLIT-PENDING: trusted-release cases have their own fixture, separate from the structural contract and mutation controls. Retained during the CI selection change to reuse the pinned-release fixture for both optional policy paths, with separate history and pins per case. The scan-protocol suite split remains structure refactor step 5 work; no fixture setup is duplicated. Owner and destination: refactor stream A, structure refactor step 5 (inventory enforcement suites). Trigger: step-5 suite rewrite; integration retains this finite obligation if that work is not scheduled. Clear after fixtures are split by contract without duplication or lost coverage and cross-family review passes. |
| `tools/Test-PreReview.ps1` | Help and exact-export artifact prerequisite 1-127; native/Git execution 139-189; documentation drift 191-285; ratchet reporting/completion 287-372; citations 374-522; ownership 524-557; repeat diagnostics 559-582; check dispatch 584-606; binary freshness 608-634; assertions 638-642; private real-table controls 644-702; hosted self-test 704-1371; command dispatch/orchestration 1373-1696. | SPLIT-PENDING: CI selection and registry parsing now live in LaneSelection.ps1 and their copies are deleted. Real-table controls run privately before every helper export; hosted self-tests never read excluded documentation. Both entry points reuse the same assertions and command helpers. Citations and fixture construction remain separable jobs outside the approved CI-selection extraction; preserve their loading and self-test entry contracts at the remaining split. Owner: build hub. Destination: focused modules beside tools/ci/prereview_checks.py, called by this command, and a separate pre-review fixture entry. Trigger: the build hub's pre-review split assignment or tooling integration review, whichever occurs first; the CI-selection extraction retains this obligation rather than claiming the remaining jobs are split. Clear only after the split preserves the hosted positive/negative contract and receives cross-family review; otherwise leave this finite code obligation open, not silently cleared. |
| `tools/ci/prereview_checks.py` | Git/source evidence 59–102; receipt checking 105–129; unit growth 132–208; registration parsing and exemptions 211–300; witnesses 303–335; registration inventory/checking 338–401; CLI dispatch 404–429. | SPLIT-PENDING: receipt/growth adapters and registration/witness analysis are distinct jobs. Retained while the imported gate's registration compatibility and mandatory checks settle; N16 is cleared: namespace-tail extraction now calls spec_file_identity.type_prefix at 226-227, deleting its copied namespace regex. Registration/witness interpretation remains here; the broader split obligation is unchanged. Owner: refactor stream A, structure refactor step 4 (failure evidence). Destination: registration/witness module under tools/ci, leaving receipt/growth adapters and dispatch here. Trigger: the next structural change to this helper, with the obligation checked at tooling integration even without a later change. Clear after extraction preserves all enforcement fixtures and a cross-family reviewer confirms ownership; until then keep this finite obligation explicit. |
| `tools/ci/spec_trust.py:check` | Release/candidate findings and artifact validation 182-194; measured comparison using resolved permissions 195-203. | N7 OPEN: findings and structural scans still lex each tree separately, and identity matching repeats. The nested type-name join also repeats in check-spec-ratchet.py:263 and spec_trust.py:122 (N15). O3 reuses resolved permission offsets across both scans, but does not clear this broader debt. Sharing a per-file scan result is not a few-line correction: it changes the scanner/identity handoff used by both entry points. Owner: refactor stream A, structure refactor step 5; destination: step-5 tools inventory and the shared scan protocol in check-spec-ratchet.py/spec_trust.py. Trigger: step-5 inventory review or the next scan-protocol change, whichever occurs first. Clear only after one parsed inventory feeds both consumers, unchanged enforcement fixtures pass, and before/after work is measured. If that stage is not scheduled, the integration review retains the explicit open obligation; no automatic product wait is involved. |
| `.github/workflows/sync-shark-ci.yml` | Workflow configuration 1-44; GitHub job graph 46-1297; selection, builds, artifact producers and check jobs remain named units. | Retained as the single job graph: each named job owns its execution and each step one action. Matrix/artifact dependencies remain explicit; reusable workflows would add cross-workflow interfaces without separating policy. Selection rules live in LaneSelection.ps1, packing in EngineJobs.ps1, check execution in Invoke-Checks.ps1 and mandatory policy in MandatoryChecks.ps1. |
| Ratchet committed baseline and manifest (O1) | spec_trust.py baseline_findings 91-108 and measured_baseline 154-179; candidate manifest policy visibility 66-88. | OPEN: committed baseline/manifest no longer determine measured debt clearance; they still provide provenance display and edit guards. No behavior change in this round. Owner: build hub. Trigger: build-hub verdict-evidence design or tooling integration review. Clear after their remaining consumers are justified or removed with matching fixtures and cross-family review; retain this finite obligation if that work is not scheduled. |
| Release-relative policy reporting (O2) | spec_trust.py policy_findings 66-88; optional policy archive 33-63. | OPEN: inherited release-relative changes obscure lane-only policy changes. Optional policy inputs are now correctly archived when present; this removes false changes without changing release-relative review semantics. The lane-specific evidence design remains build-hub work. Owner: build hub. Trigger: build-hub review-reference design or tooling integration review. Clear after lane-specific reviewed policy changes are distinguishable with positive/negative fixtures; until then reviewers compare policy files against main. Retain the obligation if that work is not scheduled. |
| `tools/public-mirror/Invoke-LaneCi.ps1` | Entry and defaults 1-46; run timing and Git boundary 48-90; identifier-policy merge admission 92-297; queue lifecycle 299-374; inputs and lane assembly 376-449; tree-based selection and provenance 451-520; private ownership control admission 521-523; dry-run 525-530; export and publication 533-662; run watch 664-683; result/timing report 685-768. | SPLIT-PENDING: identifier-policy merge admission and export queue lifecycle remain separate jobs in this entry script. The CI-selection change delegates mapping and coverage to LaneSelection.ps1 and removes the old manual selection rule; extracting the pre-existing merge and queue protocols is outside this approved change. Owner: build tooling integration; destination: focused modules beside PublicMirror.Common.ps1. Trigger: next helper structural assignment or tooling integration review. Clear after both protocols have one owner and all existing queue/merge fixtures plus cross-family review pass; keep this finite obligation visible if that assignment is not scheduled. |
| `tools/public-mirror/Test-LaneExport.ps1` | Actual helper-stage loading and assertions 1-19; phase-duration contracts 20-23; stage loading 24-26; queue/publication contracts 27-343; full-selection contract 344-354; identifier-policy fixture setup 356-433 and cases 435-632; selection/provenance fixture driver and cases 634-741; result 742-744. | One fixture file runs the helper's actual stages. The cases share stage-slicing and repository setup; splitting them would duplicate that setup rather than separate production policy. Queue, merge and selection decisions remain owned by the helper and LaneSelection.ps1, not copied into fixture implementations. |
| `tests/SyncShark.Engine.Tests/Layout/LiteralsChecks.cs` | Fence entry 19-70; message-catalog policy 72-332; enforcement fixtures 334-514; literal rules and ownership 515-719 (shared string permission 556-573; literal-kind predicate 575-576); coordinates 721-725; repository/type-to-file inventory 727-774; fence source selection 776-789. | SPLIT-PENDING: message-catalog policy and the shared type-to-file inventory are separable from literal enforcement. Required passing/failing fixtures stay with their rules. O3 extracts an existing permission branch and adds producer assertions, not a new policy job; the broader split is retained for structural work. Owner: refactor stream A, structure refactor step 5 (inventory integration lane). Destination: dedicated message-catalog and type-file inventory owners beside this file, with DependencyChecks, OwnershipChecks and SettingsParserChecks calling the inventory owner. Trigger: the next structural edit to LiteralsChecks, with this obligation checked at tooling integration even if no later edit occurs. Clear after both jobs are extracted, all callers reuse their owners, enforcement fixtures pass and cross-family review confirms the split. |

The split-folder integration also extends the existing type-file inventory to resolve exact methods in partial types. Its current jobs are literal enforcement and fixtures (19-725), repository/type-and-method inventory (727-791), and source selection (793-806). The inventory extraction above remains open: this correction preserves one parser and the existing dependency consumers, and does not attempt a separate ownership-tooling refactor. The new partial-method passing/failing fixtures exercise that attribution without granting type-wide permissions.

## src/SyncShark.Engine/Executor/ItemSync.cs

<!-- spec-source: d4e4b83411af~42dbf79d8e08~c7164d1b28ef~a12feebc1191~6f92879df6fa~8e3e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 81 | long-member | RunRowAttemptAsync :81-130 (50) | Split by job | Current registered-attempt envelope captures its shared dirty answer, prepares/settles fast outcomes and routes admitted execution through ItemExecution. Frozen 49 lines, current 50; the structural review keeps its split pending. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: ItemSync structural lane; destination: this register row and its split implementation in src/SyncShark.Engine/Executor/ItemSync.cs. Closure requires the split and fresh cross-family verdict; if the protocol/composition blocker persists, the row stays explicitly open at that release gate, not in an automatic product wait. |

## src/SyncShark.Engine/Dropbox/DropboxEntries.cs

<!-- spec-source: e316cf5bbfe6~b7136cd8b0c0~7415f568088b~ffda94f2f12d~5513c9540603~0f3f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Split by job | Lexical validation, field projection and Dropbox metadata interpretation are distinct jobs. Open: the release feature freeze allows only live-defect fixes. Owner: the structural refactor lane. Clearing condition: that lane's extraction of the unit. Trigger: the first release after that lane lands. |
| 113 | long-member | Read(ReadOnlySpan<byte>,bool,ReadOnlySpan<string>,bool,CancellationTok | Record why it cannot be split | Record one token pump sharing incremental consumption, readiness and final-block state. |
| 238 | long-member | StringByte(byte,ReadOnlySpan<string>) 92 | Record why it cannot be split | Record escape, surrogate and UTF-8 branches as transitions of one incremental string decoder. |
| 156 | long-member | StructuralByte(byte,ReadOnlySpan<string>,bool) 66 | Record why it cannot be split | Record punctuation, container closure and lexeme start as one grammar-frame transition. |
| 7 | file-size | file-size | Split by job | Incremental JSON lexing, selected-field projection and metadata interpretation are distinct jobs. Open: the release feature freeze allows only live-defect fixes. Owner: the structural refactor lane. Clearing condition: that lane's extraction of the unit. Trigger: the first release after that lane lands. |

## src/SyncShark.Engine/Run/RunLog.cs

Current source description (supersedes historical coordinates below; existing split obligations and independent review remain): The file retains canonical formatting, mutable progress/accounting, ordered delivery and sink lifetime. StartOpportunity :218-221 clears only the existing exception-dedup set under its gate when the supervisor reuses a carried log. The unused private SafeProviderTag copy is deleted; both active call sites already use DropboxErrors.SafeProviderTag. File creation, retention and output routing are unchanged; the existing codec/accounting/delivery split remains open.

<!-- spec-source: 4cb9b9e04a98~de8edcd73835~e2c85d71e7f9~d3c85521d7e0~8054053078aa~f47e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size (771) | Split by job | Canonical codec differs from mutable accounting, flushing and subscriber delivery. Grew to 685 with S2 active-branch tracking, to 713 with the 0.3.83 first-failure exception (field :58, accessor :204/:207, remaining sites :627/:636/:655, disk-full rendering :573), to 761 with row side/work-id stage records and the one-per-pass held report, to 767 with the B-145 whole-subject branch identity, to 769 with the component-agreeing partial-branch fallback, to 770 when that fallback was limited to facts that omit part of their identity, and to 771 with the named failure-message separator the literals rule requires. The split obligation is unchanged; this row records the current size. Split plan (extraction not complete): the logging owner first extracts the pure canonical codec/grammar, then the progress reduction/snapshot state, preserving sink lifetime, ordered delivery, canonical bytes, failure capture and the public callers. Destination: the Run module and this register section. Trigger: the next logging correction or structural cleanup gate. Closure: current-source independent review plus the applicable codec/progress/delivery tests. |
| 222 | long-member | Event(RunEvent) :222-331 (110) | Split by job | Progress reduction, canonical accounting and ordered human delivery are distinct jobs. Grew 70 to 87 with S2 branch tracking, to 102 with the row side/work-id stage records and the held-wait wording, to 107 with the B-145 whole-subject branch identity, to 109 with the component-agreeing reducer, which removes only the exact matching subject, and to 110 when a whole-subject miss stopped walking the active list. Same split plan, destination, trigger and closure as the class row. |
| 86 | long-member | ReportProgress(string?,bool) 48 | Record why it cannot be split | Record one quiet observation’s matching diagnostic and sequenced human snapshots. |
| 13 | file-size | file-size (783) | Split by job | Canonical codec differs from mutable accounting, flush lifetime and delivery; 783 lines under the same split obligation as the class row, whose row records the split plan, destination, trigger and closure. |

## src/SyncShark.Engine/Dropbox/DropboxListing.cs

<!-- spec-source: 81cc288314f6~63ef86441219~b8538c764fbc~1ed757f07224~b977b4b3b31d~e4d8 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size (424) | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. Open: the release feature freeze allows only live-defect fixes. Owner: the structural refactor lane. Clearing condition: that lane's extraction of the unit. Trigger: the first release after that lane lands. |
| 27 | long-member | ReadAll(string,ListOptions,ListingReadContext,CancellationToken) :27-141 (115) | Split by job | Retry-owned page acquisition/cleanup differs from traversal spelling, filtering and progress. Open: the release feature freeze allows only live-defect fixes. Owner: the structural refactor lane. Clearing condition: that lane's extraction of the unit. Trigger: the first release after that lane lands. |
| 7 | file-size | file-size (424) | Split by job | Page/item acquisition differs from stateful ancestor-spelling reconstruction. ReadByIdAsync :390-426 (37) is under the figure. Open: the release feature freeze allows only live-defect fixes. Owner: the structural refactor lane. Clearing condition: that lane's extraction of the unit. Trigger: the first release after that lane lands. |

## src/SyncShark.Engine/Dropbox/DropboxWriter.cs

Exclusive creation and positional move submit/check extend the existing folder-mutation job; the upload/folder ownership split remains open for the structural refactor lane under the feature freeze. StageAsync and SettleCohort retain their shared verification and single-feedback lifetimes. This change does not clear the existing rows.

<!-- spec-source: 2d89aed63b81~a0498fec6087~7b7397a2f194~3f00cae6bcd5~90daaf178001~fd11 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 10 | class-size | class-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 27 | long-member | StageAsync(string,Stream,Entry,CancellationToken) 46 | Record why it cannot be split | Record one verified stage sharing hash, offset and terminal session result across upload alternatives. B-70 threads one request attempt through it for the failure disposition (42 to 46). |
| 8 | file-size | file-size | Split by job | Upload staging/publication collection differs from folder mutation/submission. |
| 256 | long-member | SettleCohort(List<PendingPublish>,IReadOnlyList<(Entry?Entry, Exception?Error, bool KnownNoEffect, bool WriteThrottled)>,RequestAttempt) | Verify existing reason (R2) | Confirm or move the existing constraint comment directly above the unit, then record REASON-RECORDED; source row 68. |

## src/SyncShark.Engine/Local/LocalSide.cs

<!-- spec-source: e16cf0d9d284~8f69a169dfa7~feb2a3cda34a~62c31b8f5986~176397989a3e~e9f5 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 14 | class-size | class-size | Split by job | Checked IO, publication/removal, recovery custody and identity/drive observation remain distinct jobs. Held-stage lifetime and publication-boundary helpers extend publication (:74-315); StageAsync keeps the FileStream default buffer through the existing authority (:88-89), so the #90b growth adds no new job. 0.3.83 (#36): the page spool is created as an AdmittedSpool after a creation admission :827-845, +3 lines; checked IO, same split. 0.3.82 B-140A: EnsureFolderAsync creates only in an existing parent through the native owner; same publication job. 0.3.82 B-179: ReadByIdAsync and ReadByIdDocumentedAsync share ReadById (:474-512), +8 lines; identity observation, same split. |
| 14 | file-size | file-size | Split by job | Checked IO (:27-182), pending/publication effects (:184-482), recovery custody (:610-795) and identity/drive observation (:549-607, :852-886) remain distinct jobs; the publication boundary (:268-315) belongs to publication effects. #36 +3 lines in CreatePageSpoolAsync (see class-size). 0.3.82 B-140A: EnsureFolderAsync creates only in an existing parent through the native owner; same publication job. 0.3.82 B-179: the documented-absence by-id read (:474-512) is identity observation (see class-size). |

## src/SyncShark.Engine/Dropbox/DropboxApi.cs

Split-folder delta: route-aware batch feedback reuses AdmissionIdentity and AdaptiveLimiter.ReportBatchOutcome; the public upload-batch entry point preserves its route and null wait. Redundant same-file partial declarations are removed; entry-point signatures, optional defaults and bodies are preserved. Current jobs remain request observation, exchange entry points, retry orchestration, and exchange execution/settlement. The existing transport structural lane owns their extraction under the clearing condition below. Its class/file debt stays open; this source receipt is not an independent size verdict.

<!-- spec-source: aed4272b6c7e~17d7fdd15ba7~9a8ba66ac8d4~aa07667ca564~1af80e32d99a~a102 --> 0.3.94: a 429 on upload_session/start or /append (Exchange.SessionSlice) is classified as no effect, and an in-place replay is allowed only when the body was not consumed; every other route and status keeps its classification. This is not a new reviewer verdict.

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 13 | class-size | class-size (528) | Split by job | Two jobs that predate B-70: execute and settle one Dropbox HTTP exchange (request observation :24-80, entry points :82-212, CopyCompletion :269-275, ConsumeAsync :277-337, release/send/build :339-410, header acceptance and wait publication :412-461, failure decoding and classification :463-525, Exchange :526-540), and orchestrate retries across Request-owned exchanges (the `ExecuteAsync` loop, :214-267). B-70 classifies temporary and unknown writes through the transient-evidence owner DropboxErrors.TransientEvidence (:501); B-38 gives the retry Wait the failed request's RequestId (:232-233, :255) and threads it into ConsumeAsync (:280-282). Both grow the class inside those two jobs. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Dropbox transport structural lane; destination: this row and the extracted owners in src/SyncShark.Engine/Dropbox/DropboxApi.cs. Closure requires the split and a fresh cross-family verdict; if the coupled seams are still changing then, the row stays explicitly open at that release gate, not closed. |
| 216 | long-member | ExecuteAsync<1>(string,object,Stream?,ContentBlockHasher?,DropboxReply<T>,RequestAttempt,bool,CancellationToken,string?,Func<ContentBlockHasher>?,string) :216-267 (52) | Record why it cannot be split | Record replay eligibility, request episode and terminal completion as one retry lifecycle (reason :214-215). B-70 adds the request-retry Wait (:250-255) inside it; B-38 adds the per-attempt RequestId (:232-233) that the Wait carries (49 to 52). Deferred with the structural work outside 0.3.82's serious-reliability-only scope. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Dropbox transport structural lane; destination: this row, the current ExecuteAsync verdict in tools/ci/spec-verdicts.json, and src/SyncShark.Engine/Dropbox/DropboxApi.cs. Close only after independent adjudication records an accepted one-job reason or a completed job split; otherwise keep this row open at that release gate. |
| 11 | file-size | file-size (540) | Split by job | The file holds DropboxApi alone, with the class row's two jobs: exchange execute/settle (:24-212, :277-540) and request retry orchestration (:214-275). The :11 comment names the exchange owner, not one job for the whole file. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: Dropbox transport structural lane; destination: this row and the extracted owners in src/SyncShark.Engine/Dropbox/DropboxApi.cs. Closure requires the split and a fresh cross-family verdict; if the coupled seams are still changing then, the row stays explicitly open at that release gate, not closed. |

## src/SyncShark.Engine/Planner/Decide.cs

Closed rows: class :7-364 (358) and file (364) have the fresh REASON-RECORDED verdict from B-214 implementation review (one ordinary-policy job, reason :5). DropboxIdentityHolds :230-232 is the id-only rule used by UnchangedBoth :178, InSyncIdentityHolds :215-217 and CrossedFar :275-276. DropboxMatchesBaseline :225-228 is the full echo match used by SelectionObservation.SettlesDropboxEcho :12. These cleared units carry no register row.

<!-- spec-source: ee4bf1544133~271567fe2c8e~7355c553aa81~0eb6ea398356~af7359dc882f~d5f0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 73-130 | long-member | NoRecord: :73-130 (58) | Split by job | Retain the carried SPLIT-PENDING verdict: first-run deletion evidence handling :86-98 and ordinary no-record precedence :99-129 are separable decisions; preserve their ordered Table A outcome. Deferred with the structural work outside 0.3.82's serious-reliability-only scope. 0.3.83 trigger expired; retained; replacement trigger: refactor wave G3 (Storage/Executor restructure) or the next growth of this unit. Owner: Part-1 refactor coordinator; destination: this row and the extracted first-run/no-record decision helper in src/SyncShark.Engine/Planner/Decide.cs. Closure requires the extraction with Table A precedence preserved and a fresh cross-family verdict; until then the row remains explicitly open at that release gate. |

Current merged delta: ranges above are descriptive, not a new semantic verdict. The existing dispositions remain; current grown-unit clearance awaits independent review.

## src/SyncShark.Engine/Run/Monitor.cs

<!-- spec-source: ffddbe4ef3f7~f7718eb9fdfc~176e4f0d4ee1~41c7a9c567e4~715ab52c6bfe~9dec -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 7 | class-size | class-size | Record why it cannot be split | One timing authority: wake and deadline registration, earliest-due scheduling, callback lifetime and shutdown; the item wait observation belongs to it. |
| 190 | long-member | RunAsync(CancellationToken) | Record why it cannot be split | Record deadline selection, arming, dispatch and wake consumption as one scheduling loop. |
| 6 | file-size | file-size | Record why it cannot be split | Record one earliest-deadline scheduling and shutdown authority for all opportunity kinds. |

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

## src/SyncShark.Engine/Local/LogFiles.cs

<!-- spec-source: d573967f7532~254517a644b8~02c6a7d602f9~61b54dae1fac~855210bfe811~69e9 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 39 | long-member | static Read(string,Action<string, bool>,int,CancellationToken,out bool) 57 | Split by job | Filesystem open/read/error ownership differs from incremental BOM/newline record framing. |

## src/SyncShark.Engine/Planner/FolderDeletes.cs

Current delete-safety delta: Classify :36-49 treats MovedOut as a survivor fact; FollowUp :108-109 leaves that survivor to its queued rename. Both remain pure decisions over supplied facts. FirstRun :55-98 (44) is unchanged and retains its existing reason; the row below uses its historical start coordinate.

<!-- spec-source: b119470c90f5~5d694891b56f~df44c25024bc~109094bc573a~70422bad04f0~a7f0 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 36 | long-member | static FirstRun(Row,bool?,bool,bool) 44 | Record why it cannot be split | Record one folder decision over the same completed-content and mapped-target evidence. |

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

## src/SyncShark.Engine/Dropbox/DropboxErrors.cs

Current class: :9-401 (393), file 401. WithMove :260-262 changes the cause only for unknown-effect moves; Copy :264-269 preserves the remaining failure fields and defaults to the original cause for provider-wait decoration. These remain the existing classification/decoration jobs; the split below is still open.

<!-- spec-source: 1b53b95f7338~3352754b1e12~2154385bff87~27b06caa9903~f2c46e06b838~0323 -->
Oversized-move and positional move-member classification extend the existing provider-refusal job, including required payload-shape validation. The existing disposition/decoration split remains open for the structural refactor lane under the feature freeze; changing that boundary is outside this adapter change. No debt row is cleared.


| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 9 | class-size | class-size | Split by job | B-70: the write-failure disposition and its decoration (`Disposition` :230, `NoUsableReply`, `Contention`, `WithProviderWait`, `WithMove`, `Copy`) is a sub-job beside refusal construction. Split at the next structure pass; kept whole in B-70 because the design names `DropboxErrors` the one classification owner (317 to 388). Open: the release feature freeze allows only live-defect fixes. Owner: the structural refactor lane. Clearing condition: that lane's extraction of the unit. Trigger: the first release after that lane lands. |
| 1 | file-size | file-size (453) | Split by job | One type; same split as the class row. |

## src/SyncShark.Engine/Dropbox/DropboxFeed.cs

<!-- spec-source: f86983802328~51661fd98381~6be5decce82b~4a94072f929d~96711ac1cb5f~4d3e -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 5 | file-size | file-size | Record why it cannot be split | Record installation, continuation, longpoll and reconnect as one cursor/handoff lifecycle. 0.3.80.1 (B-64): the finite-refusal flag _refusedCatchUp :83, set at :40 and consumed once in Accepted :355-362 through the existing WorkCommitted wake, is part of that lifecycle (366 to 375). 0.3.82 (B-163): the handed-out terminal _handedCursor :86 (set at :43, cleared and woken once at :241-243) and the resumed-turn skip AtTerminal :203 are part of the same hand-back lifecycle (375 to 391). |

## src/SyncShark.Engine/Dropbox/DropboxReply.cs

<!-- spec-source: c633700a7dcd~9375367a1a27~146980fc44c3~ef2099b7312f~ecbdb1d3139b~9d0f -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 312 | file-size | file-size | Record why it cannot be split | DropboxReply.cs now holds one type, DropboxReply<T> :10-312; the static DropboxReply moved out of this file. One incremental JSON reply decode; #36 adds the spool drain delegate (:16, :27, :63) inside that job (+3 lines). |

## src/SyncShark.Engine/PathNames.cs

<!-- spec-source: 9dfb6f0ae89f~fccf64295c9e~7f88a5659016~339cbec26fc8~76326d42101e~d953 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 12 | class-size | class-size (318; :12-329) | Split by job | The file's only type, so the same split inventory as the file-size row below; the :9 comment names the domain owner, not a constraint against splitting (independent review). Split actual jobs, keeping each shared rule in one owner. Same clearing trigger, owner and destination as the file row. |
| 1 | file-size | file-size (329; class :12-329, 318) | Split by job | The :9 comment names one path-identity owner, but the current file holds separable jobs. (a) Loading and validating the embedded frozen case table: CaseTable :17-23, LoadCaseTable :280-304, ReadScalar :306-312. (b) Key and comparison: :25-48 and :54-81, plus the folding helpers SameSpelling/UnderBoundary/FoldNext/UpperAscii :246-278. (c) The dormant spelling-mapping facade :50-53 and :83-103, with saved-parent mapping MappedDestination :140-152. (d) Relative-path grammar :106-138 and :154-217 (Child to HasDescendantKey and IsSelfOrUnder to IsSingleSegment), with the side selector Other at :105. (e) Dropbox root grammar :219-244. (f) Local root normalization :314-328. Job (a) parses a persisted JSON format, and jobs (e) and (f) are each side's root grammar; none of them is key or comparison work. Since e2e26b750 (ed76a395c), HasDescendantKey :192-197 is the one strict-descendant probe over an ordinal key set and reuses DescendantBounds :189-190 and IsUnderKey :163-165; that is grammar job (d), 322 to 329 lines. SameName :127-129 (B-92) is unchanged. Clearing trigger: the cleanup-only 0.3.83 structural recheck; owner: path-identity structural lane; destination: this row and the case-table and root-grammar types split out of src/SyncShark.Engine/PathNames.cs, with docs/design/engine.md's domain-to-owner map updated in the same change (§1.1). |

## src/SyncShark.Engine/TextKey.cs

Current source description: File 1-467 remains one persisted-by-name strings-catalog enum. FailDropboxOutcomeUnknown :39 distinguishes uncertain move outcomes; RequiredRepairRetryNoSpace :466 retains the appended prerequisite vocabulary. No execution or formatting policy is added; older stores cannot interpret new names. Existing catalogue reasoning and independent review remain.

<!-- spec-source: a6619285c079~9d266fc2e78b~0335703133cd~9fceef31d599~39d65a7e69ff~ca2c -->
Split-folder delta: four vocabulary identifiers are appended without changing existing enum values, including the ordinary oversized-move cause. The single-catalogue reason remains subject to current-content review; no structural row is cleared.


| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 3 | file-size | file-size | Record why it cannot be split | Record one strings-catalog identifier enum; arbitrary splitting would fragment that contract (reason :3). 0.3.80: the cut-over removes four import keys; adds the dependency-cycle, changed-retry and Retry-runs-Repair keys and 14 folder-browser/storage keys moved from the app into this catalog (now 441 lines). 0.3.83 (#36) adds RepairStoppedNoSpace and RepairCompletedSecondaryFailure. B-137 adds AutoSyncQueueAtStart (449 lines). Serial/parallel mode snapshots add two catalog keys; the same one-catalog reason stands. |

## src/SyncShark.Engine/Dropbox/DropboxReplyShapes.cs

<!-- spec-source: 063299770418~bc3917866ae5~a173882a6a27~5df9977cf06b~d1d78562e5c5~e705 -->

Move-member decoding extends the existing strict reply projection: route-specific selections and failure classifiers share cardinality and metadata checks. Redundant same-file partial method declarations are removed, keeping every optional default on its implementation; the PageSpool class part remains. This reduces the source without changing decoding. The author receipt records the change only; independent review owns any size verdict. No threshold or earlier disposition is changed.

## src/SyncShark.Engine/Run/AutoSync.cs

<!-- spec-source: 63736b409d44~c7e0ab26e6dd~a7ece80a37bd~f37abe3682d6~5bde50d6b402~0033 -->

| Line | Check | Unit | Action | Finding |
|---|---|---|---|---|
| 137 | long-member | OpportunityAsync(string,(bool Ordinary, bool Receipts),Func<long, RunLog?>,CancellationToken) (60) | Split by job | the admission and item-wait gate (pair execution, admission, live refusal, item wait narrowing) leaves the dispatched session body (run, retry completion, revisit, folder poll). |
