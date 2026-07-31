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
- `DecryptFolderZipAsync`: decrypt to a temporary ZIP and extract directly to
  the requested destination while preserving partial output on failure.
- `EncryptFolderPerFileAsync` / `DecryptFolderPerFileAsync`: one `VaultMode.PerFile` vault per regular file while preserving relative paths. Filename encryption uses each authenticated filename ciphertext; decrypt reads the behavior from each header and restores authenticated names.
- A failed or cancelled decrypt must not delete files or directories from the
  configured output folder. Preserve completed outputs and any partial output
  produced before the failure so the UI can report each vault independently.
- `PerFileProgress` reports the source file path with its per-file percentage.
- `DecryptOutputFileNameAsync` decrypts the standalone Base64URL output name with the password and runs Argon2 off the caller thread; a shortened output name may differ from the complete name restored from the vault.
- `ReadVaultMetadataAsync` validates the password and decrypts only the
  authenticated filename chunk, not the file contents. It returns the complete
  original filename, vault filesystem metadata, format/mode, filename flag,
  chunk size, KDF parameters, and algorithm.
- File and ZIP encryption default `overwriteExisting` to `false`. Use
  create-new semantics unless the caller explicitly enables overwrite.
- File decryption also defaults `overwriteExisting` to `false` for both clear
  and restored encrypted filenames. Only use create semantics after explicit
  overwrite confirmation.
- Derive the vault key and final encrypted output path before opening the destination; never create a clear-name staging vault or truncate the requested placeholder path when filename encryption is enabled.
- Skip symlinks, junctions, and other reparse points.
- Folder destinations must be outside the source tree.
- Empty directories do not need to be preserved.
- Streaming folder progress is based on source bytes read and reaches 100% only after the crypto pipeline completes.

## UI integration contract

- Construct one `FileEncryptor` per active UI operation; shared instances do not provide independent concurrent progress state.
- Convert passwords to UTF-8 bytes, pass the byte array to Core, and zero the caller-owned array in UI `finally` blocks.
- Pass `AppSettings.GetChunkSizeBytes()` and `GetKdfParameters()` to encrypt operations.
- Filename encryption is not stored in `AppSettings`. Read it from the encryption-form checkbox and pass it explicitly to the relevant Core method; a nullable filename-encryption argument defaults to `false`.
- Build encrypted destinations under `AppSettings.DefaultOutputPath` and
  decrypted destinations under `AppSettings.DefaultDecryptOutputPath`, creating
  the configured root when necessary. Keep these two output roots visually and
  semantically distinct. Defaults are `Documents/SafeFile/Encrypted` and
  `Documents/SafeFile/Decrypted`.
- Always use the path returned by `EncryptFileAsync`, `DecryptFileAsync`, and `EncryptFolderZipAsync`; filename encryption can change the basename.
- For encrypted-name file decrypt, `destinationPath` supplies the parent directory and Core restores the authenticated full basename.
- ZIP and PerFile decrypt destination folders must not already exist. Folder destinations must be outside the source tree.
- Use `IProgress<double>` for file/ZIP progress and `IProgress<PerFileProgress>` for current source path plus per-file progress.
- Await `DecryptOutputFileNameAsync` to preview a standalone encrypted name. It may return a shortened stem; full vault decrypt restores the complete stored filename.
- Use `ReadVaultMetadataAsync` after an explicit **Check password** action when
  the UI needs the complete authenticated original filename. Do not invoke it
  on password-field changes because it runs Argon2id.
- Pass the encryption-form overwrite checkbox to `EncryptFileAsync` and
  `EncryptFolderZipAsync`. PerFile mode still requires a new destination
  directory and does not support this checkbox.
- Pass the decryption-form overwrite checkbox to `DecryptFileAsync`. With the
  checkbox disabled, an existing plaintext file must be rejected and preserved
  on the first and every subsequent attempt.
- Handle `OperationCanceledException` as cancellation; key-verifier `InvalidOperationException` or `CryptographicException` as wrong password/tampering; `InvalidDataException` as malformed vault data; and path/I/O exceptions as user-correctable selection conflicts.
- Core never shows prompts. The UI owns pickers, overwrite/naming decisions, password confirmation, success/error messages, and cancellation-token lifetime.

## Current Avalonia UI behavior

- The main window opens centered at 1280×720.
- The encryption view supports selecting or dropping one or more files, or one
  folder. Multiple files are encrypted sequentially into independent `.safe`
  vaults under the configured encrypted-output root; a failed file does not
  prevent later selected files from being attempted. Do not accept a mixed
  file-and-folder drop as one selection.
- Multi-file encryption progress is weighted by the selected files' total byte
  size. The source summary shows the selection count; its filename and path
  tooltips list every selected item on numbered lines.
- Do not add algorithm, chunk-size, or worker-count controls back to the encryption form. Those operational values come from Settings.
- Keep the filename-encryption checkbox on the encryption form only; it is intentionally absent from Settings.
- The encryption form includes an overwrite checkbox for single-file and ZIP
  vault output. Disable it for PerFile folder mode.
- `ConfirmPasswordToggle` controls whether the confirmation label/input are visible and whether matching-password validation runs.
- Both encryption password inputs have independent show/hide controls.
- The encryption status bar is owned by `EncryptViewModel`, not `MainWindowViewModel`. It is hidden before an operation, shown during progress, and retained after success, cancellation, or failure until the user closes it. Its close button cancels while encryption is active.
- Keep status messages on their own row below the output preview/actions so long output names cannot hide them. Truncate long preview names visually with an ellipsis.
- The decryption view parses the unauthenticated header after file selection,
  but only verifies the password and reveals authenticated metadata when the
  user presses **Check password**.
