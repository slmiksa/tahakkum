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

GitHub: private repo `slmiksa/tahakkum`. Push only when the owner asks ("ارفع التحديثات"). The owner works from more than one computer: at the start of a session, if the owner says they changed machines or asks to pull, run `git pull` before editing anything, and never clone the repository inside itself.

## Secrets: never in git

`supabase-backend/secrets/` holds `license-signing-key.json` (ECDSA P-256 private key that signs licenses) and `functions.env` (`LICENSE_SIGNING_KEY`, `CODES_KEY` for encrypting stored codes). The folder itself is ignored by `.gitignore`. Without it, Edge Functions cannot be redeployed with the same keys, and replacing the signing key invalidates every license issued.

The repository carries it encrypted instead: `supabase-backend/secrets.enc` (zip, AES-256-CBC + HMAC-SHA256, keys from PBKDF2-SHA256 with 600,000 iterations). Only the owner knows the password; never ask for it in chat. On a new machine, when the owner asks to restore the secrets, give them this to run in their own terminal, where they type the password:

`powershell -ExecutionPolicy Bypass -File .\supabase-backend\tools\secrets-unlock.ps1`

After the keys change (for example a new `CODES_KEY`), re-encrypt with `tools\secrets-lock.ps1`, again run by the owner, and push the new `secrets.enc`.

Server-side, also not in git: `/etc/tahakkum-relay` (relay certificate, pinned in the program) and `/var/lib/tahakkum-relay/ids.txt` (device ID registry).

Never write passwords, tokens, or these keys into any file, commit, or message. Scan staged content for them before every push.

## Building and testing (Windows)

There is no .NET SDK on the dev machine: the program is compiled with the Framework's `csc.exe` (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319`). That compiler is **C# 5**: no `?.`, `$""`, `nameof`, `out var`, expression-bodied members, tuples.

- Build the exe: `powershell -ExecutionPolicy Bypass -File client-program\build.ps1` (draws the icon, embeds `assets/wordmark.png`). It fails if `Tahakkum.exe` is running: ask the owner before closing it.
- Package everything handed over: `powershell -ExecutionPolicy Bypass -File package-release.ps1`.
- Tests are console programs compiled from `client-program/src/*.cs` plus one test file, with `/main:Tahakkum.<TestClass>`. References: System, System.Core, System.Drawing, System.Windows.Forms, System.Security, System.Xml, System.Runtime.Serialization, System.ServiceProcess (and WPF UIAutomation for the UI tests). Use Windows paths with csc; forward slashes break it.
  - `AccessTest` (password, IDs, relay, contacts), `LicenseTest <path to supabase-backend>` (runs `tests/dev-server.ts`), `FileTransferTest`, `SharedSessionTest` (discovery, multi-viewer): safe, no input injected.
  - `FileTransferUiTest`, `SessionTest`: open real windows; `SessionTest` moves the real mouse for a minute, so run it only with the owner's agreement.
  - Tests isolate themselves with `AppFiles.DirOverride`, `LicenseManager.Settings = new LicenseSettings { ApiUrl = "" }`, `RelayConfig.Override`, and `InputInjector.Suppress`.
