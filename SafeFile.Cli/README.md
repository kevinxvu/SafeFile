# SafeFile CLI Guide

`safefile` is the command-line interface for SafeFile. It runs the same
`SafeFile.Core` engine as the desktop app, so vaults are fully interchangeable:
anything encrypted by the CLI opens in the app and vice versa.

It is designed for two audiences:

- **people** in a terminal: a live progress line with percent, speed, and ETA;
- **scripts and tools**: `--json` turns stdout into a stream of NDJSON events,
  and exit codes are stable.

Contents:

1. [Build and install](#1-build-and-install)
2. [Quick start](#2-quick-start)
3. [Commands](#3-commands)
4. [Passwords](#4-passwords)
5. [Settings](#5-settings)
6. [Text output](#6-text-output)
7. [JSON output for integrations](#7-json-output-for-integrations)
8. [Exit codes and error codes](#8-exit-codes-and-error-codes)
9. [Integration examples](#9-integration-examples)
10. [Behavior notes](#10-behavior-notes)

## 1. Build and install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
dotnet build SafeFile.Cli
dotnet run --project SafeFile.Cli -- --help
```

Publish a self-contained single executable (`safefile.exe` / `safefile`):

```powershell
dotnet publish SafeFile.Cli -c Release -r win-x64 -o artifacts/cli/win-x64
```

The project file makes every `-r` publish self-contained, single-file, and
compressed (about 37 MB; no .NET install needed). Replace `win-x64` with
`osx-arm64`, `osx-x64`, or `linux-x64` for other platforms. Put the output
folder on `PATH` to call `safefile` from anywhere.

The macOS builds are not signed or notarized. After extracting, remove the
quarantine flag once:

```bash
xattr -d com.apple.quarantine ./safefile
chmod +x ./safefile
```

## 2. Quick start

```powershell
# Encrypt a file and a folder into D:\Vaults (prompts for the password)
safefile encrypt report.pdf photos -o D:\Vaults

# Decrypt everything under D:\Vaults, keeping the folder layout
safefile decrypt D:\Vaults -o D:\Restored

# Check a password without decrypting
safefile verify D:\Vaults

# Hide sub-folder names in place, then restore them
safefile folder-names encrypt D:\Private
safefile folder-names decrypt D:\Private

# Machine-readable output
safefile decrypt D:\Vaults -o D:\Restored --password-env SAFEFILE_PASSWORD --json
```

## 3. Commands

```text
safefile [global options] <command> [arguments] [options]
```

| Command | Purpose |
| --- | --- |
| [`encrypt`](#31-encrypt) | Encrypt files and folders into `.safe` vaults |
| [`decrypt`](#32-decrypt) | Decrypt vaults |
| [`verify`](#33-verify) | Check that a password opens vaults, without decrypting |
| [`info`](#34-info) | Show vault header details |
| [`folder-names`](#35-folder-names) | Protect or restore sub-folder names in place |
| [`genpass`](#36-genpass) | Generate strong random passwords |

Global options, accepted by every command:

| Option | Description |
| --- | --- |
| `--json` | Write NDJSON events to stdout (see [section 7](#7-json-output-for-integrations)) |
| `--no-progress` | No progress line (text) / no `progress` events (JSON) |
| `--progress-interval MS` | Minimum milliseconds between progress updates (default 250, minimum 50) |
| `--ignore-settings` | Use built-in defaults instead of the desktop app's saved settings |
| `--threads N` | Worker threads for encryption/decryption (1-256) |
| `-h`, `--help` | Help for the command |
| `--version` | CLI version |

### 3.1 `encrypt`

```text
safefile encrypt <sources>... [options]
```

`sources` can mix files and folders.

| Option | Default | Description |
| --- | --- | --- |
| `-o`, `--output DIR` | current directory | Output folder (created if missing) |
| `--mode zip\|perfile` | `zip` | How folders are encrypted |
| `--name-mode none\|aes\|sha256\|md5` | `none` | Protect output file names |
| `--exclude DIR` | | Sub-folder of a source folder to skip; repeatable |
| `--overwrite` | off | Overwrite existing output files |
| `--chunk-size-mb N` | setting | Chunk size, 1-16 MB |
| `--argon2-memory-kb N` | setting | Argon2id memory |
| `--argon2-iterations N` | setting | Argon2id iterations |
| `--argon2-parallelism N` | setting | Argon2id parallelism |
| password options | prompt | See [section 4](#4-passwords) |

Outputs, identical to the desktop app:

| Source | Mode | Output |
| --- | --- | --- |
| file `a.pdf` | | `<output>/a.pdf.safe` |
| folder `photos` | `zip` | `<output>/photos.safe` (one vault) |
| folder `photos` | `perfile` | `<output>/photos_encrypted/…` (one vault per file, same layout) |

With `--name-mode sha256` or `md5`, vault names become hashes of the original
name; with `aes` they become an encrypted name. The original name is always
restored on decrypt.

### 3.2 `decrypt`

```text
safefile decrypt <sources>... [options]
```

`sources` are `.safe` files and/or folders. Folders are searched recursively
for `*.safe` files, and each vault is written to the matching relative folder
under the output.

| Option | Default | Description |
| --- | --- | --- |
| `-o`, `--output DIR` | current directory | Output root |
| `--exclude DIR` | | Sub-folder of a source folder to skip; repeatable |
| `--overwrite` | off | Overwrite existing output **files**. A zip vault never overwrites an existing folder |
| password options | prompt | |

Each vault is first authenticated, so a wrong password fails fast with
`wrong_password` and nothing is written. Other vaults in the batch continue.

### 3.3 `verify`

```text
safefile verify <sources>... [--exclude DIR] [password options]
```

Opens each vault's authenticated metadata (one Argon2id derivation per vault)
and reports whether the password is correct. Nothing is decrypted to disk.

### 3.4 `info`

```text
safefile info <sources>... [--exclude DIR] [password options]
```

Shows the unencrypted header: mode, name mode, format version, size, chunk
size, and Argon2id parameters. **If a password option is given**, it also
verifies the password and shows the original name and cipher. Without a
password option, `info` never prompts.

### 3.5 `folder-names`

Renames every sub-folder under a root to a protected name, and back. The root
folder itself, file names, and file contents are never changed. The mapping is
stored encrypted in `<root>/.safefile-names`.

```text
safefile folder-names status  <root> [--exclude DIR] [password options]
safefile folder-names encrypt <root> [--mode md5|sha256|aes] [--exclude DIR] [password options]
safefile folder-names decrypt <root> [password options]
```

| Subcommand | Behavior |
| --- | --- |
| `status` | Manifest present or not, physical folder count. With a password option, also verifies the manifest and shows protected/clear/new/conflict counts |
| `encrypt` | New root: creates a manifest in `--mode` (default `md5`) and protects all sub-folders. Existing manifest: verifies it and protects only new folders; its mode is kept (a different `--mode` produces a `mode_ignored` warning) |
| `decrypt` | Restores original names and removes the manifest when nothing is left protected. Exclusions are not accepted |

A new manifest requires a password of at least the configured minimum length
(default 8). An interrupted run can be resumed by running the same command
again.

### 3.6 `genpass`

```text
safefile genpass [-l|--length N] [-n|--count N] [--no-uppercase] [--no-lowercase]
                 [--no-numbers] [--no-special] [--exclude-ambiguous]
```

Length 4-64 (default 20), count 1-100. Each password contains at least one
character from every enabled group. Text mode prints one password per line on
stdout.

## 4. Passwords

Use **one** of the following. With none, the CLI asks for the password with
hidden input.

| Option | Example | Notes |
| --- | --- | --- |
| `-p`, `--password VALUE` | `-p "S3cret!pass"` | Inline. Easiest, but visible in shell history and process lists |
| `--password-stdin` | `echo $pw \| safefile … --password-stdin` | First line of stdin |
| `--password-env NAME` | `--password-env SAFEFILE_PASSWORD` | Environment variable |
| `--password-file PATH` | `--password-file C:\keys\vault.txt` | File content; one trailing newline is ignored |
| *(none)* | | Interactive hidden prompt |

- When creating something new (`encrypt`, `folder-names encrypt` on a new
  root), the prompt asks twice if **Confirm password** is enabled in Settings.
- If stdin is redirected and no option is given, the CLI fails immediately
  with `password_required` (exit code 2) instead of hanging.
- Giving more than one source fails with `invalid_argument`.
- For automation prefer `--password-env`, `--password-stdin`, or
  `--password-file` over `-p`.

## 5. Settings

By default the CLI reads the desktop app's saved `settings.json`, the same file
the Settings page edits:

| Setting | Used for |
| --- | --- |
| Chunk size | `encrypt` (override: `--chunk-size-mb`) |
| Worker threads | all crypto work (override: `--threads`) |
| Argon2id memory/iterations/parallelism | `encrypt` (overrides: `--argon2-*`) |
| Minimum password length | `encrypt`, `folder-names encrypt` |
| Confirm password | interactive prompt |

The CLI never writes settings. Output folders do **not** come from Settings:
without `-o` the CLI writes to the current directory.

Use `--ignore-settings` in scripts so results do not depend on what a user
configured in the app.

## 6. Text output

- **stderr**: the progress line, start message, and per-item `OK` / `PART` /
  `FAIL` lines.
- **stdout**: the final summary (and the output of `info`, `status`, `genpass`).

On a terminal the progress line redraws in place:

```text
[###########-------------]  45.2%  120.5 MB / 266.7 MB  35.1 MB/s  ETA 00:04  3/10  video.mp4
```

When stderr is redirected to a file or pipe, a new line is written at most
every 2 seconds. `--no-progress` disables it.

## 7. JSON output for integrations

With `--json`, **stdout contains only NDJSON**: one JSON object per line,
UTF-8 without BOM, flushed per line. Nothing else is written to stdout. Read it
line by line and switch on `event`.

Every event has:

| Field | Type | Description |
| --- | --- | --- |
| `event` | string | Event name |
| `time` | string | ISO 8601 UTC timestamp |
| `operation` | string | `encrypt`, `decrypt`, `verify`, `info`, `folder-names.status`, `folder-names.encrypt`, `folder-names.decrypt`, `genpass` |

### 7.1 Event sequence

```text
start → ( item-start → progress* → item )* → progress → result
```

A `warning` can appear at any point. On an aborting failure the stream ends
with `error` followed by `result`. **`result` is always the last line**, and
its `exitCode` equals the process exit code.

### 7.2 `start`

| Field | Description |
| --- | --- |
| `schemaVersion` | Event schema version, currently `1` |
| `totalBytes` | Total workload in bytes (encrypt/decrypt) |
| `totalItems` | Number of items counted in `succeeded`/`failed` |
| `outputDirectory` | Resolved output folder (encrypt/decrypt) |
| `sources` | encrypt: `[{path, kind, sizeBytes, fileCount, destination}]` |
| `vaults` | decrypt: `[{path, sizeBytes, relativeDirectory, mode, valid}]` |
| `nameMode` | encrypt: name protection mode |
| `root`, `mode`, `protectedCount`, `clearCount`, `conflictCount` | folder-names |

An `encrypt` start event looks like this:

```json
{"event":"start","time":"2026-11-24T06:48:46.89Z","operation":"encrypt","schemaVersion":1,
 "outputDirectory":"D:\\Vaults","totalBytes":60000023,"totalItems":2,"nameMode":"none",
 "sources":[{"path":"D:\\data\\src","kind":"zip","sizeBytes":60000016,"fileCount":1,"destination":"D:\\Vaults\\src.safe"},
            {"path":"D:\\data\\one.txt","kind":"file","sizeBytes":7,"fileCount":1,"destination":"D:\\Vaults\\one.txt.safe"}]}
```

### 7.3 `progress`

```json
{"event":"progress","time":"2026-11-24T06:48:47.29Z","operation":"encrypt",
 "phase":"encrypting","percent":45.23,"fraction":0.4523,"unit":"bytes",
 "processed":271380000,"total":600000000,"rate":35100000.0,"etaSeconds":9.4,
 "elapsedSeconds":7.7,"currentPath":"D:\\data\\video.mp4",
 "succeeded":3,"failed":0,"remaining":7,"totalItems":10}
```

| Field | Type | Description |
| --- | --- | --- |
| `phase` | string | `encrypting`, `decrypting`, `verifying`, or `finalizing` (last 1% of a zip vault) |
| `percent` | number | 0-100, two decimals |
| `fraction` | number | 0-1, four decimals |
| `unit` | string | `bytes` (encrypt/decrypt), `vaults` (verify), `folders` (folder-names) |
| `processed` / `total` | integer | Work done / total, in `unit` |
| `rate` | number \| null | Smoothed speed in `unit` per second |
| `etaSeconds` | number \| null | Estimated seconds remaining |
| `elapsedSeconds` | number | Since the operation started |
| `currentPath` | string \| null | File or folder being processed |
| `succeeded` / `failed` / `remaining` / `totalItems` | integer | Item counters |

- `rate` and `etaSeconds` are `null` until enough samples exist (about
  1.5 seconds and two samples). The estimator is the same
  `TransferMetricsEstimator` the desktop app uses: speed counts only bytes
  actually transferred, ETA uses resolved workload, so skipped items do not
  inflate the speed.
- Events are throttled by `--progress-interval`, with extra events at item
  boundaries. Very fast operations may emit few progress events.
- `processed` never decreases.

### 7.4 `item-start` and `item`

`item-start`: `index`, `path`, `kind`, `sizeBytes`, `destination` (encrypt).

`item`:

| Field | Description |
| --- | --- |
| `path` | Source file/folder (encrypt) or vault (decrypt/verify/info) |
| `kind` | encrypt: `file`, `zip`, `perfile`; decrypt: vault mode `file`, `zip`, `perfile` |
| `parent` | For files inside a `perfile` folder: the source folder; otherwise `null` |
| `status` | `succeeded`, `failed`, or (perfile folder summary) `completed_with_errors` |
| `output` | Created file or folder, or `null` |
| `errorCode` / `error` | Set when the item failed |
| `succeeded` / `failed` | perfile folder summary only |

For a `perfile` folder, every file gets an `item` with `kind: "file"`, then the
folder gets a summary `item` with `kind: "perfile"`.

`verify` and `info` items also carry `sizeBytes`, `lastModifiedUtc`, `header`
(`version`, `mode`, `nameMode`, `chunkSizeBytes`, `kdf`), `passwordValid`
(`true`, `false`, or `null` when not checked), `originalName`, and
`encryptionAlgorithm`.

### 7.5 `warning` and `error`

`warning`: `code`, `message`. Example: `mode_ignored` from `folder-names encrypt`.

`error`: `code`, `message`, `path`. Emitted once when the whole operation
aborts, for example on a missing password, invalid arguments, or cancellation.
Per-item failures are reported in `item` instead.

### 7.6 `result`

| Field | Description |
| --- | --- |
| `status` | `succeeded`, `completed_with_errors`, `failed`, `cancelled` |
| `exitCode` | Same as the process exit code |
| `succeeded` / `failed` / `totalItems` | Final counters |
| `elapsedSeconds` | Total duration |
| `outputs` | encrypt/decrypt: created files and folders |
| `errorCode` / `message` | When aborted |

Command-specific fields: `outputDirectory`, `totalBytes` (encrypt/decrypt);
`renamed`, `mode`, `root` (folder-names); `hasManifest`, `manifestVerified`,
`physicalFolderCount`, `protectedCount`, `clearCount`, `newCount`,
`conflictCount` (status); `passwords` (genpass).

### 7.7 Compatibility

Within `schemaVersion` 1, fields and events may be **added** but not removed
or renamed. Ignore unknown fields and events.

## 8. Exit codes and error codes

| Exit code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Failure |
| 2 | Invalid usage: bad arguments, missing or mismatched password, password too short, invalid exclusion |
| 3 | Wrong password (or corrupted data that fails authentication) |
| 4 | Completed with errors: some items succeeded, some failed |
| 130 | Cancelled with Ctrl+C |

For a batch where nothing succeeded, the exit code is that of the first
failure, for example 3 when every vault had a wrong password.

| Error code | Meaning |
| --- | --- |
| `wrong_password` | Password does not open the vault or manifest |
| `password_required` | No password available (non-interactive) or empty password |
| `password_mismatch` | Confirmation prompt did not match |
| `password_too_short` | Below the minimum length for a new folder-name manifest |
| `destination_exists` | Output already exists and cannot be overwritten |
| `not_found` | Source path, password file, or folder missing |
| `invalid_vault` | Not a valid `.safe` header or unsupported mode |
| `invalid_exclusion` | `--exclude` not inside a source folder, missing, or a link |
| `invalid_argument` | Invalid option value |
| `invalid_data` | Corrupted or truncated data |
| `no_vaults` | No `.safe` files found in the sources |
| `no_manifest` | `folder-names decrypt` on a root without a manifest |
| `io_error`, `access_denied` | File-system errors |
| `cancelled` | Ctrl+C |
| `error` | Anything else |

## 9. Integration examples

### PowerShell

```powershell
$env:SAFEFILE_PASSWORD = Read-Host -AsSecureString "Password" |
    ForEach-Object { [Net.NetworkCredential]::new('', $_).Password }

safefile encrypt D:\data -o D:\Vaults --password-env SAFEFILE_PASSWORD --json --ignore-settings |
    ForEach-Object {
        $e = $_ | ConvertFrom-Json
        switch ($e.event) {
            'progress' { Write-Progress -Activity 'Encrypting' -Status "$($e.percent)%  ETA $($e.etaSeconds)s" -PercentComplete $e.percent }
            'item'     { if ($e.status -eq 'failed') { Write-Warning "$($e.path): $($e.error)" } }
            'result'   { "Done: $($e.status), $($e.succeeded) ok, $($e.failed) failed" }
        }
    }
"Exit code: $LASTEXITCODE"
```

### Python

```python
import json, os, subprocess

proc = subprocess.Popen(
    ["safefile", "decrypt", r"D:\Vaults", "-o", r"D:\Restored",
     "--password-stdin", "--json", "--ignore-settings"],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
proc.stdin.write(os.environ["VAULT_PASSWORD"] + "\n")
proc.stdin.close()

for line in proc.stdout:
    event = json.loads(line)
    if event["event"] == "progress":
        print(f'{event["percent"]:6.2f}%  eta={event["etaSeconds"]}', end="\r")
    elif event["event"] == "item" and event["status"] == "failed":
        print(f'\nFAILED {event["path"]}: {event["errorCode"]}')
    elif event["event"] == "result":
        print(f'\n{event["status"]}: {event["succeeded"]} ok, {event["failed"]} failed')

exit_code = proc.wait()
```

### Node.js

```js
const { spawn } = require("node:child_process");
const readline = require("node:readline");

const child = spawn("safefile", ["verify", "D:\\Vaults", "--password-env", "SAFEFILE_PASSWORD", "--json"]);
readline.createInterface({ input: child.stdout }).on("line", (line) => {
  const e = JSON.parse(line);
  if (e.event === "item") console.log(e.path, e.passwordValid ? "OK" : e.errorCode);
});
child.on("close", (code) => console.log("exit", code));
```

## 10. Behavior notes

- **Cancellation:** Ctrl+C cancels the running operation. Core removes the
  incomplete output of the item in progress, as in the desktop app; completed
  items stay. The CLI waits up to 30 seconds for that cleanup.
- **Memory hygiene:** password bytes are zeroed after use. An inline `-p`
  value is a .NET string and cannot be zeroed, which is another reason to
  prefer the other sources.
- **Batches continue on errors:** a failing item does not stop the others;
  check `failed` and the exit code (4).
- **Links are skipped:** symlinks, junctions, and other reparse points are not
  followed, matching Core.
- **Empty folders** are not preserved in vaults (a Core limitation).
- Technical details of the vault format and Core APIs are in the
  [SafeFile.Core Technical Guide](../SafeFile.Core/README.md).
