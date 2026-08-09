# SafeFile.Core Technical and UI Integration Guide

`SafeFile.Core` is the reusable cryptography and file-processing layer for SafeFile. It has no Avalonia dependency and can be used by the desktop UI, a CLI, or tests.

This document is the integration contract for future UI work. It describes which values the UI must collect, what each Core method returns, and how the UI should handle progress, cancellation, output paths, and errors.

## 1. Capabilities

Core supports three vault modes:

| Mode | Encrypt operation | Decrypt operation | Output |
|---|---|---|---|
| `File` | `EncryptFileAsync` | `DecryptFileAsync` | One source file and one `.safe` vault |
| `Zip` | `EncryptFolderZipAsync` | `DecryptFolderZipAsync` | One source folder packed into one `.safe` vault |
| `PerFile` | `EncryptFolderPerFileAsync` | `DecryptFolderPerFileAsync` | One independent `.safe` vault per regular source file |

All modes:

- derive a 32-byte key with Argon2id;
- encrypt authenticated chunks with AES-256-GCM;
- support cancellation;
- optionally encrypt output filenames;
- skip symlinks, junctions, and other reparse points;
- reject unsafe chunk sizes and KDF parameters;
- clean incomplete encryption output created by a failed encryption operation;
- preserve decryption output when a vault fails or a batch is cancelled.

Empty directories are not preserved.

Core also provides non-vault utilities:

- `TextCryptoService` encrypts and decrypts portable authenticated text and
  calculates SHA-256 for UTF-8 text;
- `PasswordGenerator` creates cryptographically random passwords while
  guaranteeing that every selected character group occurs at least once.

The desktop application also provides folder-name protection through the
app-side `FolderNameProtectionService`. It is intentionally not a production
Core API: it orchestrates directory renames and reuses Core's
`TextCryptoService` only for its encrypted manifest. See section 6.3.

## 2. Package layout

```text
SafeFile.Core/
├── Crypto/
│   ├── Argon2Kdf.cs
│   ├── AesGcmEngine.cs
│   └── KdfDerivation.cs       # Shared asynchronous KDF helper
├── Format/
│   ├── VaultHeader.cs
│   └── VaultMode.cs
├── Pipeline/
│   ├── CryptoPipeline.cs       # Internal bounded pipeline
│   └── UnencryptedChunk.cs
├── IO/
│   ├── FileEncryptor.cs        # Main UI-facing API
│   ├── StreamZipper.cs
│   └── PasswordValidator.cs
├── Models/AppSettings.cs
└── Services/
    ├── SettingsService.cs
    ├── TextCryptoService.cs
    └── PasswordGenerator.cs
```

## 3. UI setup

### 3.1 Load settings

```csharp
var settingsService = new SettingsService();
var settings = settingsService.Load();
```

Relevant `AppSettings` values:

| Setting | UI purpose |
|---|---|
| `Language` | UI culture: `en` or `vi`; defaults to English |
| `Theme` | Avalonia theme: `Light` or `Dark`; first run follows the system and falls back to Dark |
| `DefaultChunkSizeMb` | Chunk-size selector, valid range 1–16 MiB |
| `MaxThreads` | Encryption worker count |
| `Argon2MemorySizeKb` | KDF memory setting |
| `Argon2Iterations` | KDF iteration setting |
| `Argon2Parallelism` | Portable KDF parallelism, valid range 1–16 |
| `DefaultOutputPath` | Destination root used by the desktop encryption form |
| `DefaultDecryptOutputPath` | Destination root used by the desktop decryption form |
| `ConfirmPasswordToggle` | Controls whether the encryption form shows and validates password confirmation |
| `MinPasswordLength` | Minimum password byte length enforced during encryption |

Use:

```csharp
var chunkSizeBytes = settings.GetChunkSizeBytes();
var kdfParameters = settings.GetKdfParameters();
```

`SettingsService.Save` normalizes unsafe values before writing `settings.json`.
The default roots are `Documents/SafeFile/Encrypted` and
`Documents/SafeFile/Decrypted`. Legacy `SafeFile`, `Encrypt`, and `Decrypt`
defaults are migrated to the distinct `Encrypted` and `Decrypted` folders.

### 3.2 Create one `FileEncryptor` per active operation

