# Sync Shark command line

Run `syncshark.exe` followed by one of the commands below. A read does not start the host.

| Command | Usage | Effect |
|---|---|---|
| `pairs list` | `pairs list` | List the configured pairs. |
| `pairs add` | `pairs add --name <name> --local <folder> --path <folder> --account <id> [--first-sync newest\|pc\|dropbox]` | Add a pair and select how its first comparison treats differences. |
| `pairs remove` | `pairs remove --pair <id>` | Remove the configuration, not the files. Report unfinished recovery before removal. |
| `pairs bind` | `pairs bind --pair <id> --yes` | Confirm the observed root identities for this pair. |
| `pairs exclude` | `pairs exclude --pair <id> --side local\|dropbox --rel <path> [--keep] [--yes]` | Keep the selected copy out of sync. Without --keep, preview the removal and recovery information before confirming. |
| `pairs include` | `pairs include --pair <id> --side local\|dropbox --rel <path>` | Remove the exclusion and compare the newly included scope. |
| `pairs exclusions` | `pairs exclusions --pair <id>` | List the configured exclusions. |
| `ignores list` | `ignores list --pair <id>` | List the ignore rules. |
| `ignores set` | `ignores set --pair <id> --rules <rules>` | Replace this pair’s ignore rules. |
| `host start` | `host start` | Start the background host or attach to the one already running. |
| `host stop` | `host stop` | Stop the background host. |
| `status` | `status [--pair <id>]` | Read current status without starting the host. |
| `pause` | `pause [--pair <id>]` | Pause one pair or all pairs until resume or restart. |
| `resume` | `resume [--pair <id>]` | Resume one pair or all pairs. |
| `repair` | `repair --pair <id> [--show-files]` | Compare both sides against the saved records, optionally showing individual file operations as they happen. |
| `notices` | `notices --pair <id> [--ignored]` | List current notices. |
| `resolve` | `resolve --pair <id> --issue <number> --reviewed <token> --choice <choice> [--name <name>]` | Apply an available choice to the reviewed notice. |
| `logs` | `logs --pair <id> --run <id> [--show-files]` | Read run logs, optionally including individual file operations. |
| `accounts connect` | `accounts connect` | Connect a Dropbox account through browser sign-in. |
| `accounts disconnect` | `accounts disconnect --account <id>` | Disconnect the account and pause affected pairs without deleting files. |
