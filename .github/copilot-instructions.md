# Copilot Instructions

## Project Guidelines
- Use .safe as encrypted file extension instead of .svault in the SafeFile plan/spec.

---

## SafeFile Core Architecture

## SafeFile Core Architecture

### Architecture Diagram

```
┌─────────────────────────────────────────────────┐
│          SafeFile UI (Avalonia)                  │
│  ┌──────────────────────────────────────────┐   │
│  │  ViewModels (MVVM CommunityToolkit)      │   │
│  │  • MainViewModel                         │   │
│  │  • EncryptViewModel                      │   │
│  │  • DecryptViewModel                      │   │
│  └──────────────────────┬────────────────────┘   │
└─────────────────────────┼────────────────────────┘
                          │
                    Uses via Delegates
                          │
          ┌───────────────┴────────────────┐
          │                                 │
┌─────────▼────────────────────────────────▼───────┐
│       SafeFile.Core (Reusable Crypto Layer)       │
│                                                   │
│  ┌─────────────────────────────────────────┐     │
│  │ Cryptography Layer (Crypto/)             │     │
│  │  • Argon2Kdf: Password → Master Key     │     │
│  │  • AesGcmEngine: Authenticated Encrypt  │     │
│  └─────────────────────────────────────────┘     │
│                       ▲                          │
│                       │                          │
│  ┌─────────────────────────────────────────┐     │
│  │ Format Layer (Format/)                   │     │
│  │  • VaultHeader: Metadata Structure      │     │
│  │  • VaultMode: File/Zip/PerFile Modes   │     │
│  └─────────────────────────────────────────┘     │
│                       ▲                          │
│                       │                          │
│  ┌─────────────────────────────────────────┐     │
│  │ Pipeline Layer (Pipeline/)               │     │
│  │  • CryptoPipeline: Parallel Processing  │     │
│  │  • Producer → Consumers → Writer        │     │
│  │  • Handles Out-of-Order with Buffering  │     │
│  └─────────────────────────────────────────┘     │
│                       ▲                          │
│                       │                          │
│  ┌─────────────────────────────────────────┐     │
│  │ I/O Layer (IO/)                          │     │
│  │  • FileEncryptor: High-Level API        │     │
│  │  • PasswordValidator: Checksum Engine   │     │
│  │  • StreamZipper: Folder → Zip           │     │
│  └─────────────────────────────────────────┘     │
│                                                   │
│  ┌─────────────────────────────────────────┐     │
│  │ Models & Services                        │     │
│  │  • AppSettings: Config Model            │     │
│  │  • SettingsService: JSON Persistence    │     │
│  └─────────────────────────────────────────┘     │
└───────────────────────────────────────────────────┘
```

### Overview
SafeFile is a .NET 10 file/folder encryption application with a two-layer architecture:
- **SafeFile.Core**: Crypto and I/O layer (reusable, no UI dependencies)
- **SafeFile**: Avalonia UI layer (uses Core via MVVM)

### Core Components

#### 1. Cryptography Layer (`Crypto/`)
- **Argon2Kdf.cs**: Password → 32-byte master key via Argon2id (memory=64MB, iterations=4, parallelism=2)
- **AesGcmEngine.cs**: Per-chunk authenticated encryption (AES-256-GCM)
  - Nonce: 12 bytes = 4-byte prefix + 8-byte chunk index (little-endian)
  - AAD: includes chunk index + isLastChunk flag
  - Returns EncryptedChunk with Index, NoncePrefix, Ciphertext, Tag, IsLastChunk

#### 2. Format Layer (`Format/`)
- **VaultHeader.cs**: Self-describing binary header (104 bytes)
  - Magic: "SAFE"
  - Version, Mode (File/Zip/PerFile)
  - Argon2 parameters, Salt (16 bytes), NoncePrefix (4 bytes)
  - ChunkSize, PasswordChecksum (4 bytes via HMAC-SHA256)
- **VaultMode** enum: File, Zip, PerFile

