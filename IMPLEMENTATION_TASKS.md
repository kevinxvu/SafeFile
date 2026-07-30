# SafeFile Implementation Tasks & Progress Checklist

This document tracks the full implementation plan for the SafeFile app (.NET 10 + Avalonia + MVVM), with detailed tasks and a checklist to monitor progress.

---

## Progress Summary

- Overall progress: **14 / 14 main tasks completed**
- Status legend:
  - `[ ]` Not started
  - `[-]` In progress
  - `[x]` Done

---

## Task 1 — Solution Restructure (`SafeFile.Core` + `SafeFile` wiring)

**Checklist**
- [x] Create `SafeFile.Core` class library targeting `.NET 10`
- [x] Add `SafeFile.Core` to solution
- [x] Add reference from `SafeFile` (UI) -> `SafeFile.Core`
- [x] Add core crypto package (`Konscious.Security.Cryptography.Argon2`)
- [x] Verify solution builds after restructuring

**Deliverable**
- Clean two-layer architecture: UI project + reusable core project.

---

## Task 2 — Core Crypto: Argon2id Key Derivation

**Checklist**
- [x] Create `SafeFile.Core/Crypto/Argon2Kdf.cs`
- [x] Implement Argon2id derive-key API with configurable params
- [x] Support defaults: Memory=65536KB, Iterations=4, Parallelism=2
- [x] Generate/consume 16-byte salt
- [x] Zero sensitive arrays with `CryptographicOperations.ZeroMemory`
- [x] Pin password buffer when processing in memory

**Deliverable**
- Secure 32-byte master key derivation from password.

---

## Task 3 — Core Crypto: AES-256-GCM Chunk Engine

**Checklist**
- [x] Create `SafeFile.Core/Crypto/AesGcmEngine.cs`
- [x] Encrypt/decrypt chunk API using AES-256-GCM
- [x] Build nonce as `noncePrefix(4B) + chunkIndex(8B)`
- [x] Build AAD as `chunkIndex(8B) + isLastChunk(1B)`
- [x] Validate tag mismatch handling (fail-fast)

**Deliverable**
- Deterministic, authenticated per-chunk encryption/decryption.

---

## Task 4 — Vault File Format (`.safe`) Header

**Checklist**
- [x] Create `SafeFile.Core/Format/VaultMode.cs`
- [x] Create `SafeFile.Core/Format/VaultHeader.cs`
- [x] Implement header fields: Magic, Version, Mode, Argon2 params, Salt, NoncePrefix, ChunkSize
- [x] Implement `WriteTo(Stream)` for serialization
- [x] Implement `ReadFrom(Stream)` for deserialization + validation
- [x] Add compatibility checks (magic/version)

**Deliverable**
- Stable, self-describing `.safe` header format.

---

## Task 5 — Multithread Pipeline (Producer -> Consumers -> Writer)

**Checklist**
- [x] Create `SafeFile.Core/Pipeline/CryptoPipeline.cs`
- [x] Producer reads source stream into indexed chunks
- [x] Consumers encrypt/decrypt chunks in parallel via `System.Threading.Channels`
- [x] Writer ensures ordered output using in-memory ordering buffer (`PriorityQueue`/map)
- [x] Add cancellation support (`CancellationToken`) across all stages
- [x] Enforce mode-specific failure behavior: clean incomplete encryption output and preserve decryption output

**Deliverable**
- High-throughput, ordered, corruption-safe processing pipeline.

---

## Task 6 — Folder Handling Modes (Zip on-the-fly + Per-file)

**Checklist**
- [x] Create `SafeFile.Core/IO/StreamZipper.cs` for in-memory/on-the-fly zip stream
- [x] Implement mode `ZipFolder` (single output `.safe`)
- [x] Decryption with on-the-fly zip extraction
- [x] Validate stream-based zip operations
- [x] Per-file mode with independent vaults, filename restoration, and progress

**Deliverable**
- Full Zip folder encryption/decryption strategy aligned with spec.

**Implementation Notes:**
- `StreamZipper.CreateZipStreamAsync()`: in-memory zip creation from folder (no temp files)
- `StreamZipper.ExtractZipStreamAsync()`: restores folder structure on decrypt
- Integrated into `FileEncryptor`: ZIP and PerFile encrypt/decrypt workflows.
- ZIP decryption preserves partial extracted output on failure or cancellation.

---

## Task 7 — Core Application Service Layer