```csharp
var progress = new Progress<double>(value =>
{
    // value is in the inclusive range 0.0–1.0
    ProgressPercent = value * 100;
});

var perFileProgress = new Progress<PerFileProgress>(value =>
{
    CurrentFilePath = value.SourceFilePath;
    ProgressPercent = value.Progress * 100;
});

var encryptor = new FileEncryptor(
    consumerThreads: settings.MaxThreads,
    progress: progress,
    settings: settings,
    perFileProgress: perFileProgress);
```

Do not run concurrent operations through the same `FileEncryptor` when independent progress is required. The pipeline keeps shared progress state. Create a separate instance for each active UI job.

### 3.3 Prepare the password

Core accepts UTF-8 password bytes, not a `string`:

```csharp
var passwordBytes = Encoding.UTF8.GetBytes(password);
try
{
    // Call Core.
}
finally
{
    CryptographicOperations.ZeroMemory(passwordBytes);
}
```

Core does not mutate the caller-owned array. The UI should clear its own byte array after the operation. Encryption enforces `AppSettings.MinPasswordLength`; decryption only requires a non-empty password for vault compatibility.

## 4. API contract for the UI

### 4.1 Encrypt one file

```csharp
Task<string> EncryptFileAsync(
    string sourcePath,
    string destinationPath,
    byte[] passwordBytes,
    int chunkSizeBytes = 1_048_576,
    Argon2Parameters? kdfParams = null,
    OutputFileNameMode outputFileNameMode = OutputFileNameMode.None,
    CancellationToken cancellationToken = default,
    bool overwriteExisting = false);
```

UI inputs:

- `sourcePath`: existing regular source file.
- `destinationPath`: requested `.safe` path and parent directory.
- `passwordBytes`: UTF-8 password bytes.
- `chunkSizeBytes`: 1–16 MiB.
- `kdfParams`: normally `settings.GetKdfParameters()`.
- `outputFileNameMode`: pass `None`, `Aes`, `Sha256`, or `Md5` from the encryption form; it defaults to `None`.
- `cancellationToken`: token owned by the UI operation.
- `overwriteExisting`: replace an existing vault only after explicit user
  confirmation; the default `false` preserves an existing destination.

Return value:

- `None`: returns `destinationPath`;
- `Aes`: returns the actual Base64URL `.safe` path in the same parent directory;
- `Sha256`: returns `lowercase_hex(SHA256(UTF8(originalFileName))) + ".safe"` in the same parent directory.
- `Md5`: returns `lowercase_hex(MD5(UTF8(originalFileName))) + ".safe"` in the same parent directory.

When filename encryption is on, Core derives the final encrypted path before opening output. The clear-name `destinationPath` is not created or truncated. The UI must use the returned path for notifications, recent files, reveal-in-folder, and subsequent decrypt operations.

Example:

```csharp
var actualVaultPath = await encryptor.EncryptFileAsync(
    sourcePath,
    requestedVaultPath,
    passwordBytes,
    settings.GetChunkSizeBytes(),
    settings.GetKdfParameters(),
    outputFileNameMode: selectedOutputFileNameMode,
    cancellationToken,
    overwriteExisting: overwriteToggle);
```

### 4.2 Decrypt one file

```csharp
Task<string> DecryptFileAsync(
    string sourcePath,
    string destinationPath,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default,
    bool overwriteExisting = false);
```

UI inputs:

- `sourcePath`: existing `VaultMode.File` vault.
- `destinationPath`: requested plaintext output path or a placeholder path whose parent directory should receive the restored filename.
- `passwordBytes`: non-empty UTF-8 password bytes.
- `overwriteExisting`: allows replacing the authenticated restored filename when
  it already exists. Keep the default `false` unless the user explicitly
  confirms overwrite in the UI.

Return value:

- vault flag off: returns and writes exactly `destinationPath`;
- vault flag on: ignores the destination basename, restores the complete authenticated filename from the vault, writes it under `Path.GetDirectoryName(destinationPath)`, and returns that actual path.

The UI must use the returned path. Do not assume the basename selected before decrypt is the basename Core used.