#### 3. Pipeline Layer (`Pipeline/`)
- **CryptoPipeline.cs**: Multi-threaded channel-based processor
  - Producers read sequentially → Consumers encrypt/decrypt in parallel → Writer reorders and writes
  - Uses bounded channels (capacity: 100 chunks) to prevent memory explosion
  - Handles out-of-order chunks via buffering and sequential output guarantee
  - EncryptAsync: (sourceReader, outputWriter) delegates for flexible I/O
  - DecryptAsync: Similar pattern for decryption
- **UnencryptedChunk.cs**: Record (Index, Data, IsLastChunk)

#### Pipeline Flow Diagram (Encrypt)

```
┌──────────────────────────────────────────────────────────────────────┐
│                      SEQUENTIAL READING                              │
│                                                                       │
│  ┌─────────┐  ┌─────────┐  ┌─────────┐         ┌─────────────┐     │
│  │ Chunk 0 │→ │ Chunk 1 │→ │ Chunk 2 │→ ... → │ Producer    │     │
│  │ (5MB)   │  │ (5MB)   │  │ (5MB)   │        │ (1 thread)  │     │
│  └─────────┘  └─────────┘  └─────────┘        └──────┬──────┘     │
│                                                        │              │
│                                   ┌────────────────────┘              │
│                                   │ (Ordered)                         │
│                                   ↓                                   │
│                        ┌──────────────────────┐                      │
│                        │ Unencrypted Channel  │                      │
│                        │ (capacity: 100)      │                      │
│                        └──────┬───────────────┘                      │
└─────────────────────────────────┼──────────────────────────────────┘
                                  │
┌─────────────────────────────────┼──────────────────────────────────┐
│                   PARALLEL ENCRYPTION                              │
│                                  │                                 │
│                       ┌──────────┴─────────┬──────────┐            │
│                       │                    │          │            │
│                  ┌────▼────┐          ┌────▼────┐ ┌──▼────┐       │
│                  │Consumer1│          │Consumer2│ │Consum.│       │
│                  │Thread   │          │Thread   │ │Thread │       │
│                  │         │          │         │ │       │       │
│          ┌─────┐ │         │  ┌─────┐│         │ │       │       │
│          │Chunk│→│Encrypt0 │  │Chunk│→Encrypt1│ │Encrypt│       │
│          │(0)  │ │(3ms)    │  │(1)  │ (2ms)   │ │(2)    │       │
│          └─────┘ │         │  └─────┘│         │ │(4ms)  │       │
│                  │  ⬇      │         │  ⬇      │ │ ⬇     │       │
│                  └─────────┘         └────┬────┘ └───────┘       │
│                       █                   ║         █             │
│                       ║ Out-of-order!    ║         ║             │
│                       ╚═══════╤═══════════╬═════════╝             │
│                               │           │                      │
│                        ┌──────▼───────────▼──────┐               │
│                        │ Encrypted Channel      │               │
│                        │ (capacity: 100)        │               │
│                        │ [1][0][2]... (buffered)│               │
│                        └───────────┬────────────┘               │
└─────────────────────────────────────┼──────────────────────────────┘
                                      │
┌─────────────────────────────────────┼──────────────────────────────┐
│                   WRITER REORDERING                                 │
│                                      │                              │
│                        ┌─────────────▼──────────┐                  │
│                        │ WriterAsync (1 thread)′│                  │
│                        │                        │                  │
│                        │ Buffer: {1:enc_chunk} │                  │
│           Chunk 1 ─────→ Remove from buffer    │                  │
│           Chunk 0 ─────→ nextIndex=1, Ready!  │                  │
│           Chunk 2 ─────→ nextIndex=2, Ready!  │                  │
│                        └──────────┬────────────┘                   │
│                                   │ (Guaranteed Sequential!)        │
│                        ┌──────────▼──────────┐                    │
│                        │ Output Writer      │                     │
│                        │ Lock + Sequential  │                     │
│                        │ [0][1][2][3]...    │                     │
│                        └──────────┬──────────┘                     │
│                                   │                               │
│                        ┌──────────▼──────────┐                    │
│                        │  Vault File        │                     │
│                        │  (.safe format)    │                     │
│                        │  Chunks in order!  │                     │
│                        └────────────────────┘                     │
└─────────────────────────────────────────────────────────────────────┘
```

