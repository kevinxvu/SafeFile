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
- clean incomplete output created by a failed operation.

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
| `DefaultChunkSizeMb` | Chunk-size selector, valid range 1–16 MiB |
| `MaxThreads` | Encryption worker count |
| `Argon2MemorySizeKb` | KDF memory setting |
| `Argon2Iterations` | KDF iteration setting |
| `Argon2Parallelism` | Portable KDF parallelism, valid range 1–16 |
| `DefaultOutputPath` | Initial output picker location |
| `NamingPolicy` | UI conflict policy; Core does not resolve naming prompts |
| `EncryptFileNames` | Default filename-encryption toggle |
| `ConfirmPasswordToggle` | UI-only password confirmation preference |
| `MinPasswordLength` | Minimum password byte length enforced during encryption |

Use:

```csharp
var chunkSizeBytes = settings.GetChunkSizeBytes();
var kdfParameters = settings.GetKdfParameters();
```

`SettingsService.Save` normalizes unsafe values before writing `settings.json`.

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
    bool? encryptFileName = null,
    CancellationToken cancellationToken = default);
```

UI inputs:

- `sourcePath`: existing regular source file.
- `destinationPath`: requested `.safe` path and parent directory.
- `passwordBytes`: UTF-8 password bytes.
- `chunkSizeBytes`: 1–16 MiB.
- `kdfParams`: normally `settings.GetKdfParameters()`.
- `encryptFileName`: `null` uses `settings.EncryptFileNames`; otherwise overrides it.
- `cancellationToken`: token owned by the UI operation.

Return value:

- filename encryption off: returns `destinationPath`;
- filename encryption on: returns the actual Base64URL `.safe` path in the same parent directory.

When filename encryption is on, Core derives the final encrypted path before opening output. The clear-name `destinationPath` is not created or truncated. The UI must use the returned path for notifications, recent files, reveal-in-folder, and subsequent decrypt operations.

Example:

```csharp
var actualVaultPath = await encryptor.EncryptFileAsync(
    sourcePath,
    requestedVaultPath,
    passwordBytes,
    settings.GetChunkSizeBytes(),
    settings.GetKdfParameters(),
    encryptFileName: encryptNameToggle,
    cancellationToken);
```

### 4.2 Decrypt one file

```csharp
Task<string> DecryptFileAsync(
    string sourcePath,
    string destinationPath,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default);
```

UI inputs:

- `sourcePath`: existing `VaultMode.File` vault.
- `destinationPath`: requested plaintext output path or a placeholder path whose parent directory should receive the restored filename.
- `passwordBytes`: non-empty UTF-8 password bytes.

Return value:

- vault flag off: returns and writes exactly `destinationPath`;
- vault flag on: ignores the destination basename, restores the complete authenticated filename from the vault, writes it under `Path.GetDirectoryName(destinationPath)`, and returns that actual path.

The UI must use the returned path. Do not assume the basename selected before decrypt is the basename Core used.

### 4.3 Encrypt a folder as one ZIP vault

```csharp
Task<string> EncryptFolderZipAsync(
    string sourceFolderPath,
    string destinationPath,
    byte[] passwordBytes,
    int chunkSizeBytes = 1_048_576,
    Argon2Parameters? kdfParams = null,
    bool? encryptFileName = null,
    CancellationToken cancellationToken = default);
```

UI inputs match single-file encryption, except the source is an existing folder. `destinationPath` must be outside the source tree.

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

Core decrypts to a temporary seekable ZIP, extracts into a staging directory, validates extraction paths, and moves the completed staging directory to `destinationFolder`.

The stored encrypted filename can be displayed separately with `DecryptOutputFileNameAsync`, but ZIP extraction always uses the destination folder selected by the UI.

### 4.5 Encrypt a folder as independent per-file vaults

```csharp
Task EncryptFolderPerFileAsync(
    string sourceFolderPath,
    string destinationFolderPath,
    byte[] passwordBytes,
    int chunkSizeBytes = 1_048_576,
    Argon2Parameters? kdfParams = null,
    bool? encryptFileNames = null,
    CancellationToken cancellationToken = default);
```

UI inputs:

- source folder must exist;
- destination folder must not exist and must be outside the source tree;
- `encryptFileNames = null` uses the setting default.

Core preserves relative subfolder structure but does not preserve empty directories. Each regular file receives its own salt, key, header, filename chunk, and data chunks.

Use `IProgress<PerFileProgress>` to show:

```csharp
public sealed record PerFileProgress(
    string SourceFilePath,
    double Progress);
```

### 4.6 Decrypt a per-file folder

```csharp
Task DecryptFolderPerFileAsync(
    string sourceFolderPath,
    string destinationFolderPath,
    byte[] passwordBytes,
    CancellationToken cancellationToken = default);
```

The destination folder must not exist. Core recursively processes regular files ending in `.safe`; other files are skipped. Each vault header determines whether the authenticated full filename must be restored. Missing vault files are simply absent from the result.

If any processed vault fails, Core removes the destination tree created for that operation and rethrows the error.

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

Argon2 runs on a background task. Cancellation is checked before scheduling; the current Argon2 library cannot interrupt a KDF invocation that has already started.

## 5. Filename encryption

When enabled, the visible output name is not a random identifier. It is a self-contained Base64URL payload containing:

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

The complete original filename is independently encrypted in the vault filename chunk and is never shortened there.

## 6. Progress behavior

| Operation | Progress source |
|---|---|
| File encrypt | Ordered encrypted chunks |
| File decrypt | Authenticated decrypted chunks |
| ZIP encrypt | Source bytes read divided by estimated regular-file input bytes |
| PerFile | Resets for each file and includes `SourceFilePath` |

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
- Direct file destinations use `FileMode.Create` and may truncate an existing selected destination.
- Filename-encrypted output is written directly to its final Base64URL path; the clear requested path is not opened.
- File decrypt deletes incomplete plaintext output after failure.
- ZIP decrypt uses a temporary ZIP plus an atomic staging-directory move.
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
| 6 | 1 | Flags; bit 0 means output filename encryption |
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

```bash
dotnet restore SafeFile.slnx
dotnet build SafeFile.slnx
dotnet test SafeFile.slnx
```

Tests live in `SafeFile.Core.Tests` and cover File, ZIP, PerFile, filename encryption, long-name restoration, empty files, cancellation before KDF, truncation, Zip Slip, progress, and ordered multi-consumer output.

## 13. Format-change rules

The application has not been published yet, but format changes must still be deliberate:

1. Update `VaultHeader.CurrentVersion` when the serialized layout becomes incompatible.
2. Keep all integer serialization explicitly little-endian.
3. Preserve nonce uniqueness and filename/data index separation.
4. Validate attacker-controlled sizes and KDF parameters before allocation or expensive work.
5. Update this UI contract, GitHub instructions, and round-trip tests together.