**Checklist**
- [x] Create high-level orchestration service (e.g., `FileEncryptor`)
- [x] Implement `EncryptFileAsync(...)` end-to-end flow
- [x] Implement `DecryptFileAsync(...)` end-to-end flow
- [x] Hook header read/write + KDF + pipeline + folder mode
- [x] Add progress reporting (`IProgress<double>`)

**Deliverable**
- Simple API surface for UI to call encryption/decryption workflows.

---

## Task 8 — Settings Model & Persistence

**Checklist**
- [x] Create `SafeFile.Core/Models/AppSettings.cs`
- [x] Create settings persistence service (`Load/Save` JSON)
- [x] Store in cross-platform app data path (`ApplicationData`/`~/.config`)
- [x] Include chunk size, threads, Argon2 params, output defaults
- [x] Ensure no password/key/path-sensitive logs are persisted
- [x] Add restore-defaults behavior

**Deliverable**
- Cross-platform settings subsystem with safe defaults.

**Implementation Details:**
- `AppSettings.cs`: POCO w/ safe property validators, `GetDefaults()`
- `SettingsService.cs`:
  - Cross-platform app data path (Windows: `%AppData%/SafeFile`, Unix: `~/.SafeFile`)
  - JSON-based persistence
  - Settings validation (bounds checking, enum validation)
  - Caching for performance
  - `RestoreDefaults()` on-demand
- Settings include: language, theme, chunk size, threads, CPU priority,
  Argon2 parameters, distinct encrypted/decrypted output paths, password
  confirmation toggle, and minimum password length.
- **NO password/key/secret data persisted**

---

## Task 9 — Main Shell UI (Layout + Navigation + Status Bar)

**Checklist**
- [x] Redesign `MainWindow.axaml` with sidebar + content region
- [x] Add menu items: Encrypt, Decrypt, Logs, Settings, About
- [x] Add localized current-page header; language/theme controls live in Settings
- [x] Add bottom status bar (file, progress %, speed, ETA, cancel)
- [x] Implement navigation state in `MainWindowViewModel`

**Deliverable**
- Full application shell matching target UX direction.

**Implementation Notes**
- Encrypt and Decrypt own their status bars so operation-specific state,
  progress, and cancellation remain isolated within each workflow.
- Navigation keeps one instance of each page. Switching pages no longer loses
  the running operation's progress or cancellation state.

---

## Task 10 — Encrypt Screen (View + ViewModel)

**Checklist**
- [x] Create `Views/EncryptView.axaml`
- [x] Create `ViewModels/EncryptViewModel.cs`
- [x] Source picker (file/folder) + drag/drop zone
- [x] Password + confirm + strength indicator
- [x] Options: filename encryption, overwrite, folder ZIP/per-file mode
- [x] Commands: Browse, Encrypt, Reset, Cancel, Open output folder
- [x] Bind real progress from core service
- [x] Reject existing file/ZIP vaults unless overwrite is explicitly enabled
- [x] Preserve an existing vault when overwrite is disabled

**Deliverable**
- Functional encrypt flow UI matching spec.

**Implementation Notes**
- The overwrite checkbox applies to single-file and ZIP vault outputs.
- Per-file folder mode keeps its stricter contract: the destination directory
  must not already exist.
- `EncryptFileAsync` and `EncryptFolderZipAsync` default
  `overwriteExisting` to `false`.

---

## Task 11 — Decrypt Screen (View + ViewModel)

**Checklist**
- [x] Create `Views/DecryptView.axaml`
- [x] Create `ViewModels/DecryptViewModel.cs`
- [x] `.safe` picker + drag/drop zone
- [x] Parse header and show file info panel
- [x] Password input + verification flow
- [x] Output path options (overwrite/open folder)
- [x] Commands: Browse, Decrypt, Cancel
- [x] Add `ReadVaultMetadataAsync` for authenticated metadata and full original filename
- [x] Show password result beside the verification button
- [x] Show the complete original filename without ellipsis
- [x] Enforce overwrite consistently for clear and encrypted filenames

**Deliverable**
- Functional decrypt flow with metadata-aware UX.

**Implementation Notes**
- Metadata is read when the user presses **Check password**. If verification
  was skipped, each vault is authenticated automatically immediately before
  its decryption starts. Password changes do not automatically run Argon2id.
- Successful verification displays the complete authenticated filename, vault
  size/time, format version, mode, chunk size, KDF parameters, and algorithm.
