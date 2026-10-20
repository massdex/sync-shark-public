# Google Drive provider

## Readiness vocabulary

This document separates four readiness levels, because they are not
interchangeable: **source** (the code exists in the repository and says what it
does), **protocol** (the exact request and refusal decisions are exercised
against controlled or synthetic endpoints), **live** (the real Google service
accepted or refused a bounded operation) and **installed** (the signed installed
product contains and exercises that source). Retained evidence is cited at its own
layer and date and is never presented as a fresh live or installed acceptance.

The registered Desktop OAuth public ID is bound to Custodian's verified
17 September 2026 replacement; the client payload is in the existing protected
Windows target. The provider audience is External/Testing, not publicly verified.
Account consent and bounded live Google transfer acceptance remain pending, and no
section below claims them.

## Source readiness (current repository)

- Account connect/reconnect uses PKCE, the existing loopback listener, protected
  Windows Credential Manager custody and stable per-account identities.
  Wrong-account reconnect, duplicate connection, partial credential failure and
  cancellation preserve existing accounts.
- UI and CLI select a provider and account. Browsing retains full paths and paged
  search; incomplete results or ambiguous names cannot authorize a new folder.
- Ordinary local pairs bind Google folder IDs and the existing product Recovery
  layout. Edits retain pair/control identity; stale policy and overlapping roots
  are rejected.
- Inventory, create-only uploads, verified downloads, range reads, resumable
  creation and changes cursors use the Drive v3 API. Account/corpus identity is
  retained with each cursor.
- The real planner and runner use Google whole-file SHA-256 instead of Dropbox's
  block digest. Retained revisions bind both the stable file ID and Google's
  per-file version. Same-counter replacement files cannot inherit authority from
  the original object.
- Recovery copies preserve prior bytes before replacement. Permanent deletion is
  unavailable.
- In-app Google sharing is implemented. The account sharing client exposes list,
  create, change and revoke, and the Shared Link editors offer Google the public
  and organisation-domain audiences and the Google link-permission role while the
  Dropbox-only audience, expiry and password fields stay hidden. File requests
  remain explicit refusals rather than an emulated Dropbox feature.
- Google revision browsing is implemented for binary files through the retained
  revision listing, and the provider-neutral Recovery surface loads it for the
  selected pair's own provider. Google-native documents are opened in their own
  editor for document version history instead.
- Storage, online-only and selective-sync policy is provider-neutral rather than
  a Google branch: the pair carries the Files-On-Demand and cloud-only storage
  state, the Windows projection is the shared Cloud Files layer, and the Storage
  page exposes the same online-only and selective-sync controls for every
  provider.

## Protocol readiness (controlled endpoints)

- Existing-file upload, rename/move and file trash use a guarded implementation:
  Drive v2's ETag carried on an If-Match request, checked against the reviewed v3
  identity before sending. An identity drift, an absent or weak validator, or
  HTTP 412 is a refusal and never a blind retry. Successful content updates
  require exact ID, size, checksum and advanced-version readback.
- The current source enables that guarded path in the production capability
  profile and records at its capability definition an actual Google acceptance of
  a current PUT/PATCH and of a stale-write refusal with no retry, dated
  17 September 2026. That entry is the source's own retained checkpoint for that
  date: this document neither re-runs it nor presents it as fresh live acceptance.
  The conditional-write refusal text is emitted only while the capability is off,
  so the ordinary profile does not refuse these operations.
- Controlled endpoint tests cover the accepted-current and rejected-stale
  decisions, but a synthetic server does not prove Google's own server semantics.
  Folder subtree trash remains refused, because a folder version does not protect a
  concurrently added descendant. File trash is recoverable; permanent removal is
  not implemented.

## Live readiness

Not established by this document. No retained live result in the reviewed scope
proves live consent, live transfer, live sharing create/change/revoke, or a live
stale-write rejection recorded after the 17 September 2026 checkpoint. A bounded
live Google verification of upload, download, conditional update and stale-write
refusal remains part of release closure, and until that is recorded no layer of
this document may be described as live-verified.

## Installed readiness

Not established. Whether the installed 0.3.38 build contains the current Google
sharing, revision, conditional-write and provider-neutral storage source reviewed
here has not been verified, and rollback 0.3.37 is retained. Nothing here is
installed-product acceptance or visual approval.

## Explicit limits

- Duplicate sibling names, case collisions and Windows-invalid paths require
  resolution; no object is silently chosen or renamed.
- An incomplete search is not absence. A removed change or 404 can mean lost
  access and never by itself authorizes deletion.
- Google-native documents and shortcuts are classified rather than treated as
  ordinary downloadable files. No automatic export, shortcut following or format
  conversion occurs.
- Shared drives require an explicitly enabled account capability; they are not
  silently treated as My Drive.
- In-app file-request management and account-wide rewind remain unavailable for
  Google Drive accounts. Unsupported commands explain the provider limit rather
  than reporting a Dropbox connection error.
- Dropbox file requests are provider-specific; they are not emulated for Google
  Drive.

## Protected activation and acceptance

Credential Custodian owns the one registered desktop client and protected local
client payload. The public ID is bound in source; private bytes never enter source
or reports. Account refresh tokens use the registered per-connection WinCred
targets; access tokens remain transient. The approved scope is full Drive access,
with no billing or spending. Public OAuth-app distribution/verification is a
separate provider readiness question; local tests are not evidence of public
verification.

Before release closure: finish client binding and actual consent, verify bounded
live upload/download/update and stale-write rejection, then freeze and sign one
combined CLI+Google package and exercise the actual installed UI/CLI while
retaining Dropbox state and the prior installed release.

## Development evidence

GoogleDriveChecks, GoogleDriveOAuthChecks and GoogleDriveAccountFlowChecks exercise
production parsers, account coordinator, browser, adapter, planner, executor and
runner. SyntheticDriveServer supplies external HTTP responses only. Tests include
exact bytes, Recovery retention, account isolation, ambiguity, pagination,
cancellation, checksum failure, provider errors and guarded concurrent-write
rejection. The retained 19 September 2026 native Cloud Files evidence records real
Windows CFAPI registration, verified hydration, pin, dehydrate, reconnect and
hydrated unregister for a Google-bound root with no network call and with
synthetic provider bytes, so it is native-layer proof at the provider-neutral
storage layer and not live Google acceptance. Installed and live proof is recorded
separately and cannot be inferred from these fixtures.

