# Tahakkum (تحكّم)

Remote desktop product for companies, sold under per-machine licenses. The owner (Arabic speaker) runs the product, hands licence codes to customer companies, and wants every answer in Arabic. All user-facing text in the program and dashboard is Arabic, right to left.

The root `README.md` (Arabic) maps the folders. This file is the working knowledge a new session needs.

## Parts and where they run

| Folder | What | Runs on |
|---|---|---|
| `client-program` | `Tahakkum.exe`, C# 5 on .NET Framework 4.7.2+, Windows Forms, one exe, no installer | Customer Windows machines |
| `relay-server` | ID registry and byte relay for sessions outside the LAN, C# on .NET 8 | Owner's Linux server, TCP 443 |
| `dashboard` | License admin panel, plain HTML/CSS/JS + `server.mjs` (Node, no deps) | Owner's Linux server, `https://82.47.165.29:8443` |
| `supabase-backend` | Postgres migrations, Edge Functions (TypeScript/Deno), tests (PGlite), deploy tools | Supabase project `hosmafqkcmcmdpwhzodu` |
| `release` | What gets handed over: client zip, dashboard zip, two Linux installers | Built by `package-release.ps1` |
| `brand` | Original logo and palette images | Reference only |

GitHub: private repo `slmiksa/tahakkum`. Push only when the owner asks ("ارفع التحديثات").

## Secrets: never in git

`supabase-backend/secrets/` holds `license-signing-key.json` (ECDSA P-256 private key that signs licenses) and `functions.env` (`LICENSE_SIGNING_KEY`, `CODES_KEY` for encrypting stored codes). Ignored by `.gitignore`. On a new machine the owner copies this folder by hand. Without it, Edge Functions cannot be redeployed with the same keys, and replacing the signing key invalidates every license issued.

Server-side, also not in git: `/etc/tahakkum-relay` (relay certificate, pinned in the program) and `/var/lib/tahakkum-relay/ids.txt` (device ID registry).

Never write passwords, tokens, or these keys into any file, commit, or message. Scan staged content for them before every push.

## Building and testing (Windows)

There is no .NET SDK on the dev machine: the program is compiled with the Framework's `csc.exe` (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). That compiler is **C# 5**: no `?.`, `$""`, `nameof`, `out var`, expression-bodied members, tuples.

- Build the exe: `powershell -ExecutionPolicy Bypass -File client-program\build.ps1` (draws the icon, embeds `assets/wordmark.png`). It fails if `Tahakkum.exe` is running: ask the owner before closing it.
- Package everything handed over: `powershell -ExecutionPolicy Bypass -File package-release.ps1`.
- Tests are console programs compiled from `client-program/src/*.cs` plus one test file, with `/main:Tahakkum.<TestClass>`. References: System, System.Core, System.Drawing, System.Windows.Forms, System.Security, System.Xml, System.Runtime.Serialization (and WPF UIAutomation for the UI tests). Use Windows paths with csc; forward slashes break it.
  - `AccessTest` (password, IDs, relay, contacts), `LicenseTest <path to supabase-backend>` (runs `tests/dev-server.ts`), `FileTransferTest`, `SharedSessionTest` (discovery, multi-viewer): safe, no input injected.
  - `FileTransferUiTest`, `SessionTest`: open real windows; `SessionTest` moves the real mouse for a minute, so run it only with the owner's agreement.
  - Tests isolate themselves with `AppFiles.DirOverride`, `LicenseManager.Settings = new LicenseSettings { ApiUrl = "" }`, `RelayConfig.Override`, and `InputInjector.Suppress`.
