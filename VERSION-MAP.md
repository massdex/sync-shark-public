# Sync Shark version -> source commit map

Every released version and the source commit it was built from.

Evidence classes, strongest first:

1. `MANIFEST` - `<package>-manifest.json` in the package's build output folder (`commit`, `version`, `productVersion`).
2. `DEPLOYLOG` - `deploy-log.json` beside the manifest (`runs[].commit`).
3. `TAG` - annotated tag in this repository (also present on `origin`).
4. `STAMP` - the commit that changed `<Version>` in `Directory.Build.props`.
5. `MSG` - commit subject/body names the version.

`on origin` = the number of remote-tracking branches that contain the commit.

| version | commit (full SHA) | date | evidence | confidence | on origin |
|---|---|---|---|---|---|
| 0.1.0 | f854d0e7eddbb349ae0e2779dd3072fdc68450a7 | 2026-09-01 | TAG v0.1.0 (stamp 313ce736) | exact | yes (285) |
| 0.1.1 | 0aa926d9a179ec0f3429e34cb0449d96c60f4fc0 | 2026-09-01 | TAG v0.1.1 (stamp bb61859b) | exact | yes (284) |
| 0.2.0 | 8cb31ad57b61c6bb9d67e46b2e8ca463959327a8 | 2026-09-02 | STAMP | exact | yes (282) |
| 0.2.1 | c7713490c1c595dcdd3cc3c24cc44ecd0db7db5e | 2026-09-04 | STAMP | exact | yes (282) |
| 0.2.2 | e7f10050b7828c2fa5cb030be08f6088dfa86858 | 2026-09-04 | STAMP | exact | yes (282) |
| 0.2.3 | 33292e60244ed75ec0a7efce639e3d0628be535d | 2026-09-04 | STAMP | exact | yes (282) |
| 0.2.4 | 6acfe801456e52d95682b3987af7024fe4329c58 | 2026-09-04 | TAG v0.2.4 (same commit; also STAMP) | exact | yes (282) |
| 0.3.0 | 5c19c93614ba0bf163a559bc6b197bfeab2a87a5 | 2026-09-08 | TAG v0.3.0 (stamp 917e93ee) | exact | yes (281) |
| 0.3.1 | ebcc740827c8edc1c3ef58faf405f5a5f4baa1c7 | 2026-09-09 | STAMP | exact | yes (280) |
| 0.3.2 | 63c3bd915d60d4d5c1ffe9fd458ea326edaf0e94 | 2026-09-09 | STAMP | exact | yes (280) |
| 0.3.3 | b979455ae4784b478060ee9cc1f7664926b9e3fa | 2026-09-09 | STAMP | exact | yes (280) |
| 0.3.4 | 33d2b331820aabcaffc4f80b280de5e597aafdf6 | 2026-09-09 | STAMP | exact | yes (280) |
| 0.3.5 | 0b8c749ec5bc6fa316384bac87cbd5fcf2030e10 | 2026-09-09 | STAMP | exact | yes (280) |
| 0.3.6 | 68c0f65ce2da0110dc6ddccb069bec069cf59c54 | 2026-09-10 | STAMP | exact | yes (280) |
| 0.3.7 | cacc94661d5b4f5aa2b43dfa2ade23537b9c79b0 | 2026-09-10 | STAMP | exact | yes (280) |
| 0.3.8 | f9edbe7781cf07afb2c1903a8affd6b9557b7084 | 2026-09-10 | STAMP | exact | yes (280) |
| 0.3.9 | cc1b4d79e7b160389612dec936f03b135d54c3f5 | 2026-09-10 | STAMP | exact | yes (280) |
| 0.3.10 | 037f670b8f2abe25647b12a11c6dd850ed94eaba | 2026-09-11 | STAMP | exact | yes (280) |
| 0.3.11 | 99b263999cc4d1a1c5f9ebaaa89a978cdbef7c29 | 2026-09-11 | STAMP | exact | yes (280) |
| 0.3.12 | a12f0744b6e2e13540a1fec61c34a78641e32f41 | 2026-09-12 | STAMP | exact | yes (280) |
| 0.3.13 | 028833fe74394e54e7e1a10d529785002d3c6e39 | 2026-09-13 | STAMP | exact | yes (280) |
| 0.3.14 | d087c355da722b81d421c0bef117d79013af5fb8 | 2026-09-13 | STAMP | exact | yes (280) |
| 0.3.15 | 8dd80832bda333b78521478a5ea8a843edc006b9 | 2026-09-13 | STAMP | exact | yes (280) |
| 0.3.16 | c9d3f41b4941143660f3f60a0f6760f5f15f420f | 2026-09-13 | STAMP | exact | yes (280) |
| 0.3.17 | 435a0c0734544523f3d30a082f5d1c15f18a78fa | 2026-09-13 | STAMP | exact | yes (280) |
| 0.3.18 | 3bb2d1781799ee4747d1d838649a7d6b7099869d | 2026-09-14 | STAMP | exact | yes (280) |
| 0.3.19 | 7fa9ad90af8080c91ef6991e8ad5b6983fd6b239 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.20 | b35e01cdaf80ec5f68238b77f9e6f88fb14f4249 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.21 | 4aee6e230758229d44fb52cf010b76cc6cb32a28 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.22 | f92cc09bd2437e722cf38ac49f75528c376b2e0c | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.23 | 6d3af49f06296b3aa5ae9df265cf5c3897ce9a85 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.24 | 56f2ba3b31ed4559ec8c625e34cc143b3a7fb9a9 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.25 | 3c9a91ff62a578d0979f79b8f3c8c57d5d613464 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.26 | 9ae407a26afd904ef0b0e9d8bfc9df6f492b148d | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.27 | 3c9cc5aef6de147758c43c7cd743d5c2d52912f3 | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.28 | 95f8f070702b25d2b93f09f79e3cca532c2b341a | 2026-09-15 | STAMP | exact | yes (280) |
| 0.3.29 | 536cba23381e335a93f066f1211f431a5e6c7d72 | 2026-09-16 | STAMP | exact | yes (280) |
| 0.3.30 | d54887b407f403722805480cb8ae90bc23b6fa91 | 2026-09-16 | STAMP | exact | yes (280) |
| 0.3.31 | 7c22ba85af8b45a9db91615110cdcb7f181fc295 | 2026-09-16 | STAMP | exact | yes (280) |
| 0.3.32 | fc415dbb676ddf871e758ee4b32b1880b5effd2b | 2026-09-16 | STAMP | exact | yes (280) |
| 0.3.33 | 94a083a4800d47d4615096c9c533306c00d9de31 | 2026-09-16 | STAMP | exact | yes (280) |
| 0.3.34 | 5af97ce485fcb455cf3bc3f7f95c6d815cd6d2c6 | 2026-09-16 | STAMP | exact | yes (280) |
| 0.3.35 | 5353f8233df76d3a7eb05bbd1d7fecb2eede880a | 2026-09-17 | STAMP | exact | yes (279) |
| 0.3.36 | b3e4f747dc63c5923af05c3b5f1aedb38401a5e3 | 2026-09-18 | STAMP | exact | yes (279) |
| 0.3.37 | ea1c5157c8013ffabed4d5bfdf274f5bfc26231f | 2026-09-18 | STAMP | exact | yes (279) |
| 0.3.38 | fd1d1dfa5a4327f669f281d531a89d6b0f3b0042 | 2026-09-18 | STAMP | exact | yes (279) |
| 0.3.39 | 30d0f99d24640ab0f12bd0b4c51667929dc12073 | 2026-09-22 | STAMP | exact | yes (279) |
| 0.3.40 | cc08cea3653f9e4f5558ea1a795b07c7a86017d4 | 2026-09-22 | STAMP | exact | yes (279) |
| 0.3.41 | 8321bf72e5a2b234d2404ad385dd8ccbeb5fd283 | 2026-09-22 | STAMP | exact | yes (279) |
| 0.3.42 | 2604e898ad6a7c0863b544f145d422fa4db12673 | 2026-09-22 | STAMP | exact | yes (279) |
| 0.3.43 | d5caff952e229e5a3079e096332d616944fad11d | 2026-09-22 | STAMP ("Prepare 0.3.43") | exact | yes (279) |
| 0.3.44 | 75260400533e4c01de7ae5a54b9cd6dfb42b33f5 | 2026-09-23 | TAG v0.3.44 (stamp a39422c9) | exact | yes (279) |
| 0.3.45 | 91bf594552e5f296f782f5a86b21731949e85e8d | 2026-09-24 | MSG "Bind release 0.3.45 to installed 0.3.44" | inferred | yes (279) |
| 0.3.46 | f94880c4df283406bee4a68d2f2713aee1e53816 | 2026-09-25 | MSG "Bind release 0.3.46 to installed 0.3.45" | inferred | yes (279) |
| 0.3.47 | 19fb262ff6a943d5bf37b4ca003f07235e72515d | 2026-09-25 | MSG "Bind release 0.3.47 to installed 0.3.46" | inferred | yes (279) |
| 0.3.48 | e5a9be0b8c1813805da0c63748ce078c10061d73 | 2026-10-22 | DEPLOYLOG rc3 (0.3.48+e5a9be0b); original work commit 1a996f5c, 2026-09-25 | exact | yes (182) |
| 0.3.49 | -- | -- | no evidence | unresolved | n/a |
| 0.3.50 | -- | -- | no evidence | unresolved | n/a |
| 0.3.51 | -- | -- | no evidence | unresolved | n/a |
| 0.3.52 | -- | -- | no evidence | unresolved | n/a |
| 0.3.53 | -- | -- | no evidence | unresolved | n/a |
| 0.3.54 | -- | -- | no evidence | unresolved | n/a |
| 0.3.55 | -- | -- | no evidence | unresolved | n/a |
| 0.3.56 | -- | -- | no evidence | unresolved | n/a |
| 0.3.57 | -- | -- | no evidence (a regex grep hit on 22893bda is a false positive) | unresolved | n/a |
| 0.3.58 | -- | -- | no evidence | unresolved | n/a |
| 0.3.59 | -- | -- | no evidence | unresolved | n/a |
| 0.3.60 | -- | -- | no evidence | unresolved | n/a |
| 0.3.61 | a8768e1d217ac17edda1ce0b8a0dae58a98f9726 | 2026-10-08 | MSG "Sync Shark 0.3.61: explicit Release predecessor guard ..." | inferred | yes (278) |
| 0.3.62 | c3a74cd594de7e574b8d00175fad3de79709113d | 2026-10-08 | MSG "Sync Shark 0.3.62" | inferred | yes (278) |
| 0.3.63 | -- | -- | only `docs/windows-installation.md` refers to "the 0.3.63 upgrade"; no commit, manifest or tag names it | unresolved | n/a |
| 0.3.64 | f4d62c4b60e0c9e54de8e8f373816efecbc20ab1 | 2026-10-24 | DEPLOYLOG rc5-0364 (manifest path recorded, file no longer on disk) | exact | yes (65) |
| 0.3.65 | 4f91b6697f19b70e271787fa85ee714fa7a64cde | 2026-10-24 | DEPLOYLOG rc5-0365 | exact | yes (57) |
| 0.3.66 | 37204384cbfa2673027e014b7119467a6bcf2462 | 2026-10-24 | DEPLOYLOG rc5-0366 | exact | yes (53) |
| 0.3.67 | da143b182865fe809e6c1c9745ea180e3184fd18 | 2026-10-24 | DEPLOYLOG rc5-0367 | exact | yes (52) |
| 0.3.68 | f69af919603754f43e59fe74371e8c349ea01428 | 2026-10-25 | DEPLOYLOG rc5-0368 | exact | yes (50) |
| 0.3.69 | 8c61dfb6919614d1f27d7c42f8be5d28e7663c90 | 2026-10-25 | DEPLOYLOG rc5-0369 | exact | yes (48) |
| 0.3.70 | 46b94e0fcd6b19e23a01a6b4929429e1563b65dd | 2026-10-25 | DEPLOYLOG rc5-0370-hotfix | exact | yes (41) |
| 0.3.71 | 4528e02d736e138cb27b9e62d62393c1d4774f0b | 2026-10-26 | DEPLOYLOG rc5-0371 (stamps 66d89ea 0.3.71, 0e38ea8 0.3.71.1, bc2245a 0.3.71 hotfix) | exact | yes (29) |
| 0.3.72 | f24d5ddc0d56658406745db24280990692c1f974 | 2026-10-26 | DEPLOYLOG rc5-0372; later 0.3.72 rebuilds 9df367c8 (rc5-0372r) and a9108856 (rc5-0372s); stamp 79a8b51 | exact | yes (23) |
| 0.3.73 | -- | -- | deploy attempted 2026-11-03 17:28 (`*.pre-0373-deploy-attempt-*.bak`, `statedb-backup-*-pre-0373`); no manifest, deploy-log or commit names it | unresolved | n/a |
| 0.3.74 | 9cde30a0e7e3227c64cb6041ad6753f41b890f00 | 2026-11-04 | MANIFEST + DEPLOYLOG build\rc8-0374log (stamp caff638, same release line) | exact | yes (21) |
| 0.3.75 | 876adc0c43442723611786f146f660c1da4d3149 | 2026-11-04 | MANIFEST + DEPLOYLOG build\rc9-0375 | exact | yes (21) |
| 0.3.76 | f0b020522ffc6e4ec9a69e47d09d72ac329b8e16 | 2026-11-05 | MANIFEST + DEPLOYLOG build\rc10-0376 (also the STAMP commit) | exact | yes (17) |
| 0.3.77 | 1ba4297d3a80a4acc9dc7971315cd33234fc8520 | 2026-11-07 | MANIFEST + DEPLOYLOG rc11-0377 | exact | yes (6) |
| 0.3.78 | e27789302fa5fad0c62456d264b2bc0efcf891c6 | 2026-11-07 | MANIFEST + DEPLOYLOG rc12-0378h; TAG v0.3.78; hotfix: 0.3.77 plus the Repair refusal handoff fix only | exact | yes |
| 0.3.79 | afa739564a7ec8f472ae444ca58af23307c3abd0 | 2026-11-07 | MANIFEST + DEPLOYLOG rc13-0379; TAG v0.3.79 | exact | yes |