File and individual PerFile-vault decryption report progress from encrypted
vault bytes consumed. The sequence starts at `0`, advances after authenticated
chunks are written, throttles callbacks to changes of at least 0.1%, and keeps
intermediate values below `1`. Core reports exactly `1` only after validating
the final chunk. A failed, truncated, or cancelled operation does not report
successful completion.
For both naming modes, an existing destination is rejected unless
`overwriteExisting` is explicitly set to `true`.

### 4.3 Encrypt a folder as one ZIP vault

```csharp
Task<string> EncryptFolderZipAsync(
    string sourceFolderPath,
    string destinationPath,
    byte[] passwordBytes,
    int chunkSizeBytes = 1_048_576,
    Argon2Parameters? kdfParams = null,
    OutputFileNameMode outputFileNameMode = OutputFileNameMode.None,
    CancellationToken cancellationToken = default,
    bool overwriteExisting = false,
    IReadOnlyCollection<string>? excludedFolderPaths = null);
```

UI inputs match single-file encryption, except the source is an existing folder. `destinationPath` must be outside the source tree.
`overwriteExisting` follows the same explicit-confirmation rule as
`EncryptFileAsync`.
`excludedFolderPaths` may contain existing descendant directories of the source
root. Each selected directory and its complete subtree is omitted from the ZIP;
the source root, outside paths, and reparse points are rejected.

Return value follows the same naming rule as `EncryptFileAsync`. Store and display the returned actual vault path.

ZIP creation is streamed:

```text
source folder → ZipArchive → bounded Pipe → crypto pipeline → one .safe
```

No plaintext ZIP is written during encryption.

### 4.4 Decrypt a ZIP folder vault

```csharp
Task DecryptFolderZipAsync(
    string sourcePath,
    string destinationFolder,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default);
```

UI inputs:

- `sourcePath`: existing `VaultMode.Zip` vault;
- `destinationFolder`: folder that must not already exist;
- `passwordBytes`: non-empty UTF-8 password bytes.

Core decrypts to a temporary seekable ZIP and extracts directly into
`destinationFolder`, validating every extraction path. If extraction fails or
is cancelled, successfully written and partial output remains available; Core
does not remove the destination folder.

Progress is monotonic across both ZIP-decryption stages. Decrypting the vault
to the temporary ZIP reports from 0% through 60% based on encrypted source
bytes consumed. Extraction reports from 60% through 100% based on total
uncompressed entry bytes written. Updates are throttled to 0.1% increments to
avoid flooding the UI synchronization context for large archives, and 100% is
reported only after extraction completes. The value remains a normal per-item
`IProgress<double>` value, so the desktop batch calculation works unchanged
when File, ZIP, and PerFile vaults are mixed.

The stored encrypted filename can be displayed separately with `DecryptOutputFileNameAsync`, but ZIP extraction always uses the destination folder selected by the UI.

### 4.5 Encrypt a folder as independent per-file vaults

```csharp
Task EncryptFolderPerFileAsync(
    string sourceFolderPath,
    string destinationFolderPath,
    byte[] passwordBytes,
    int chunkSizeBytes = 1_048_576,
    Argon2Parameters? kdfParams = null,
    OutputFileNameMode outputFileNameMode = OutputFileNameMode.None,
    CancellationToken cancellationToken = default,
    bool overwriteExisting = false,
    IReadOnlyCollection<string>? excludedFolderPaths = null);
```

UI inputs:

- source folder must exist;
- destination folder may already exist but must be outside the source tree;
- pass `outputFileNameMode` explicitly from the encryption form; it defaults to `None`.
- `overwriteExisting` controls whether existing per-file vaults are replaced.
- `excludedFolderPaths` omits matching descendant directories and every file
  below them; validation matches ZIP-folder encryption.

Core preserves relative subfolder structure but does not preserve empty directories. Each regular file receives its own salt, key, header, filename chunk, and data chunks.
When overwrite is disabled, an existing vault is preserved and recorded as a
per-file failure. Core continues encrypting later source files and throws one
summary error after every file has been attempted. Successful outputs and the
destination directory are preserved.

For `None`, `Sha256`, and `Md5`, Core can determine the final output path without the
vault key. It checks that path before Argon2id and reports an existing vault as
`PerFileResult.DestinationExists` without throwing an exception for that item.
This avoids KDF work and exception/stack-trace allocation when resuming a large
folder. `FileMode.CreateNew` remains the final race-condition guard. AES retains
the derived-key path calculation because its randomized visible name depends
on salt and the master key and is extremely unlikely to collide.