- Backend tests: `cd supabase-backend && npm install && node --test tests/licensing.test.ts` (real migrations on PGlite with a stub of Supabase's auth schema and roles).
- Dashboard preview with fake data: `node supabase-backend/tests/dashboard-preview.ts`, then `http://127.0.0.1:47890` (user `salem`, password `demo`, code `123456`; mock only).

Shell notes on this machine: PowerShell script execution is disabled, so use `npx.cmd`, not `npx`. Git lives in `%LOCALAPPDATA%\Programs\Git\cmd`, GitHub CLI in `C:\Program Files\GitHub CLI`.

## Deploying

- **Supabase:** `supabase-backend\tools\deploy.cmd` (db push `--remote`, secrets from `secrets/functions.env`, five functions with `--use-api --no-verify-jwt`: activate, refresh, issue-batch, export-batch, admin-users). The agent's safety classifier blocks production deploys, so the owner runs it; then verify from outside (wrong code to `activate` returns `invalid_code`, unauthenticated `issue-batch`/`export-batch`/`admin-users` return 403, tables unreadable with the publishable key). **On Windows, send the JSON body from a file (`curl.exe ... --data "@body.json"`), not inline `-d '{...}'`: PowerShell strips the quotes out of an inline JSON arg, the function receives mangled JSON, `req.json()` throws, and every function then looks like it returns 500 `server_error` — a test-harness artifact, not a real failure.**
- **Dashboard / relay on the server:** `release/tahakkum-dashboard-install.sh` and `release/tahakkum-relay-install.sh` are self-contained (sources embedded as base64, LF line endings, Debian family; the server runs Kali). Copy with `scp` from Windows, run with `bash` on the server. Both are safe to rerun. The dashboard installer gets a Let's Encrypt certificate for the IP (needs TCP 80 open) or falls back to a self-signed one.
- After changing `dashboard/` or relay sources, rerun `package-release.ps1` so the installers carry the change.

Live values (not secret): server `82.47.165.29`; relay on 443, its certificate pin is `DefaultPin` in `client-program/src/RelayLink.cs`; dashboard on 8443; Supabase URL and publishable key in `dashboard/config.js`; functions URL and license public key in `client-program/src/License.cs`. The admin account is `salem@tahakkum.local` (the dashboard turns a bare username into `@tahakkum.local`), listed in `public.admins`, MFA required.

## Design decisions to keep

- **Consent first:** nothing about the screen is sent and no input is accepted until the person at the machine approves, or the viewer proves the access password. The session bar is always visible and cannot be hidden; Ctrl+Alt+Shift+Q ends everything.
- **License is per machine** (SHA-256 of Windows `MachineGuid`), stored in `%ProgramData%\Tahakkum`, so it survives a change of employee. Expiry counts from when a batch is issued, not when a code is activated. Tokens are verified offline; refresh is daily when online. The token payload is `v1|code_id|device|exp|issued|base64url(company)` with an **optional 7th flags field** (`LicenseToken.cs`, shared with the relay, accepts 6 or 7 fields); `a` in it = Android TV control allowed.
- **Android TV control over IP** (scrcpy-style, no APK on the TV): the Windows viewer drives an Android TV with bundled `adb` (`AdbTool` → `connect`/`screencap`/`input`; `AndroidForm` shows the polled screen with a D-pad, touch and text). adb runs as the shell user, which may inject input, so control is full without root. adb isn't in git — the owner drops `platform-tools` next to the exe (`AdbTool.Path` looks in `platform-tools\`, `vendor\platform-tools\`, beside the exe, then PATH); `package-release.ps1` should bundle it. Gated per company **and** per device: `companies.android_control` and `activations.android_control`, toggled from the dashboard (Companies page and Devices page), combined into the token's flags field by `refresh`/`activate`. The client enforces via `LicenseManager.AndroidControlAllowed` (reads `LicenseToken.AndroidControl`; a local `android-control.on` file in MachineDir is a test override). Migration `20261105000000_android_control.sql`; functions `set_company_android`/`set_device_android` (admin + MFA). Compile- and PGlite-verified; a real Android TV with network ADB tests the live path.
- **Per-company admins** (`20261110000000_company_admins.sql`): the owner creates login accounts scoped to one company. A company admin signs in to the *same dashboard*, MFA required like the owner (`my_role()` tells the dashboard owner-vs-company; `admin_company()` returns their company only at aal2). RLS adds a `company_admin_read` policy per data table (owner's `admin_read` is untouched; Postgres ORs them), so they see only their own company's batches/codes/devices. They may grant TV control per device and release a device, but **not** issue codes, flip the company-level TV switch, or see other pages — `set_device_android`/`release_activation` were widened to allow the owner **or** a company admin of that device's company (authorise before revealing whether a device id exists; `coalesce(... = ..., false)` so a null company is a plain refusal, not null logic). Creating the auth login needs the service role, so the **`admin-users` Edge Function** does it: it first checks the caller is the owner via their own token (`is_admin`), then uses the service role to create/delete/reset the GoTrue user, writing the `company_admins` row through the owner-token RPCs `create_company_admin`/`delete_company_admin` (which re-check and audit). Delete/reset refuse any user that is not already a company admin, so the owner can't be touched. Dashboard: owner gets a "مدراء الشركات" tab; a company admin gets a single scoped devices page (`pageDevices(scope)`). PGlite- and preview-verified (`?as=company` in `dashboard-preview.ts` shows the company view); the live GoTrue path (real account creation + first-login MFA enrolment) needs a deploy to test.
- **Access password and display name are per Windows user** (`%APPDATA%\Tahakkum`), so a previous employee's password stops working. The password cannot be changed during a session.
- **Machine identity certificate** is per machine (`%ProgramData%`), trusted on first use by viewers; the 6-digit verification code is shown on both ends.
- **Keep-awake** (`KeepAwake`, `SetThreadExecutionState` with `ES_SYSTEM_REQUIRED`): optional toggle `awake.on`; holds off sleep while hosting so the machine stays reachable, display still sleeps.
- **Lock-screen access** is off by default and opt-in per machine, installed (admin) from the access-password dialog. A `TahakkumAccess` Windows service (SYSTEM) keeps a `/lockagent` process alive in the console session (`LockLaunch.SpawnInConsoleSession` duplicates the service token into the console session). While locked, the input desktop is `Winlogon`; the agent follows it with `SetThreadDesktop`, reuses `ScreenCapturer`/`InputInjector`, and pipes frames/input over `\\.\pipe\Tahakkum-Lock-Data` (DACL: SYSTEM + the signed-in user). The user process (`LockScreenLink`) hosts that pipe and fans frames out; a `HostSession` uses it **only when `byPassword`** — no on-machine consent exists at the lock screen, so it is password-only. Frame/ScreenInfo payloads are byte-identical to normal ones, so the viewer is unchanged. Security: the service runs the exe as SYSTEM, so the program must live in a protected folder; the UI warns if it is under the user profile. Only compile-verified here — the secure desktop can be tested only on a real locked machine.
  - **Typing the password at the lock screen:** normal key injection replays scan codes, which the *active layout* turns into characters. The secure desktop (LogonUI) may run a different layout than the viewer typed (e.g. Arabic), so a Latin password came out wrong. Fix: the viewer also computes the real character with `ToUnicodeEx` and sends it appended to `Msg.Key` (optional trailing `ushort`, so older peers are unaffected); at the lock screen the agent types it with `KEYEVENTF_UNICODE` (`InputInjector.Char`), which is layout-independent. Printable keys only (control/navigation keys still travel as virtual keys). Normal sessions are unchanged.
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