- The decryption view accepts one file, multiple `.safe` files, a folder, and
  drag-and-drop. Keep the picker/drop zone independent from the queue/detail
  region so long metadata does not change the source-selection layout.
- Show each queued vault in a table with vault basename, authenticated original
  filename, size, per-item progress, and success/failure state. In the details
  panel, visually truncate the vault and authenticated original filename with
  an ellipsis. Their tooltips show the full source path and complete original
  filename respectively.
- Lock every picker, drop zone, queue mutation, password field, and option while
  `IsDecrypting` is true. A batch failure must not stop subsequent valid vaults;
  preserve each row's result and show the aggregate result after the batch.
- Display the password-check result beside its button. Clear the verified state
  and metadata when the password changes.
- Keep only the File details section beside Security and options; aggregate
  batch results belong in the bottom progress/status bar. Reset previous item
  progress and result states when a new decrypt batch starts while retaining
  already verified metadata.
- The decryption view offers overwrite and open-folder options. Overwrite
  applies only to `File` and individual `PerFile` vault outputs; a ZIP
  destination folder must remain new. Do not re-add the removed automatic
  collision-renaming option.
- All `ProgressBar` controls use the application-level primary blue `#2563EB`, matching the primary encryption button.
- PerFile encryption's overall progress is
  `(completed file count + current file progress) / total file count`; do not
  display the current file percentage as the overall percentage.
- Every submit-time failure on encryption, decryption, Settings, and Logs is
  shown through `IErrorDialogService`, not as inline error text.
- Cache one ViewModel instance per main-shell page. The bottom status bars stay
  owned by their individual pages rather than the main shell.
- Settings exposes appearance, performance, password/KDF, and separate
  encrypted/decrypted output roots. It does not expose filename encryption or a
  naming policy.
- Language (`en` or `vi`, default `en`) and theme (`Light` or `Dark`) are staged
  in Settings and take effect only after **Save settings**. Do not place these
  controls back in the header. Restoring defaults only populates the form; Save
  is still required.
- On the first run (no `settings.json`), initialize Theme from Avalonia platform
  color settings. Use Dark when the platform preference is unavailable. Persist
  that initial choice; subsequent launches honor the saved Theme.
- UI text belongs in neutral-English `Resources/Strings.resx` and Vietnamese
  `Resources/Strings.vi.resx`. Use `{loc:Tr Key}` in AXAML and
  `LocalizationService` for runtime strings. Both resource files must have
  identical keys. Language changes refresh the live resource bindings; do not
  recreate the Settings page.
- Main navigation includes Encrypt, Decrypt, Logs, Settings, and About. About
  shows product/security/privacy information, runtime details, MIT licensing,
  copyable system information, and access to the log folder.
- Serilog is the only application logging pipeline. Write structured events to
  the daily rolling file sink and `UiLogSink`; technical log messages remain in
  English. The Logs page can filter/search, clear its in-memory display, export,
  auto-scroll, and open the log directory.

## Pipeline and safety

- `CryptoPipeline` is internal. Producer reads sequentially; consumers encrypt in parallel; writer reorders and writes sequentially.
- Use bounded channels and propagate cancellation/failures across all stages.
- Open source files with read sharing only and verify their size is unchanged before completing encryption.
- Keep binary integers explicitly little-endian.
- Reject unsafe lengths and KDF parameters before allocation or expensive work.
- Encryption may clean only output or staging paths created by that encryption
  operation. Decryption must not delete anything from the output folder when a
  vault fails or the batch is cancelled.
- A create-new collision must never delete or modify the pre-existing file.

## Tests

- Tests live in `SafeFile.Core.Tests`.
- Maintain happy-case round trips for `File`, `Zip`, and `PerFile`.
- Also retain regression coverage for empty files, truncation, Zip Slip, progress, and ordered multi-consumer output.
- Retain regression tests proving that file/ZIP encryption and file decryption
  preserve existing destinations by default, overwrite only when explicitly
  enabled, and never delete a collision after `FileMode.CreateNew` fails.
- Retain coverage for `ReadVaultMetadataAsync`, including wrong-password
  rejection and full long-filename recovery.
- Run:

```powershell
dotnet.exe build SafeFile/SafeFile.csproj
```

Run desktop builds with Windows `dotnet.exe`. Do not run Core tests unless the
current task explicitly requires them.

## MCP Integration

- This project uses **Avalonia Build MCP** to enhance GitHub Copilot with real-time access to Avalonia documentation and expert development guidance.
- Configuration: See `.github/copilot-mcp.json`
- **Available MCP Tools:**
  - `search_avalonia_docs` – Search Avalonia documentation, tutorials, API references, migration guides
  - `lookup_avalonia_api` – Look up specific Avalonia classes, properties, methods
  - `get_avalonia_expert_rules` – Load comprehensive Avalonia development rules and best practices
  - `migrate_diagnostics` – Guidance for upgrading Avalonia Developer Tools
  - `analyze_wpf_project`, `migrate_to_xpf`, `migrate_to_avalonia` – WPF-to-Avalonia migration support
- When working on Avalonia UI code (in `SafeFile` project), request Copilot to use these tools for:
  - AXAML syntax validation and optimization
  - Data binding and MVVM pattern guidance
  - Styling, theming, and layout best practices
  - Control and component recommendations