Use `IProgress<PerFileProgress>` to show:

```csharp
public sealed record PerFileProgress(
    string SourceFilePath,
    double Progress,
    PerFileResult Result = PerFileResult.InProgress);
```

`Result` is `InProgress` for progress updates, then `Succeeded`,
`DestinationExists`, or `Failed` for the terminal update. The UI uses these
structured results to maintain and localize the per-file summary.

Before enumerating the PerFile batch, Core forces an asynchronous continuation
without capturing the caller context. This keeps a run containing many
synchronous collision checks off the Avalonia UI thread. It does not make file
encryption parallel and does not change cancellation or exception propagation.

### 4.6 Decrypt a per-file folder

```csharp
Task DecryptFolderPerFileAsync(
    string sourceFolderPath,
    string destinationFolderPath,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default);
```

The destination folder must not exist. Core recursively processes regular files ending in `.safe`; other files are skipped. Each vault header determines whether the authenticated full filename must be restored. Missing vault files are simply absent from the result.

If any processed vault fails, Core rethrows the error without deleting the
destination tree. Files already written and any partial output remain in place.
The desktop batch UI normally invokes the appropriate operation per queue item
so one failure can be recorded without stopping the remaining valid vaults.

### 4.7 Decrypt only the standalone encrypted output name

```csharp
Task<string> DecryptOutputFileNameAsync(
    string encryptedOutputFileName,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default);
```

Use this when the UI wants to preview an encrypted output name without decrypting the vault contents.

Inputs:

- a basename or path ending in `.safe`;
- the password used to create the vault;
- optional cancellation.

The returned value is the standalone filename stored in the Base64URL name. For long names, this value may have a shortened stem while preserving the final extension. Full vault decryption still restores the complete filename from the encrypted filename chunk inside the vault.

This API accepts only AES Base64URL output names. SHA-256 and MD5 output names are not reversible; use `ReadVaultMetadataAsync` with the vault file to recover its authenticated original filename.

Argon2 runs on a background task. Cancellation is checked before scheduling; the current Argon2 library cannot interrupt a KDF invocation that has already started.

### 4.8 Read authenticated vault metadata

```csharp
Task<VaultMetadata> ReadVaultMetadataAsync(
    string sourcePath,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default);
```

This API validates the password and decrypts only the authenticated filename
chunk; it does not decrypt the file contents. The returned metadata includes
the complete original filename, vault size and modification time, version,
mode, output-filename mode, chunk size, Argon2id parameters, and encryption
algorithm.

Because the call runs Argon2id, invoke it from an explicit UI action such as
**Check password**, not on every password-field change. Clear the caller-owned
password byte array after the call.

### 4.9 Encrypt and decrypt text

`TextCryptoService` uses the existing `Argon2Kdf`, shared asynchronous
`KdfDerivation`, and `AesGcmEngine` implementations. Text is encoded as strict
UTF-8 and handled as one authenticated AES-GCM chunk with index `1` and
`IsLastChunk = true`.

```csharp
var textCrypto = new TextCryptoService();
var encrypted = await textCrypto.EncryptAsync(
    plaintext,
    passwordBytes,
    cancellationToken);

var decrypted = await textCrypto.DecryptAsync(
    encrypted,
    passwordBytes,
    cancellationToken);
```

Rules:

- plaintext is limited to `TextCryptoService.MaximumTextCharacters`, currently
  1,000,000 .NET characters;
- encryption and decryption require non-empty UTF-8 password bytes at Core
  level; the desktop encryption form additionally enforces
  `AppSettings.MinPasswordLength`;
- Argon2id parameters are fixed by text format version 1 at 64 MiB, four
  iterations, and parallelism two;
- each encryption creates a random 16-byte salt and random 4-byte nonce prefix;
- output is an unprefixed Base64URL string. Decryption also accepts the legacy
  `SAFETEXT1.` prefix;
- invalid UTF-8, unsupported versions, malformed Base64URL, unsafe lengths, and
  AES-GCM authentication failures are rejected without returning partial text;
- the caller owns and must clear its password byte array.

