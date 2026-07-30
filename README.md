<div align="center">

# 🔐 SafeFile

### Private, authenticated file encryption — without sending your data anywhere.

SafeFile is a modern desktop application for protecting files and folders with
**AES-256-GCM** and password keys derived through **Argon2id**. Everything runs
locally: no account, no cloud, and no remote service.

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia](https://img.shields.io/badge/Avalonia-12.0-8B44AC)](https://avaloniaui.net/)
[![Encryption](https://img.shields.io/badge/Encryption-AES--256--GCM-2563EB)](#security-at-a-glance)
[![KDF](https://img.shields.io/badge/KDF-Argon2id-059669)](#how-long-would-an-attacker-need)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

</div>

---

<!--
Add the main screenshot at docs/images/safefile-overview.png, then use:
![SafeFile overview](docs/images/safefile-overview.png)
-->

> 📷 **Screenshot placeholder — Main application overview**

## Why SafeFile?

SafeFile is for anyone who needs to protect sensitive data before storing,
archiving, copying, or sharing it:

- personal documents, identity records, and financial files;
- confidential work material and project archives;
- photos, media, backups, and portable-drive data;
- folders transferred through untrusted storage providers;
- any file that should remain unreadable without the exact password.

Files, passwords, and encryption keys remain on the current device. SafeFile
does not upload them or require an internet connection.

## Highlights

- **Authenticated encryption** with AES-256-GCM.
- **Memory-hard password protection** with configurable Argon2id.
- **File, ZIP-folder, and PerFile workflows**.
- **Optional filename encryption** with authenticated name restoration.
- **Batch decryption** for one file, multiple vaults, or entire folders.
- **Per-vault progress and results** in a numbered queue.
- **Explicit overwrite protection** for existing output.
- **English and Vietnamese** with live language updates.
- **Light and Dark themes**, with first-run system-theme detection.
- **Structured logs** with search, filtering, export, and rolling files.
- **Local processing** without accounts or cloud dependencies.

## Application tour

### Encrypt

Choose a file or folder, enter a password, and select an output mode:

| Source | Mode | Result |
|---|---|---|
| File | `File` | One `.safe` vault |
| Folder | `ZIP` | One streamed folder vault |
| Folder | `PerFile` | One independent vault per regular file |

<!-- Add: docs/images/encrypt-screen.png -->
> 📷 **Screenshot placeholder — Encrypt screen**

### Decrypt

Add one vault, multiple vaults, or a folder recursively. The queue shows each
vault's sequence number, name, authenticated original filename, size, progress,
and final status. One failed vault does not prevent later valid vaults from
being processed.

<!-- Add: docs/images/decrypt-screen.png -->
> 📷 **Screenshot placeholder — Batch decrypt and vault details**

### Settings, logs, and About

Settings manages language, theme, performance, password policy, Argon2id
parameters, and separate encrypted/decrypted output locations. Logs provides an
in-app operational console. About presents version, runtime, privacy, licensing,
and diagnostic information.

<!-- Add: docs/images/settings-screen.png -->
> 📷 **Screenshot placeholder — Settings and Dark theme**

## Security at a glance

SafeFile assumes an attacker knows the algorithms, source code, and complete
`.safe` format. Security does not depend on hiding implementation details.

- AES-256-GCM protects confidentiality and detects modification or corruption.
- Argon2id makes every offline password guess consume processing time and
  memory.
- Every vault uses a unique random salt and nonce prefix.
- A one-byte password difference produces a different key and fails
  authentication; guesses cannot be “partially correct.”
- Password byte arrays and derived keys are cleared when operations finish.
- Passwords and keys are never persisted in settings or application logs.

Argon2id is a memory-hard password KDF standardized in
[RFC 9106](https://www.rfc-editor.org/rfc/rfc9106.html). OWASP also documents
[recommended Argon2id usage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#argon2id)
for password-derived secrets.

For the vault layout, API contracts, nonce construction, validation rules,
pipeline, filesystem behavior, and complete cryptographic implementation notes,
read the **[SafeFile.Core Technical Guide](SafeFile.Core/README.md)**.

### How long would an attacker need?

An attacker holding a vault can guess passwords offline. The following estimate
assumes:

- a truly random password;
- SafeFile's default Argon2id configuration;
- average discovery after half the password space;
- an extremely aggressive aggregate rate of **1,000,000 Argon2id guesses per
  second**.

That rate is deliberately pessimistic and is not a benchmark or guarantee.
Argon2id's memory requirement makes it very expensive to sustain at scale.

| Truly random password | Approximate average time |
|---|---:|
| 8 lowercase letters | about 29 hours |
| 8 letters + digits | about 3.5 years |
| 10 letters + digits | about 13,000 years |
| 12 letters + digits | about 51 million years |
| 16 letters + digits | about 756 trillion years |
| 4 random Diceware words | about 58 years |
| 6 random Diceware words | about 3.5 billion years |

These estimates do **not** apply to human-created passwords. Names, dates,
quotations, keyboard patterns, reused credentials, and passwords such as
`Password@123` may be found quickly with dictionaries and mutation rules.

For important data, use either:

- at least 16 random characters generated by a password manager; or
- at least 6 independently selected Diceware words.

> **Important:** password quality, malware, keyloggers, and exposed plaintext
> are generally more realistic risks than directly attacking AES-256.

### Honest limitations

- SafeFile cannot recover a forgotten password.
- A compromised operating system can capture passwords or plaintext.
- Secure deletion cannot be guaranteed on SSDs, snapshots, or copy-on-write
  filesystems.
- This project has not yet undergone an independent professional security
  audit.

See the
[OWASP Cryptographic Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html)
for broader storage-security guidance.

## Getting started

### Requirements

- Windows, Linux, or macOS supported by Avalonia Desktop;
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- sufficient memory for the configured Argon2id cost.

The project is currently built and validated primarily through the Windows
toolchain.

### Build from source

```powershell
git clone <your-repository-url>
cd SafeFile

dotnet restore SafeFile.slnx
dotnet build SafeFile/SafeFile.csproj
dotnet run --project SafeFile/SafeFile.csproj
```

From WSL or a Unix shell targeting the Windows SDK:

```bash
dotnet.exe build SafeFile/SafeFile.csproj
```

### Basic usage

1. Review output paths and security parameters in **Settings**.
2. Open **Encrypt** and select a file or folder.
3. Enter a strong, unique password and choose the desired options.
4. Start encryption and keep the resulting `.safe` vault.
5. Open **Decrypt**, add the vaults, enter the exact password, and start the
   batch.

Default output locations:

```text
Documents/
└── SafeFile/
    ├── Encrypted/
    └── Decrypted/
```

## Project structure

```text
SafeFile/
├── SafeFile.Core/          Cryptography and file-processing layer
├── SafeFile/               Avalonia desktop application
├── SafeFile.Core.Tests/    Core regression tests
└── SafeFile.slnx
```

Detailed Core documentation is intentionally kept out of this landing page:

- **[Core technical and UI integration guide](SafeFile.Core/README.md)**
- **[Implementation status](IMPLEMENTATION_TASKS.md)**

## Contributing

Issues, security reviews, documentation improvements, translations, tests, and
focused pull requests are welcome.

Please:

1. explain the user or security problem being addressed;
2. keep `SafeFile.Core` independent from Avalonia;
3. never log passwords, keys, salts, checksums, or file contents;
4. preserve existing files unless overwrite is explicit;
5. keep English and Vietnamese resource keys synchronized;
6. build the complete solution before submitting a change.

For suspected vulnerabilities, avoid publishing sensitive exploit details in a
public issue. Use the repository's private security-reporting channel when one
is available.

## License

SafeFile is available under the [MIT License](LICENSE).

```text
Copyright (c) 2026 kevinxvu
```

---

<div align="center">

**Your files stay yours. Your password is the key.**

</div>
