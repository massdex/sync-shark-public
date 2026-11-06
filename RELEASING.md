# Releasing Sync Shark setup

1. Publish the exact application revision for Windows x64 using the existing product build. Keep source, credentials and signing material out of the public release repository.
2. Run `pwsh -File tools/Build-SyncSharkSetup.ps1 -PublishedApp <published-folder> -OutputDirectory <new-output-folder>`. This adapts Liminal's single embedded-resource setup shell. Discovery, Explorer, old updater/setup binaries and symbols are excluded. The shared application assembly still contains dormant GUI code; setup never launches it.
3. Run the Core tests with `--setup-update-checks` and `pwsh -File tools/Test-SyncSharkSetup.ps1`. Tests use synthetic JSON and fixture folders only.
4. The owner signs the final `SyncShark-Setup-vX.Y.Z.exe` separately with the product-specific **package** identity in `tools/SyncShark.SignerIdentity.json`. Do not reuse another application's signer. The updater requires embedded signer and timestamp certificates, exact package subject/thumbprint/SPKI, and a trusted timestamp chain. Its only chain exception is the registered signer UntrustedRoot boundary; other errors fail closed.
5. Publish that exact setup as a non-draft, non-prerelease `vX.Y.Z` release in `massdex/sync-shark-releases`. Check the latest-release API supplies exactly one matching setup asset and its `sha256:` digest, computed after signing.
6. Check with `SyncShark.exe --cli setup check`; explicitly apply with `SyncShark.exe --cli setup apply --update X.Y.Z --yes`.

## Integration prerequisites

The private `--core v2 --initialize-store <root>` switch calls the product-owned `DataLocationSetup.Initialize`. Initialization creates and validates the store before registering its location; failed initialization stays retryable. A killed initialization that left only empty store files is reclaimed on the next run; leftover non-empty files are named in the refusal with the action to remove them and retry.

The embedded setup script is launched as `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File setup.ps1`, so the process-scoped override applies only to that one setup process and no machine or user policy changes (owner decision D-1106-10). Owner-signing the script does not satisfy a `Restricted` policy: `Restricted` rejects every script file, signed or not.

## Installation and recovery

Setup uses stock Windows PowerShell 5.1. The published application's .NET runtime must be available (or included by a self-contained publish). Setup installs under `%LOCALAPPDATA%/Programs/SyncShark/versions/<product-version>` and writes `SyncShark.cmd` beside `versions` as the CLI launcher. Existing versions, backups and failed staging are retained, never pruned automatically.

With no registered HKCU `Software/Massdex/SyncShark/DataLocation`, setup adopts a store the product reports valid at the old data pointer, otherwise one at `C:/SyncShark/Data`, and registers that location. Only when neither holds a store does setup initialize `C:/SyncShark/Data`. A registered location whose store is missing or unusable is refused; an existing value is never replaced. Setup does not relocate user data.

Setup gracefully stops host and supervisor, confirms no remaining product process, and verifies copies of `state.db` and present WAL/SHM sidecars before switching the launcher. It starts through the product host readiness handshake (which performs the product's own migration), then checks host identity/status, pair preservation and lifecycle failures. Start With Windows stays disabled if absent; an existing enabled registration follows a successful upgrade.

Before starting the candidate, a failure restarts the old running version. **After candidate start is attempted, recovery is roll-forward only.** On health failure, setup requests graceful stop of the candidate and keeps the launcher on the candidate. Preserve the database, backup and both version folders; fix the reported cause and start/check the candidate, or install a fixed newer release. Never replace the database with an empty store or automatically restore the pre-upgrade backup. A process that cannot be confirmed stopped is never killed.

The GitHub updater is adapted from Resource Shark's stable-release parser, digest-checked download and pinned Authenticode pattern, without its extra metadata protocol or runtime state journal. Signing and publishing remain separate owner actions.

## Retention and follow-ups

Setup removes its own extracted payload and failed staging when it exits. `setup apply` removes its `%TEMP%/SyncShark-Update-*` download folder: immediately on a failed download, verification or launch, and through a detached janitor once the started setup exits. It lists retained versions and backups with their paths: manually remove an old version only when no launcher, startup registration or process references it, and remove a backup only after recovery is no longer needed. Never remove the active data directory.

`src/SyncShark.Core/SyncPublicUpdates.cs` is the superseded ZIP/PowerShell update protocol and a removal candidate; `SetupUpdates.Repository` owns the new setup channel. Remove the old protocol with its old callers in the separate retirement lane.

Signature consolidation remains a follow-up: `SyncSharkUpdaterSignature.Verify` couples MSIX format to package signer identity and PE format to publisher identity, with a 64 MiB inspection cap. Reusing it for a package-signed embedded-payload EXE needs a reviewed format/identity separation. Directory-lease hardening of the GUID download remains a follow-up; the downloaded file itself is held against write/delete through verification and launch.

Backup admission requires store bytes plus max(10% of store bytes, 256 MiB). This approved installation-headroom choice is a policy, not a measured throughput or storage guarantee; real-store measurement remains outstanding.