`TextCryptoService.IsEncryptedText` performs lightweight format recognition by
checking the decoded `SFTX` magic and version. The desktop Tools form uses this
to distinguish encrypted text from standalone AES-encrypted filenames without
asking the user to choose an input type.

### 4.10 SHA-256 text hashing

```csharp
var hex = TextCryptoService.ComputeSha256Hex(content);
var base64 = TextCryptoService.ComputeSha256Base64(content);
```

Both methods hash strict UTF-8 bytes and enforce the same 1,000,000-character
limit. SHA-256 is one-way and is not an encryption or password-based operation.

### 4.11 Generate a password

```csharp
var password = PasswordGenerator.Generate(new PasswordGeneratorOptions(
    Length: 10,
    IncludeUppercase: true,
    IncludeLowercase: true,
    IncludeNumbers: true,
    IncludeSpecialCharacters: true,
    ExcludeAmbiguousCharacters: false));
```

Lengths from 4 through 64 are accepted. At least one character group must be
enabled, the requested length must accommodate the number of enabled groups,
and every enabled group is guaranteed to occur at least once. Selection and
final shuffling use `RandomNumberGenerator`; generated passwords must never be
logged.

## 5. Output filename protection

Filename protection is an operation-level choice, not a persisted `AppSettings`
value. The desktop encryption form presents MD5, SHA-256, and AES in that order,
defaults the enabled control to MD5 (including after Reset), and passes
`OutputFileNameMode` explicitly to `EncryptFileAsync`,
`EncryptFolderZipAsync`, or `EncryptFolderPerFileAsync`.

AES keeps the reversible Base64URL output-name format described below. SHA-256 and MD5 directly hash the original UTF-8 filename without a salt, password, or master key and write lowercase hexadecimal digests with `.safe` (64 characters for SHA-256 and 32 for MD5). Hashed names cannot be reversed independently; the complete original filename remains authenticated and encrypted with AES-256-GCM inside the vault and is restored from there during decryption. MD5 is provided only for shorter name obfuscation, not cryptographic security.

In AES mode, the visible output name is a self-contained Base64URL payload containing:

```text
format version
Argon2 parameters
salt
encrypted shortened filename
AES-GCM authentication tag
```

The output component ends in `.safe`. To fit the common 255-byte filesystem component limit:

- standalone filename plaintext is limited to 140 UTF-8 bytes;
- Core shortens only the stem on Unicode rune boundaries;
- the final extension from `Path.GetExtension` is preserved;
- an extension longer than 140 UTF-8 bytes is rejected and the UI should ask the user to rename it.

## 6. Desktop integration summary

The desktop shell caches Encrypt, Decrypt, Folder Names, Tools, Logs, Settings,
and About ViewModels. UI strings live in matching neutral-English and Vietnamese resource
files; language and theme changes apply only after Settings is saved. Submit
errors use the shared dialog service, while Serilog records technical events
without sensitive content.

### 6.1 Vault workflows

- Encrypt accepts multiple files or one folder. It reads performance/KDF values
  from Settings, keeps filename protection on the form, processes batches past
  individual failures, and always uses Core's returned output path.
- Folder encryption exposes an operation-scoped multi-folder exclusion list.
  Exclusions are normalized absolute descendant paths, deduplicated, collapsed
  beneath selected parents, and cleared by Reset or a primary-source change.
  The source summary and progress denominator include only non-excluded files.
- Decrypt queues files or folders and isolates per-vault results. Header display
  is unauthenticated until an explicit **Check password** call to
  `ReadVaultMetadataAsync`; changing the password clears verified metadata.
- Decrypt exclusions apply only to physical folders containing `.safe` files,
  not entries inside ZIP vaults. Adding one removes matching queue rows; removing
  one or using Clear all rescans the selected folder roots and restores eligible
  vaults. Reset clears both source roots and exclusions.
- Overwrite is always explicit. File and PerFile output may be overwritten only
  when enabled; ZIP destinations remain new. Failures and cancellation preserve
  existing, completed, and partial decryption output.
- Password byte arrays are cleared by the caller. Conflicting controls remain
  locked during active work, and page-owned status bars report aggregate and
  per-item results.

### 6.2 Tools

