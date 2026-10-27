# Sync Shark

Windows file synchronisation client with Dropbox and Google Drive support.

This repository contains cleaned source snapshots, not the private development history.
No licence is granted. All rights reserved.

Build prerequisites: Windows, .NET SDK specified in global.json, and the native toolchains
referenced by the application project. Restore with `dotnet restore SyncShark.sln --locked-mode`.
Use `dotnet build SyncShark.sln -c Debug`; release signing requires separately provisioned credentials.
Public signer identities are verification data, not private signing keys.

Live-data operator launchers and internal capability/requirements records are not distributed.