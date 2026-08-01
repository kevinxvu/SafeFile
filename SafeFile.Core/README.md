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

## 2. Package layout

```text
SafeFile.Core/
├── Crypto/
│   ├── Argon2Kdf.cs
│   └── AesGcmEngine.cs
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
└── Services/SettingsService.cs
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
- `outputFileNameMode`: pass `None`, `Aes`, or `Sha256` from the encryption form; it defaults to `None`.
- `cancellationToken`: token owned by the UI operation.
- `overwriteExisting`: replace an existing vault only after explicit user
  confirmation; the default `false` preserves an existing destination.

Return value:

- `None`: returns `destinationPath`;
- `Aes`: returns the actual Base64URL `.safe` path in the same parent directory;
- `Sha256`: returns `lowercase_hex(SHA256(UTF8(originalFileName))) + ".safe"` in the same parent directory.

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
    bool overwriteExisting = false);
```

UI inputs match single-file encryption, except the source is an existing folder. `destinationPath` must be outside the source tree.
`overwriteExisting` follows the same explicit-confirmation rule as
`EncryptFileAsync`.

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
    bool overwriteExisting = false);
```

UI inputs:

- source folder must exist;
- destination folder may already exist but must be outside the source tree;
- pass `outputFileNameMode` explicitly from the encryption form; it defaults to `None`.
- `overwriteExisting` controls whether existing per-file vaults are replaced.

Core preserves relative subfolder structure but does not preserve empty directories. Each regular file receives its own salt, key, header, filename chunk, and data chunks.
When overwrite is disabled, an existing vault is preserved and recorded as a
per-file failure. Core continues encrypting later source files and throws one
summary error after every file has been attempted. Successful outputs and the
destination directory are preserved.

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

This API accepts only AES Base64URL output names. A SHA-256 output name is not reversible; use `ReadVaultMetadataAsync` with the vault file to recover its authenticated original filename.

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

## 5. Output filename protection

Filename protection is an operation-level choice, not a persisted `AppSettings` value. The desktop encryption form owns the checkbox and AES/SHA-256 choice and passes `OutputFileNameMode` explicitly to `EncryptFileAsync`, `EncryptFolderZipAsync`, or `EncryptFolderPerFileAsync`.

AES keeps the reversible Base64URL output-name format described below. SHA-256 directly hashes the original UTF-8 filename without a salt, password, or master key and writes the lowercase 64-character hexadecimal digest with `.safe`. SHA-256 names cannot be reversed independently; the complete original filename remains authenticated and encrypted with AES-256-GCM inside the vault and is restored from there during decryption.

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

## 6. Current desktop encryption integration

The Avalonia encryption form currently integrates Core as follows:

- one or more files, or one folder, can be selected with a picker or
  drag-and-drop; mixed file-and-folder drops are rejected;
- a multi-file selection calls `EncryptFileAsync` sequentially for each source
  and writes one independent vault per file under
  `AppSettings.DefaultOutputPath`; one failure does not prevent later selected
  files from being attempted;
- multi-file overall progress is weighted by total selected source bytes, and
  the source filename/path tooltips show numbered lists;
- algorithm, chunk size, and worker count are not editable on the form;
- chunk size, worker count, and Argon2id parameters come from `AppSettings`;
- all encrypted output is written under `AppSettings.DefaultOutputPath`, which is created when necessary;
- the filename-encryption checkbox exists only on the encryption form and is passed explicitly to Core;
- the overwrite checkbox is passed to file and ZIP encryption; when disabled,
  an existing vault is rejected and preserved;
- overwrite is passed to PerFile mode; its destination folder may already
  exist, and collisions are collected while later files continue;
- the estimated output label uses a placeholder for encrypted names because the final Base64URL name depends on a random salt created during encryption;
- password confirmation is shown and validated only when `ConfirmPasswordToggle` is enabled;
- each password field has an independent show/hide button;
- the operation status bar belongs to the encryption view, appears only after an operation starts, supports cancellation while running, and remains visible after completion, cancellation, or failure until dismissed;
- the form uses the actual path returned by Core when reporting successful file or ZIP encryption.

The Settings screen does not expose filename encryption or an output-name
collision policy. File and ZIP encryption default to no overwrite and require
explicit UI confirmation to replace an existing vault. PerFile preserves
existing vault collisions unless overwrite is explicitly enabled.

Settings owns the language, theme, performance, password/KDF, encrypted-output,
and decrypted-output values. Language and theme are staged in the form and are
applied only after **Save settings**. Restoring defaults populates the form but
does not apply or persist the values until Save.
On the first run, the desktop app detects the platform Light/Dark preference and
stores it as the initial selection. If the platform cannot report a preference,
Dark is used. Later launches always honor the saved setting.

The complete original filename is independently encrypted in the vault filename chunk and is never shortened there.

## 6.1 Current desktop decryption integration

The Avalonia decryption form integrates Core as follows:

- one `.safe` file, multiple files, or a folder can be selected with pickers or
  drag-and-drop; duplicates are ignored;
- selected vaults appear in a queue table with basename, authenticated original
  filename, size, per-item progress, and status;
- the details panel truncates long vault and authenticated original filenames;
  the vault tooltip shows its complete source path and the original-filename
  tooltip shows the complete authenticated name;
- the unauthenticated header is parsed immediately to show format, mode, chunk
  size, KDF summary, and algorithm;
- the password is not checked while the user types;
- pressing **Check password** calls `ReadVaultMetadataAsync`, which runs
  Argon2id once, validates the key verifier, and decrypts only the filename
  chunk;
- the password result is displayed beside the check button;
- successful verification makes the complete original filename available in
  its tooltip and displays vault size and modification time, KDF
  memory/iterations/parallelism, version, mode, chunk size, and algorithm;
- changing the password clears the verified metadata until the user checks it
  again;
- all source inputs, drop zones, queue mutation controls, password input, and
  options are locked while a batch is decrypting;
- an invalid or failed vault records its own error and does not prevent later
  valid queue items from running;
- aggregate progress is based on completed queue items plus the current item's
  progress, and the table retains individual success/failure results;
- aggregate results are shown in the bottom status bar rather than a separate
  Overview tab; starting another decrypt run resets prior progress/results while
  retaining verified metadata;
- automatic collision renaming is not offered;
- when overwrite is disabled, both clear-name and restored encrypted-name
  destinations use create-new semantics and an existing file is preserved;
- when overwrite is enabled, `DecryptFileAsync` receives
  `overwriteExisting: true` and replaces the existing file;
- overwrite applies to `File` and individual `PerFile` outputs only; ZIP
  destination folders must not already exist;
- password and derived-key buffers owned by the caller are cleared after use.
- submit-time errors are shown through a dialog rather than inline error text;
- failure or cancellation never triggers cleanup of the configured output
  folder.

## 6.2 Desktop shell, localization, logs, and About

- `MainWindowViewModel` keeps one ViewModel instance for each page: Encrypt,
  Decrypt, Logs, Settings, and About.
- Page-specific bottom status bars remain inside their pages; they are not moved
  into the main shell.
- All user-facing AXAML text is stored in `SafeFile/Resources/Strings.resx`
  (neutral English) and `Strings.vi.resx`. `LocalizationService` updates live
  bindings when the saved language changes.
- English is the default. Language and Light/Dark theme controls live only in
  Settings and apply on Save; the header contains only the current page title.
- Serilog writes structured events to a daily rolling file and the in-memory UI
  sink. The Logs page supports level filtering, search, clear-display, export,
  auto-scroll, and opening the log directory.
- The About page explains the cryptographic and local-processing model, reads
  version/runtime information from the running application, can copy diagnostic
  system information, opens the log folder, and identifies the MIT license.

## 6.3 Progress behavior

| Operation | Progress source |
|---|---|
| File encrypt | Ordered encrypted chunks |
| File decrypt | Authenticated decrypted chunks |
| ZIP encrypt | Source bytes read divided by estimated regular-file input bytes |
| PerFile | Includes `SourceFilePath`; overall UI progress is `(completed files + current file progress) / total files` |

ZIP encryption remains below 100% until both ZIP production and encryption finish.

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
| 6 | 1 | Flags: `0` none, `1` AES, `3` SHA-256; bit 0 means protected, bit 1 selects SHA-256, bits 2–7 are reserved |
| 7 | 4 | Argon2 memory KiB |
| 11 | 4 | Argon2 iterations |
| 15 | 4 | Argon2 parallelism |
| 19 | 16 | Salt |
| 35 | 4 | Nonce prefix |
| 39 | 4 | Chunk size |
| 43 | 4 | Key verifier |

The encrypted filename chunk follows the header, then data chunks.

## 11. Pipeline and memory

`CryptoPipeline` is internal. Encryption uses a sequential producer, parallel AES-GCM consumers, and an ordered writer. Channels and total in-flight chunks are bounded. Before encryption, Core estimates resident chunk buffers, active consumers, and the ZIP pipe, and rejects configurations exceeding half of GC-reported available memory.

Pipeline faults cancel other stages. Encrypt and decrypt both reject missing final chunks and data after the final chunk.

## 12. Build and tests

```powershell
dotnet.exe restore SafeFile.slnx
dotnet.exe build SafeFile/SafeFile.csproj
```

Tests live in `SafeFile.Core.Tests` and cover File, ZIP, PerFile, filename encryption, long-name restoration, empty files, cancellation before KDF, truncation, Zip Slip, progress, and ordered multi-consumer output.
Run Core tests only when the current task explicitly requires them.

## 13. Format-change rules

The application has not been published yet, but format changes must still be deliberate:

1. Update `VaultHeader.CurrentVersion` when the serialized layout becomes incompatible.
2. Keep all integer serialization explicitly little-endian.
3. Preserve nonce uniqueness and filename/data index separation.
4. Validate attacker-controlled sizes and KDF parameters before allocation or expensive work.
5. Update this UI contract, GitHub instructions, and round-trip tests together.