- Tools exposes the text crypto, hashing, and password APIs from sections
  4.9–4.11 through four compact tabs.
- Decrypt auto-detects current or legacy text payloads and standalone AES
  filenames; `.safe` is optional. SHA-256 and MD5 names are not reversible and no vault
  picker is offered.
- Text and hashes support copy/save; recovered filenames are copy-only. Text
  save names are `lowercase_hex(SHA256(UTF8(original content))) + ".txt"`.
- Tools never logs input, output, hashes, passwords, or generated passwords.

### 6.3 Folder-name protection

The desktop **Folder Names** page protects descendant directory names without
renaming the selected root or modifying any regular filename or file content.
Its naming choices are MD5, SHA-256, and AES in that order, with MD5 selected
for a new clear root and after Reset; an existing manifest restores and locks
its recorded mode.
It stores an SFTX-encrypted JSON manifest at:

```text
<selected root>/.safefile-names
```

Only `.safefile-names` is recognized. The file must stay alongside the
processed tree: deleting, renaming, editing, or moving it prevents reliable
restoration. The page shows this warning below its action card whenever the
manifest exists.

Manifest plaintext version 1 has this logical shape before SFTX encryption:

```json
{
  "format": "SafeFileFolderMap",
  "version": 1,
  "nameMode": "Aes",
  "directories": [
    {
      "originalPath": "Photos/2025",
      "protectedPath": "Photos/A1b2C3d4E5f6"
    }
  ]
}
```

Paths use portable `/` separators and begin with the selected root basename;
they are not absolute. AES produces a random unpadded Base64URL token. SHA-256
and MD5 produce deterministic lowercase hashes of the complete logical original path.
Neither form uses a `d_` prefix. Existing manifest mappings preserve their
token and lock the password and AES/SHA-256/MD5 mode for incremental runs.
Manifest JSON is serialized directly as UTF-8 Unicode without unnecessary
`\uXXXX` escaping and has an independent 64 MiB plaintext byte limit. This does
not change the 1,000,000-character limit of the user-facing text tools.

Folder-name encryption may exclude operation-scoped descendant directories.
Excluded branches are not renamed or added as new mappings, while mappings
already stored below an excluded branch remain in the encrypted manifest.
Folder-name decryption is disabled until the exclusion list is cleared. The
list supports multiple folders, parent/child collapsing, individual removal,
and Clear all; it is cleared by Reset or a root-folder change.

The app service flushes a sibling temporary manifest and atomically replaces
`.safefile-names` before renaming directories deepest first. This makes mixed
states resumable without rollback after cancellation or a crash:

- Encrypt resumes original/clear mappings and discovers new folders under both
  clear and protected parents.
- Decrypt restores protected mappings, keeps new unmapped clear folders, and
  removes the manifest only after all mapped folders are clear.
- An original-only mapping is pending Encrypt, a protected-only mapping is
  pending Decrypt, both paths existing is a conflict, and neither is stale.
- Manual token renames are inconsistent and are not guessed. Symlinks,
  junctions, and reparse points are skipped.

Password changes invalidate verification without invoking Argon2. Verification
runs from explicit **Check manifest** or an operation, and caller-owned password
bytes are zeroed afterward. Logs include operation, mode, counts, cancellation,
cleanup, and errors but exclude passwords, plaintext manifests, and mappings.
After Encrypt, cancel, failure, or another operation leaves a manifest, the
ViewModel refreshes the scan and directly verifies that manifest with the
current password. It restores the session, mode, counts, and verified state so
the Folder Names Encrypt and Decrypt buttons immediately reflect the refreshed
state; this automatic path does not depend on the guarded Check manifest command.

### 6.4 Progress behavior

| Operation | Progress source |
|---|---|
| File encrypt | Ordered encrypted chunks |
| File decrypt | Authenticated decrypted chunks |
| ZIP encrypt | Source bytes read divided by estimated regular-file input bytes |
| ZIP decrypt | Vault bytes consumed for 0–60%, then uncompressed extracted bytes for 60–100% |
| PerFile | Includes `SourceFilePath`; overall UI progress is `(completed files + current file progress) / total files` |

ZIP encryption remains below 100% until both ZIP production and encryption finish.