#### 4. I/O Layer (`IO/`)
- **FileEncryptor.cs**: High-level orchestration
  - EncryptFileAsync: Source file → Pipeline → Vault file (.safe)
    1. Read source sequentially in Producer
    2. Consumers encrypt chunks in parallel
    3. Writer reorders, writes vault sequentially
  - DecryptFileAsync: Vault → Validated sequential read → Destination
    - Validates expectedChunkIndex at each chunk
    - Fails fast if corruption/tampering detected
  - EncryptFolderZipAsync: Source folder → Zip (via StreamZipper) → Pipeline → Vault
  - DecryptFolderZipAsync: Vault → Pipeline → Zip → Extract to folder
- **PasswordValidator.cs**: HMAC-SHA256 checksum (4 bytes) for early password rejection
  - MinPasswordLength = 8
  - ComputeChecksum & ValidateChecksum (constant-time)
- **StreamZipper.cs**: On-the-fly zip creation/extraction (no temp files)

#### Vault File Format (.safe binary)

```
┌───────────────────────────────────────────────────────────────┐
│  VAULT FILE FORMAT (.safe)                                    │
├───────────────────────────────────────────────────────────────┤
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ HEADER (104 bytes)                                      │  │
│  ├─────────────────────────────────────────────────────────┤  │
│  │ Magic: "SAFE" (4 bytes)                                 │  │
│  │ Version: 1 (1 byte)                                     │  │
│  │ Mode: File/Zip/PerFile (1 byte)                         │  │
│  │ Salt: random 16 bytes                                   │  │
│  │ NoncePrefix: random 4 bytes                             │  │
│  │ Argon2MemorySizeKb (4 bytes)                            │  │
│  │ Argon2Iterations (4 bytes)                              │  │
│  │ Argon2Parallelism (4 bytes)                             │  │
│  │ ChunkSize (4 bytes)                                     │  │
│  │ PasswordChecksum: HMAC-SHA256[0:4] (4 bytes)           │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ FILENAME CHUNK (Index=0)                                │  │
│  ├─────────────────────────────────────────────────────────┤  │
│  │ NonceSize: 12 (4 bytes)                                 │  │
│  │ Nonce: [prefix(4B) + index(8B)] (12 bytes)             │  │
│  │ CiphertextSize: N (4 bytes)                             │  │
│  │ Ciphertext: encrypted filename (N bytes)                │  │
│  │ Tag: AES-GCM auth tag (16 bytes)                        │  │
│  │ IsLastChunk: 1 (1 byte)                                 │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ DATA CHUNK 0 (Index=1)                                  │  │
│  ├─────────────────────────────────────────────────────────┤  │
│  │ NonceSize: 12 (4 bytes)                                 │  │
│  │ Nonce: [prefix(4B) + index=1(8B)] (12 bytes)           │  │
│  │ CiphertextSize: N (4 bytes)                             │  │
│  │ Ciphertext: encrypted data (N bytes)                    │  │
│  │ Tag: AES-GCM auth tag (16 bytes)                        │  │
│  │ IsLastChunk: 0 (1 byte)                                 │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ DATA CHUNK 1 (Index=2)                                  │  │
│  ├─────────────────────────────────────────────────────────┤  │
│  │ NonceSize: 12 (4 bytes)                                 │  │
│  │ Nonce: [prefix(4B) + index=2(8B)] (12 bytes)           │  │
│  │ CiphertextSize: N (4 bytes)                             │  │
│  │ Ciphertext: encrypted data (N bytes)                    │  │
│  │ Tag: AES-GCM auth tag (16 bytes)                        │  │
│  │ IsLastChunk: 0 (1 byte)                                 │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                               │
│  ... more data chunks ...                                    │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐  │
│  │ DATA CHUNK N (Index=N)                                  │  │
│  ├─────────────────────────────────────────────────────────┤  │
│  │ NonceSize: 12 (4 bytes)                                 │  │
│  │ Nonce: [prefix(4B) + index=N(8B)] (12 bytes)           │  │
│  │ CiphertextSize: N (4 bytes)                             │  │
│  │ Ciphertext: encrypted data (N bytes)                    │  │
│  │ Tag: AES-GCM auth tag (16 bytes)                        │  │
│  │ IsLastChunk: 1 (1 byte)  ⬅️ LAST CHUNK FLAG             │  │
│  └─────────────────────────────────────────────────────────┘  │
│                                                               │
└───────────────────────────────────────────────────────────────┘
```