- `DecryptFileAsync` defaults `overwriteExisting` to `false`; an existing
  plaintext file is preserved unless overwrite is explicitly enabled.
- The removed automatic-rename option is not part of the current decrypt UI.
- The source queue accepts one file, multiple files, or folders recursively;
  every vault reports its own verification result, progress, and final status.

---

## Task 12 — Settings Screen (View + ViewModel)

**Checklist**
- [x] Create `Views/SettingsView.axaml`
- [x] Create `ViewModels/SettingsViewModel.cs`
- [x] Performance section (chunk size, thread count, CPU priority)
- [x] Security section (KDF + policy toggles)
- [x] Output section (paths; naming policy is operation-specific)
- [x] Save + Restore Defaults actions

**Deliverable**
- Editable settings screen integrated with persisted settings.

**Implementation Notes**
- Encrypted and decrypted output paths are configured independently.
- Filename encryption is selected per encryption operation and is intentionally
  not persisted as a global naming-policy setting.
- English/Vietnamese language and Light/Dark theme are applied only after Save.
- On first run, theme follows the system preference and falls back to Dark.
- Restore Defaults stages values in the form and requires Save to apply them.

---

## Task 13 — Logs + Utility Services

**Checklist**
- [x] Create `Views/LogView.axaml`
- [x] Create `Services/LogService.cs`
- [x] Add in-memory log collection with level/timestamp
- [x] Add clear/filter capability
- [x] Create file/folder picker abstraction using Avalonia `StorageProvider`

**Deliverable**
- Basic operational logs and reusable platform-safe picker services.

**Implementation Notes**
- Serilog writes structured events to both a bounded in-memory UI sink and an
  asynchronous rolling file sink.
- Log files roll daily and at 10 MB, with the latest 30 files retained under
  the platform-local application data directory.
- The log screen supports level filtering, text search, auto-scroll, clearing
  the in-memory view, exporting the filtered view, and opening the log folder.
- File picker utilities support single/multiple files, folders, and save-file
  selection through Avalonia `StorageProvider`.
- Passwords, derived keys, salts, checksums, and other secret material are
  never included in application log events.

---

## Task 14 — Integration, Validation, and UI Polish

**Checklist**
- [x] Connect all ViewModels to core services
- [x] End-to-end manual test: encrypt/decrypt file and folder modes
- [x] Verify cancellation behavior and partial file cleanup
- [x] Verify wrong password fail-fast path
- [x] Apply visual polish (spacing, cards, buttons, theme consistency)
- [x] Run full build and fix compile issues

**Deliverable**
- Working, build-clean MVP aligned with technical specification.

**Integration and Validation Notes**
- `MainWindowViewModel` retains one instance of Encrypt, Decrypt, Logs,
  Settings, and About. Encrypt/Decrypt create one `FileEncryptor` per active
  operation and use persisted chunk, thread, KDF, and output settings.
- File, ZIP-folder, and PerFile workflows are wired end to end. Decrypt accepts
  one file, multiple files, folders, and drag-and-drop, with per-vault and
  aggregate progress.
- Cancellation is propagated through linked tokens. UI-owned password byte
  arrays and Core-owned key material are zeroed in `finally` blocks.
- Failed encryption removes only output created by that encryption operation.
  Failed or cancelled decryption never cleans the configured output folder;
  completed and partial outputs are preserved.
- The header key verifier rejects a wrong password before full payload
  decryption. Authentication and malformed-vault errors are reported through
  the shared error dialog without logging secrets.
- Submit errors use modal dialogs across Encrypt, Decrypt, Settings, and Logs.
  Inputs and drop zones are locked during active decrypt operations.
- All user-facing text is available in neutral English and Vietnamese resources
  with live binding refresh. Light/Dark semantic brushes cover the shell,
  cards, dialogs, controls, status bars, and drag/drop states.
- Windows full-solution build completed successfully with zero warnings and
  zero errors. The Core test project was compiled but its test suite was not
  executed, per project workflow.

---

## How to Update Progress

When a task starts, mark header checklist item(s) as `[-]` in your working notes or convert first checkbox to in-progress marker in commit notes.
When a task is finished, change all related checkboxes to `[x]`.

Recommended cadence:
- Update this file after each completed main task.
- Keep commit messages aligned with task number (e.g., `Task 5: Implement channel pipeline ordering`).