The desktop floors incomplete percentage labels, so 99.52% displays as 99%.
It displays 100% only when the operation reports exactly `1.0`. PerFile status
also reports succeeded, failed, and remaining counts plus byte-based speed and
ETA.
The desktop decryption status bar places the current vault name above progress,
shows its complete path in a tooltip, retains aggregate batch counts, and uses
the queued vault sizes to estimate smoothed transfer speed and remaining time.

Encrypt and Decrypt share `TransferMetricsEstimator`. Speed samples use bytes
actually transferred, while ETA uses resolved workload. A successful file
contributes its complete size; a failed file contributes only its last observed
progress; an existing destination or other immediate skip contributes no fake
throughput. Failed and skipped files are nevertheless removed from remaining
work, while cancellation leaves the active item unresolved. This separation
keeps speed meaningful without preventing ETA from converging.

When the desktop receives a decryption folder, filesystem enumeration and
unauthenticated header parsing run off the Avalonia UI thread. Scan results are
collected before queue mutation, then applied to the observable queue in UI
batches and renumbered once. Exclusion rescans use the same asynchronous path;
do not move recursive enumeration or per-vault header I/O back onto the UI
thread.

Progress callbacks may be marshalled by `Progress<T>` to the UI synchronization context. The UI should treat progress as display-only and not use it for correctness decisions.

## 7. Cancellation and UI state

Recommended UI flow:

1. Disable conflicting controls.
2. Create one `CancellationTokenSource`.
3. Await the Core method.
4. Use the returned actual path when one is returned.
5. Handle cancellation separately from failures.
6. Clear password bytes in `finally`.
7. Re-enable controls.

```csharp
try
{
    var actualPath = await encryptor.EncryptFileAsync(
        sourcePath,
        requestedPath,
        passwordBytes,
        chunkSizeBytes,
        kdfParameters,
        encryptFileName,
        cancellation.Token);

    ShowSuccess(actualPath);
}
catch (OperationCanceledException)
{
    ShowCanceled();
}
catch (Exception ex)
{
    ShowFailure(ex.Message);
}
finally
{
    CryptographicOperations.ZeroMemory(passwordBytes);
}
```

## 8. Errors the UI should handle

| Exception | Typical meaning | Suggested UI action |
|---|---|---|
| `ArgumentException` | Empty/short password or invalid argument | Validate input and keep the dialog open |
| `ArgumentOutOfRangeException` | Unsafe chunk/KDF/settings value | Reset to supported range |
| `FileNotFoundException` | Source file disappeared | Ask the user to reselect it |
| `DirectoryNotFoundException` | Source folder disappeared | Ask the user to reselect it |
| `IOException` | Destination conflict, invalid placement, or source changed | Show the exact message and let the user choose another path |
| `PathTooLongException` | Extension cannot fit standalone filename format | Ask the user to shorten the extension |
| `InvalidDataException` | Wrong vault mode, malformed/truncated data, or invalid filename format | Report an invalid/corrupt vault |
| `CryptographicException` | AES-GCM authentication failed or standalone filename password is wrong | Report wrong password or tampered data without exposing details |
| `InvalidOperationException` | Key verifier mismatch, unsafe state, or memory budget unavailable | Show the message; for verifier mismatch report wrong password/corruption |
| `OperationCanceledException` | User cancellation | Show a non-error canceled state |

Core does not display prompts. Naming conflict policy and overwrite confirmation belong to the UI.

## 9. Filesystem behavior

- Folder destinations must be outside their source tree.
- Symlinks, junctions, and reparse points are skipped.
- Source files are opened with read sharing only.
- Encryption uses a fixed source-length snapshot and rejects size changes.
- File and ZIP encryption destinations use `FileMode.CreateNew` by default and
  preserve an existing vault. They use `FileMode.Create` only when
  `overwriteExisting` is explicitly enabled.
- Filename-encrypted output is written directly to its final Base64URL path; the clear requested path is not opened.
- File decrypt uses `FileMode.CreateNew` by default for both clear and restored
  filenames. It uses `FileMode.Create` only with explicit overwrite.
- File and PerFile decrypt do not delete output after failure or cancellation.
  A pre-existing collision is never deleted or modified unless explicit file
  overwrite was enabled.