#### 5. Models & Settings (`Models/`, `Services/`)
- **AppSettings.cs**: Persisted config (chunk size, threads, Argon2 params, output path, etc.)
- **SettingsService.cs**: JSON load/save in AppData directory (no secrets stored)
- **FolderMetadata.cs**: Scaffold for future per-file folder mode

### Processing Flow

#### Encryption Flow (File)
```
Password (string)
    ↓
Encode → passwordBytes
    ↓
[VaultHeader]
  Salt (random 16 bytes)
  NoncePrefix (random 4 bytes)
  Argon2Kdf.DeriveKey(passwordBytes, salt, params) → 32-byte masterKey
  PasswordValidator.ComputeChecksum(passwordBytes, salt) → 4-byte checksum
    ↓
[Filename Chunk]
  Index: 0
  Plaintext: originalFileName.GetBytes()
  Encrypt via AesGcmEngine → EncryptedChunk(Index=0, IsLastChunk=true)
  Write to vault
    ↓
[Data Chunks via Pipeline]
  Producer: Read file sequentially (chunk 0, 1, 2...)
  Consumers: Parallel AES-GCM encryption (may be out-of-order)
  Writer: Buffer & reorder by Index → Sequential vault writes
    ↓
Vault File (.safe format):
  [Header (104 bytes)]
  [Filename Chunk: nonce(12) + ciphertext + tag + isLastChunk_flag]
  [Data Chunk 0: nonce(12) + ciphertext + tag + isLastChunk_flag]
  [Data Chunk 1: ...]
  [...Data Chunk N with IsLastChunk=true]
```

#### Decryption Flow (File)
```
Vault File (.safe)
    ↓
[VaultHeader]
  Extract Salt, NoncePrefix, Argon2 params, PasswordChecksum
    ↓
Derive masterKey from password using extracted params
    ↓
Validate PasswordChecksum → Reject early if password wrong
    ↓
[Read Filename Chunk]
  Validate Index = 0
  Decrypt → originalFileName
    ↓
[Read & Validate Data Chunks]
  Sequential stream read
  expectedChunkIndex = 1
  For each EncryptedChunk:
    - Validate: chunk.Index == expectedChunkIndex (fail-fast if mismatch)
    - Decrypt via AesGcmEngine
    - Write plaintext to destination
    - Increment expectedChunkIndex
    ↓
Destination File (original plaintext)
```

### Key Invariants & Guarantees

1. **Chunk Ordering**: Pipeline's WriterAsync guarantees sequential vault output regardless of parallel encryption
2. **Last-Chunk Detection**: IsLastChunk flag written/read; AAD includes this flag
3. **Corruption Detection**: Chunk index validation in decrypt fails fast
4. **Authentication**: AES-GCM tag + password checksum verify integrity
5. **Memory Safety**: Bounded channels (100-chunk limit), password bytes zeroed via CryptographicOperations.ZeroMemory
6. **No Data Loss**: Out-of-order chunks detected and either reordered (encrypt) or rejected (decrypt)

### Performance Characteristics

- **Sequential Reader**: Producer reads source 1 thread → no contention
- **Parallel Consumers**: N worker threads encrypt AES-GCM in parallel
- **Bounded Reordering**: Writer buffers up to 100 out-of-order chunks
- **Speedup**: 6-8x on 8-core CPU (from parallel AES-GCM)
- **Memory**: ~100-200MB for 100-chunk buffer (1-2GB per 10K-chunk file with 10MB chunks)

### Testing Recommendations

1. **Round-trip**: File → Encrypt → Decrypt → Verify matches original
2. **Corruption detection**: Modify vault bytes → Decrypt should fail
3. **Performance**: 1GB file encryption time, CPU/memory utilization
4. **Concurrency**: Multiple files encrypting simultaneously
5. **Folder operations**: Nested directories, symlinks, permissions