## Versions with no reliable commit

- **0.3.49 - 0.3.60** - no tag, manifest, deploy log or commit message. The 0.3.48 line
  (released 2026-10-22) is followed in the deploy records by 0.3.64 (2026-10-24), so these
  numbers appear never to have shipped from this repository, or shipped from builds whose
  records are gone.
- **0.3.57** - the only `git log --grep` hit (22893bda) is a regex false positive; the
  fixed-string search finds nothing.
- **0.3.63** - a real upgrade (referenced by `docs/windows-installation.md`), but the
  source commit is not recorded anywhere in the repository or the release artifacts.
- **0.3.73** - a deploy was attempted on 2026-11-03 (backups named `pre-0373`), but no
  manifest, deploy-log or commit records its source revision. It looks like it was
  rolled back before a manifest was kept.

## Notes on the stronger evidence

- Annotated tags are present on `origin`: v0.1.0, v0.1.1, v0.2.4, v0.3.0, v0.3.44 and
  every version from v0.3.70 onward. A separate tag, `swarm-freeze-2026-11-03`
  (b3eb32a2cb8aa8aca92be1e1a8c95f4f55bffc67, 2026-11-03), is a work freeze, not a
  released version.
- `Directory.Build.props` held `0.3.44` from 2026-09-23 until 2026-10-25, so versions
  0.3.45 - 0.3.70 were built with `-p:Version=` overrides and left no stamp commit.
- `0.3.44` was rebuilt twice after the tag: `163c7f15` (package `fod`, 2026-10-21) and
  `3a6d2e40` (package `final26.2`, 2026-10-21); both report `0.3.44+<sha>`.
- Manifests survive on disk for 0.3.74 through 0.3.77 (packages `rc8-0374log` through
  `rc11-0377`). The 0.3.64 - 0.3.72 evidence is the `runs[]` array of the deploy logs
  kept with those release packages.
