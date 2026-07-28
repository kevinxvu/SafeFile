# SafeFile Copilot Instructions

## Project

- .NET 10 application with Avalonia UI and reusable `SafeFile.Core`.
- Encrypted files use the `.safe` extension.
- Preserve existing user changes and keep Core independent from UI.

## Core crypto and format

- Derive 32-byte keys with Argon2id.
  - Default: 64 MiB, 4 iterations, up to 2 CPUs.
  - Allowed: 16–256 MiB, 1–20 iterations, parallelism 1–16 for cross-machine portability.
  - Use the shared async KDF helper so public workflows do not block the caller thread; check cancellation before scheduling.
- Encrypt chunks with AES-256-GCM.
  - Nonce: 4-byte random vault prefix + 8-byte little-endian chunk index.
  - Filename uses index `0`; data starts at index `1`. Never reuse a nonce.
  - AAD contains chunk index and `IsLastChunk`.
- Vault header v1 is 47 bytes. Its flags byte records whether output filenames are encrypted for every vault mode.
- Chunk size must be 1–16 MiB.
- Bound total in-flight chunks across channels/reordering. Require estimated resident chunk buffers, including active consumers, to use at most half of GC available memory.
- Enforce encryption password length from `AppSettings.MinPasswordLength`; decryption remains compatible with older vaults.
- The 4-byte password verifier is computed from the Argon2-derived key, never directly from the password.
- Validate mode, chunk order, nonce prefix, sizes, authentication tags, final chunk, and trailing/truncated data before accepting a vault.
- Zero internal password copies and master keys; do not mutate the caller's password buffer.

## I/O modes

- `EncryptFileAsync` / `DecryptFileAsync`: one file, `VaultMode.File`; for standalone output-name encryption, shorten the plaintext stem on UTF-8 boundaries when necessary, preserve its extension, then encrypt and Base64URL-encode it. Keep the complete authenticated original name inside the vault. These APIs return the actual output path.
- `EncryptFolderZipAsync`: `ZipArchive → bounded Pipe → crypto pipeline → one .safe`; filename encryption uses the reversible filename ciphertext rather than a random identifier. Encrypt returns the actual vault path.
- `DecryptFolderZipAsync`: decrypt to temporary ZIP, extract through a staging directory, then move to destination.
- `EncryptFolderPerFileAsync` / `DecryptFolderPerFileAsync`: one `VaultMode.PerFile` vault per regular file while preserving relative paths. Filename encryption uses each authenticated filename ciphertext; decrypt reads the behavior from each header and restores authenticated names.
- `PerFileProgress` reports the source file path with its per-file percentage.
- `DecryptOutputFileNameAsync` decrypts the standalone Base64URL output name with the password and runs Argon2 off the caller thread; a shortened output name may differ from the complete name restored from the vault.
- Derive the vault key and final encrypted output path before opening the destination; never create a clear-name staging vault or truncate the requested placeholder path when filename encryption is enabled.
- Skip symlinks, junctions, and other reparse points.
- Folder destinations must be outside the source tree.
- Empty directories do not need to be preserved.
- Streaming folder progress is based on source bytes read and reaches 100% only after the crypto pipeline completes.

## UI integration contract

- Construct one `FileEncryptor` per active UI operation; shared instances do not provide independent concurrent progress state.
- Convert passwords to UTF-8 bytes, pass the byte array to Core, and zero the caller-owned array in UI `finally` blocks.
- Pass `AppSettings.GetChunkSizeBytes()` and `GetKdfParameters()` to encrypt operations. A nullable filename-encryption argument overrides `AppSettings.EncryptFileNames`.
- Always use the path returned by `EncryptFileAsync`, `DecryptFileAsync`, and `EncryptFolderZipAsync`; filename encryption can change the basename.
- For encrypted-name file decrypt, `destinationPath` supplies the parent directory and Core restores the authenticated full basename.
- ZIP and PerFile decrypt destination folders must not already exist. Folder destinations must be outside the source tree.
- Use `IProgress<double>` for file/ZIP progress and `IProgress<PerFileProgress>` for current source path plus per-file progress.
- Await `DecryptOutputFileNameAsync` to preview a standalone encrypted name. It may return a shortened stem; full vault decrypt restores the complete stored filename.
- Handle `OperationCanceledException` as cancellation; key-verifier `InvalidOperationException` or `CryptographicException` as wrong password/tampering; `InvalidDataException` as malformed vault data; and path/I/O exceptions as user-correctable selection conflicts.
- Core never shows prompts. The UI owns pickers, overwrite/naming decisions, password confirmation, success/error messages, and cancellation-token lifetime.

## Pipeline and safety

- `CryptoPipeline` is internal. Producer reads sequentially; consumers encrypt in parallel; writer reorders and writes sequentially.
- Use bounded channels and propagate cancellation/failures across all stages.
- Open source files with read sharing only and verify their size is unchanged before completing encryption.
- Keep binary integers explicitly little-endian.
- Reject unsafe lengths and KDF parameters before allocation or expensive work.
- On failed operations, clean only output or staging paths created for that operation.

## Tests

- Tests live in `SafeFile.Core.Tests`.
- Maintain happy-case round trips for `File`, `Zip`, and `PerFile`.
- Also retain regression coverage for empty files, truncation, Zip Slip, progress, and ordered multi-consumer output.
- Run:

```bash
dotnet test SafeFile.slnx
dotnet build SafeFile.slnx
```