- Backend tests: `cd supabase-backend && npm install && node --test tests/licensing.test.ts` (real migrations on PGlite with a stub of Supabase's auth schema and roles).
- Dashboard preview with fake data: `node supabase-backend/tests/dashboard-preview.ts`, then `http://127.0.0.1:47890` (user `salem`, password `demo`, code `123456`; mock only).

Shell notes on this machine: PowerShell script execution is disabled, so use `npx.cmd`, not `npx`. Git lives in `%LOCALAPPDATA%\Programs\Git\cmd`, GitHub CLI in `C:\Program Files\GitHub CLI`.

## Deploying

- **Supabase:** `supabase-backend\tools\deploy.cmd` (db push `--remote`, secrets from `secrets/functions.env`, four functions with `--use-api --no-verify-jwt`). The agent's safety classifier blocks production deploys, so the owner runs it; then verify from outside (wrong code to `activate` returns `invalid_code`, unauthenticated `issue-batch`/`export-batch` return 403, tables unreadable with the publishable key).
- **Dashboard / relay on the server:** `release/tahakkum-dashboard-install.sh` and `release/tahakkum-relay-install.sh` are self-contained (sources embedded as base64, LF line endings, Debian family; the server runs Kali). Copy with `scp` from Windows, run with `bash` on the server. Both are safe to rerun. The dashboard installer gets a Let's Encrypt certificate for the IP (needs TCP 80 open) or falls back to a self-signed one.
- After changing `dashboard/` or relay sources, rerun `package-release.ps1` so the installers carry the change.

Live values (not secret): server `82.47.165.29`; relay on 443, its certificate pin is `DefaultPin` in `client-program/src/RelayLink.cs`; dashboard on 8443; Supabase URL and publishable key in `dashboard/config.js`; functions URL and license public key in `client-program/src/License.cs`. The admin account is `salem@tahakkum.local` (the dashboard turns a bare username into `@tahakkum.local`), listed in `public.admins`, MFA required.

## Design decisions to keep

- **Consent first:** nothing about the screen is sent and no input is accepted until the person at the machine approves, or the viewer proves the access password. The session bar is always visible and cannot be hidden; Ctrl+Alt+Shift+Q ends everything.
- **License is per machine** (SHA-256 of Windows `MachineGuid`), stored in `%ProgramData%\Tahakkum`, so it survives a change of employee. Expiry counts from when a batch is issued, not when a code is activated. Tokens are verified offline; refresh is daily when online.
- **Access password and display name are per Windows user** (`%APPDATA%\Tahakkum`), so a previous employee's password stops working. The password cannot be changed during a session.
- **Machine identity certificate** is per machine (`%ProgramData%`), trusted on first use by viewers; the 6-digit verification code is shown on both ends.
- **Relay** only forwards bytes; the session's own TLS runs end to end inside it, and the password proof is bound to the host certificate. Only licensed machines may register an ID.
- **Codes** are stored as SHA-256 hashes plus AES-GCM ciphertext (`CODES_KEY`), so batches can be exported again with each code's status; batches from before that feature have no stored codes.
- **Shared sessions:** up to `Proto.MaxViewers` (4) viewers; one controls at a time (`ControlDesk`), first to join controls, control passes to the oldest viewer when the controller leaves. Each viewer has its own capture loop.
- **Discovery:** UDP 47801 broadcast inside the LAN, answered only to private addresses, rate limited, only by machines accepting requests with a valid license.
- **Protocol compatibility:** `Proto.Version` is 3. New fields are appended so older peers keep working (the Hello nickname, `ControlState` ignored by old viewers); bump the version only for a real break.
- Dashboard renders everything with `textContent`; machine names come from customers and must never become HTML.

## Brand

Teal `#1FAEB4`, charcoal `#0E1517`, light gray `#F5F6F6`, white. Text on teal is charcoal (white is too faint). The symbol is drawn in code (`LogoView` in `Theme.cs`, the same shape in `build.ps1` and `dashboard/logo.svg`); the calligraphic wordmark is `client-program/assets/wordmark.png`, cut from `brand/logo.png`.

## Ideas parked by the owner

- Controlling Android/Google TV by IP via scrcpy/adb (cancelled for now).
- Code signing certificate (OV recommended), a subdomain for the relay instead of a bare IP, SSH hardening of the server.
