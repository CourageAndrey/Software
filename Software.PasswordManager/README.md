# Password Manager

A local WPF password manager with encrypted file-based vaults. No cloud service, account, recovery password, or Microsoft Office installation is required.

## Run

Requires the .NET 10 SDK.

```powershell
dotnet run --project Software.PasswordManager
```

Create a new `.vault` file with a master password of at least 12 characters, or unlock an existing vault. Entry fields include title, username, password, website, and notes. Search covers title/username/website only. Entries can be added, edited, deleted, and given cryptographically generated 24-character passwords. Passwords are masked by default with an explicit reveal control.

Save entry encrypts and saves the vault immediately. The toolbar Save action retries unsaved vault changes after a failed write; it does not apply edits still in the entry form. Failed operations leave the last saved vault intact. Changed external files are never silently overwritten. New vault creation refuses existing destinations.

## Encryption And Master Password

The master password is not persisted in files, settings, logs, clipboard, Windows Credential Manager, or a password-verifier record. The master-password dialog uses PasswordBox/SecureString, clears its controls when accepted/closed, and passes the password only for key derivation. Temporary character buffers and native copies are cleared afterward. The application retains a derived key in process memory only while unlocked, then zeroes it on lock/disposal.

The file uses AES-256-GCM with a 128-bit authentication tag, a fresh 96-bit nonce on every save, and PBKDF2-HMAC-SHA-256 with 600,000 iterations and a random 256-bit salt per vault. Format/version/KDF/salt/nonce/length metadata is authenticated along with the content. Salt and nonce are public metadata, not passwords. All entry fields, not just entry passwords, are encrypted. Wrong passwords and authentication failures are rejected before entries are exposed. Temporary save files contain ciphertext only; saved files are atomically replaced.

Changing the master password creates a fresh salt/key and re-encrypts the vault. Old vault backups still require their old master password. There is no recovery mechanism: losing the master password loses access to the vault. Use a strong, unique passphrase and keep independent encrypted backups.

## Locking And Clipboard

Lock clears entry UI controls, undo histories, loaded entries, and the derived encryption key. Ctrl+L locks manually. The vault also locks after five minutes without keyboard/mouse activity in the main window and on Windows session lock/logoff/disconnect. Entry-form edits not saved before automatic locking are discarded. If an operation is running, locking completes immediately after that operation finishes.

Explicitly copied passwords/usernames expire after 30 seconds and clear on lock, provided another application has not replaced the clipboard contents. Clipboard-history and cloud-sync opt-out formats are supplied on a best-effort basis. Other applications may still capture clipboard data, so avoid copying secrets on untrusted systems.

## Limits And Security Boundary

This is a simple local vault, not an independently audited password-manager product. Entry values are managed strings while unlocked; .NET cannot guarantee erasure of all old string copies from process memory. A compromised OS, debugger, memory dump, malware, or OS paging/hibernation can expose in-memory data. The app does not deliberately persist the master password, but cannot prevent those external mechanisms. SecureString is used to reduce unnecessary copies, not as a guarantee against process-memory inspection.

Keep vault files on a trusted local filesystem. External-change detection is a guard, not a full multi-process transaction/locking system. Limits: 1,000 entries and a 2 MiB serialized vault. No plaintext export, master-password command-line arguments, or automatic credential filling is provided.

## Tests

```powershell
dotnet test Software.UnitTests --filter 'FullyQualifiedName~PasswordVaultTests|FullyQualifiedName~PasswordManagerUiTests' -p:BuildInParallel=false
```

Tests use dummy credentials only. They cover authenticated encryption, tampering, wrong passwords, password changes, nonce freshness, save-conflict protection, disposal, and real WPF create/save/lock/unlock and secret-control clearing. Clipboard expiry and actual Windows session-lock notifications require manual checks.