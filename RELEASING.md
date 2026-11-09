# Releasing Sync Shark setup

1. Publish the exact reviewed application revision for Windows x64. Keep source, credentials and signing material out of the release repository.
2. Run `tools/Build-SyncSharkSetup.ps1 -PublishedApp <published-folder> -OutputDirectory <new-output-folder>` through the release build. The embedded setup shell excludes Discovery, Explorer, legacy updater/setup binaries, tools and symbols. Its explicit manifest is `asInvoker`; it never requests elevation.
3. Run the setup transition, standard-user and Core `--setup-update-checks` fixtures on public GitHub Actions. Local test execution is not part of this release procedure. Final-candidate positive and mutant-negative evidence is required before acceptance.
4. The protected signing owner signs the final `SyncShark-Setup-vX.Y.Z.exe` with the **package** identity in `tools/SyncShark.SignerIdentity.json`. The updater preserves the Resource Shark house verifier: exact subject/thumbprint/SPKI, embedded Authenticode signer and timestamp, isolated signer UntrustedRoot exception only, timestamp-signing EKU and trusted timestamp chain. Signing and publishing remain separate authorized actions.
5. Publish those exact bytes as the sole matching setup asset on a non-draft, non-prerelease `vX.Y.Z` release in `massdex/sync-shark-releases`. The release API must supply the exact asset URL, positive size and post-signing `sha256:` digest.
6. Check using `SyncShark.exe --cli setup check`; apply using `SyncShark.exe --cli setup apply --update X.Y.Z --yes`.

## Program installation only

Setup uses Windows PowerShell 5.1 with a **process-scoped** `-ExecutionPolicy Bypass`; it changes neither user nor machine policy. Managed policy still takes precedence. The application's .NET runtime must be installed or included in a self-contained publish.

Program files live under `%LOCALAPPDATA%\Massdex\SyncShark\versions\<version>`. The existing stable `active\SyncShark.exe` contract is preserved. Existing `active` junctions are retargeted through one held-handle `FSCTL_SET_REPARSE_POINT` operation, not removed and recreated. Windows documents modification of an existing reparse point with the same tag in [FSCTL_SET_REPARSE_POINT](https://learn.microsoft.com/windows-hardware/drivers/ifs/fsctl-set-reparse-point); the [mount-point buffer](https://learn.microsoft.com/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_reparse_data_buffer) supplies the target. This is not a claimed elapsed-time or power-loss durability guarantee; interruption behavior requires native CI evidence.

Setup never opens, initializes, backs up, moves or restores user data. The existing `product-root.txt` authority and runtime move/recovery implementation remain unchanged. Setup never stops or starts the host and never rewrites Windows startup registration. A running host keeps its existing version; the new version applies at the next start. Startup continues to use the stable launcher. Direct setup refuses versions not newer than the resolved active predecessor.

## Transaction and cleanup

An exclusive `versions\setup.lock` serializes installers. Before publishing a version, setup flushes a journal containing the held roots' identities, payload identities and hashes, exact predecessor junction bytes and candidate junction bytes. Failure before commit restores the exact predecessor with the same-handle retarget operation and deletes only verified owned payload objects. Changed or foreign objects stop recovery with a concrete retained-evidence error. No recursive deletion follows a reparse point.

The next setup invocation replays an interrupted journal before accepting a new transaction. A committed transaction verifies its installed graph before consuming the journal; an uncommitted transaction restores the predecessor and removes its owned destination so the same version can be retried. Preparation lives under `versions\.staging`; under the exclusive claim, the next invocation drains interrupted preparation and prints every removed path. Obstructions are explicit failures, not silent automatic waits. Existing installed versions are retained; this installer does not prune them.

The updater removes its GUID download immediately after a download, verification or launch failure. A detached janitor removes it after a launched setup exits. Cleanup failures name the retained folder on diagnostic output and in `%TEMP%\SyncShark-Update-cleanup.log`; a log-write failure is also reported. Never mistake successful setup launch for completed installation.

## Legacy channel

`updates check/apply` and `SyncPublicUpdates` remain live for their existing GUI/bridge consumers until their separate retirement change. Their protected installer and registration semantics are not changed by this setup lane. The house setup verifier is not silently replaced by `SyncSharkUpdaterSignature`, whose PE/MSIX identity coupling is a different contract.

Signed installed acceptance, including package signature and timestamp verification on final published bytes, remains a release gate separate from synthetic public CI.
