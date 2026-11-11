# Coworker for Windows: installer, updater and release pipeline

The Windows twin of `Humanlike-apps/coworker/create_dmg.sh` + Sparkle.

| Piece | Where | What it is |
|---|---|---|
| Version | `Coworker.csproj` (`AppVersion`, `AppBuild`) | Single source. `AppVersion` = Mac `MARKETING_VERSION` (three numbers, shown to users, `sparkle:shortVersionString`). `AppBuild` = Mac `CURRENT_PROJECT_VERSION` (integer, `sparkle:version`, what the updater compares; raise it on every release). Flows into the assembly/file version, `ApiConstants.AppVersion` / `AppBuild` / `AppVersionDisplay` ("1.0.0 (1)"), the installer and the feed. |
| Installer | `installer/Coworker.iss` | Inno Setup 6.3+, per-user (no UAC), installs the self-contained publish output to `%LOCALAPPDATA%\Programs\Coworker`, registers `coworker-auth://` and `coworker://`, Start menu shortcut, clean uninstall. |
| Release script | `installer/create_installer.ps1` | publish -> sign -> ISCC -> sign -> Ed25519 signature -> appcast item -> copy into the website. |
| Update signer | `installer/tools/UpdateSigner` | `keygen`, `public-key`, `sign`, `verify` for the Ed25519 update signature (Sparkle's `generate_keys` / `sign_update`). |
| In-app updater | `Services/UpdateService.cs`, `Services/Updates/*`, `Views/UpdateWindow.cs` | NetSparkle (`NetSparkleUpdater.SparkleUpdater` 3.1.0) for the protocol, our own WinUI window for the UI. |
| Feed + download page | `Humanlike-next/public/coworker/appcast-windows.xml`, `latest.json`, `app/(public)/coworker/download` | Served at `https://humanlike.co/coworker/appcast-windows.xml` and `/coworker/download`. |
| Tests | `installer/tests/UpdateTests` (xunit, 45 tests), `Humanlike-next/tests/coworker` (python) | Updater against a local mock feed, signing round trip, feed/JSON tooling. |

## How updating works (mirrors the Mac app)

- Feed: `appcast-windows.xml`, a **separate** file from the Mac `appcast.xml` (the Mac delegate scans the whole Mac feed for `sparkle:criticalUpdate`, so a Windows item in it could trigger the Mac "Update Required" alert). Items are Sparkle-format with `sparkle:os="windows-x64"` (or `windows-arm64`).
- Trust: every installer must carry a valid Ed25519 `sparkle:edSignature` for the public key built into the app (`UpdatePublicKey` in `Coworker.csproj`, the equivalent of `SUPublicEDKey`). A build without a public key has updating switched off. The feed itself is not signed (same as the Mac feed); an item without a signature is ignored.
- Cadence (Info.plist `SUScheduledCheckInterval` = 3600): a background check shortly after launch (when the last one is older than an hour) and then hourly. Five seconds after launch a separate probe looks for a critical update.
- Default behaviour (Mac `SUAutomaticallyUpdate` + install immediately): a background check that finds a newer build downloads it, verifies the signature and installs it right away by running the installer silently and relaunching the app in the tray. Unlike Sparkle, it holds the install back while a dictation is recording or being transcribed (the installer restarts the app and would drop it) and while the update window is open.
- Critical update (`<sparkle:criticalUpdate />` or `<sparkle:tag>critical</sparkle:tag>`, newer than the running build): blocking window "Update Required" / "Coworker {version} is required to continue. The app will now update." with **Update Now** (runs the normal check window) and **Quit**. Same copy as `UpdaterDelegate.swift`.
- "Check for Updates..." (tray and Settings, both call `UpdateService.Shared.CheckForUpdatesAsync(userInitiated: true)`): Sparkle-style window with "Checking for updates...", "You're up to date!", or "A new version of Coworker is available!" with the release notes rendered from the feed's HTML, **Skip This Version**, **Remind Me Later**, **Install Update**, then download progress and install. Skip This Version silences background prompts for that build only.
- Dev builds only (`#if DEBUG`): `COWORKER_UPDATE_FEED` pins a feed, otherwise `http://localhost:3000/coworker/appcast-windows.xml` is preferred when it answers and is newer (Mac `checkLocalFeedAvailability`); `COWORKER_UPDATE_PUBLIC_KEY` supplies a throwaway public key. Neither exists in Release builds.

### Why NetSparkle (and not Velopack/WinSparkle)

Verified by restoring and building, not assumed: `NetSparkleUpdater.SparkleUpdater` 3.1.0 restores and builds into the unpackaged WinUI 3 project (net9.0-windows10.0.22621.0, self-contained) and publishes (`NetSparkle.dll`, `Chaos.NaCl.dll`). It reads the same appcast format, uses the same Ed25519 scheme as the Mac's Sparkle, and supports `sparkle:criticalUpdate`. The UI package is not used: the update window is our own (`Views/UpdateWindow.cs`). Two NetSparkle behaviours are adapted in `Services/Updates`: its OS check accepts any `sparkle:os` containing "win" (so `WindowsBuildFilter` keeps x64 and ARM64 items apart), and none of its signature modes matches the Mac setup (unsigned feed accepted, unsigned installer refused), so `Ed25519DownloadVerifier` implements exactly that rule. Velopack remains the documented fallback if delta updates become important (the payload is large).

## One-time setup

1. .NET 9 SDK (already needed to build the app), Python 3 (feed tooling), and **Inno Setup 6.3+**: `winget install JRSoftware.InnoSetup` (or the installer from https://jrsoftware.org/isinfo.php). `ISCC.exe` is found on PATH, in `%LOCALAPPDATA%\Programs\Inno Setup 6`, or `ISCC_PATH`. It was not installed on the machine where this was written, so the `.iss` was never compiled; see "Not verified".
2. **Update signing key.** Since 11 Nov 2026 the Windows app uses the Mac's Sparkle key (see "Building from a Mac"); the steps below are only for a separate key. The repo never contains one.

   ```powershell
   cd Humanlike-apps\coworker-windows
   dotnet run --project installer\tools\UpdateSigner -- keygen
   ```

   This writes `coworker-windows-update.key` (private, base64 seed) and `.pub` under `%LOCALAPPDATA%\Coworker\update-signing\` (choose another folder with `--out`; it refuses to write inside a git work tree and never overwrites an existing key). Back the private key up in a password manager: losing it strands every installed copy, because they only accept updates signed by it. Use a key pair separate from the Mac one.
3. Put the printed **public** key in `Coworker.csproj`:

   ```xml
   <UpdatePublicKey>BASE64-PUBLIC-KEY</UpdatePublicKey>
   ```

   (or pass `-p:UpdatePublicKey=...` to the publish in CI). `create_installer.ps1` refuses to release when this key is missing or does not belong to the signing key.
4. Code signing: see below. Nothing is stored in the repo; everything comes from environment variables.

CI: provide the private key as the secret `COWORKER_UPDATE_PRIVATE_KEY` (the base64 seed) instead of a file.

## Releasing

```powershell
# 1. In Coworker.csproj: raise AppBuild (always) and AppVersion (when users should see a new number).
# 2. Release notes are required, like create_dmg.sh (RELEASE_NOTES_FILE or RELEASE_NOTES_HTML):
$env:RELEASE_NOTES_FILE = "$PWD\release-notes.html"
# 3. Signing identity (one of, see "Code signing"):
$env:COWORKER_SIGN_CERT_SHA1 = "<thumbprint>"
# 4. Build, sign, package, sign again, Ed25519-sign, write the feed item:
.\installer\create_installer.ps1 -UpdateAppcast
```

What it does, in order (it stops at the first failure):

1. Reads `AppVersion` / `AppBuild` from the csproj; refuses a build number that is not higher than every `sparkle:version` already in `appcast-windows.xml`.
2. Builds the signer, checks that a private key exists and that its public key equals `UpdatePublicKey` in the csproj.
3. `dotnet publish -c Release -p:Platform=x64` (self-contained, `win-x64`), checks the output (`Assets` included) and that `Coworker.exe` reports file version `AppVersion.AppBuild`.
4. Authenticode-signs `Coworker.exe` and `Coworker.dll`.
5. Compiles `Coworker.iss` with ISCC into `installer\release\Coworker-<version>-win-<arch>.exe`; signs it (RFC 3161 timestamp).
6. Ed25519-signs the installer and verifies the signature against the public key; writes `<installer>.sha256`.
7. Writes `installer\release\appcast-item-windows.xml` and copies the installer (and its `.sha256`) to `<web>\public\coworker\downloads\`.
8. With `-UpdateAppcast`: inserts the item at the top of `appcast-windows.xml` and runs `Humanlike-next/scripts/update-coworker-downloads.py`, which refreshes `public/coworker/latest.json` (the data of the `/coworker/download` page). Without it, it prints the item to paste (the Mac script's manual workflow).

Options: `-Arch arm64`, `-Critical` (adds `<sparkle:criticalUpdate />`, running apps show "Update Required"), `-Unsigned` (local test: no Authenticode, key mismatches only warn; never publish that output), `-InstallerPath <exe>` (re-sign/publish a ready-made installer, no publish/ISCC), `-WebPublicDir` / `WEB_PUBLIC_DIR` (default `..\..\Humanlike-next\public\coworker`), `-BaseUrl` / `COWORKER_INSTALLER_BASE_URL` (default `https://humanlike.co/coworker/downloads`; point at GitHub Releases or R2 for a big installer).

Then, as with the Mac: commit `appcast-windows.xml` and `latest.json` together (not the installer), make sure the installer is reachable at the enclosure URL, deploy the website, and test the upgrade from the previous build. The checklist is in `Humanlike-apps/coworker/RELEASE_CHECKLIST.md` ("Windows" section).

## Building from a Mac (no Windows machine)

The app only builds on Windows, and the private HumanlikeAI repository's Actions budget can be used up. Public
repositories build for free, so the Windows app has a public build mirror: **https://github.com/mmkontis/coworker-windows**
(this folder only, without `docs/` and without history). Its workflow is `.github/workflows/build.yml` in this
folder; inside the monorepo that file does nothing.

```bash
cd Humanlike-apps/coworker-windows
installer/public_build.sh            # copy this folder there, wait for the run, download the installer
installer/sign_update_mac.py installer/release/Coworker-1.0.0-win-x64.exe --notes release-notes.html \
    --base-url https://github.com/mmkontis/coworker-windows/releases/download/v1.0.0 --update-appcast
gh release create v1.0.0 installer/release/Coworker-1.0.0-win-x64.exe --repo mmkontis/coworker-windows \
    --title "Coworker for Windows 1.0.0" --notes-file release-notes.html
```

- `public_build.sh` refuses to copy anything that looks like a credential, and the workflow uses no secrets. The
  run restores, builds, runs the updater and text rules tests, publishes, compiles `Coworker.iss`, then installs
  the installer silently, checks the app stays running and uninstalls it.
- The update key is **the Mac's Sparkle key** (login Keychain, account `coworker`; public half in `UpdatePublicKey`,
  the same value as the Mac's `SUPublicEDKey`). One key signs both apps' updates, so there is one key to keep safe:
  a copy of the seed is in `~/.config/coworker-release/` on the release Mac. `sign_update_mac.py` signs with
  Sparkle's `sign_update`, checks the signature a second way and refuses a key that is not the one in the csproj.
- The installer from the mirror has **no Authenticode signature** (no certificate exists yet, see below), so a
  first download shows SmartScreen's "Windows protected your PC". Updates are not affected: the updater checks the
  Ed25519 signature and starts the installer itself.

## Code signing

Every shipped PE and the installer should be Authenticode-signed with an RFC 3161 timestamp (`/tr ... /td sha256 /fd sha256`) so signatures outlive the certificate. Without it SmartScreen shows "Windows protected your PC" and Defender may quarantine an unsigned, self-updating installer. The Ed25519 update signature is separate and always required.

`create_installer.ps1` reads the signing identity from the environment (first match wins):

| Variable(s) | Use for |
|---|---|
| `COWORKER_SIGN_COMMAND` | Any signer; `{file}` is replaced by the quoted path (AzureSignTool, DigiCert `smctl`, SSL.com CodeSignTool...). |
| `COWORKER_SIGN_TS_DLIB` + `COWORKER_SIGN_TS_METADATA` | Azure Trusted Signing through `signtool` (path to `Azure.CodeSigning.Dlib.dll` and the `metadata.json` with endpoint, account and profile; authenticate with the usual `AZURE_*` variables or `az login`). Timestamp defaults to `http://timestamp.acs.microsoft.com`. |
| `COWORKER_SIGN_CERT_SHA1` | A certificate in the Windows store (an OV/EV token, DigiCert KeyLocker or SSL.com eSigner client). |
| `COWORKER_SIGN_PFX` (+ `COWORKER_SIGN_PFX_PASSWORD`) | A `.pfx` file; only for legacy/dev certificates, current OV/EV certificates cannot be exported. |
| `COWORKER_SIGN_TIMESTAMP_URL`, `SIGNTOOL_PATH`, `ISCC_PATH` | Overrides. `signtool.exe` comes with the Windows SDK. |

Which certificate (decision for the owner, see the audit's section 11.3):

- **Azure Trusted Signing** (now branded Artifact Signing): cheapest (about 10 USD/month at the time of writing), no hardware token, short-lived certificates handled by Azure, works from CI. Requires a verifiable legal entity, and availability by country/entity type has been limited; check eligibility in the Azure portal before planning around it. Publisher reputation for SmartScreen is tied to the identity, so keep one signing identity across releases.
- **OV or EV code-signing certificate** on a hardware token or cloud HSM (DigiCert KeyLocker, SSL.com eSigner, Sectigo...): works for any company, 200-500 USD/year, keys must live on FIPS hardware or a cloud signer, so CI needs the vendor's client. EV no longer bypasses SmartScreen warnings by itself; reputation builds with downloads either way.

Whatever is chosen, keep the same publisher identity release after release, and test the first signed installer on a clean Windows 11 VM.

## ARM64

The app publishes for `win-arm64` (the csproj has the profile) and the script supports it (`-Arch arm64`: `Coworker-<version>-win-arm64.exe`, `sparkle:os="windows-arm64"`, Inno `ArchitecturesAllowed=arm64`). It is optional: the x64 installer installs on Windows 11 ARM64 (`x64compatible`) and runs under emulation, and an ARM64 PC keeps receiving the x64 items (an item with `windows-arm64` is only offered to ARM64 processes, `windows-x64` only to x64 processes). Not built or run here. If you ship both, release them with the same version/build so both items sit in the one feed.

## Hosting the installer

`create_installer.ps1` copies the installer into `Humanlike-next/public/coworker/downloads/` (the folder the Mac DMGs use; `next.config.js` already serves it as an attachment with a long cache and `proxy.ts` skips it). Never commit installers to git. The self-contained WinUI 3 publish is about 235 MB on disk; the compressed installer will be well under that (estimate 60-110 MB, measure on the first real build). Check your host's limit for a single static file before relying on the website deployment (Vercel's is around 100 MB per file at the time of writing; the script warns above 90 MB). If it does not fit, upload the installer to Cloudflare R2 or a GitHub Release and release with `-BaseUrl https://github.com/mmkontis/<repo>/releases/download/v<version>` (the download-data script accepts `github.com/mmkontis/...` release URLs; add an R2 host to `WEBSITE_HOSTS`/`validate_url` in `Humanlike-next/scripts/update-coworker-downloads.py` if you use one). Only the small appcast and `latest.json` need to be on the site.

Website pieces (all in `Humanlike-next`, no deploy done):

- `public/coworker/appcast-windows.xml` (checked in, empty until the first release, like `appcast.xml`), served with `application/xml` and revalidation (`next.config.js`) and excluded from the auth/locale proxy (`proxy.ts` matcher).
- `app/(public)/coworker/download/page.tsx`: the previously missing `/coworker/download` page, OS-detected, Windows and Mac buttons from `public/coworker/latest.json`, shows "not published yet" instead of a dead link.
- `scripts/update-coworker-downloads.py` (`--check` in CI): rebuilds `latest.json` from both appcasts; `python -m unittest discover -s tests/coworker -p 'test_*.py'`.

## Hooks the other slices need

- `MainWindow` already calls `UpdateService.Shared.StartPeriodicChecks()` once at startup (keep it).
- Tray menu "Check for Updates..." and Settings > App Actions "Check for Updates" -> `_ = UpdateService.Shared.CheckForUpdatesAsync(userInitiated: true);` (the window reports every outcome, so no extra toast). The foundation's `CheckForUpdateAsync()` still exists and behaves the same (always returns true = handled).
- `App.OnLaunched`: the installer starts the new build after a silent update as `Coworker.exe --background --updated`; the app should treat `--background` as "start in the tray, do not activate the main window".
- "Launch at login" belongs to the app: use the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value named `Coworker`; the uninstaller removes exactly that value.
- About/version strings: `ApiConstants.AppVersionDisplay` gives the Mac format "1.0.0 (1)".
- `UpdateService.Shared.LastCheckTime` and `IsConfigured` are available for Settings/diagnostics.

## Testing

```powershell
dotnet test installer\tests\UpdateTests          # 45 tests, ~10 s, no network
python -m unittest discover -s ..\..\Humanlike-next\tests\coworker -p "test_*.py"   # from installer\, or run inside Humanlike-next
```

The updater tests run `UpdateEngine` (the UI-free core of `UpdateService`) against a loopback HTTP listener that plays humanlike.co: newest-build selection (numeric compare), other-OS and other-CPU items, unsigned items dropped, wrong-key / tampered / re-tampered installers rejected, critical detection, skip-version, failure modes, cancel, and launching a fake installer only after the app exited, with the silent switches.

Manual smoke test of the real window without touching your install: run a Debug build with `COWORKER_DATA_DIR=<temp>`, `COWORKER_UPDATE_FEED=http://127.0.0.1:<port>/appcast-windows.xml`, `COWORKER_UPDATE_PUBLIC_KEY=<throwaway public key>` and `--check-for-updates`, serve a feed from any static server with an installer signed by `UpdateSigner sign`.

## Not verified (needs a machine with the tools, or a real release)

- `Coworker.iss` was reviewed but **never compiled** (Inno Setup is not installed here). First step of the first release: run the script without `-Unsigned` on a machine with Inno Setup, then install, upgrade over the previous build through the real updater, and uninstall in a clean VM (checklist).
- Authenticode signing (`signtool`, Azure Trusted Signing) was not exercised; no certificate exists yet.
- The updater was verified end to end against a mock feed with a stand-in installer (silent switches, wait-for-exit, relaunch is the installer's `[Run]` entry), not against a real Inno installer. The real window was driven on Windows 11 x64 (check, up to date, update available, critical "Update Required", download, background auto-install).
- ARM64 build, Windows 10 1809, SmartScreen/Defender behaviour, and deploying the website changes (`next build` was not run; the new page type-checks and renders in an isolated harness) are untested.