- ZIP decrypt uses a temporary ZIP, validates extraction paths, and preserves
  partial destination output on failure.
- ZIP extraction rejects entries outside the destination root.

## 10. Cryptographic format

### 10.1 Argon2id

| Parameter | Default | Accepted range |
|---|---:|---:|
| Memory | 64 MiB | 16–256 MiB |
| Iterations | 4 | 1–20 |
| Parallelism | 2 | 1–16 |
| Salt | 16 random bytes | Exactly 16 bytes |

All public workflows run Argon2 through the shared async helper. Argon2 runs once per vault operation, not once per chunk. PerFile mode creates one vault and therefore one KDF invocation per source file.

### 10.2 AES-256-GCM chunks

- key: 32 bytes;
- nonce: random 4-byte vault prefix plus 8-byte little-endian chunk index;
- tag: 16 bytes;
- AAD: 8-byte chunk index plus one-byte `IsLastChunk`;
- filename chunk index: `0`;
- data chunk indexes: start at `1`.

The reader validates nonce prefix, ordered indexes, ciphertext sizes, tags, exactly one final data chunk, truncation, and trailing data.

### 10.3 Vault header v1

The header is 47 bytes. Multi-byte integers are little-endian.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII magic `SAFE` |
| 4 | 1 | Version |
| 5 | 1 | `VaultMode` |
| 6 | 1 | Flags: `0` none, `1` AES, `3` SHA-256, `5` MD5; bit 0 means protected, bit 1 selects SHA-256, bit 2 selects MD5, bits 3–7 are reserved |
| 7 | 4 | Argon2 memory KiB |
| 11 | 4 | Argon2 iterations |
| 15 | 4 | Argon2 parallelism |
| 19 | 16 | Salt |
| 35 | 4 | Nonce prefix |
| 39 | 4 | Chunk size |
| 43 | 4 | Key verifier |

The encrypted filename chunk follows the header, then data chunks.

### 10.4 Authenticated text format v1

The text result is the following binary payload encoded directly with
unpadded Base64URL. New output has no textual prefix; readers retain support
for the legacy `SAFETEXT1.` prefix.

| Offset | Size | Field |
|---:|---:|---|
| 0 | 4 | ASCII magic `SFTX` |
| 4 | 1 | Version `1` |
| 5 | 16 | Random Argon2id salt |
| 21 | 4 | Random AES-GCM nonce prefix |
| 25 | 8 | Ciphertext length, UInt64 little-endian |
| 33 | variable | UTF-8 ciphertext |
| following ciphertext | 16 | AES-GCM authentication tag |

Version 1 fixes Argon2id at 65,536 KiB, four iterations, parallelism two, and a
32-byte derived key. The nonce is the stored random 4-byte prefix followed by
the little-endian chunk index `1`. AAD is that index plus
`IsLastChunk = true`. The parser validates encoded size, magic, version,
declared length, UTF-8, and authentication before returning plaintext.

## 11. Pipeline and memory

`CryptoPipeline` is internal. Encryption uses a sequential producer, parallel AES-GCM consumers, and an ordered writer. Channels and total in-flight chunks are bounded. Before encryption, Core estimates resident chunk buffers, active consumers, and the ZIP pipe, and rejects configurations exceeding half of GC-reported available memory.

Pipeline faults cancel other stages. Encrypt and decrypt both reject missing final chunks and data after the final chunk.

## 12. Build and tests

```powershell
dotnet.exe restore SafeFile.slnx
dotnet.exe build SafeFile/SafeFile.csproj
```

Tests live in `SafeFile.Core.Tests` and cover File, ZIP, PerFile, filename encryption, long-name restoration, empty files, cancellation before KDF, truncation, Zip Slip, incremental file-decryption progress, both ZIP-decryption phases, and ordered multi-consumer output.
Run Core tests only when the current task explicitly requires them.

## 13. Format-change rules

The application has not been published yet, but format changes must still be deliberate:

1. Update `VaultHeader.CurrentVersion` when the serialized layout becomes incompatible.
2. Keep all integer serialization explicitly little-endian.
3. Preserve nonce uniqueness and filename/data index separation.
4. Validate attacker-controlled sizes and KDF parameters before allocation or expensive work.
5. Update this UI contract, GitHub instructions, and round-trip tests together.
