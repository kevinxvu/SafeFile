# SafeFile Implementation Tasks & Progress Checklist

This document tracks the full implementation plan for the SafeFile app (.NET 10 + Avalonia + MVVM), with detailed tasks and a checklist to monitor progress.

---

## Progress Summary

- Overall progress: **3 / 14 main tasks completed**
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
- [ ] Create `SafeFile.Core/Format/VaultMode.cs`
- [ ] Create `SafeFile.Core/Format/VaultHeader.cs`
- [ ] Implement header fields: Magic, Version, Mode, Argon2 params, Salt, NoncePrefix, ChunkSize
- [ ] Implement `WriteTo(Stream)` for serialization
- [ ] Implement `ReadFrom(Stream)` for deserialization + validation
- [ ] Add compatibility checks (magic/version)

**Deliverable**
- Stable, self-describing `.safe` header format.

---

## Task 5 — Multithread Pipeline (Producer -> Consumers -> Writer)

**Checklist**
- [ ] Create `SafeFile.Core/Pipeline/CryptoPipeline.cs`
- [ ] Producer reads source stream into indexed chunks
- [ ] Consumers encrypt/decrypt chunks in parallel via `System.Threading.Channels`
- [ ] Writer ensures ordered output using in-memory ordering buffer (`PriorityQueue`/map)
- [ ] Add cancellation support (`CancellationToken`) across all stages
- [ ] Ensure partial output cleanup on cancellation/error

**Deliverable**
- High-throughput, ordered, corruption-safe processing pipeline.

---

## Task 6 — Folder Handling Modes (Zip on-the-fly + Per-file)

**Checklist**
- [ ] Create `SafeFile.Core/IO/StreamZipper.cs` for in-memory/on-the-fly zip stream
- [ ] Implement mode `ZipFolder` (single output `.safe`)
- [ ] Implement mode `PerFile` (per-file `.safe` + `index.safe` metadata)
- [ ] Restore original folder structure during decrypt
- [ ] Validate behavior for large folder trees

**Deliverable**
- Two folder encryption strategies aligned with spec.

---

## Task 7 — Core Application Service Layer

**Checklist**
- [ ] Create high-level orchestration service (e.g., `FileEncryptor`)
- [ ] Implement `EncryptAsync(...)` end-to-end flow
- [ ] Implement `DecryptAsync(...)` end-to-end flow
- [ ] Hook header read/write + KDF + pipeline + folder mode
- [ ] Add progress reporting (`IProgress<double>`)

**Deliverable**
- Simple API surface for UI to call encryption/decryption workflows.

---

## Task 8 — Settings Model & Persistence

**Checklist**
- [ ] Create `SafeFile.Core/Models/AppSettings.cs`
- [ ] Create settings persistence service (`Load/Save` JSON)
- [ ] Store in cross-platform app data path (`ApplicationData`/`~/.config`)
- [ ] Include chunk size, threads, Argon2 params, output defaults
- [ ] Ensure no password/key/path-sensitive logs are persisted
- [ ] Add restore-defaults behavior

**Deliverable**
- Cross-platform settings subsystem with safe defaults.

---

## Task 9 — Main Shell UI (Layout + Navigation + Status Bar)

**Checklist**
- [ ] Redesign `MainWindow.axaml` with sidebar + content region
- [ ] Add menu items: Encrypt, Decrypt, Logs, Settings, About
- [ ] Add top-right controls (language/theme placeholders)
- [ ] Add bottom status bar (file, progress %, speed, ETA, cancel)
- [ ] Implement navigation state in `MainWindowViewModel`

**Deliverable**
- Full application shell matching target UX direction.

---

## Task 10 — Encrypt Screen (View + ViewModel)

**Checklist**
- [ ] Create `Views/EncryptView.axaml`
- [ ] Create `ViewModels/EncryptViewModel.cs`
- [ ] Source picker (file/folder) + drag/drop zone
- [ ] Password + confirm + strength indicator
- [ ] Options: algorithm label, chunk size, threads, folder mode
- [ ] Commands: Browse, Encrypt, Reset, Cancel, Open output folder
- [ ] Bind real progress from core service

**Deliverable**
- Functional encrypt flow UI matching spec.

---

## Task 11 — Decrypt Screen (View + ViewModel)

**Checklist**
- [ ] Create `Views/DecryptView.axaml`
- [ ] Create `ViewModels/DecryptViewModel.cs`
- [ ] `.safe` picker + drag/drop zone
- [ ] Parse header and show file info panel
- [ ] Password input + verification flow
- [ ] Output path options (overwrite/rename/open folder)
- [ ] Commands: Browse, Decrypt, Cancel

**Deliverable**
- Functional decrypt flow with metadata-aware UX.

---

## Task 12 — Settings Screen (View + ViewModel)

**Checklist**
- [ ] Create `Views/SettingsView.axaml`
- [ ] Create `ViewModels/SettingsViewModel.cs`
- [ ] Performance section (chunk/thread/CPU profile placeholders)
- [ ] Security section (KDF + policy toggles)
- [ ] Output section (path + naming policy)
- [ ] Save + Restore Defaults actions

**Deliverable**
- Editable settings screen integrated with persisted settings.

---

## Task 13 — Logs + Utility Services

**Checklist**
- [ ] Create `Views/LogView.axaml`
- [ ] Create `Services/LogService.cs`
- [ ] Add in-memory log collection with level/timestamp
- [ ] Add clear/filter capability
- [ ] Create file/folder picker abstraction using Avalonia `StorageProvider`

**Deliverable**
- Basic operational logs and reusable platform-safe picker services.

---

## Task 14 — Integration, Validation, and UI Polish

**Checklist**
- [ ] Connect all ViewModels to core services
- [ ] End-to-end manual test: encrypt/decrypt file and folder modes
- [ ] Verify cancellation behavior and partial file cleanup
- [ ] Verify wrong password fail-fast path
- [ ] Apply visual polish (spacing, cards, buttons, theme consistency)
- [ ] Run full build and fix compile issues

**Deliverable**
- Working, build-clean MVP aligned with technical specification.

---

## How to Update Progress

When a task starts, mark header checklist item(s) as `[-]` in your working notes or convert first checkbox to in-progress marker in commit notes.
When a task is finished, change all related checkboxes to `[x]`.

Recommended cadence:
- Update this file after each completed main task.
- Keep commit messages aligned with task number (e.g., `Task 5: Implement channel pipeline ordering`).
